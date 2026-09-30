//
//  ScopeLayout.cs
//  VideoScopePad.Win
//
//  布局计算的 C# 移植，与 iPad 版 `Model/ScopeLayout.swift` **逐行对应**。
//
//  唯一的一份布局计算：D3D11 渲染（像素空间）与 WPF 刻度叠加（DIP 空间）共用。
//  所有输出矩形都在「归一化单位空间」：x/y ∈ 0...1、左上角为原点、y 轴向下。
//  只要两边的容器宽高比一致，结果就一致 —— 这是「轨迹与刻度严格对齐、任何尺寸都不跑偏」的根。
//

namespace VideoScopePad.Win.Core;

/// <summary>一个格子的布局</summary>
public sealed record PaneLayout
{
    public required PaneContent Content { get; init; }
    /// <summary>第几个格子（0 起，四分割时用于界面标识）</summary>
    public required int Slot { get; init; }
    /// <summary>整个格子</summary>
    public required RectF Panel { get; init; }
    /// <summary>画面显示区（Content == Picture 时有效）</summary>
    public RectF? Video { get; init; }
    /// <summary>画面纹理采样区（fit / fill 裁切）</summary>
    public RectF? VideoUV { get; init; }
    /// <summary>示波器轨迹区</summary>
    public RectF? Plot { get; init; }
    /// <summary>示波器刻度栏（波形 / Parade 才有）</summary>
    public RectF? Gutter { get; init; }
    /// <summary>画面顺时针旋转角度（0 / 90 / 180 / 270）</summary>
    public int Rotation { get; init; }

    /// <summary>旋转 90 / 270 度时，画面的宽高要对调</summary>
    public bool SwapsVideoAxes => Rotation is 90 or 270;
}

public sealed class ScopeLayoutResult
{
    public List<PaneLayout> Panes { get; set; } = new();
    /// <summary>示波器是否浮在画面上（半透明背景）</summary>
    public bool IsOverlay { get; set; }

    public bool HasPicture => Panes.Exists(p => p.Content == PaneContent.Picture);

    /// <summary>当前布局里实际用到的示波器种类（决定要统计哪些示波器）</summary>
    public IReadOnlyCollection<ScopePanelKind> VisibleScopeKinds
    {
        get
        {
            var kinds = new List<ScopePanelKind>();
            foreach (var pane in Panes)
            {
                if (pane.Content.ScopeKind() is { } kind && !kinds.Contains(kind))
                {
                    kinds.Add(kind);
                }
            }
            return kinds;
        }
    }

    public PaneLayout? Pane(int slot) => Panes.Find(p => p.Slot == slot);
}

public static class ScopeLayout
{
    /// <summary>
    /// 示波器纹理本身的尺寸（决定绘图区尺寸时**不再**按它内缩）：
    ///   亮度波形 = 512×256 = 2:1，RGB Parade = 1536×256 = 6:1，矢量 = 256×256 = 1:1。
    /// 只有矢量 / 钻石 / 马蹄必须 1:1；波形与 Parade 的纵向是标定过的幅度轴、横向只是取样位置，
    /// 所以绘图区铺满可用区域即可（纹理被拉伸不影响读数，刻度线与轨迹用同一个矩形）。
    /// </summary>
    public static double TextureAspect(PaneContent content) => content switch
    {
        PaneContent.Vectorscope or PaneContent.Diamond or PaneContent.Cie => 1.0,
        PaneContent.Waveform => 2.0,
        PaneContent.Parade => 6.0,
        PaneContent.Picture => 16.0 / 9.0,
        _ => 1.0,
    };

    /// <summary>
    /// 绘图区相对可用区域再缩一点，留呼吸空间。
    /// 矢量 / 钻石 / 马蹄必须正方形所以缩得多；波形 / Parade 的幅度轴已由刻度标定，
    /// 缩太多只会浪费格子，所以只留很小的边。
    /// </summary>
    public static double FillFactor(PaneContent content) => content switch
    {
        PaneContent.Vectorscope => 0.78,
        PaneContent.Diamond => 0.86,
        PaneContent.Cie => 0.94,
        PaneContent.Waveform => 0.97,
        PaneContent.Parade => 0.98,
        PaneContent.AudioPhase => 0.92,
        _ => 1.0,
    };

