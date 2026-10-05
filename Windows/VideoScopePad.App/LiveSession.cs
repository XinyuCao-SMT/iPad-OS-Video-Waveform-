//
//  LiveSession.cs
//  VideoScopePad.App
//
//  实时链路：**取帧 → (采集卡的话：YUV 上传 + GPU 转换) → 示波器 → 合成 → 回读**。
//  界面程序与无窗口自检用的是同一个类，所以「我验过的」就是「你看到的」。
//
//  线程模型（重要）：
//    · 渲染线程：独占 D3D11 设备、采集设备、示波器引擎 —— 一帧一帧地跑，绝不让 UI 线程碰 D3D。
//    · UI 线程：只做一件事 —— 把最新一帧的字节拷进 WriteableBitmap。
//      用「双缓冲 + 锁」而不是队列：界面永远只要最新帧，追不上就丢帧，这才是监视器该有的行为。
//
//  ⚠️ 为什么合成纹理用 B8G8R8A8（而不是工程里到处用的 R8G8B8A8）：
//    WPF 没有 Rgba32 这个像素格式。让 D3D 直接以 BGRA 内存序输出，
//    界面那一层就是一次 memcpy；否则每帧要在 UI 线程上做 200 万次通道交换（几毫秒纯浪费）。
//    存 PNG 时才换回来（低频操作）。
//

using System.Diagnostics;
using System.IO;
using VideoScopePad.Win.Capture;
using VideoScopePad.Win.Core;
using VideoScopePad.Win.Render;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;

namespace VideoScopePad.App;

/// <summary>信号源</summary>
public enum LiveSourceKind
{
    Synthetic,
    /// <summary>HDMI/UVC 采集卡（旧的固定写法，实际用哪个设备看 SelectedDeviceKey）</summary>
    CaptureCard,
    Camera,
    /// <summary>按设备标识选的任意一路采集设备（多张卡时用这个）</summary>
    Device,
}

/// <summary>一帧的统计信息（界面顶部那一行就显示它）</summary>
public sealed record LiveStats(
    string Source,
    string Format,
    string Color,
    double CaptureFps,
    double DisplayFps,
    double SourceMs,
    double ScopeMs,
    double CompositeMs,
    double ReadbackMs,
    long Frames,
    string Message)
{
    public static LiveStats Empty { get; } = new(
        "—", "—", "—", 0, 0, 0, 0, 0, 0, 0, "未启动");

    /// <summary>一帧从取到到能显示的总耗时（毫秒）</summary>
    public double TotalMs => SourceMs + ScopeMs + CompositeMs + ReadbackMs;
}

public sealed class LiveSession : IDisposable
{
    private readonly int _width;
    private readonly int _height;
    private readonly object _frameLock = new();

    private byte[] _front;          // UI 线程读这个
    private byte[] _back;           // 渲染线程写这个
    private long _sequence;
    private long _readSequence = -1;

    private Thread? _thread;
    private volatile bool _running;
    private volatile bool _rebuildRequested = true;
    /// <summary>换源/换设备才置位（会拆掉采集设备重开）；布局变化不要置这个，见渲染循环里的说明</summary>
    private volatile bool _sourceChanged = true;
    private volatile LiveSourceKind _kind = LiveSourceKind.Synthetic;

    // 设备列表 / 热插拔状态
    private readonly DeviceWatcher _watcher = new();
    private int _deviceRevision;
    private volatile bool _deviceRefreshRequested;
    private volatile bool _pendingDeviceResolve;
    private string? _selectedDeviceKey;
    private string? _selectedDeviceName;
    private string _deviceState = string.Empty;
    private DateTime _nextOpenAttempt = DateTime.MinValue;
    private DateTime _nextDeviceRefresh = DateTime.MinValue;
    private volatile string _deviceFragment = "UT-VID";
    private LiveStats _stats = LiveStats.Empty;
    private int _videoWidth;
    private int _videoHeight;
    private ScopeRenderSettings _scopeSettings = new();

