//
//  CaptureFormat.cs
//  VideoScopePad.Win
//
//  一个「原生媒体类型」（IMFMediaType）解析出来的纯数据格式描述，对应 iPad 版的
//  AVCaptureDevice.Format。采集卡会给出一张格式清单（分辨率 × 帧率 × 像素格式），
//  界面里的「输入格式」下拉就是这份清单，默认自动挑一个（见 PickPreferred）。
//
//  ⚠️ MF 的尺寸/帧率都是「两个 UInt32 打包进一个 UInt64」的属性：
//       MF_MT_FRAME_SIZE  → high = 宽, low = 高
//       MF_MT_FRAME_RATE  → high = 分子, low = 分母
//     （mfapi.h 的 MFSetAttributeSize/MFSetAttributeRatio 就是这么编码的）
//     读的时候绝对不要用 GetUInt64 直接当数值用 —— 那是两个字段拼起来的。
//

using Vortice.MediaFoundation;

namespace VideoScopePad.Win.Capture;

/// <summary>采集设备的一个原生格式。</summary>
public sealed record CaptureFormat
{
    /// <summary>在设备的原生类型表里的序号（SetNativeFormat 用的就是它）。</summary>
    public int NativeIndex { get; init; } = -1;

    public Guid Subtype { get; init; } = Guid.Empty;

    /// <summary>像素格式名（NV12 / YUY2 / MJPG …）。</summary>
    public string SubtypeName { get; init; } = "?";

    public uint Width { get; init; }

    public uint Height { get; init; }

    public uint FrameRateNumerator { get; init; }

    public uint FrameRateDenominator { get; init; } = 1;

    public double FrameRate => FrameRateDenominator == 0 ? 0.0 : FrameRateNumerator / (double)FrameRateDenominator;

    public VideoInterlaceMode InterlaceMode { get; init; } = VideoInterlaceMode.Unknown;

    public bool IsCompressed { get; init; }

    /// <summary>色彩元数据（量化范围 / 原色 / 传输函数 / 矩阵）。</summary>
    public VideoColorInfo Color { get; init; } = VideoColorInfo.Unknown;

    /// <summary>MF_MT_DEFAULT_STRIDE，负数表示自下而上（DIB 习惯）。</summary>
    public int DefaultStride { get; init; }

    public bool HasDefaultStride { get; init; }

    /// <summary>像素宽高比（一般是 1:1）。</summary>
    public uint PixelAspectNumerator { get; init; } = 1;

    public uint PixelAspectDenominator { get; init; } = 1;

    public bool HasPixelAspect { get; init; }

    /// <summary>每帧字节数（MFCalculateImageSize 的估算，压缩格式不可靠）。</summary>
    public uint ImageSize { get; init; }

    /// <summary>命令行的紧凑写法：1920x1080@60:NV12</summary>
    public string Spec => $"{Width}x{Height}@{FrameRate:0.###}:{SubtypeName}";

    public string Describe()
        => $"{Width}×{Height} @ {FrameRate:0.###} fps  {SubtypeName}"
         + (IsCompressed ? "（压缩）" : string.Empty)
         + (InterlaceMode is VideoInterlaceMode.Progressive or VideoInterlaceMode.Unknown
                ? string.Empty
                : $"  隔行 {InterlaceMode}");

