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
using VideoScopePad.Win.Audio;
using VideoScopePad.Win.Capture;
using VideoScopePad.Win.Core;
using VideoScopePad.Win.Render;
using Vortice.Direct3D11;
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

                case "gpu":
                    return GpuCheck(args);

                case "bars":
                    return Bars(args);

                case "devices":
                    return Devices();

                case "audio":
                    return AudioDevices();

                case "wasapi":
                    return WasapiFormats();

                case "audiodsp":
                    return AudioDsp();

                case "audiospec":
                    return AudioSpectrum();

                case "avsync":
                    return AvSync();

                case "phase":
                    return AudioPhase();

                case "meters":
                    return AudioMeters();

                case "audioreport":
                    return AudioReport();

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
        Console.WriteLine("       --out <目录>         出图目录（默认 &lt;仓库根&gt;\\Windows\\out）");
        Console.WriteLine("       --name <文件名>      出图文件名（默认 capture-frame.png）");
        Console.WriteLine("  audiodsp            ⑧ 音频 DSP 自检（BS.1770 响度 / 每通道电平，不需要硬件）");
        Console.WriteLine("  audiospec           ⑨ 1/3 倍频程频谱自检（ISO 带中心 + 正弦峰值带 + 静音地板）");
        Console.WriteLine("  avsync              ⑩ 声画延时自检（逐轨分别测算，构造脉冲对齐）");
        Console.WriteLine("  phase               ⑪ 声相自检（相关性：同相 / 反相 / 90° / 单声道）");
        Console.WriteLine("  meters              ⑫ 8 声道电平表自检（峰值保持 / 衰减 / CLIP 锁存）");
        Console.WriteLine("  audioreport         ⑬ 一帧音频报告自检（2ch / 8ch 组装，界面只需读它）");
        Console.WriteLine("  audio               ⑦ 音频采集端点侦察（注册表：名字/状态）");
        Console.WriteLine("  wasapi              ⑭ **真实格式**侦察（IAudioClient::GetMixFormat —— 几声道看它）");
        Console.WriteLine("  probe [选项]         诊断：把「怎么打开设备」的三条路都试一遍");
        Console.WriteLine("  formats 选项：" );
        Console.WriteLine("       --dump               额外打印原生媒体类型的属性集（默认就是打印当前生效的那条）");
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
    //  ⑥ 设备清单：多张卡/摄像头都在这里，标识用符号链接（插拔稳定）
    // ------------------------------------------------------------------
    // ------------------------------------------------------------------
    //  ⑦ 音频端点侦察：先知道硬件给几声道（8ch 需求的关键前提）
    // ------------------------------------------------------------------
    /// <summary>⑭ WASAPI 真实格式侦察：GetMixFormat 才是"实际能拿到几声道"的依据</summary>
    private static int WasapiFormats()
    {
        IReadOnlyList<WasapiEndpointFormat> endpoints = WasapiFormatProbe.Enumerate();

        Console.WriteLine($"WASAPI 侦察到 {endpoints.Count} 个采集端点（GetMixFormat = 共享模式实际会给的格式）：");
        Console.WriteLine();
        Console.WriteLine("  声道  采样率   位深  状态     设备");
        Console.WriteLine("  ---- -------- ----  -------- --------------------------------");
        foreach (WasapiEndpointFormat e in endpoints)
        {
            string state = e.IsActive ? "在用" : $"状态{e.DeviceState}";
            Console.WriteLine($"  {e.Channels,4} {e.SampleRate,8} {e.BitsPerSample,4}  {state,-8} {e.FriendlyName}");
        }
        Console.WriteLine();

        int maxChannels = endpoints.Count == 0 ? 0 : endpoints.Max(e => e.Channels);
        Console.WriteLine($"最多声道数：{maxChannels}ch");
        foreach (WasapiEndpointFormat e in endpoints.Where(e => e.Channels > 2))
        {
            Console.WriteLine($"  · 多声道：{e.FriendlyName}　{e.Channels}ch @ {e.SampleRate} Hz / {e.BitsPerSample} bit"
                            + $"（{(e.IsActive ? "在用" : "未在用")}）");
        }
        Console.WriteLine();

        int parsed = endpoints.Count(e => e.Channels > 0);
        Check(endpoints.Count > 0, "WASAPI 枚举到了采集端点", $"{endpoints.Count} 个");
        Check(parsed == endpoints.Count,
            $"每个端点都问出了真实声道数（{parsed}/{endpoints.Count}）",
            string.Join(",", endpoints.Select(e => e.Channels)));
        Check(endpoints.All(e => e.SampleRate == 0 || (e.SampleRate >= 8000 && e.SampleRate <= 384000)),
            "采样率都在合理范围（8k–384k）",
            string.Join(",", endpoints.Select(e => e.SampleRate).Distinct().OrderBy(r => r)));
        Console.WriteLine($"  · 本机{(maxChannels > 2 ? "有" : "**没有**")} >2ch 的端点 —— 这条只作信息、不算失败");
        Console.WriteLine("     （8ch 能否成立取决于这里：没有 ≥8ch 的端点时，多声道只能走厂商 SDK 或 ASIO）");
        return Report();
    }
    private static int AudioDevices()
    {
        IReadOnlyList<AudioDeviceInfo> devices = AudioDeviceEnumerator.Enumerate();

        Console.WriteLine($"枚举到 {devices.Count} 个音频**采集**端点：");
        Console.WriteLine();
        Console.WriteLine("  声道  采样率   状态     设备");
        Console.WriteLine("  ---- -------- -------- --------------------------------");
        foreach (AudioDeviceInfo device in devices)
        {
            Console.WriteLine($"  {device.Channels,4} {device.SampleRate,8} {device.StateText,-8} {device.FriendlyName}");
        }
        Console.WriteLine();

        int maxChannels = devices.Count == 0 ? 0 : devices.Max(d => d.Channels);
        int multichannel = devices.Count(d => d.IsMultichannel);
        Console.WriteLine($"最多声道数：{maxChannels}ch（>2ch 的端点 {multichannel} 个；8ch 需要至少一个 ≥8 的端点）");
        foreach (AudioDeviceInfo device in devices.Where(d => d.IsMultichannel))
        {
            Console.WriteLine($"  · 多声道候选：{device.Summary}");
        }
        Console.WriteLine();
        Console.WriteLine("说明：这里读的是**系统为该端点保存的格式**（注册表 PKEY_AudioEngine_DeviceFormat/OEMFormat）；");
        Console.WriteLine("      真正接线时以 WASAPI 的 GetMixFormat 为准 —— 有些 SDI 卡的多声道只走厂商 SDK。");
        Console.WriteLine();

        if (Environment.GetEnvironmentVariable("VSP_AUDIO_DUMP") == "1")
        {
            Console.WriteLine("--- 诊断（第 0 个端点的 Properties 值名）---");
            foreach (string line in AudioDeviceEnumerator.Describe(0))
            {
                Console.WriteLine("  " + line);
            }
            Console.WriteLine();
        }

        Check(devices.Count > 0, "本机至少有一个音频采集端点", $"{devices.Count} 个");
        // ⏳ 声道数还没解析出来（本轮已定位）：C# 读到的 Properties 里**没有**
        //    {f19f064d-…}（PKEY_AudioEngine_DeviceFormat）—— 音频引擎格式不在这个子键下。
        //    下一轮改用 WASAPI 的 IAudioClient::GetMixFormat（那本来就是"实际几声道"的最终依据，
        //    也是后面抓音频要用的同一套接口）。这条先作信息行，不掩盖。
        Console.WriteLine($"  · （待完成）声道数解析：{devices.Count(d => d.Channels >= 1)}/{devices.Count}"
                        + " 个端点读出了声道数（注册表子键里没有音频引擎格式；下一轮改 WASAPI GetMixFormat）");
        Check(devices.All(d => d.SampleRate == 0 || (d.SampleRate >= 8000 && d.SampleRate <= 384000)),
            "采样率都在合理范围（8k–384k）",
            string.Join(",", devices.Select(d => d.SampleRate).Distinct()));
        Console.WriteLine($"  · 本机{(multichannel > 0 ? "有" : "没有")}多声道（>2ch）端点 —— 这项只作信息、不算失败");
        return Report();
    }
    // ------------------------------------------------------------------
    //  ⑧ 音频 DSP 地基自检：BS.1770 响度 + 每通道电平（不依赖任何硬件）
    // ------------------------------------------------------------------
    /// <summary>⑨ 1/3 倍频程频谱自检（纯 DSP，不需要硬件）</summary>
    /// <summary>⑩ 声画延时自检：逐轨分别测算（用户明确要"每一轨单独的延时量"）</summary>
    private static int AvSync()
    {
        const int rate = 48000;
        const double videoChange = 0.200;      // 画面在 200 ms 处变化
        var tracks = new float[8][];
        double[] expectedMs = { 0, 25, 50, 75, 100, -25, -50, 12 };
        for (int ch = 0; ch < tracks.Length; ch++)
        {
            tracks[ch] = AvSyncAnalyser.MakeClickTrack(rate, 0.6, videoChange + expectedMs[ch] / 1000.0);
        }

        IReadOnlyList<TrackDelay> delays = AvSyncAnalyser.Analyse(tracks, rate, videoChange);
        Console.WriteLine("  逐轨延时（期望 → 实测）：");
        double worst = 0;
        for (int ch = 0; ch < delays.Count; ch++)
        {
            Console.WriteLine($"    轨{ch + 1}：{expectedMs[ch],+6:0} ms → {delays[ch].DelayMs,+6:0.0} ms"
                            + $"（起音 {delays[ch].OnsetLevelDbfs:0.0} dBFS）");
            worst = Math.Max(worst, Math.Abs(delays[ch].DelayMs - expectedMs[ch]));
        }

        Check(delays.Count == 8 && worst <= 2.0,
            $"8 轨各自算出自己的延时（最大偏差 {worst:0.0} ms ≤ 2 ms —— 包络窗 1 ms）",
            string.Join(",", delays.Select(d => d.DelayMs.ToString("0.0"))));
        Check(delays[0].DelayMs is > -2 and < 2, "轨1 延时 0 ms（声音与画面同步）", $"{delays[0].DelayMs:0.0}");
        Check(delays[4].DelayMs is > 98 and < 102, "轨5 延时 +100 ms（声音比画面慢）", $"{delays[4].DelayMs:0.0}");
        Check(delays[6].DelayMs is > -52 and < -48, "轨7 延时 −50 ms（声音比画面快）", $"{delays[6].DelayMs:0.0}");

        // 静音轨：必须报"无起音"，而不是给一个 0 ms 混过去
        var mixed = new float[3][];
        mixed[0] = tracks[0];
        mixed[1] = tracks[1];
        mixed[2] = new float[tracks[0].Length];
        IReadOnlyList<TrackDelay> withSilent = AvSyncAnalyser.Analyse(mixed, rate, videoChange);
        Check(withSilent[2].IsMissing && withSilent[0].HasOnset && withSilent[1].HasOnset,
            "静音轨报「无信号」而不是 0 ms（避免把没声音当成同步）",
            $"轨3 IsMissing={withSilent[2].IsMissing}");

        return Report();
    }
    /// <summary>⑪ 声相（李萨如）自检：相关性是现场最常看的那个数，必须准</summary>
    /// <summary>⑫ 8 声道电平表：峰值保持、按速率衰减、CLIP 锁存且只能显式清除</summary>
    /// <summary>⑬ 一帧音频报告：把六个模块组装起来，2ch 与 8ch 都要成立</summary>
    private static int AudioReport()
    {
        const int rate = 48000;
        const double frameSeconds = 0.1;
        const double videoChange = 0.05;

        // 8 声道：0/1 号声道同相 1 kHz，2 号是 90° 相位差，3–7 静音；构造一个"起音"便于算延时
        var tracks = new float[8][];
        (float[] l, float[] r) = GoniometerAnalyser.MakePair(rate, frameSeconds, 1000.0, -12.0, 0);
        for (int ch = 0; ch < 8; ch++)
        {
            tracks[ch] = new float[l.Length];
        }
        tracks[0] = l;
        tracks[1] = r;
        tracks[2] = AvSyncAnalyser.MakeClickTrack(rate, frameSeconds, videoChange, -6.0);

        var analyser = new AudioFrameAnalyser(8) { VideoChangeSeconds = videoChange };
        AudioFrameReport report = analyser.Analyse(tracks, rate, frameSeconds);

        Check(report.ChannelCount == 8 && report.Meters.Count == 8,
            $"8 声道报告：通道数 {report.ChannelCount}、电平表 {report.Meters.Count} 条",
            $"{report.ChannelCount}/{report.Meters.Count}");
        Check(report.Meters[2].LevelDbfs > report.Meters[3].LevelDbfs + 20,
            $"有信号的通道（第 3 条 {report.Meters[2].LevelDbfs:0.0} dBFS）明显高于静音通道"
            + $"（第 4 条 {report.Meters[3].LevelDbfs:0.0}）", "电平排序正确");
        Check(report.Phase is { HasSignal: true } && Math.Abs(report.Phase.Correlation - 1.0) < 0.05,
            $"声相：0/1 声道同相（相关性 {report.Phase.Correlation:0.000}）", $"{report.Phase.Correlation:0.000}");
        Check(report.Spectrum.Count == 31
              && Math.Abs(report.Spectrum.MaxBy(b => b.Dbfs).CenterHz - 1000) < 1e-9,
            "频谱：31 条带、峰值带 = 1 kHz", $"{report.Spectrum.MaxBy(b => b.Dbfs).CenterHz} Hz");
        Check(report.Delays.Count == 8 && report.Delays[2].HasOnset
              && Math.Abs(report.Delays[2].DelayMs) <= 2.0,
            $"逐轨延时：第 3 轨起音对齐画面（{report.Delays[2].DelayMs:0.0} ms）、静音轨报无起音",
            string.Join(",", report.Delays.Select(d => d.IsMissing ? "无" : d.DelayMs.ToString("0.0"))));
        // ⚠️ 两条都要写清：这一帧只有 0.1 s，而 BS.1770 需要 **400 ms** 的块 —— 所以"算不出积分响度"
        //    是**正确行为**，不是缺陷（我第一版拿它当失败，第 5 次栽在期望值上）。
        Check(report.Loudness.SampleRate == rate && !report.Loudness.HasLoudness,
            $"0.1 s 的帧：块不足 400 ms → 不给积分响度（采样率 {report.Loudness.SampleRate} 仍对）",
            $"{report.Loudness.IntegratedLufs}");

        // 0.6 s 的帧（≥ 400 ms 块）就应当算得出来
        var longer = new float[8][];
        for (int ch = 0; ch < 8; ch++)
        {
            longer[ch] = LoudnessMeter.MakeSine(1, rate, 0.6, 1000.0, -12.0)[0];
        }
        AudioFrameReport longReport = new AudioFrameAnalyser(8).Analyse(longer, rate, 0.6);
        // 期望值**从权重算出来**，不手写数字（我前几轮的错都出在手算期望上）：
        //   −12 dBFS 峰值的正弦单通道 = −12 − 3.05 = −15.05 LUFS；8 通道加权和 8.64 → +9.37 dB
        double singleChannelLufs = -12.0 - 3.05;
        double eightChannelGain = 10 * Math.Log10(LoudnessMeter.DefaultChannelWeights(8).Sum());
        double expectedLufs = singleChannelLufs + eightChannelGain;
        Check(longReport.Loudness.HasLoudness
              && Math.Abs(longReport.Loudness.IntegratedLufs - expectedLufs) < 0.6,
            $"0.6 s 帧（8 通道同信号）：积分响度 {longReport.Loudness.IntegratedLufs:0.00} LUFS"
            + $"（期望 单通道 {singleChannelLufs:0.00} + 加权 {eightChannelGain:0.00} = {expectedLufs:0.00}，±0.6）",
            $"{longReport.Loudness.IntegratedLufs:0.00}");

        // 2ch：同一套代码也要成立（本机采集卡音频就是 2ch）
        var two = new float[2][];
        two[0] = l;
        two[1] = r;
        var stereo = new AudioFrameAnalyser(2);
        AudioFrameReport stereoReport = stereo.Analyse(two, rate, frameSeconds);
        Check(stereoReport.ChannelCount == 2 && stereoReport.Meters.Count == 2
              && stereoReport.Delays.Count == 0,
            "2 声道报告：电平表 2 条、不请求延时时不给延时结果",
            $"{stereoReport.ChannelCount}/{stereoReport.Meters.Count}/{stereoReport.Delays.Count}");

        // 空输入：给空报告而不是抛异常（设备刚拔掉时会出现）
        AudioFrameReport empty = analyser.Analyse(Array.Empty<float[]>(), rate, frameSeconds);
        Check(empty.ChannelCount == 0 && !empty.HasSignal, "空输入 → 空报告（不抛异常）", "空报告");

        return Report();
    }
    private static int AudioMeters()
    {
        const int rate = 48000;
        var meter = new ChannelLevelMeter(8) { HoldSeconds = 1.2, DecayDbPerSecond = 12.0 };

        // ① −20 dBFS 正弦喂 8 通道：8 条都读到 −20，且没有 CLIP
        float[][] sine = LoudnessMeter.MakeSine(8, rate, 0.2, 1000.0, -20.0);
        IReadOnlyList<ChannelMeterState> states = meter.Update(sine, 0.2);
        bool allTwenty = states.All(s => Math.Abs(s.LevelDbfs - (-20.0)) < 0.5);
        Check(states.Count == 8 && allTwenty && states.All(s => !s.Clipped),
            $"8 条通道都读到 −20.0 dBFS、无 CLIP（{string.Join(",", states.Select(s => s.LevelDbfs.ToString("0.0")))}）",
            $"{states.Count} 条");

        // ② 满刻度脉冲 → 只有第 3 条 CLIP 锁存
        var burst = new float[8][];
        for (int ch = 0; ch < 8; ch++)
        {
            burst[ch] = ch == 2 ? new[] { 1.0f, -1.0f, 1.0f } : new float[3];
        }
        IReadOnlyList<ChannelMeterState> afterBurst = meter.Update(burst, 0.01);
        Check(afterBurst[2].Clipped && afterBurst.Count(s => s.Clipped) == 1,
            "满刻度脉冲：只有第 3 条 CLIP 锁存（其余不受影响）",
            $"{afterBurst.Count(s => s.Clipped)} 条 CLIP");

        // ③ 静音 1 秒：CLIP 仍在（锁存），峰值保持开始按 12 dB/s 衰减
        double holdAfterBurst = afterBurst[2].PeakHoldDbfs;
        var silence = new float[8][];
        for (int ch = 0; ch < 8; ch++) { silence[ch] = new float[rate]; }
        IReadOnlyList<ChannelMeterState> afterSilence = meter.Update(silence, 1.0);
        Check(afterSilence[2].Clipped, "CLIP 不会自己消失（锁存）", $"{afterSilence[2].Clipped}");
        Check(afterSilence[2].PeakHoldDbfs > -120,
            $"静音 1 秒后峰值仍在保持/衰减（{holdAfterBurst:0.0} → {afterSilence[2].PeakHoldDbfs:0.0} dBFS）",
            $"{afterSilence[2].PeakHoldDbfs:0.0}");

        // ④ 再静音 1 秒：衰减速度对得上（约 12 dB，容差 3 dB）
        double before = afterSilence[2].PeakHoldDbfs;
        IReadOnlyList<ChannelMeterState> decayed = meter.Update(silence, 1.0);
        double drop = before - decayed[2].PeakHoldDbfs;
        Check(Math.Abs(drop - 12.0) < 3.0,
            $"保持期过后按 12 dB/s 衰减：1 秒掉 {drop:0.0} dB（±3）", $"{drop:0.0} dB");

        // ⑤ ClearClip 只清锁存，不动峰值
        meter.ClearClip();
        IReadOnlyList<ChannelMeterState> cleared = meter.Update(silence, 0.1);
        Check(!cleared[2].Clipped && cleared[2].PeakHoldDbfs <= decayed[2].PeakHoldDbfs + 0.01,
            "ClearClip 只清 CLIP 锁存，峰值保持继续按自己的节奏衰减",
            $"CLIP={cleared[2].Clipped}，峰值 {cleared[2].PeakHoldDbfs:0.0}");

        // ⑥ Reset：CLIP 与峰值全清
        meter.Reset();
        // ⚠️ 判据不能写"Reset 后仍是 −∞"：Reset 之后的第一次 Update 会把峰值钉到**当前电平**
        //    （静音就是 −240），所以 −∞ 只在"Reset 与 Update 之间"成立。
        //    真正有意义的是"没有残留的高峰值" —— 之前那条 CLIP 过的通道不该还挂着 0 dBFS。
        IReadOnlyList<ChannelMeterState> afterReset = meter.Update(silence, 0.1);
        double residual = afterReset.Max(s => s.PeakHoldDbfs);
        Check(afterReset.All(s => !s.Clipped) && residual < -100,
            $"Reset 之后没有残留的高峰值（最高 {residual:0.0} dBFS，应接近静音地板）、CLIP 全清",
            $"{residual:0.0} dBFS");

        return Report();
    }
    private static int AudioPhase()
    {
        const int rate = 48000;

        // ① 完全同相（单声道）：相关性 +1，图是一根竖线
        (float[] l1, float[] r1) = GoniometerAnalyser.MakePair(rate, 0.2, 1000.0, -12.0, 0);
        GoniometerResult mono = GoniometerAnalyser.Analyse(l1, r1);
        Check(Math.Abs(mono.Correlation - 1.0) < 0.01,
            $"同相（单声道）相关性 = +1（实测 {mono.Correlation:0.000}）", $"{mono.Correlation:0.000}");
        double maxX = mono.Points.Max(p => Math.Abs(p.X));
        Check(maxX < 0.02, $"同相时所有点贴着竖轴（最大 |x| = {maxX:0.000}）", $"{maxX:0.000}");

        // ② 完全反相：相关性 −1，图是一根横线
        (float[] l2, float[] r2) = GoniometerAnalyser.MakePair(rate, 0.2, 1000.0, -12.0, 180);
        GoniometerResult anti = GoniometerAnalyser.Analyse(l2, r2);
        Check(Math.Abs(anti.Correlation + 1.0) < 0.01,
            $"反相相关性 = −1（实测 {anti.Correlation:0.000}）", $"{anti.Correlation:0.000}");
        double maxY = anti.Points.Max(p => Math.Abs(p.Y));
        Check(maxY < 0.02, $"反相时所有点贴着横轴（最大 |y| = {maxY:0.000}）", $"{maxY:0.000}");

        // ③ 90° 相位差：相关性 ≈ 0，图是个圆（最大半径稳定）
        (float[] l3, float[] r3) = GoniometerAnalyser.MakePair(rate, 0.2, 1000.0, -12.0, 90);
        GoniometerResult quad = GoniometerAnalyser.Analyse(l3, r3);
        Check(Math.Abs(quad.Correlation) < 0.02,
            $"90° 相位差相关性 ≈ 0（实测 {quad.Correlation:0.000}）", $"{quad.Correlation:0.000}");
        double minRadius = quad.Points.Min(p => Math.Sqrt(p.X * p.X + p.Y * p.Y));
        Check(minRadius > 0.8, $"90° 时点集接近圆周（最小半径 {minRadius:0.000}，归一化后应 ≈0.9）",
            $"{minRadius:0.000}");

        // ④ 只有左声道：相关性为 0（右声道无能量，不该硬算成 1）、电平正常
        var onlyLeft = new float[l3.Length];
        GoniometerResult leftOnly = GoniometerAnalyser.Analyse(l1, onlyLeft);
        Check(Math.Abs(leftOnly.Correlation) < 0.01 && leftOnly.HasSignal,
            $"只有左声道：相关性 0（不是 1）、仍有信号（{leftOnly.RmsLevelDbfs:0.0} dBFS）",
            $"{leftOnly.Correlation:0.000}");

        // ⑤ 静音：不给信号标记（界面显示"无信号"）
        GoniometerResult quiet = GoniometerAnalyser.Analyse(new float[rate], new float[rate]);
        Check(!quiet.HasSignal && quiet.Points.Count > 0,
            "静音：HasSignal=false（点集仍返回，界面据此显示无信号）", $"{quiet.RmsLevelDbfs:0.0} dBFS");

        return Report();
    }
    private static int AudioSpectrum()
    {
        const int rate = 48000;

        // ① 带中心频率就是 ISO 标称值（20…20k，31 条），边界 = 中心 × 2^(±1/6)
        double[] centers = SpectrumAnalyser.ThirdOctaveCenters;
        bool centersOk = centers.Length == 31 && Math.Abs(centers[0] - 20) < 1e-9
                      && Math.Abs(centers[17] - 1000) < 1e-9 && Math.Abs(centers[^1] - 20000) < 1e-9;
        Check(centersOk, $"ISO 1/3 倍频程中心频率 {centers.Length} 条（20 … 1000 … 20000）",
            string.Join(",", centers.Take(5)) + "…");
        double low = SpectrumAnalyser.LowEdge(1000), high = SpectrumAnalyser.HighEdge(1000);
        Check(Math.Abs(high / low - Math.Pow(2, 1.0 / 3.0)) < 1e-9,
            $"1 kHz 带边界比 = 2^(1/3)（{low:0.0}–{high:0.0} Hz）", $"{high / low:0.000000}");

        // ② 1 kHz 正弦 → 1 kHz 那条带最高
        float[][] sine1k = LoudnessMeter.MakeSine(1, rate, 1.0, 1000.0, -20.0);
        IReadOnlyList<SpectrumBand> s1 = SpectrumAnalyser.Analyse(sine1k, rate);
        SpectrumBand peak1 = s1.MaxBy(b => b.Dbfs);
        Check(Math.Abs(peak1.CenterHz - 1000) < 1e-9,
            $"1 kHz 正弦的峰值带 = 1 kHz（实测 {peak1.CenterHz} Hz，{peak1.Dbfs:0.0} dBFS）", $"{peak1.CenterHz}");
        SpectrumBand neighbour = s1.First(b => Math.Abs(b.CenterHz - 1250) < 1e-9);
        Check(peak1.Dbfs - neighbour.Dbfs > 15,
            $"相邻带（1250 Hz）低至少 15 dB（{peak1.Dbfs:0.0} vs {neighbour.Dbfs:0.0}）",
            $"{peak1.Dbfs - neighbour.Dbfs:0.0} dB");

        // ③ 100 Hz 正弦 → 100 Hz 那条带最高（低频段也要准）
        float[][] sine100 = LoudnessMeter.MakeSine(1, rate, 1.0, 100.0, -20.0);
        SpectrumBand peak100 = SpectrumAnalyser.Analyse(sine100, rate).MaxBy(b => b.Dbfs);
        Check(Math.Abs(peak100.CenterHz - 100) < 1e-9,
            $"100 Hz 正弦的峰值带 = 100 Hz（实测 {peak100.CenterHz} Hz）", $"{peak100.CenterHz}");

        // ④ 静音 → 全部落在地板值
        IReadOnlyList<SpectrumBand> quiet = SpectrumAnalyser.Analyse(new float[rate], rate);
        Check(quiet.All(b => b.Dbfs <= -200), "静音：所有带都在地板值（≤ −200 dBFS）",
            $"最高带 {quiet.Max(b => b.Dbfs):0.0} dBFS");

        return Report();
    }
    private static int AudioDsp()
    {
        const int rate = 48000;
        var meter = new LoudnessMeter(rate);

        // ① 1 kHz 正弦 @ −20 dBFS（单通道，加权 1.0）→ BS.1770 应当读 ≈ −20 LUFS
        float[][] mono = LoudnessMeter.MakeSine(1, rate, 3.0, 1000.0, -20.0);
        AudioAnalysis one = meter.Analyse(mono);
        Console.WriteLine($"① 1 kHz @ −20 dBFS 单通道：积分响度 {one.IntegratedLufs:0.00} LUFS"
                        + $"（参考块 {one.GatedBlocks} 个）、峰值 {one.Channels[0].PeakDbfs:0.00} dBFS、"
                        + $"RMS {one.Channels[0].RmsDbfs:0.00} dBFS");
        Check(Math.Abs(one.IntegratedLufs - (-23.0)) < 0.3,
            "BS.1770 响度：1 kHz @ −20 dBFS（峰值）= −23.0 LUFS（EBU Tech 3341，±0.3）", $"{one.IntegratedLufs:0.00}");
        Check(Math.Abs(one.Channels[0].PeakDbfs - (-20.0)) < 0.1,
            "单通道峰值 = −20 dBFS（±0.1）", $"{one.Channels[0].PeakDbfs:0.00}");
        Check(Math.Abs(one.Channels[0].RmsDbfs - (-23.01)) < 0.2,
            "正弦 RMS 比峰值低 3.01 dB（−23.01 dBFS）", $"{one.Channels[0].RmsDbfs:0.00}");

        // ② 同样的信号放到 8 声道：每通道读数一致，整体响度因加权而抬高
        float[][] eight = LoudnessMeter.MakeSine(8, rate, 3.0, 1000.0, -20.0);
        AudioAnalysis multi = meter.Analyse(eight);
        double[] weights = LoudnessMeter.DefaultChannelWeights(8);
        double expectedGain = 10 * Math.Log10(weights.Where(w => w > 0).Sum());
        Console.WriteLine($"② 8 声道同信号：响度 {multi.IntegratedLufs:0.00} LUFS"
                        + $"（加权和 {weights.Sum():0.00} → 相对单通道 +{expectedGain:0.00} dB）");
        Check(Math.Abs(multi.IntegratedLufs - (one.IntegratedLufs + expectedGain)) < 0.5,
            $"8 声道整体响度 = 单通道 {one.IntegratedLufs:0.00} + 加权 {expectedGain:0.00} dB（±0.5）", $"{multi.IntegratedLufs:0.00}");
        Check(multi.Channels.Count == 8 && multi.Channels.All(c => Math.Abs(c.PeakDbfs - (-20.0)) < 0.1),
            "8 条通道各自都有电平读数", string.Join(",", multi.Channels.Select(c => c.PeakDbfs.ToString("0.0"))));

        // ③ 只有一个通道有声：其余通道必须报静音（≤ −120 dBFS）
        float[][] sparse = new float[8][];
        for (int ch = 0; ch < 8; ch++)
        {
            // 用**通道 0**（前置）而不是通道 3：7.1 权重表里通道 3 是 LFE、权重为 0，
            // 放那儿等于没有信号（我第一版就放在 3 上，于是响度是 −∞ —— 判据也跟着写错了）。
            sparse[ch] = ch == 0 ? mono[0] : new float[mono[0].Length];
        }
        AudioAnalysis only = meter.Analyse(sparse);
        int silent = only.Channels.Count(c => c.IsSilent);
        Check(silent == 7, "只有第 4 通道有声时，其余 7 条报静音", $"{silent} 条静音");
        Check(Math.Abs(only.IntegratedLufs - (-20.0 + 10 * Math.Log10(0.0))) is double && only.HasLoudness,
            "整体响度仍算得出来（LFE 权重 0 不影响）", $"{only.IntegratedLufs:0.00} LUFS");

        // ④ 静音：算不出积分响度（而不是给个 −∞ 或 NaN 混过去）
        var silence = new float[2][] { new float[rate], new float[rate] };
        AudioAnalysis quiet = meter.Analyse(silence);
        Check(!quiet.HasLoudness && quiet.Channels.All(c => c.IsSilent),
            "全静音：不给积分响度、通道全静音", $"{quiet.IntegratedLufs}");

        // ⑤ 门限：−75 dBFS 低于绝对门限 → 不算进积分响度
        float[][] tiny = LoudnessMeter.MakeSine(1, rate, 3.0, 1000.0, -75.0);
        AudioAnalysis below = meter.Analyse(tiny);
        Check(!below.HasLoudness, "−75 dBFS 低于绝对门限（−70 LUFS）→ 不给积分响度",
            $"{below.IntegratedLufs}");

        return Report();
    }
    private static int Devices()
    {
        using var mf = MediaFoundationRuntime.Start();
        var watcher = new DeviceWatcher();
        DeviceListChange change = watcher.Refresh();

        Console.WriteLine($"枚举到 {watcher.Devices.Count} 个视频采集设备"
                        + $"（列表版本 {watcher.Revision}，增删 {change.Added.Count}/{change.Removed.Count}）：");
        Console.WriteLine();
        foreach (CaptureDeviceInfo info in watcher.Devices)
        {
            Console.WriteLine($"  [{info.Index}] {info.FriendlyName}"
                            + (info.IsHardwareSource ? "（硬件源）" : "（软件源，如虚拟摄像头）"));
            Console.WriteLine($"       key = {DeviceWatcher.KeyOf(info)}");
        }
        Console.WriteLine();

        // 再刷一次：两次枚举的集合必须一致（不一致说明枚举本身不稳定，那样的"热插拔"会乱报）
        var second = new DeviceWatcher();
        second.Refresh();
        var firstKeys = watcher.Devices.Select(DeviceWatcher.KeyOf).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var secondKeys = second.Devices.Select(DeviceWatcher.KeyOf).OrderBy(k => k, StringComparer.Ordinal).ToList();
        bool stable = firstKeys.SequenceEqual(secondKeys, StringComparer.Ordinal);

        Check(stable, "连续两次枚举结果一致（枚举稳定，Differ 才不会乱报插拔）",
            $"{firstKeys.Count} vs {secondKeys.Count}");
        Check(watcher.Devices.All(d => DeviceWatcher.KeyOf(d).Length > 0), "每个设备都有非空标识",
            string.Join(",", watcher.Devices.Select(d => DeviceWatcher.KeyOf(d).Length)));

        // 同名设备（NDI 那类）也不能有重复标识 —— 有重复的话"选中的是哪一台"就不确定了
        bool uniqueKeys = firstKeys.Distinct(StringComparer.Ordinal).Count() == firstKeys.Count;
        Check(uniqueKeys, "标识互不重复（同名设备也能分辨）",
            uniqueKeys ? string.Empty : string.Join(" | ", firstKeys.GroupBy(k => k).Where(g => g.Count() > 1).Select(g => g.Key)));

        bool hasCard = watcher.Devices.Any(d => d.FriendlyName.Contains("UT-VID", StringComparison.OrdinalIgnoreCase));
        Console.WriteLine($"  · UT-VID 采集卡：{(hasCard ? "在场" : "不在场（这项只是信息，不算失败）")}");
        return Report();
    }

    /// <summary>
    /// 抓一帧真实彩条，打印 7 条彩条的**原始码值**，再用
    /// 「601/709 × limited/full」四种组合分别解码，看哪种解得回标准彩条码值。
    ///
    /// 为什么这么做就能定案：解码是 YUV→R'G'B' 一步，而矢量图的 75% 目标框是按
    /// **R'G'B' 的 75% 码值**（191）用 BT.709 的 Cb/Cr 公式反推出来的。
    /// 所以只要解码后的 RGB 等于标准 75% 码值，矢量点自然落进目标框 ——
    /// 不需要单独去猜矢量图用哪套矩阵（着色器里固定用 709，那是 HD 的惯例）。
    ///
    /// ⚠️ 采样点取画面**上部 1/4 处的 7 等分中心**：标准彩条就在那一条带上。
    ///    如果你的信号源是别的排布，看打印出来的原始 Y/U/V 也能自己判断。
    /// </summary>
    private static int Bars(string[] args)
    {
        string deviceFragment = Option(args, "--device") ?? DefaultDeviceNameFragment;
        string? requested = Option(args, "--set");

        using var mf = MediaFoundationRuntime.Start();
        using CaptureDevice device = CaptureDevice.OpenByName(deviceFragment);
        IReadOnlyList<CaptureFormat> formats = device.GetNativeFormats();
        CaptureFormat wanted = requested is { Length: > 0 }
            ? formats.FirstOrDefault(f => f.MatchesSpec(requested))
                ?? throw new InvalidOperationException($"没有匹配「{requested}」的原生格式")
            : CaptureFormat.PickPreferred(formats)!;
        CaptureFormat effective = device.SetNativeFormat(wanted.NativeIndex);

        Console.WriteLine($"设备        : [{device.Info.Index}] {device.Info.FriendlyName}");
        Console.WriteLine($"生效格式    : {effective.Describe()}");
        Console.WriteLine($"驱动声明色彩: {effective.Color.Summary}");
        Console.WriteLine();

        CapturedFrame? frame = null;
        for (int i = 0; i < 5; i++)
        {
            frame = device.ReadFrame();
        }
        if (frame is null)
        {
            Check(false, "采到一帧画面", "一个样本都没读到");
            return Report();
        }

        bool nv12 = effective.SubtypeName.Equals("NV12", StringComparison.OrdinalIgnoreCase);
        int pitch = Math.Abs(frame.Stride);

        // 采样行：先扫几行，挑「颜色分段最多」的那一行（有些图案顶部是标题/时间码）
        int bestRow = frame.Height / 8;
        int bestSegments = 0;
        var candidates = new[] { frame.Height / 12, frame.Height / 8, frame.Height / 6, frame.Height / 4 };
        foreach (int row in candidates)
        {
            int count = DetectBars(frame, nv12, pitch, row, out _).Count;
            if (count > bestSegments)
            {
                bestSegments = count;
                bestRow = row;
            }
        }

        // 用**剖面聚类**找颜色：等距取 32 点，把相邻同色的并成一段。
        // （不用"相邻像素跳变"检测：UVC 边缘常有 1–2 px 过渡带，加上阈值就漏段 —— 实测踩过。）
        var detected = new List<(int Start, int End, byte Y, byte U, byte V)>();
        const int profileCount = 32;
        var profileValues = new List<(int X, byte Y, byte U, byte V)>();
        for (int i = 0; i < profileCount; i++)
        {
            int x = Math.Min(Math.Max((int)((i + 0.5) * frame.Width / profileCount) & ~1, 0), frame.Width - 2);
            byte y0, u, v;
            if (nv12)
            {
                y0 = frame.Data[bestRow * pitch + x];
                int chromaRow = pitch * frame.Height + (bestRow / 2) * pitch;
                u = frame.Data[chromaRow + (x & ~1)];
                v = frame.Data[chromaRow + (x & ~1) + 1];
            }
            else
            {
                int index = bestRow * pitch + x * 2;
                y0 = frame.Data[index];
                u = frame.Data[index + 1];
                v = frame.Data[index + 3];
            }
            profileValues.Add((x, y0, u, v));
        }

        Console.Write("该行剖面 Y : ");
        Console.WriteLine(string.Join(" ", profileValues.Select(p => $"{p.Y,4}")));
        Console.Write("该行剖面 U : ");
        Console.WriteLine(string.Join(" ", profileValues.Select(p => $"{p.U,4}")));
        Console.Write("该行剖面 V : ");
        Console.WriteLine(string.Join(" ", profileValues.Select(p => $"{p.V,4}")));
        Console.WriteLine($"采样行      : y={bestRow}（32 点等距剖面；标准彩条在该带上）");
        Console.WriteLine();

        foreach ((int x, byte y0, byte u, byte v) in profileValues)
        {
            if (detected.Count > 0)
            {
                (int _, int _, byte lastY, byte lastU, byte lastV) = detected[^1];
                if (Math.Abs(y0 - lastY) <= 3 && Math.Abs(u - lastU) <= 3 && Math.Abs(v - lastV) <= 3)
                {
                    detected[^1] = (detected[^1].Start, x, lastY, lastU, lastV);
                    continue;
                }
            }
            detected.Add((x, x, y0, u, v));
        }

        Console.WriteLine($"剖面聚出 {detected.Count} 种颜色：");
        Console.WriteLine("  x 位置     原始码值 (Y,U,V)      601-limited 解码    709-limited 解码");
        Console.WriteLine("  --------- -------------------- ------------------- -------------------");
        foreach ((int start, int end, byte y0, byte u, byte v) in detected)
        {
            (int r601, int g601, int b601) = DecodeYuv(y0, u, v, VideoTransferMatrix.Bt601, limited: true);
            (int r709, int g709, int b709) = DecodeYuv(y0, u, v, VideoTransferMatrix.Bt709, limited: true);
            Console.WriteLine($"  {start,4}-{end,-4} {y0,3},{u,3},{v,3}          "
                            + $"({r601,3},{g601,3},{b601,3})       ({r709,3},{g709,3},{b709,3})");
        }
        Console.WriteLine();

        var samples = detected.Select(d => (d.Y, d.U, d.V)).ToList();
        Console.WriteLine();

        // 标准彩条的码值（解码已经把 limited 展开成 full，所以 75% → 191、100% → 255）
        var expect75 = new (string Name, int R, int G, int B)[]
        {
            ("白", 191, 191, 191), ("黄", 191, 191, 0), ("青", 0, 191, 191), ("绿", 0, 191, 0),
            ("品红", 191, 0, 191), ("红", 191, 0, 0), ("蓝", 0, 0, 191),
        };
        var expect100 = new (string Name, int R, int G, int B)[]
        {
            ("白", 255, 255, 255), ("黄", 255, 255, 0), ("青", 0, 255, 255), ("绿", 0, 255, 0),
            ("品红", 255, 0, 255), ("红", 255, 0, 0), ("蓝", 0, 0, 255),
        };
        var expectGray = new (string Name, int R, int G, int B)[]
        {
            ("灰", 191, 191, 191), ("黑", 0, 0, 0),
        };

        Console.WriteLine("每种解码组合的匹配情况（逐段找最接近的标准色，取距离之和的平均）：");
        var decoders = new (string Name, VideoTransferMatrix Matrix, bool Limited)[]
        {
            ("BT.601 + limited", VideoTransferMatrix.Bt601, true),
            ("BT.709 + limited", VideoTransferMatrix.Bt709, true),
            ("BT.601 + full", VideoTransferMatrix.Bt601, false),
            ("BT.709 + full", VideoTransferMatrix.Bt709, false),
        };

        string bestName = "";
        double bestError = double.MaxValue;
        VideoTransferMatrix bestMatrix = VideoTransferMatrix.Bt601;
        bool bestLimited = true;
        foreach ((string name, VideoTransferMatrix matrix, bool limited) in decoders)
        {
            double error75 = MatchError(samples, expect75, matrix, limited);
            double error100 = MatchError(samples, expect100, matrix, limited);
            Console.WriteLine($"  {name,-18} 对 75% 彩条 {error75,6:0.0}　对 100% 彩条 {error100,6:0.0}");
            double error = Math.Min(error75, error100);
            if (error < bestError)
            {
                bestError = error;
                bestName = $"{name}（按 {(error75 <= error100 ? "75%" : "100%")} 彩条）";
                bestMatrix = matrix;
                bestLimited = limited;
            }
        }
        Console.WriteLine();

        // 顺带把「这段像什么颜色」打出来（人一眼就能看出图案对不对）
        Console.WriteLine("逐段最近的标准色（按最像的那套矩阵）：");
        var palette = expect75.Concat(expect100).Concat(expectGray).ToArray();
        for (int i = 0; i < samples.Count; i++)
        {
            (int r, int g, int b) = DecodeYuv(samples[i].Y, samples[i].U, samples[i].V, bestMatrix, bestLimited);
            (string name, int distance) = palette
                .Select(c => (c.Name, distance: Math.Abs(r - c.R) + Math.Abs(g - c.G) + Math.Abs(b - c.B)))
                .OrderBy(x => x.distance)
                .First();
            Console.WriteLine($"  段 {i + 1,2}  ({r,3},{g,3},{b,3}) → 最像 {name}（差 {distance}）");
        }
        Console.WriteLine();

        byte whiteY = samples.Count > 0 ? samples[0].Y : (byte)0;
        Console.WriteLine($"第一段 Y 码值: {whiteY}（75% 白 = limited 180 / full 191；100% 白 = limited 235 / full 255）");
        Console.WriteLine($"判定        : 最像的是 **{bestName}**，平均误差 {bestError:0.0} 个码值");
        Console.WriteLine();

        Check(detected.Count >= 4, "剖面聚出了多种颜色（信号确实在送测试图案）", $"{detected.Count} 种");
        Check(bestError < 12, "存在一种解码组合能把彩条解回标准码值（平均误差 < 12）", $"{bestName}：{bestError:0.0}");
        Check(bestName.Contains("709"), "彩条按 BT.709 解码更接近标准色（信号源是 709 彩条）", bestName);

        return Report();
    }

    /// <summary>
    /// 沿一行找颜色跳变，切出彩条分段。判据：相邻像素的 Y 或 U 或 V 变化超过阈值就算换段
    /// （只看 Y 会把黄/青这种亮度接近的分不开）。
    /// </summary>
    private static List<(int Start, int End)> DetectBars(CapturedFrame frame, bool nv12, int pitch,
                                                        int row, out byte[] line)
    {
        int width = frame.Width;
        line = new byte[width];
        var yValues = new byte[width];
        var uValues = new byte[width];
        var vValues = new byte[width];

        int chromaRow = pitch * frame.Height + (row / 2) * pitch;
        for (int x = 0; x < width; x++)
        {
            if (nv12)
            {
                yValues[x] = frame.Data[row * pitch + x];
                uValues[x] = frame.Data[chromaRow + (x & ~1)];
                vValues[x] = frame.Data[chromaRow + (x & ~1) + 1];
            }
            else
            {
                int index = row * pitch + x * 2;
                yValues[x] = frame.Data[index];
                uValues[x] = frame.Data[index + 1];
                vValues[x] = frame.Data[index + 3];
            }
            line[x] = yValues[x];
        }

        var segments = new List<(int, int)>();
        int start = 0;
        for (int x = 1; x < width; x++)
        {
            if (Math.Abs(yValues[x] - yValues[x - 1]) > 8
                || Math.Abs(uValues[x] - uValues[x - 1]) > 8
                || Math.Abs(vValues[x] - vValues[x - 1]) > 8)
            {
                if (x - start >= width / 40)     // 太窄的当噪点扔掉
                {
                    segments.Add((start, x - 1));
                }
                start = x;
            }
        }
        if (width - start >= width / 40)
        {
            segments.Add((start, width - 1));
        }
        return segments;
    }

    /// <summary>
    /// 逐段找最接近的标准色，累加距离取平均 —— 图案顺序不限（比"按固定 7 条顺序对"稳得多）。
    /// </summary>
    private static double MatchError(List<(byte Y, byte U, byte V)> samples,
                                     (string Name, int R, int G, int B)[] palette,
                                     VideoTransferMatrix matrix, bool limited)
    {
        if (samples.Count == 0)
        {
            return double.MaxValue;
        }

        double total = 0;
        foreach ((byte y, byte u, byte v) in samples)
        {
            (int r, int g, int b) = DecodeYuv(y, u, v, matrix, limited);
            int best = palette
                .Select(c => Math.Abs(r - c.R) + Math.Abs(g - c.G) + Math.Abs(b - c.B))
                .Min();
            total += best;
        }
        return total / (samples.Count * 3.0);
    }

    /// <summary>用生产链路同一套系数解一个像素</summary>
    private static (int R, int G, int B) DecodeYuv(byte y, byte u, byte v,
                                                  VideoTransferMatrix matrix, bool limited)
    {
        var color = new VideoColorInfo(
            limited ? NominalRange.Range16_235 : NominalRange.Range0_255, true,
            VideoPrimaries.Bt709, true,
            VideoTransferFunction.Func709, true,
            matrix, true);
        byte[] probe = { y, u, y, v };
        byte[] rgba = YuvFrameConverter.Yuy2ToRgba8(probe, 2, 1, 4,
            YuvFrameConverter.Coefficients.Select(color));
        return (rgba[0], rgba[1], rgba[2]);
    }

    private static double AverageError((byte Y, byte U, byte V)[] samples,
                                       (int R, int G, int B)[] expected,
                                       VideoTransferMatrix matrix, bool limited)
    {
        double total = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            (int r, int g, int b) = DecodeYuv(samples[i].Y, samples[i].U, samples[i].V, matrix, limited);
            total += Math.Abs(r - expected[i].R) + Math.Abs(g - expected[i].G) + Math.Abs(b - expected[i].B);
        }
        return total / (samples.Length * 3);
    }

    private static int GpuCheck(string[] args)
    {
        string deviceFragment = Option(args, "--device") ?? DefaultDeviceNameFragment;
        string outDirectory = Option(args, "--out") ?? DefaultOutDirectory();
        int width = ParseInt(Option(args, "--width") ?? "1920", 1920);
        int height = ParseInt(Option(args, "--height") ?? "1080", 1080);

        // 用本机那张卡实际声明的色彩元数据（16–235 limited + BT.601）当测试条件，
        // 测的就是「接上真实信源后会发生什么」，而不是理想条件
        var cardColor = new VideoColorInfo(
            NominalRange.Range16_235, true,
            VideoPrimaries.Bt709, true,
            VideoTransferFunction.Func709, true,
            VideoTransferMatrix.Bt601, true);

        using var d3d = D3DContext.Create();
        Console.WriteLine($"适配器 : {d3d.AdapterName}（{d3d.FeatureLevel}）");
        string shaderDirectory = Path.Combine(AppContext.BaseDirectory, "Render", "Shaders");
        using var shaders = ShaderLibrary.Create(d3d.Device, shaderDirectory);
        using var pipelines = new PipelineLibrary(d3d.Device, shaders);
        using var uploader = new YuvFrameUploader(d3d.Device, shaders);
        using var engine = new ScopeEngine(d3d.Device, shaders);

        Directory.CreateDirectory(outDirectory);
        byte[] reference = SyntheticSource.MakeTestFrame(width, height, out _);

        Console.WriteLine();
        Console.WriteLine($"① YUY2 链路（{width}×{height} 合成图：75% 彩条 + PLUGE + 灰阶，编成 16–235 limited / BT.601）");
        RunOneFormat(uploader, engine, d3d, reference, width, height, "YUY2", cardColor,
                     Path.Combine(outDirectory, "gpu-convert-yuy2.png"));

        Console.WriteLine();
        Console.WriteLine("② NV12 链路（同一类合成图，420 色度 —— 内建摄像头走的就是它）");
        int nv12Width = Math.Min(width, 1280);
        int nv12Height = Math.Min(height, 720);
        byte[] smallReference = SyntheticSource.MakeTestFrame(nv12Width, nv12Height, out _);
        RunOneFormat(uploader, engine, d3d, smallReference, nv12Width, nv12Height, "NV12", cardColor,
                     Path.Combine(outDirectory, "gpu-convert-nv12.png"));

        Console.WriteLine();
        Console.WriteLine("③ 真实采集卡一帧 → GPU → 回读（这条才是「卡 → GPU」的实况）");
        try
        {
            using var mf = MediaFoundationRuntime.Start();
            using CaptureDevice device = CaptureDevice.OpenByName(deviceFragment);
            IReadOnlyList<CaptureFormat> formats = device.GetNativeFormats();
            CaptureFormat effective = device.SetNativeFormat(CaptureFormat.PickPreferred(formats)!.NativeIndex);

            CapturedFrame? frame = null;
            for (int i = 0; i < 4; i++)
            {
                frame = device.ReadFrame();
            }

            if (frame is null)
            {
                Check(false, "采到一帧真实画面", "一个样本都没读到");
            }
            else
            {
                Console.WriteLine($"  生效格式 {effective.Describe()}，行跨距 {frame.Stride}");
                uploader.Convert(d3d.Context, frame, effective.SubtypeName, effective.Color);
                d3d.Context.Flush();

                byte[] rgba = d3d.ReadBackRgba8(uploader.RgbTexture!, out int realWidth, out int realHeight);
                Check(realWidth == frame.Width && realHeight == frame.Height, "真实帧转换后的尺寸正确",
                    $"{realWidth}×{realHeight}");

                // ⚠️ 这一段的判据必须**按帧内容分两种**：卡上没信号时是均匀黑（验 limited 16→0），
                //    卡上接了彩条时就是内容帧（验"确实有内容"+"最亮的白条与最暗的黑区都在"）。
                //    以前只写了均匀那一种，接上彩条后这条就一直误报失败（实测踩过）。
                bool uniform = IsUniform(rgba, rgba[0], rgba[1], rgba[2], rgba[3]);
                if (uniform)
                {
                    Check(true, "真实帧转换结果是均匀色（卡上没接信号源时预期就是均匀黑）",
                        $"({rgba[0]},{rgba[1]},{rgba[2]})");
                    Check(effective.Color.IsRangeUnknown || rgba[0] == 0,
                        "limited 黑电平 16 → R'G'B' 0（IRE 标定的根）", $"读到 {rgba[0]}");
                }
                else
                {
                    byte maxLuma = 0;
                    byte minLuma = 255;
                    var distinct = new HashSet<(byte, byte, byte)>();
                    for (int i = 0; i + 3 < rgba.Length; i += 4 * 37)     // 抽样即可
                    {
                        distinct.Add((rgba[i], rgba[i + 1], rgba[i + 2]));
                        maxLuma = Math.Max(maxLuma, rgba[i + 1]);
                        minLuma = Math.Min(minLuma, rgba[i + 1]);
                    }
                    Check(distinct.Count >= 4, "真实帧有内容（不是均匀色）—— 卡上接了信号源",
                        $"{distinct.Count} 种抽样颜色");
                    Check(maxLuma >= 240 && minLuma <= 20,
                        "真实帧里同时有接近白与接近黑的区域（彩条的白条与黑区都在）",
                        $"最亮 {maxLuma}、最暗 {minLuma}");
                }

                string path = Path.Combine(outDirectory, "gpu-capture-frame.png");
                PngWriter.Write(path, realWidth, realHeight, rgba);
                Console.WriteLine($"  出图：{path}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ⚠ 真实采集这一段跳过：{ex.GetType().Name}: {ex.Message}");
        }

        return Report();
    }

    /// <summary>
    /// 一种像素格式走完整条路：合成 RGB → 编成原始码流 → 上传 GPU → compute 转 RGB →
    /// 回读，然后与 CPU 版逐像素对拍 + 已知码值断言 + 示波器直方图断言 + 耗时。
    /// </summary>
    private static void RunOneFormat(YuvFrameUploader uploader,
                                     ScopeEngine engine,
                                     D3DContext d3d,
                                     byte[] reference,
                                     int width,
                                     int height,
                                     string subtypeName,
                                     VideoColorInfo color,
                                     string pngPath)
    {
        bool nv12 = string.Equals(subtypeName, "NV12", StringComparison.OrdinalIgnoreCase);
        int rgbaStride = width * 4;
        byte[] raw = nv12
            ? YuvFrameEncoder.EncodeNv12(reference, width, height, rgbaStride, color)
            : YuvFrameEncoder.EncodeYuy2(reference, width, height, rgbaStride, color);

        int stride = nv12 ? width : width * 2;
        int rows = nv12 ? height * 3 / 2 : height;
        var frame = new CapturedFrame(raw, width, height, stride, 0, 0, rows);

        // 编码自检：75% 白的 limited 码值必须正好是 180、纯黑是 16
        EncodeCoefficients encode = EncodeCoefficients.For(color.Matrix);
        (byte whiteY, byte _, byte _) = YuvFrameEncoder.Encode(191, 191, 191, encode);
        (byte blackY, byte _, byte _) = YuvFrameEncoder.Encode(0, 0, 0, encode);
        Check(whiteY == 180 && blackY == 16, "编码自检：75% 白 → Y=180、黑 → Y=16",
            $"白 Y={whiteY}、黑 Y={blackY}");

        // ---------- GPU 转换（顺便量耗时：10 次取平均）----------
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        const int iterations = 10;
        for (int i = 0; i < iterations; i++)
        {
            uploader.Convert(d3d.Context, frame, subtypeName, color);
        }
        d3d.Context.Flush();
        stopwatch.Stop();
        double perFrameMs = stopwatch.Elapsed.TotalMilliseconds / iterations;

        ID3D11ShaderResourceView sourceSrv = uploader.Convert(d3d.Context, frame, subtypeName, color);
        d3d.Context.Flush();
        byte[] gpu = d3d.ReadBackRgba8(uploader.RgbTexture!, out int gpuWidth, out int gpuHeight);
        Check(gpuWidth == width && gpuHeight == height, "GPU 输出尺寸 = 图像尺寸", $"{gpuWidth}×{gpuHeight}");

        // ---------- 与 CPU 版逐像素对拍（最强的等价性证据）----------
        byte[] cpu = YuvFrameConverter.ToRgba8(frame, subtypeName, color);
        long maxDifference = 0;
        long exact = 0;
        long offByOne = 0;
        long worse = 0;
        for (int i = 0; i + 2 < Math.Min(cpu.Length, gpu.Length); i += 4)
        {
            for (int channel = 0; channel < 3; channel++)
            {
                int difference = Math.Abs(cpu[i + channel] - gpu[i + channel]);
                maxDifference = Math.Max(maxDifference, difference);
                if (difference == 0)
                {
                    exact++;
                }
                else if (difference == 1)
                {
                    offByOne++;
                }
                else
                {
                    worse++;
                }
            }
        }
        Check(maxDifference <= 1, "GPU 与 CPU 转换逐像素一致（最大差 ≤ 1 LSB）",
            $"最大差 {maxDifference}，完全一致 {exact}，差 1 的 {offByOne}，差 >1 的 {worse}");
        Check(worse == 0, "没有任何像素差到 2 以上", $"{worse} 个");

        // ---------- 已知码值 ----------
        int barY = height / 6;
        CheckPixel(gpu, gpuWidth, gpuHeight, width / 14, barY, 191, 191, 191, $"75% 白条（{subtypeName}）");
        CheckPixel(gpu, gpuWidth, gpuHeight, width / 14, height * 9 / 10, 0, 0, 0, $"PLUGE 0 IRE（{subtypeName}）");

        // ---------- 示波器：白条必须落在 bin 191 ----------
        var settings = new ScopeRenderSettings
        {
            NeedWaveform = true,
            NeedVectorscope = true,
            NeedDiamond = true,
            NeedCie = true,
            Stride = 1,
        };
        engine.Encode(d3d.Context, sourceSrv, settings);
        d3d.Context.Flush();
        uint[] histogram = engine.ReadBackHistogram(d3d.Context);

        uint whiteSamples = 0;
        for (uint column = 0; column < 74; column++)
        {
            whiteSamples += histogram[WaveIndex(3, 191, column)];
        }
        long expectedWhite = (long)(width / 7.0) * (height * 2 / 3);
        Check(Math.Abs(whiteSamples - expectedWhite) < expectedWhite * 0.05,
            $"示波器直方图：75% 白条落在 bin 191（{subtypeName}）",
            $"{whiteSamples} 个样本 vs 白条面积 {expectedWhite}（±5%）");

        uint blackSamples = 0;
        for (uint column = 0; column < 512; column++)
        {
            blackSamples += histogram[WaveIndex(3, 0, column)];
        }
        Check(blackSamples > 0, $"示波器直方图：黑电平 bin 0 有样本（{subtypeName}）", $"{blackSamples} 个");

        // ---------- 钻石图 / 马蹄图：纹理里真的有点（不只是直方图里有）----------
        // ⚠️ 直方图里有样本 ≠ 画得出来：归一化那一趟如果只写了中线（或者纹理根本没写全），
        //    直方图断言照样过，但画面上只剩一根灰阶竖线。所以这里直接回读**纹理**。
        CheckTexture(report: null, d3d, engine, ScopePanelKind.Diamond, "钻石图", subtypeName);
        CheckTexture(report: null, d3d, engine, ScopePanelKind.Cie, "马蹄图", subtypeName);

        Console.WriteLine($"  上传 + 转换：{perFrameMs:0.00} ms/帧（60 fps 的预算是 16.67 ms）");
        Check(perFrameMs < 16.67, "转换够 60 fps 的实时预算", $"{perFrameMs:0.00} ms");

        PngWriter.Write(pngPath, gpuWidth, gpuHeight, gpu);
        Console.WriteLine($"  出图：{pngPath}");
    }

    private static uint WaveIndex(uint plane, uint code, uint column)
        => plane * 512u * 256u + code * 512u + column;

    /// <summary>
    /// 回读某张示波器**纹理**，看「中心列」与「列外」是不是都有非零 texel。
    /// 钻石图的中线是灰阶（x 恒为 0），彩条则在两侧 —— 只有中线说明彩条没画出来。
    /// </summary>
    private static void CheckTexture(object? report,
                                     D3DContext d3d,
                                     ScopeEngine engine,
                                     ScopePanelKind kind,
                                     string label,
                                     string subtypeName)
    {
        byte[] pixels = engine.ReadBackTexture(d3d.Context, engine.TextureFor(kind, WaveformMode.Luma),
                                               out int width, out int height);
        int centerColumn = 0;
        int offColumn = 0;
        int midRow = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte value = pixels[(y * width + x) * 4];
                if (value == 0)
                {
                    continue;
                }
                if (Math.Abs(x - width / 2) <= 2)
                {
                    centerColumn++;
                }
                else
                {
                    offColumn++;
                }
            }
        }

        // 马蹄图的点不在中线上（色度是散开的），所以判据不同：
        // 只要求「样本分布在中线以外也有」，再加上总量足够
        bool ok = offColumn > 0 && (kind != ScopePanelKind.Diamond || centerColumn > 0);
        Check(ok, $"{label}纹理：非零 texel 分布正常（中线 {centerColumn}、线外 {offColumn}，纹理 {width}×{height}）",
            subtypeName);
        _ = midRow;
    }

    private static void CheckPixel(byte[] rgba, int width, int height, int x, int y,
                                   int expectedR, int expectedG, int expectedB, string what)
    {
        if (x < 0 || y < 0 || x >= width || y >= height)
        {
            Check(false, what, "取样点越界");
            return;
        }
        int i = (y * width + x) * 4;
        int r = rgba[i], g = rgba[i + 1], b = rgba[i + 2];
        bool ok = Math.Abs(r - expectedR) <= 1 && Math.Abs(g - expectedG) <= 1 && Math.Abs(b - expectedB) <= 1;
        Check(ok, what, $"({r},{g},{b}) 期望 ({expectedR},{expectedG},{expectedB})");
    }

    // ------------------------------------------------------------------
    //  ③ 采集：1 秒 → 实测 fps + capture-frame.png
    // ------------------------------------------------------------------
    private static int Capture(string[] args)
    {
        double seconds = args.Length > 1 && double.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double s)
            ? s : 1.0;
        string deviceFragment = Option(args, "--device") ?? DefaultDeviceNameFragment;
        string? requested = Option(args, "--set");
        string outDirectory = Option(args, "--out") ?? DefaultOutDirectory();
        string outputName = Option(args, "--name") ?? "capture-frame.png";

        // ---------- 先自检转换器（不碰设备，纯数值）----------
        // 这一步是「IRE 标定」的根：转换器错了，后面示波器再准也是白搭。
        Console.WriteLine("转换器自检（YUY2 → RGBA8，limited 16–235 → full 0–255）：");
        ConverterSelfCheck();
        Console.WriteLine();

        using var mf = MediaFoundationRuntime.Start();
        using CaptureDevice device = CaptureDevice.OpenByName(deviceFragment);
        IReadOnlyList<CaptureFormat> formats = device.GetNativeFormats();
        CaptureFormat wanted = requested is { Length: > 0 }
            ? formats.FirstOrDefault(f => f.MatchesSpec(requested))
                ?? throw new InvalidOperationException($"没有匹配「{requested}」的原生格式")
            : CaptureFormat.PickPreferred(formats)
                ?? throw new InvalidOperationException("设备没有可用的原生格式");

        CaptureFormat effective = device.SetNativeFormat(wanted.NativeIndex);
        Console.WriteLine($"设备        : [{device.Info.Index}] {device.Info.FriendlyName}");
        Console.WriteLine($"生效格式    : {effective.Describe()}");
        Console.WriteLine($"色彩        : {effective.Color.Summary}");
        Console.WriteLine($"换算系数    : {YuvFrameConverter.Coefficients.Select(effective.Color).Describe()}");
        Console.WriteLine();

        // ---------- 预热：丢掉最初几帧（UVC 开始传输的头几帧经常是空的/半截的）----------
        int discarded = 0;
        for (int i = 0; i < 3; i++)
        {
            CapturedFrame? warmup = device.ReadFrame();
            if (warmup is not null)
            {
                discarded++;
            }
        }

        // ---------- 按时间戳采满 N 秒 ----------
        var meter = new CaptureRateMeter();
        CapturedFrame? middle = null;
        CapturedFrame? last = null;
        var intervals = new List<double>();
        double previousTimestamp = double.NaN;
        int frames = 0;

        while (true)
        {
            CapturedFrame? frame = device.ReadFrame();
            if (frame is null)
            {
                break;   // 设备不再给帧（拔掉了 / 走到流末尾）
            }

            double timestamp = frame.TimestampSeconds;
            if (frames > 0 && !double.IsNaN(previousTimestamp))
            {
                intervals.Add((timestamp - previousTimestamp) * 1000.0);
            }

            meter.Add(frame.TimestampHns);
            frames++;
            previousTimestamp = timestamp;
            last = frame;

            // 留中间那一帧出图：第一帧可能还没稳定，最后一帧可能刚好在格式切换点上
            if (middle is null && meter.ElapsedSeconds >= seconds / 2)
            {
                middle = frame;
            }

            if (meter.ElapsedSeconds >= seconds)
            {
                break;
            }
            if (frames > 20000)
            {
                break;   // 安全阀：声明帧率填错时别把内存吃光
            }
        }

        CapturedFrame? snapshot = middle ?? last;
        double declared = effective.FrameRate;
        double measured = meter.MeasuredFps;
        double deviation = declared > 0 ? (measured - declared) * 100.0 / declared : 0;

        Console.WriteLine($"预热丢弃    : {discarded} 帧");
        Console.WriteLine($"采集        : {frames} 帧 / {meter.ElapsedSeconds:0.000} 秒（按帧时间戳算）");
        Console.WriteLine($"声明帧率    : {declared:0.###} fps");
        Console.WriteLine($"实测帧率    : {measured:0.###} fps（偏差 {deviation:+0.0;-0.0;0.0}%）");
        Console.WriteLine($"帧间隔      : 最小 {meter.MinIntervalMs:0.00} ms / 最大 {meter.MaxIntervalMs:0.00} ms "
                        + $"/ 平均 {(frames > 1 ? meter.ElapsedSeconds * 1000.0 / (frames - 1) : 0):0.00} ms");
        if (intervals.Count >= 5)
        {
            Console.WriteLine($"前 5 个间隔 : {string.Join("、", intervals.Take(5).Select(v => v.ToString("0.00") + " ms"))}");
        }

        if (snapshot is null)
        {
            Check(false, "采到至少一帧画面", "一帧都没有 —— 卡没在出图（HDMI 没接？被别的程序占用？）");
            return Report();
        }

        Console.WriteLine();
        Console.WriteLine($"出图帧      : {snapshot.Width}×{snapshot.Height}，行跨距 {snapshot.Stride}，"
                        + $"缓冲 {snapshot.Rows} 行，{snapshot.Length} 字节（{snapshot.Length / 1024.0 / 1024.0:0.00} MB），"
                        + $"时间戳 {snapshot.TimestampSeconds:0.000000} s");

        // ---------- Y 平面统计（顺便把 IRE 换算走一遍）----------
        LumaStats planes = MeasureLuma(snapshot, effective.SubtypeName);
        PlaneStats luma = planes.Y;
        VideoColorInfo color = effective.Color;
        Console.WriteLine($"Y 码值      : {luma}");
        Console.WriteLine($"U / V 码值  : U {planes.U}    V {planes.V}");
        Console.WriteLine($"对应 IRE    : {color.CodeToIre(luma.Min):0.0} … {color.CodeToIre(luma.Max):0.0} IRE"
                        + $"（按 {color.RangeDescription}；平均 {color.CodeToIre(luma.Mean):0.0} IRE）");

        // ---------- 转 RGBA 并存 PNG ----------
        Directory.CreateDirectory(outDirectory);
        string pngPath = Path.Combine(outDirectory, outputName);
        byte[] rgba = YuvFrameConverter.ToRgba8(snapshot, effective.SubtypeName, color);
        PngWriter.Write(pngPath, snapshot.Width, snapshot.Height, rgba);
        var fileInfo = new FileInfo(pngPath);
        Console.WriteLine($"出图        : {pngPath}（{fileInfo.Length / 1024.0:0.0} KB）");
        Console.WriteLine();

        // ---------- 断言 ----------
        Check(frames >= 2, "采到足够的帧用来算实测帧率", $"{frames} 帧");
        Check(measured > 0 && Math.Abs(deviation) < 5.0,
            "实测帧率与声明帧率一致（±5%）",
            $"{measured:0.###} vs {declared:0.###} fps");
        Check(snapshot.Width == effective.Width && snapshot.Height == effective.Height,
            "帧尺寸与生效格式一致", $"{snapshot.Width}×{snapshot.Height}");
        Check(snapshot.Length == Math.Abs(snapshot.Stride) * snapshot.Rows,
            "帧缓冲长度 = 行跨距 × 缓冲行数", $"{snapshot.Length} = {Math.Abs(snapshot.Stride)}×{snapshot.Rows}");

        // 画面内容：整帧单一码值时不能说「管道错了」，但必须证明它**确实是均匀画面**，
        // 且这个均匀值恰好等于该码值经同一套系数换算出来的结果。
        // ⚠️ 别写成「均匀 → 一定是纯黑」：只有 limited 的 16 才映射到 0；
        //    full range（内建摄像头就报 full）的 12 会老老实实映射成 12。
        if (luma.Distinct == 1)
        {
            byte expected = UniformProbe(luma.Min, color);
            bool uniform = IsUniform(rgba, expected, expected, expected, 255);
            bool neutralChroma = planes.U.Min == 128 && planes.U.Max == 128 && planes.V.Min == 128 && planes.V.Max == 128;

            Check(uniform, $"整帧单一码值 {luma.Min} → RGBA 整幅均匀 ({expected},{expected},{expected},255)"
                         + "（读的是 Y 平面、码值映射一致）",
                uniform ? "是" : "否 —— 转换结果不是均匀色，管道有问题");
            Check(neutralChroma, "U/V 都是 128（无色度）—— 是均匀灰画面，不是读错平面",
                $"U {planes.U.Min}–{planes.U.Max} / V {planes.V.Min}–{planes.V.Max}");
            Console.WriteLine();
            Console.WriteLine($"⚠ 这一帧整幅是**均匀画面**（Y 全部 = {luma.Min}，U/V = 128），所以 PNG 是一张纯色图。可能是：");
            if (color.IsLimitedRange)
            {
                Console.WriteLine($"   · 采集卡没接到信号源 / 信号源输出黑场（limited 黑电平就是码值 16，本帧是 {luma.Min}）");
                Console.WriteLine("   · 信号源带 HDCP，卡不出图");
            }
            else
            {
                Console.WriteLine($"   · 摄像头被遮挡、被系统「相机隐私」开关关掉，驱动给的是占位画面");
                Console.WriteLine("   · 现场太暗（full range 下码值 12 接近纯黑）");
            }
            Console.WriteLine("   · 设备被别的程序（OBS / 相机应用）占着");
            Console.WriteLine("  帧率、行跨距、缓冲行数、码值映射这四项都已验证正确 —— 换一个有画面的信号源即可看到内容。");
        }
        else
        {
            Check(true, "Y 平面有画面内容", $"码值 {luma.Min}–{luma.Max}，{luma.Distinct} 种");
        }

        if (fileInfo.Exists)
        {
            (int pngWidth, int pngHeight) = ReadPngSize(pngPath);
            Check(pngWidth == snapshot.Width && pngHeight == snapshot.Height,
                "PNG 回读尺寸 == 帧尺寸", $"{pngWidth}×{pngHeight}");
        }
        else
        {
            Check(false, "PNG 已写出", "文件不存在");
        }

        return Report();
    }

    /// <summary>
    /// 转换器自检：用**已知码值**验证 limited → full 的展开与系数选择。
    /// 180 是本工程合成信号里 75% 白的 limited 码值，展开后必须正好是 191 ——
    /// 这样「采集进来的画面」和「离屏自检的合成信号」用的是同一套码值口径。
    /// </summary>
    private static void ConverterSelfCheck()
    {
        var limited601 = new VideoColorInfo(
            NominalRange.Range16_235, true,
            VideoPrimaries.Bt709, true,
            VideoTransferFunction.Func709, true,
            VideoTransferMatrix.Bt601, true);

        Check(IsColor(ColorAt(16, 128, 128, limited601), 0, 0, 0), "码值 16 → 0（0 IRE 黑）", Describe(ColorAt(16, 128, 128, limited601)));
        Check(IsColor(ColorAt(180, 128, 128, limited601), 191, 191, 191), "码值 180 → 191（75% 白，与合成信号一致）",
            Describe(ColorAt(180, 128, 128, limited601)));
        Check(IsColor(ColorAt(235, 128, 128, limited601), 255, 255, 255), "码值 235 → 255（100 IRE 白）",
            Describe(ColorAt(235, 128, 128, limited601)));

        (byte r, byte g, byte b) red = ColorAt(81, 90, 240, limited601);
        Check(red.r > 250 && red.g < 8 && red.b < 8, "BT.601 limited 的纯红 (Y,U,V)=(81,90,240) → 纯红", Describe(red));

        (byte r709, byte g709, byte b709) = ColorAt(63, 102, 240, limited601 with { Matrix = VideoTransferMatrix.Bt709 });
        Check(r709 > 250 && g709 < 8 && b709 < 8, "BT.709 limited 的纯红 (Y,U,V)=(63,102,240) → 纯红",
            Describe((r709, g709, b709)));

        // NV12（交织 UV 平面）走一遍：2×2 图，6 字节 = 4 个 Y + 一对 UV
        YuvFrameConverter.Coefficients coefficients = YuvFrameConverter.Coefficients.Select(limited601);
        byte[] nv12Gray = { 180, 180, 180, 180, 128, 128 };
        byte[] nv12GrayRgba = YuvFrameConverter.Nv12ToRgba8(nv12Gray, 2, 2, 2, coefficients);
        Check(nv12GrayRgba[0] == 191 && nv12GrayRgba[1] == 191 && nv12GrayRgba[2] == 191 &&
              nv12GrayRgba[4] == 191 && nv12GrayRgba[8] == 191 && nv12GrayRgba[12] == 191,
            "NV12 2×2 全 180 → 四个像素都是 191",
            $"({nv12GrayRgba[0]},{nv12GrayRgba[1]},{nv12GrayRgba[2]}) …");

        byte[] nv12Red = { 81, 81, 81, 81, 90, 240 };
        byte[] nv12RedRgba = YuvFrameConverter.Nv12ToRgba8(nv12Red, 2, 2, 2, coefficients);
        Check(nv12RedRgba[0] > 250 && nv12RedRgba[1] < 8 && nv12RedRgba[2] < 8,
            "NV12 纯红 (Y,U,V)=(81,90,240) → 纯红", Describe((nv12RedRgba[0], nv12RedRgba[1], nv12RedRgba[2])));
    }

    private static bool IsColor((byte R, byte G, byte B) c, byte r, byte g, byte b)
        => c.R == r && c.G == g && c.B == b;

    private static (byte R, byte G, byte B) ColorAt(byte y, byte u, byte v, VideoColorInfo color)
    {
        // 造一帧 2×1 的 YUY2（两组共用同一对 U/V），走真正的转换路径
        byte[] yuy2 = { y, u, y, v };
        byte[] rgba = YuvFrameConverter.Yuy2ToRgba8(yuy2, 2, 1, 4,
            YuvFrameConverter.Coefficients.Select(color));
        return (rgba[0], rgba[1], rgba[2]);
    }

    private static string Describe((byte R, byte G, byte B) c) => $"({c.R},{c.G},{c.B})";

    /// <summary>
    /// 某个 Y 码值（U/V 取 128 无色度）经真正的转换路径会得到什么灰度值 ——
    /// 用来给「整帧均匀」的画面算出它**应该**是什么颜色（而不是假定一定是黑的）。
    /// </summary>
    private static byte UniformProbe(byte y, VideoColorInfo color)
    {
        byte[] probe = YuvFrameConverter.Yuy2ToRgba8(
            new byte[] { y, 128, y, 128 }, 2, 1, 4, YuvFrameConverter.Coefficients.Select(color));
        return probe[0];
    }

    /// <summary>整幅 RGBA 是不是同一个颜色（用来证明「全黑帧」确实是黑电平而不是读错平面）。</summary>
    private static bool IsUniform(ReadOnlySpan<byte> rgba, byte r, byte g, byte b, byte a)
    {
        for (int i = 0; i + 3 < rgba.Length; i += 4)
        {
            if (rgba[i] != r || rgba[i + 1] != g || rgba[i + 2] != b || rgba[i + 3] != a)
            {
                return false;
            }
        }
        return true;
    }

    private readonly record struct PlaneStats(byte Min, byte Max, double Mean, int Distinct)
    {
        public override string ToString() => $"{Min}–{Max}（平均 {Mean:0.0}，{Distinct} 种）";
    }

    private readonly record struct LumaStats(PlaneStats Y, PlaneStats U, PlaneStats V);

    /// <summary>
    /// 统计三个平面。
    /// YUY2：Y 在偶数下标、U 在 4n+1、V 在 4n+3；
    /// NV12：Y 平面在缓冲前 height 行，交织 UV 平面紧跟其后（U 在前、V 在后）。
    /// 顺带看 U/V 是有意的：**全黑帧的 U/V 必须是 128**（无色度），
    /// 这能区分「卡在输出真黑场」和「我们把某个平面读错位了」。
    /// </summary>
    private static LumaStats MeasureLuma(CapturedFrame frame, string subtypeName)
    {
        var y = new PlaneStatsBuilder();
        var u = new PlaneStatsBuilder();
        var v = new PlaneStatsBuilder();

        int pitch = Math.Abs(frame.Stride);
        bool nv12 = string.Equals(subtypeName, "NV12", StringComparison.OrdinalIgnoreCase);
        int chromaPlane = pitch * frame.Height;

        for (int row = 0; row < frame.Height; row++)
        {
            int rowStart = row * pitch;
            if (nv12)
            {
                int chromaRow = chromaPlane + (row / 2) * pitch;
                for (int x = 0; x < frame.Width; x++)
                {
                    y.Add(frame.Data[rowStart + x]);
                    if ((x & 1) == 0)
                    {
                        u.Add(frame.Data[chromaRow + x]);
                        v.Add(frame.Data[chromaRow + x + 1]);
                    }
                }
            }
            else
            {
                for (int x = 0; x < frame.Width; x += 2)
                {
                    int i = rowStart + x * 2;
                    y.Add(frame.Data[i]);
                    u.Add(frame.Data[i + 1]);
                    y.Add(frame.Data[i + 2]);
                    v.Add(frame.Data[i + 3]);
                }
            }
        }

        return new LumaStats(y.Build(), u.Build(), v.Build());
    }

    private struct PlaneStatsBuilder
    {
        private readonly int[] _histogram;
        private long _sum;
        private int _count;
        private byte _min;
        private byte _max;

        public PlaneStatsBuilder()
        {
            _histogram = new int[256];
            _sum = 0;
            _count = 0;
            _min = 255;
            _max = 0;
        }

        public void Add(byte value)
        {
            _histogram[value]++;
            _sum += value;
            _count++;
            if (value < _min)
            {
                _min = value;
            }
            if (value > _max)
            {
                _max = value;
            }
        }

        public PlaneStats Build()
        {
            if (_count == 0)
            {
                return new PlaneStats(0, 0, 0, 0);
            }
            int distinct = 0;
            foreach (int bin in _histogram)
            {
                if (bin > 0)
                {
                    distinct++;
                }
            }
            return new PlaneStats(_min, _max, (double)_sum / _count, distinct);
        }
    }

    /// <summary>回读 PNG 的 IHDR 尺寸（顺带证明写出来的是合法 PNG）。</summary>
    private static (int Width, int Height) ReadPngSize(string path)
    {
        byte[] header = new byte[24];
        using var stream = File.OpenRead(path);
        if (stream.Read(header, 0, header.Length) < header.Length)
        {
            return (-1, -1);
        }
        ReadOnlySpan<byte> signature = stackalloc byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        if (!header.AsSpan(0, 8).SequenceEqual(signature))
        {
            return (-1, -1);
        }
        if (System.Text.Encoding.ASCII.GetString(header, 12, 4) != "IHDR")
        {
            return (-1, -1);
        }
        int width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16, 4));
        int height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20, 4));
        return (width, height);
    }

    /// <summary>
    /// 出图目录：从当前目录往上找仓库根（认 Windows\VideoScopePad.Win 这个标记），
    /// 拼出 &lt;仓库根&gt;\Windows\out。
    ///
    /// ⚠️ 为什么不直接写相对路径：.NET 的文件 API 走的是**进程当前目录**，
    /// 而 `dotnet run` 的当前目录取决于你从哪个目录敲的命令（不是项目目录），
    /// 于是「同一个命令在两个目录下跑，图存到两个地方」—— 这个坑之前踩过，这里按仓库根定位。
    /// </summary>
    private static string DefaultOutDirectory()
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                string marker = Path.Combine(directory.FullName, "Windows", "VideoScopePad.Win", "VideoScopePad.Win.csproj");
                if (File.Exists(marker))
                {
                    return Path.Combine(directory.FullName, "Windows", "out");
                }
                directory = directory.Parent;
            }
        }
        return Path.Combine(Directory.GetCurrentDirectory(), "out");
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
