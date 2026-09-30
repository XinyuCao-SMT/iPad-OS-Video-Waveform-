//
//  ScopeKernels.metal
//  VideoScopePad
//
//  示波器统计：从已完成转换/调色的中间纹理中累计直方图。
//    - vsAccumulateHistogram : 每帧一次，原子累加到直方图缓冲区
//    - vsNormalizeWaveform   : 亮度波形（0-255 码值 -> 竖直 bin）
//    - vsNormalizeOverlay    : RGB 三通道叠加波形
//    - vsNormalizeParade     : RGB Parade（三列并排）
//    - vsNormalizeVectorscope: 矢量示波器（Cb/Cr 二维直方图）
//

#include <metal_stdlib>
#include "ShaderTypes.h"

using namespace metal;

static inline uint vsWaveIndex(uint plane, uint bin, uint column)
{
    return ((plane * VS_WAVEFORM_BINS) + bin) * VS_WAVEFORM_COLUMNS + column;
}

static inline uint vsVectorIndex(uint x, uint y, uint section)
{
    return uint(VS_WAVEFORM_COUNT)
        + section * uint(VS_VECTORSCOPE_COUNT)
        + y * uint(VS_VECTORSCOPE_SIZE) + x;
}

static inline float vsLuma(float3 rgb)
{
    return dot(rgb, float3(0.2126, 0.7152, 0.0722));
}

static inline float2 vsChroma(float3 rgb)
{
    // BT.709 的 Cb / Cr（以 0 为中心，理论范围 ±0.5）
    float cb = -0.114572 * rgb.r - 0.385428 * rgb.g + 0.5 * rgb.b;
    float cr = 0.5 * rgb.r - 0.454153 * rgb.g - 0.045847 * rgb.b;
    return float2(cb, cr);
}

/// 钻石图（**Tektronix 原版**，依据应用手册《Color Grading with the Spearhead Display》第 2 节）：
///   · 上菱形画 **G（左轴）与 B（右轴）**，下菱形画 **G（左轴）与 R（右轴）**；
///   · 纯黑在两菱形交会的**中心**；纯白分别落在上菱形顶端与下菱形底端；
///   · 灰阶（R=G=B）因此是正中的一条竖线，中灰落在两个菱形最宽处的中心。
/// 由此坐标唯一确定：
///   上菱形 x = B − G（−1…1）、y = (G + B)/2（0…1，向上）
///   下菱形 x = R − G（−1…1）、y = −(R + G)/2（−1…0，向下）
/// 分量都在 0–100% 之内时点必在菱形内；超出（超白 / 负值 / 非法 YCbCr 转换结果）
/// 就跑到菱形外 —— 越界一目了然。
static inline float2 vsDiamondTop(float3 rgb)
{
    return float2(rgb.b - rgb.g, (rgb.g + rgb.b) * 0.5);
}

static inline float2 vsDiamondBottom(float3 rgb)
{
    return float2(rgb.r - rgb.g, -(rgb.r + rgb.g) * 0.5);
}

/// 显示坐标（x、y 均为 −1…1，y 向上）→ 直方图 bin（binY 越大越靠画面顶部）
static inline uint2 vsDiamondBin(float2 p)
{
    float bx = clamp((p.x + 1.0) * 0.5, 0.0, 1.0);
    float by = clamp((p.y + 1.0) * 0.5, 0.0, 1.0);
    return uint2(uint(bx * 255.0 + 0.5), uint(by * 255.0 + 0.5));
}

/// BT.709 传输函数（OETF）的反函数：示波器拿到的是显示伽马编码值，CIE 需要线性光
static inline float vsToLinear(float v)
{
    return v < 0.081 ? v / 4.5 : pow((v + 0.099) / 1.099, 1.0 / 0.45);
}

