//
//  Program.cs
//  mf-capture
//
//  Media Foundation 采集链路的命令行自检。三步走，每步一条命令一个验收点：
//
//    dotnet run --project Windows\tools\mf-capture -- list                    ① 枚举设备
//    dotnet run --project Windows\tools\mf-capture -- formats                 ② 原生格式 + 生效格式 + 视频范围/色彩矩阵
//    dotnet run --project Windows\tools\mf-capture -- capture 1               ③ 采 1 秒，实测 fps + 存 PNG
//
//  惯例与 render-selfcheck 一致：结论必须是**可计算的断言**（不是「看着像对」），
//  同时把能给人看的东西（设备清单、格式表、PNG）打到屏幕/存成文件。
//

using System.Globalization;
using VideoScopePad.Win.Capture;
using Vortice.MediaFoundation;

namespace VideoScopePad.Tools.MfCapture;

internal static class Program
{
    private static int _passed;
    private static int _failed;

    private static int Main(string[] args)
    {
        string command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";

        try
        {
            switch (command)
            {
                case "list":
                    return ListDevices(args.Length > 1 ? args[1] : DefaultDeviceNameFragment);

                case "formats":
                    return Formats(args);

                case "capture":
                    return Capture(args);

                default:
                    PrintHelp();
                    return command is "help" or "-h" or "--help" ? 0 : 2;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine($"✗ 未捕获的异常：{ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    /// <summary>本机那张采集卡的名字（UT-VID 00K0601910），用于默认断言。</summary>
    private const string DefaultDeviceNameFragment = "UT-VID 00K0601910";

    private static void PrintHelp()
    {
        Console.WriteLine("mf-capture —— Media Foundation 采集链路自检");
        Console.WriteLine();
        Console.WriteLine("  list [名字片段]      ① 枚举视频采集设备（默认断言找 UT-VID 00K0601910）");
        Console.WriteLine("  formats [选项]       ② 打印原生格式清单 + 回读生效格式 + 视频范围/色彩矩阵");
        Console.WriteLine("       --device <片段>      选设备（默认 UT-VID 00K0601910）");
        Console.WriteLine("       --set <WxH@fps:FourCC>  指定要生效的格式（默认第一个）");
        Console.WriteLine("  capture [秒数] [选项]  ③ 采集并统计实测 fps，存 capture-frame.png");
        Console.WriteLine("       --device <片段>      选设备");
        Console.WriteLine("       --set <WxH@fps:FourCC>  指定采集格式");
        Console.WriteLine("       --out <目录>         出图目录（默认 Windows/out）");
    }

    // ------------------------------------------------------------------
    //  ① 枚举设备
    // ------------------------------------------------------------------
    private static int ListDevices(string nameFragment)
    {
        using var mf = MediaFoundationRuntime.Start();

        Console.WriteLine("Media Foundation 运行时 : 已启动（MFStartup, useLightVersion=false）");
        Console.WriteLine();

        // 属性键到底住在哪 —— 这就是「先确认属性键位置」那件事，
        // 直接把 Vortice 里的字段 + 它映射的 MF GUID 打出来（可与 mfidl.h 对照）。
        Console.WriteLine("属性键位置（Vortice.MediaFoundation.CaptureDeviceAttributeKeys，均为裸 Guid 字段）：");
        PrintKey("SOURCE_TYPE", CaptureDeviceAttributeKeys.SourceType);
        PrintKey("SOURCE_TYPE_VIDCAP", CaptureDeviceAttributeKeys.SourceTypeVidcap);
        PrintKey("SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK", CaptureDeviceAttributeKeys.SourceTypeVidcapSymbolicLink);
        PrintKey("SOURCE_TYPE_VIDCAP_CATEGORY", CaptureDeviceAttributeKeys.SourceTypeVidcapCategory);
        PrintKey("SOURCE_TYPE_VIDCAP_HW_SOURCE", CaptureDeviceAttributeKeys.SourceTypeVidcapHwSource);
        PrintKey("FRIENDLY_NAME", CaptureDeviceAttributeKeys.FriendlyName);
        Console.WriteLine();

        IReadOnlyList<CaptureDeviceInfo> devices = VideoDeviceEnumerator.Enumerate();

        Console.WriteLine($"视频采集设备 : {devices.Count} 台");
        Console.WriteLine();
        if (devices.Count == 0)
        {
            Console.WriteLine("  （一台都没有 —— 采集卡没插好 / 被别的程序占用 / 驱动异常）");
        }
        foreach (CaptureDeviceInfo device in devices)
        {
            Console.WriteLine($"  [{device.Index}] {device.FriendlyName}");
            Console.WriteLine($"       符号链接 : {device.SymbolicLink}");
            Console.WriteLine($"       sourceType : {device.SourceType}");
            Console.WriteLine($"       类别       : {device.Category}");
            Console.WriteLine($"       硬件来源   : {(device.IsHardwareSource ? "是" : "否")}");
        }
        Console.WriteLine();

        // 验收断言：清单里必须出现这台采集卡
        if (!string.IsNullOrEmpty(nameFragment))
        {
            CaptureDeviceInfo? found = devices.FirstOrDefault(
                d => d.FriendlyName.Contains(nameFragment, StringComparison.OrdinalIgnoreCase));
            Check(found is not null, $"设备清单里出现「{nameFragment}」",
                found is null ? "没找到这台设备" : $"序号 {found.Index}：{found.FriendlyName}");
        }
        else
        {
            Console.WriteLine("（未指定 --expect 片段，跳过存在性断言）");
        }

        return Report();
    }

    private static void PrintKey(string name, Guid key)
    {
        Console.WriteLine($"  MF_DEVSOURCE_ATTRIBUTE_{name,-34} = {key.ToString("D").ToUpperInvariant()}");
    }

    // ------------------------------------------------------------------
    //  ② 格式清单（下一步实现）
    // ------------------------------------------------------------------
    private static int Formats(string[] args)
    {
        Console.WriteLine("formats 尚未实现（第 2 步）");
        return 2;
    }

    // ------------------------------------------------------------------
    //  ③ 采集（下一步实现）
    // ------------------------------------------------------------------
    private static int Capture(string[] args)
    {
        Console.WriteLine("capture 尚未实现（第 3 步）");
        return 2;
    }

    // ------------------------------------------------------------------
    //  断言与统计（与 render-selfcheck 同一套写法）
    // ------------------------------------------------------------------
    internal static void Check(bool condition, string what, string detail = "")
    {
        if (condition)
        {
            _passed++;
            Console.WriteLine($"  ✓ {what}{(detail.Length > 0 ? "：" + detail : string.Empty)}");
        }
        else
        {
            _failed++;
            Console.WriteLine($"  ✗ {what}{(detail.Length > 0 ? "：" + detail : string.Empty)}");
        }
    }

    private static int Report()
    {
        Console.WriteLine();
        Console.WriteLine($"断言：{_passed} 通过 / {_failed} 失败");
        return _failed == 0 ? 0 : 3;
    }

    internal static int ParseInt(string text, int fallback)
        => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;
}