    /// <summary>该内容是否需要左侧刻度栏</summary>
    public static bool NeedsGutter(PaneContent content)
        => content is PaneContent.Waveform or PaneContent.Parade;

    // MARK: - 入口

    public static ScopeLayoutResult Compute(
        double containerWidth,
        double containerHeight,
        double videoWidth,
        double videoHeight,
        MonitorLayoutPreset preset,
        AspectMode aspectMode,
        PaneContent fullscreenContent,
        IReadOnlyList<PaneContent> quadContents,
        IReadOnlyList<ScopePanelKind> legacyPanels,
        PictureRotation pictureRotation = PictureRotation.None)
    {
        var result = new ScopeLayoutResult();

        if (containerWidth <= 1 || containerHeight <= 1)
        {
            return result;
        }

        // 归一化工作盒：宽 = 容器宽高比，高 = 1
        double boxWidth = containerWidth / containerHeight;
        var box = new RectF(0, 0, boxWidth, 1);

        // 画面旋转：自动模式下竖屏界面把画面转 90°（跟着设备方向走）
        int rotationDegrees = pictureRotation.ResolvedDegrees(containerIsPortrait: containerHeight > containerWidth);

        var effectivePreset = preset;
        // 竖屏下右侧栏太窄，退化成底部条
        if (effectivePreset == MonitorLayoutPreset.RightColumn && boxWidth < 1.05 && legacyPanels.Count > 0)
        {
            effectivePreset = MonitorLayoutPreset.BottomStrip;
        }

        double videoAspect = (videoWidth > 1 && videoHeight > 1) ? videoWidth / videoHeight : 16.0 / 9.0;

        RectF Normalize(RectF rect) => new(rect.MinX / boxWidth,
                                           rect.MinY,
                                           rect.Width / boxWidth,
                                           rect.Height);

        List<PaneLayout> NormalizeAll(List<PaneLayout> panes)
        {
            var list = new List<PaneLayout>(panes.Count);
            foreach (var pane in panes)
            {
                list.Add(pane with
                {
                    Panel = Normalize(pane.Panel),
                    Video = pane.Video is { } v ? Normalize(v) : null,
                    Plot = pane.Plot is { } p ? Normalize(p) : null,
                    Gutter = pane.Gutter is { } g ? Normalize(g) : null,
                });
            }
            return list;
        }

        switch (effectivePreset)
        {
            case MonitorLayoutPreset.Fullscreen:
                result.Panes = NormalizeAll(new List<PaneLayout>
                {
                    MakePane(fullscreenContent, 0, box, videoAspect, aspectMode, rotationDegrees),
                });
                break;

            case MonitorLayoutPreset.Quad:
            {
                double gap = box.Width * 0.004;
                double cellWidth = (box.Width - gap * 3) / 2;
                double cellHeight = (box.Height - gap * 3) / 2;

                var panes = new List<PaneLayout>();
                for (int slot = 0; slot < 4; slot++)
                {
                    int row = slot / 2;
                    int column = slot % 2;
                    var panel = new RectF(box.MinX + gap * (column + 1) + cellWidth * column,
                                          box.MinY + gap * (row + 1) + cellHeight * row,
                                          cellWidth,
                                          cellHeight);
                    var content = slot < quadContents.Count ? quadContents[slot] : PaneContent.Picture;
                    panes.Add(MakePane(content, slot, panel, videoAspect, aspectMode, rotationDegrees));
                }
                result.Panes = NormalizeAll(panes);
                break;
            }

            case MonitorLayoutPreset.BottomStrip:
            {
                var strip = StripPanels(box, legacyPanels);
                double stripHeight = 0;
                foreach (var pane in strip)
                {
                    stripHeight = Math.Max(stripHeight, pane.Panel.Height);
                }

                var monitorBox = new RectF(box.MinX, box.MinY,
                                           box.Width,
                                           Math.Max(box.Height - stripHeight, box.Height * 0.35));
                var panes = new List<PaneLayout>
                {
                    MakePane(PaneContent.Picture, 0, monitorBox, videoAspect, aspectMode, rotationDegrees),
                };
                panes.AddRange(strip);
                result.Panes = NormalizeAll(panes);
                break;
            }

            case MonitorLayoutPreset.RightColumn:
            {
                double columnWidth = Math.Min(Math.Max(box.Width * 0.32, 0.42), box.Width * 0.5);
                var column = new RectF(box.MaxX - columnWidth, box.MinY, columnWidth, box.Height);
                var monitorBox = new RectF(box.MinX, box.MinY,
                                           Math.Max(box.Width - columnWidth, box.Width * 0.35),
                                           box.Height);

                var panes = new List<PaneLayout>
                {
                    MakePane(PaneContent.Picture, 0, monitorBox, videoAspect, aspectMode, rotationDegrees),
                };

                int count = Math.Max(legacyPanels.Count, 1);
                double panelHeight = box.Height / count;
                for (int index = 0; index < legacyPanels.Count; index++)
                {
                    var panel = new RectF(column.MinX + columnWidth * 0.05,
                                          column.MinY + index * panelHeight + box.Height * 0.01,
                                          columnWidth * 0.9,
                                          panelHeight - box.Height * 0.02);
                    panes.Add(MakePane(ContentFor(legacyPanels[index]), index + 1, panel,
                                       videoAspect, aspectMode, rotationDegrees));
                }
                result.Panes = NormalizeAll(panes);
                break;
            }

            case MonitorLayoutPreset.Overlay:
            {
                result.IsOverlay = true;
                var panes = new List<PaneLayout>
                {
                    MakePane(PaneContent.Picture, 0, box, videoAspect, aspectMode, rotationDegrees),
                };
                panes.AddRange(StripPanels(box, legacyPanels));
                result.Panes = NormalizeAll(panes);
                break;
            }
        }

        return result;
    }