/// 马蹄图（CIE 1931 xy 色度）：R'G'B' → 线性 RGB → XYZ → 色度坐标 xy
/// 返回负值表示该像素太暗（色度不可靠），调用方应丢弃。
static inline float2 vsChromaticity(float3 rgb)
{
    float3 lin = float3(vsToLinear(rgb.r), vsToLinear(rgb.g), vsToLinear(rgb.b));
    float3 xyz = float3(dot(lin, float3(0.4124, 0.3576, 0.1805)),
                        dot(lin, float3(0.2126, 0.7152, 0.0722)),
                        dot(lin, float3(0.0193, 0.1192, 0.9505)));
    float sum = xyz.x + xyz.y + xyz.z;
    if (sum < 2.0e-4) {                    // 亮度太低时色度会乱跳，直接丢掉
        return float2(-1.0, -1.0);
    }
    return float2(xyz.x / sum, xyz.y / sum);
}

/// 马蹄图的 xy → 0–1（与 SwiftUI 刻度层共用 ShaderTypes.h 里的常量）
static inline float2 vsCIENormalize(float2 xy)
{
    const float span = float(VS_CIE_SPAN);
    return float2((xy.x + float(VS_CIE_ORIGIN_X)) / span,
                  (xy.y + float(VS_CIE_ORIGIN_Y)) / span);
}

/// 把计数压缩到 0-1，使用对数曲线，避免大块单色区域把整幅图压黑
static inline float vsRamp(uint count, float reference)
{
    if (count == 0u) {
        return 0.0;
    }
    return saturate(log(1.0 + float(count)) / log(1.0 + max(reference, 2.0)));
}

// MARK: - 累计

kernel void vsAccumulateHistogram(texture2d<float, access::read> source [[texture(VSTextureIndexSource)]],
                                  device atomic_uint *histogram [[buffer(0)]],
                                  constant VSScopeUniforms &u [[buffer(1)]],
                                  uint2 gid [[thread_position_in_grid]])
{
    uint stride = uint(max(u.params.z, 1.0));
    uint2 p = gid * stride;

    uint width = source.get_width();
    uint height = source.get_height();
    if (p.x >= width || p.y >= height) {
        return;
    }

    float3 rgb = saturate(source.read(p).rgb);

    uint column = min(uint(VS_WAVEFORM_COLUMNS) - 1u, (p.x * uint(VS_WAVEFORM_COLUMNS)) / max(width, 1u));

    uint r = uint(clamp(rgb.r * 255.0 + 0.5, 0.0, 255.0));
    uint g = uint(clamp(rgb.g * 255.0 + 0.5, 0.0, 255.0));
    uint b = uint(clamp(rgb.b * 255.0 + 0.5, 0.0, 255.0));
    uint y = uint(clamp(vsLuma(rgb) * 255.0 + 0.5, 0.0, 255.0));

    // 波形数据只在真的要看波形 / Parade 时才算（省一半以上的原子操作）
    if (u.flags.z > 0.5) {
        atomic_fetch_add_explicit(&histogram[vsWaveIndex(0u, r, column)], 1u, memory_order_relaxed);
        atomic_fetch_add_explicit(&histogram[vsWaveIndex(1u, g, column)], 1u, memory_order_relaxed);
        atomic_fetch_add_explicit(&histogram[vsWaveIndex(2u, b, column)], 1u, memory_order_relaxed);
        atomic_fetch_add_explicit(&histogram[vsWaveIndex(3u, y, column)], 1u, memory_order_relaxed);
    }

    // y = 需要哪几种二维直方图：1 矢量（Cb/Cr）· 2 钻石（RGB 色域）· 4 马蹄（CIE 色度）
    uint mask = uint(max(u.flags.y, 0.0));

    if ((mask & 1u) != 0u) {
        float2 cbcr = vsChroma(rgb);
        int vx = int(clamp((cbcr.x + 0.5) * float(VS_VECTORSCOPE_SIZE), 0.0, float(VS_VECTORSCOPE_SIZE) - 1.0));
        int vy = int(clamp((cbcr.y + 0.5) * float(VS_VECTORSCOPE_SIZE), 0.0, float(VS_VECTORSCOPE_SIZE) - 1.0));
        atomic_fetch_add_explicit(&histogram[vsVectorIndex(uint(vx), uint(vy), uint(VS_GAMUT_SECTION_VECTORSCOPE))],
                                  1u, memory_order_relaxed);
    }

    if ((mask & 2u) != 0u) {
        // 钻石图：一帧里要累加**两个**点 —— 上菱形（G/B）与下菱形（G/R）
        uint2 top = vsDiamondBin(vsDiamondTop(rgb));
        uint2 bottom = vsDiamondBin(vsDiamondBottom(rgb));
        atomic_fetch_add_explicit(&histogram[vsVectorIndex(top.x, top.y, uint(VS_GAMUT_SECTION_DIAMOND))],
                                  1u, memory_order_relaxed);
        atomic_fetch_add_explicit(&histogram[vsVectorIndex(bottom.x, bottom.y, uint(VS_GAMUT_SECTION_DIAMOND))],
                                  1u, memory_order_relaxed);
    }

    if ((mask & 4u) != 0u) {
        float2 xy = vsChromaticity(rgb);
        if (xy.x >= 0.0) {
            float2 n = vsCIENormalize(xy);
            uint bx = uint(clamp(n.x, 0.0, 1.0) * 255.0 + 0.5);
            uint by = uint(clamp(n.y, 0.0, 1.0) * 255.0 + 0.5);
            atomic_fetch_add_explicit(&histogram[vsVectorIndex(bx, by, uint(VS_GAMUT_SECTION_CIE))],
                                      1u, memory_order_relaxed);
        }
    }
}

