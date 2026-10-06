//
//  AudioMeterElement.cs
//  VideoScopePad.App
//
//  音频电平表（自绘）：每通道一根竖条 + 峰值保持线 + CLIP 标记 + dB 刻度。
//  通道数按实际设备来（本机 2ch 就画 2 条；架构上最多 8 条，接 8ch 设备不用改代码）。
//
//  为什么自绘而不是用 XAML 控件堆：与示波器同样的理由 —— 每帧要按 dBFS 换算高度、
//  画峰值线、标 CLIP，用 Rectangle 堆出来反而更绕；自绘一次到位，也便于按像素断言。
//

using System.Globalization;
using System.Windows;
using System.Windows.Media;
using VideoScopePad.Win.Audio;

namespace VideoScopePad.App;

public sealed class AudioMeterElement : FrameworkElement
{
    private static readonly Typeface Mono = new("Consolas");
    private static readonly Brush Background = new SolidColorBrush(Color.FromRgb(18, 21, 26));
    private static readonly Brush BarBrush = new SolidColorBrush(Color.FromRgb(63, 185, 116));
    private static readonly Brush WarnBrush = new SolidColorBrush(Color.FromRgb(240, 196, 74));
    private static readonly Brush ClipBrush = new SolidColorBrush(Color.FromRgb(255, 90, 80));
    private static readonly Brush PeakBrush = new SolidColorBrush(Color.FromRgb(230, 234, 240));
    private static readonly Brush TextBrush = new SolidColorBrush(Color.FromRgb(124, 135, 151));
    private static readonly Pen PeakPen = new(PeakBrush, 2);

    static AudioMeterElement()
    {
        Background.Freeze();
        BarBrush.Freeze();
        WarnBrush.Freeze();
        ClipBrush.Freeze();
        PeakBrush.Freeze();
        TextBrush.Freeze();
        PeakPen.Freeze();
    }

    /// <summary>当前要显示的电平（由界面每帧喂进来）</summary>
    public IReadOnlyList<ChannelMeterState> Levels { get; set; } = Array.Empty<ChannelMeterState>();

    /// <summary>标题（例如 "音频（2ch @ 48 kHz）"）</summary>
    public string Caption { get; set; } = "音频";

    /// <summary>响度读数（没有就传 null）</summary>
    public double? IntegratedLufs { get; set; }

    public static readonly double FloorDb = -60.0;
    public static readonly double CeilingDb = 0.0;

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth;
        double height = ActualHeight;
        if (width < 30 || height < 20)
        {
            return;
        }

        dc.DrawRectangle(Background, null, new Rect(0, 0, width, height));

        double labelHeight = 14;
        double barsTop = 4;
        double barsBottom = height - labelHeight - 2;
        double barsHeight = barsBottom - barsTop;
        if (barsHeight < 10)
        {
            return;
        }

        // 标题与响度
        DrawText(dc, Caption, 10, TextBrush, new Point(4, height - labelHeight + 1));
        if (IntegratedLufs is { } lufs && !double.IsNegativeInfinity(lufs))
        {
            string text = $"{lufs:0.0} LUFS";
            FormattedText formatted = Format(text, 10, TextBrush);
            DrawText(dc, text, 10, TextBrush, new Point(width - formatted.Width - 4, height - labelHeight + 1));
        }

        int channels = Math.Max(Levels.Count, 1);
        double slot = width / channels;
        double barWidth = Math.Max(slot * 0.55, 6);
        double gap = (slot - barWidth) / 2;

        // 顶部/底部刻度线（0 dB 与 −60 dB 之外再给 −20/−40 两条参考）
        var gridPen = new Pen(new SolidColorBrush(Color.FromRgb(42, 48, 58)), 1);
        gridPen.Freeze();
        foreach (double db in new[] { -12.0, -24.0, -36.0, -48.0 })
        {
            double y = barsBottom - (db - FloorDb) / (CeilingDb - FloorDb) * barsHeight;
            dc.DrawLine(gridPen, new Point(0, y), new Point(width, y));
        }

        for (int ch = 0; ch < Levels.Count; ch++)
        {
            ChannelMeterState state = Levels[ch];
            double x = ch * slot + gap;
            double level = Math.Clamp(state.LevelDbfs, FloorDb, CeilingDb);
            double filled = (level - FloorDb) / (CeilingDb - FloorDb) * barsHeight;
            double y = barsBottom - filled;

            // 条：正常绿；接近满刻度转黄；CLIP 时整条红
            Brush brush = state.Clipped ? ClipBrush : level > -6 ? WarnBrush : BarBrush;
            dc.DrawRectangle(brush, null, new Rect(x, y, barWidth, Math.Max(filled, 0)));

            // 峰值保持线
            if (!double.IsNegativeInfinity(state.PeakHoldDbfs))
            {
                double peak = Math.Clamp(state.PeakHoldDbfs, FloorDb, CeilingDb);
                double peakY = barsBottom - (peak - FloorDb) / (CeilingDb - FloorDb) * barsHeight;
                dc.DrawLine(PeakPen, new Point(x, peakY), new Point(x + barWidth, peakY));
            }

            // 通道号 / CLIP
            FormattedText label = Format(state.Clipped ? "CLIP" : $"{(ch + 1)}", 9, state.Clipped ? ClipBrush : TextBrush);
            dc.DrawText(label, new Point(x + (barWidth - label.Width) / 2, barsBottom + 1));
        }
    }

    private static FormattedText Format(string text, double size, Brush brush)
        => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Mono, size, brush, 1.0);

    private static void DrawText(DrawingContext dc, string text, double size, Brush brush, Point origin)
        => dc.DrawText(Format(text, size, brush), origin);
}
