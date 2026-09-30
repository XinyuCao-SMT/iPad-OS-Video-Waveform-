//
//  ShaderTypes.cs
//  VideoScopePad.Win
//
//  HLSL 侧 `ShaderTypes.hlsli` 的 C# 镜像：常量值、结构体字节布局必须**严格一致**。
//
//  HLSL 的 cbuffer 打包规则：每个成员按 16 字节寄存器对齐，float3 也占 16 字节
//  （后 4 字节是空洞）。所以下面 float3 的地方都显式补一个 pad 字段，
//  这样 C# 结构体的字节偏移与 HLSL 完全相同 —— 偏移错了表现是「颜色/亮度全乱」，
//  而且编译器不会报错，只能靠这里的注释和自检兜住。
//

using System.Numerics;
using System.Runtime.InteropServices;

namespace VideoScopePad.Win.Render;

public static class ShaderConstants
{
    // 波形直方图：512 列 × 256 桶 × 4 个平面（Y / R / G / B）
    public const int WaveformColumns = 512;
    public const int WaveformBins = 256;
    public const int WaveformPlanes = 4;
    public const int WaveformCount = WaveformColumns * WaveformBins * WaveformPlanes;

    // 二维直方图（矢量 / 钻石 / 马蹄）
    public const int VectorscopeSize = 256;
    public const int VectorscopeCount = VectorscopeSize * VectorscopeSize;

    public const int GamutSectionVectorscope = 0;
    public const int GamutSectionDiamond = 1;
    public const int GamutSectionCie = 2;
    public const int GamutSectionCount = 3;
    public const int GamutCount = GamutSectionCount * VectorscopeCount;

    public const int HistogramUintCount = WaveformCount + GamutCount;
    public const int HistogramByteSize = HistogramUintCount * sizeof(uint);

    // 测量直方图：4 个通道（Y/R/G/B）× 256 桶 + 64 个径向桶
    public const int MeasurePlanes = 4;
    public const int MeasureBins = 256;
    public const int MeasureRadialBins = 64;
    public const int MeasureChannelCount = MeasurePlanes * MeasureBins;
    public const int MeasureUintCount = MeasureChannelCount + MeasureRadialBins;

    // 显示通道
    public const int DisplayModeColor = 0;
    public const int DisplayModeLuma = 1;
    public const int DisplayModeRed = 2;
    public const int DisplayModeGreen = 3;
    public const int DisplayModeBlue = 4;

    // 波形模式（注意：叠加模式是 2，不是 1 —— 与 iPad 版 / 着色器里的取值一致）
    public const int WaveformModeLuma = 0;
    public const int WaveformModeOverlay = 2;

    /// <summary>二维直方图位掩码（flags.y）</summary>
    public const int GamutMaskVectorscope = 1;
    public const int GamutMaskDiamond = 2;
    public const int GamutMaskCie = 4;
}

/// <summary>与 HLSL `VSScopeUniforms` 一致（64 字节）</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct ScopeUniforms
{
    /// <summary>x: 矢量图放大倍率 y: 轨迹亮度 z: 采样步长 w: 参考层不透明度（1 = 实时轨迹）</summary>
    public Vector4 Params;
    /// <summary>rgb: 轨迹颜色</summary>
    public Vector4 Color;
    /// <summary>x: 波形强度参考 y: 矢量图强度参考 z: 二维直方图分段</summary>
    public Vector4 Refs;
    /// <summary>x: 波形模式 y: 需要累计的二维直方图位掩码 z: 是否需要波形数据</summary>
    public Vector4 Flags;

    public static ScopeUniforms Default => new()
    {
        Params = new Vector4(1, 1, 1, 1),
        Color = new Vector4(1, 1, 1, 1),
        Refs = new Vector4(1, 1, 0, 0),
        Flags = Vector4.Zero,
    };
}

/// <summary>与 HLSL `VSQuadUniforms` 一致（48 字节）</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct QuadUniforms
{
    /// <summary>单位空间矩形：x, y, width, height</summary>
    public Vector4 Rect;
    /// <summary>纹理采样区：x, y, scaleX, scaleY</summary>
    public Vector4 Uv;
    /// <summary>x: 画面顺时针旋转角度（0 / 90 / 180 / 270）</summary>
    public Vector4 Misc;
}

/// <summary>与 HLSL `VSRenderUniforms` 一致（176 字节）</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct RenderUniforms
{
    // yuvToRGB：3×3，列主序（列 0 在 +0、列 1 在 +16、列 2 在 +32）
    public float M00, M01, M02, Pad0;
    public float M10, M11, M12, Pad1;
    public float M20, M21, M22, Pad2;

    // ycbcrScale（+48）：量化范围展开
    public float ScaleR, ScaleG, ScaleB, Pad3;
    // ycbcrBias（+64）
    public float BiasR, BiasG, BiasB, Pad4;
    // primaryWeights（+80）：亮度权重
    public float WeightR, WeightG, WeightB, Pad5;

    /// <summary>+96：x 曝光(档) y 对比度 z 饱和度 w 伽马</summary>
    public Vector4 Grade;
    /// <summary>+112：x 强度 y 3D 尺寸 z 1D 尺寸 w 是否启用</summary>
    public Vector4 LutParams;
    /// <summary>+128：x domainMin y domainMax</summary>
    public Vector4 LutDomain;
    /// <summary>+144：x 显示模式 y 源是否为双平面</summary>
    public Vector4 Flags;
    /// <summary>+160：x 高光阈值 y 高光斑马开关 z 黑切割阈值 w 黑斑马开关</summary>
    public Vector4 Zebra;

    /// <summary>默认：单位矩阵 + 不做范围展开 + 中性调色（全范围 BT.709 权重）</summary>
    public static RenderUniforms Default
    {
        get
        {
            var u = new RenderUniforms
            {
                M00 = 1, M01 = 0, M02 = 0,
                M10 = 0, M11 = 1, M12 = 0,
                M20 = 0, M21 = 0, M22 = 1,
                ScaleR = 1, ScaleG = 1, ScaleB = 1,
                BiasR = 0, BiasG = 0, BiasB = 0,
                WeightR = 0.2126f, WeightG = 0.7152f, WeightB = 0.0722f,
                Grade = new Vector4(0, 1, 1, 1),
                LutParams = new Vector4(0, 1, 1, 0),
                LutDomain = new Vector4(0, 1, 0, 0),
                Flags = Vector4.Zero,
                Zebra = new Vector4(1, 0, 0, 0),
            };
            return u;
        }
    }

    /// <summary>设置 YUV → R'G'B' 矩阵（列主序，三个列向量）</summary>
    public void SetYuvToRgb(float c0x, float c0y, float c0z,
                            float c1x, float c1y, float c1z,
                            float c2x, float c2y, float c2z)
    {
        M00 = c0x; M01 = c0y; M02 = c0z;
        M10 = c1x; M11 = c1y; M12 = c1z;
        M20 = c2x; M21 = c2y; M22 = c2z;
    }
}