    private SignalMeasurement _measurement = SignalMeasurement.Empty;
    private SignalMeasurement? _referenceMeasurement;
    private readonly PeakHoldTracker _peakHold = new();
    private PeakHoldState _peakHoldState;
    private PeakHoldState? _referencePeakHoldState;

    /// <summary>参考层请求：0 = 无事，1 = 抓取，2 = 清除（渲染线程每帧只处理一次）</summary>
    private int _referenceRequest;

    private volatile MonitorLayoutPreset _preset = MonitorLayoutPreset.Quad;
    private volatile PaneContent _fullscreenContent = PaneContent.Picture;
    private volatile WaveformMode _waveformMode = WaveformMode.Luma;
    private readonly PaneContent[] _quadContents =
    {
        PaneContent.Picture, PaneContent.Waveform, PaneContent.Vectorscope, PaneContent.Parade,
    };

    /// <summary>参考层不透明度（0.05…1.0，默认 0.55 —— 与 iPad 版 referenceOpacity 默认值一致）</summary>
    public double ReferenceOpacity { get; set; } = 0.55;

    /// <summary>
    /// 色彩矩阵覆盖：null = 跟随驱动声明，否则强制按这一套解码。
    ///
    /// 为什么必须有这个开关：**采集卡声明的矩阵会错**。本机那张 UT-VID 声明 BT.601，
    /// 但用它自己的 709 彩条实测（`mf-capture bars`）：
    ///   BT.709 解码平均误差 11.0（每条彩条只差 0–3 个码值），
    ///   BT.601 解码平均误差 16.0，而且把饱和色主通道压到 232/233（该 255，差约 9%）。
    /// 照声明解会让矢量点整体偏移、读数也偏 —— 所以给一个覆盖开关，用彩条一测就知道该用哪个。
    /// </summary>
    public VideoTransferMatrix? ColorMatrixOverride { get; set; }

    /// <summary>把驱动的声明与覆盖开关合起来，得到真正要用的解码参数</summary>
    private VideoColorInfo EffectiveColor(VideoColorInfo declared)
        => ColorMatrixOverride is { } matrix && matrix != declared.Matrix
            ? declared with { Matrix = matrix, MatrixFromDriver = false }
            : declared;

    /// <summary>是否把参考层叠在实时轨迹上（= iPad 版的 freezeReference）</summary>
    public bool ShowReference { get; set; } = true;

    /// <summary>冻结时连实时画面一起冻住（默认关；与 iPad 版的 freezePictureToo 对应）</summary>
    public bool FreezePictureToo { get; set; }

    /// <summary>最新一帧的幅度读数</summary>
    public SignalMeasurement Measurement => Volatile.Read(ref _measurement);

    /// <summary>抓参考那一刻的读数（没抓过就是 null）</summary>
    public SignalMeasurement? ReferenceMeasurement => Volatile.Read(ref _referenceMeasurement);

    /// <summary>峰值保持游标（实时）</summary>
    public PeakHoldState PeakHold => _peakHoldState;

    /// <summary>峰值保持游标（参考层）</summary>
    public PeakHoldState? ReferencePeakHold => _referencePeakHoldState;

    /// <summary>是否已抓有参考层</summary>
    public bool HasReference => _referenceMeasurement is not null;

    /// <summary>请求抓一份参考层（下一帧在渲染线程上执行，保证抓的就是刚算完的那一帧）</summary>
    public void RequestReferenceCapture() => Interlocked.Exchange(ref _referenceRequest, 1);

    /// <summary>请求清除参考层</summary>
    public void RequestReferenceClear() => Interlocked.Exchange(ref _referenceRequest, 2);

    // ------------------------------------------------------------------
    //  布局与格内容（与 iPad 版一样：布局是唯一来源，引擎按可见格子决定要算什么）
    // ------------------------------------------------------------------

    /// <summary>布局预设（全屏 / 四分割 / …）</summary>
    public MonitorLayoutPreset Preset
    {
        get => _preset;
        set { _preset = value; _rebuildRequested = true; }
    }