    /// <summary>从 IMFMediaType 解析。nativeIndex &lt; 0 表示「当前生效格式」。</summary>
    public static CaptureFormat FromMediaType(IMFMediaType type, int nativeIndex)
    {
        ArgumentNullException.ThrowIfNull(type);

        Guid subtype = type.TryGetGuid(MediaTypeAttributeKeys.Subtype) ?? Guid.Empty;

        uint width = 0;
        uint height = 0;
        if (!type.TryGetSize(MediaTypeAttributeKeys.FrameSize, out width, out height))
        {
            width = 0;
            height = 0;
        }

        uint frameNum = 0;
        uint frameDen = 1;
        if (type.TryGetRatio(MediaTypeAttributeKeys.FrameRate, out uint fn, out uint fd))
        {
            frameNum = fn;
            frameDen = fd;
        }
        else if (type.TryGetPackedPair(MediaTypeAttributeKeys.FrameRateRangeMax, out uint mn, out uint md))
        {
            // 有些驱动不给固定帧率，只给范围 —— 取上界当声明帧率
            frameNum = mn;
            frameDen = md;
        }

        var isCompressed = type.IsCompressedFormat || MediaSubtype.IsCompressed(subtype);

        // 色彩元数据：分别判断「驱动给了没有」，缺的按 Unknown 留下，绝不猜成 0（0 = Unknown）
        bool hasRange = type.TryGetUInt32(MediaTypeAttributeKeys.VideoNominalRange, out uint range);
        bool hasPrimaries = type.TryGetUInt32(MediaTypeAttributeKeys.VideoPrimaries, out uint primaries);
        bool hasTransfer = type.TryGetUInt32(MediaTypeAttributeKeys.TransferFunction, out uint transfer);
        bool hasMatrix = type.TryGetUInt32(MediaTypeAttributeKeys.YuvMatrix, out uint matrix);

        var color = new VideoColorInfo(
            NominalRange: hasRange ? (NominalRange)range : NominalRange.Unknown,
            RangeFromDriver: hasRange && range != (uint)NominalRange.Unknown,
            Primaries: hasPrimaries ? (VideoPrimaries)primaries : VideoPrimaries.Unknown,
            PrimariesFromDriver: hasPrimaries && primaries != (uint)VideoPrimaries.Unknown,
            TransferFunction: hasTransfer ? (VideoTransferFunction)transfer : VideoTransferFunction.FuncUnknown,
            TransferFunctionFromDriver: hasTransfer && transfer != (uint)VideoTransferFunction.FuncUnknown,
            Matrix: hasMatrix ? (VideoTransferMatrix)matrix : VideoTransferMatrix.Unknown,
            MatrixFromDriver: hasMatrix && matrix != (uint)VideoTransferMatrix.Unknown);

        bool hasStride = type.TryGetInt32(MediaTypeAttributeKeys.DefaultStride, out int stride);

        uint parNum = 1;
        uint parDen = 1;
        bool hasPar = type.TryGetPackedPair(MediaTypeAttributeKeys.PixelAspectRatio, out uint pn, out uint pd);
        if (hasPar)
        {
            parNum = pn == 0 ? 1 : pn;
            parDen = pd == 0 ? 1 : pd;
        }

        uint imageSize = 0;
        try
        {
            imageSize = MediaFactory.MFCalculateImageSize(subtype, width, height);
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
            imageSize = 0;
        }

        return new CaptureFormat
        {
            NativeIndex = nativeIndex,
            Subtype = subtype,
            SubtypeName = MediaSubtype.Name(subtype),
            Width = width,
            Height = height,
            FrameRateNumerator = frameNum,
            FrameRateDenominator = frameDen,
            InterlaceMode = type.TryGetUInt32(MediaTypeAttributeKeys.InterlaceMode, out uint interlace)
                ? (VideoInterlaceMode)interlace
                : VideoInterlaceMode.Unknown,
            IsCompressed = isCompressed,
            Color = color,
            DefaultStride = stride,
            HasDefaultStride = hasStride,
            PixelAspectNumerator = parNum,
            PixelAspectDenominator = parDen,
            HasPixelAspect = hasPar,
            ImageSize = imageSize,
        };
    }

    /// <summary>
    /// 「这条格式是不是命令行 / 界面点名要的那条」。
    /// 语法：<c>1920x1080@60:NV12</c>，后两段可省（@60 与 :NV12 都能缺，缺的部分不参与比较）。
    /// 分辨率必须给。大小写不敏感。
    /// </summary>
    public bool MatchesSpec(string spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
        {
            return true;
        }

        string[] parts = spec.Trim().Split(':', 2);
        string geometry = parts[0].Trim();
        string? wantedSubtype = parts.Length > 1 ? parts[1].Trim() : null;

        if (wantedSubtype is { Length: > 0 } &&
            !string.Equals(SubtypeName, wantedSubtype, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string[] rate = geometry.Split('@', 2);
        string[] size = rate[0].Split('x', 'X');
        if (size.Length != 2 ||
            !uint.TryParse(size[0], out uint width) ||
            !uint.TryParse(size[1], out uint height))
        {
            return false;
        }
        if (width != Width || height != Height)
        {
            return false;
        }

        if (rate.Length == 2 && rate[1].Trim().Length > 0 &&
            double.TryParse(rate[1].Trim(), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double fps))
        {
            // 帧率允许 0.5% 的浮点误差（59.94 与 60 是两条不同格式，不互相匹配）
            if (Math.Abs(FrameRate - fps) > fps * 0.005)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 自动挑一条：优先未压缩、分辨率与帧率最大、其次优先 NV12 &gt; YUY2。
    /// iPad 版有同样的「默认自动选择输入格式」逻辑，这里的取舍保持一致：
    /// 示波器要的是**未压缩的原始码流**，能拿到 NV12/YUY2 就绝不选 MJPG。
    /// </summary>
    public static CaptureFormat? PickPreferred(IEnumerable<CaptureFormat> formats)
    {
        static int SubtypeRank(CaptureFormat f) => f.SubtypeName switch
        {
            "NV12" => 0,
            "YUY2" => 1,
            "UYVY" => 2,
            _ => 3,
        };

        return formats
            .Where(f => f.Width > 0 && f.Height > 0 && f.FrameRate > 0)
            .OrderBy(f => f.IsCompressed ? 1 : 0)
            .ThenByDescending(f => (long)f.Width * f.Height)
            .ThenByDescending(f => f.FrameRate)
            .ThenBy(SubtypeRank)
            .FirstOrDefault();
    }
}
