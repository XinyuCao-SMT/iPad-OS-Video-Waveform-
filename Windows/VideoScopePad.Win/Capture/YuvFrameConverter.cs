//
//  YuvFrameConverter.cs
//  VideoScopePad.Win
//
//  采集卡的原始码流 → RGBA8。**这一步决定 IRE 标定准不准**，所以规则写死在这里：
//
//  【归一化策略】把 limited（16–235）在**入口处**展开成 full（0–255）：
//      Y=16  → 0     （0 IRE）
//      Y=235 → 255   （100 IRE）
//      Y=180 → 191   （75% 白 —— 正是本工程合成信号用的码值，两边能对上）
//  这样后面整条渲染链（HLSL 示波器、合成信号、断言）继续用「0…1 = 0…100 IRE」
//  这一套口径，不用在每个着色器里再判一次范围。
//
//  【为什么系数要按矩阵选】BT.601 与 BT.709 的 U/V 系数不同：
//      红色 601 是 (Y,U,V)=(81,90,240)，709 是 (63,102,240) 这一带
//      —— 差 18 个码值，矢量图上一个 75% 目标框的宽度还不止。所以矩阵不能瞎填。
//  实测本机这张 UT-VID 卡声明的是 **BT.601**（1080p 也报 601，廉价卡的常见默认），
//  但真值要等接上已知彩条信源再定；矩阵从 VideoColorInfo 传进来，改一处即可。
//
//  【CPU 还是 GPU】这里是 CPU 版，给「采一帧出 PNG / 自检」用；
//  1920×1080 YUY2 一帧大约十几毫秒，够出图但**不够 60 fps 实时**。
//  实时链路要把 YUV 直接当纹理喂给 D3D11（见 README 的下一步），
//  到时候这段逻辑会有一个 HLSL 版本，两者必须用同一套系数（断言也要对拍）。
//
//  【超白/超黑】limited→full 展开会截断在 255：码值 236(=101 IRE 超白) 也会变成 255。
//  8 位 RGBA8 没有余量，这是格式的物理限制。要准确量超白就得在 GPU 侧直接吃 YUV
//  （保留原始码值），所以「超标报警」要等 GPU 链路 —— 这一点先记在这里，别误以为
//  是报警阈值写错了。
//

using Vortice.MediaFoundation;

namespace VideoScopePad.Win.Capture;

/// <summary>YUV → RGBA8 转换。</summary>
public static class YuvFrameConverter
{
    /// <summary>
    /// YUV↔RGB 的系数（Y 先做 (Y − YOffset) × YScale，U/V 取 −128 的偏移）。
    /// 四个色度系数与业界记法对应：Rv（R 用 V）、Gu/Gv（G 用 U/V）、Bu（B 用 U）。
    /// </summary>
    public readonly record struct Coefficients(
        double YScale,
        double YOffset,
        double Rv,
        double Gu,
        double Gv,
        double Bu)
    {
        private static readonly Coefficients Bt601Limited = new(1.164, 16, 1.596, 0.391, 0.813, 2.018);
        private static readonly Coefficients Bt709Limited = new(1.164, 16, 1.793, 0.213, 0.533, 2.112);
        private static readonly Coefficients Bt601Full = new(1.0, 0, 1.402, 0.344, 0.714, 1.772);
        private static readonly Coefficients Bt709Full = new(1.0, 0, 1.5748, 0.1873, 0.4681, 1.8556);

        /// <summary>按「矩阵 + 量化范围」选系数。</summary>
        public static Coefficients Select(VideoTransferMatrix matrix, bool fullRange)
        {
            bool bt709 = matrix is VideoTransferMatrix.Bt709
                              or VideoTransferMatrix.Bt202010
                              or VideoTransferMatrix.Bt202012;
            return (bt709, fullRange) switch
            {
                (true, true) => Bt709Full,
                (true, false) => Bt709Limited,
                (false, true) => Bt601Full,
                (false, false) => Bt601Limited,   // 缺省：BT.601 + limited（广播最保守的组合）
            };
        }

        /// <summary>按色彩元数据选系数（范围未知时按 limited 处理 —— 广播约定，宁可保守）。</summary>
        public static Coefficients Select(VideoColorInfo color)
            => Select(color.Matrix, color.IsFullRange);

        public string Describe()
            => $"Y' = ({YScale:0.###}×(Y−{YOffset:0}))  超白/超黑在 8 位输出处截断";
    }

    /// <summary>
    /// 一帧 → RGBA8（行优先、左上角原点、每行 width×4 字节）。
    /// </summary>
    public static byte[] ToRgba8(CapturedFrame frame, string subtypeName, VideoColorInfo color)
    {
        ArgumentNullException.ThrowIfNull(frame);

        Coefficients coefficients = Coefficients.Select(color);
        return subtypeName.ToUpperInvariant() switch
        {
            "YUY2" or "YUYV" => Yuy2ToRgba8(frame.Data, frame.Width, frame.Height, frame.Stride, coefficients),
            "NV12" => Nv12ToRgba8(frame.Data, frame.Width, frame.Height, frame.Stride, coefficients),
            _ => throw new NotSupportedException(
                    $"还不支持把 {subtypeName} 转成 RGBA（目前有 YUY2 与 NV12；MJPG 要解码器、P010 要 10 位路径）"),
        };
    }