    /// <summary>全屏时那一格显示什么</summary>
    public PaneContent FullscreenContent
    {
        get => _fullscreenContent;
        set { _fullscreenContent = value; _rebuildRequested = true; }
    }

    /// <summary>波形模式（亮度 / RGB 叠加）</summary>
    public WaveformMode WaveformMode
    {
        get => _waveformMode;
        set { _waveformMode = value; _rebuildRequested = true; }
    }

    /// <summary>四分割每一格显示什么（4 个；界面里逐格可换）</summary>
    public IReadOnlyList<PaneContent> QuadContents => _quadContents;

    /// <summary>改某一格的内容</summary>
    public void SetQuadContent(int slot, PaneContent content)
    {
        if (slot is < 0 or > 3 || _quadContents[slot] == content)
        {
            return;
        }
        _quadContents[slot] = content;
        _sourceChanged = true;
    }

    /// <summary>无窗口自检用：跑够这么多帧就自己退出（0 = 一直跑）</summary>
    public int StopAfterFrames { get; init; }

    /// <summary>UI 刷新上限（帧/秒）。0 = 不限</summary>
    public double DisplayFpsCap { get; init; } = 60;

    /// <summary>示波器采样步长（1 = 全采，2 = 隔点采；实时预览用 2 省一半算力）</summary>
    public int ScopeStride { get; init; } = 1;

    /// <summary>
    /// 合成信号是否加动态元素（扫掠竖线 + 伸缩码值条）。
    /// 界面里要开（一眼看出画面在刷新）；**自检要关** —— 那根 100 IRE 的白线会把峰值读数
    /// 顶到 100 IRE、把色度峰抬到 89%，读数就没法用已知码值精确核对了。
    /// </summary>
    public bool AnimateSynthetic { get; init; } = true;

    public LiveSession(int width, int height)
    {
        _width = width;
        _height = height;
        _front = new byte[width * height * 4];
        _back = new byte[width * height * 4];
    }

    public int Width => _width;

    public int Height => _height;

    public LiveStats Stats => Volatile.Read(ref _stats);

    /// <summary>当前布局（界面覆盖层与自检取样点都用它，与 GPU 出来的画面严格对齐）</summary>
    public ScopeLayoutResult Layout { get; private set; } = new();

    /// <summary>当前这一帧向引擎要的数据（由布局推出来；自检拿它核对"只算看得见的格子"）</summary>
    public ScopeRenderSettings ScopeSettings => _scopeSettings;

    /// <summary>信号源的原始尺寸（画面格里按它做等比适配）</summary>
    public int VideoWidth => _videoWidth;

    public int VideoHeight => _videoHeight;

    public LiveSourceKind Source => _kind;

    // ------------------------------------------------------------------
    //  设备列表与热插拔（不再假设只有一张卡）
    // ------------------------------------------------------------------

    /// <summary>最近一次枚举到的设备列表（渲染线程刷新，界面只读快照）</summary>
    public IReadOnlyList<CaptureDeviceInfo> AvailableDevices => _watcher.Devices;

    /// <summary>设备列表版本号：界面靠它判断要不要重建下拉</summary>
    public int DeviceListRevision => Volatile.Read(ref _deviceRevision);

    /// <summary>当前选中的设备标识（符号链接；null = 合成信号）</summary>
    public string? SelectedDeviceKey => _selectedDeviceKey;

    /// <summary>设备在场状态的人话描述（等待接入 / 已被占用 / 正常）</summary>
    public string DeviceState => _deviceState;

    /// <summary>立刻重新枚举设备（界面上的「刷新设备」按钮）</summary>
    public void RequestDeviceRefresh() => _deviceRefreshRequested = true;

