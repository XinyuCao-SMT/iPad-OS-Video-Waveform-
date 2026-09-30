//
//  Program.cs
//  render-selfcheck
//
//  离屏自检：不依赖窗口与采集卡，把合成测试信号跑一遍渲染链路并输出 PNG。
//
//  用法：
//    dotnet run --project Windows\tools\render-selfcheck -- out\selfcheck.png [宽 高]
//
//  现在（第一步）只验证：D3D11 设备、纹理上传、读回、PNG 编码 —— 也就是「本地能自己看图」这条路。
//  示波器渲染接进来之后，这里会输出完整的四格布局图。
//

using System.Globalization;
using VideoScopePad.Win.Render;

namespace VideoScopePad.Tools.RenderSelfCheck;

internal static class Program
{
    private static int Main(string[] args)
    {
        string outPath = args.Length > 0 ? args[0] : "selfcheck.png";
        int width = args.Length > 1 && int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var w) ? w : 1920;
        int height = args.Length > 2 && int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h) ? h : 1080;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);

            using var d3d = D3DContext.Create();
            Console.WriteLine($"适配器      : {d3d.AdapterName}");
            Console.WriteLine($"特性级别    : {d3d.FeatureLevel}");

            // 1) 合成测试帧 → 上传成纹理
            var pixels = SyntheticSource.MakeTestFrame(width, height, out int srcStride);
            using var source = d3d.CreateTextureFromRgba8(width, height, pixels, out _);
            Console.WriteLine($"合成信号    : {width}×{height}（SMPTE 75% 彩条 + 蓝条 + PLUGE + 灰阶斜坡）");

            // 2) 读回 + 存 PNG：这一步通了，后面示波器的出图自检就都有保障
            d3d.SavePng(source, outPath);
            var info = new FileInfo(outPath);
            Console.WriteLine($"输出 PNG    : {Path.GetFullPath(outPath)}  {info.Length} bytes");

            // 3) 顺手核对几个像素，确认上传/读回没有整体偏移或通道交换
            //    图案：上部 2/3 是 7 条 75% 彩条（每条约 w/7 宽），中间是蓝/黑交替，下部是 PLUGE + 斜坡
            var readBack = d3d.ReadBackRgba8(source, out int rw, out int rh);
            int barY = height / 6;
            Check(readBack, rw, rh, width / 14, barY, 191, 191, 191, "75% 白条");
            Check(readBack, rw, rh, width * 3 / 14, barY, 191, 191, 0, "黄条");
            Check(readBack, rw, rh, width * 5 / 14, barY, 0, 191, 191, "青条");
            Check(readBack, rw, rh, width * 11 / 14, barY, 191, 0, 0, "红条");
            Check(readBack, rw, rh, width * 13 / 14, barY, 0, 0, 191, "蓝条");
            Check(readBack, rw, rh, width / 14, height * 72 / 100, 0, 0, 191, "蓝条带");
            Check(readBack, rw, rh, width * 3 / 14, height * 72 / 100, 0, 0, 0, "蓝条带黑缝");
            Check(readBack, rw, rh, width / 14, height * 9 / 10, 0, 0, 0, "PLUGE 0 IRE");

            // 灰阶斜坡必须是单调递增的（波形图上应该是一条从 0 到 100 的斜线）
            int rampY = height * 9 / 10;
            byte previous = 0;
            for (int i = 1; i <= 6; i++)
            {
                int x = width / 4 + (width * 3 / 4) * i / 7;
                int offset = (rampY * rw + x) * 4;
                byte value = readBack[offset];
                if (value <= previous)
                {
                    throw new InvalidOperationException($"灰阶斜坡不单调：x={x} 读到 {value}，前一个 {previous}");
                }
                previous = value;
            }
            Console.WriteLine($"  ✓ 灰阶斜坡      6 点单调递增，末点码值 {previous}");

            Console.WriteLine("自检通过 ✅");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("自检失败 ❌");
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void Check(byte[] pixels, int width, int height, int x, int y,
                              int expectR, int expectG, int expectB, string label)
    {
        int offset = (y * width + x) * 4;
        if (offset < 0 || offset + 3 >= pixels.Length)
        {
            throw new InvalidOperationException($"取样点越界：{label}");
        }

        byte r = pixels[offset], g = pixels[offset + 1], b = pixels[offset + 2];
        bool ok = Math.Abs(r - expectR) <= 2 && Math.Abs(g - expectG) <= 2 && Math.Abs(b - expectB) <= 2;
        string status = ok ? "✓" : "✗";
        Console.WriteLine($"  {status} {label,-16} ({x},{y}) = ({r},{g},{b})  期望 ({expectR},{expectG},{expectB})");
        if (!ok)
        {
            throw new InvalidOperationException($"像素核对失败：{label}");
        }
    }
}
