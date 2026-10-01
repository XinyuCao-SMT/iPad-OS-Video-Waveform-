//
//  YuvFrameEncoder.cs
//  VideoScopePad.Win
//
//  RGB → 采集卡那种原始码流（YUY2 / NV12，limited 16–235）。
//  它不是给生产链路用的（我们不编码），而是给**自检**用的：
//  要验证「采集 → GPU 转换 → 示波器」这条路对不对，就得先造一帧码值已知的原始帧。
//
//  与 YuvFrameConverter（解码）互为逆运算，系数同样按矩阵选，所以：
//      75% 白 191 → Y=180 → 解码回 191（**正好**，因为中性色的 U=V=128 不受量化影响）
//      纯色（如黄 191/191/0）→ 解码回 191/190/0（G 差 1，来自色度 4:2:2/4:2:0 的量化）
//  自检里对中性色要求严格相等，对含色度的颜色放宽到 ±2 —— 这是格式本身的极限，不是 bug。
//
//  ⚠️ 用的是「数字视频」那套标准式（219/224 缩放），与解码侧的 (Y−16)×1.164 严格互逆：
//      Y  = 16  + 219 × (Kr·R + Kg·G + Kb·B) / 255
//      Cb = 128 + 224 × (B − Ylin) / (2 × 255 × (1 − Kb))
//      Cr = 128 + 224 × (R − Ylin) / (2 × 255 × (1 − Kr))
//   若改用课本里 0.257/0.504/0.098 那套（等价于 Kr=0.299 的展开），结果是同一个；
//   但别用「full range」的系数去编 limited 的流，那样灰阶会整体偏 16 个码值。
//

using Vortice.MediaFoundation;

namespace VideoScopePad.Win.Capture;

/// <summary>RGB ↔ YUV 的编码系数（按矩阵选 Kr / Kb）。</summary>
public readonly record struct EncodeCoefficients(double Kr, double Kg, double Kb)
{
    public static EncodeCoefficients For(VideoTransferMatrix matrix)
    {
        // 2020 的两套（NCL/CL）在 SDR 场景下按 709 处理即可，等真接 HDR 再细分
        bool bt709 = matrix is VideoTransferMatrix.Bt709
                              or VideoTransferMatrix.Bt202010
                              or VideoTransferMatrix.Bt202012;
        return bt709
            ? new EncodeCoefficients(0.2126, 0.7152, 0.0722)   // BT.709
            : new EncodeCoefficients(0.2990, 0.5870, 0.1140);  // BT.601（默认）
    }
}

/// <summary>把 RGB 帧编成采集卡格式的原始码流。</summary>
public static class YuvFrameEncoder
{
    /// <summary>单像素 RGB → (Y, Cb, Cr)，8 位码值（limited）。</summary>
    public static (byte Y, byte U, byte V) Encode(byte r, byte g, byte b, EncodeCoefficients c)
    {
        double luma = c.Kr * r + c.Kg * g + c.Kb * b;   // 0…255 单位的亮度

        double y = 16.0 + 219.0 * luma / 255.0;
        double cb = 128.0 + 224.0 * (b - luma) / (2.0 * 255.0 * (1.0 - c.Kb));
        double cr = 128.0 + 224.0 * (r - luma) / (2.0 * 255.0 * (1.0 - c.Kr));

        return (ClampRound(y, 16, 235), ClampRound(cb, 16, 240), ClampRound(cr, 16, 240));
    }

    /// <summary>RGBA8 → YUY2（(Y0,U,Y1,V)，两像素一组，色度 4:2:2 取左像素的值）。</summary>
    public static byte[] EncodeYuy2(ReadOnlySpan<byte> rgba, int width, int height, int rgbaStride, VideoColorInfo color)
    {
        EncodeCoefficients coefficients = EncodeCoefficients.For(color.Matrix);
        var output = new byte[width * 2 * height];
        int rowBytes = width * 2;

        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> source = rgba.Slice(y * rgbaStride, rgbaStride);
            Span<byte> destination = output.AsSpan(y * rowBytes, rowBytes);

            for (int x = 0; x < width; x += 2)
            {
                int i0 = x * 4;
                int i1 = (x + 1) * 4;
                byte y0 = Encode(source[i0], source[i0 + 1], source[i0 + 2], coefficients).Y;
                byte y1 = Encode(source[i1], source[i1 + 1], source[i1 + 2], coefficients).Y;

                // 4:2:2：一对像素共用一组色度，用**两个像素的平均**（UVC 常见做法；
                // 直接用左像素会在竖边处偏色，自检里对彩条边界有影响）
                (byte _, byte u0, byte v0) = Encode(source[i0], source[i0 + 1], source[i0 + 2], coefficients);
                (byte _, byte u1, byte v1) = Encode(source[i1], source[i1 + 1], source[i1 + 2], coefficients);

                destination[x * 2] = y0;
                destination[x * 2 + 1] = (byte)((u0 + u1 + 1) / 2);
                destination[x * 2 + 2] = y1;
                destination[x * 2 + 3] = (byte)((v0 + v1 + 1) / 2);
            }
        }

        return output;
    }

    /// <summary>RGBA8 → NV12（Y 平面 + 交织 UV 平面，色度 4:2:0 取 2×2 平均）。</summary>
    public static byte[] EncodeNv12(ReadOnlySpan<byte> rgba, int width, int height, int rgbaStride, VideoColorInfo color)
    {
        EncodeCoefficients coefficients = EncodeCoefficients.For(color.Matrix);
        var output = new byte[width * height * 3 / 2];

        // Y 平面
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> source = rgba.Slice(y * rgbaStride, rgbaStride);
            Span<byte> destination = output.AsSpan(y * width, width);
            for (int x = 0; x < width; x++)
            {
                int i = x * 4;
                destination[x] = Encode(source[i], source[i + 1], source[i + 2], coefficients).Y;
            }
        }

        // UV 平面（紧跟 Y 平面，每 2×2 一组）
        int chromaPlane = width * height;
        for (int y = 0; y < height; y += 2)
        {
            ReadOnlySpan<byte> row0 = rgba.Slice(y * rgbaStride, rgbaStride);
            ReadOnlySpan<byte> row1 = rgba.Slice((y + 1) * rgbaStride, rgbaStride);
            Span<byte> destination = output.AsSpan(chromaPlane + (y / 2) * width, width);

            for (int x = 0; x < width; x += 2)
            {
                int u = 0, v = 0;
                foreach (int offset in new[] { x * 4, (x + 1) * 4 })
                {
                    (byte _, byte u0, byte v0) = Encode(row0[offset], row0[offset + 1], row0[offset + 2], coefficients);
                    (byte _, byte u1, byte v1) = Encode(row1[offset], row1[offset + 1], row1[offset + 2], coefficients);
                    u += u0 + u1;
                    v += v0 + v1;
                }
                destination[x] = (byte)((u + 2) / 4);
                destination[x + 1] = (byte)((v + 2) / 4);
            }
        }

        return output;
    }

    private static byte ClampRound(double value, int minimum, int maximum)
    {
        int rounded = (int)Math.Floor(value + 0.5);
        return (byte)Math.Clamp(rounded, minimum, maximum);
    }
}
