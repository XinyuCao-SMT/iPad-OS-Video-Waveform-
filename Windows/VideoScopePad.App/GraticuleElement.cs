//
//  GraticuleElement.cs
//  VideoScopePad.App
//
//  刻度层的承载元素：一个自绘的 FrameworkElement，铺在实时位图之上。
//  自绘（OnRender）而不是往 Canvas 里塞一堆 Shape —— 刻度每帧要重画几十条线，
//  Shape 会产生大量布局对象，自绘只是一次 DrawingContext 调用。
//
//  ⚠️ 画面是用 Stretch=Uniform 显示的，所以**实际画面矩形**（可能带留白）要自己算：
//     刻度必须按这个矩形换算，不能按控件大小 —— 否则窗口比例一变刻度就整体错位。
//

using System.Windows;
using System.Windows.Media;
using VideoScopePad.Win.Core;

namespace VideoScopePad.App;

public sealed class GraticuleElement : FrameworkElement
{
    private ScopeLayoutResult? _layout;
    private int _frameWidth = 16;
    private int _frameHeight = 9;

    /// <summary>当前布局（由渲染线程产出，界面按引用变化触发重绘）</summary>
    public ScopeLayoutResult? Layout
    {
        get => _layout;
        set
        {
            if (!ReferenceEquals(_layout, value))
            {
                _layout = value;
                InvalidateVisual();
            }
        }
    }

    /// <summary>合成画面的像素尺寸（= 布局容器尺寸）</summary>
    public void SetFrameSize(int width, int height)
    {
        if (width != _frameWidth || height != _frameHeight)
        {
            _frameWidth = Math.Max(width, 1);
            _frameHeight = Math.Max(height, 1);
            InvalidateVisual();
        }
    }

    public GraticuleOptions Options { get; set; } = new();

    public bool ShowGraticule { get; set; } = true;

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        if (!ShowGraticule || Layout is not { } layout || layout.Panes.Count == 0)
        {
            return;
        }

        Rect image = FittedRect(ActualWidth, ActualHeight, _frameWidth, _frameHeight);
        ScopeGraticule.Draw(dc, layout, image, Options);
    }

    /// <summary>Uniform 适配后的画面矩形（与 Image 控件 Stretch="Uniform" 的规则一致）</summary>
    public static Rect FittedRect(double availableWidth, double availableHeight, int frameWidth, int frameHeight)
    {
        if (availableWidth <= 0 || availableHeight <= 0 || frameWidth <= 0 || frameHeight <= 0)
        {
            return new Rect(0, 0, 0, 0);
        }

        double scale = Math.Min(availableWidth / frameWidth, availableHeight / frameHeight);
        double width = frameWidth * scale;
        double height = frameHeight * scale;
        return new Rect((availableWidth - width) / 2, (availableHeight - height) / 2, width, height);
    }
}
