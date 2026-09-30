//
//  VideoColorInfo.cs
//  VideoScopePad.Win
//
//  视频**色彩元数据**：量化范围（limited / full）+ 原色 + 传输函数 + YCbCr 矩阵。
//
//  这是 Windows 版比 iPad 版更值钱的地方（README 开头那张表里的第一条）：
//  AVFoundation 那边基本拿不到「这是 16–235 还是 0–255」，只能靠格式名猜；
//  MF 会把 MF_MT_VIDEO_NOMINAL_RANGE / MF_MT_VIDEO_PRIMARIES /
//  MF_MT_TRANSFER_FUNCTION / MF_MT_YUV_MATRIX 直接给我们。
//
//  ⚠️⚠️ 两个必须记住的坑（写在这里，以后不用再查 mfidl.h）：
//
//  1) MFNominalRange 的枚举名是**反的**：
//       MFNominalRange_Normal  = 1 = 0–255   （full range / 电脑范围）
//       MFNominalRange_Wide    = 2 = 16–235  （limited / 视频范围，广播标准）
//     所以 C# 里 NominalRange.Normal 与 Range0_255 是同一个值（别名），
//     switch 里同时写这两个 case 会编译报错 —— 判断一律用等值比较。
//
//  2) 属性**缺失**与「值为 Unknown(0)」必须分开处理：
//     缺属性是常态（尤其廉价 UVC 卡），这时只能按下面的规则推断，
//     而且要把「这是推断值」明确标出来 —— 否则 IRE 标定会悄悄偏掉
//     （16–235 的 235 当 0–255 用，100 IRE 会算成 92 IRE，正好差 8%）。
//

using Vortice.MediaFoundation;

namespace VideoScopePad.Win.Capture;

/// <summary>
/// 一个视频格式的量化范围与色彩三件套。所有字段都带「是否由驱动显式给出」的标记。
/// </summary>
public sealed record VideoColorInfo(
    NominalRange NominalRange,
    bool RangeFromDriver,
    VideoPrimaries Primaries,
    bool PrimariesFromDriver,
    VideoTransferFunction TransferFunction,
    bool TransferFunctionFromDriver,
    VideoTransferMatrix Matrix,
    bool MatrixFromDriver)
{
    /// <summary>驱动什么色彩元数据都没给（只有几何与帧率可用）。</summary>
    public static VideoColorInfo Unknown { get; } = new(
        NominalRange.Unknown, false,
        VideoPrimaries.Unknown, false,
        VideoTransferFunction.FuncUnknown, false,
        VideoTransferMatrix.Unknown, false);

    /// <summary>0–255（full range）。</summary>
    public bool IsFullRange => NominalRange == NominalRange.Range0_255;

    /// <summary>16–235（limited / 视频范围）。</summary>
    public bool IsLimitedRange => NominalRange == NominalRange.Range16_235;

    /// <summary>范围未知（驱动没说）。</summary>
    public bool IsRangeUnknown => !IsFullRange && !IsLimitedRange;

    /// <summary>黑电平（8 位码值）：full = 0，limited = 16。</summary>
    public double BlackCode => IsFullRange ? 0.0 : IsLimitedRange ? 16.0 : double.NaN;

    /// <summary>白电平（8 位码值）：full = 255，limited = 235。</summary>
    public double WhiteCode => IsFullRange ? 255.0 : IsLimitedRange ? 235.0 : double.NaN;

    /// <summary>
    /// 8 位码值 → IRE（0 IRE = 黑电平，100 IRE = 白电平）。
    /// 这就是「IRE 标定准不准」的那条公式，示波器与读数都走它。
    /// </summary>
    public double CodeToIre(double code)
    {
        double black = BlackCode;
        double white = WhiteCode;
        if (double.IsNaN(black) || double.IsNaN(white) || white <= black)
        {
            return double.NaN;
        }
        return (code - black) * 100.0 / (white - black);
    }

    /// <summary>
    /// 归一化 0…1（0 IRE … 100 IRE）对应的码值 —— 着色器与合成信号都用 0…1，
    /// 这里给出「色度/幅度」的换算，采集进来的 YUV 先除以它再上 GPU。
    /// </summary>
    public double CodeToNormalized(double code)
    {
        double ire = CodeToIre(code);
        return double.IsNaN(ire) ? double.NaN : ire / 100.0;
    }

    /// <summary>量化范围的中文描述（顶栏那一行要让用户看懂）。</summary>
    public string RangeDescription => NominalRange switch
    {
        NominalRange.Range0_255 => "0–255（full range）",
        NominalRange.Range16_235 => "16–235（limited / 视频范围）",
        NominalRange.Range48_208 => "48–208（窄范围）",
        NominalRange.Range64_127 => "64–127（窄范围）",
        _ => "未声明",
    };

    /// <summary>驱动给的范围描述后面要不要加「推断」标记。</summary>
    public string RangeDescriptionWithSource
        => RangeFromDriver ? RangeDescription : RangeDescription + "（推断）";

    /// <summary>原色 / 传输函数 / 矩阵的中文描述，缺的标「未声明」。</summary>
    public string DescribePrimaries() => Primaries switch
    {
        VideoPrimaries.Bt709 => "BT.709",
        VideoPrimaries.Bt4702SysBG => "BT.601-625 (BT.470 BG)",
        VideoPrimaries.Bt4702SysM => "BT.601-525 (SMPTE 170M 系)",
        VideoPrimaries.Smpte170m => "SMPTE 170M",
        VideoPrimaries.Smpte240m => "SMPTE 240M",
        VideoPrimaries.Bt2020 => "BT.2020",
        VideoPrimaries.DciP3 => "DCI-P3",
        VideoPrimaries.DisplayP3 => "Display P3",
        _ => "未声明",
    };

    public string DescribeTransferFunction() => TransferFunction switch
    {
        VideoTransferFunction.Func709 => "BT.709",
        VideoTransferFunction.FuncSRGB => "sRGB",
        VideoTransferFunction.Func2020 => "BT.2020 (10 bit)",
        VideoTransferFunction.Func2020Const => "BT.2020 (12 bit)",
        VideoTransferFunction.Func2084 => "PQ (SMPTE 2084)",
        VideoTransferFunction.FuncHlg => "HLG",
        VideoTransferFunction.Func240m => "SMPTE 240M",
        _ => "未声明",
    };

    public string DescribeMatrix() => Matrix switch
    {
        VideoTransferMatrix.Bt709 => "BT.709",
        VideoTransferMatrix.Bt601 => "BT.601",
        VideoTransferMatrix.Smpte240m => "SMPTE 240M",
        VideoTransferMatrix.Bt202010 => "BT.2020 NCL",
        VideoTransferMatrix.Bt202012 => "BT.2020 CL",
        VideoTransferMatrix.ICtCp => "ICtCp",
        _ => "未声明",
    };

    /// <summary>一行摘要（日志 / 顶栏复用它）。</summary>
    public string Summary
        => $"{RangeDescriptionWithSource} · {DescribePrimaries()} · {DescribeTransferFunction()} · 矩阵 {DescribeMatrix()}";
}
