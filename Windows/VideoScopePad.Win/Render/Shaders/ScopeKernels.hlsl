//
//  ScopeKernels.hlsl
//  VideoScopePad
//
//  示波器统计：从已完成转换/调色的中间纹理中累计直方图。
//    - vsAccumulateHistogram : 每帧一次，原子累加到直方图缓冲区
//    - vsNormalizeWaveform   : 亮度波形（0-255 码值 -> 竖直 bin）
//    - vsNormalizeOverlay    : RGB 三通道叠加波形
//    - vsNormalizeParade     : RGB Parade（三列并排）
//    - vsNormalizeVectorscope: 矢量示波器（Cb/Cr 二维直方图）
//
//  本文件是 VideoScopePad/VideoScopePad/Shaders/ScopeKernels.metal 的逐行 HLSL 翻译，
//  数学表达式、常量、注释一律照搬，没有做任何合并/化简/精度改动（half 没有出现；所有
//  浮点量都保持 float，没有降精度）。
//
//  Metal → HLSL 映射说明（与本次翻译逐条对应）：
//    · texture2d<float, access::read> + read(uint2) → Texture2D<float4> + Load(int3(coord, 0))
//      （Metal 的 read 返回 float4，单通道也取 .r；HLSL 同样统一用 float4 再取分量）
//    · texture2d<float, access::write> + write(v, coord) → RWTexture2D<float4> + tex[coord] = v
//    · device atomic_uint* + atomic_fetch_add_explicit → RWStructuredBuffer<uint> + InterlockedAdd
//      （丢弃旧值时 HLSL 写 InterlockedAdd(buf[i], v)；需要旧值时传 out 参数）
//    · atomic_load_explicit(..., memory_order_relaxed) → 直接下标读取
//      （HLSL 没有 atomic load，对 RWStructuredBuffer 的普通读取就是 relaxed 读）
//    · device float* → RWStructuredBuffer<float>（SM 5.0 没有 Buffer<float> 这种裸指针缓冲）
//    · constant T& [[buffer(N)]] → 全局 cbuffer 块 + register(bN)
//      （fxc 不认 ConstantBuffer<T> 这种写法，见 ShaderTypes.hlsli 顶部说明）
//    · get_width() / get_height() → GetDimensions(out width, out height)
//    · thread_position_in_grid → SV_DispatchThreadID
//    · [numthreads] 取 iOS 侧 ScopeEngine.swift 里实际用的 threadsPerThreadgroup（16×16×1），
//      vsFrameSignature 取 FrameSignatureAnalyzer.swift 里的 8×8×1。
//
//  入口点命名：kernel 名 = Metal 函数名去 vs 前缀加 CS 前缀（例如 vsNormalizeParade →
//  CSNormalizeParade），函数体与 Metal 原文逐字对应。
//

#include "ShaderTypes.hlsli"

// MARK: - 资源绑定
//
// Metal 的 buffer 与 texture 是两个独立索引空间；HLSL 里 SRV(t#) / CBV(b#) / sampler(s#) 各自独立，
// 但**可写缓冲与可写纹理共用 u# 空间**，所以有两个地方必须留意：
//   1) 只读纹理 texture(VSTextureIndexSource)=0 → register(t0)，不与 u0 冲突；
//   2) vsNormalize* 里的 dst 是 texture(VSTextureIndexScope)=0，若照抄成 register(u0) 会与
//      histogram 的 buffer(0) → register(u0) 撞车（fxc 直接报 error X4500: overlapping register
//      semantics），故这几个 kernel 的 dst 挂 u1；histogram 仍按 Metal 的 buffer(0) 挂 u0。
//      这是本次翻译里唯一的寄存器号偏移，宿主侧绑定要与之一致。

// Metal: device atomic_uint *histogram [[buffer(0)]] / measure [[buffer(0)]] / out [[buffer(0)]]
RWStructuredBuffer<uint> histogram : register(u0);       // vsAccumulateHistogram / vsNormalize*
RWStructuredBuffer<uint> measure : register(u0);         // vsAccumulateMeasurement
RWStructuredBuffer<float> frameSignature : register(u0); // vsFrameSignature（Metal 里参数名是 out，
                                                          // out 是 HLSL 关键字，改名 frameSignature）

// Metal: texture2d<float, access::read> source [[texture(VSTextureIndexSource)]]
Texture2D<float4> source : register(t0);

// Metal: texture2d<float, access::write> dst [[texture(VSTextureIndexScope)]]
RWTexture2D<float4> dst : register(u1);   // 见上面第 2 条：Metal 是 texture(0)，HLSL 挪到 u1

