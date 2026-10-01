//
//  ScopeGraticule.cs
//  VideoScopePad.App
//
//  示波器刻度层 —— 逐条移植 iPad 版 Views/ScopeGraticuleView.swift。
//
//  为什么刻度放在**界面层**而不是 GPU 里（与 iPad 版同一个理由）：
//    · 文字（IRE 数字、R/G/B、色标名）要清晰、要能随 DPI 缩放，WPF 里现成；
//    · 刻度不占 GPU 通道，示波器纹理只画轨迹；
//    · 与轨迹共用同一份 ScopeLayout（plot / gutter / panel 都是同一组矩形），所以严格对齐 ——
//      数字绝不会压在轨迹上。
//
//  ⚠️ 纵轴映射：**本工程的采集链在入口就把 limited(16–235) 展开成 full(0–255)**（见
//     Capture/YuvFrameConverter.cs），示波器直方图也是按展开后的码值分箱的，所以刻度必须用
//     「0 IRE = 码值 0、100 IRE = 码值 255」这条全范围映射（VideoRange = false）。
//     若照 iPad 版传 videoRange = true（0 IRE = 16、100 IRE = 235），所有刻度会整体偏 8%
//     —— 这是最容易搞错、而且一眼看不出来的地方，所以单独写一条断言钉住它。
//
//  ⚠️ 本文件画的所有东西都**只读布局**，不碰 D3D、不碰采集，所以可以离线用
//     DrawingVisual + RenderTargetBitmap 渲染进 PNG（「存一帧」就是这么带上刻度的）。
//

using System.Globalization;
using System.Windows;
using System.Windows.Media;
using VideoScopePad.Win.Core;
using VideoScopePad.Win.Render;

namespace VideoScopePad.App;

/// <summary>刻度层选项</summary>
public sealed record GraticuleOptions(
    ScaleUnit Unit = ScaleUnit.Ire,
    double VectorscopeGain = 1.0,
    bool VideoRange = false,
    string WaveformLabel = "Y",
    PeakHoldState? PeakHold = null,
    PeakHoldState? ReferencePeakHold = null,
    bool ShowPeakHold = true);

/// <summary>示波器刻度绘制（静态方法，画面与出图共用同一份）。</summary>
public static class ScopeGraticule
{
    private static readonly Typeface Monospace = new("Consolas");
    private static readonly Typeface Sans = new("Segoe UI");

