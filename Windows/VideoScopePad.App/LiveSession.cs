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

namespace VideoScopePad.App;

/// <summary>信号源</summary>
public enum LiveSourceKind
{
    /// <summary>合成测试信号（彩条 + PLUGE + 灰阶；不需要任何硬件，界面自检用）</summary>
    Synthetic,

    /// <summary>HDMI 采集卡（UVC）</summary>
    CaptureCard,

    /// <summary>摄像头（内建 / 外接 USB）</summary>
    Camera,
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
    private volatile LiveSourceKind _kind = LiveSourceKind.Synthetic;
    private volatile string _deviceFragment = "UT-VID";
    private LiveStats _stats = LiveStats.Empty;
    private int _videoWidth;
    private int _videoHeight;

    /// <summary>无窗口自检用：跑够这么多帧就自己退出（0 = 一直跑）</summary>
    public int StopAfterFrames { get; init; }

    /// <summary>UI 刷新上限（帧/秒）。0 = 不限</summary>
    public double DisplayFpsCap { get; init; } = 60;

    /// <summary>示波器采样步长（1 = 全采，2 = 隔点采；实时预览用 2 省一半算力）</summary>
    public int ScopeStride { get; init; } = 1;

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

    /// <summary>信号源的原始尺寸（画面格里按它做等比适配）</summary>
    public int VideoWidth => _videoWidth;

    public int VideoHeight => _videoHeight;

    public LiveSourceKind Source => _kind;

    /// <summary>切换信号源（渲染线程下一帧生效）</summary>
    public void SwitchSource(LiveSourceKind kind, string? deviceFragment = null)
    {
        _kind = kind;
        if (!string.IsNullOrWhiteSpace(deviceFragment))
        {
            _deviceFragment = deviceFragment!;
        }
        _rebuildRequested = true;
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
            var scopeSettings = new ScopeRenderSettings
            {
                NeedWaveform = true,
                NeedVectorscope = true,
                NeedDiamond = false,
                NeedCie = false,
                Stride = ScopeStride,
            };

            CaptureDevice? device = null;
            LiveSourceKind currentKind = LiveSourceKind.Synthetic;
            string message = string.Empty;
            string sourceName = "合成测试信号";
            string formatText = $"{_width}×{_height} 合成";
            string colorText = "全范围 0–255（合成信号本身就是 R'G'B'）";

            var syntheticSource = new SyntheticLiveSource();
            long frames = 0;
            var captureMeter = new CaptureRateMeter();
            double displayFps = 0;
            long lastDisplayTimestamp = 0;
            double sourceMs = 0, scopeMs = 0, compositeMs = 0, readbackMs = 0;

            SetLayout(renderer, _width, _height, _width, _height);

            while (_running)
            {
                // ---------- 换源 ----------
                if (_rebuildRequested)
                {
                    _rebuildRequested = false;
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
                        SetLayout(renderer, _width, _height, _width, _height);
                    }
                    else
                    {
                        // 采集卡按名字片段找；摄像头取内建那台
                        string fragment = currentKind == LiveSourceKind.CaptureCard
                            ? _deviceFragment
                            : "Integrated Camera";
                        try
                        {
                            device = CaptureDevice.OpenByName(fragment);
                            IReadOnlyList<CaptureFormat> formats = device.GetNativeFormats();
                            CaptureFormat picked = CaptureFormat.PickPreferred(formats)
                                ?? throw new InvalidOperationException("设备没有可用格式");
                            CaptureFormat effective = device.SetNativeFormat(picked.NativeIndex);
                            sourceName = device.Info.FriendlyName;
                            formatText = effective.Describe();
                            colorText = effective.Color.Summary;
                            SetLayout(renderer, _width, _height, (int)effective.Width, (int)effective.Height);
                            message = string.Empty;
                        }
                        catch (Exception ex)
                        {
                            device = null;
                            sourceName = currentKind == LiveSourceKind.CaptureCard ? "采集卡（未找到）" : "摄像头（未找到）";
                            formatText = "—";
                            colorText = "—";
                            message = $"打开失败：{ex.Message}　→ 暂时显示合成信号"
                                    + "（采集卡没插好 / HDMI 没接 / 被别的程序占用都会这样）";
                            SetLayout(renderer, _width, _height, _width, _height);
                        }
                    }
                }

                ID3D11ShaderResourceView? source = null;

                if (device is not null)
                {
                    long start = Stopwatch.GetTimestamp();
                    CapturedFrame? frame = device.ReadFrame();
                    if (frame is null)
                    {
                        continue;   // 这一轮没有样本（流 tick 等），继续读
                    }

                    CaptureFormat current = device.CurrentFormat;
                    source = uploader.Convert(d3d.Context, frame, current.SubtypeName, current.Color);
                    captureMeter.Add(frame.TimestampHns);
                    sourceMs = MsSince(start);
                    frames++;
                }
                else
                {
                    long start = Stopwatch.GetTimestamp();
                    syntheticSource.Render(syntheticPixels, _width, _height, frames);
                    d3d.Context.UpdateSubresource<byte>(syntheticPixels, synthetic, 0, (uint)(_width * 4), 0, null);
                    source = syntheticSrv;
                    sourceMs = MsSince(start);
                    frames++;
                }

                try
                {
                    long start = Stopwatch.GetTimestamp();
                    engine.Encode(d3d.Context, source, scopeSettings);
                    scopeMs = MsSince(start);

                    start = Stopwatch.GetTimestamp();
                    renderer.Render(d3d.Context, compositeView, _width, _height, source, engine, options);
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
            Volatile.Write(ref _stats, LiveStats.Empty with { Message = $"实时链路启动失败：{ex.GetType().Name}: {ex.Message}" });
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
    /// 四分割布局：画面 / 亮度波形 / 矢量图 / RGB Parade（与 iPad 版默认一致）。
    /// container 是合成目标尺寸，video 是信号源尺寸（决定画面格里的等比适配）。
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
            preset: MonitorLayoutPreset.Quad,
            aspectMode: AspectMode.Fit,
            fullscreenContent: PaneContent.Picture,
            quadContents: new[]
            {
                PaneContent.Picture, PaneContent.Waveform, PaneContent.Vectorscope, PaneContent.Parade,
            },
            legacyPanels: Array.Empty<ScopePanelKind>());
        renderer.Layout = Layout;
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}
