//
//  Program.cs
//  render-selfcheck
//
//  离屏自检：不依赖窗口与采集卡，把合成测试信号跑完整条示波器链路，然后：
//    1) 断言上传/读回/通道顺序（像素级）
//    2) 断言 GPU 直方图落在**算得出来**的位置（75% 白条 = 191 bin、彩条落点 = 矢量图目标框）
//    3) 断言归一化后的示波器纹理方向正确（波形第 64 行 = 191 bin）
//    4) 把 6 张示波器纹理存成 PNG（给人看）
//
//  为什么全是数值断言：这个模型看不了图片，正确性只能靠可计算的结论兜住；
//  PNG 是给用户直接打开核对的。两边都要有，缺一不可。
//
//  用法：
//    dotnet run --project Windows\tools\render-selfcheck -- out\ 1920 1080
//

using System.Globalization;
using VideoScopePad.Win.Core;
using VideoScopePad.Win.Render;

namespace VideoScopePad.Tools.RenderSelfCheck;

internal static class Program
{
    private static int _passed;
    private static int _failed;

    private static int Main(string[] args)
    {
        string outDirectory = args.Length > 0 ? args[0] : "out";
        int width = args.Length > 1 && int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var w) ? w : 1920;
        int height = args.Length > 2 && int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h) ? h : 1080;

        try
        {
            Directory.CreateDirectory(outDirectory);

            using var d3d = D3DContext.Create();
            Console.WriteLine($"适配器        : {d3d.AdapterName}");
            Console.WriteLine($"特性级别      : {d3d.FeatureLevel}");

            string shaderDirectory = Path.Combine(AppContext.BaseDirectory, "Render", "Shaders");
            using var shaders = ShaderLibrary.Create(d3d.Device, shaderDirectory);
            using var pipelines = new PipelineLibrary(d3d.Device, shaders);
            Console.WriteLine($"着色器编译    : {shaders.Log.Count} 条");
            foreach (string line in shaders.Log)
            {
                Console.WriteLine("  " + line);
            }
            if (_failed > 0)
            {
                return Report();
            }

            // ---------- 1) 合成信号 → 纹理，核对像素 ----------
            var pixels = SyntheticSource.MakeTestFrame(width, height, out _);
            using var source = d3d.CreateTextureFromRgba8(width, height, pixels, out _);
            Console.WriteLine();
            Console.WriteLine($"合成信号      : {width}×{height}（SMPTE 75% 彩条 + 蓝条 + PLUGE + 灰阶斜坡）");

            d3d.SavePng(source, Path.Combine(outDirectory, "source.png"));
            var readBack = d3d.ReadBackRgba8(source, out int rw, out int rh);
            int barY = height / 6;
            CheckPixel(readBack, rw, rh, width / 14, barY, 191, 191, 191, "75% 白条");
            CheckPixel(readBack, rw, rh, width * 3 / 14, barY, 191, 191, 0, "黄条");
            CheckPixel(readBack, rw, rh, width * 5 / 14, barY, 0, 191, 191, "青条");
            CheckPixel(readBack, rw, rh, width * 11 / 14, barY, 191, 0, 0, "红条");
            CheckPixel(readBack, rw, rh, width * 13 / 14, barY, 0, 0, 191, "蓝条");
            CheckPixel(readBack, rw, rh, width / 14, height * 72 / 100, 0, 0, 191, "蓝条带");
            CheckPixel(readBack, rw, rh, width * 3 / 14, height * 72 / 100, 0, 0, 0, "蓝条带黑缝");
            CheckPixel(readBack, rw, rh, width / 14, height * 9 / 10, 0, 0, 0, "PLUGE 0 IRE");

            // ---------- 2) 示波器：累计 + 归一化 ----------
            using var sourceSrv = d3d.Device.CreateShaderResourceView(source);
            using var engine = new ScopeEngine(d3d.Device, shaders);

            var settings = new ScopeRenderSettings
            {
                NeedWaveform = true,
                NeedVectorscope = true,
                NeedDiamond = true,
                NeedCie = true,
                Stride = 1,          // 自检要精确，全采
            };
            engine.Encode(d3d.Context, sourceSrv, settings);
            d3d.Context.Flush();

            Console.WriteLine();
            Console.WriteLine("直方图断言：");
            var histogram = engine.ReadBackHistogram(d3d.Context);

            // 75% 白条的亮度码值（我造的信号就是 191）→ 落在哪个 bin
            int whiteCode = 191;
            // 亮度 = 加权和；白条三通道相同，所以 luma 码值 = 191
            uint whiteCount = 0;
            for (uint column = 0; column < 74; column++)   // 第 1 条彩条 = 前 1/7 列（512/7 ≈ 73）
            {
                whiteCount += histogram[WaveIndex(3, (uint)whiteCode, column)];
            }
            Check(whiteCount > 0, $"75% 白条的亮度落在 bin {whiteCode}（前 74 列共 {whiteCount} 个样本）");

            // 该 bin 的样本必须**只出现在白条那几列**（其它彩条的亮度都不是 191）——
            // 这比「最大值在哪个 bin」更能说明列映射正确（蓝条亮度只有 14，总量本来就最大）
            uint whiteInBarColumns = 0;
            uint whiteOutside = 0;
            for (uint column = 0; column < 512; column++)
            {
                uint count = histogram[WaveIndex(3, (uint)whiteCode, column)];
                if (column < 74)
                {
                    whiteInBarColumns += count;
                }
                else
                {
                    whiteOutside += count;
                }
            }
            long expectedWhite = (long)(width / 7.0) * (height * 2 / 3);
            // 白条列之外的 bin 191 只可能来自灰阶斜坡穿过 0.749 的那几列（每列约 238 个样本），
            // 所以给一个小上限，而不是要求严格为 0。
            Check(whiteOutside < 5000, $"bin {whiteCode} 在白条列之外只有斜坡穿过的那点样本（{whiteOutside} < 5000）");
            Check(Math.Abs(whiteInBarColumns - expectedWhite) < expectedWhite * 0.05,
                  $"bin {whiteCode} 的白条样本数 {whiteInBarColumns} 与白条面积 {expectedWhite} 相符（±5%）");

            // 纯黑（蓝条的黑缝 + PLUGE）必须有计数
            uint blackCount = 0;
            for (uint column = 0; column < 512; column++)
            {
                blackCount += histogram[WaveIndex(3, 0, column)];
            }
            Check(blackCount > 0, $"黑电平 bin 0 有 {blackCount} 个样本");

            // 矢量图：7 条彩条各自的 (Cb, Cr) 落点用同一套公式算出期望 bin
            Console.WriteLine();
            Console.WriteLine("矢量图断言（用与着色器相同的 BT.709 Cb/Cr 公式算期望落点）：");

            // 诊断：先把三块二维直方图的能量分布打出来（定位「落点全在中心」这类问题）
            DumpSection(histogram, ShaderConstants.GamutSectionVectorscope, "矢量");
            DumpSection(histogram, ShaderConstants.GamutSectionDiamond, "钻石");
            DumpSection(histogram, ShaderConstants.GamutSectionCie, "马蹄");

            // 诊断：各通道平面的总量（应当接近采样像素数）
            Console.WriteLine($"    各平面样本数：R={PlaneTotal(histogram, 0)} G={PlaneTotal(histogram, 1)} " +
                              $"B={PlaneTotal(histogram, 2)} Y={PlaneTotal(histogram, 3)}（全采应约 {(long)width * height}）");
            var bars = new (string Name, double R, double G, double B)[]
            {
                ("75% 白", 191.0 / 255, 191.0 / 255, 191.0 / 255),
                ("黄", 191.0 / 255, 191.0 / 255, 0),
                ("青", 0, 191.0 / 255, 191.0 / 255),
                ("绿", 0, 191.0 / 255, 0),
                ("品红", 191.0 / 255, 0, 191.0 / 255),
                ("红", 191.0 / 255, 0, 0),
                ("蓝", 0, 0, 191.0 / 255),
            };

            var expectedBins = new List<(string Name, uint X, uint Y)>();
            foreach (var bar in bars)
            {
                double cb = -0.114572 * bar.R - 0.385428 * bar.G + 0.5 * bar.B;
                double cr = 0.5 * bar.R - 0.454153 * bar.G - 0.045847 * bar.B;
                // ⚠️ 着色器里是 int(...) = 向零截断，不是四舍五入；断言必须用同一套取整方式，
                //    否则会像刚才那样「明明对得上却差 1 个 bin」。
                uint vx = (uint)Math.Clamp(Math.Truncate((cb + 0.5) * 256.0), 0, 255);
                uint vy = (uint)Math.Clamp(Math.Truncate((cr + 0.5) * 256.0), 0, 255);

                uint count = histogram[VectorIndex(vx, vy, ShaderConstants.GamutSectionVectorscope)];
                expectedBins.Add((bar.Name, vx, vy));
                Console.WriteLine($"    {bar.Name,-6} Cb={cb,7:+0.0000;-0.0000} Cr={cr,7:+0.0000;-0.0000} → bin({vx},{vy}) 计数 {count}");
                Check(count > 0, $"{bar.Name} 落在矢量图 bin({vx},{vy})");
            }

            // 灰度斜坡 + 黑场应当落在低饱和区域（矢量图中心附近），也就是整块矢量的重心靠近中心
            uint centerCount = 0;
            for (uint x = 118; x <= 138; x++)
            {
                for (uint y = 118; y <= 138; y++)
                {
                    centerCount += histogram[VectorIndex(x, y, ShaderConstants.GamutSectionVectorscope)];
                }
            }
            Check(centerCount > 0, $"矢量图中心区（灰度与黑场）有 {centerCount} 个样本");

            // 钻石图：灰阶斜坡是正中竖线（x = B−G = 0 → bin 128）
            uint diamondCenter = 0;
            for (uint y = 0; y < 256; y++)
            {
                diamondCenter += histogram[VectorIndex(128, y, ShaderConstants.GamutSectionDiamond)];
            }
            Check(diamondCenter > 0, $"钻石图正中竖线（灰阶）有 {diamondCenter} 个样本");

            // 马蹄图：应当有样本落进 CIE 图里（全量统计，别用步长抽样 —— 抽样会正好踩空）
            uint cieCount = 0;
            for (uint x = 0; x < 256; x++)
            {
                for (uint y = 0; y < 256; y++)
                {
                    cieCount += histogram[VectorIndex(x, y, ShaderConstants.GamutSectionCie)];
                }
            }
            Check(cieCount > 0, $"马蹄图内有 {cieCount} 个样本");

            // ---------- 3) 归一化纹理：方向与内容 ----------
            Console.WriteLine();
            Console.WriteLine("示波器纹理断言：");
            var waveform = engine.ReadBackTexture(d3d.Context, engine.TextureFor(ScopePanelKind.Waveform, WaveformMode.Luma), out int ww, out int wh);
            Check(ww == 512 && wh == 256, $"亮度波形纹理尺寸 {ww}×{wh}");

            // 波形第 y 行对应 bin = 255 − y，所以 191 bin 应该在第 64 行亮
            int rowForWhite = 256 - 1 - whiteCode;
            double rowValue = RowAverage(waveform, ww, rowForWhite, 0, 74);
            double otherRow = RowAverage(waveform, ww, 200, 0, 74);
            Check(rowValue > otherRow, $"波形第 {rowForWhite} 行（= {whiteCode} bin）比第 200 行亮：{rowValue:0.0} vs {otherRow:0.0}");

            var vectorscope = engine.ReadBackTexture(d3d.Context, engine.TextureFor(ScopePanelKind.Vectorscope, WaveformMode.Luma), out int vw, out int vh);
            Check(vw == 256 && vh == 256, $"矢量图纹理尺寸 {vw}×{vh}");
            int litPixels = 0;
            for (int i = 0; i < vectorscope.Length; i += 4)
            {
                if (vectorscope[i] > 8)
                {
                    litPixels++;
                }
            }
            // 矢量图本来就该是「几条彩条各自一个亮点」的样子（不是整片亮）
            Check(litPixels is >= 7 and < 2000, $"矢量图有 {litPixels} 个点亮的像素（7 条彩条应各一个亮点）");

            // 纹理像素与 bin 的对应关系：x = binX，y = 255 − binY —— 每条彩条都要在纹理上点亮
            bool allLit = true;
            foreach (var (name, binX, binY) in expectedBins)
            {
                byte value = vectorscope[((255 - (int)binY) * vw + (int)binX) * 4];
                if (value <= 0)
                {
                    allLit = false;
                    Console.WriteLine($"      ✗ 纹理上 {name} 落点 ({binX},{binY}) 像素为 0");
                }
            }
            Check(allLit, "7 条彩条在矢量图纹理上都有亮点（bin → 纹理坐标 y 翻转正确）");

            // ---------- 4) 整机合成：布局 + 画面 + 轨迹 ----------
            Console.WriteLine();
            Console.WriteLine("布局断言（四分割：画面 / 亮度波形 / 矢量图 / Parade）：");

            var layout = ScopeLayout.Compute(
                containerWidth: width,
                containerHeight: height,
                videoWidth: width,
                videoHeight: height,
                preset: MonitorLayoutPreset.Quad,
                aspectMode: AspectMode.Fit,
                fullscreenContent: PaneContent.Picture,
                quadContents: new[]
                {
                    PaneContent.Picture, PaneContent.Waveform, PaneContent.Vectorscope, PaneContent.Parade,
                },
                legacyPanels: Array.Empty<ScopePanelKind>());

            Check(layout.Panes.Count == 4, $"四分割给出 {layout.Panes.Count} 个格子");
            Check(layout.HasPicture, "布局里包含画面格");
            Check(layout.VisibleScopeKinds.Count == 3, $"布局用到 {layout.VisibleScopeKinds.Count} 种示波器（波形/矢量/Parade）");

            double boxWidth = (double)width / height;
            foreach (var pane in layout.Panes)
            {
                if (pane.Plot is { } plot)
                {
                    bool inside = plot.MinX >= pane.Panel.MinX - 1e-9 && plot.MaxX <= pane.Panel.MaxX + 1e-9
                               && plot.MinY >= pane.Panel.MinY - 1e-9 && plot.MaxY <= pane.Panel.MaxY + 1e-9;
                    Check(inside, $"格子 {pane.Slot}（{pane.Content}）的绘图区在格子内 {plot} ⊆ {pane.Panel}");

                    if (pane.Content.NeedsSquarePlot())
                    {
                        // 单位空间里 x 被容器宽高比缩过，所以「正方形」要在盒空间里判：width × boxWidth == height
                        double boxedWidth = plot.Width * boxWidth;
                        Check(Math.Abs(boxedWidth - plot.Height) < 1e-6,
                              $"格子 {pane.Slot}（{pane.Content}）绘图区是正方形：盒空间 {boxedWidth:0.#####} × {plot.Height:0.#####}");
                    }
                }
                if (pane.Video is { } video)
                {
                    bool inside = video.MinX >= pane.Panel.MinX - 1e-9 && video.MaxX <= pane.Panel.MaxX + 1e-9
                               && video.MinY >= pane.Panel.MinY - 1e-9 && video.MaxY <= pane.Panel.MaxY + 1e-9;
                    Check(inside, $"格子 {pane.Slot} 的画面区在格子内 {video} ⊆ {pane.Panel}");
                }
            }

            // 格子之间不许重叠（四分割最容易出的错就是「内容跑到别的框里」）
            bool overlaps = false;
            for (int i = 0; i < layout.Panes.Count; i++)
            {
                for (int j = i + 1; j < layout.Panes.Count; j++)
                {
                    var a = layout.Panes[i].Panel;
                    var b = layout.Panes[j].Panel;
                    var hit = a.Intersect(b);
                    if (hit.Width > 1e-6 && hit.Height > 1e-6)
                    {
                        overlaps = true;
                        Console.WriteLine($"      格子 {i} 与 {j} 重叠 {hit}");
                    }
                }
            }
            Check(!overlaps, "四分割的格子两两不重叠");

            // ---------- 5) 合成渲染并核对 ----------
            Console.WriteLine();
            Console.WriteLine("合成渲染断言：");
            using var composite = d3d.CreateRenderTarget(width, height);
            using var compositeRtv = d3d.Device.CreateRenderTargetView(composite);
            using var renderer = new VideoRenderer(d3d.Device, pipelines)
            {
                Layout = layout,
            };

            var options = new RenderOptions { WaveformMode = WaveformMode.Luma, VectorscopeGain = 1.0 };
            renderer.Render(d3d.Context, compositeRtv, width, height, sourceSrv, engine, options);
            d3d.Context.Flush();
            d3d.SavePng(composite, Path.Combine(outDirectory, "composite-quad.png"));

            var image = d3d.ReadBackRgba8(composite, out int cw, out int ch);

            // 画面格：应当能看到彩条（非黑）
            var picturePane = layout.Panes[0];
            Check(picturePane.Video is not null, "画面格有画面区");
            if (picturePane.Video is { } pv)
            {
                int px = (int)((pv.MinX + pv.Width / 7 * 0.5) * cw);
                int py = (int)((pv.MinY + pv.Height * 0.25) * ch);
                byte r = image[(py * cw + px) * 4];
                byte g = image[(py * cw + px) * 4 + 1];
                byte b = image[(py * cw + px) * 4 + 2];
                Check(r > 60 && g > 60 && b > 60, $"画面格左上角是 75% 白条：({r},{g},{b})");
            }

            // 波形格：191 IRE 那一行必须有轨迹
            var waveformPane = layout.Panes[1];
            Check(waveformPane.Plot is not null, "波形格有绘图区");
            if (waveformPane.Plot is { } wp)
            {
                int traceRow = (int)((wp.MinY + wp.Height * (rowForWhite / 256.0)) * ch);
                int quietRow = (int)((wp.MinY + wp.Height * (128 / 256.0)) * ch);   // 约 127 bin，彩条不在这
                int left = (int)(wp.MinX * cw) + 4;
                int right = (int)(wp.MaxX * cw) - 4;

                // 判据：只看**白条那 1/7 列**的纵向剖面 —— 在白条占的列里，
                // 亮度最高的一行就是 191 IRE（白条在那几列里有 720 行、亮度统一，
                // 比它自己的蓝条段 360 行更亮），所以 argmax 应该正好落在期望行上。
                int whiteLeft = left + 4;
                int whiteRight = left + (right - left) / 7 - 4;
                int bestRow = -1;
                double bestValue = -1;
                for (int y = (int)(wp.MinY * ch); y < (int)(wp.MaxY * ch); y++)
                {
                    double value = RowAverageRgba(image, cw, y, whiteLeft, whiteRight);
                    if (value > bestValue)
                    {
                        bestValue = value;
                        bestRow = y;
                    }
                }
                Check(Math.Abs(bestRow - traceRow) <= 4,
                      $"白条那几列的纵向剖面峰值在 y={bestRow}（均值 {bestValue:0.0}），" +
                      $"期望 191 IRE 的 y={traceRow}（±4 px）");

                int litAtTrace = CountAbove(image, cw, bestRow, left, right, 90);
                int expectedColumns = (int)((right - left) / 7.0);
                Check(litAtTrace >= expectedColumns * 0.6,
                      $"该行有 {litAtTrace} 个亮列，白条占 1/7 宽度（至少应有 {expectedColumns * 0.6:0}）");

                double tracePeak = RowMaxRgba(image, cw, traceRow, left, right);
                double quietPeak = RowMaxRgba(image, cw, quietRow, left, right);
                Check(tracePeak > quietPeak, $"191 IRE 行峰值 {tracePeak:0.0} 高于 128 bin 行 {quietPeak:0.0}");

            }

            // 矢量图格：应当有 7 个亮点
            var vectorPane = layout.Panes[2];
            Check(vectorPane.Plot is not null, "矢量图格有绘图区");
            if (vectorPane.Plot is { } vp)
            {
                int lit = 0;
                int x0 = (int)(vp.MinX * cw), x1 = (int)(vp.MaxX * cw);
                int y0 = (int)(vp.MinY * ch), y1 = (int)(vp.MaxY * ch);
                for (int y = y0; y < y1; y++)
                {
                    for (int x = x0; x < x1; x++)
                    {
                        int offset = (y * cw + x) * 4;
                        if (image[offset] + image[offset + 1] + image[offset + 2] > 150)
                        {
                            lit++;
                        }
                    }
                }
                Check(lit > 7, $"矢量图格里有 {lit} 个亮点像素（7 条彩条）");
            }

            // Parade 格：三列都要有轨迹
            var paradePane = layout.Panes[3];
            Check(paradePane.Plot is not null, "Parade 格有绘图区");
            if (paradePane.Plot is { } pp)
            {
                for (int channel = 0; channel < 3; channel++)
                {
                    double from = pp.MinX + pp.Width * channel / 3.0;
                    double to = pp.MinX + pp.Width * (channel + 1) / 3.0;
                    double value = 0;
                    for (int y = (int)(pp.MinY * ch); y < (int)(pp.MaxY * ch); y++)
                    {
                        value = Math.Max(value, RowAverageRgba(image, cw, y, (int)(from * cw) + 2, (int)(to * cw) - 2));
                    }
                    string[] names = { "R", "G", "B" };
                    Check(value > 8, $"Parade 第 {names[channel]} 列有轨迹（峰值行均值 {value:0.0}）");
                }
            }

            Console.WriteLine();
            Console.WriteLine("输出 PNG：");
            Save(engine, ScopePanelKind.Waveform, WaveformMode.Luma, d3d, "scope-waveform.png", outDirectory);
            Save(engine, ScopePanelKind.Waveform, WaveformMode.RgbOverlay, d3d, "scope-overlay.png", outDirectory);
            Save(engine, ScopePanelKind.Parade, WaveformMode.Luma, d3d, "scope-parade.png", outDirectory);
            Save(engine, ScopePanelKind.Vectorscope, WaveformMode.Luma, d3d, "scope-vectorscope.png", outDirectory);
            Save(engine, ScopePanelKind.Diamond, WaveformMode.Luma, d3d, "scope-diamond.png", outDirectory);
            Save(engine, ScopePanelKind.Cie, WaveformMode.Luma, d3d, "scope-cie.png", outDirectory);

            return Report();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("自检异常 ❌");
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    // ---------- 直方图索引（与 HLSL 里的 vsWaveIndex / vsVectorIndex 完全一致） ----------

    private static long PlaneTotal(uint[] histogram, int plane)
    {
        long total = 0;
        for (int bin = 0; bin < ShaderConstants.WaveformBins; bin++)
        {
            for (int column = 0; column < ShaderConstants.WaveformColumns; column++)
            {
                total += histogram[WaveIndex((uint)plane, (uint)bin, (uint)column)];
            }
        }
        return total;
    }

    /// <summary>把某一块二维直方图里最亮的若干个 bin 打出来（诊断用）</summary>
    private static void DumpSection(uint[] histogram, int section, string label)
    {
        long total = 0;
        var top = new List<(int X, int Y, uint Count)>();
        for (int y = 0; y < ShaderConstants.VectorscopeSize; y++)
        {
            for (int x = 0; x < ShaderConstants.VectorscopeSize; x++)
            {
                uint count = histogram[VectorIndex((uint)x, (uint)y, section)];
                if (count == 0)
                {
                    continue;
                }
                total += count;
                top.Add((x, y, count));
            }
        }

        top.Sort((a, b) => b.Count.CompareTo(a.Count));
        string topText = string.Join("  ", top.Take(8).Select(t => $"({t.X},{t.Y})={t.Count}"));
        Console.WriteLine($"    [{label}] 总样本 {total}，最亮 bin：{topText}");
    }

    private static int WaveIndex(uint plane, uint bin, uint column)
        => (int)(((plane * (uint)ShaderConstants.WaveformBins) + bin) * (uint)ShaderConstants.WaveformColumns + column);

    private static int VectorIndex(uint x, uint y, int section)
        => ShaderConstants.WaveformCount
         + section * ShaderConstants.VectorscopeCount
         + (int)(y * (uint)ShaderConstants.VectorscopeSize + x);

    // ---------- 工具 ----------

    private static double RowAverage(byte[] pixels, int width, int row, int fromX, int toX)
    {
        if (row < 0 || row * width * 4 >= pixels.Length)
        {
            return 0;
        }
        double sum = 0;
        int count = 0;
        for (int x = fromX; x < toX && x < width; x++)
        {
            sum += pixels[(row * width + x) * 4];
            count++;
        }
        return count > 0 ? sum / count : 0;
    }

    /// <summary>一行里的最大亮度（判轨迹有没有到位，比均值可靠）</summary>
    private static double RowMaxRgba(byte[] pixels, int width, int row, int fromX, int toX)
    {
        if (row < 0 || row * width * 4 >= pixels.Length)
        {
            return 0;
        }
        fromX = Math.Max(fromX, 0);
        toX = Math.Min(toX, width);
        double max = 0;
        for (int x = fromX; x < toX; x++)
        {
            int offset = (row * width + x) * 4;
            max = Math.Max(max, Math.Max(pixels[offset], Math.Max(pixels[offset + 1], pixels[offset + 2])));
        }
        return max;
    }

    /// <summary>一行里亮度超过阈值的像素个数</summary>
    private static int CountAbove(byte[] pixels, int width, int row, int fromX, int toX, double threshold)
    {
        if (row < 0 || row * width * 4 >= pixels.Length)
        {
            return 0;
        }
        fromX = Math.Max(fromX, 0);
        toX = Math.Min(toX, width);
        int count = 0;
        for (int x = fromX; x < toX; x++)
        {
            int offset = (row * width + x) * 4;
            double value = Math.Max(pixels[offset], Math.Max(pixels[offset + 1], pixels[offset + 2]));
            if (value > threshold)
            {
                count++;
            }
        }
        return count;
    }

    /// <summary>一行的平均亮度（RGB 三个通道都算，矢量图 / Parade 的亮点是彩色的）</summary>
    private static double RowAverageRgba(byte[] pixels, int width, int row, int fromX, int toX)
    {
        if (row < 0 || row * width * 4 >= pixels.Length)
        {
            return 0;
        }
        fromX = Math.Max(fromX, 0);
        toX = Math.Min(toX, width);
        if (toX <= fromX)
        {
            return 0;
        }

        double sum = 0;
        int count = 0;
        for (int x = fromX; x < toX; x++)
        {
            int offset = (row * width + x) * 4;
            sum += Math.Max(pixels[offset], Math.Max(pixels[offset + 1], pixels[offset + 2]));
            count++;
        }
        return count > 0 ? sum / count : 0;
    }

    private static void Save(ScopeEngine engine,
                             ScopePanelKind kind,
                             WaveformMode mode,
                             D3DContext d3d,
                             string fileName,
                             string outDirectory)
    {
        var texture = engine.TextureFor(kind, mode);
        string path = Path.Combine(outDirectory, fileName);
        d3d.SavePng(texture, path);
        var info = new FileInfo(path);
        Console.WriteLine($"  {fileName,-26} {texture.Description.Width}×{texture.Description.Height}  {info.Length,7} bytes");
    }

    private static void Check(bool condition, string message)
    {
        if (condition)
        {
            _passed++;
            Console.WriteLine($"  ✓ {message}");
        }
        else
        {
            _failed++;
            Console.WriteLine($"  ✗ {message}");
        }
    }

    private static void CheckPixel(byte[] pixels, int width, int height, int x, int y,
                                   int expectR, int expectG, int expectB, string label)
    {
        int offset = (y * width + x) * 4;
        byte r = pixels[offset], g = pixels[offset + 1], b = pixels[offset + 2];
        bool ok = Math.Abs(r - expectR) <= 2 && Math.Abs(g - expectG) <= 2 && Math.Abs(b - expectB) <= 2;
        Check(ok, $"{label,-16} ({x},{y}) = ({r},{g},{b}) 期望 ({expectR},{expectG},{expectB})");
    }

    private static int Report()
    {
        Console.WriteLine();
        Console.WriteLine($"=========== 自检结果：{_passed} 项通过，{_failed} 项失败 ===========");
        return _failed == 0 ? 0 : 1;
    }
}