// Metal: constant VSScopeUniforms &u [[buffer(1)]]
cbuffer VSScopeUniformsBuffer : register(b1) { VSScopeUniforms u; }

// Metal: constant uint &gridSize [[buffer(1)]]
// 注意：HLSL 里常量必须在常量缓冲区里，宿主侧要按 16 字节（而不是 4 字节）提交，
//       即 D3D11 侧是 CreateBuffer(ByteWidth = 16) 而不是 iOS 那样的 setBytes(length: 4)。
cbuffer VSFrameSignatureUniformsBuffer : register(b1) { uint gridSize; }

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

// Metal: kernel void vsAccumulateHistogram(texture2d<float, access::read> source [[texture(VSTextureIndexSource)]],
//                                          device atomic_uint *histogram [[buffer(0)]],
//                                          constant VSScopeUniforms &u [[buffer(1)]],
//                                          uint2 gid [[thread_position_in_grid]])
[numthreads(16, 16, 1)]
void CSAccumulateHistogram(uint2 gid : SV_DispatchThreadID)
{
    uint stride = uint(max(u.params.z, 1.0));
    uint2 p = gid * stride;

    // Metal: source.get_width() / source.get_height() → HLSL 用 GetDimensions 输出参数
    uint width, height;
    source.GetDimensions(width, height);
    if (p.x >= width || p.y >= height) {
        return;
    }

    // Metal: source.read(p).rgb（read 返回 float4）
    // HLSL: Texture2D.Load 的坐标是 int3(coord, mipLevel)
    float3 rgb = saturate(source.Load(int3(p, 0)).rgb);

    uint column = min(uint(VS_WAVEFORM_COLUMNS) - 1u, (p.x * uint(VS_WAVEFORM_COLUMNS)) / max(width, 1u));

    uint r = uint(clamp(rgb.r * 255.0 + 0.5, 0.0, 255.0));
    uint g = uint(clamp(rgb.g * 255.0 + 0.5, 0.0, 255.0));
    uint b = uint(clamp(rgb.b * 255.0 + 0.5, 0.0, 255.0));
    uint y = uint(clamp(vsLuma(rgb) * 255.0 + 0.5, 0.0, 255.0));

    // 波形数据只在真的要看波形 / Parade 时才算（省一半以上的原子操作）
    if (u.flags.z > 0.5) {
        // Metal: atomic_fetch_add_explicit(&histogram[i], 1u, memory_order_relaxed)（旧值丢弃）
        InterlockedAdd(histogram[vsWaveIndex(0u, r, column)], 1u);
        InterlockedAdd(histogram[vsWaveIndex(1u, g, column)], 1u);
        InterlockedAdd(histogram[vsWaveIndex(2u, b, column)], 1u);
        InterlockedAdd(histogram[vsWaveIndex(3u, y, column)], 1u);
    }

    // y = 需要哪几种二维直方图：1 矢量（Cb/Cr）· 2 钻石（RGB 色域）· 4 马蹄（CIE 色度）
    uint mask = uint(max(u.flags.y, 0.0));

    if ((mask & 1u) != 0u) {
        float2 cbcr = vsChroma(rgb);
        int vx = int(clamp((cbcr.x + 0.5) * float(VS_VECTORSCOPE_SIZE), 0.0, float(VS_VECTORSCOPE_SIZE) - 1.0));
        int vy = int(clamp((cbcr.y + 0.5) * float(VS_VECTORSCOPE_SIZE), 0.0, float(VS_VECTORSCOPE_SIZE) - 1.0));
        InterlockedAdd(histogram[vsVectorIndex(uint(vx), uint(vy), uint(VS_GAMUT_SECTION_VECTORSCOPE))], 1u);
    }

    if ((mask & 2u) != 0u) {
        // 钻石图：一帧里要累加**两个**点 —— 上菱形（G/B）与下菱形（G/R）
        uint2 top = vsDiamondBin(vsDiamondTop(rgb));
        uint2 bottom = vsDiamondBin(vsDiamondBottom(rgb));
        InterlockedAdd(histogram[vsVectorIndex(top.x, top.y, uint(VS_GAMUT_SECTION_DIAMOND))], 1u);
        InterlockedAdd(histogram[vsVectorIndex(bottom.x, bottom.y, uint(VS_GAMUT_SECTION_DIAMOND))], 1u);
    }

    if ((mask & 4u) != 0u) {
        float2 xy = vsChromaticity(rgb);
        if (xy.x >= 0.0) {
            float2 n = vsCIENormalize(xy);
            uint bx = uint(clamp(n.x, 0.0, 1.0) * 255.0 + 0.5);
            uint by = uint(clamp(n.y, 0.0, 1.0) * 255.0 + 0.5);
            InterlockedAdd(histogram[vsVectorIndex(bx, by, uint(VS_GAMUT_SECTION_CIE))], 1u);
        }
    }
}

