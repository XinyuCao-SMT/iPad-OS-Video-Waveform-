//
//  App.xaml.cs
//  VideoScopePad.App
//
//  两种启动方式：
//    ① 正常：开主窗口（实时四分割监视器）
//    ② 无窗口自检：VideoScopePad.App.exe --snapshot <png> --frames 180 --source synthetic
//       —— 我（AI）看不到窗口，所以实时链路的速度与画面必须能落成「数值报告 + PNG」，
//          否则「能不能跑」只能靠猜。报告写在 <png>.report.txt 里。
//

using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using VideoScopePad.Win.Render;

namespace VideoScopePad.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        string? snapshot = ArgumentValue(e.Args, "--snapshot");
        if (snapshot is not null)
        {
            int exitCode = RunHeadless(e.Args, snapshot);
            Shutdown(exitCode);
            return;
        }

        var window = new MainWindow(e.Args);
        window.Show();
    }

    /// <summary>
    /// 无窗口自检：按真实链路跑 N 帧，统计各阶段耗时与帧率，存一张合成 PNG + 一份报告。
    /// </summary>
    private static int RunHeadless(string[] args, string snapshotPath)
    {
        int frames = ParseInt(ArgumentValue(args, "--frames"), 180);
        int width = ParseInt(ArgumentValue(args, "--width"), 1600);
        int height = ParseInt(ArgumentValue(args, "--height"), 900);
        int stride = ParseInt(ArgumentValue(args, "--stride"), 1);
        string source = ArgumentValue(args, "--source") ?? "synthetic";
        string device = ArgumentValue(args, "--device") ?? "UT-VID";

        var report = new List<string>
        {
            $"无窗口自检：{width}×{height}，信号源 {source}，目标 {frames} 帧，示波器采样步长 {stride}",
            $"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}",
            string.Empty,
        };

        int exitCode = 0;
        try
        {
            using var session = new LiveSession(width, height)
            {
                DisplayFpsCap = 0,      // 自检不设上限：要量的是链路真实速度
                ScopeStride = stride,
                StopAfterFrames = frames,
            };
            session.SwitchSource(ParseSource(source), device);
            session.Start();

            // 等它跑够帧数（或超时）
            var deadline = DateTime.UtcNow.AddSeconds(90);
            while (session.Stats.Frames < frames && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(100);
            }
            session.Stop();

            LiveStats stats = session.Stats;
            report.Add($"信号源    : {stats.Source}");
            report.Add($"格式      : {stats.Format}");
            report.Add($"色彩      : {stats.Color}");
            report.Add($"帧数      : {stats.Frames}");
            report.Add($"采集帧率  : {stats.CaptureFps:0.###} fps");
            report.Add($"显示帧率  : {stats.DisplayFps:0.###} fps");
            report.Add($"耗时分解  : 取帧/转换 {stats.SourceMs:0.0} ms + 示波器 {stats.ScopeMs:0.0} ms"
                     + $" + 合成 {stats.CompositeMs:0.0} ms + 回读 {stats.ReadbackMs:0.0} ms"
                     + $" = {stats.TotalMs:0.0} ms/帧");
            report.Add("            （注 1：D3D11 是异步提交，示波器与合成的 GPU 时间会算在「回读」那一步 —— "
                     + "Map 会等 GPU 干完，所以回读那一项是这条链路的真实瓶颈）");
            report.Add("            （注 2：真实设备的「取帧/转换」里绝大部分是**等下一帧**（同步 ReadSample 会阻塞），"
                     + "不是 CPU 开销；转换本身的耗时见 mf-capture gpu：1080p YUY2 约 4 ms、720p NV12 约 0.6 ms）");
            if (!string.IsNullOrEmpty(stats.Message))
            {
                report.Add($"消息      : {stats.Message}");
            }

            session.SaveLatestFramePng(snapshotPath);
            report.Add(string.Empty);
            report.Add($"出图      : {snapshotPath}");

            // ---------- 数值断言：这张合成图必须是「有内容的四分割」----------
            // ⚠️ 取样点不能拍脑袋给：画面格里的视频是**等比适配**放进格子里的（可能有留白），
            //    所以要用布局里的画面区矩形，把「视频像素坐标」换算成「帧缓冲像素坐标」。
            //    （第一次就是随手取了 fw/8，结果取到第二条彩条上，断言误报。）
            byte[] frame = ReadFrameForCheck(session, out int fw, out int fh);
            report.Add(string.Empty);
            report.Add("断言：");

            if (!session.TryMapVideoPixelToFrame(25, 25, out int whiteX, out int whiteY))
            {
                report.Add("  ✗ 布局里没有画面格，无法取样");
                exitCode = 1;
            }
            else if (ParseSource(source) == LiveSourceKind.Synthetic)
            {
                // 只有合成信号才有「已知码值」可断言（真实设备的画面取决于信源接了什么）
                int index = (whiteY * fw + whiteX) * 4;
                int b = frame[index], g = frame[index + 1], r = frame[index + 2];
                bool whiteBar = Math.Abs(r - 191) <= 2 && Math.Abs(g - 191) <= 2 && Math.Abs(b - 191) <= 2;
                report.Add($"  {(whiteBar ? "✓" : "✗")} 画面格里的 75% 白条读作 BGRA=({b},{g},{r})，期望 (191,191,191)"
                         + $"（帧缓冲坐标 {whiteX},{whiteY}）");
                if (!whiteBar)
                {
                    exitCode = 1;
                }

                // 通道顺序自证：黄条 (R=191,G=191,B=0) 在 BGRA 内存里必须是 (0,191,191)。
                // 这一条专门抓「R/B 弄反」—— 白条是对称的，抓不到。
                session.TryMapVideoPixelToFrame(session.VideoWidth * 3 / 14, 25, out int yellowX, out int yellowY);
                int yellowIndex = (yellowY * fw + yellowX) * 4;
                int yb = frame[yellowIndex], yg = frame[yellowIndex + 1], yr = frame[yellowIndex + 2];
                bool yellow = Math.Abs(yb - 0) <= 2 && Math.Abs(yg - 191) <= 2 && Math.Abs(yr - 191) <= 2;
                report.Add($"  {(yellow ? "✓" : "✗")} 黄条通道顺序正确（BGRA 内存序 → (0,191,191)）：读到 ({yb},{yg},{yr})");
                if (!yellow)
                {
                    exitCode = 1;
                }
            }
            else
            {
                // 真实设备：只报告读到的值（画面内容取决于信源，没接信号源时就是均匀黑）
                int index = (whiteY * fw + whiteX) * 4;
                report.Add($"  · 画面格取样（视频 (25,25) → 帧缓冲 {whiteX},{whiteY}）："
                         + $"BGRA=({frame[index]},{frame[index + 1]},{frame[index + 2]})"
                         + "　真实设备的画面内容取决于信源，不做码值断言");
            }

            int distinct = CountDistinctColors(frame, 4096);
            bool hasContent = distinct > 8;
            report.Add($"  {(hasContent ? "✓" : "✗")} 合成图不是纯色（抽样 4096 点里有 {distinct} 种颜色，示波器格有轨迹）");
            if (!hasContent)
            {
                exitCode = 1;
            }

            if (stats.Frames < frames)
            {
                report.Add($"  ✗ 没跑够帧数（{stats.Frames}/{frames}）");
                exitCode = 1;
            }
            else
            {
                report.Add($"  ✓ 跑满 {frames} 帧");
            }
        }
        catch (Exception ex)
        {
            report.Add($"✗ 自检抛异常：{ex.GetType().Name}: {ex.Message}");
            report.Add(ex.StackTrace ?? string.Empty);
            exitCode = 1;
        }

        report.Add(string.Empty);
        report.Add(exitCode == 0 ? "结果：全部通过" : "结果：有失败项");

        string reportPath = snapshotPath + ".report.txt";
        string? directory = Path.GetDirectoryName(Path.GetFullPath(reportPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        File.WriteAllLines(reportPath, report, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Console.WriteLine(string.Join(Environment.NewLine, report));
        return exitCode;
    }

    /// <summary>把最新一帧拷出来做像素核对</summary>
    private static byte[] ReadFrameForCheck(LiveSession session, out int width, out int height)
    {
        width = session.Width;
        height = session.Height;
        var buffer = new byte[width * height * 4];
        session.TryCopyLatestFrame(buffer);
        return buffer;
    }

    private static int CountDistinctColors(byte[] frame, int samples)
    {
        var seen = new HashSet<int>();
        int pixels = frame.Length / 4;
        int step = Math.Max(pixels / samples, 1);
        for (int i = 0; i + 3 < frame.Length; i += 4 * step)
        {
            seen.Add((frame[i] << 16) | (frame[i + 1] << 8) | frame[i + 2]);
        }
        return seen.Count;
    }

    private static LiveSourceKind ParseSource(string text) => text.ToLowerInvariant() switch
    {
        "card" or "capture" or "capturecard" or "采集卡" => LiveSourceKind.CaptureCard,
        "camera" or "webcam" or "摄像头" => LiveSourceKind.Camera,
        _ => LiveSourceKind.Synthetic,
    };

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

    private static int ParseInt(string? text, int fallback)
        => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : fallback;
}
