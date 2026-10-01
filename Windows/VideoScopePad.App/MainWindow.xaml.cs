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
using System.Windows.Media;
using System.Windows.Media.Imaging;
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

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _session.Start();
        Graticule.SetFrameSize(_session.Width, _session.Height);
        Graticule.Options = new GraticuleOptions();
        CompositionTarget.Rendering += OnRendering;
        SizeChanged += (_, _) => Graticule.InvalidateVisual();
        DetailText.Text = $"链路就绪：{_session.Width}×{_session.Height} 四分割（画面 / 亮度波形 / 矢量图 / RGB Parade）";
    }

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

    private void OnSourceChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }
        var kind = SourceBox.SelectedIndex switch
        {
            1 => LiveSourceKind.CaptureCard,
            2 => LiveSourceKind.Camera,
            _ => LiveSourceKind.Synthetic,
        };
        _session.SwitchSource(kind);
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
