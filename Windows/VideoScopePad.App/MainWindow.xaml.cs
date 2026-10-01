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
using VideoScopePad.Win.Render;

namespace VideoScopePad.App;

public partial class MainWindow : Window
{
    private readonly LiveSession _session;
    private WriteableBitmap? _bitmap;
    private byte[] _frameBuffer;
    private long _lastStatsTicks;

    public MainWindow(string[] args)
    {
        InitializeComponent();

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
        CompositionTarget.Rendering += OnRendering;
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
        }
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
            string path = Path.Combine(directory,
                $"vsp-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            _session.SaveLatestFramePng(path);
            SaveHint.Text = $"已存：{path}";
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