// MARK: - 画面签名（声画延时测量用）
//
// 每帧算一次：64×64 采样点的平均亮度与平均饱和度，写进一块小 buffer 由 CPU 回读。
// 静音黑场（亮度低、饱和度低）与彩条（亮度高、饱和度高）据此就能区分，
// 「黑场 → 彩条」的那一帧就是声画延时测量里的画面事件。

// Metal: kernel void vsFrameSignature(texture2d<float, access::read> source [[texture(VSTextureIndexSource)]],
//                                     device float *out [[buffer(0)]],
//                                     constant uint &gridSize [[buffer(1)]],
//                                     uint2 gid [[thread_position_in_grid]])
[numthreads(8, 8, 1)]
void CSFrameSignature(uint2 gid : SV_DispatchThreadID)
{
    uint width, height;
    source.GetDimensions(width, height);
    if (gid.x >= gridSize || gid.y >= gridSize || width == 0u || height == 0u) {
        return;
    }

    uint x = min(gid.x * width / gridSize, width - 1u);
    uint y = min(gid.y * height / gridSize, height - 1u);
    float3 rgb = saturate(source.Load(int3(uint2(x, y), 0)).rgb);

    float luma = vsLuma(rgb);
    float mx = max(rgb.r, max(rgb.g, rgb.b));
    float mn = min(rgb.r, min(rgb.g, rgb.b));
    float saturation = mx > 1.0e-4 ? (mx - mn) / mx : 0.0;

    uint index = (gid.y * gridSize + gid.x) * 2u;
    frameSignature[index] = luma;
    frameSignature[index + 1u] = saturation;
}

// MARK: - 全局测量（数值读数）

/// 把采样像素累加进「全局直方图」：
///   4 个通道各 256 bin + 64 bin 色度半径直方图
/// CPU 侧据此算出峰值白、黑位、平均值、超白/超黑占比、各通道峰值、色度峰值。
/// 采样步长由 u.params.z 决定（默认 4：每 16 个像素取 1 个，够精确而且几乎不耗性能）。
// Metal: kernel void vsAccumulateMeasurement(texture2d<float, access::read> source [[texture(VSTextureIndexSource)]],
//                                            device atomic_uint *measure [[buffer(0)]],
//                                            constant VSScopeUniforms &u [[buffer(1)]],
//                                            uint2 gid [[thread_position_in_grid]])
[numthreads(16, 16, 1)]
void CSAccumulateMeasurement(uint2 gid : SV_DispatchThreadID)
{
    uint stride = uint(max(u.params.z, 1.0));
    uint2 p = gid * stride;

    uint width, height;
    source.GetDimensions(width, height);
    if (p.x >= width || p.y >= height) {
        return;
    }

    float3 rgb = saturate(source.Load(int3(p, 0)).rgb);

    uint r = uint(clamp(rgb.r * 255.0 + 0.5, 0.0, 255.0));
    uint g = uint(clamp(rgb.g * 255.0 + 0.5, 0.0, 255.0));
    uint b = uint(clamp(rgb.b * 255.0 + 0.5, 0.0, 255.0));
    uint y = uint(clamp(vsLuma(rgb) * 255.0 + 0.5, 0.0, 255.0));

    uint bins = uint(VS_MEASURE_BINS);
    InterlockedAdd(measure[r], 1u);
    InterlockedAdd(measure[bins + g], 1u);
    InterlockedAdd(measure[bins * 2u + b], 1u);
    InterlockedAdd(measure[bins * 3u + y], 1u);

    // 色度幅度：|Cb,Cr| / 0.5 —— 0 = 灰，1 = 100% 饱和度
    float2 cbcr = vsChroma(rgb);
    float radius = clamp(length(cbcr) * 2.0, 0.0, 1.0);
    uint radialBins = uint(VS_MEASURE_RADIAL_BINS);
    uint radialBin = uint(clamp(radius * float(radialBins - 1u) + 0.5,
                               0.0, float(radialBins - 1u)));
    InterlockedAdd(measure[uint(VS_MEASURE_RADIAL_INDEX) + radialBin], 1u);
}

// MARK: - 归一化