// MARK: - 全局测量（数值读数）

/// 把采样像素累加进「全局直方图」：
///   4 个通道各 256 bin + 64 bin 色度半径直方图
/// CPU 侧据此算出峰值白、黑位、平均值、超白/超黑占比、各通道峰值、色度峰值。
/// 采样步长由 u.params.z 决定（默认 4：每 16 个像素取 1 个，够精确而且几乎不耗性能）。
kernel void vsAccumulateMeasurement(texture2d<float, access::read> source [[texture(VSTextureIndexSource)]],
                                    device atomic_uint *measure [[buffer(0)]],
                                    constant VSScopeUniforms &u [[buffer(1)]],
                                    uint2 gid [[thread_position_in_grid]])
{
    uint stride = uint(max(u.params.z, 1.0));
    uint2 p = gid * stride;

    uint width = source.get_width();
    uint height = source.get_height();
    if (p.x >= width || p.y >= height) {
        return;
    }

    float3 rgb = saturate(source.read(p).rgb);

    uint r = uint(clamp(rgb.r * 255.0 + 0.5, 0.0, 255.0));
    uint g = uint(clamp(rgb.g * 255.0 + 0.5, 0.0, 255.0));
    uint b = uint(clamp(rgb.b * 255.0 + 0.5, 0.0, 255.0));
    uint y = uint(clamp(vsLuma(rgb) * 255.0 + 0.5, 0.0, 255.0));

    uint bins = uint(VS_MEASURE_BINS);
    atomic_fetch_add_explicit(&measure[r], 1u, memory_order_relaxed);
    atomic_fetch_add_explicit(&measure[bins + g], 1u, memory_order_relaxed);
    atomic_fetch_add_explicit(&measure[bins * 2u + b], 1u, memory_order_relaxed);
    atomic_fetch_add_explicit(&measure[bins * 3u + y], 1u, memory_order_relaxed);

    // 色度幅度：|Cb,Cr| / 0.5 —— 0 = 灰，1 = 100% 饱和度
    float2 cbcr = vsChroma(rgb);
    float radius = clamp(length(cbcr) * 2.0, 0.0, 1.0);
    uint radialBins = uint(VS_MEASURE_RADIAL_BINS);
    uint radialBin = uint(clamp(radius * float(radialBins - 1u) + 0.5,
                               0.0, float(radialBins - 1u)));
    atomic_fetch_add_explicit(&measure[uint(VS_MEASURE_RADIAL_INDEX) + radialBin],
                              1u, memory_order_relaxed);
}