    // MARK: - 生成单个格子

    private static PaneLayout MakePane(PaneContent content,
                                       int slot,
                                       RectF panel,
                                       double videoAspect,
                                       AspectMode aspectMode,
                                       int rotation = 0)
    {
        if (content == PaneContent.Picture)
        {
            // 画面旋转 90 / 270 度时，显示区要按「对调后的宽高比」适配，
            // 否则竖屏下画面会被裁掉或留下大片黑边
            bool swapped = rotation is 90 or 270;
            double effectiveAspect = swapped ? 1.0 / Math.Max(videoAspect, 0.0001) : videoAspect;
            var video = FittedRect(effectiveAspect, panel, aspectMode);
            return new PaneLayout
            {
                Content = PaneContent.Picture,
                Slot = slot,
                Panel = panel,
                Video = video,
                VideoUV = UvRect(effectiveAspect, video, aspectMode),
                Rotation = rotation,
            };
        }

        // 内边距按「较短边」取，各种窗口尺寸的观感一致
        double pad = Math.Min(panel.Width, panel.Height) * 0.035;

        // 刻度栏宽度跟格子高度挂钩，同时不超过格子宽度的一定比例（窄格子里不至于挤掉绘图区）
        double gutterWidth = 0;
        if (NeedsGutter(content))
        {
            gutterWidth = Math.Min(Math.Max(panel.Height * 0.085, 0.015), panel.Width * 0.22);
        }

        // 可用空间（先扣掉刻度栏与内边距）
        double availableWidth = Math.Max(panel.Width - gutterWidth - pad * 2, 0.001);
        double availableHeight = Math.Max(panel.Height - pad * 2, 0.001);

        // 小格子上优先把刻度栏收窄，保证绘图区还有地方
        const double minPlotWidth = 0.03;
        if (gutterWidth > 0 && availableWidth < minPlotWidth)
        {
            double deficit = minPlotWidth - availableWidth;
            gutterWidth -= Math.Min(deficit, gutterWidth * 0.5);
            availableWidth = Math.Max(panel.Width - gutterWidth - pad * 2, 0.001);
        }

        double width = availableWidth;
        double height = availableHeight;
        if (content.NeedsSquarePlot())
        {
            double side = Math.Min(width, height);
            width = side;
            height = side;
        }

        // 再乘留白系数
        double fill = FillFactor(content);
        width *= fill;
        height *= fill;

        // 让「刻度栏 + 绘图区」整体在格子里水平居中（只让绘图区居中的话带刻度栏的格子会整体偏右）
        double groupWidth = gutterWidth + width;
        double groupMinX = panel.MidX - groupWidth / 2;
        var plot = new RectF(groupMinX + gutterWidth,
                             panel.MidY - height / 2,
                             width,
                             height);

        RectF? gutter = null;
        if (gutterWidth > 0)
        {
            gutter = new RectF(groupMinX,
                               panel.MinY + pad * 0.5,
                               gutterWidth,
                               Math.Max(panel.Height - pad, 0.001));
        }

        return new PaneLayout
        {
            Content = content,
            Slot = slot,
            Panel = panel,
            Plot = plot,
            Gutter = gutter,
        };
    }