    /// <summary>
    /// NV12 → RGBA8。布局：Y 平面（width×height）+ 交织的 UV 平面（每两个字节一组 U、V，
    /// 覆盖 2×2 个像素 —— 色度 4:2:0，横竖都减半）。
    /// UV 平面紧跟在 Y 平面后，**共用同一个行跨距**（所以它在缓冲里的起始行号是 height）。
    /// </summary>
    public static byte[] Nv12ToRgba8(ReadOnlySpan<byte> source, int width, int height, int stride, Coefficients c)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "尺寸必须为正");
        }
        if ((width & 1) != 0 || (height & 1) != 0)
        {
            throw new ArgumentException("NV12 的宽高都必须是偶数（色度 4:2:0）", nameof(width));
        }

        int pitch = Math.Abs(stride);
        if (pitch < width)
        {
            throw new ArgumentException($"行跨距 {stride} 装不下一行 {width} 像素的 Y 平面", nameof(stride));
        }
        long needed = (long)pitch * height * 3 / 2;
        if (source.Length < needed)
        {
            throw new ArgumentException($"缓冲区只有 {source.Length} 字节，NV12 需要 {needed}", nameof(source));
        }

        var rgba = new byte[width * 4 * height];
        bool bottomUp = stride < 0;
        int chromaPlane = pitch * height;

        for (int y = 0; y < height; y++)
        {
            int ySourceRow = bottomUp ? height - 1 - y : y;
            ReadOnlySpan<byte> lumaRow = source.Slice(ySourceRow * pitch, width);

            int chromaRowIndex = bottomUp ? (height / 2 - 1 - y / 2) : (y / 2);
            ReadOnlySpan<byte> chromaRow = source.Slice(chromaPlane + chromaRowIndex * pitch, width);

            int destinationRow = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                int chromaIndex = x & ~1;   // U/V 两个字节管两个像素
                WritePixel(rgba, destinationRow + x * 4, lumaRow[x], chromaRow[chromaIndex], chromaRow[chromaIndex + 1], c);
            }
        }

        return rgba;
    }

    /// <summary>
    /// YUY2（= YUYV）→ RGBA8。内存布局：<c>Y0 U Y1 V</c> 四个字节一组，一组管两个像素
    /// （色度 4:2:2 水平方向减半，U/V 由相邻两像素共用）。
    /// </summary>
    public static byte[] Yuy2ToRgba8(ReadOnlySpan<byte> source, int width, int height, int stride, Coefficients c)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "尺寸必须为正");
        }
        if ((width & 1) != 0)
        {
            throw new ArgumentException("YUY2 的宽度必须是偶数（色度 4:2:2 两像素一组）", nameof(width));
        }

        int rowBytes = width * 2;
        int pitch = Math.Abs(stride);
        if (pitch < rowBytes)
        {
            throw new ArgumentException($"行跨距 {stride} 装不下一行 {width} 像素的 YUY2（需要 {rowBytes} 字节）", nameof(stride));
        }
        if (source.Length < pitch * height)
        {
            throw new ArgumentException($"缓冲区只有 {source.Length} 字节，需要 {pitch * height}", nameof(source));
        }

        var rgba = new byte[width * 4 * height];
        bool bottomUp = stride < 0;

        for (int y = 0; y < height; y++)
        {
            // stride 为负 = 驱动给的是自下而上的行序（DIB 习惯），翻转回来
            int sourceRow = bottomUp ? height - 1 - y : y;
            ReadOnlySpan<byte> row = source.Slice(sourceRow * pitch, rowBytes);
            int destinationRow = y * width * 4;

            for (int x = 0; x < width; x += 2)
            {
                int i = x * 2;
                byte y0 = row[i];
                byte u = row[i + 1];
                byte y1 = row[i + 2];
                byte v = row[i + 3];

                WritePixel(rgba, destinationRow + x * 4, y0, u, v, c);
                WritePixel(rgba, destinationRow + (x + 1) * 4, y1, u, v, c);
            }
        }

        return rgba;
    }

    private static void WritePixel(Span<byte> destination, int offset, byte y, byte u, byte v, Coefficients c)
    {
        double luma = (y - c.YOffset) * c.YScale;
        double du = u - 128.0;
        double dv = v - 128.0;

        double r = luma + c.Rv * dv;
        double g = luma - c.Gu * du - c.Gv * dv;
        double b = luma + c.Bu * du;

        destination[offset] = ToByte(r);
        destination[offset + 1] = ToByte(g);
        destination[offset + 2] = ToByte(b);
        destination[offset + 3] = 255;
    }

    /// <summary>
    /// 浮点 → 8 位码值：**四舍五入**（+0.5 后截断），并夹在 0…255。
    ///
    /// ⚠️ 别和「断言用向零截断」那条规矩搞混（README 里那条说的是**直方图分箱**：
    /// 着色器用 int() 截断，所以断言必须同样截断）。
    /// 这里是 YUV→RGB 的**像素换算**，业界（libyuv / swscale / GPU 的 round()）都是四舍五入：
    ///   1.164×(180−16) = 190.90 → 191（75% 白，正好等于本工程合成信号的码值）
    ///   若用截断会得到 190，和合成信号差 1 —— 采集与合成两条链就对不上了。
    /// 将来把这段搬到 HLSL 时，着色器里必须用 round() 而不是 int() 截断，否则同样差 1。
    /// </summary>
    private static byte ToByte(double value)
    {
        if (value <= 0)
        {
            return 0;
        }
        if (value >= 255)
        {
            return 255;
        }
        return (byte)(int)(value + 0.5);
    }
}
