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
using VideoScopePad.Win.Core;
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
            // 断言直接跑在**即将写进 PNG 的那份缓冲**上（含刻度层）：
            // 验的就是用户拿到手的产物，而不是它的一半。
            GraticuleOptions graticuleOptions = new();
            byte[] frame = LiveSnapshot.RenderBgra(session, graticuleOptions, includeGraticule: true,
                                                   out int fw, out int fh);
            LiveSnapshot.WritePng(snapshotPath, frame, fw, fh);
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

            // ---------- 刻度层：纵轴映射 + 目标框与轨迹是否真的对齐 ----------
            report.Add(string.Empty);
            report.Add("刻度层断言（刻度必须与轨迹对齐，否则等于没有刻度）：");

            // ① 纵轴映射：本工程在采集入口就把 limited 展开成 full，
            //    所以 0 IRE 必须在绘图区底、100 IRE 必须在顶（若误用 videoRange，会整体偏 8%）
            ScopeLayoutResult layout = session.Layout;
            PaneLayout? waveformPane = layout.Panes.FirstOrDefault(p => p.Content == PaneContent.Waveform);
            if (waveformPane?.Plot is not { } plotUnit)
            {
                report.Add("  ✗ 布局里没有波形格，无法核对刻度");
                exitCode = 1;
            }
            else
            {
                double plotHeightPx = plotUnit.Height * fh;
                var plotPx = new Rect(plotUnit.MinX * fw, plotUnit.MinY * fh, plotUnit.Width * fw, plotUnit.Height * fh);
                double y0 = ScopeGraticule.YPositionForIre(0, plotPx, videoRange: false);
                double y100 = ScopeGraticule.YPositionForIre(100, plotPx, videoRange: false);
                bool axisOk = Math.Abs(y0 - plotPx.Bottom) < 0.5 && Math.Abs(y100 - plotPx.Y) < 0.5;
                report.Add($"  {(axisOk ? "✓" : "✗")} 纵轴：0 IRE 在绘图区底、100 IRE 在顶"
                         + $"（0 IRE y={y0:0.0} vs 底 {plotPx.Bottom:0.0}；100 IRE y={y100:0.0} vs 顶 {plotPx.Y:0.0}）");
                if (!axisOk)
                {
                    exitCode = 1;
                }

                if (ParseSource(source) == LiveSourceKind.Synthetic)
                {
                    // ② 75% 白条的轨迹必须**压在 75 IRE 那条刻度线上**。
                    //    合成信号的白条码值是 191 → 191/255 = 74.9% ≈ 75 IRE。
                    //    ⚠️ 不能拿「整幅最亮的那一行」当判据：样本最多的码值不是白条
                    //       （蓝条的亮度只有 14 却占了更大面积，实测最亮行在 483 = 码值 14）。
                    //       正确做法是直接量 75 IRE 那一行：那里必须有一大段轨迹，
                    //       而上下各偏 12 px 处应该几乎什么都没有（证明轨迹是紧贴刻度线的细线）。
                    double y75 = ScopeGraticule.YPositionForIre(75, plotPx, videoRange: false);
                    int onRow = 0;
                    for (int y = (int)y75 - 2; y <= (int)y75 + 2; y++)
                    {
                        onRow = Math.Max(onRow, CountTracePixelsInRow(frame, fw, y, (int)plotPx.X, (int)plotPx.Right));
                    }
                    int offRow = 0;
                    foreach (int delta in new[] { -12, 12 })
                    {
                        int y = (int)y75 + delta;
                        offRow = Math.Max(offRow, CountTracePixelsInRow(frame, fw, y, (int)plotPx.X, (int)plotPx.Right));
                    }
                    long histogramColumnsOfWhiteBar = 512 / 7;          // 直方图 512 列，白条占 1/7 ≈ 73 列
                    double plotPixelsPerColumn = plotPx.Width / 512.0;  // 轨迹纹理被拉到绘图区宽度
                    long expectedWhiteColumns = (long)(histogramColumnsOfWhiteBar * plotPixelsPerColumn);
                    bool rowOk = onRow >= expectedWhiteColumns * 0.5 && offRow <= onRow * 0.25;
                    report.Add($"  {(rowOk ? "✓" : "✗")} 75 IRE 刻度线上有整段白条轨迹（{onRow} 个轨迹像素，"
                             + $"按「白条 1/7 宽 → 直方图 73 列 → 绘图区约 {expectedWhiteColumns} px」估）；"
                             + $"偏 12 px 处只有 {offRow} 个");
                    if (!rowOk)
                    {
                        exitCode = 1;
                    }
                }

                // ③ 矢量图：6 个 75% 目标框中心附近必须真有轨迹（合成信号就是 75% 彩条）
                PaneLayout? vectorPane = layout.Panes.FirstOrDefault(p => p.Content == PaneContent.Vectorscope);
                if (vectorPane?.Plot is { } vectorPlotUnit)
                {
                    var vectorPlot = new Rect(vectorPlotUnit.MinX * fw, vectorPlotUnit.MinY * fh,
                                              vectorPlotUnit.Width * fw, vectorPlotUnit.Height * fh);
                    var center = new Point(vectorPlot.X + vectorPlot.Width / 2, vectorPlot.Y + vectorPlot.Height / 2);
                    double radius = Math.Min(vectorPlot.Width, vectorPlot.Height) / 2;

                    int hits = 0;
                    var misses = new List<string>();
                    foreach ((string name, double cb, double cr) in ScopeGraticule.ColorTargets75)
                    {
                        int x = (int)(center.X + cb / 0.5 * radius);
                        int y = (int)(center.Y - cr / 0.5 * radius);
                        // ⚠️ 必须按「轨迹像素」判据找（绿明显大于红），**不能只看绿通道**：
                        //    目标框旁边的白色标签（灰）绿通道高达 234，比轨迹还亮 ——
                        //    只看绿通道会把标签当成轨迹，得出「所有目标都偏 15 px」的假结论。
                        //    同时判据要**分辨率无关**：720p 时轨迹整体更暗，
                        //    写死绝对阈值会把暗的那两条判成「没有」（实测 G / Yl 就这么误报过），
                        //    所以用「窗口内 g−r 的峰值」——灰线/文字的 g−r 恒为 0，只有轨迹会把它抬起来。
                        (int distance, int contrast) = TracePeakOffset(frame, fw, fh, x, y, 18);
                        if (distance is >= 0 and <= 6 && contrast >= 20)
                        {
                            hits++;
                        }
                        else
                        {
                            misses.Add($"{name}(最近轨迹 {distance} px，色度对比 {contrast})");
                        }
                    }

                    // 诊断：把矢量图里真正的亮点位置聚类打出来（看清轨迹到底落在哪）
                    report.Add($"    矢量图绘图区 {vectorPlot.X:0},{vectorPlot.Y:0} {vectorPlot.Width:0}×{vectorPlot.Height:0}"
                             + $"　中心 ({center.X:0},{center.Y:0})　半径 {radius:0}");
                    foreach ((string name, double cb, double cr) in ScopeGraticule.ColorTargets75)
                    {
                        report.Add($"      目标 {name,-3} cb={cb:+0.000;-0.000} cr={cr:+0.000;-0.000}"
                                 + $" → ({center.X + cb / 0.5 * radius:0},{center.Y - cr / 0.5 * radius:0})");
                    }
                    foreach (string line in DescribeTraceClusters(frame, fw, fh, vectorPlot))
                    {
                        report.Add("    " + line);
                    }
                    bool targetsOk = hits == ScopeGraticule.ColorTargets75.Length;
                    report.Add($"  {(targetsOk ? "✓" : "✗")} 矢量图 6 个 75% 目标框中心都有轨迹：命中 {hits}/6"
                             + (misses.Count > 0 ? $"（未命中：{string.Join("、", misses)}）" : string.Empty));
                    if (!targetsOk)
                    {
                        exitCode = 1;
                    }
                }
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

    /// <summary>
    /// 「轨迹像素」判据：示波器轨迹是**青色**加法混合（R=0.36 G=1.0 B=0.55），
    /// 而刻度线是灰色的（R=G=B）—— 用「绿明显大于红」就能把刻度与文字排除掉，
    /// 这样刻度层画进快照之后依然能干净地量到轨迹。
    /// ⚠️ 门槛不能定高：轨迹亮度是按样本数归一化出来的，细线又常常落在半像素上，
    ///    实测同一张图里各条的峰值绿在 100…240 之间浮动（定 110 会把暗的那几条判成"没有"）。
    /// </summary>
    private static bool IsTracePixel(byte[] frame, int index)
    {
        int b = frame[index], g = frame[index + 1], r = frame[index + 2];   // BGRA 内存序
        return g >= 70 && g - r >= 25;
    }

    private static int CountTracePixelsInRow(byte[] frame, int width, int y, int x0, int x1)
    {
        int count = 0;
        for (int x = Math.Max(x0, 0); x < Math.Min(x1, width); x++)
        {
            if (IsTracePixel(frame, (y * width + x) * 4))
            {
                count++;
            }
        }
        return count;
    }

    /// <summary>
    /// 在窗口里找「最像轨迹」的像素：取 g−r 最大的那个（轨迹是青绿加法混合，g 明显大于 r；
    /// 灰刻度线与白文字的 g−r 恒为 0），返回它到窗口中心的距离与对比度。
    /// 用相对量而不是绝对亮度，才能在不同分辨率下都站得住。
    /// </summary>
    private static (int Distance, int Contrast) TracePeakOffset(byte[] frame, int width, int height, int x, int y, int radius)
    {
        int bestContrast = int.MinValue;
        int bestX = 0;
        int bestY = 0;
        for (int dy = -radius; dy <= radius; dy++)
        {
            int py = y + dy;
            if (py < 0 || py >= height)
            {
                continue;
            }
            for (int dx = -radius; dx <= radius; dx++)
            {
                int px = x + dx;
                if (px < 0 || px >= width)
                {
                    continue;
                }
                int index = (py * width + px) * 4;
                int contrast = frame[index + 1] - frame[index + 2];   // G − R
                if (contrast > bestContrast)
                {
                    bestContrast = contrast;
                    bestX = dx;
                    bestY = dy;
                }
            }
        }

        if (bestContrast == int.MinValue)
        {
            return (-1, 0);
        }
        return (Math.Max(Math.Abs(bestX), Math.Abs(bestY)), bestContrast);
    }

    /// <summary>
    /// 到最近「轨迹像素」的切比雪夫距离（搜索半径内没有就返回 -1）。
    /// 用于核对刻度与轨迹是否对得上：距离 ≤ 几像素才算对齐。
    /// </summary>
    private static int NearestTraceDistance(byte[] frame, int width, int height, int x, int y, int radius)
    {
        for (int r = 0; r <= radius; r++)
        {
            for (int dy = -r; dy <= r; dy++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
                    {
                        continue;   // 只看这一圈，保证返回的是最近距离
                    }
                    int px = x + dx;
                    int py = y + dy;
                    if (px < 0 || px >= width || py < 0 || py >= height)
                    {
                        continue;
                    }
                    if (IsTracePixel(frame, (py * width + px) * 4))
                    {
                        return r;
                    }
                }
            }
        }
        return -1;
    }

    /// <summary>窗口内最亮的绿通道值（诊断用：区分「轨迹不在那儿」与「判据太严」）</summary>
    private static int BrightestGreenNear(byte[] frame, int width, int height, int x, int y, int radius)
        => BrightestGreenOffset(frame, width, height, x, y, radius).Green;

    /// <summary>窗口内最亮的绿通道值 + 它相对窗口中心的位置（用来量刻度与轨迹的偏移）</summary>
    private static (int Green, int OffsetX, int OffsetY) BrightestGreenOffset(
        byte[] frame, int width, int height, int x, int y, int radius)
    {
        int best = -1;
        int bestX = 0;
        int bestY = 0;
        for (int dy = -radius; dy <= radius; dy++)
        {
            int py = y + dy;
            if (py < 0 || py >= height)
            {
                continue;
            }
            for (int dx = -radius; dx <= radius; dx++)
            {
                int px = x + dx;
                if (px < 0 || px >= width)
                {
                    continue;
                }
                int green = frame[(py * width + px) * 4 + 1];
                if (green > best)
                {
                    best = green;
                    bestX = dx;
                    bestY = dy;
                }
            }
        }
        return (Math.Max(best, 0), bestX, bestY);
    }

    /// <summary>
    /// 诊断：把矢量图绘图区里「明显是轨迹」的像素按 8×8 格子聚类，打印每簇的中心与亮度，
    /// 并对照 6 个 75% 目标框的理论位置 —— 一眼就能看出刻度与轨迹是否错位、错多少。
    /// </summary>
    private static IReadOnlyList<string> DescribeTraceClusters(byte[] frame, int width, int height, Rect plot)
    {
        const int cell = 8;
        var buckets = new Dictionary<(int, int), (int Count, int MaxGreen, long SumX, long SumY)>();
        for (int y = (int)plot.Y; y < (int)plot.Bottom && y < height; y++)
        {
            for (int x = (int)plot.X; x < (int)plot.Right && x < width; x++)
            {
                int index = (y * width + x) * 4;
                if (!IsTracePixel(frame, index))
                {
                    continue;
                }
                var key = (x / cell, y / cell);
                buckets.TryGetValue(key, out var bucket);
                buckets[key] = (bucket.Count + 1,
                                Math.Max(bucket.MaxGreen, frame[index + 1]),
                                bucket.SumX + x,
                                bucket.SumY + y);
            }
        }

        var lines = new List<string> { $"矢量图轨迹簇（{cell}×{cell} 格，绿 > 110 才算）：{buckets.Count} 簇" };
        foreach (var pair in buckets.OrderByDescending(p => p.Value.Count).Take(8))
        {
            double cx = (double)pair.Value.SumX / pair.Value.Count;
            double cy = (double)pair.Value.SumY / pair.Value.Count;
            lines.Add($"    簇 @ ({cx:0},{cy:0})　{pair.Value.Count} 像素　最亮绿 {pair.Value.MaxGreen}");
        }
        return lines;
    }

    private static bool HasTraceNear(byte[] frame, int width, int height, int x, int y, int radius)
    {
        for (int dy = -radius; dy <= radius; dy++)
        {
            int py = y + dy;
            if (py < 0 || py >= height)
            {
                continue;
            }
            for (int dx = -radius; dx <= radius; dx++)
            {
                int px = x + dx;
                if (px < 0 || px >= width)
                {
                    continue;
                }
                if (IsTracePixel(frame, (py * width + px) * 4))
                {
                    return true;
                }
            }
        }
        return false;
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
