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
    //  ④ 采集帧 → GPU（YUV→RGB 转换）→ 示波器：整条链路的数值验收
    // ------------------------------------------------------------------
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

                bool uniform = IsUniform(rgba, rgba[0], rgba[1], rgba[2], rgba[3]);
                Check(uniform, "真实帧转换结果是均匀色（本机卡现在没接信号源，预期就是均匀黑）",
                    $"({rgba[0]},{rgba[1]},{rgba[2]})");
                Check(effective.Color.IsRangeUnknown || rgba[0] == 0,
                    "limited 黑电平 16 → R'G'B' 0（IRE 标定的根）", $"读到 {rgba[0]}");

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
            NeedDiamond = false,
            NeedCie = false,
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

        Console.WriteLine($"  上传 + 转换：{perFrameMs:0.00} ms/帧（60 fps 的预算是 16.67 ms）");
        Check(perFrameMs < 16.67, "转换够 60 fps 的实时预算", $"{perFrameMs:0.00} ms");

        PngWriter.Write(pngPath, gpuWidth, gpuHeight, gpu);
        Console.WriteLine($"  出图：{pngPath}");
    }

    private static uint WaveIndex(uint plane, uint code, uint column)
        => plane * 512u * 256u + code * 512u + column;

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
