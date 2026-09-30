//
//  RectF.cs
//  VideoScopePad.Win
//
//  布局计算用的轻量矩形（左上角为原点、y 轴向下 —— 与 iPad 版的 CGRect 习惯一致）。
//
//  为什么不用现成的矩形类型：布局是「唯一的一份计算」，两个平台的渲染与界面都要跟它严格对齐，
//  所以这里只要最小、语义明确的实现，不引入任何外部依赖。
//

using System.Globalization;

namespace VideoScopePad.Win.Core;

public readonly struct RectF : IEquatable<RectF>
{
    public readonly double X;
    public readonly double Y;
    public readonly double Width;
    public readonly double Height;

    public RectF(double x, double y, double width, double height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public double MinX => X;
    public double MinY => Y;
    public double MaxX => X + Width;
    public double MaxY => Y + Height;
    public double MidX => X + Width / 2.0;
    public double MidY => Y + Height / 2.0;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public RectF InsetBy(double dx, double dy)
        => new(X + dx, Y + dy, Math.Max(Width - dx * 2, 0), Math.Max(Height - dy * 2, 0));

    public RectF Intersect(RectF other)
    {
        double minX = Math.Max(MinX, other.MinX);
        double minY = Math.Max(MinY, other.MinY);
        double maxX = Math.Min(MaxX, other.MaxX);
        double maxY = Math.Min(MaxY, other.MaxY);
        return new RectF(minX, minY, Math.Max(maxX - minX, 0), Math.Max(maxY - minY, 0));
    }

    public bool Equals(RectF other)
        => X.Equals(other.X) && Y.Equals(other.Y)
        && Width.Equals(other.Width) && Height.Equals(other.Height);

    public override bool Equals(object? obj) => obj is RectF other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(X, Y, Width, Height);

    public override string ToString()
        => string.Format(CultureInfo.InvariantCulture,
                         "({0:0.###},{1:0.###} {2:0.###}×{3:0.###})",
                         X, Y, Width, Height);
}