    /// <summary>
    /// 选设备：key 是 <see cref="DeviceWatcher.KeyOf"/> 出来的标识；传 null = 回到合成信号。
    /// 切源与"设备回来了自动重开"走的是同一条路（下一帧重建）。
    /// </summary>
    public void SelectDevice(string? key, string? displayName = null)
    {
        _selectedDeviceKey = key;
        _selectedDeviceName = displayName ?? key;
        _kind = key is null ? LiveSourceKind.Synthetic : LiveSourceKind.Device;
        _nextOpenAttempt = DateTime.MinValue;   // 立刻试一次，别等退避
        _sourceChanged = true;
    }

    /// <summary>切换信号源（渲染线程下一帧生效）</summary>
    public void SwitchSource(LiveSourceKind kind, string? deviceFragment = null)
    {
        _kind = kind;
        if (kind == LiveSourceKind.Synthetic)
        {
            _selectedDeviceKey = null;
            _selectedDeviceName = null;
        }
        else if (!string.IsNullOrWhiteSpace(deviceFragment))
        {
            _deviceFragment = deviceFragment!;
            _pendingDeviceResolve = true;   // 片段 → 标识的解析放在渲染线程（枚举要在那边做）
        }
        _nextOpenAttempt = DateTime.MinValue;
        _sourceChanged = true;
    }

    public void Start()
    {
        if (_running)
        {
            return;
        }
        _running = true;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "VideoScopePad-Live",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(TimeSpan.FromSeconds(3));
        _thread = null;
    }

    /// <summary>
    /// 把最新一帧拷进 <paramref name="destination"/>（长度必须 ≥ width×height×4）。
    /// 返回 false = 还没有新帧（界面这帧不用重画）。
    /// </summary>
    public bool TryCopyLatestFrame(byte[] destination)
    {
        lock (_frameLock)
        {
            if (_sequence == _readSequence)
            {
                return false;
            }
            Buffer.BlockCopy(_front, 0, destination, 0, _front.Length);
            _readSequence = _sequence;
            return true;
        }
    }

    /// <summary>把最新一帧存成 PNG（BGRA → RGBA 只在这里换一次）</summary>
    public void SaveLatestFramePng(string path)
    {
        byte[] snapshot;
        lock (_frameLock)
        {
            snapshot = new byte[_front.Length];
            Buffer.BlockCopy(_front, 0, snapshot, 0, _front.Length);
        }

        var rgba = new byte[snapshot.Length];
        for (int i = 0; i + 3 < snapshot.Length; i += 4)
        {
            rgba[i] = snapshot[i + 2];      // R ← B
            rgba[i + 1] = snapshot[i + 1];  // G
            rgba[i + 2] = snapshot[i];      // B ← R
            rgba[i + 3] = 255;
        }

        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        PngWriter.Write(path, _width, _height, rgba);
    }

    // ------------------------------------------------------------------
    //  渲染线程
    // ------------------------------------------------------------------
    private void Run()
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var d3d = D3DContext.Create();
            string shaderDirectory = Path.Combine(AppContext.BaseDirectory, "Render", "Shaders");
            using var shaders = ShaderLibrary.Create(d3d.Device, shaderDirectory);
            using var pipelines = new PipelineLibrary(d3d.Device, shaders);
            using var engine = new ScopeEngine(d3d.Device, shaders);
            using var renderer = new VideoRenderer(d3d.Device, pipelines);
            using var uploader = new YuvFrameUploader(d3d.Device, shaders);
            using var composite = d3d.CreateDisplayTarget(_width, _height);
            using var compositeView = d3d.Device.CreateRenderTargetView(composite);
            using var readback = new ReadbackBuffer(d3d.Device, _width, _height, Format.B8G8R8A8_UNorm);

