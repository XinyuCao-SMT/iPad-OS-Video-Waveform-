//
//  MainWindow.xaml.cs
//  VideoScopePad.App
//
//  窗口这边只干三件事：
//    1) 起一个 LiveSession（采集 + 示波器 + 合成全在它的渲染线程里）
//    2) 每个渲染时机把最新一帧拷进 WriteableBitmap（**一次 memcpy**，不做通道交换）
//    3) 显示状态、切换信号源、存图
//
//  ⚠️ 合成纹理是 B,G,R,A 内存序（D3D 的 B8G8R8A8_UNORM），
//     正好是 WPF <c>PixelFormats.Bgra32</c> 要的顺序 —— 这是刻意的，见 LiveSession 注释。
//

using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VideoScopePad.Win.Capture;
using VideoScopePad.Win.Core;
using VideoScopePad.Win.Render;

namespace VideoScopePad.App;

public partial class MainWindow : Window
{
    private readonly LiveSession _session;
    private WriteableBitmap? _bitmap;
    private byte[] _frameBuffer;
    private long _lastStatsTicks;

    /// <summary>程序集版本（csproj 里的 <c>Version</c>）—— 标题栏与发版清单都用它</summary>
    private static string AppVersion
    {
        get
        {
            Version? version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    public MainWindow(string[] args)
    {
        InitializeComponent();

        // 标题栏带上版本号：手里拿着哪个 exe、回滚到哪一版，一眼就能对上
        // （iPad 版靠 CFBundleShortVersionString，Windows 这边靠程序集版本）
        Title = $"VideoScopePad · Windows 版（实时监视） v{AppVersion}";

        int width = ArgumentValue(args, "--width") is { } w && int.TryParse(w, out int parsedWidth) ? parsedWidth : 1600;
        int height = ArgumentValue(args, "--height") is { } h && int.TryParse(h, out int parsedHeight) ? parsedHeight : 900;

        _session = new LiveSession(width, height)
        {
            DisplayFpsCap = 60,
            ScopeStride = 1,
        };
        _frameBuffer = new byte[width * height * 4];

        BuildLayoutRow();
        ZebraThresholdBox.SelectedIndex = 4;      // 默认 100 IRE（与 iPad 版一致）
        AlarmThresholdBox.SelectedIndex = 0;      // 默认门槛 1 次
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    /// <summary>
    /// 格内容选项表（下拉里显示什么 → 布局里的 PaneContent）。
    /// 「波形」两种模式在这里就是两个选项 —— 比"先选波形再选模式"少一步，
    /// 而且不会出现"选了波形但模式还是旧的"这种状态。
    /// </summary>
    private static readonly (string Label, PaneContent Content, WaveformMode Mode)[] ContentChoices =
    {
        ("画面", PaneContent.Picture, WaveformMode.Luma),
        ("波形（亮度）", PaneContent.Waveform, WaveformMode.Luma),
        ("波形（RGB 叠加）", PaneContent.Waveform, WaveformMode.RgbOverlay),
        ("RGB Parade", PaneContent.Parade, WaveformMode.Luma),
        ("矢量图", PaneContent.Vectorscope, WaveformMode.Luma),
        ("钻石图", PaneContent.Diamond, WaveformMode.Luma),
        ("马蹄图（CIE）", PaneContent.Cie, WaveformMode.Luma),
    };

    private ComboBox _layoutBox = null!;
    private ComboBox _matrixBox = null!;
    private ComboBox _rotationBox = null!;
    private TextBlock _matrixHint = null!;
    private ComboBox _fullscreenContentBox = null!;
    private readonly ComboBox[] _quadBoxes = new ComboBox[4];
    private StackPanel _fullscreenPanel = null!;
    private StackPanel _quadPanel = null!;
    private bool _buildingLayoutRow;

    /// <summary>建「布局 + 格内容」那一行（选项列表只写一处，多个下拉共用）</summary>
    private void BuildLayoutRow()
    {
        _buildingLayoutRow = true;

        LayoutRow.Children.Add(Label("布局"));
        _layoutBox = MakeComboBox(140);
        _layoutBox.Items.Add("四分割（逐格可换）");
        _layoutBox.Items.Add("全屏（一格铺满）");
        _layoutBox.Items.Add("底部条（画面 + 示波器条）");
        _layoutBox.Items.Add("右侧栏（画面 + 右栏示波器）");
        _layoutBox.Items.Add("叠加（示波器压在画面上）");
        _layoutBox.SelectedIndex = 0;
        _layoutBox.SelectionChanged += OnLayoutChanged;
        LayoutRow.Children.Add(_layoutBox);

        // 色彩矩阵：驱动声明的会错，必须能覆盖。
        // 本机那张 UT-VID 声明 BT.601，而用它的 709 彩条实测：709 解码平均误差 11.0、
        // 601 是 16.0 且把饱和色主通道压到 232/233（该 255）—— 按声明解矢量点会整体偏。
        LayoutRow.Children.Add(Label("色彩矩阵"));
        _matrixBox = MakeComboBox(150);
        _matrixBox.Items.Add("跟随驱动");
        _matrixBox.Items.Add("强制 BT.601");
        _matrixBox.Items.Add("强制 BT.709");
        _matrixBox.SelectedIndex = 0;
        _matrixBox.SelectionChanged += OnColorMatrixChanged;
        LayoutRow.Children.Add(_matrixBox);

        LayoutRow.Children.Add(Label("画面方向"));
        _rotationBox = MakeComboBox(130);
        _rotationBox.Items.Add("自动");
        _rotationBox.Items.Add("不旋转");
        _rotationBox.Items.Add("顺时针 90°");
        _rotationBox.Items.Add("逆时针 90°");
        _rotationBox.Items.Add("180°");
        _rotationBox.SelectedIndex = 0;
        _rotationBox.SelectionChanged += OnRotationChanged;
        LayoutRow.Children.Add(_rotationBox);
        _matrixHint = Label(string.Empty);
        _matrixHint.Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xC4, 0x6A));
        LayoutRow.Children.Add(_matrixHint);

        // 全屏：一个内容下拉
        _fullscreenPanel = new StackPanel { Orientation = Orientation.Horizontal };
        _fullscreenPanel.Children.Add(Label("内容"));
        _fullscreenContentBox = MakeComboBox(160);
        AddContentChoices(_fullscreenContentBox, PaneContent.Picture, WaveformMode.Luma);
        _fullscreenContentBox.SelectionChanged += OnFullscreenContentChanged;
        _fullscreenPanel.Children.Add(_fullscreenContentBox);
        _fullscreenPanel.Visibility = Visibility.Collapsed;
        LayoutRow.Children.Add(_fullscreenPanel);

        // 四分割：四个格各自一个内容下拉
        _quadPanel = new StackPanel { Orientation = Orientation.Horizontal };
        for (int slot = 0; slot < 4; slot++)
        {
            _quadPanel.Children.Add(Label($"格{slot + 1}"));
            ComboBox box = MakeComboBox(150);
            PaneContent content = _session.QuadContents[slot];
            AddContentChoices(box, content, WaveformMode.Luma);
            int captured = slot;
            box.SelectionChanged += (_, _) => OnQuadContentChanged(captured, box);
            _quadBoxes[slot] = box;
            _quadPanel.Children.Add(box);
        }
        LayoutRow.Children.Add(_quadPanel);

        _buildingLayoutRow = false;
        UpdateLayoutRowVisibility();
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA4, 0xB2)),
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(10, 0, 6, 0),
    };

    private static ComboBox MakeComboBox(double width) => new()
    {
        Width = width,
        VerticalAlignment = VerticalAlignment.Center,
        Background = new SolidColorBrush(Color.FromRgb(0x22, 0x26, 0x2E)),
        Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xEA, 0xF0)),
        BorderBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x34, 0x40)),
    };

    private static void AddContentChoices(ComboBox box, PaneContent content, WaveformMode mode)
    {
        int selected = 0;
        for (int i = 0; i < ContentChoices.Length; i++)
        {
            box.Items.Add(ContentChoices[i].Label);
            if (ContentChoices[i].Content == content && ContentChoices[i].Mode == mode)
            {
                selected = i;
            }
        }
        box.SelectedIndex = selected;
    }

    private void OnLayoutChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_buildingLayoutRow || !IsLoaded)
        {
            return;
        }
        _session.Preset = _layoutBox.SelectedIndex switch
        {
            1 => MonitorLayoutPreset.Fullscreen,
            2 => MonitorLayoutPreset.BottomStrip,
            3 => MonitorLayoutPreset.RightColumn,
            4 => MonitorLayoutPreset.Overlay,
            _ => MonitorLayoutPreset.Quad,
        };
        Diag.Log($"用户切布局 → {_session.Preset}（下拉索引 {_layoutBox.SelectedIndex}）");
        UpdateLayoutRowVisibility();
        foreach (ComboBox box in _quadBoxes)
        {
            if (box.SelectedIndex < 0 && box.Items.Count > 0)
            {
                box.SelectedIndex = 0;
                Diag.Log("格下拉本来没有选中项，已兜回第 0 项（避免显示成空白）");
            }
        }
    }

    private void UpdateLayoutRowVisibility()
    {
        // 全屏看"内容"下拉；四分割看 4 个格下拉；
        // 底部条/右侧栏/叠加 用的是固定的一套示波器（波形/矢量/Parade），没有逐格可选项
        bool fullscreen = _layoutBox.SelectedIndex == 1;
        bool quad = _layoutBox.SelectedIndex == 0;
        _fullscreenPanel.Visibility = fullscreen ? Visibility.Visible : Visibility.Collapsed;
        _quadPanel.Visibility = quad ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnFullscreenContentChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_buildingLayoutRow || !IsLoaded)
        {
            return;
        }
        (string _, PaneContent content, WaveformMode mode) = ContentChoices[_fullscreenContentBox.SelectedIndex];
        _session.FullscreenContent = content;
        _session.WaveformMode = mode;
        Diag.Log($"用户换全屏内容 → {content} / {mode}");
    }

    private void OnQuadContentChanged(int slot, ComboBox box)
    {
        if (_buildingLayoutRow || !IsLoaded)
        {
            return;
        }
        (string _, PaneContent content, WaveformMode mode) = ContentChoices[box.SelectedIndex];
        _session.SetQuadContent(slot, content);
        Diag.Log($"用户换格{slot + 1}内容 → {content}");
        // 波形模式是全局的（与 iPad 版的「波形模式」设置一致）：只要某一格选了叠加，整体就按叠加算
        _session.WaveformMode = mode;
    }

    private void OnColorMatrixChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_buildingLayoutRow || !IsLoaded)
        {
            return;
        }
        _session.ColorMatrixOverride = _matrixBox.SelectedIndex switch
        {
            1 => Vortice.MediaFoundation.VideoTransferMatrix.Bt601,
            2 => Vortice.MediaFoundation.VideoTransferMatrix.Bt709,
            _ => null,
        };
        UpdateMatrixHint();
    }

    /// <summary>
    /// 驱动声明与分辨率不符时给个提示：HD（≥720 行）却声明 BT.601 是最常见的谎报，
    /// 而 SD（≤576 行）声明 601 才是正常的。
    /// </summary>
    private void UpdateMatrixHint()
    {
        LiveStats stats = _session.Stats;
        _matrixHint.Text = string.Empty;
        if (!IsLoaded || stats.Format.Length == 0)
        {
            return;
        }

        bool declares601 = stats.Color.Contains("BT.601");
        int height = _session.VideoHeight;
        if (_matrixBox.SelectedIndex == 0 && declares601 && height >= 720)
        {
            _matrixHint.Text = $"⚠ 驱动声明 BT.601，但分辨率是 {height}p —— 建议用 709 彩条实测一下"
                             + "（dotnet run --project Windows\\tools\\mf-capture -- bars）";
        }
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        Diag.Session($"主窗口启动 v{AppVersion}");
        StartUiHeartbeat();
        _session.Start();

        // 设备列表要等渲染线程第一次枚举完才有；这里先按"没设备"建一版，随后按版本号重建
        (string? savedKey, string? savedName) = LoadDeviceSelection();
        _pendingDeviceKey = savedKey;
        _pendingDeviceName = savedName;
        RebuildSourceBox(savedKey, savedName);

        Graticule.SetFrameSize(_session.Width, _session.Height);
        Graticule.Options = new GraticuleOptions();
        CompositionTarget.Rendering += OnRendering;
        SizeChanged += (_, _) => Graticule.InvalidateVisual();
        DetailText.Text = $"链路就绪：{_session.Width}×{_session.Height} 四分割（画面 / 亮度波形 / 矢量图 / RGB Parade）";
    }

    private System.Windows.Threading.DispatcherTimer? _uiHeartbeat;
    private DateTime _lastUiTick = DateTime.UtcNow;

    /// <summary>
    /// UI 线程心跳：DispatcherTimer 本身会被"线程被堵"拖后，所以两次 tick 的间隔就能
    /// 反映 UI 线程有没有停摆 —— 卡住时能分清是"窗口没响应"还是"渲染线程停了但窗口还活"。
    /// </summary>
    private void StartUiHeartbeat()
    {
        _uiHeartbeat = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _uiHeartbeat.Tick += (_, _) =>
        {
            double gap = (DateTime.UtcNow - _lastUiTick).TotalMilliseconds;
            _lastUiTick = DateTime.UtcNow;
            if (gap > 1500)
            {
                Diag.Log($"⚠ UI 线程停摆：两次 tick 间隔 {gap:0} ms");
            }
            else if (Environment.TickCount64 % 5000 < 1000)
            {
                Diag.Log("UI 心跳正常");
            }
            Diag.Flush();
        };
        _uiHeartbeat.Start();
    }

    private string? _pendingDeviceKey;
    private string? _pendingDeviceName;

    private void OnClosed(object? sender, EventArgs e)
    {
        CompositionTarget.Rendering -= OnRendering;
        _session.Dispose();
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!_session.TryCopyLatestFrame(_frameBuffer))
        {
            return;
        }

        if (_bitmap is null)
        {
            _bitmap = new WriteableBitmap(_session.Width, _session.Height, 96, 96, PixelFormats.Bgra32, null);
            Preview.Source = _bitmap;
        }

        _bitmap.WritePixels(new Int32Rect(0, 0, _session.Width, _session.Height),
                            _frameBuffer, _session.Width * 4, 0);

        // 状态栏别每帧都刷（文字布局很贵），5 Hz 足够
        long now = Environment.TickCount64;
        if (now - _lastStatsTicks >= 200)
        {
            _lastStatsTicks = now;
            LiveStats stats = _session.Stats;
            StatsText.Text = $"采集 {stats.CaptureFps:0.0} fps　显示 {stats.DisplayFps:0.0} fps　"
                           + $"一帧 {stats.TotalMs:0.0} ms（示波器 {stats.ScopeMs:0.0} / 合成 {stats.CompositeMs:0.0} / 回读 {stats.ReadbackMs:0.0}）";
            DetailText.Text = $"{stats.Source}　·　{stats.Format}　·　{stats.Color}";
            MessageText.Text = stats.Message;
            UpdateMatrixHint();

            // 设备列表变了（插拔 / 刷新）→ 重建下拉并保住当前选择
            if (_session.DeviceListRevision != _lastDeviceRevision
                && RebuildSourceBox(_pendingDeviceKey ?? _session.SelectedDeviceKey, _pendingDeviceName))
            {
                _lastDeviceRevision = _session.DeviceListRevision;
                _pendingDeviceKey = null;
                _pendingDeviceName = null;
            }
            DeviceStateText.Text = _session.DeviceState;

            // 超标报警：按版本号刷新（报警文字/红框不该每帧重建）
            if (_session.AlarmRevision != _lastAlarmRevision)
            {
                _lastAlarmRevision = _session.AlarmRevision;
                IReadOnlyList<string> warnings = _session.ActiveWarnings;
                if (warnings.Count == 0)
                {
                    AlarmBar.Visibility = Visibility.Collapsed;
                    AlarmFrame.BorderThickness = new Thickness(0);
                }
                else
                {
                    AlarmBar.Visibility = Visibility.Visible;
                    AlarmText.Text = "⚠ 超标报警：" + string.Join("　·　", warnings);
                    AlarmFrame.BorderThickness = new Thickness(3);
                }
            }

            // 布局是渲染线程算出来的：按引用变化同步给刻度层（换分辨率/换源时会重建）
            Graticule.Layout = _session.Layout;
            Graticule.SetFrameSize(_session.Width, _session.Height);

            // 刻度层还要画峰值保持游标（实时 + 参考），所以把游标状态一起喂过去
            Graticule.Options = Graticule.Options with
            {
                PeakHold = _session.PeakHold,
                ReferencePeakHold = _session.ReferencePeakHold,
            };
            Graticule.InvalidateVisual();

            MeasurementText.Text = FormatMeasurement(_session, CurrentUnit());
        }
    }

    /// <summary>当前刻度单位（读数与刻度层共用）</summary>
    private ScaleUnit CurrentUnit() => UnitBox.SelectedIndex switch
    {
        1 => ScaleUnit.Millivolt,
        2 => ScaleUnit.Percent,
        _ => ScaleUnit.Ire,
    };

    /// <summary>
    /// 底部读数行：实时读数 + （抓过参考时）参考读数与差值。
    /// 文案与 iPad 版顶栏一致：峰 / 稳 / 黑 / 均 / 色度 + 参考 / Δ。
    /// </summary>
    private static string FormatMeasurement(LiveSession session, ScaleUnit unit)
    {
        SignalMeasurement m = session.Measurement;
        if (!m.HasData)
        {
            return "读数：等待第一帧…";
        }

        var text = new System.Text.StringBuilder();
        text.Append($"峰 {unit.FormatPrecise(m.PeakWhiteIre)}");
        text.Append($"　稳 {unit.FormatPrecise(m.StableWhiteIre)}");
        text.Append($"　黑 {unit.FormatPrecise(m.BlackLevelIre)}");
        text.Append($"　均 {unit.FormatPrecise(m.AverageIre)}");
        text.Append($"　色度 {m.PeakSaturationPercent:0}%");
        text.Append($"　R/G/B 峰 {m.RedPeakIre:0} / {m.GreenPeakIre:0} / {m.BluePeakIre:0}");
        text.Append($"　超白 {m.AboveWhitePercent:0.00}%　超黑 {m.BelowBlackPercent:0.00}%");

        if (session.ReferenceMeasurement is { HasData: true } reference)
        {
            double peakDelta = m.PeakWhiteIre - reference.PeakWhiteIre;
            double averageDelta = m.AverageIre - reference.AverageIre;
            text.Append("　│　参考 峰 ")
                .Append(unit.FormatPrecise(reference.PeakWhiteIre))
                .Append(" 均 ").Append(unit.FormatPrecise(reference.AverageIre));
            text.Append("　Δ 峰 ").Append(FormatDelta(peakDelta, unit))
                .Append(" 均 ").Append(FormatDelta(averageDelta, unit));
        }

        return text.ToString();
    }

    /// <summary>差值文案（带正负号；与 iPad 版同一套格式）</summary>
    private static string FormatDelta(double delta, ScaleUnit unit)
        => (delta >= 0 ? "+" : string.Empty) + unit.FormatPrecise(delta);

    private void OnGraticuleToggled(object sender, RoutedEventArgs e)
    {
        // ⚠️ XAML 里写的事件处理器会在 InitializeComponent **解析过程中**就触发
        //    （CheckBox 的 IsChecked="True" 立刻引发 Checked），那时后面的字段
        //    （Graticule 元素）还没赋值 —— 不加这道判断就是启动即 NullReferenceException。
        //    同一个坑也适用于 ComboBox 的 SelectionChanged（那边用 IsLoaded 兜住）。
        if (!IsLoaded || Graticule is null)
        {
            return;
        }
        Graticule.ShowGraticule = GraticuleBox.IsChecked == true;
        Graticule.InvalidateVisual();
    }

    private void OnUnitChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded || Graticule is null)
        {
            return;
        }
        ScaleUnit unit = CurrentUnit();
        Graticule.Options = Graticule.Options with { Unit = unit };
        Graticule.InvalidateVisual();
        MeasurementText.Text = FormatMeasurement(_session, unit);
    }

    private void OnFreezeClicked(object sender, RoutedEventArgs e)
    {
        // 抓取在渲染线程执行：抓到的就是「按下这一刻刚算完的那一帧」
        _session.RequestReferenceCapture();
        SaveHint.Text = "已抓参考层：实时轨迹照常刷新，琥珀色是参考";
    }

    private void OnClearReferenceClicked(object sender, RoutedEventArgs e)
    {
        _session.RequestReferenceClear();
        SaveHint.Text = "已清除参考层";
    }

    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_session is null)
        {
            return;
        }
        _session.ReferenceOpacity = e.NewValue;
    }

    // ------------------------------------------------------------------
    //  信号源下拉：按**实时设备列表**建，记住上次选的设备
    // ------------------------------------------------------------------

    private const string SyntheticItem = "合成测试信号（无需硬件）";
    private int _lastDeviceRevision = -1;
    private int _lastAlarmRevision = -1;
    private bool _buildingSourceBox;

    /// <summary>
    /// 用当前设备列表重建下拉。设备用**标识**（符号链接）挂在 Tag 上 ——
    /// 不用序号：插拔一次序号就整体平移，会把"选中的卡"悄悄换成另一台设备。
    /// </summary>
    private bool RebuildSourceBox(string? preferredKey, string? preferredName)
    {
        // 下拉正开着的时候不要重建：清了又加会让用户的点击落空（菜单会自己收起来/换位置），
        // 表现就是"点不动、选不了"。等它关上再重建（版本号没变，下一帧会再进来）。
        if (SourceBox.IsDropDownOpen)
        {
            return false;   // ⚠️ 没建成就不能更新"已同步到的版本号"，否则这一次重建会被永久丢掉
        }

        _buildingSourceBox = true;
        try
        {
            object? selected = null;
            SourceBox.Items.Clear();
            SourceBox.Items.Add(new ComboBoxItem { Content = SyntheticItem, Tag = null });

            foreach (CaptureDeviceInfo info in _session.AvailableDevices)
            {
                string key = DeviceWatcher.KeyOf(info);
                var item = new ComboBoxItem
                {
                    Content = $"[{info.Index}] {info.FriendlyName}",
                    Tag = key,
                };
                SourceBox.Items.Add(item);
                if (preferredKey is not null && string.Equals(key, preferredKey, StringComparison.OrdinalIgnoreCase))
                {
                    selected = item;
                }
                else if (selected is null && preferredKey is null && preferredName is not null
                         && info.FriendlyName.Contains(preferredName, StringComparison.OrdinalIgnoreCase))
                {
                    selected = item;    // 标识变了（换了 USB 口）但名字还在：按名字兜一下
                }
            }

            // 选中的设备当前不在场：把它作为"等待接入"的条目留在列表里，别让用户的选择丢掉
            if (preferredKey is not null && selected is null)
            {
                var missing = new ComboBoxItem
                {
                    Content = $"（等待接入）{preferredName ?? preferredKey}",
                    Tag = preferredKey,
                };
                SourceBox.Items.Add(missing);
                selected = missing;
            }

            SourceBox.SelectedItem = selected ?? SourceBox.Items[0];

            // 双重保险：SelectedItem 指向的对象万一不在集合里，WPF 会显示**空白**
            // （用户看到的就是"选项栏里看不到当前选项"）。用索引再确认一次。
            if (SourceBox.SelectedIndex < 0 && SourceBox.Items.Count > 0)
            {
                SourceBox.SelectedIndex = 0;
            }
        }
        finally
        {
            _buildingSourceBox = false;
        }

        // 🔴 关键一步：**程序里设的选中也要真的生效**。
        //    SelectionChanged 在 _buildingSourceBox 期间是被屏蔽的（否则重建一次就会
        //    连环触发），所以这里必须手动把选择送到会话 —— 否则会出现
        //    「下拉显示的是采集卡，画面却还是合成信号」这种自相矛盾的状态（实测踩过）。
        ApplySourceSelection();
        Diag.Log($"重建信号源下拉：{SourceBox.Items.Count} 项，选中索引 {SourceBox.SelectedIndex}");
        return true;
    }

    /// <summary>把当前下拉选择送到会话并记住（用户点的 与 程序设的 都走这里）</summary>
    private void ApplySourceSelection()
    {
        if (SourceBox.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        string? key = item.Tag as string;
        string label = item.Content?.ToString() ?? string.Empty;
        string displayName = label.StartsWith("（等待接入）", StringComparison.Ordinal)
            ? label["（等待接入）".Length..]
            : label;

        if (!ReferenceEquals(_session.SelectedDeviceKey, key)
            && !string.Equals(_session.SelectedDeviceKey, key, StringComparison.Ordinal))
        {
            _session.SelectDevice(key, displayName);
            Diag.Log($"用户/程序选设备 → {(key is null ? "合成信号" : displayName)}");
        }
        SaveDeviceSelection(key, displayName);
    }

    private void OnSourceChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _buildingSourceBox)
        {
            return;
        }
        ApplySourceSelection();
    }

    /// <summary>画面方向（自动 / 不旋转 / 顺逆 90 / 180）</summary>
    private void OnRotationChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_buildingLayoutRow || !IsLoaded)
        {
            return;
        }

        _session.PictureRotation = _rotationBox.SelectedIndex switch
        {
            1 => PictureRotation.None,
            2 => PictureRotation.Clockwise90,
            3 => PictureRotation.CounterClockwise90,
            4 => PictureRotation.Rotate180,
            _ => PictureRotation.Automatic,
        };
        Diag.Log($"画面方向 → {_session.PictureRotation}（下拉索引 {_rotationBox.SelectedIndex}）");
    }

    private void OnRefreshDevices(object sender, RoutedEventArgs e)
    {
        _session.RequestDeviceRefresh();
        DeviceStateText.Text = "正在刷新设备列表…";
    }

    /// <summary>把上次选的设备记在 %LOCALAPPDATA%\VideoScopePad\settings.txt（一行 key=value）</summary>
    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VideoScopePad", "settings.txt");

    private static void SaveDeviceSelection(string? key, string? name)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllLines(SettingsPath, new[]
            {
                "device=" + (key ?? string.Empty),
                "deviceName=" + (name ?? string.Empty),
            });
        }
        catch (Exception)
        {
            // 记不住就算了：设置文件写不了不该影响监视
        }
    }

    private static (string? Key, string? Name) LoadDeviceSelection()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return (null, null);
            }

            string? key = null;
            string? name = null;
            foreach (string line in File.ReadAllLines(SettingsPath))
            {
                if (line.StartsWith("device=", StringComparison.Ordinal))
                {
                    key = line["device=".Length..].Trim();
                }
                else if (line.StartsWith("deviceName=", StringComparison.Ordinal))
                {
                    name = line["deviceName=".Length..].Trim();
                }
            }
            return (string.IsNullOrEmpty(key) ? null : key, string.IsNullOrEmpty(name) ? null : name);
        }
        catch (Exception)
        {
            return (null, null);
        }
    }

    /// <summary>斑马纹设置（超白阈值 / 黑切割）——只影响显示通道，不碰示波器</summary>
    private void OnZebraChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        _session.ZebraEnabled = ZebraBox.IsChecked == true;
        _session.ZebraBlackEnabled = ZebraBlackBox.IsChecked == true;
        _session.ZebraThresholdIre = ZebraThresholdBox.SelectedIndex switch
        {
            0 => 70, 1 => 75, 2 => 90, 3 => 95, _ => 100,
        };
        Diag.Log($"斑马纹：{(ZebraBox.IsChecked == true ? "超白开" : "超白关")}({_session.ZebraThresholdIre:0} IRE)、"
               + $"{(ZebraBlackBox.IsChecked == true ? "黑切割开" : "黑切割关")}");
    }

    /// <summary>超标报警设置（开关 + 确认门槛）</summary>
    private void OnAlarmChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        _session.AlarmEnabled = AlarmBox.IsChecked == true;
        _session.AlarmRaiseThreshold = AlarmThresholdBox.SelectedIndex + 1;
        Diag.Log($"超标报警：{(_session.AlarmEnabled ? "开" : "关")}，确认门槛 {_session.AlarmRaiseThreshold} 次");
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            string directory = Path.Combine(AppContext.BaseDirectory, "snapshots");
            string path = Path.Combine(directory, $"vsp-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            LiveSnapshot.Save(_session, path, Graticule.Options, Graticule.ShowGraticule);
            SaveHint.Text = $"已存（含刻度）：{path}";
        }
        catch (Exception ex)
        {
            SaveHint.Text = $"存图失败：{ex.Message}";
        }
    }

    private static string? ArgumentValue(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }
        return null;
    }
}