    /// <summary>底部条：n 个示波器等宽并排</summary>
    private static List<PaneLayout> StripPanels(RectF box, IReadOnlyList<ScopePanelKind> panels)
    {
        var result = new List<PaneLayout>();
        if (panels.Count == 0)
        {
            return result;
        }

        double count = panels.Count;
        double gap = box.Width * 0.006;
        double panelWidth = (box.Width - gap * (count + 1)) / count;

        // 条带高度：示波器会铺满自己的格子，所以给一个与容器高度挂钩的合理值
        double stripHeight = Math.Min(Math.Max(box.Height * 0.30, box.Height * 0.22), box.Height * 0.50);

        for (int index = 0; index < panels.Count; index++)
        {
            var panel = new RectF(box.MinX + gap * (index + 1) + panelWidth * index,
                                  box.MaxY - stripHeight,
                                  panelWidth,
                                  stripHeight);
            result.Add(MakePane(ContentFor(panels[index]), index + 1, panel, 16.0 / 9.0, AspectMode.Fit));
        }
        return result;
    }

    private static PaneContent ContentFor(ScopePanelKind kind) => kind switch
    {
        ScopePanelKind.Vectorscope => PaneContent.Vectorscope,
        ScopePanelKind.Waveform => PaneContent.Waveform,
        ScopePanelKind.Parade => PaneContent.Parade,
        ScopePanelKind.Diamond => PaneContent.Diamond,
        ScopePanelKind.Cie => PaneContent.Cie,
        _ => PaneContent.Picture,
    };

    // MARK: - 辅助

    /// <summary>fit：完整显示；fill：铺满（矩形即容器，由 UV 裁切控制）</summary>
    private static RectF FittedRect(double aspect, RectF rect, AspectMode mode)
    {
        if (mode == AspectMode.Fill)
        {
            return rect;
        }

        double targetWidth = rect.Height * aspect;
        if (targetWidth <= rect.Width)
        {
            return new RectF(rect.MidX - targetWidth / 2, rect.MinY, targetWidth, rect.Height);
        }

        double height = rect.Width / aspect;
        return new RectF(rect.MinX, rect.MidY - height / 2, rect.Width, height);
    }

    /// <summary>计算纹理 UV 采样区域</summary>
    private static RectF UvRect(double videoAspect, RectF target, AspectMode mode)
    {
        if (mode == AspectMode.Fit)
        {
            return new RectF(0, 0, 1, 1);
        }

        double containerAspect = target.Width / Math.Max(target.Height, 1);
        if (videoAspect > containerAspect)
        {
            double width = containerAspect / videoAspect;
            return new RectF((1 - width) / 2, 0, width, 1);
        }
        else
        {
            double height = videoAspect / Math.Max(containerAspect, 0.0001);
            return new RectF(0, (1 - height) / 2, 1, height);
        }
    }
}