            // 合成信号：一张可反复更新的 RGBA 纹理（不是每帧新建，避免显存与 GC 抖动）
            byte[] syntheticPixels = new byte[_width * _height * 4];
            using var synthetic = d3d.Device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)_width,
                Height = (uint)_height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R8G8B8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
            });
            using var syntheticSrv = d3d.Device.CreateShaderResourceView(synthetic);

            using var mediaFoundation = MediaFoundationRuntime.Start();

            var options = new RenderOptions();

            CaptureDevice? device = null;
            LiveSourceKind currentKind = LiveSourceKind.Synthetic;
            string message = string.Empty;
            string sourceName = "合成测试信号";
            string formatText = $"{_width}×{_height} 合成";
            string colorText = "全范围 0–255（合成信号本身就是 R'G'B'）";

            // 「冻结时连实时画面一起冻住」用的画面副本（默认不用，所以先建好但不占几 MB）
            using var frozenSource = d3d.Device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)_width,
                Height = (uint)_height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R8G8B8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
            });
            using var frozenSourceSrv = d3d.Device.CreateShaderResourceView(frozenSource);
            bool pictureFrozen = false;

            var syntheticSource = new SyntheticLiveSource();
            long frames = 0;
            var captureMeter = new CaptureRateMeter();
            double displayFps = 0;
            long lastDisplayTimestamp = 0;
            double sourceMs = 0, scopeMs = 0, compositeMs = 0, readbackMs = 0;

            SetLayout(renderer, _width, _height, _width, _height);

            while (_running)
            {
                // ---------- 设备在场服务（每帧，内部节流）----------
                // 为什么放在这儿而不是放在重建分支里：重建分支只在"换源"时跑一次，
                // 而热插拔要**持续**看着设备列表 —— 每帧检查、每秒真枚举。
                if (_deviceRefreshRequested || DateTime.UtcNow >= _nextDeviceRefresh)
                {
                    _deviceRefreshRequested = false;
                    _nextDeviceRefresh = DateTime.UtcNow.AddSeconds(1);
                    _watcher.Refresh();
                    Volatile.Write(ref _deviceRevision, _watcher.Revision);
                }

                if (_kind != LiveSourceKind.Synthetic)
                {
                    // 名字片段 → 设备标识（片段是命令行/旧界面给的，标识才是稳定选择）
                    if (_pendingDeviceResolve && DateTime.UtcNow >= _nextOpenAttempt)
                    {
                        CaptureDeviceInfo? resolved = _watcher.FindByKeyOrName(_deviceFragment ?? string.Empty)
                            ?? (int.TryParse(_deviceFragment, out int index) ? _watcher.FindByIndex(index) : null);
                        if (resolved is not null)
                        {
                            _pendingDeviceResolve = false;
                            _selectedDeviceKey = DeviceWatcher.KeyOf(resolved);
                            _selectedDeviceName = resolved.FriendlyName;
                            _sourceChanged = true;
                        }
                        else
                        {
                            // 还没插上：保持"待解析"，1 秒后再试（不会每帧刷）
                            _nextOpenAttempt = DateTime.UtcNow.AddSeconds(1);
                            _deviceState = $"等待设备接入（按名字找：{_deviceFragment}）";
                        }
                    }

                    if (device is not null && _watcher.Find(_selectedDeviceKey) is null)
                    {
                        // 用着的设备被拔了：立刻释放句柄并回到"等待接入"（继续用会一直读失败）
                        device.Dispose();
                        device = null;
                        _deviceState = "已被拔出";
                        _sourceChanged = true;
                    }
                    else if (device is null && _selectedDeviceKey is not null && DateTime.UtcNow >= _nextOpenAttempt)
                    {
                        // 该开却还没开（首次 / 插回来了 / 上次被占用）：让重建分支去开，
                        // 失败时那边会把 _nextOpenAttempt 往后推 1 秒，避免每帧都去抢设备
                        _sourceChanged = true;
                    }
                }

                // ---------- 布局变化：只重算布局，**绝不碰采集设备** ----------
                // 🔴 血泪坑：以前把"重算布局"和"重开设备"合在一个 _rebuildRequested 分支里，
                //    结果换一下格内容就把 UVC 设备拆掉重开 —— 重开要做格式协商 + 等首帧，
                //    偶尔还会卡住（实测：真实卡跑 60 fps 时，切布局后帧数停在 77、
                //    消息还是空的，因为渲染线程正卡在重开设备里）。
                //    自检里那条"3 秒内没等到新帧"就是这么暴露出来的。
                if (_rebuildRequested)
                {
                    _rebuildRequested = false;
                    if (device is not null && _videoWidth > 0 && _videoHeight > 0)
                    {
                        // 设备的画面尺寸不变 → 按当前视频尺寸重算布局即可
                        SetLayout(renderer, _width, _height, _videoWidth, _videoHeight);
                    }
                    else
                    {
                        SetLayout(renderer, _width, _height, _width, _height);
                    }
                }

                // ---------- 换源 / 设备重开：才需要拆掉设备 ----------
                if (_sourceChanged)
                {
                    _sourceChanged = false;
                    device?.Dispose();
                    device = null;
                    currentKind = _kind;
                    captureMeter.Reset();

                    if (currentKind == LiveSourceKind.Synthetic)
                    {
                        sourceName = "合成测试信号";
                        formatText = $"{_width}×{_height} 合成";
                        colorText = "全范围 0–255（合成信号本身就是 R'G'B'）";
                        message = "彩条 + PLUGE + 灰阶，另有一根扫过全场的竖线用来证明「画面是活的」";
                        _deviceState = string.Empty;
                        SetLayout(renderer, _width, _height, _width, _height);
                    }
                    else
                    {
                        CaptureDeviceInfo? target = _watcher.Find(_selectedDeviceKey);
                        if (target is null)
                        {
                            // 设备不在场：不报错、不清空选择，等它回来（这就是热插拔恢复）
                            string wanted = _selectedDeviceName ?? _deviceFragment ?? "所选设备";
                            sourceName = wanted;
                            formatText = "—";
                            colorText = "—";
                            _deviceState = "等待设备接入";
                            message = $"「{wanted}」现在不在设备列表里（拔掉了 / 还没插上）；"
                                    + "插回来会自动恢复，不用重开程序";
                            SetLayout(renderer, _width, _height, _width, _height);
                        }
                        else if (device is null && DateTime.UtcNow >= _nextOpenAttempt)
                        {
                            try
                            {
                                device = CaptureDevice.Open(target);
                                IReadOnlyList<CaptureFormat> formats = device.GetNativeFormats();
                                CaptureFormat picked = CaptureFormat.PickPreferred(formats)
                                    ?? throw new InvalidOperationException("设备没有可用格式");
                                CaptureFormat effective = device.SetNativeFormat(picked.NativeIndex);
                                sourceName = device.Info.FriendlyName;
                                _selectedDeviceName = sourceName;
                                formatText = effective.Describe();
                                colorText = EffectiveColor(effective.Color).Summary
                                          + (ColorMatrixOverride is null ? string.Empty : "（矩阵已手动覆盖）");
                                _deviceState = "正常";
                                SetLayout(renderer, _width, _height, (int)effective.Width, (int)effective.Height);
                                message = string.Empty;
                            }
                            catch (Exception ex)
                            {
                                device = null;
                                sourceName = _selectedDeviceName ?? "采集设备";
                                formatText = "—";
                                colorText = "—";
                                // 失败要退避重试：被别的程序占着（OBS 之类）时，一秒钟一次就够，
                                // 每帧都去开只会在日志里刷屏、还拖慢渲染线程。
                                _nextOpenAttempt = DateTime.UtcNow.AddSeconds(1);
                                _deviceState = "打开失败，1 秒后重试";
                                message = $"打开「{_selectedDeviceName}」失败：{ex.Message}"
                                        + "　→ 暂时显示合成信号（设备被别的程序占用 / 没插好 / 没接信号都会这样）";
                                SetLayout(renderer, _width, _height, _width, _height);
                            }
                        }
                    }
                }

                ID3D11ShaderResourceView? source = null;
                ID3D11Texture2D? sourceTexture = null;

                if (device is not null)
                {
                    long start = Stopwatch.GetTimestamp();
                    CapturedFrame? frame = device.ReadFrame();
                    if (frame is null)
                    {
                        continue;   // 这一轮没有样本（流 tick 等），继续读
                    }

                    CaptureFormat current = device.CurrentFormat;
                    source = uploader.Convert(d3d.Context, frame, current.SubtypeName, EffectiveColor(current.Color));
                    sourceTexture = uploader.RgbTexture;
                    captureMeter.Add(frame.TimestampHns);
                    sourceMs = MsSince(start);
                    frames++;
                }
                else
                {
                    long start = Stopwatch.GetTimestamp();
                    syntheticSource.Render(syntheticPixels, _width, _height, frames, AnimateSynthetic);
                    d3d.Context.UpdateSubresource<byte>(syntheticPixels, synthetic, 0, (uint)(_width * 4), 0, null);
                    source = syntheticSrv;
                    sourceTexture = synthetic;
                    sourceMs = MsSince(start);
                    frames++;
                }

                try
                {
                    long start = Stopwatch.GetTimestamp();
                    engine.Encode(d3d.Context, source, _scopeSettings);
                    scopeMs = MsSince(start);

                    // 测量（数值读数）：GPU 累加 + 非阻塞回读，回调里更新读数与峰值保持
                    engine.EncodeMeasurement(d3d.Context, source, ScopeStride, counts =>
                    {
                        if (SignalMeasurementBuilder.FromMeasureBuffer(counts) is { } measured)
                        {
                            Volatile.Write(ref _measurement, measured);
                            _peakHold.Update(measured, stopwatch.Elapsed.TotalSeconds);
                            _peakHoldState = _peakHold.Snapshot();
                        }
                    });

                    // 冻结参考层请求（抓取必须在 Encode 之后：抓到的才是刚算完的这一帧）
                    int request = Interlocked.Exchange(ref _referenceRequest, 0);
                    if (request == 1)
                    {
                        engine.CaptureReference(d3d.Context);
                        Volatile.Write(ref _referenceMeasurement, _measurement);
                        _referencePeakHoldState = _peakHold.Snapshot();

                        // 「冻结时连实时画面一起冻住」（默认关）：把当前画面源也拷一份，
                        // 之后画面格用这份冻结纹理，而示波器仍从实时源算 —— 与 iPad 版语义一致。
                        if (FreezePictureToo && sourceTexture is not null && frozenSource is not null)
                        {
                            d3d.Context.CopyResource(frozenSource, sourceTexture);
                            pictureFrozen = true;
                        }
                    }
                    else if (request == 2)
                    {
                        engine.ClearReference();
                        Volatile.Write(ref _referenceMeasurement, null);
                        _referencePeakHoldState = null;
                        pictureFrozen = false;
                    }

                    options.ShowReference = ShowReference && engine.HasReference;
                    options.ReferenceOpacity = ReferenceOpacity;
                    options.WaveformMode = _waveformMode;

                    start = Stopwatch.GetTimestamp();
                    renderer.Render(d3d.Context, compositeView, _width, _height,
                                    pictureFrozen ? frozenSourceSrv! : source, engine, options);
                    compositeMs = MsSince(start);

                    start = Stopwatch.GetTimestamp();
                    byte[] pixels = readback.Read(d3d.Context, composite);
                    readbackMs = MsSince(start);

                    lock (_frameLock)
                    {
                        (_front, _back) = (_back, _front);   // 交换后 _back 是刚空出来的旧前缓冲
                        Buffer.BlockCopy(pixels, 0, _front, 0, _front.Length);
                        _sequence++;
                    }
                }
                catch (Exception ex)
                {
                    message = $"渲染出错：{ex.GetType().Name}: {ex.Message}";
                    Thread.Sleep(200);
                    continue;
                }

                // 显示帧率（按实际交付给界面的帧算）
                long now = Stopwatch.GetTimestamp();
                if (lastDisplayTimestamp != 0)
                {
                    double instant = 1000.0 / MsSince(lastDisplayTimestamp);
                    displayFps = displayFps == 0 ? instant : displayFps * 0.9 + instant * 0.1;
                }
                lastDisplayTimestamp = now;

                Volatile.Write(ref _stats, new LiveStats(
                    sourceName, formatText, colorText,
                    captureMeter.MeasuredFps, displayFps,
                    sourceMs, scopeMs, compositeMs, readbackMs,
                    frames, message));

                if (StopAfterFrames > 0 && frames >= StopAfterFrames)
                {
                    _running = false;
                }

                // 限速：界面用不到的帧不必读回（读回是这条路里最贵的一步）
                if (DisplayFpsCap > 0)
                {
                    double target = 1000.0 / DisplayFpsCap;
                    double spent = MsSince(now);
                    if (spent < target)
                    {
                        Thread.Sleep((int)Math.Max(0, target - spent));
                    }
                }
            }

            device?.Dispose();
        }
        catch (Exception ex)
        {
            // 把异常类型与栈的前两行也带出来：这个 catch 是渲染线程的最后一道网，
            // 只写一句"启动失败"的话，上层（无窗口自检/界面）根本看不出是哪一步炸的。
            string where = ex.StackTrace is { Length: > 0 } stack
                ? string.Join(" | ", stack.Split('\n').Take(2).Select(line => line.Trim()))
                : string.Empty;
            Volatile.Write(ref _stats, LiveStats.Empty with
            {
                Message = $"实时链路启动失败：{ex.GetType().Name}: {ex.Message}　@{where}",
            });
            _running = false;
        }
    }

    /// <summary>从时间戳算毫秒（Stopwatch.ElapsedMilliseconds 只有整数毫秒，会把亚毫秒的阶段显示成 0）</summary>
    private static double MsSince(long timestamp)
        => (Stopwatch.GetTimestamp() - timestamp) * 1000.0 / Stopwatch.Frequency;

    /// <summary>
    /// 把「信号源像素坐标」换算成「合成帧缓冲像素坐标」（自检取样用）。
    /// 画面格里的视频是等比适配（可能有留白），所以必须走布局里的画面区矩形，
    /// 不能直接按帧缓冲比例取样 —— 否则会取到别的彩条上。
    /// </summary>
    public bool TryMapVideoPixelToFrame(int videoX, int videoY, out int frameX, out int frameY)
    {
        frameX = 0;
        frameY = 0;
        ScopeLayoutResult layout = Layout;
        if (_videoWidth <= 0 || _videoHeight <= 0)
        {
            return false;
        }

        foreach (PaneLayout pane in layout.Panes)
        {
            if (pane.Content != PaneContent.Picture || pane.Video is not { } video)
            {
                continue;
            }

            double u = video.MinX + (videoX + 0.5) / _videoWidth * video.Width;
            double v = video.MinY + (videoY + 0.5) / _videoHeight * video.Height;
            frameX = Math.Clamp((int)(u * _width), 0, _width - 1);
            frameY = Math.Clamp((int)(v * _height), 0, _height - 1);
            return true;
        }
        return false;
    }

    /// <summary>
    /// 布局：容器是合成目标尺寸，video 是信号源尺寸（决定画面格里的等比适配）。
    /// 格内容来自 Preset / FullscreenContent / QuadContents —— 与 iPad 版同一个来源，
    /// 引擎要不要算什么也由这份布局决定（见 ScopeRenderSettings.ForLayout）。
    /// </summary>
    private void SetLayout(VideoRenderer renderer,
                           int containerWidth,
                           int containerHeight,
                           int videoWidth,
                           int videoHeight)
    {
        _videoWidth = videoWidth;
        _videoHeight = videoHeight;
        Layout = ScopeLayout.Compute(
            containerWidth: containerWidth,
            containerHeight: containerHeight,
            videoWidth: videoWidth,
            videoHeight: videoHeight,
            preset: _preset,
            aspectMode: AspectMode.Fit,
            fullscreenContent: _fullscreenContent,
            quadContents: _quadContents,
            legacyPanels: Array.Empty<ScopePanelKind>());
        renderer.Layout = Layout;
        _scopeSettings = ScopeRenderSettings.ForLayout(Layout, ScopeStride);
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}