// MARK: - 归一化

kernel void vsNormalizeWaveform(device atomic_uint *histogram [[buffer(0)]],
                                texture2d<float, access::write> dst [[texture(VSTextureIndexScope)]],
                                constant VSScopeUniforms &u [[buffer(1)]],
                                uint2 gid [[thread_position_in_grid]])
{
    if (gid.x >= dst.get_width() || gid.y >= dst.get_height()) {
        return;
    }

    uint bin = dst.get_height() - 1u - gid.y;
    uint count = atomic_load_explicit(&histogram[vsWaveIndex(3u, bin, gid.x)], memory_order_relaxed);
    float t = vsRamp(count, u.refs.x);

    dst.write(float4(t, t, t, 1.0), gid);
}

kernel void vsNormalizeOverlay(device atomic_uint *histogram [[buffer(0)]],
                               texture2d<float, access::write> dst [[texture(VSTextureIndexScope)]],
                               constant VSScopeUniforms &u [[buffer(1)]],
                               uint2 gid [[thread_position_in_grid]])
{
    if (gid.x >= dst.get_width() || gid.y >= dst.get_height()) {
        return;
    }

    uint column = gid.x;
    uint bin = dst.get_height() - 1u - gid.y;

    float r = vsRamp(atomic_load_explicit(&histogram[vsWaveIndex(0u, bin, column)], memory_order_relaxed), u.refs.x);
    float g = vsRamp(atomic_load_explicit(&histogram[vsWaveIndex(1u, bin, column)], memory_order_relaxed), u.refs.x);
    float b = vsRamp(atomic_load_explicit(&histogram[vsWaveIndex(2u, bin, column)], memory_order_relaxed), u.refs.x);

    dst.write(float4(r, g, b, 1.0), gid);
}

kernel void vsNormalizeParade(device atomic_uint *histogram [[buffer(0)]],
                              texture2d<float, access::write> dst [[texture(VSTextureIndexScope)]],
                              constant VSScopeUniforms &u [[buffer(1)]],
                              uint2 gid [[thread_position_in_grid]])
{
    if (gid.x >= dst.get_width() || gid.y >= dst.get_height()) {
        return;
    }

    uint channel = gid.x / uint(VS_WAVEFORM_COLUMNS);
    uint column = gid.x % uint(VS_WAVEFORM_COLUMNS);
    uint bin = dst.get_height() - 1u - gid.y;

    uint count = atomic_load_explicit(&histogram[vsWaveIndex(channel, bin, column)], memory_order_relaxed);
    float t = vsRamp(count, u.refs.x);

    float3 tint = float3(1.0, 1.0, 1.0);
    if (channel == 0u) {
        tint = float3(1.0, 0.25, 0.25);
    } else if (channel == 1u) {
        tint = float3(0.25, 1.0, 0.35);
    } else if (channel == 2u) {
        tint = float3(0.35, 0.45, 1.0);
    }

    dst.write(float4(tint * t, 1.0), gid);
}

kernel void vsNormalizeVectorscope(device atomic_uint *histogram [[buffer(0)]],
                                   texture2d<float, access::write> dst [[texture(VSTextureIndexScope)]],
                                   constant VSScopeUniforms &u [[buffer(1)]],
                                   uint2 gid [[thread_position_in_grid]])
{
    if (gid.x >= dst.get_width() || gid.y >= dst.get_height()) {
        return;
    }

    uint binX = gid.x;
    uint binY = dst.get_height() - 1u - gid.y;

    // refs.z 选读哪一块二维直方图：0 = 矢量、1 = 钻石、2 = 马蹄
    uint section = uint(max(u.refs.z, 0.0));

    uint count = atomic_load_explicit(&histogram[vsVectorIndex(binX, binY, section)], memory_order_relaxed);
    float t = vsRamp(count, u.refs.y);

    dst.write(float4(t, t, t, 1.0), gid);
}
