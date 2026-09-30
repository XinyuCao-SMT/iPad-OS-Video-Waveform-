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

                case "probe":
                    return Probe(args);

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
    //  ② 格式清单 + 生效格式 + 视频范围/色彩矩阵元数据
    // ------------------------------------------------------------------
    private static int Formats(string[] args)
    {
        string deviceFragment = Option(args, "--device") ?? DefaultDeviceNameFragment;
        string? requested = Option(args, "--set");

        using var mf = MediaFoundationRuntime.Start();
        using CaptureDevice device = CaptureDevice.OpenByName(deviceFragment);
        Console.WriteLine($"设备          : [{device.Info.Index}] {device.Info.FriendlyName}");
        Console.WriteLine($"符号链接      : {device.Info.SymbolicLink}");
        Console.WriteLine();

        // ---------- 原生格式清单 ----------
        IReadOnlyList<CaptureFormat> formats = device.GetNativeFormats();
        Console.WriteLine($"原生格式清单  : {formats.Count} 条");
        Console.WriteLine();
        Console.WriteLine("  #   分辨率        帧率        像素格式   隔行  压缩  行跨距  每帧字节");
        Console.WriteLine("  --- ------------- ----------- ---------- ----- ----- ------- ----------");
        foreach (CaptureFormat f in formats)
        {
            string stride = f.HasDefaultStride ? f.DefaultStride.ToString() : "—";
            string size = f.ImageSize > 0 ? f.ImageSize.ToString() : "—";
            Console.WriteLine($"  {f.NativeIndex,3} {f.Width,5}×{f.Height,-6} {f.FrameRate,7:0.###} fps {f.SubtypeName,-10} "
                            + $"{(f.InterlaceMode == VideoInterlaceMode.Progressive ? "逐行 " : "?    ")} "
                            + $"{(f.IsCompressed ? "是   " : "否   ")} {stride,7} {size,10}");
        }
        Console.WriteLine();

        // 验收断言：格式清单不能是空的，且四条基本字段都得解析出来
        Check(formats.Count > 0, "原生格式清单非空", $"{formats.Count} 条");
        Check(formats.All(f => f.Width > 0 && f.Height > 0), "每条格式都解析出分辨率",
            formats.Count > 0 ? $"例如 {formats[0].Width}×{formats[0].Height}" : "");
        Check(formats.All(f => f.SubtypeName != "?"), "每条格式都识别出像素格式",
            formats.Count > 0 ? string.Join("、", formats.Select(f => f.SubtypeName).Distinct()) : "");

        // ---------- 选一条生效 ----------
        CaptureFormat wanted = requested is { Length: > 0 }
            ? formats.FirstOrDefault(f => f.MatchesSpec(requested))
                ?? throw new InvalidOperationException($"没有匹配「{requested}」的原生格式")
            : CaptureFormat.PickPreferred(formats)
                ?? throw new InvalidOperationException("设备没有可用的原生格式");

        Console.WriteLine($"选择格式      : #{wanted.NativeIndex} {wanted.Describe()}"
                        + (requested is { Length: > 0 } ? "（命令行指定）" : "（自动挑选：优先未压缩 + 最大分辨率）"));
        CaptureFormat effective = device.SetNativeFormat(wanted.NativeIndex);
        Console.WriteLine($"回读生效格式  : {effective.Describe()}");
        Console.WriteLine();

        Check(effective.Width == wanted.Width && effective.Height == wanted.Height,
            "回读分辨率与所设一致", $"{effective.Width}×{effective.Height} vs 请求 {wanted.Width}×{wanted.Height}");
        Check(string.Equals(effective.SubtypeName, wanted.SubtypeName, StringComparison.OrdinalIgnoreCase),
            "回读像素格式与所设一致", $"{effective.SubtypeName} vs 请求 {wanted.SubtypeName}");
        Check(Math.Abs(effective.FrameRate - wanted.FrameRate) < 0.01,
            "回读帧率与所设一致", $"{effective.FrameRate:0.###} vs 请求 {wanted.FrameRate:0.###}");
        Check(!effective.IsCompressed, "生效格式是未压缩的原始码流（示波器要的就是它）",
            effective.IsCompressed ? $"{effective.SubtypeName} 是压缩格式，读数会受编码影响" : effective.SubtypeName);

        // ---------- 色彩元数据 ----------
        Console.WriteLine();
        Console.WriteLine("色彩元数据（决定 IRE 标定准不准）：");
        PrintColorMetadata("有效格式", effective);

        // ---------- 属性集整个摊开（证据链）----------
        Console.WriteLine();
        Console.WriteLine("当前生效媒体类型的属性集（IMFAttributes 全量）：");
        IReadOnlyList<(Guid Key, AttributeType Type, string Value)> attributes = device.DescribeCurrentTypeAttributes();
        foreach ((Guid key, AttributeType type, string value) in attributes)
        {
            Console.WriteLine($"  {MediaAttributeCatalog.Describe(key),-58} [{type,-7}] = {value}");
        }

        // ---------- 交叉核对：打包属性解出来必须和我们解析的一致 ----------
        // MF 把尺寸/帧率塞进一个 UInt64（high<<32 | low），解析错一位就会差出天去，
        // 所以这里拿**原始值**反解一遍，和自己解析的结果对拍。
        Console.WriteLine();
        uint rawWidth = 0, rawHeight = 0, rawNum = 0, rawDen = 0;
        bool hasSizeAttr = Lookup(attributes, MediaTypeAttributeKeys.FrameSize, out ulong packedSize);
        bool hasRateAttr = Lookup(attributes, MediaTypeAttributeKeys.FrameRate, out ulong packedRate);
        if (hasSizeAttr)
        {
            rawWidth = (uint)(packedSize >> 32);
            rawHeight = (uint)(packedSize & 0xFFFFFFFF);
        }
        if (hasRateAttr)
        {
            rawNum = (uint)(packedRate >> 32);
            rawDen = (uint)(packedRate & 0xFFFFFFFF);
        }
        Check(hasSizeAttr && rawWidth == effective.Width && rawHeight == effective.Height,
            "MF_MT_FRAME_SIZE 原始值反解 == 解析出的分辨率",
            hasSizeAttr ? $"{packedSize} → {rawWidth}×{rawHeight}" : "属性缺失");
        Check(hasRateAttr && rawNum == effective.FrameRateNumerator && rawDen == effective.FrameRateDenominator,
            "MF_MT_FRAME_RATE 原始值反解 == 解析出的帧率",
            hasRateAttr ? $"{packedRate} → {rawNum}/{rawDen}" : "属性缺失");

        // ---------- 交叉核对：FourCC 解码器 vs Vortice 的常量表 ----------
        // 表里有的格式走 Known 分支，反解器不会被走到；用同一条 GUID 对拍一次，
        // 保证「非标准 FourCC」那条路径也是对的（采集卡偶发私有格式时全靠它）。
        int decoded = 0;
        int mismatch = 0;
        foreach (CaptureFormat f in formats)
        {
            if (MediaSubtype.TryDecodeFourCc(f.Subtype, out string fourCc))
            {
                decoded++;
                if (!string.Equals(fourCc, f.SubtypeName, StringComparison.OrdinalIgnoreCase))
                {
                    mismatch++;
                }
            }
        }
        Check(decoded > 0 && mismatch == 0, "FourCC 反解器与常量表一致",
            $"反解 {decoded} 条，其中 {mismatch} 条对不上");

        // ---------- 色彩矩阵的实务提醒 ----------
        if (effective.Color.Matrix == VideoTransferMatrix.Bt601 && effective.Height >= 720)
        {
            Console.WriteLine();
            Console.WriteLine("⚠ 矩阵声明为 BT.601，但分辨率是 HD —— 这是廉价 UVC 卡的常见默认填法。");
            Console.WriteLine("  对示波器的影响：矢量图的 Cb/Cr 解码若照声明走 601，7 条彩条的落点会整体偏一点；");
            Console.WriteLine("  是否改用 BT.709 解码，等接进链路后用合成彩条的信源实测再定（有数据才好拍板）。");
        }

        // 量化范围：驱动给了就用驱动，没给就推断 —— 但必须显式说出来
        Console.WriteLine();
        if (effective.Color.IsRangeUnknown)
        {
            string inferred = InferRange(effective.SubtypeName);
            Console.WriteLine($"⚠ 驱动**没有**声明量化范围（MF_MT_VIDEO_NOMINAL_RANGE 缺失或为 Unknown）。");
            Console.WriteLine($"  按经验推断：{effective.SubtypeName} → {inferred}");
            Console.WriteLine($"  推断依据：MJPEG 是 JPEG 系（0–255 full）；未压缩 YUV 走广播约定（16–235 limited）。");
            Console.WriteLine($"  标定影响：若按 limited 标定，码值 235 = 100 IRE；同一信号被当成 full 就会读成 "
                            + $"{235.0 / 255.0 * 100.0:0.0} IRE（差 {100 - 235.0 / 255.0 * 100.0:0.0} IRE）。");
        }
        else
        {
            Check(true, "驱动显式给出了量化范围", effective.Color.RangeDescription);
            Console.WriteLine($"  黑电平 {effective.Color.BlackCode:0} / 白电平 {effective.Color.WhiteCode:0}（8 位码值）"
                            + $" → 码值 16 = {effective.Color.CodeToIre(16):0.0} IRE、"
                            + $"235 = {effective.Color.CodeToIre(235):0.0} IRE");
        }

        Check(effective.Color.IsRangeUnknown || effective.Color.WhiteCode > effective.Color.BlackCode,
            "量化范围可换算 IRE", effective.Color.RangeDescriptionWithSource);

        return Report();
    }

    /// <summary>从「键/类型/值」清单里按 GUID 找一个 UInt64 值（交叉核对用）。</summary>
    private static bool Lookup(IReadOnlyList<(Guid Key, AttributeType Type, string Value)> attributes, Guid key, out ulong value)
    {
        foreach ((Guid k, AttributeType _, string text) in attributes)
        {
            if (k == key && ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                return true;
            }
        }
        value = 0;
        return false;
    }

    private static void PrintColorMetadata(string label, CaptureFormat format)
    {
        VideoColorInfo c = format.Color;
        Console.WriteLine($"  {label}：");
        Console.WriteLine($"    量化范围 : {c.RangeDescriptionWithSource,-38} {(c.RangeFromDriver ? "← 驱动" : "← 推断/缺失")}");
        Console.WriteLine($"    原色     : {c.DescribePrimaries(),-38} {(c.PrimariesFromDriver ? "← 驱动" : "← 缺失")}");
        Console.WriteLine($"    传输函数 : {c.DescribeTransferFunction(),-38} {(c.TransferFunctionFromDriver ? "← 驱动" : "← 缺失")}");
        Console.WriteLine($"    YCbCr矩阵: {c.DescribeMatrix(),-38} {(c.MatrixFromDriver ? "← 驱动" : "← 缺失")}");
    }

    /// <summary>驱动没给量化范围时的经验推断（写在这里，界面与示波器共用同一套说法）。</summary>
    private static string InferRange(string subtypeName) => subtypeName switch
    {
        "MJPG" => "0–255（full range）",
        "NV12" or "YUY2" or "UYVY" or "YVYU" or "I420" or "YV12" => "16–235（limited / 视频范围）",
        _ => "16–235（limited，按广播约定保守处理）",
    };

    // ------------------------------------------------------------------
    //  诊断：把「怎么打开一台设备」的三条路都试一遍，把结论留成证据
    // ------------------------------------------------------------------
    private static int Probe(string[] args)
    {
        string deviceFragment = Option(args, "--device") ?? DefaultDeviceNameFragment;

        using var mf = MediaFoundationRuntime.Start();
        CaptureDeviceInfo info = VideoDeviceEnumerator.FindByName(deviceFragment)
            ?? throw new InvalidOperationException($"找不到「{deviceFragment}」");

        Console.WriteLine($"目标设备 : [{info.Index}] {info.FriendlyName}");
        Console.WriteLine($"符号链接 : {info.SymbolicLink}");
        Console.WriteLine();

        // ---- 路线 A：在集合还活着的时候 ActivateObject ----
        Console.WriteLine("路线 A：枚举 → 集合 `using` 内 ActivateObject<IMFMediaSource>()");
        try
        {
            using IMFActivateCollection collectionA = MediaFactory.MFEnumVideoDeviceSources();
            IMFActivate? target = null;
            int i = 0;
            foreach (IMFActivate a in collectionA)
            {
                if (i == info.Index)
                {
                    target = a;
                    break;
                }
                i++;
            }
            if (target is null)
            {
                Console.WriteLine("  ✗ 集合里没找到目标");
            }
            else
            {
                using IMFMediaSource source = target.ActivateObject<IMFMediaSource>();
                Console.WriteLine($"  ✓ 成功，IMFMediaSource = {source.NativePointer:X}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ✗ {ex.GetType().Name}: {ex.Message}");
        }

        // ---- 路线 B：把子对象带出集合（集合已被 Dispose）----
        Console.WriteLine();
        Console.WriteLine("路线 B：把 IMFActivate 带出集合（集合 `using` 结束后再 ActivateObject）");
        try
        {
            IMFActivate? carried = TakeCarried(info.Index);
            Console.WriteLine($"  带出来的 IMFActivate.NativePointer = {(carried is null ? "null" : carried.NativePointer.ToString("X"))}");
            using IMFMediaSource source = carried!.ActivateObject<IMFMediaSource>();
            Console.WriteLine($"  ✓ 成功，IMFMediaSource = {source.NativePointer:X}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ✗ {ex.GetType().Name}: {ex.Message}");
        }

        // ---- 路线 C：MFCreateDeviceSource（按符号链接直接建媒体源）----
        Console.WriteLine();
        Console.WriteLine("路线 C：MFCreateDeviceSource(属性集{SymbolicLink})");
        try
        {
            using IMFAttributes attributes = MediaFactory.MFCreateAttributes(2);
            attributes.Set(CaptureDeviceAttributeKeys.SourceType, CaptureDeviceAttributeKeys.SourceTypeVidcap);
            attributes.Set(CaptureDeviceAttributeKeys.SourceTypeVidcapSymbolicLink, info.SymbolicLink);
            using IMFMediaSource source = MediaFactory.MFCreateDeviceSource(attributes);
            Console.WriteLine($"  ✓ 成功，IMFMediaSource = {source.NativePointer:X}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ✗ {ex.GetType().Name}: {ex.Message}");
        }

        return Report();
    }

    /// <summary>路线 B 用的「把子对象带出集合」写法（故意保留这个错误示范）。</summary>
    private static IMFActivate? TakeCarried(int deviceIndex)
    {
        using IMFActivateCollection collection = MediaFactory.MFEnumVideoDeviceSources();
        int index = 0;
        foreach (IMFActivate activate in collection)
        {
            if (index == deviceIndex)
            {
                return activate;
            }
            index++;
        }
        return null;
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

    /// <summary>取 --name value 形式的选项；没有就返回 null。</summary>
    internal static string? Option(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }
        return null;
    }
}
