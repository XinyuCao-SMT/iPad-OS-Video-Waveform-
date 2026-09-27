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

static inline uint vsVectorIndex(uint x, uint y)
{
    return uint(VS_WAVEFORM_COUNT) + y * uint(VS_VECTORSCOPE_SIZE) + x;
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

    atomic_fetch_add_explicit(&histogram[vsWaveIndex(0u, r, column)], 1u, memory_order_relaxed);
    atomic_fetch_add_explicit(&histogram[vsWaveIndex(1u, g, column)], 1u, memory_order_relaxed);
    atomic_fetch_add_explicit(&histogram[vsWaveIndex(2u, b, column)], 1u, memory_order_relaxed);
    atomic_fetch_add_explicit(&histogram[vsWaveIndex(3u, y, column)], 1u, memory_order_relaxed);

    float2 cbcr = vsChroma(rgb);
    int vx = int(clamp((cbcr.x + 0.5) * float(VS_VECTORSCOPE_SIZE), 0.0, float(VS_VECTORSCOPE_SIZE) - 1.0));
    int vy = int(clamp((cbcr.y + 0.5) * float(VS_VECTORSCOPE_SIZE), 0.0, float(VS_VECTORSCOPE_SIZE) - 1.0));

    atomic_fetch_add_explicit(&histogram[vsVectorIndex(uint(vx), uint(vy))], 1u, memory_order_relaxed);
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

    uint count = atomic_load_explicit(&histogram[vsVectorIndex(binX, binY)], memory_order_relaxed);
    float t = vsRamp(count, u.refs.y);

    dst.write(float4(t, t, t, 1.0), gid);
}
