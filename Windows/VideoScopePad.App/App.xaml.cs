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
                AnimateSynthetic = false,   // 静态合成图 = 码值完全已知，读数才能精确核对
                // ⚠️ 要多给一段帧预算：读数断言之后还要抓/清参考层，
                //    如果 StopAfterFrames 正好等于 frames，链路会在断言跑之前就停了
                //    （第一版就是这么错的：RequestReferenceCapture 永远没人处理）。
                StopAfterFrames = frames + 120,
            };
            session.SwitchSource(ParseSource(source), device);
            session.Start();

            // 等它跑够帧数（或超时）
            var deadline = DateTime.UtcNow.AddSeconds(90);
            while (session.Stats.Frames < frames && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(100);
            }

            // ⚠️ 这里**不能** session.Stop()：后面的读数与冻结参考断言还要继续驱动渲染线程
            //    （抓参考是「下一帧」在渲染线程执行的）。让它继续跑到 StopAfterFrames，
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

            // ---------- 冻结参考层：抓取后必须多出「落在轨迹上的琥珀贡献」----------
            // ⚠️ 不能简单地"数琥珀像素"：参考层与实时轨迹是**同一信号**时两者完全重合，
            //    加法混合下绿通道直接饱和，出来的像素反而是白/绿占优 —— 一条都数不到。
            //    正确做法是比对「画参考前后」的同一块区域：
            //      · 差异像素必须出现（否则参考层根本没参与合成）；
            //      · 差异必须**落在轨迹上**（靠近原来的轨迹像素）—— 这才说明参考层对的是位置；
            //      · 差异要往红偏（琥珀），而不是随便变亮。
            report.Add(string.Empty);
            report.Add("冻结参考层断言（比对画参考前后同一区域：差异必须出现、必须落在轨迹上、必须往红偏）：");

            PaneLayout? wavePane = session.Layout.Panes.FirstOrDefault(p => p.Content == PaneContent.Waveform);
            var wavePlot = wavePane?.Plot is { } wavePlotUnit
                ? new Rect(wavePlotUnit.MinX * fw, wavePlotUnit.MinY * fh,
                           wavePlotUnit.Width * fw, wavePlotUnit.Height * fh)
                : new Rect(0, 0, 0, 0);

            byte[] withoutReference = frame;
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
                report.Add("  ✗ 抓取参考后拿不到参考读数");
                exitCode = 1;
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