// Metal: kernel void vsNormalizeWaveform(device atomic_uint *histogram [[buffer(0)]],
//                                        texture2d<float, access::write> dst [[texture(VSTextureIndexScope)]],
//                                        constant VSScopeUniforms &u [[buffer(1)]],
//                                        uint2 gid [[thread_position_in_grid]])
[numthreads(16, 16, 1)]
void CSNormalizeWaveform(uint2 gid : SV_DispatchThreadID)
{
    uint dstWidth, dstHeight;
    dst.GetDimensions(dstWidth, dstHeight);
    if (gid.x >= dstWidth || gid.y >= dstHeight) {
        return;
    }

    uint bin = dstHeight - 1u - gid.y;
    // Metal: atomic_load_explicit(..., memory_order_relaxed) → HLSL 直接读
    uint count = histogram[vsWaveIndex(3u, bin, gid.x)];
    float t = vsRamp(count, u.refs.x);

    dst[gid] = float4(t, t, t, 1.0);
}

// Metal: kernel void vsNormalizeOverlay(device atomic_uint *histogram [[buffer(0)]],
//                                       texture2d<float, access::write> dst [[texture(VSTextureIndexScope)]],
//                                       constant VSScopeUniforms &u [[buffer(1)]],
//                                       uint2 gid [[thread_position_in_grid]])
[numthreads(16, 16, 1)]
void CSNormalizeOverlay(uint2 gid : SV_DispatchThreadID)
{
    uint dstWidth, dstHeight;
    dst.GetDimensions(dstWidth, dstHeight);
    if (gid.x >= dstWidth || gid.y >= dstHeight) {
        return;
    }

    uint column = gid.x;
    uint bin = dstHeight - 1u - gid.y;

    float r = vsRamp(histogram[vsWaveIndex(0u, bin, column)], u.refs.x);
    float g = vsRamp(histogram[vsWaveIndex(1u, bin, column)], u.refs.x);
    float b = vsRamp(histogram[vsWaveIndex(2u, bin, column)], u.refs.x);

    dst[gid] = float4(r, g, b, 1.0);
}

// Metal: kernel void vsNormalizeParade(device atomic_uint *histogram [[buffer(0)]],
//                                      texture2d<float, access::write> dst [[texture(VSTextureIndexScope)]],
//                                      constant VSScopeUniforms &u [[buffer(1)]],
//                                      uint2 gid [[thread_position_in_grid]])
[numthreads(16, 16, 1)]
void CSNormalizeParade(uint2 gid : SV_DispatchThreadID)
{
    uint dstWidth, dstHeight;
    dst.GetDimensions(dstWidth, dstHeight);
    if (gid.x >= dstWidth || gid.y >= dstHeight) {
        return;
    }

    uint channel = gid.x / uint(VS_WAVEFORM_COLUMNS);
    uint column = gid.x % uint(VS_WAVEFORM_COLUMNS);
    uint bin = dstHeight - 1u - gid.y;

    uint count = histogram[vsWaveIndex(channel, bin, column)];
    float t = vsRamp(count, u.refs.x);

    float3 tint = float3(1.0, 1.0, 1.0);
    if (channel == 0u) {
        tint = float3(1.0, 0.25, 0.25);
    } else if (channel == 1u) {
        tint = float3(0.25, 1.0, 0.35);
    } else if (channel == 2u) {
        tint = float3(0.35, 0.45, 1.0);
    }

    dst[gid] = float4(tint * t, 1.0);
}

// Metal: kernel void vsNormalizeVectorscope(device atomic_uint *histogram [[buffer(0)]],
//                                           texture2d<float, access::write> dst [[texture(VSTextureIndexScope)]],
//                                           constant VSScopeUniforms &u [[buffer(1)]],
//                                           uint2 gid [[thread_position_in_grid]])
[numthreads(16, 16, 1)]
void CSNormalizeVectorscope(uint2 gid : SV_DispatchThreadID)
{
    uint dstWidth, dstHeight;
    dst.GetDimensions(dstWidth, dstHeight);
    if (gid.x >= dstWidth || gid.y >= dstHeight) {
        return;
    }

    uint binX = gid.x;
    uint binY = dstHeight - 1u - gid.y;

    // refs.z 选读哪一块二维直方图：0 = 矢量、1 = 钻石、2 = 马蹄
    uint section = uint(max(u.refs.z, 0.0));

    uint count = histogram[vsVectorIndex(binX, binY, section)];
    float t = vsRamp(count, u.refs.y);

    dst[gid] = float4(t, t, t, 1.0);
}