    // 笔刷与画笔预先冻结：每帧重建 Brush 是 WPF 里很典型的隐形开销
    private static readonly Brush TextBrush = Frozen(new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)));
    private static readonly Brush TextDimBrush = Frozen(new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)));
    private static readonly Brush SkinBrush = Frozen(new SolidColorBrush(Color.FromArgb(140, 255, 149, 0)));
    private static readonly Pen ThinPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(46, 255, 255, 255))), 1));
    private static readonly Pen NormalPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(82, 255, 255, 255))), 1));
    private static readonly Pen StrongPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(133, 255, 255, 255))), 1));
    private static readonly Pen GridMajorPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(107, 255, 255, 255))), 1));
    private static readonly Pen GridMinorPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(36, 255, 255, 255))), 1));
    private static readonly Pen ColumnPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(87, 255, 255, 255))), 1));
    private static readonly Pen TargetPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(153, 255, 255, 255))), 1));
    private static readonly Pen BorderPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(77, 255, 255, 255))), 1));
    private static readonly Pen TickMajorPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(191, 255, 255, 255))), 1.5));
    private static readonly Pen TickMinorPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255))), 1));
    private static readonly Pen SkinPen = Frozen(new Pen(SkinBrush, 1.2) { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) });

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    /// <summary>
    /// 把整层刻度画到 <paramref name="dc"/>。
    /// <paramref name="imageRect"/> = 合成画面在目标上的实际矩形（等比适配后可能有留白），
    /// 布局里的单位空间矩形都相对它换算 —— 这样刻度与画面永远严丝合缝。
    /// </summary>
    public static void Draw(DrawingContext dc, ScopeLayoutResult layout, Rect imageRect, GraticuleOptions options)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (imageRect.Width < 16 || imageRect.Height < 16)
        {
            return;
        }

        foreach (PaneLayout pane in layout.Panes)
        {
            if (pane.Content.ScopeKind() is not { } kind || pane.Plot is not { } plotUnit)
            {
                continue;
            }

            Rect plot = Map(plotUnit, imageRect);
            if (plot.Width <= 16 || plot.Height <= 16)
            {
                continue;
            }

            // 裁到绘图区（外扩 1.5 px，否则边框线会被裁掉一半、看起来「缺一条边」）
            Rect clip = plot;
            clip.Inflate(1.5, 1.5);
            dc.PushClip(new RectangleGeometry(clip));
            switch (kind)
            {
                case ScopePanelKind.Vectorscope:
                    DrawVectorscope(dc, plot, options.VectorscopeGain);
                    break;

                case ScopePanelKind.Waveform:
                    DrawWaveform(dc, plot, columns: 1, options,
                                 label: options.WaveformLabel);
                    DrawPeakHold(dc, plot, options);
                    break;

                case ScopePanelKind.Parade:
                    DrawWaveform(dc, plot, columns: 3, options, label: null);
                    DrawPeakHold(dc, plot, options);
                    break;

                case ScopePanelKind.Diamond:
                    DrawDiamond(dc, plot);
                    break;

                case ScopePanelKind.Cie:
                    DrawChromaticity(dc, plot);
                    break;

                // 钻石图 / 马蹄图：现在界面里的四分割用不到它们（ShowDiamond/ShowCie 还没接界面开关），
                // 等格子内容可选时再按 ScopeGraticuleView.swift 的 drawDiamond/drawChromaticity 移植。
            }
            dc.Pop();

            if (pane.Gutter is { } gutterUnit)
            {
                // 刻度栏整体裁在**格子**内：数字与刻度线绝不会跑到相邻格子里
                Rect panel = Map(pane.Panel, imageRect);
                dc.PushClip(new RectangleGeometry(panel));
                DrawGutter(dc, Map(gutterUnit, imageRect), plot, kind, options);
                dc.Pop();
                dc.PushClip(new RectangleGeometry(clip));
                if (kind == ScopePanelKind.Parade && plot.Width > 120)
                {
                    // 三列通道名标在绘图区底部（与 iPad 版一致）
                    double fontSize = Math.Max(Math.Min(plot.Height, plot.Width) * 0.03, 9);
                    double columnWidth = plot.Width / 3;
                    string[] names = { "R", "G", "B" };
                    for (int index = 0; index < 3; index++)
                    {
                        DrawText(dc, names[index], Sans, fontSize, TextDimBrush,
                                 new Point(plot.X + columnWidth * (index + 0.5), plot.Bottom - fontSize),
                                 TextAlign.Center, bold: true);
                    }
                }
                dc.Pop();
            }
        }
    }

    // ------------------------------------------------------------------
    //  侧边刻度栏
    // ------------------------------------------------------------------
    private static void DrawGutter(DrawingContext dc, Rect gutter, Rect plot, ScopePanelKind kind, GraticuleOptions options)
    {
        if (gutter.Width <= 10 || gutter.Height <= 18)
        {
            return;
        }

        ScaleUnit unit = options.Unit;
        IReadOnlyList<double> values = unit.TickValues();

        // 字号跟刻度栏宽度走：格子小就自动变小；太窄就只留刻度线不写数字
        double fontSize = Math.Clamp(gutter.Width * 0.30, 7, 12);
        bool showNumbers = gutter.Width >= 22;
        double tickRight = gutter.Right - 3;

        if (gutter.Width >= 30)
        {
            DrawText(dc, unit.ShortTitle(), Sans, Math.Min(fontSize, 11), TextBrush,
                     new Point(gutter.X + gutter.Width / 2, gutter.Y + fontSize), TextAlign.Center, bold: true);
        }

        foreach (double value in values)
        {
            double ire = unit.ToIre(value);
            double y = YPositionForIre(ire, plot, options.VideoRange);
            if (y < plot.Y - 1 || y > plot.Bottom + 1)
            {
                continue;
            }

            bool major = unit.IsMajorTick(value);
            double length = major ? 9 : 5;
            dc.DrawLine(major ? TickMajorPen : TickMinorPen,
                        new Point(tickRight - length, y), new Point(tickRight, y));

            if (!showNumbers || !IsTickLabelVisible(value, unit, plot.Height))
            {
                continue;
            }

            DrawText(dc, unit.Format(value), Monospace, fontSize, TextBrush,
                     new Point(gutter.Right - Math.Max(fontSize * 0.5, 4), y), TextAlign.Right);
        }
    }

    /// <summary>绘图区越矮，标注越稀（避免数字互相叠住）——与 iPad 版同一套规则</summary>
    private static bool IsTickLabelVisible(double value, ScaleUnit unit, double plotHeight)
    {
        if (plotHeight >= 240)
        {
            return true;
        }
        if (plotHeight >= 130)
        {
            return unit.IsMajorTick(value);
        }

        IReadOnlyList<double> values = unit.TickValues();
        double first = values[0];
        double last = values[^1];
        double middle = values[values.Count / 2];
        return value == first || value == middle || value == last;
    }

    // ------------------------------------------------------------------
    //  波形 / Parade 的横线网格
    // ------------------------------------------------------------------
    private static void DrawWaveform(DrawingContext dc, Rect rect, int columns, GraticuleOptions options, string? label)
    {
        ScaleUnit unit = options.Unit;

        foreach (double value in unit.TickValues())
        {
            double ire = unit.ToIre(value);
            double y = YPositionForIre(ire, rect, options.VideoRange);
            if (y < rect.Y - 1 || y > rect.Bottom + 1)
            {
                continue;
            }
            dc.DrawLine(unit.IsMajorTick(value) ? GridMajorPen : GridMinorPen,
                        new Point(rect.X, y), new Point(rect.Right, y));
        }

        double columnWidth = rect.Width / Math.Max(columns, 1);

        if (columns > 1)
        {
            for (int index = 1; index < columns; index++)
            {
                double x = rect.X + columnWidth * index;
                dc.DrawLine(ColumnPen, new Point(x, rect.Y), new Point(x, rect.Bottom));
            }
        }

        // 每列内的 1/4、1/2、3/4 竖线（看取样位置用）
        foreach (double fraction in new[] { 0.25, 0.5, 0.75 })
        {
            for (int column = 0; column < Math.Max(columns, 1); column++)
            {
                double x = rect.X + columnWidth * (column + fraction);
                dc.DrawLine(GridMinorPen, new Point(x, rect.Y), new Point(x, rect.Bottom));
            }
        }

        if (label is not null)
        {
            DrawText(dc, label, Sans, 11, TextBrush, new Point(rect.Right - 12, rect.Y + 11), TextAlign.Center, bold: true);
        }

        dc.DrawRectangle(null, BorderPen, rect);
    }

    // ------------------------------------------------------------------
    //  峰值保持游标（实时 + 冻结参考）
    // ------------------------------------------------------------------
    private static readonly Pen PeakWhitePen = Frozen(new Pen(
        Frozen(new SolidColorBrush(Color.FromArgb(230, 255, 210, 60))), 1.2)
    { DashStyle = new DashStyle(new double[] { 5, 3 }, 0) });

    private static readonly Pen BlackFloorPen = Frozen(new Pen(
        Frozen(new SolidColorBrush(Color.FromArgb(230, 80, 220, 235))), 1.2)
    { DashStyle = new DashStyle(new double[] { 5, 3 }, 0) });

    private static readonly Brush PeakWhiteBrush = Frozen(new SolidColorBrush(Color.FromArgb(240, 255, 210, 60)));
    private static readonly Brush BlackFloorBrush = Frozen(new SolidColorBrush(Color.FromArgb(240, 80, 220, 235)));

    /// <summary>参考游标用琥珀色细虚线（比实时的细、且错开一点位置，避免两条叠住看不清）</summary>
    private static readonly Pen ReferencePen = Frozen(new Pen(
        Frozen(new SolidColorBrush(Color.FromArgb(240, 255, 148, 31))), 1.0)
    { DashStyle = new DashStyle(new double[] { 2, 3 }, 0) });

    private static readonly Brush ReferenceBrush = Frozen(new SolidColorBrush(Color.FromArgb(240, 255, 148, 31)));

    /// <summary>
    /// 把保持住的最高 / 最低电平用虚线钉在波形上（与 iPad 版 drawPeakHold 同一套做法）：
    /// 实时用黄（峰值）与青（黑位），参考层用琥珀色细线并往中间错一点，两条并排对照。
    /// </summary>
    private static void DrawPeakHold(DrawingContext dc, Rect rect, GraticuleOptions options)
    {
        if (!options.ShowPeakHold)
        {
            return;
        }

        ScaleUnit unit = options.Unit;
        bool narrow = rect.Width < 170;

        if (options.PeakHold is { HasData: true } live)
        {
            DrawCursor(dc, rect, live.WhitePeakIre, unit, options.VideoRange, PeakWhitePen, PeakWhiteBrush, "峰值", narrow);
            DrawCursor(dc, rect, live.BlackFloorIre, unit, options.VideoRange, BlackFloorPen, BlackFloorBrush, "黑位", narrow);
        }

        if (options.ReferencePeakHold is { HasData: true } reference)
        {
            DrawCursor(dc, rect, reference.WhitePeakIre, unit, options.VideoRange, ReferencePen, ReferenceBrush,
                       "参考峰", narrow, labelOffsetY: 11);
            DrawCursor(dc, rect, reference.BlackFloorIre, unit, options.VideoRange, ReferencePen, ReferenceBrush,
                       "参考黑", narrow, labelOffsetY: -11);
        }
    }

    private static void DrawCursor(DrawingContext dc, Rect rect, double ire, ScaleUnit unit, bool videoRange,
                                   Pen pen, Brush brush, string name, bool narrow, double labelOffsetY = -11)
    {
        double y = YPositionForIre(ire, rect, videoRange);
        if (y < rect.Y - 1 || y > rect.Bottom + 1)
        {
            return;
        }

        dc.DrawLine(pen, new Point(rect.X, y), new Point(rect.Right, y));

        // 标注放在放得下的一侧（绘图区窄的时候放右边会被裁掉）
        DrawText(dc, $"{name} {unit.FormatPrecise(ire)}", Monospace, 11, brush,
                 new Point(narrow ? rect.X + 6 : rect.Right - 6, y + labelOffsetY),
                 narrow ? TextAlign.Left : TextAlign.Right);
    }

    // ------------------------------------------------------------------
    //  矢量示波器
    // ------------------------------------------------------------------
    private static void DrawVectorscope(DrawingContext dc, Rect rect, double gain)
    {
        var center = new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
        double radius = Math.Min(rect.Width, rect.Height) / 2;
        double g = Math.Max(gain, 0.25);

        double fontSize = Math.Clamp(radius / 9, 6.5, 11);
        bool showRingLabels = radius >= 70;

        foreach (double fraction in new[] { 0.25, 0.5, 0.75, 1.0 })
        {
            double r = radius * fraction * g;
            // 放大时外圈会超出绘图区：**整圈不画**，免得只画出半圈像图形缺失
            if (r > radius + 0.5)
            {
                continue;
            }
            dc.DrawEllipse(null, Math.Abs(fraction - 0.75) < 1e-9 ? StrongPen : ThinPen,
                           center, r, r);

            if (showRingLabels && r <= radius - fontSize * 1.4)
            {
                DrawText(dc, $"{fraction * 100:0}%", Monospace, fontSize, TextDimBrush,
                         new Point(center.X + fontSize * 0.9, center.Y - r), TextAlign.Left);
            }
        }

        dc.DrawLine(NormalPen, new Point(rect.X, center.Y), new Point(rect.Right, center.Y));
        dc.DrawLine(NormalPen, new Point(center.X, rect.Y), new Point(center.X, rect.Bottom));

        // 75% 彩条目标框（整框落在绘图区内才画）
        foreach ((string name, double cb, double cr) in ColorTargets75)
        {
            double x = center.X + cb / 0.5 * radius * g;
            double y = center.Y - cr / 0.5 * radius * g;
            double box = Math.Max(radius * 0.045, 4);
            var boxRect = new Rect(x - box, y - box, box * 2, box * 2);
            if (!rect.Contains(boxRect))
            {
                continue;
            }

            dc.DrawRectangle(null, TargetPen, boxRect);
            if (radius >= 60)
            {
                bool placeRight = x + box + 3 + fontSize * 3.2 <= rect.Right;
                DrawText(dc, name, Sans, fontSize, TextBrush,
                         new Point(placeRight ? x + box + 3 : x - box - 3, y - fontSize * 0.6),
                         placeRight ? TextAlign.Left : TextAlign.Right, bold: true);
            }
        }

        // 肤色线（I 轴约 123°，广播标准刻度），长度裁到不出绘图区
        if (radius >= 60)
        {
            double angle = 123.0 * Math.PI / 180.0;
            double dx = Math.Cos(angle);
            double dy = -Math.Sin(angle);
            double reach = MaxLength(center, dx, dy, rect);
            double length = Math.Min(radius * g * 0.9, reach * 0.96);
            if (length > fontSize * 2)
            {
                dc.DrawLine(SkinPen, center, new Point(center.X + dx * length, center.Y + dy * length));
                DrawText(dc, "肤色", Sans, fontSize, SkinBrush,
                         new Point(center.X + dx * length * 0.8, center.Y + dy * length * 0.8), TextAlign.Left, bold: true);
            }
        }

        if (radius >= 80)
        {
            DrawText(dc, "B-Y", Sans, fontSize, TextDimBrush,
                     new Point(rect.Right - fontSize * 1.8, center.Y - fontSize * 0.9), TextAlign.Center, bold: true);
            DrawText(dc, "R-Y", Sans, fontSize, TextDimBrush,
                     new Point(center.X + fontSize * 2.2, rect.Y + fontSize), TextAlign.Center, bold: true);
        }
    }

    /// <summary>从矩形中心沿 (dx, dy) 到边界的最大距离（把参考线裁在绘图区内）</summary>
    private static double MaxLength(Point center, double dx, double dy, Rect rect)
    {
        double limit = double.MaxValue;
        if (dx > 0.0001)
        {
            limit = Math.Min(limit, (rect.Right - center.X) / dx);
        }
        if (dx < -0.0001)
        {
            limit = Math.Min(limit, (rect.X - center.X) / dx);
        }
        if (dy > 0.0001)
        {
            limit = Math.Min(limit, (rect.Bottom - center.Y) / dy);
        }
        if (dy < -0.0001)
        {
            limit = Math.Min(limit, (rect.Y - center.Y) / dy);
        }
        return Math.Max(limit, 0);
    }

    // ------------------------------------------------------------------
    //  钻石图（RGB 色域，泰克原版）
    // ------------------------------------------------------------------
    /// <summary>
    /// 钻石图刻度（依据 Tektronix 应用手册《Color Grading with the Spearhead Display》第 2 节，
    /// 与 iPad 版 drawDiamond 逐条一致）：
    ///   上菱形 = G（左轴）与 B（右轴）；下菱形 = G（左轴）与 R（右轴）；
    ///   纯黑在两菱形交会的**中心**，纯白在上菱形顶端 / 下菱形底端；
    ///   灰阶是正中的竖线，中灰落在两个菱形最宽处的中心。
    /// 显示坐标 x、y ∈ −1…1（y 向上，原点即纯黑）；分量超出 0–100% 的点会跑出菱形 —— 那就是色域越界。
    /// </summary>
    private static void DrawDiamond(DrawingContext dc, Rect rect)
    {
        double fontSize = Math.Clamp(Math.Min(rect.Width, rect.Height) / 24, 7, 11);

        Point Point(double x, double y)
            => new(rect.X + rect.Width / 2 + x * rect.Width / 2,
                   rect.Y + rect.Height / 2 - y * rect.Height / 2);

        // 以原点（纯黑）为中心把菱形缩放 level 倍：0.25/0.5/0.75 就是「两分量之和」的等值线
        void DiamondPath(double level, bool up, Pen pen)
        {
            (double X, double Y)[] vertices = up
                ? new[] { (0.0, 0.0), (-level, level * 0.5), (0.0, level), (level, level * 0.5) }
                : new[] { (0.0, 0.0), (-level, -level * 0.5), (0.0, -level), (level, -level * 0.5) };

            var geometry = new StreamGeometry();
            using (StreamGeometryContext ctx = geometry.Open())
            {
                ctx.BeginFigure(Point(vertices[0].X, vertices[0].Y), isFilled: false, isClosed: true);
                for (int i = 1; i < vertices.Length; i++)
                {
                    ctx.LineTo(Point(vertices[i].X, vertices[i].Y), isStroked: true, isSmoothJoin: false);
                }
            }
            geometry.Freeze();
            dc.DrawGeometry(null, pen, geometry);
        }

        // 内部等值线（更淡）→ 75% 彩条边界 → 100% 合法边界（最亮）
        foreach (double level in new[] { 0.25, 0.5 })
        {
            DiamondPath(level, true, GridMinorPen);
            DiamondPath(level, false, GridMinorPen);
        }
        DiamondPath(0.75, true, ThinPen);
        DiamondPath(0.75, false, ThinPen);
        DiamondPath(1.0, true, StrongPen);
        DiamondPath(1.0, false, StrongPen);

        // 正中竖线 = 灰阶轴；两条水平线 = 两个菱形最宽处（中灰所在）
        dc.DrawLine(NormalPen, Point(0, 1), Point(0, -1));
        dc.DrawLine(NormalPen, Point(-1, 0.5), Point(1, 0.5));
        dc.DrawLine(NormalPen, Point(-1, -0.5), Point(1, -0.5));

        // 中心 = 纯黑
        double dot = Math.Max(fontSize * 0.28, 2);
        Point center = Point(0, 0);
        dc.DrawEllipse(null, StrongPen, center, dot, dot);

        // 标注：绿色在两个菱形的左侧，B 在右上、R 在右下，W 在上下顶端
        (string Name, double X, double Y, TextAlign Align)[] labels =
        {
            ("W", 0, 1.07, TextAlign.Center),
            ("B", 1.04, 0.5, TextAlign.Left),
            ("G", -1.04, 0.5, TextAlign.Right),
            ("W", 0, -1.07, TextAlign.Center),
            ("R", 1.04, -0.5, TextAlign.Left),
            ("G", -1.04, -0.5, TextAlign.Right),
        };
        foreach ((string name, double x, double y, TextAlign align) in labels)
        {
            DrawText(dc, name, Sans, fontSize, TextBrush, Point(x, y), align, bold: true);
        }

        DrawText(dc, "黑", Sans, fontSize * 0.85, TextDimBrush,
                 new Point(center.X + fontSize * 0.5, center.Y + fontSize * 0.9), TextAlign.Left);
        DrawText(dc, "灰阶", Sans, fontSize * 0.85, TextDimBrush,
                 new Point(center.X + fontSize * 0.5, rect.Y + fontSize * 1.1), TextAlign.Left);
    }

    // ------------------------------------------------------------------
    //  马蹄图（CIE 1931 色度）
    // ------------------------------------------------------------------
    /// <summary>
    /// CIE 刻度：光谱轨迹（马蹄形）+ BT.709 / BT.2020 色域三角 + D65 白点。
    /// 坐标映射与着色器完全一致（用 ShaderConstants 里的 VS_CIE_ORIGIN_* / VS_CIE_SPAN）。
    /// </summary>
    private static void DrawChromaticity(DrawingContext dc, Rect rect)
    {
        double fontSize = Math.Clamp(Math.Min(rect.Width, rect.Height) / 26, 7, 11);
        double span = ShaderConstants.CieSpan;
        double originX = ShaderConstants.CieOriginX;
        double originY = ShaderConstants.CieOriginY;

        Point Point(double x, double y)
        {
            double nx = (x + originX) / span;
            double ny = (y + originY) / span;
            return new Point(rect.X + nx * rect.Width, rect.Bottom - ny * rect.Height);
        }

        // 坐标框 + 0.1 网格
        dc.DrawRectangle(null, NormalPen, rect);
        for (double step = 0.1; step < 0.9; step += 0.1)
        {
            dc.DrawLine(GridMinorPen, Point(step, -originY), Point(step, -originY + span));
            dc.DrawLine(GridMinorPen, Point(-originX, step), Point(-originX + span, step));
        }

        // 光谱轨迹（380–700nm，5nm 步长的 CIE 1931 2° 标准观察者数据；末尾闭合补上紫边）
        var locus = new StreamGeometry();
        using (StreamGeometryContext ctx = locus.Open())
        {
            Point first = Point(SpectralLocus[0].X, SpectralLocus[0].Y);
            ctx.BeginFigure(first, isFilled: false, isClosed: true);
            for (int i = 1; i < SpectralLocus.Length; i++)
            {
                ctx.LineTo(Point(SpectralLocus[i].X, SpectralLocus[i].Y), isStroked: true, isSmoothJoin: false);
            }
        }
        locus.Freeze();
        dc.DrawGeometry(null, ThinPen, locus);

        void Triangle(string name, (double X, double Y) r, (double X, double Y) g, (double X, double Y) b, double opacity)
        {
            var pen = new Pen(new SolidColorBrush(Color.FromArgb((byte)(opacity * 255), 255, 255, 255)), 1.2);
            pen.Freeze();
            var geometry = new StreamGeometry();
            using (StreamGeometryContext ctx = geometry.Open())
            {
                ctx.BeginFigure(Point(r.X, r.Y), isFilled: false, isClosed: true);
                ctx.LineTo(Point(g.X, g.Y), isStroked: true, isSmoothJoin: false);
                ctx.LineTo(Point(b.X, b.Y), isStroked: true, isSmoothJoin: false);
            }
            geometry.Freeze();
            dc.DrawGeometry(null, pen, geometry);

            Point center = Point((r.X + g.X + b.X) / 3, (r.Y + g.Y + b.Y) / 3);
            DrawText(dc, name, Sans, fontSize * 0.9, TextDimBrush,
                     new Point(center.X, center.Y + fontSize * 0.7), TextAlign.Center, bold: true);
        }

        Triangle("BT.709", (0.640, 0.330), (0.300, 0.600), (0.150, 0.060), 0.42);
        Triangle("BT.2020", (0.708, 0.292), (0.170, 0.797), (0.131, 0.046), 0.22);

        // D65 白点
        Point white = Point(0.3127, 0.3290);
        double radius = Math.Max(fontSize * 0.4, 3);
        dc.DrawEllipse(null, StrongPen, white, radius, radius);
        DrawText(dc, "D65", Sans, fontSize, TextDimBrush,
                 new Point(white.X + radius + 2, white.Y - fontSize * 0.6), TextAlign.Left);

        DrawText(dc, "x", Sans, fontSize, TextDimBrush,
                 new Point(rect.Right - fontSize * 1.6, rect.Bottom - fontSize * 1.1), TextAlign.Center, bold: true);
        DrawText(dc, "y", Sans, fontSize, TextDimBrush,
                 new Point(rect.X + fontSize * 1.4, rect.Y + fontSize * 1.1), TextAlign.Center, bold: true);
    }

    /// <summary>CIE 1931 2° 光谱轨迹（380–700nm，5nm 步长）—— 与 iPad 版 spectralLocus 逐点一致</summary>
    private static readonly (double X, double Y)[] SpectralLocus =
    {
        (0.1741, 0.0050), (0.1740, 0.0050), (0.1738, 0.0049), (0.1736, 0.0049),
        (0.1733, 0.0048), (0.1730, 0.0048), (0.1726, 0.0048), (0.1721, 0.0048),
        (0.1714, 0.0051), (0.1703, 0.0058), (0.1689, 0.0069), (0.1669, 0.0086),
        (0.1644, 0.0109), (0.1611, 0.0138), (0.1566, 0.0177), (0.1510, 0.0227),
        (0.1440, 0.0297), (0.1355, 0.0399), (0.1241, 0.0578), (0.1096, 0.0868),
        (0.0913, 0.1327), (0.0687, 0.2007), (0.0454, 0.2950), (0.0235, 0.4127),
        (0.0082, 0.5384), (0.0039, 0.6548), (0.0139, 0.7502), (0.0389, 0.8120),
        (0.0743, 0.8338), (0.1142, 0.8262), (0.1547, 0.8059), (0.1929, 0.7816),
        (0.2292, 0.7543), (0.2658, 0.7243), (0.3016, 0.6923), (0.3373, 0.6589),
        (0.3731, 0.6245), (0.4087, 0.5896), (0.4441, 0.5547), (0.4788, 0.5202),
        (0.5125, 0.4866), (0.5448, 0.4544), (0.5752, 0.4242), (0.6029, 0.3965),
        (0.6270, 0.3725), (0.6482, 0.3514), (0.6658, 0.3340), (0.6801, 0.3197),
        (0.6915, 0.3083), (0.7006, 0.2993), (0.7079, 0.2920), (0.7140, 0.2859),
        (0.7190, 0.2809), (0.7230, 0.2770), (0.7260, 0.2740), (0.7283, 0.2717),
        (0.7300, 0.2700), (0.7311, 0.2689), (0.7320, 0.2680), (0.7327, 0.2673),
        (0.7334, 0.2666), (0.7340, 0.2660), (0.7344, 0.2656), (0.7346, 0.2654),
        (0.7347, 0.2653),
    };

    // ------------------------------------------------------------------
    //  单位换算与坐标
    // ------------------------------------------------------------------

    /// <summary>
    /// IRE → 纵向像素。
    /// 全范围（本工程的常态）：0 IRE = 码值 0、100 IRE = 码值 255。
    /// 视频范围（VideoRange = true）：0 IRE = 码值 16、100 IRE = 码值 235。
    /// </summary>
    public static double YPositionForIre(double ire, Rect rect, bool videoRange)
    {
        double code = videoRange ? 16 + ire / 100 * 219 : ire / 100 * 255;
        return rect.Bottom - code / 255.0 * rect.Height;
    }

    /// <summary>布局里的单位空间矩形 → 目标上的像素矩形</summary>
    private static Rect Map(RectF unitRect, Rect imageRect)
        => new(imageRect.X + unitRect.MinX * imageRect.Width,
               imageRect.Y + unitRect.MinY * imageRect.Height,
               unitRect.Width * imageRect.Width,
               unitRect.Height * imageRect.Height);

    /// <summary>
    /// 75% 彩条在 (Cb, Cr) 平面上的位置（BT.709 系数）——
    /// 与 iPad 版 ScopeGraticuleView.colorTargets75 逐字一致，也与示波器着色器里的
    /// 分箱公式同源（自检里那 7 个落点 bin 就是这么算出来的）。
    /// </summary>
    public static readonly (string Name, double Cb, double Cr)[] ColorTargets75 = BuildColorTargets();

    private static (string, double, double)[] BuildColorTargets()
    {
        const double level = 0.75;
        static (double cb, double cr) Chroma(double r, double g, double b)
            => (-0.114572 * r - 0.385428 * g + 0.5 * b,
                 0.5 * r - 0.454153 * g - 0.045847 * b);

        return new[]
        {
            ("R", Chroma(level, 0, 0)),
            ("Mg", Chroma(level, 0, level)),
            ("B", Chroma(0, 0, level)),
            ("Cy", Chroma(0, level, level)),
            ("G", Chroma(0, level, 0)),
            ("Yl", Chroma(level, level, 0)),
        }.Select(item => (item.Item1, item.Item2.cb, item.Item2.cr)).ToArray();
    }

    // ------------------------------------------------------------------
    //  文本
    // ------------------------------------------------------------------
    private enum TextAlign
    {
        Left,
        Center,
        Right,
    }

    private static void DrawText(DrawingContext dc, string text, Typeface typeface, double fontSize,
                                 Brush brush, Point anchor, TextAlign align, bool bold = false)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            bold ? new Typeface(typeface.FontFamily, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal) : typeface,
            fontSize,
            brush,
            1.0);   // pixelsPerDip：这个层是像素对齐的（合成分辨率），不是 DPI 缩放的界面

        double x = align switch
        {
            TextAlign.Center => anchor.X - formatted.Width / 2,
            TextAlign.Right => anchor.X - formatted.Width,
            _ => anchor.X,
        };
        double y = anchor.Y - formatted.Height / 2;
        dc.DrawText(formatted, new Point(x, y));
    }
}
