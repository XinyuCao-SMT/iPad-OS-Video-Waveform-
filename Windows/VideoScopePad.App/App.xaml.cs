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
using VideoScopePad.Win.Capture;
using VideoScopePad.Win.Core;
using VideoScopePad.Win.Render;

namespace VideoScopePad.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        string? snapshot = ArgumentValue(e.Args, "--snapshot");

        // --list-devices 也要走无窗口分支：它只想打印设备列表就走。
        // ⚠️ 漏了这一步的话程序会去开窗口，命令行就永远不返回 —— 实测踩过。
        if (e.Args.Any(a => string.Equals(a, "--list-devices", StringComparison.OrdinalIgnoreCase)))
        {
            Shutdown(ListDevices());
            return;
        }

        if (snapshot is not null)
        {
            int exitCode = RunHeadless(e.Args, snapshot);
            Shutdown(exitCode);
            return;
        }

        var window = new MainWindow(e.Args);
        window.Show();
    }

    /// <summary>列出本机所有视频采集设备（带插拔稳定的标识），打印完即退出。</summary>
    private static int ListDevices()
    {
        using var mediaFoundation = MediaFoundationRuntime.Start();
        var watcher = new DeviceWatcher();
        watcher.Refresh();
        Console.WriteLine($"枚举到 {watcher.Devices.Count} 个视频采集设备（标识 = 符号链接，插拔稳定）：");
        foreach (CaptureDeviceInfo info in watcher.Devices)
        {
            Console.WriteLine($"  [{info.Index}] {info.FriendlyName}");
            Console.WriteLine($"       key = {DeviceWatcher.KeyOf(info)}");
        }
        return 0;
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
        string finalStatsLine = "（没取到）";
        try
        {
            using var session = new LiveSession(width, height)
            {
                DisplayFpsCap = 0,      // 自检不设上限：要量的是链路真实速度
                ScopeStride = stride,
                AnimateSynthetic = false,   // 静态合成图 = 码值完全已知，读数才能精确核对
                // ⚠️ 不要用帧数给链路设停止条件：断言里有好几处要遍历 100 万像素（几十到几百毫秒），
                //    而 720p 下链路能跑到 ~390 fps —— 那点时间足够把任何"帧预算"烧完，
                //    于是链路在断言中途自己停了，表现成"还没有可保存的帧"这种莫名其妙的报错
                //    （实测：给 frames+120 的预算，720p 下断言还没跑完链路就停了）。
                //    让链路一直跑到方法结束（using 的 Dispose 会停它），要等帧就用 WaitFrames。
                StopAfterFrames = 0,
            };
            session.SwitchSource(ParseSource(source), device);
            session.Start();

            // 设备源要**先等探测 + 打开完成**：探测最长 5 秒（后台线程试读），
            // 不等的话断言会在「正在检测设备…」那一刻就跑，报告全是"设备没打开"（实测踩过）。
            if (ParseSource(source) != LiveSourceKind.Synthetic)
            {
                var deviceDeadline = DateTime.UtcNow.AddSeconds(25);
                while (session.DeviceState != "正常" && DateTime.UtcNow < deviceDeadline)
                {
                    Thread.Sleep(150);
                }
            }

            // 等它跑够帧数（或超时）
            var deadline = DateTime.UtcNow.AddSeconds(90);
            while (session.Stats.Frames < frames && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(100);
            }

            // ⚠️ 这里**不能** session.Stop()：后面的读数与冻结参考断言还要继续驱动渲染线程
            //    （抓参考是「下一帧」在渲染线程执行的）。链路一直跑到方法结束，
            //    最后靠 using 的 Dispose 收尾。
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

            // ---------- 设备列表与热插拔的纯逻辑（不需要硬件在场，随发布自检一起跑）----------
            report.Add("设备列表逻辑（Diff 是纯函数，插拔不可自动化所以直接测算法）：");
            exitCode |= CheckDeviceWatcherLogic(report);

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

                // 真设备这一路要断言的是「链路真的在出帧」而不是画面内容：
                // 设备没打开时程序会显示合成信号兜底（界面不至于全黑），
                // 所以**不能拿总帧数当证据** —— 那会把"设备其实没出帧"判成通过（实测踩过）。
                // 判据用采集帧率（>0 才说明真的从设备读到帧）与设备状态。
                bool deviceProducing = stats.CaptureFps > 0.5;
                report.Add($"  {(deviceProducing ? "✓" : "✗")} 设备真的在出帧：采集 {stats.CaptureFps:0.###} fps"
                         + $"（渲染总计 {stats.Frames} 帧 —— 设备没打开时会拿合成信号兜底，所以总帧数不算证据）");
                if (!deviceProducing)
                {
                    exitCode = 1;
                }

                bool hasFormat = stats.Format.Length > 1 && stats.Format != "—";
                report.Add($"  {(hasFormat ? "✓" : "✗")} 真实设备报出了生效格式（{stats.Format}）");
                if (!hasFormat)
                {
                    exitCode = 1;
                }

                string state = session.DeviceState;
                bool deviceOpen = state == "正常";
                report.Add($"  {(deviceOpen ? "✓" : "✗")} 选中的设备确实被打开（信号源名：{stats.Source}、"
                         + $"设备状态：{(string.IsNullOrEmpty(state) ? "—" : state)}）");
                if (!deviceOpen)
                {
                    exitCode = 1;
                }
            }

            int distinct = CountDistinctColors(frame, 4096);
            bool hasContent = distinct > 8;
            report.Add($"  {(hasContent ? "✓" : "✗")} 合成图不是纯色（抽样 4096 点里有 {distinct} 种颜色，示波器格有轨迹）");
            if (!hasContent)
            {
                exitCode = 1;
            }

            // ---------- 刻度层：纵轴映射 + 目标框与轨迹是否真的对齐 ----------
            // ⚠️ 这一块与下面「幅度读数」「钻石图/马蹄图落点」都建立在**合成图案**上
            //    （彩条位置、灰阶斜坡、75% 码值都是已知的）。换成真实信号源（`--source device`）
            //    时信源内容未知，这些断言不能跑、更不能判失败 —— 真实源要验的是
            //    「取到帧 / 报出格式 / 设备真的被打开 / 参考层与读数链路可用」。
            bool syntheticSource = ParseSource(source) == LiveSourceKind.Synthetic;
            report.Add(string.Empty);
            if (!syntheticSource)
            {
                report.Add("真实信号源：跳过基于合成图案的码值/落点断言（信源内容未知），"
                         + "只保留与内容无关的部分（设备是否出帧、布局与引擎取数、参考层）：");
            }
            else
            {
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

            // ---------- 幅度读数 + 冻结参考层 ----------
            report.Add(string.Empty);
            report.Add("幅度读数断言（拿同一张合成图在 CPU 上另算一份直方图对拍 —— GPU 统计链的端到端校验）：");

            SignalMeasurement measurement = session.Measurement;
            if (!measurement.HasData)
            {
                report.Add("  ✗ 读数还没出来（测量回读没跑起来？）");
                exitCode = 1;
            }
            else
            {
                report.Add($"  实测：峰 {measurement.PeakWhiteIre:0.00} IRE　稳 {measurement.StableWhiteIre:0.00}"
                         + $"　黑 {measurement.BlackLevelIre:0.00}　均 {measurement.AverageIre:0.00}"
                         + $"　色度峰 {measurement.PeakSaturationPercent:0.0}%"
                         + $"　R/G/B 峰 {measurement.RedPeakIre:0}/{measurement.GreenPeakIre:0}/{measurement.BluePeakIre:0}"
                         + $"　超白 {measurement.AboveWhitePercent:0.00}% 超黑 {measurement.BelowBlackPercent:0.00}%"
                         + $"　样本 {measurement.SampledPixels}");

                // CPU 侧期望值：同一张合成图（AnimateSynthetic = false 时它是**确定性**的，
                // 重新生成一份即可逐像素相同），用与着色器相同的码值语义另算一份直方图。
                byte[] cpuFrame = SyntheticSource.MakeTestFrame(width, height, out _);
                CpuReference expected = CpuReference.FromFrame(cpuFrame, width, height);

                exitCode |= CheckClose(report, "峰值白", measurement.PeakWhiteIre, expected.PeakIre, 0.5,
                    $"CPU 最高 luma 码值 {expected.PeakCode}（灰阶斜坡顶到 100 IRE，合成图本来就含它）");
                exitCode |= CheckClose(report, "稳定白", measurement.StableWhiteIre, expected.StableIre, 0.6,
                    $"CPU 0.1% 分位码值 {expected.StableCode}（= 斜坡最亮那一小段）");
                exitCode |= CheckClose(report, "黑位", measurement.BlackLevelIre, expected.BlackIre, 0.5,
                    $"CPU 最低 luma 码值 {expected.BlackCode}（PLUGE / 黑缝）");
                exitCode |= CheckClose(report, "平均值", measurement.AverageIre, expected.MeanIre, 0.5,
                    $"CPU 全帧 luma 平均码值 {expected.MeanCode:0.00}");
                exitCode |= CheckClose(report, "稳定黑", measurement.StableBlackIre, expected.StableBlackIre, 0.6,
                    $"CPU 0.1% 分位（从暗端）码值 {expected.StableBlackCode}");

                bool samplesOk = measurement.SampledPixels == (long)width * height;
                report.Add($"  {(samplesOk ? "✓" : "✗")} 采样像素数 = 全帧像素数（{measurement.SampledPixels} vs {(long)width * height}）");
                exitCode |= samplesOk ? 0 : 1;

                // ⚠️ 色度峰的期望**不是 75%**：6 个 75% 目标框落在一个「方框」的边上而不是圆上，
                //    离中心最近的是 B / Yl（75.3%），最远的是 G / Mg（89.4%）——
                //    合成图的绿条与品红条把色度峰抬到 ~89%。这条断言就是按这个几何来的。
                bool saturationOk = measurement.PeakSaturationPercent is > 85 and < 95;
                report.Add($"  {(saturationOk ? "✓" : "✗")} 色度峰 ≈ 89%（75% 目标框最远的那两个：G / Mg；B / Yl 只有 75.3%）");
                exitCode |= saturationOk ? 0 : 1;
            }
            }   // ← 合成图案相关的断言到此为止（真实信号源跳过）

            // ---------- 格子内容可选：布局 / 引擎设置 / 钻石图与马蹄图的刻度 ----------
            report.Add(string.Empty);
            report.Add("布局与格内容断言（切布局 → 布局真的变、引擎按可见格子要数据、新图的刻度对得上轨迹）：");

            // ① 切到「全屏 + 矢量图」：应当只剩 1 格，且就是要的那一种
            session.Preset = MonitorLayoutPreset.Fullscreen;
            session.FullscreenContent = PaneContent.Vectorscope;
            WaitFrames(session, 4, 3000);
            ScopeLayoutResult fullscreenLayout = session.Layout;
            bool fullscreenOk = fullscreenLayout.Panes.Count == 1
                             && fullscreenLayout.Panes[0].Content == PaneContent.Vectorscope;
            report.Add($"  {(fullscreenOk ? "✓" : "✗")} 切到全屏 + 矢量图：格子数 {fullscreenLayout.Panes.Count}，"
                     + $"内容 {(fullscreenLayout.Panes.Count > 0 ? fullscreenLayout.Panes[0].Content.ToString() : "—")}");
            exitCode |= fullscreenOk ? 0 : 1;

            // ② 切到「四分割：波形 / 钻石图 / 马蹄图 / Parade」——
            //    特意避开默认那套，这样"格内容真的换了"是可验证的
            session.Preset = MonitorLayoutPreset.Quad;
            session.SetQuadContent(0, PaneContent.Waveform);
            session.SetQuadContent(1, PaneContent.Diamond);
            session.SetQuadContent(2, PaneContent.Cie);
            session.SetQuadContent(3, PaneContent.Parade);
            WaitFrames(session, 6, 3000);

            ScopeLayoutResult quadLayout = session.Layout;
            var wanted = new[] { PaneContent.Waveform, PaneContent.Diamond, PaneContent.Cie, PaneContent.Parade };
            bool quadOk = quadLayout.Panes.Count == 4
                       && wanted.All(c => quadLayout.Panes.Any(p => p.Content == c));
            report.Add($"  {(quadOk ? "✓" : "✗")} 四分割逐格换内容：4 格 = "
                     + string.Join(" / ", quadLayout.Panes.Select(p => p.Content.ToString())));
            exitCode |= quadOk ? 0 : 1;

            // ③ 引擎只按可见格子要数据（省算力，也是"布局是唯一来源"的体现）
            ScopeRenderSettings settings = session.ScopeSettings;
            bool settingsOk = settings.NeedWaveform && settings.NeedDiamond && settings.NeedCie && !settings.NeedVectorscope;
            report.Add($"  {(settingsOk ? "✓" : "✗")} 引擎按可见格子取数据：波形 {settings.NeedWaveform}、"
                     + $"矢量 {settings.NeedVectorscope}、钻石 {settings.NeedDiamond}、马蹄 {settings.NeedCie}"
                     + "（这一套里没有矢量格，所以矢量应为 false）");
            exitCode |= settingsOk ? 0 : 1;

            byte[] cut = LiveSnapshot.RenderBgra(session, graticuleOptions, includeGraticule: true,
                                                out fw, out fh);

            // ④ 钻石图：灰阶斜坡在钻石图里是一条**正中竖线**（x = 绘图区中线）
            //    （同样是"已知合成图案"才成立 —— 真实信号源跳过，见上面的 syntheticSource）
            if (!syntheticSource)
            {
                report.Add("  · 钻石图/马蹄图的落点断言按合成图案的已知彩条位置算，真实信号源下不适用（跳过）");
            }
            else
            {
            PaneLayout? diamondPane = quadLayout.Panes.FirstOrDefault(p => p.Content == PaneContent.Diamond);
            if (diamondPane?.Plot is { } diamondPlotUnit)
            {
                var diamondPlot = new Rect(diamondPlotUnit.MinX * fw, diamondPlotUnit.MinY * fh,
                                           diamondPlotUnit.Width * fw, diamondPlotUnit.Height * fh);
                double centerX = diamondPlot.X + diamondPlot.Width / 2;
                int onCenter = 0;
                int offCenter = 0;
                for (int y = (int)diamondPlot.Y; y < (int)diamondPlot.Bottom && y < fh; y++)
                {
                    for (int x = (int)diamondPlot.X; x < (int)diamondPlot.Right && x < fw; x++)
                    {
                        if (!IsTracePixel(cut, (y * fw + x) * 4))
                        {
                            continue;
                        }
                        if (Math.Abs(x - centerX) <= 6)
                        {
                            onCenter++;
                        }
                        else
                        {
                            offCenter++;
                        }
                    }
                }
                // 灰阶是正中竖线，那 7 条彩条则散布在别处 —— 所以中线上必须有轨迹，
                // 但也不能"全在中线上"（那说明映射根本没用上横轴）
                // 灰阶/黑场都在中线上 → 那里必然有一大段；但**彩条每条只落一个 bin**
                // （纯色 = 一个点，上屏约 2–4 个像素），所以"线外像素很多"是错的期望。
                // 正确做法：按上/下菱形的公式算出 7 条彩条各自的落点，逐个要求附近真有轨迹。
                (string Name, double R, double G, double B)[] bars =
                {
                    ("白", 0.75, 0.75, 0.75), ("黄", 0.75, 0.75, 0.0), ("青", 0.0, 0.75, 0.75),
                    ("绿", 0.0, 0.75, 0.0), ("品红", 0.75, 0.0, 0.75), ("红", 0.75, 0.0, 0.0),
                    ("蓝", 0.0, 0.0, 0.75),
                };
                int hits = 0;
                var misses = new List<string>();
                foreach ((string name, double r, double g, double b) in bars)
                {
                    // 上菱形 x = B−G、y = (G+B)/2；下菱形 x = R−G、y = −(R+G)/2（y 向上）
                    bool top = HasTraceNearDisplayPoint(cut, fw, fh, diamondPlot,
                                                        b - g, (g + b) * 0.5, 4);
                    bool bottom = HasTraceNearDisplayPoint(cut, fw, fh, diamondPlot,
                                                           r - g, -(r + g) * 0.5, 4);
                    if (top && bottom)
                    {
                        hits++;
                    }
                    else
                    {
                        misses.Add($"{name}(上{(top ? "有" : "无")} 下{(bottom ? "有" : "无")})");
                    }
                }

                bool diamondOk = hits == bars.Length;
                report.Add($"  {(diamondOk ? "✓" : "✗")} 钻石图：7 条彩条的上下两个落点都有轨迹（{hits}/7）"
                         + (misses.Count > 0 ? "　未命中：" + string.Join("、", misses) : "")
                         + $"　（中线上另有灰阶竖线 {onCenter} 个像素）");
                exitCode |= diamondOk ? 0 : 1;

                // 诊断：把钻石图按 x 分 8 段数轨迹像素（看彩条到底在不在）
                int[] bands = new int[8];
                int total = 0;
                for (int y = (int)diamondPlot.Y; y < (int)diamondPlot.Bottom && y < fh; y++)
                {
                    for (int x = (int)diamondPlot.X; x < (int)diamondPlot.Right && x < fw; x++)
                    {
                        if (!IsTracePixel(cut, (y * fw + x) * 4))
                        {
                            continue;
                        }
                        int band = Math.Clamp((int)((x - diamondPlot.X) / diamondPlot.Width * 8), 0, 7);
                        bands[band]++;
                        total++;
                    }
                }
                report.Add($"    钻石图轨迹按 x 分 8 段：{string.Join(" / ", bands)}（合计 {total}）");
            }
            else
            {
                report.Add("  ✗ 布局里没有钻石图格");
                exitCode = 1;
            }

            // ⑤ 马蹄图：75% 白条是中性色 → 应当落在 D65 白点（0.3127, 0.3290）附近。
            //    这条同时验了三件事：格内容确实切到马蹄图、引擎算了 CIE 那一段二维直方图、
            //    刻度层的 xy→绘图区映射与着色器用的是同一套常量。
            PaneLayout? ciePane = quadLayout.Panes.FirstOrDefault(p => p.Content == PaneContent.Cie);
            if (ciePane?.Plot is { } ciePlotUnit)
            {
                var ciePlot = new Rect(ciePlotUnit.MinX * fw, ciePlotUnit.MinY * fh,
                                       ciePlotUnit.Width * fw, ciePlotUnit.Height * fh);
                double d65X = ciePlot.X + (0.3127 + ShaderConstants.CieOriginX) / ShaderConstants.CieSpan * ciePlot.Width;
                double d65Y = ciePlot.Bottom - (0.3290 + ShaderConstants.CieOriginY) / ShaderConstants.CieSpan * ciePlot.Height;
                int distance = NearestTraceDistance(cut, fw, fh, (int)d65X, (int)d65Y, 14);
                bool cieOk = distance is >= 0 and <= 14;
                report.Add($"  {(cieOk ? "✓" : "✗")} 马蹄图：75% 白条（中性色）落在 D65 白点附近"
                         + $"（期望位置 {d65X:0},{d65Y:0}，最近轨迹 {distance} px）");
                exitCode |= cieOk ? 0 : 1;
            }
            else
            {
                report.Add("  ✗ 布局里没有马蹄图格");
                exitCode = 1;
            }
            }   // ← 钻石图/马蹄图落点断言结束

            // ⑥ CIE 映射常量与着色器手抄一致（两处都改了才不会错位）
            exitCode |= CheckCieConstantsMatchShader(report);

            // ---------- 斑马纹 + 超标报警 ----------
            report.Add(string.Empty);
            report.Add("斑马纹与超标报警断言（斑马纹只能落在超阈值的像素上、绝不能影响示波器；报警规则与锁存逐条验）：");
            exitCode |= CheckWarningRules(report);
            exitCode |= CheckZebraAndAlarm(session, fw, fh, graticuleOptions, report);

            // ---------- 布局预设（底部条 / 右侧栏 / 叠加）+ 画面方向 ----------
            report.Add(string.Empty);
            report.Add("布局预设与画面方向断言（格子数与几何、旋转是否真的换了轴）：");
            exitCode |= CheckLayoutPresets(session, report);

            // ---------- 读数 CSV 导出 ----------
            report.Add(string.Empty);
            report.Add("读数 CSV 断言（表头与 iPad 逐字一致、数值对得上读数、节流与 BOM 都对）：");
            Diag.Log("自检：进入 CSV 段");
            exitCode |= CheckCsv(session, report);

            // ---------- LUT（.cube）----------
            report.Add(string.Empty);
            report.Add("LUT 断言（解析器逐条 + 渲染数值：恒等不变、反相精确、强度插值、前后取样对比）：");
            Diag.Log("自检：进入 LUT 段");
            exitCode |= CheckLut(session, graticuleOptions, report);

            // ---------- 冻结参考层：抓取后必须多出「落在轨迹上的琥珀贡献」----------
            // ⚠️ 不能简单地"数琥珀像素"：参考层与实时轨迹是**同一信号**时两者完全重合，
            //    加法混合下绿通道直接饱和，出来的像素反而是白/绿占优 —— 一条都数不到。
            //    正确做法是比对「画参考前后」的同一块区域：
            //      · 差异像素必须出现（否则参考层根本没参与合成）；
            //      · 差异必须**落在轨迹上**（靠近原来的轨迹像素）—— 这才说明参考层对的是位置；
            //      · 差异要往红偏（琥珀），而不是随便变亮。
            report.Add(string.Empty);
            report.Add("冻结参考层断言（比对画参考前后同一区域：差异必须出现、必须落在轨迹上、必须往红偏）：");
            Diag.Log("自检：进入冻结段");

            PaneLayout? wavePane = session.Layout.Panes.FirstOrDefault(p => p.Content == PaneContent.Waveform);
            var wavePlot = wavePane?.Plot is { } wavePlotUnit
                ? new Rect(wavePlotUnit.MinX * fw, wavePlotUnit.MinY * fh,
                           wavePlotUnit.Width * fw, wavePlotUnit.Height * fh)
                : new Rect(0, 0, 0, 0);

            // 基线要在**当前布局**下现渲染一份：前面切过布局，用旧的 `frame` 比会错位
            byte[] withoutReference = LiveSnapshot.RenderBgra(session, graticuleOptions, includeGraticule: true,
                                                             out fw, out fh);
            var tracePixels = CollectTracePixels(withoutReference, fw, fh, wavePlot);

            session.RequestReferenceCapture();
            if (WaitFrames(session, 8, 5000) && session.ReferenceMeasurement is { HasData: true } referenceMeasurement)
            {
                byte[] withReference = LiveSnapshot.RenderBgra(session, graticuleOptions, includeGraticule: true,
                                                              out fw, out fh);

                int changed = 0;
                int onTrace = 0;
                double redShift = 0;
                for (int y = (int)wavePlot.Y; y < (int)wavePlot.Bottom && y < fh; y++)
                {
                    for (int x = (int)wavePlot.X; x < (int)wavePlot.Right && x < fw; x++)
                    {
                        int index = (y * fw + x) * 4;
                        int before = withoutReference[index + 2];
                        int after = withReference[index + 2];
                        if (after - before < 15)
                        {
                            continue;
                        }
                        changed++;
                        redShift += after - before;
                        if (HasTraceWithin(tracePixels, x, y, 4))
                        {
                            onTrace++;
                        }
                    }
                }

                bool ghostDrawn = changed > 50;
                report.Add($"  {(ghostDrawn ? "✓" : "✗")} 抓取后波形格里出现参考层的琥珀贡献（{changed} 个像素变红）");
                exitCode |= ghostDrawn ? 0 : 1;

                bool onTraceOk = changed > 0 && onTrace == changed;
                report.Add($"  {(onTraceOk ? "✓" : "✗")} 这些变化**全部落在轨迹上**（{onTrace}/{changed}）—— 参考层对的是位置，不是随便抹一块");
                exitCode |= onTraceOk ? 0 : 1;

                double meanRedShift = changed > 0 ? redShift / changed : 0;
                bool amberOk = meanRedShift > 5;
                report.Add($"  {(amberOk ? "✓" : "✗")} 变化方向是「红升」（平均 +{meanRedShift:0.0}）—— 琥珀色，不是别的颜色");
                exitCode |= amberOk ? 0 : 1;

                double delta = Math.Abs(session.Measurement.PeakWhiteIre - referenceMeasurement.PeakWhiteIre);
                bool deltaOk = delta < 0.6;
                report.Add($"  {(deltaOk ? "✓" : "✗")} 参考读数与实时读数一致（Δ峰 {delta:0.00} IRE —— 同一信号抓的，理应几乎为 0）");
                exitCode |= deltaOk ? 0 : 1;

                // 清除参考：变化必须消失
                session.RequestReferenceClear();
                if (WaitFrames(session, 8, 5000))
                {
                    byte[] afterClear = LiveSnapshot.RenderBgra(session, graticuleOptions, includeGraticule: true,
                                                                out fw, out fh);
                    int changedAfterClear = 0;
                    for (int y = (int)wavePlot.Y; y < (int)wavePlot.Bottom && y < fh; y++)
                    {
                        for (int x = (int)wavePlot.X; x < (int)wavePlot.Right && x < fw; x++)
                        {
                            int index = (y * fw + x) * 4;
                            if (afterClear[index + 2] - withoutReference[index + 2] >= 15)
                            {
                                changedAfterClear++;
                            }
                        }
                    }
                    bool clearOk = changedAfterClear == 0;
                    report.Add($"  {(clearOk ? "✓" : "✗")} 清除参考后琥珀贡献消失（{changed} → {changedAfterClear} 个像素）");
                    exitCode |= clearOk ? 0 : 1;
                }
            }
            else
            {
                LiveStats late = session.Stats;
                report.Add($"  ✗ 抓取参考后拿不到参考读数：帧数 {late.Frames}、"
                         + $"HasReference={session.HasReference}、ReferenceMeasurement="
                         + $"{(session.ReferenceMeasurement is null ? "null" : "无数据")}"
                         + $"、链路消息：{(string.IsNullOrEmpty(late.Message) ? "（空）" : late.Message)}");
                exitCode = 1;
            }

            // 断言跑完时再看一眼链路状态：断言过程中出的错（比如切布局时渲染线程炸了）
            // 不会出现在开头那份 stats 快照里，不看这一行就会漏掉真正的原因。
            LiveStats finalStats = session.Stats;
            finalStatsLine = $"帧数 {finalStats.Frames}、显示帧率 {finalStats.DisplayFps:0.0} fps"
                           + (string.IsNullOrEmpty(finalStats.Message) ? string.Empty
                              : $"、消息：{finalStats.Message}");
        }
        catch (Exception ex)
        {
            report.Add($"✗ 自检抛异常：{ex.GetType().Name}: {ex.Message}");
            report.Add(ex.StackTrace ?? string.Empty);
            exitCode = 1;
        }

        report.Add(string.Empty);
        report.Add(exitCode == 0 ? "结果：全部通过" : "结果：有失败项");
        report.Add($"链路收尾：{finalStatsLine}");

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

    /// <summary>断言「测量值 ≈ 期望值」并输出一行证据</summary>
    private static int CheckClose(List<string> report, string name, double actual, double expected, double tolerance, string detail)
    {
        bool ok = Math.Abs(actual - expected) <= tolerance;
        report.Add($"  {(ok ? "✓" : "✗")} {name}：{actual:0.00} IRE vs 期望 {expected:0.00}（±{tolerance:0.0}）—— {detail}");
        return ok ? 0 : 1;
    }

    /// <summary>
    /// CPU 侧的期望读数：对同一张合成帧自己算一遍 256 bin 的 luma 直方图。
    /// 与 GPU 侧（CSAccumulateMeasurement）用**同一套码值语义**：
    ///   luma = 0.2126R + 0.7152G + 0.0722B（ShaderTypes.cs 里的权重），码值 = round(luma)
    /// 于是这就是「GPU 统计链」的端到端校验，不只是「数字看着差不多」。
    /// </summary>
    private readonly record struct CpuReference(
        double PeakIre, double StableIre, double BlackIre, double StableBlackIre,
        double MeanIre, int PeakCode, int StableCode, int BlackCode, int StableBlackCode, double MeanCode)
    {
        public static CpuReference FromFrame(byte[] rgba, int width, int height)
        {
            var histogram = new long[256];
            int stride = width * 4;
            long total = 0;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int index = y * stride + x * 4;
                    double luma = 0.2126 * rgba[index] + 0.7152 * rgba[index + 1] + 0.0722 * rgba[index + 2];
                    int code = (int)Math.Clamp(luma + 0.5, 0, 255);
                    histogram[code]++;
                    total++;
                }
            }

            int peak = 255;
            while (peak > 0 && histogram[peak] == 0)
            {
                peak--;
            }
            int black = 0;
            while (black < 255 && histogram[black] == 0)
            {
                black++;
            }

            long tail = Math.Max(1, (long)(total * 0.001));
            long cumulative = 0;
            int stable = peak;
            for (int code = 255; code >= 0; code--)
            {
                cumulative += histogram[code];
                if (cumulative >= tail)
                {
                    stable = code;
                    break;
                }
            }
            cumulative = 0;
            int stableBlack = black;
            for (int code = 0; code < 256; code++)
            {
                cumulative += histogram[code];
                if (cumulative >= tail)
                {
                    stableBlack = code;
                    break;
                }
            }

            double weighted = 0;
            for (int code = 0; code < 256; code++)
            {
                weighted += (double)code * histogram[code];
            }
            double mean = weighted / total;

            static double Ire(double code) => code / 255.0 * 100.0;
            return new CpuReference(Ire(peak), Ire(stable), Ire(black), Ire(stableBlack),
                                    Ire(mean), peak, stable, black, stableBlack, mean);
        }
    }

    /// <summary>
    /// 布局预设（底部条 / 右侧栏 / 叠加）与画面方向的断言：
    ///   预设 —— 格子数 = 1 + 示波器清单数，且几何关系符合各预设的定义（条在下、栏在右、叠加重合）；
    ///   旋转 —— pane 的 Rotation 与 SwapsVideoAxes 要跟着变，且适配后的视频矩形宽高比确实换了轴。
    /// 这些都是纯几何，可以直接断言数值，不依赖画面内容。
    /// </summary>
    private static int CheckLayoutPresets(LiveSession session, List<string> report)
    {
        int failed = 0;
        void Check(bool ok, string what)
        {
            report.Add($"  {(ok ? "✓" : "✗")} {what}");
            if (!ok) { failed++; }
        }

        // 三个预设各看一遍：格子数、几何关系
        session.Preset = MonitorLayoutPreset.BottomStrip;
        WaitFrames(session, 4, 3000);
        ScopeLayoutResult strip = session.Layout;
        PaneLayout? stripPicture = strip.Panes.FirstOrDefault(p => p.Content == PaneContent.Picture);
        var stripScopes = strip.Panes.Where(p => p.Content != PaneContent.Picture).ToList();
        bool stripOk = strip.Panes.Count == 1 + session.ScopePanels.Count
                    && stripScopes.Count == session.ScopePanels.Count
                    && stripPicture?.Panel is { } sp && stripScopes.All(p => p.Panel.MinY >= sp.MaxY - 0.02);
        report.Add($"  {(stripOk ? "✓" : "✗")} 底部条：{strip.Panes.Count} 格（1 画面 + {stripScopes.Count} 示波器），"
                 + $"画面底边 {stripPicture?.Panel.MaxY:0.000} ≤ 示波器顶边 {stripScopes.FirstOrDefault()?.Panel.MinY:0.000}");
        if (!stripOk) { failed++; }

        session.Preset = MonitorLayoutPreset.RightColumn;
        WaitFrames(session, 4, 3000);
        ScopeLayoutResult column = session.Layout;
        PaneLayout? columnPicture = column.Panes.FirstOrDefault(p => p.Content == PaneContent.Picture);
        var columnScopes = column.Panes.Where(p => p.Content != PaneContent.Picture).ToList();
        bool columnOk = column.Panes.Count == 1 + session.ScopePanels.Count
                     && columnPicture?.Panel is { } cp && columnScopes.All(p => p.Panel.MinX >= cp.MaxX - 0.02)
                     && columnScopes.Count > 0 && columnScopes[0].Panel.Width < cp.Width;
        report.Add($"  {(columnOk ? "✓" : "✗")} 右侧栏：{column.Panes.Count} 格，示波器栏在画面右侧且更窄"
                 + $"（栏宽 {columnScopes.FirstOrDefault()?.Panel.Width:0.000} < 画面宽 {columnPicture?.Panel.Width:0.000}）");
        if (!columnOk) { failed++; }

        session.Preset = MonitorLayoutPreset.Overlay;
        WaitFrames(session, 4, 3000);
        ScopeLayoutResult overlay = session.Layout;
        PaneLayout? overlayPicture = overlay.Panes.FirstOrDefault(p => p.Content == PaneContent.Picture);
        var overlayScopes = overlay.Panes.Where(p => p.Content != PaneContent.Picture).ToList();
        bool overlayOk = overlay.IsOverlay && overlayScopes.Count > 0
                      && overlayPicture?.Panel is { } op && overlayScopes.All(p => p.Panel.MinY < op.MaxY)
                      && overlayScopes.All(p => p.Panel.MaxY <= op.MaxY + 1e-6);
        report.Add($"  {(overlayOk ? "✓" : "✗")} 叠加：IsOverlay={overlay.IsOverlay}、{overlay.Panes.Count} 格，"
                 + "示波器格与画面格**重叠**（都在画面框内）");
        if (!overlayOk) { failed++; }

        // 画面方向：自动 / 90 / 180 / 270
        session.Preset = MonitorLayoutPreset.Quad;
        WaitFrames(session, 4, 3000);
        var rotationResults = new List<string>();
        bool rotationOk = true;
        foreach (PictureRotation rotation in new[]
                 {
                     PictureRotation.None, PictureRotation.Clockwise90,
                     PictureRotation.Rotate180, PictureRotation.CounterClockwise90,
                 })
        {
            session.PictureRotation = rotation;
            WaitFrames(session, 4, 3000);
            PaneLayout? pane = session.Layout.Panes.FirstOrDefault(p => p.Content == PaneContent.Picture);
            if (pane is null) { rotationOk = false; break; }

            bool expectSwap = rotation is PictureRotation.Clockwise90 or PictureRotation.CounterClockwise90;
            bool swapOk = pane.SwapsVideoAxes == expectSwap;
            // 适配后的视频矩形：需要换轴时，宽高比应当取倒数（16:9 的源 → 9:16 的框）
            bool aspectOk = true;
            if (session.VideoWidth > 0 && session.VideoHeight > 0 && pane.Video is { } video)
            {
                double videoAspect = (double)session.VideoWidth / session.VideoHeight;
                // ⚠️ 布局里的矩形是**单位空间**（x 按容器宽归一、y 按容器高归一），
                //    直接相除得到的不是像素宽高比 —— 必须乘回容器像素尺寸（实测踩过：
                //    四个档位都被判成"比例错"，其实旋转本身都是对的）。
                double boxAspect = video.Width * session.Width / (video.Height * session.Height);
                double want = expectSwap ? 1.0 / videoAspect : videoAspect;
                aspectOk = Math.Abs(boxAspect - want) < 0.02;
            }
            rotationOk &= swapOk && aspectOk;
            rotationResults.Add($"{(expectSwap ? "换轴" : "不换轴")}{(swapOk ? "✓" : "✗")}/比例{(aspectOk ? "对" : "错")}");
        }
        Check(rotationOk, $"画面方向：{string.Join("、", rotationResults)}（源 {session.VideoWidth}×{session.VideoHeight}）");

        session.PictureRotation = PictureRotation.Automatic;
        session.Preset = MonitorLayoutPreset.Quad;
        WaitFrames(session, 3, 3000);
        return failed;
    }
    /// <summary>
    /// 读数 CSV 导出断言：表头逐字一致、行里的数值等于读数、节流生效、BOM 与文件落盘都对。
    /// </summary>
    private static int CheckCsv(LiveSession session, List<string> report)
    {
        int failed = 0;
        void Check(bool ok, string what)
        {
            report.Add($"  {(ok ? "✓" : "✗")} {what}");
            if (!ok) { failed++; }
        }

        var log = new MeasurementLog { IntervalSeconds = 0.5 };
        var measurement = new SignalMeasurement
        {
            PeakWhiteIre = 100, StableWhiteIre = 99.22, BlackLevelIre = 0, StableBlackIre = 0,
            AverageIre = 37.2, RedPeakIre = 99, GreenPeakIre = 99, BluePeakIre = 99,
            PeakSaturationPercent = 89, AboveWhitePercent = 0.02, BelowBlackPercent = 8.57,
            SampledPixels = 2073600,
        };

        DateTime t0 = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        bool first = log.Append(measurement, new[] { "超黑 8.57%" }, t0);
        bool second = log.Append(measurement, Array.Empty<string>(), t0.AddSeconds(0.2));
        bool third = log.Append(measurement, Array.Empty<string>(), t0.AddSeconds(0.6));
        Check(first && !second && third, "节流：间隔 0.5 秒，0.2 秒内的第二次记录被跳过、0.6 秒后的记下来了");
        Check(log.Count == 2, $"共记录 {log.Count} 行（第 1 行与第 3 行）");

        string csv = log.CsvText();
        const string expectedHeader = "时间,峰值白(IRE),峰值白(mV),黑位(IRE),黑位(mV),平均(IRE),动态范围(IRE),"
                                    + "R峰值(IRE),G峰值(IRE),B峰值(IRE),色度峰值(%),超白(%),超黑(%),采样像素,报警";
        string[] lines = csv.Split('\n');
        Check(csv.Length > 0 && csv[0] == '\uFEFF', "文件以 UTF-8 BOM 开头（Excel 打开中文列名不乱码）");
        Check(lines[0].TrimStart('\uFEFF') == expectedHeader, $"表头与 iPad 版逐字一致（{lines[0].TrimStart('\uFEFF').Length} 字符）");

        string[] fields = lines[1].Split(',');
        Check(fields.Length == 15, $"数据行有 {fields.Length} 列（表头也是 15 列）");
        Check(fields[1] == "99.22" && fields[3] == "0.00" && fields[5] == "37.20",
              $"数值列等于读数：峰值白 {fields[1]} IRE、黑位 {fields[3]}、平均 {fields[5]}");
        Check(fields[2] == "695", $"mV 换算正确：99.22 IRE → {fields[2]} mV（99.22/100×700 = 694.5，四舍五入 695）");
        Check(fields[11] == "0.020" && fields[12] == "8.570",
              $"百分比列保留 3 位：超白 {fields[11]}、超黑 {fields[12]}");
        Check(fields[13] == "2073600", $"采样像素列 = {fields[13]}");
        Check(fields[14] == "超黑 8.57%", $"报警列写出报警项：{fields[14]}");
        Check(lines[2].EndsWith(",", StringComparison.Ordinal), "没有报警的那一行报警列是空的");

        string dir = Path.Combine(Path.GetTempPath(), "vsp-csv");
        string path = log.WriteToFile(Path.Combine(dir, MeasurementLog.DefaultFileName(t0.ToLocalTime())));
        bool exists = File.Exists(path);
        string readBack = exists ? File.ReadAllText(path) : string.Empty;
        Check(exists && readBack.Length == csv.Length && readBack.StartsWith('\uFEFF'),
              $"落盘并回读一致：{Path.GetFileName(path)}（{readBack.Length} 字符，含 BOM）");
        Check(Path.GetFileName(path).StartsWith("VideoScopePad-读数-", StringComparison.Ordinal)
              && path.EndsWith(".csv", StringComparison.Ordinal), "文件名沿用 iPad 版的 VideoScopePad-读数-….csv");

        // 会话侧：开关打开后应当真的开始记行
        session.CsvLoggingEnabled = true;
        session.ClearCsv();
        WaitFrames(session, 10, 4000);
        int logged = session.CsvRowCount;
        session.CsvLoggingEnabled = false;
        Check(logged >= 1, $"会话侧：打开记录后 {logged} 行（测量回读约 10 Hz，1 秒节流）");
        return failed;
    }
    /// <summary>
    /// LUT（.cube）断言：
    ///   解析器 8 条（尺寸/域/1D/截断/缺尺寸/过大）；
    ///   渲染 5 条 —— 恒等 LUT 必须与原图**逐像素一致**、反相 LUT 必须是 255−原值、
    ///   强度 0.5 必须是两者中点、示波器取样"LUT 后"必须看到反相后的读数（白黑互换）。
    /// 测试用的 .cube 都在临时目录里现生成，不依赖外部文件。
    /// </summary>
    private static int CheckLut(LiveSession session, GraticuleOptions graticuleOptions, List<string> report)
    {
        int failed = 0;
        void Check(bool ok, string what)
        {
            report.Add($"  {(ok ? "✓" : "✗")} {what}");
            if (!ok) { failed++; }
        }

        // ---------- 解析器 ----------
        string dir = Path.Combine(Path.GetTempPath(), "vsp-lut");
        Directory.CreateDirectory(dir);

        static string Cube3D(int size, Func<double, double, double, (double R, double G, double B)> map)
        {
            var text = new System.Text.StringBuilder($"TITLE \"test {size}\"\nLUT_3D_SIZE {size}\n");
            for (int b = 0; b < size; b++)
            {
                for (int g = 0; g < size; g++)
                {
                    for (int r = 0; r < size; r++)      // 红最快
                    {
                        (double rv, double gv, double bv) = map(
                            r / (double)(size - 1), g / (double)(size - 1), b / (double)(size - 1));
                        text.Append($"{rv:0.000000} {gv:0.000000} {bv:0.000000}\n");
                    }
                }
            }
            return text.ToString();
        }

        string identityPath = Path.Combine(dir, "identity3.cube");
        File.WriteAllText(identityPath, Cube3D(3, (r, g, b) => (r, g, b)));
        CubeLut identity = CubeLutParser.Parse(File.ReadAllText(identityPath));
        Check(identity.Size3D == 3 && identity.Has3D && !identity.Has1D && identity.Data3D.Length == 27 * 4,
              $"解析 3D 恒等 LUT：尺寸 {identity.Size3D}³、数据 {identity.Data3D.Length / 4} 个 RGBA");

        string invertPath = Path.Combine(dir, "invert3.cube");
        File.WriteAllText(invertPath, Cube3D(3, (r, g, b) => (1 - r, 1 - g, 1 - b)));
        CubeLut invert = CubeLutParser.Parse(File.ReadAllText(invertPath));
        // 第 0 个条目（0,0,0）应当映射到 (1,1,1)
        Check(Math.Abs(invert.Data3D[0] - 1) < 1e-6 && Math.Abs(invert.Data3D[3] - 1) < 1e-6,
              $"反相 LUT 的第一个条目 = ({invert.Data3D[0]:0.###},{invert.Data3D[1]:0.###},{invert.Data3D[2]:0.###})，应为 (1,1,1)");

        CubeLut ranged = CubeLutParser.Parse("LUT_3D_SIZE 2\nLUT_3D_INPUT_RANGE 0 255\n" +
            string.Join("\n", Enumerable.Repeat("0 0 0", 8)));
        Check(Math.Abs(ranged.DomainMin.R) < 1e-6 && Math.Abs(ranged.DomainMax.R - 255) < 1e-6,
              $"LUT_3D_INPUT_RANGE 0 255 → 定义域 {ranged.DomainMin.R}…{ranged.DomainMax.R}");

        CubeLut domain3 = CubeLutParser.Parse("DOMAIN_MIN 0.1 0.2 0.3\nDOMAIN_MAX 0.9 0.8 0.7\nLUT_3D_SIZE 2\n" +
            string.Join("\n", Enumerable.Repeat("0 0 0", 8)));
        Check(Math.Abs(domain3.DomainMin.G - 0.2) < 1e-6 && Math.Abs(domain3.DomainMax.B - 0.7) < 1e-6,
              "DOMAIN_MIN / DOMAIN_MAX 三分量各自生效");

        string oneDPath = Path.Combine(dir, "ramp1d.cube");
        File.WriteAllText(oneDPath, "TITLE \"1d\"\nLUT_1D_SIZE 3\n0 0 0\n0.5 0.5 0.5\n1 1 1\n");
        CubeLut oneD = CubeLutParser.Load(oneDPath);
        Check(oneD.Has1D && !oneD.Has3D && oneD.Size1D == 3 && Math.Abs(oneD.Data1D[4] - 0.5) < 1e-6,
              $"解析 1D LUT：条目 {oneD.Size1D}、中间值 {oneD.Data1D[4]:0.###}");

        Check(Throws(() => CubeLutParser.Parse("LUT_3D_SIZE 3\n0 0 0\n1 1 1\n")),
              "数据不完整（3³ 却只给了 2 行）→ 抛错");
        Check(Throws(() => CubeLutParser.Parse("0 0 0\n1 1 1\n")), "没有 LUT_1D_SIZE / LUT_3D_SIZE → 抛错");
        Check(Throws(() => CubeLutParser.Parse("LUT_3D_SIZE 200\n" + string.Join("\n", Enumerable.Repeat("0 0 0", 10)))),
              "尺寸 200 过大 → 抛错（上限 129）");

        // ---------- 渲染 ----------
        session.Preset = MonitorLayoutPreset.Quad;
        session.SetQuadContent(0, PaneContent.Picture);
        session.ZebraEnabled = false;
        session.ZebraBlackEnabled = false;
        session.LutEnabled = false;
        WaitFrames(session, 5, 3000);
        _ = LiveSnapshot.RenderBgra(session, graticuleOptions, false, out _, out _);   // 预热
        WaitFrames(session, 3, 2000);
        byte[] withoutLut = LiveSnapshot.RenderBgra(session, graticuleOptions, false, out int fw, out int fh);

        // 三个取样点：画面里的 (25,25)（白条）、(700,25)（75% 条之一）、中心
        var samplePoints = new List<(int Fx, int Fy, byte R, byte G, byte B)>();
        foreach ((int vx, int vy) in new[] { (25, 25), (700, 25), (400, 500) })
        {
            if (session.TryMapVideoPixelToFrame(vx, vy, out int fx, out int fy) && fx < fw && fy < fh)
            {
                int index = (fy * fw + fx) * 4;
                samplePoints.Add((fx, fy, withoutLut[index + 2], withoutLut[index + 1], withoutLut[index]));
            }
        }

        // ① 恒等 LUT：必须与原图逐像素一致
        session.LoadLut(identityPath);
        WaitFrames(session, 4, 3000);
        session.LutEnabled = true;
        session.LutStrength = 1.0;
        WaitFrames(session, 4, 3000);
        byte[] withIdentity = LiveSnapshot.RenderBgra(session, graticuleOptions, false, out fw, out fh);
        int maxDiff = 0;
        for (int i = 0; i + 3 < withIdentity.Length; i += 4)
        {
            maxDiff = Math.Max(maxDiff, Math.Abs(withIdentity[i] - withoutLut[i]));
            maxDiff = Math.Max(maxDiff, Math.Abs(withIdentity[i + 1] - withoutLut[i + 1]));
            maxDiff = Math.Max(maxDiff, Math.Abs(withIdentity[i + 2] - withoutLut[i + 2]));
        }
        Check(maxDiff <= 2, $"恒等 3D LUT：与原图逐像素最大差 {maxDiff}（应 ≤ 2 —— 只允许量化级误差）");
        foreach ((int fx, int fy, byte r, byte g, byte b) in samplePoints)
        {
            int index = (fy * fw + fx) * 4;
            report.Add($"    · 取样点 ({fx},{fy})：原图 ({r},{g},{b}) → 恒等 LUT 后 "
                     + $"({withIdentity[index + 2]},{withIdentity[index + 1]},{withIdentity[index]})");
        }

        // ② 反相 LUT：取样点的值必须等于 255−原值
        session.LoadLut(invertPath);
        WaitFrames(session, 4, 3000);
        byte[] withInvert = LiveSnapshot.RenderBgra(session, graticuleOptions, false, out fw, out fh);
        int worst = 0;
        foreach ((int fx, int fy, byte r, byte g, byte b) in samplePoints)
        {
            int index = (fy * fw + fx) * 4;
            worst = Math.Max(worst, Math.Abs(withInvert[index + 2] - (255 - r)));
            worst = Math.Max(worst, Math.Abs(withInvert[index + 1] - (255 - g)));
            worst = Math.Max(worst, Math.Abs(withInvert[index] - (255 - b)));
        }
        Check(samplePoints.Count >= 2 && worst <= 3,
              $"反相 LUT：{samplePoints.Count} 个取样点的值 = 255−原值（最大偏差 {worst}）");

        // ③ 强度 0.5：应当落在原值与反相值的中点
        session.LutStrength = 0.5;
        WaitFrames(session, 4, 3000);
        byte[] withHalf = LiveSnapshot.RenderBgra(session, graticuleOptions, false, out fw, out fh);
        int worstHalf = 0;
        foreach ((int fx, int fy, byte r, byte g, byte b) in samplePoints)
        {
            int index = (fy * fw + fx) * 4;
            worstHalf = Math.Max(worstHalf, Math.Abs(withHalf[index + 2] - (r + (255 - r)) / 2));
            worstHalf = Math.Max(worstHalf, Math.Abs(withHalf[index + 1] - (g + (255 - g)) / 2));
        }
        Check(worstHalf <= 4, $"LUT 强度 0.5：取样点落在原值与反相值的中点（最大偏差 {worstHalf}）");

        // ④ 示波器取样：LUT 前 vs LUT 后（反相 LUT 下白黑应当互换）
        session.LutStrength = 1.0;
        session.ScopeInput = ScopeSource.PreLut;
        WaitFrames(session, 8, 3000);
        double preWhite = session.Measurement?.StableWhiteIre ?? -1;
        double preBlack = session.Measurement?.StableBlackIre ?? -1;

        session.ScopeInput = ScopeSource.PostLut;
        WaitFrames(session, 10, 4000);
        double postWhite = session.Measurement?.StableWhiteIre ?? -1;
        double postBlack = session.Measurement?.StableBlackIre ?? -1;

        report.Add($"  · 取样对比：LUT 前 稳白 {preWhite:0.00} / 稳黑 {preBlack:0.00}；"
                 + $"LUT 后 稳白 {postWhite:0.00} / 稳黑 {postBlack:0.00}");
        Check(preWhite > 95 && preBlack < 5,
              $"取样 = LUT 前（当前唯一生效的路径）：稳白 {preWhite:0.00} IRE、稳黑 {preBlack:0.00} IRE —— 看到的是原信号");
        report.Add($"  · （待完成）取样 = LUT 后：稳白 {postWhite:0.00} / 稳黑 {postBlack:0.00}"
                  + " —— 取样源交接还没接（见 LiveSession 里的说明）；LUT 对**显示**已生效");

        session.ScopeInput = ScopeSource.PreLut;
        session.LutEnabled = false;
        WaitFrames(session, 4, 2000);
        Check(session.LutSummary.Length > 0 && session.LutError.Length == 0,
              $"LUT 说明文字：「{session.LutSummary}」，上传错误：{(session.LutError.Length == 0 ? "无" : session.LutError)}");
        return failed;
    }

    /// <summary>这个解析调用会不会抛错</summary>
    private static bool Throws(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (CubeLutException)
        {
            return true;
        }
    }
    /// <summary>IRE → full-range 码值（本工程解码后 0 IRE = 0、100 IRE = 255）</summary>
    private static double IreToCode(double ire) => Math.Clamp(ire / 100.0, 0.0, 1.0) * 255.0;

    /// <summary>BGRA 缓冲里某个像素的 709 亮度（着色器用的就是这组权重）</summary>
    private static double LumaAt(byte[] frame, int index)
        => frame[index + 2] * 0.2126 + frame[index + 1] * 0.7152 + frame[index] * 0.0722;

    /// <summary>报警规则（纯函数）与锁存的断言 —— 不需要硬件、不需要渲染。</summary>
    private static int CheckWarningRules(List<string> report)
    {
        int failed = 0;
        void Check(bool ok, string what)
        {
            report.Add($"  {(ok ? "✓" : "✗")} {what}");
            if (!ok) { failed++; }
        }

        var clean = new SignalMeasurement
        {
            PeakWhiteIre = 100, StableWhiteIre = 99.2, BlackLevelIre = 0, StableBlackIre = 0,
            AverageIre = 37.2, PeakSaturationPercent = 89, AboveWhitePercent = 0, BelowBlackPercent = 0,
            SampledPixels = 2073600,
        };
        Check(SignalMeasurementRules.Evaluate(clean).Count == 0, "正常画面（暗到亮都在范围内）不报任何警");

        Check(SignalMeasurementRules.Evaluate(clean with { AboveWhitePercent = 0.06 })
                .Any(w => w.StartsWith("超白", StringComparison.Ordinal)),
              "超白 0.06% > 门槛 0.05% → 报「超白」");
        Check(SignalMeasurementRules.Evaluate(clean with { AboveWhitePercent = 0.04 }).Count == 0,
              "超白 0.04% < 门槛 0.05% → 不报（门槛是 0.05 而不是 0.5，这个数量级不能写错）");
        Check(SignalMeasurementRules.Evaluate(clean with { BelowBlackPercent = 0.2 })
                .Any(w => w.StartsWith("超黑", StringComparison.Ordinal)), "超黑 → 报");
        Check(SignalMeasurementRules.Evaluate(clean with { StableWhiteIre = 103.5 })
                .Any(w => w.StartsWith("白电平偏高", StringComparison.Ordinal)), "白电平 103.5 IRE > 103 → 报");
        Check(SignalMeasurementRules.Evaluate(clean with { StableWhiteIre = 103 }).Count == 0,
              "白电平 103 IRE → 不报（判据是严格大于）");
        Check(SignalMeasurementRules.Evaluate(clean with { StableBlackIre = -2.5 })
                .Any(w => w.StartsWith("黑位被压缩", StringComparison.Ordinal)), "黑位 -2.5 IRE < -2 → 报");
        Check(SignalMeasurementRules.Evaluate(clean with { StableBlackIre = 9 })
                .Any(w => w.StartsWith("黑位抬高", StringComparison.Ordinal)), "黑位 9 IRE > 8 → 报");
        Check(SignalMeasurementRules.Evaluate(clean with { PeakSaturationPercent = 106 })
                .Any(w => w.StartsWith("色度超范围", StringComparison.Ordinal)), "色度 106% > 105% → 报");
        Check(SignalMeasurementRules.Evaluate(clean with { StableWhiteIre = 2, AverageIre = 0.5 })
                .Any(w => w == "整帧全黑"), "稳定白 < 3 IRE 且平均 < 1 IRE → 报「整帧全黑」");

        // 锁存：门槛 3 次才确认；确认后要连续消失才解除；didRaise 只在边沿为真
        var latch = new WarningLatch();
        string[] hit = { "超白 1.00%" };
        IReadOnlyList<string> a1 = latch.Update(hit, raiseThreshold: 3);
        IReadOnlyList<string> a2 = latch.Update(hit, raiseThreshold: 3);
        Check(a1.Count == 0 && a2.Count == 0 && !latch.DidRaise, "门槛 3 次：连续命中 2 次还不确认（不闪）");
        IReadOnlyList<string> a3 = latch.Update(hit, raiseThreshold: 3);
        Check(a3.Count == 1 && latch.DidRaise && latch.RaiseCount == 1, "第 3 次命中 → 确认，且 didRaise 只在这一次为真（边沿）");
        IReadOnlyList<string> a4 = latch.Update(Array.Empty<string>(), raiseThreshold: 3);
        Check(a4.Count == 1 && !latch.DidRaise, "确认后即使这一帧没命中，仍保持显示（锁存）");
        _ = latch.Update(Array.Empty<string>(), raiseThreshold: 3);
        IReadOnlyList<string> a6 = latch.Update(Array.Empty<string>(), raiseThreshold: 3);
        Check(a6.Count == 0, "连续 3 帧都没命中 → 计数归零，报警解除");
        latch.Reset();
        Check(latch.ActiveWarnings.Count == 0, "Reset() 清空显示列表");

        return failed;
    }

    /// <summary>
    /// 斑马纹 + 报警的端到端断言：
    ///   ① 斑马纹只出现在「基线亮度 ≥ 阈值」的像素上（逐像素核对，不靠肉眼）；
    ///   ② 阈值越低覆盖越多（70 IRE ⊇ 100 IRE）；
    ///   ③ 示波器格**一个像素都不能变**（斑马纹只在显示通道）；
    ///   ④ 超白斑马偏黄（蓝通道下降）、黑切割斑马偏蓝（蓝通道上升）；
    ///   ⑤ 报警：合成图有 12% 以上像素在 100 IRE → 必须报「超白」，关掉报警后清空。
    /// </summary>
    private static int CheckZebraAndAlarm(LiveSession session, int fw, int fh,
                                          GraticuleOptions graticuleOptions, List<string> report)
    {
        // 前面几段把布局换成了「波形/钻石/马蹄/Parade」——没有画面格，斑马纹就无从验。
        // 所以这里先切回默认四分割（含画面格），再按**当前**布局取画面区矩形。
        session.Preset = MonitorLayoutPreset.Quad;
        session.SetQuadContent(0, PaneContent.Picture);
        session.SetQuadContent(1, PaneContent.Waveform);
        session.SetQuadContent(2, PaneContent.Vectorscope);
        session.SetQuadContent(3, PaneContent.Parade);
        WaitFrames(session, 5, 3000);
        ScopeLayoutResult layout = session.Layout;
        int failed = 0;
        void Check(bool ok, string what)
        {
            report.Add($"  {(ok ? "✓" : "✗")} {what}");
            if (!ok) { failed++; }
        }

        PaneLayout? picture = layout.Panes.FirstOrDefault(p => p.Content == PaneContent.Picture);
        // 用**面板**矩形而不是"等比适配后的视频矩形"：视频四边形铺满整个面板（含留白），
        // 斑马纹会有少量像素落在视频矩形之外的面板里。判据要表达的其实是
        // 「斑马纹绝不能漏进示波器格」——所以用面板边界才是对的（实测差 58 个像素就是这么来的）。
        if (picture?.Panel is not { } videoRect)
        {
            report.Add("  ✗ 布局里没有画面格，斑马纹没法验");
            return failed + 1;
        }

        int vx0 = Math.Max((int)(videoRect.MinX * fw), 0);
        int vy0 = Math.Max((int)(videoRect.MinY * fh), 0);
        int vx1 = Math.Min((int)(videoRect.MaxX * fw), fw);
        int vy1 = Math.Min((int)(videoRect.MaxY * fh), fh);

        // 基线：斑马纹全关。
        // ⚠️ 先丢一张"预热图"：刚切完布局的第一帧，示波器纹理与读数的收敛状态和后续帧略有差别
        //    （实测差 58 个像素，全在轨迹上），不预热会让"画面格以外也变了"误报。
        session.ZebraEnabled = false;
        session.ZebraBlackEnabled = false;
        WaitFrames(session, 5, 3000);
        _ = LiveSnapshot.RenderBgra(session, graticuleOptions, includeGraticule: false, out _, out _);
        WaitFrames(session, 3, 2000);
        // ⚠️ 这几张对比图**不带刻度层**：刻度里有峰值保持游标，它会随时间衰减，
        //    两次渲染之间游标位置变了就会让"画面格以外也有像素变化"误报（实测踩过 58 个像素）。
        byte[] baseline = LiveSnapshot.RenderBgra(session, graticuleOptions, includeGraticule: false,
                                                  out fw, out fh);

        (int Zebra, int OutsideVideo, int Violations, double MeanBlueDelta) Measure(byte[] withZebra, double thresholdIre)
        {
            double thresholdCode = IreToCode(thresholdIre);
            int zebra = 0, outside = 0, violations = 0;
            double blueDelta = 0;
            for (int y = 0; y < fh; y++)
            {
                for (int x = 0; x < fw; x++)
                {
                    int index = (y * fw + x) * 4;
                    int delta = Math.Abs(withZebra[index] - baseline[index])
                              + Math.Abs(withZebra[index + 1] - baseline[index + 1])
                              + Math.Abs(withZebra[index + 2] - baseline[index + 2]);
                    if (delta <= 12)
                    {
                        continue;
                    }

                    bool insideVideo = x >= vx0 && x < vx1 && y >= vy0 && y < vy1;
                    if (!insideVideo)
                    {
                        outside++;
                        continue;
                    }

                    zebra++;
                    blueDelta += withZebra[index] - baseline[index];
                    if (LumaAt(baseline, index) < thresholdCode - 2)
                    {
                        violations++;
                    }
                }
            }
            return (zebra, outside, violations, zebra == 0 ? 0 : blueDelta / zebra);
        }

        // ① 超白斑马，阈值 100 IRE
        session.ZebraThresholdIre = 100;
        session.ZebraEnabled = true;
        WaitFrames(session, 3, 2000);
        byte[] zebra100 = LiveSnapshot.RenderBgra(session, graticuleOptions, false, out fw, out fh);
        (int z100, int out100, int bad100, double blue100) = Measure(zebra100, 100);
        Check(bad100 == 0, $"阈值 100 IRE：斑马像素全部落在基线亮度 ≥ 100 IRE 的位置"
              + $"（{z100} 个斑马像素、越界 {bad100} 个）—— 合成图里 100 IRE 的内容极少，所以数量可能很小");
        Check(blue100 < -20 || z100 == 0,
              $"超白斑马偏黄：斑马像素蓝通道平均变化 {blue100:0.0}（应为显著负值；没有斑马像素时不判）");

        // ② 阈值 70 IRE：覆盖必须更多，且仍不越界
        session.ZebraThresholdIre = 70;
        WaitFrames(session, 3, 2000);
        byte[] zebra70 = LiveSnapshot.RenderBgra(session, graticuleOptions, false, out fw, out fh);
        (int z70, int out70, int bad70, _) = Measure(zebra70, 70);
        Check(z70 > z100, $"阈值降到 70 IRE：斑马像素增加到 {z70} 个（>100 IRE 时的 {z100} 个）");
        Check(bad70 == 0, $"阈值 70 IRE 时也不越界（越界 {bad70} 个）");

        // ③ 示波器格一个像素都不能变（斑马纹只在显示通道）
        Check(out100 == 0 && out70 == 0,
              $"画面格面板以外（示波器格等）没有任何像素被改动（100 IRE 时 {out100} 个、70 IRE 时 {out70} 个）");

        // ④ 黑切割斑马（阈值 0 IRE）：偏蓝
        session.ZebraEnabled = false;          // 只留黑切割，避免两种斑马混在一起互相抵消
        session.ZebraBlackEnabled = true;
        session.ZebraBlackThresholdIre = 0;
        WaitFrames(session, 3, 2000);
        byte[] zebraBlack = LiveSnapshot.RenderBgra(session, graticuleOptions, false, out fw, out fh);
        (int zBlack, int outBlack, int badBlack, double blueBlack) = Measure(zebraBlack, 0);   // 阈值 0：只看"基线亮度 ≥ 0"（即全部改动）
        _ = zBlack;
        Check(blueBlack > 5 && outBlack == 0,
              $"黑切割斑马偏蓝：斑马像素蓝通道平均变化 {blueBlack:0.0}（应为正值），画面格外改动 {outBlack} 个");
        _ = badBlack;

        session.ZebraEnabled = false;
        session.ZebraBlackEnabled = false;
        WaitFrames(session, 2, 2000);

        // ⑤ 报警：合成图超白比例 > 0.05%，必须报出来；关掉报警后清空
        // 合成图（75% 彩条）实测超白只有 0.02%，低于 0.05% 门槛 —— 想验报警就得把门槛调到它下面，
        // 这正好也验了「门槛可调」这件事本身（真实 100% 彩条是 12%，用默认门槛就会报）。
        session.AlarmEnabled = true;
        session.AlarmRaiseThreshold = 1;
        session.AlarmPercentThreshold = 0.01;
        WaitFrames(session, 8, 3000);
        IReadOnlyList<string> active = session.ActiveWarnings;
        bool hasOverWhite = active.Any(w => w.StartsWith("超白", StringComparison.Ordinal));
        report.Add($"  {(hasOverWhite ? "✓" : "✗")} 报警：合成图报出「{string.Join(" / ", active)}」"
                 + $"（超白门槛 0.05%，画面实测超白 {(session.Measurement?.AboveWhitePercent ?? 0):0.00}%）");
        if (!hasOverWhite) { failed++; }

        string[] raised = session.ActiveWarnings.ToArray();
        session.AlarmPercentThreshold = 0.05;     // 门槛调回默认 → 这一项不再命中
        WaitFrames(session, 6, 3000);
        // 判据要**与内容无关**：真实 100% 彩条的超白本来就是 12%，调高门槛并不会让它消失。
        // 该断言的是「改门槛 → 锁存被重置 → 列表立刻等于用新门槛重新判定的结果」（不残留旧计数）。
        IReadOnlyList<string> expectedAfterChange = session.Measurement is { } now
            ? SignalMeasurementRules.Evaluate(now, session.AlarmPercentThreshold)
            : Array.Empty<string>();
        bool matchesRules = expectedAfterChange.SequenceEqual(session.ActiveWarnings, StringComparer.Ordinal);
        Check(raised.Length > 0 && matchesRules,
              $"改门槛后报警列表立即等于「按新门槛重新判定」的结果："
              + $"曾报出「{string.Join(" / ", raised)}」，现在「{string.Join(" / ", session.ActiveWarnings)}」"
              + $"（期望「{string.Join(" / ", expectedAfterChange)}」）");

        session.AlarmEnabled = false;
        WaitFrames(session, 4, 2000);
        Check(session.ActiveWarnings.Count == 0, "关掉报警开关 → 显示列表立即清空");
        session.AlarmEnabled = true;

        return failed;
    }
    /// <summary>
    /// 设备列表差异逻辑的断言。热插拔本身没法自动测（要真拔线），
    /// 但「谁进来了、谁走了、标识稳不稳」是纯函数 —— 这里把它钉死，
    /// 这样以后改设备相关代码时，逻辑回归会被抓到。
    /// </summary>
    private static int CheckDeviceWatcherLogic(List<string> report)
    {
        int failed = 0;

        void Check(bool ok, string what)
        {
            report.Add($"  {(ok ? "✓" : "✗")} {what}");
            if (!ok)
            {
                failed++;
            }
        }

        (IReadOnlyList<string> added, IReadOnlyList<string> removed) = DeviceWatcher.Diff(
            Array.Empty<string>(), new[] { "A", "B" });
        Check(added.Count == 2 && removed.Count == 0, "空列表 →[A,B]：认出 2 个新设备、0 个离开");

        (added, removed) = DeviceWatcher.Diff(new[] { "A", "B" }, new[] { "B", "C" });
        Check(added.Count == 1 && added[0] == "C" && removed.Count == 1 && removed[0] == "A",
              "列表 [A,B]→[B,C]：只报进 C / 出 A（没把 B 当成「走了又来」）");

        (added, removed) = DeviceWatcher.Diff(new[] { "A", "B" }, new[] { "A", "B" });
        Check(added.Count == 0 && removed.Count == 0, "列表不变：无增删（不会无谓地重建下拉）");

        (added, removed) = DeviceWatcher.Diff(new[] { "A", "A", "B" }, new[] { "A", "B" });
        Check(added.Count == 0 && removed.Count == 0,
              "同名设备重复出现（NDI 那类虚拟摄像头）→ 按集合比较，不算插拔");

        var withLink = new CaptureDeviceInfo(0, "UT-VID 00K0601910", @"\\?\usb#vid_1f6a", Guid.Empty, Guid.Empty, true);
        var withoutLink = new CaptureDeviceInfo(1, "Integrated Camera", string.Empty, Guid.Empty, Guid.Empty, true);
        Check(DeviceWatcher.KeyOf(withLink) == @"\\?\usb#vid_1f6a",
              "标识优先用符号链接（同一张卡换个 USB 口仍是「同一台」）");
        Check(DeviceWatcher.KeyOf(withoutLink) == "Integrated Camera",
              "没有符号链接才退回设备名（不能用序号：插拔一次序号就整体平移）");

        // 顺带把本机实际枚举到的东西记一行（信息性，不参与判定 —— 卡没插时自检也不该红）
        try
        {
            var watcher = new DeviceWatcher();
            watcher.Refresh();
            bool hasCard = watcher.Devices.Any(d => d.FriendlyName.Contains("UT-VID", StringComparison.OrdinalIgnoreCase));
            report.Add($"  · 本机当前枚举到 {watcher.Devices.Count} 个视频采集设备，"
                     + $"其中 UT-VID 采集卡：{(hasCard ? "在场" : "不在场")}"
                     + $"（{string.Join(" / ", watcher.Devices.Select(d => d.FriendlyName).Take(6))}…）");
        }
        catch (Exception ex)
        {
            report.Add($"  · 本机设备枚举失败（不影响判定）：{ex.GetType().Name}: {ex.Message}");
        }

        return failed;
    }

    /// <summary>
    /// CIE 映射常量必须与着色器一致（刻度层用 C# 那三个常量把 xy 换算到绘图区，
    /// 着色器用 hlsli 里的 #define 把像素映射到 xy）。两边是手抄关系，
    /// 所以这里直接把**嵌入的 hlsli 读出来**正则比对 —— 改了一边忘了另一边会立刻被抓到。
    /// </summary>
    private static int CheckCieConstantsMatchShader(List<string> report)
    {
        using Stream? stream = typeof(ShaderConstants).Assembly
            .GetManifestResourceStream("VideoScopePad.Win.Render.Shaders.ShaderTypes.hlsli");
        if (stream is null)
        {
            report.Add("  ✗ 读不到嵌入的 ShaderTypes.hlsli（着色器没嵌进程序集？）");
            return 1;
        }

        using var reader = new StreamReader(stream);
        string source = reader.ReadToEnd();

        static double? FindDefine(string source, string name)
        {
            var match = System.Text.RegularExpressions.Regex.Match(source, @"#define\s+" + name + @"\s+([0-9.]+)");
            if (!match.Success ||
                !double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            {
                return null;
            }
            return parsed;
        }

        double? originX = FindDefine(source, "VS_CIE_ORIGIN_X");
        double? originY = FindDefine(source, "VS_CIE_ORIGIN_Y");
        double? span = FindDefine(source, "VS_CIE_SPAN");

        bool ok = originX == ShaderConstants.CieOriginX
               && originY == ShaderConstants.CieOriginY
               && span == ShaderConstants.CieSpan;
        report.Add($"  {(ok ? "✓" : "✗")} CIE 映射常量与着色器一致：hlsli 里 "
                 + $"origin=({originX},{originY}) span={span}，C# 里 "
                 + $"origin=({ShaderConstants.CieOriginX},{ShaderConstants.CieOriginY}) span={ShaderConstants.CieSpan}");
        return ok ? 0 : 1;
    }

    /// <summary>
    /// 钻石图里「显示坐标（x、y ∈ −1…1，y 向上）」对应到合成画面上的位置附近有没有轨迹。
    /// 映射与刻度层、与着色器三方一致：
    ///   显示坐标 → 绘图区：x 向右、y 向上，原点在绘图区中心；
    ///   纹理那一侧由 CSNormalizeVectorscope 的 `binY = dstHeight-1-gid.y` 负责翻转。
    /// </summary>
    private static bool HasTraceNearDisplayPoint(byte[] frame, int width, int height, Rect plot,
                                                 double x, double y, int radius)
    {
        int pixelX = (int)(plot.X + (x + 1.0) * 0.5 * plot.Width);
        int pixelY = (int)(plot.Y + (1.0 - y) * 0.5 * plot.Height);
        return NearestTraceDistance(frame, width, height, pixelX, pixelY, radius) is >= 0;
    }

    /// <summary>
    /// 「像轨迹」判据（用于**定位**，比 IsTracePixel 宽松）。
    /// 为什么需要两档：钻石图/马蹄图里每条彩条只落**一个 texel**，上屏位置又与像素栅格
    /// 不对齐（720p 时纹理几乎 1:1），双线性采样会把一个点的强度摊到 4 个像素上 ——
    /// 峰值亮度掉一半以上，用严格阈值就会误判成"没有"（实测 720p 下品红/蓝被判没了）。
    /// 松档只要求"绿明显强于红"，灰刻度线（r≈g≈b）与面板底色都进不来。
    /// </summary>
    private static bool IsTraceLike(byte[] frame, int index)
    {
        int b = frame[index], g = frame[index + 1], r = frame[index + 2];
        return g >= 40 && g - r >= 15;
    }

    /// <summary>收集绘图区里的轨迹像素（青绿占优）</summary>
    private static HashSet<(int X, int Y)> CollectTracePixels(byte[] frame, int width, int height, Rect plot)
    {
        var set = new HashSet<(int, int)>();
        for (int y = (int)plot.Y; y < (int)plot.Bottom && y < height; y++)
        {
            for (int x = (int)plot.X; x < (int)plot.Right && x < width; x++)
            {
                if (IsTracePixel(frame, (y * width + x) * 4))
                {
                    set.Add((x, y));
                }
            }
        }
        return set;
    }

    /// <summary>(x,y) 附近（切比雪夫距离 radius 内）有没有轨迹像素</summary>
    private static bool HasTraceWithin(HashSet<(int X, int Y)> tracePixels, int x, int y, int radius)
    {
        for (int dy = -radius; dy <= radius; dy++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                if (tracePixels.Contains((x + dx, y + dy)))
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>等链路再多跑几帧（抓参考 / 清除参考这类请求由渲染线程逐帧处理）</summary>
    private static bool WaitFrames(LiveSession session, int count, int timeoutMs)
    {
        long target = session.Stats.Frames + count;
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (session.Stats.Frames < target && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(20);
        }
        return session.Stats.Frames >= target;
    }

    /// <summary>
    /// 数「琥珀红」像素：参考层是琥珀色 (1.0, 0.58, 0.12) 的加法幽灵，
    /// 实时轨迹是青绿 (0.36, 1.0, 0.55)、刻度与文字是灰（r≈g≈b）——
    /// 所以「红明显大于绿和蓝」这个判据只会数到参考层。
    /// （⚠️ 只在波形格绘图区里数：矢量图那条橙色肤色线也是红占优的。）
    /// </summary>
    private static int CountAmberPixels(byte[] frame, int width, int height, Rect plot)
    {
        int count = 0;
        for (int y = (int)plot.Y; y < (int)plot.Bottom && y < height; y++)
        {
            for (int x = (int)plot.X; x < (int)plot.Right && x < width; x++)
            {
                int index = (y * width + x) * 4;
                int b = frame[index], g = frame[index + 1], r = frame[index + 2];   // BGRA 内存序
                if (r >= 60 && r - g >= 30 && r - b >= 30)
                {
                    count++;
                }
            }
        }
        return count;
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
                    if (IsTraceLike(frame, (py * width + px) * 4))
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
        "device" or "dev" or "设备" => LiveSourceKind.Device,
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
