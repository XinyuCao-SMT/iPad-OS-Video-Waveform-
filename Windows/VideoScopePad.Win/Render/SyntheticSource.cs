//
//  SyntheticSource.cs
//  VideoScopePad.Win
//
//  合成测试信号：没有采集卡、或者采集卡还没插上的时候，用它在本地把整条渲染链路跑通。
//
//  图案（故意做成「一眼就能核对读数」的样子）：
//    · 上部 2/3：SMPTE 75% 彩条（灰/黄/青/绿/品/红/蓝）—— 矢量图上必须落在 75% 目标框里
//    · 中间：蓝条（逆序彩条）—— 检查行方向与取样位置
//    · 下部：PLUGE（-4% / 0% / +4% 黑） + 0→100 IRE 灰阶斜坡 —— 波形图上是一条从 0 到 100 的斜线
//
//  像素写的是 **R'G'B' 码值（0..1）**，与示波器引擎的取样口径一致：
//  全范围时 1.0 = 100 IRE、0.75 = 75 IRE、0.0 = 0 IRE。
//

namespace VideoScopePad.Win.Render;

public static class SyntheticSource
{
    /// <summary>生成 RGBA8 测试帧（左上角为原点）</summary>
    public static byte[] MakeTestFrame(int width, int height, out int stride)
    {
        width = Math.Max(width, 16);
        height = Math.Max(height, 16);
        stride = width * 4;
        var pixels = new byte[stride * height];

        double topEnd = height * 2.0 / 3.0;
        double midEnd = height * 0.78;

        // 75% 彩条：R/G/B 三通道的码值（0.75 = 75 IRE）
        var bars = new (double R, double G, double B)[]
        {
            (0.75, 0.75, 0.75), // 75% 白
            (0.75, 0.75, 0.00), // 黄
            (0.00, 0.75, 0.75), // 青
            (0.00, 0.75, 0.00), // 绿
            (0.75, 0.00, 0.75), // 品红
            (0.75, 0.00, 0.00), // 红
            (0.00, 0.00, 0.75), // 蓝
        };

        for (int y = 0; y < height; y++)
        {
            int rowOffset = y * stride;
            for (int x = 0; x < width; x++)
            {
                double r, g, b;

                if (y < topEnd)
                {
                    int bar = Math.Min((int)(x * bars.Length / (double)width), bars.Length - 1);
                    (r, g, b) = bars[bar];
                }
                else if (y < midEnd)
                {
                    // 蓝条：蓝/黑/品/黑/青/黑/白，用来核对行方向
                    int bar = Math.Min((int)(x * bars.Length / (double)width), bars.Length - 1);
                    bool even = bar % 2 == 0;
                    (r, g, b) = even ? (0.0, 0.0, 0.75) : (0.0, 0.0, 0.0);
                }
                else
                {
                    // PLUGE（左 1/4）+ 灰阶斜坡（右 3/4）
                    if (x < width / 4)
                    {
                        int segment = x / Math.Max(width / 12, 1);
                        (r, g, b) = segment switch
                        {
                            0 => (0.0, 0.0, 0.0),      // 0 IRE
                            1 => (0.04, 0.04, 0.04),   // +4%
                            2 => (0.0, 0.0, 0.0),      // 0 IRE
                            _ => (0.08, 0.08, 0.08),   // 8% 黑（-4% 在全范围下会被截到 0）
                        };
                    }
                    else
                    {
                        double t = (x - width / 4.0) / Math.Max(width * 3.0 / 4.0, 1);
                        r = g = b = Math.Clamp(t, 0, 1);
                    }
                }

                int offset = rowOffset + x * 4;
                pixels[offset + 0] = ToByte(r);
                pixels[offset + 1] = ToByte(g);
                pixels[offset + 2] = ToByte(b);
                pixels[offset + 3] = 255;
            }
        }

        return pixels;
    }

    private static byte ToByte(double value) => (byte)Math.Clamp(Math.Round(value * 255.0), 0, 255);
}

/// <summary>
/// 「活的」合成信号源：静态测试图**只生成一次**，每帧只在它上面叠动态元素。
///
/// ⚠️ 这里踩过一次：一开始每帧都调 MakeTestFrame 重新生成整幅图（1600×900 要算 144 万像素 ×
/// 3 次 Math.Round），实测一帧要 37 ms —— 光这一条就把实时链路拖到 24 fps。
/// 静态部分必须缓存，动态部分才是每帧要做的。
/// </summary>
public sealed class SyntheticLiveSource
{
    private byte[]? _baseFrame;
    private int _width;
    private int _height;

    /// <summary>生成一帧「活的」测试图：静态彩条 + 扫掠竖线 + 伸缩码值条</summary>
    public void Render(Span<byte> destination, int width, int height, long frameIndex)
    {
        if (_baseFrame is null || _width != width || _height != height)
        {
            _baseFrame = SyntheticSource.MakeTestFrame(width, height, out _);
            _width = width;
            _height = height;
        }

        _baseFrame.AsSpan(0, Math.Min(_baseFrame.Length, destination.Length)).CopyTo(destination);

        int stride = width * 4;

        // 1) 扫掠竖线（100 IRE，宽度约 0.4%）
        int lineWidth = Math.Max(width / 250, 3);
        int phase = (int)(frameIndex % width);
        for (int x = phase; x < Math.Min(phase + lineWidth, width); x++)
        {
            for (int y = 0; y < height; y++)
            {
                int offset = y * stride + x * 4;
                destination[offset] = 255;
                destination[offset + 1] = 255;
                destination[offset + 2] = 255;
            }
        }

        // 2) 右下角的伸缩码值条（75% 绿，方便在波形 / 矢量图上认出来）
        int barHeight = Math.Max(height / 60, 4);
        int barLength = (int)(width / 4.0 * (0.5 + 0.5 * Math.Sin(frameIndex * 0.08)));
        for (int y = height - barHeight * 2; y < height - barHeight; y++)
        {
            if (y < 0)
            {
                continue;
            }
            for (int x = Math.Max(width - barLength, 0); x < width; x++)
            {
                int offset = y * stride + x * 4;
                destination[offset] = 0;
                destination[offset + 1] = 191;
                destination[offset + 2] = 0;
            }
        }
    }
}
