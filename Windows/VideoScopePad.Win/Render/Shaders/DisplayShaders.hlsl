//
//  DisplayShaders.hlsl
//  VideoScopePad
//
//  监视器显示链路：
//    1) YUV / BGRA  →  R'G'B'         (fsVideoBiPlanar / fsVideoBGRA)
//    2) 应用 1D+3D LUT 与调色          (fsApplyLUTAndGrade)
//    3) 显示模式（彩色/亮度/R/G/B）与合成 (fsDisplay / fsSolidColor / fsScopeTrace)
//
//  本文件是 VideoScopePad/VideoScopePad/Shaders/DisplayShaders.metal 的逐行 HLSL 翻译。
//  数学表达式、常量、注释一律照搬，没有合并/化简/改精度。所有浮点量保持 float。
//
//  Metal → HLSL 映射说明（逐条）：
//    · vertex VOut f(uint vid [[vertex_id]]) → VOut VSQuadVertex(uint vid : SV_VertexID)
//    · fragment float4 f(VOut in [[stage_in]]) → float4 PSX(VOut input) : SV_Target
//    · [[position]] → SV_Position（顶点输出与片元输入同语义名；片元里它同样是 drawable 像素坐标）
//    · 无属性的 float2 uv（[[user(...)]] 默认）→ TEXCOORD0
//    · texture2d<float> + sample(s, uv) → Texture2D<float4> + Sample(LinearClampSampler, uv)
//    · texture3d<float> + sample → Texture3D<float4> + Sample（LUT 3D 就是纹理，不是纹理数组）
//    · constexpr sampler s(filter::linear, address::clamp_to_edge) → SamplerState LinearClampSampler : register(s0)
//      （本文件里两个 sampler —— fs* 里的 s 与 LUT 辅助函数里的 lutSampler —— 参数完全相同，
//        都是 linear + clamp_to_edge，所以合成同一个 SamplerState）
//    · constant T& [[buffer(N)]] → 全局 cbuffer 块 + register(bN)
//      （fxc 不认 ConstantBuffer<T>，见 ShaderTypes.hlsli 顶部说明）
//    · u.yuvToRGB * ycbcr（Metal 的矩阵乘列向量）→ mul(u.yuvToRGB, ycbcr)
//      yuvToRGB 在 .hlsli 里是 column_major float3x3：内存布局与 Metal 的 float3x3 完全一致
//      （列主序、每列 16 字节、共 48 字节），mul(M, v) 与 Metal 的 M * v 计算结果逐位相同。
//    · mix → lerp，fract → frac，fmod → fmod（本文件没用到 fmod）
//    · (void)u; / (void)in; 这类「显式忽略参数」的写法在 HLSL 里会报 error X3017
//      （cannot convert from 'const struct S' to 'void'），故改为注释说明，函数体其余照抄。
//

#include "ShaderTypes.hlsli"

struct VSVertexOut {
    float4 position : SV_Position;   // Metal: float4 position [[position]]
    float2 uv : TEXCOORD0;           // Metal: float2 uv（无属性，默认插值；HLSL 给 TEXCOORD0）
};

// MARK: - 资源绑定
//
// Metal 的参数是按函数给的，HLSL 里常量缓冲/纹理/采样器都是全局对象，下面按 Metal 的
// buffer/texture 索引逐一对齐（t# 只读纹理、b# 常量、s# 采样器；本文件没有 UAV）。
//
//   VSBufferIndexQuadUniforms   = 0 → b0
//   VSBufferIndexRenderUniforms = 1 → b1（VSRenderUniforms u；fsSolidColor 的 vector_float4 也挂 b1）
//   VSBufferIndexScopeUniforms  = 2 → b2
//   VSTextureIndexSource = 0 → t0，VSTextureIndexLUT3D = 1 → t1，VSTextureIndexLUT1D = 2 → t2
//   VSTextureIndexChroma = 1 → t1，VSTextureIndexScope = 0 → t0

// Metal: constant VSQuadUniforms &quad [[buffer(VSBufferIndexQuadUniforms)]]
cbuffer VSQuadUniformsBuffer : register(b0) { VSQuadUniforms quad; }

// Metal: constant VSRenderUniforms &u [[buffer(VSBufferIndexRenderUniforms)]]
cbuffer VSRenderUniformsBuffer : register(b1) { VSRenderUniforms u; }

// Metal: constant vector_float4 &color [[buffer(VSBufferIndexRenderUniforms)]]（fsSolidColor）
cbuffer VSSolidColorBuffer : register(b1) { float4 color; }

// Metal: constant VSScopeUniforms &u [[buffer(VSBufferIndexScopeUniforms)]]（fsScopeTrace）
// 同一文件里 b1 的 VSRenderUniforms 已经占用了全局名 u，这里改叫 uScope；
// fsScopeTrace 内部再用一行局部别名把名字还原成 Metal 里的 u，保证函数体逐字对应。
cbuffer VSScopeUniformsBuffer : register(b2) { VSScopeUniforms uScope; }

// Metal: texture2d<float> sourceTexture / lumaTexture [[texture(VSTextureIndexSource)]]
//        （fsVideoBiPlanar 用的是 lumaTexture，其余用 sourceTexture，两者都是 texture(0)；
//          HLSL 里同名保留，各自只被自己的入口点使用，因此共用 t0 不会冲突）
Texture2D<float4> sourceTexture : register(t0);
// Metal: texture2d<float> lumaTexture [[texture(VSTextureIndexSource)]]（fsVideoBiPlanar）
Texture2D<float4> lumaTexture : register(t0);
// Metal: texture2d<float> scopeTexture [[texture(VSTextureIndexScope)]]（fsScopeTrace；同样是 texture(0)）
Texture2D<float4> scopeTexture : register(t0);
// Metal: texture2d<float> chromaTexture [[texture(VSTextureIndexChroma)]]
Texture2D<float4> chromaTexture : register(t1);
// Metal: texture3d<float> lut3D [[texture(VSTextureIndexLUT3D)]]
Texture3D<float4> lut3D : register(t1);
// Metal: texture2d<float> lut1D [[texture(VSTextureIndexLUT1D)]]
Texture2D<float4> lut1D : register(t2);

// Metal: constexpr sampler s(filter::linear, address::clamp_to_edge) / lutSampler(filter::linear, address::clamp_to_edge)
SamplerState LinearClampSampler : register(s0);

// MARK: - 顶点着色器
//
// rect 使用左上角为原点的单位空间（与 SwiftUI 一致），此处统一转换为 NDC。

// Metal: vertex VSVertexOut vsQuadVertex(uint vid [[vertex_id]],
//                                        constant VSQuadUniforms &quad [[buffer(VSBufferIndexQuadUniforms)]])
VSVertexOut VSQuadVertex(uint vid : SV_VertexID)
{
    float2 corner = float2(float(vid & 1), float((vid >> 1) & 1));
    float2 unit = quad.rect.xy + corner * quad.rect.zw;

    VSVertexOut output;
    output.position = float4(unit.x * 2.0 - 1.0, 1.0 - unit.y * 2.0, 0.0, 1.0);

    // 纹理采样：先按 uv 区域取点，再按需要绕采样区中心旋转
    // （画面方向跟随设备时用；几何矩形不变，所以不会溢出自己的格子）
    float2 uv = quad.uv.xy + corner * quad.uv.zw;
    float rotation = quad.misc.x;
    if (rotation > 0.5) {
        float2 center = quad.uv.xy + quad.uv.zw * 0.5;
        float2 d = uv - center;
        if (rotation > 45.0 && rotation < 135.0) {
            uv = center + float2(d.y, -d.x);          // 顺时针 90°
        } else if (rotation > 225.0 && rotation < 315.0) {
            uv = center + float2(-d.y, d.x);          // 逆时针 90°（即顺时针 270°）
        } else {
            uv = center - d;                          // 180°
        }
    }
    output.uv = uv;
    return output;
}

// MARK: - 调色

// Metal: inline float3 vsApplyGrade(float3 c, constant VSRenderUniforms &u)
static inline float3 vsApplyGrade(float3 c, in VSRenderUniforms renderUniforms)
{
    float3 rgb = c;

    // 曝光：以档为单位（±4EV 以内），在伽马空间直接缩放，符合监视器快捷调节的习惯
    rgb *= exp2(renderUniforms.grade.x);

    // 对比度：以 0.5 为中点
    rgb = (rgb - 0.5) * renderUniforms.grade.y + 0.5;

    // 饱和度：围绕亮度插值
    float luma = dot(rgb, renderUniforms.primaryWeights);
    // Metal: mix(float3(luma), rgb, u.grade.z)
    // fxc 不接受单参数向量构造 float3(luma)（error X3014），故写成等价的显式三分量。
    rgb = lerp(float3(luma, luma, luma), rgb, renderUniforms.grade.z);

    rgb = saturate(rgb);

    // 伽马（1.0 为中性）
    float g = max(renderUniforms.grade.w, 0.01);
    // Metal: pow(rgb, float3(1.0 / g))，同上理由展开成三分量，数值完全一致
    rgb = pow(rgb, float3(1.0 / g, 1.0 / g, 1.0 / g));

    return rgb;
}

// MARK: - 输入转换

// Metal: fragment float4 fsVideoBiPlanar(VSVertexOut in [[stage_in]],
//                                        texture2d<float> lumaTexture [[texture(VSTextureIndexSource)]],
//                                        texture2d<float> chromaTexture [[texture(VSTextureIndexChroma)]],
//                                        constant VSRenderUniforms &u [[buffer(VSBufferIndexRenderUniforms)]])
float4 PSVideoBiPlanar(VSVertexOut input) : SV_Target
{
    // Metal: constexpr sampler s(filter::linear, address::clamp_to_edge); → 文件顶部 s0 的 LinearClampSampler
    float y = lumaTexture.Sample(LinearClampSampler, input.uv).r;
    float2 cbcr = chromaTexture.Sample(LinearClampSampler, input.uv).rg;

    // 量化范围还原（视频范围 16-235 / 16-240 或全范围）
    float3 ycbcr = float3(y, cbcr) * u.ycbcrScale + u.ycbcrBias;
    float3 rgb = mul(u.yuvToRGB, ycbcr);   // Metal: u.yuvToRGB * ycbcr

    return float4(saturate(rgb), 1.0);
}

// Metal: fragment float4 fsVideoBGRA(VSVertexOut in [[stage_in]],
//                                    texture2d<float> sourceTexture [[texture(VSTextureIndexSource)]],
//                                    constant VSRenderUniforms &u [[buffer(VSBufferIndexRenderUniforms)]])
float4 PSVideoBGRA(VSVertexOut input) : SV_Target
{
    // Metal: constexpr sampler s(filter::linear, address::clamp_to_edge);
    // Metal: (void)u;（HLSL 里不能把结构体转成 void，未使用的 cbuffer 会被编译器丢弃）
    return float4(saturate(sourceTexture.Sample(LinearClampSampler, input.uv).rgb), 1.0);
}

// MARK: - LUT

// Metal: inline float3 vsSampleLUT3D(float3 c, texture3d<float> lut, float size)
static inline float3 vsSampleLUT3D(float3 c, Texture3D<float4> lut, float size)
{
    // Metal: constexpr sampler lutSampler(filter::linear, address::clamp_to_edge);
    float scale = (size - 1.0) / size;
    float offset = 1.0 / (2.0 * size);
    return lut.Sample(LinearClampSampler, saturate(c) * scale + offset).rgb;
}

// Metal: inline float3 vsSampleLUT1D(float3 c, texture2d<float> lut, float size)
static inline float3 vsSampleLUT1D(float3 c, Texture2D<float4> lut, float size)
{
    // Metal: constexpr sampler lutSampler(filter::linear, address::clamp_to_edge);
    float scale = (size - 1.0) / size;
    float offset = 1.0 / (2.0 * size);
    float r = lut.Sample(LinearClampSampler, float2(saturate(c.r) * scale + offset, 0.5)).r;
    float g = lut.Sample(LinearClampSampler, float2(saturate(c.g) * scale + offset, 0.5)).r;
    float b = lut.Sample(LinearClampSampler, float2(saturate(c.b) * scale + offset, 0.5)).r;
    return float3(r, g, b);
}

// Metal: fragment float4 fsApplyLUTAndGrade(VSVertexOut in [[stage_in]],
//                                           texture2d<float> sourceTexture [[texture(VSTextureIndexSource)]],
//                                           texture3d<float> lut3D [[texture(VSTextureIndexLUT3D)]],
//                                           texture2d<float> lut1D [[texture(VSTextureIndexLUT1D)]],
//                                           constant VSRenderUniforms &u [[buffer(VSBufferIndexRenderUniforms)]])
float4 PSApplyLUTAndGrade(VSVertexOut input) : SV_Target
{
    // Metal: constexpr sampler s(filter::linear, address::clamp_to_edge);
    float3 c = sourceTexture.Sample(LinearClampSampler, input.uv).rgb;

    if (u.lutParams.w > 0.5) {
        float invRange = 1.0 / max(u.lutDomain.y - u.lutDomain.x, 1e-5);
        float3 d = saturate((c - u.lutDomain.x) * invRange);

        if (u.lutParams.z > 1.5) {
            d = vsSampleLUT1D(d, lut1D, u.lutParams.z);
        }
        if (u.lutParams.y > 1.5) {
            d = vsSampleLUT3D(d, lut3D, u.lutParams.y);
        }
        c = lerp(c, d, saturate(u.lutParams.x));   // Metal: mix(c, d, saturate(u.lutParams.x))
    }

    return float4(vsApplyGrade(c, u), 1.0);
}

// MARK: - 显示

// Metal: fragment float4 fsDisplay(VSVertexOut in [[stage_in]],
//                                  texture2d<float> sourceTexture [[texture(VSTextureIndexSource)]],
//                                  constant VSRenderUniforms &u [[buffer(VSBufferIndexRenderUniforms)]])
float4 PSDisplay(VSVertexOut input) : SV_Target
{
    // Metal: constexpr sampler s(filter::linear, address::clamp_to_edge);
    float4 c = sourceTexture.Sample(LinearClampSampler, input.uv);

    int mode = int(u.flags.x + 0.5);
    if (mode == VS_DISPLAY_MODE_LUMA) {
        float l = dot(c.rgb, u.primaryWeights);
        c = float4(l, l, l, 1.0);
    } else if (mode == VS_DISPLAY_MODE_RED) {
        c = float4(c.r, c.r, c.r, 1.0);
    } else if (mode == VS_DISPLAY_MODE_GREEN) {
        c = float4(c.g, c.g, c.g, 1.0);
    } else if (mode == VS_DISPLAY_MODE_BLUE) {
        c = float4(c.b, c.b, c.b, 1.0);
    }

    // 斑马纹：只在这里（显示通道）叠加，不进示波器/幅度读数的统计链路。
    // 用屏幕坐标（[[position]] 是 drawable 像素坐标）画 45° 斜纹，跟画面内容无关，所以缩放到任何布局都一样粗。
    if (u.zebra.y > 0.5 || u.zebra.w > 0.5) {
        float luma = dot(c.rgb, u.primaryWeights);
        float period = 10.0;
        float stripe = step(0.5, frac((input.position.x + input.position.y) / period));   // Metal: fract

        bool overWhite = (u.zebra.y > 0.5) && (luma >= u.zebra.x);
        bool underBlack = (u.zebra.w > 0.5) && (luma <= u.zebra.z);

        if (overWhite) {
            // 超白：斜纹用醒目黄色
            c = float4(lerp(c.rgb, float3(1.0, 0.92, 0.1), stripe * 0.85), 1.0);   // Metal: mix
        } else if (underBlack) {
            // 黑切割：斜纹用蓝色区分
            c = float4(lerp(c.rgb, float3(0.2, 0.55, 1.0), stripe * 0.85), 1.0);   // Metal: mix
        }
    }

    return float4(c.rgb, 1.0);
}

// Metal: fragment float4 fsSolidColor(VSVertexOut in [[stage_in]],
//                                     constant vector_float4 &color [[buffer(VSBufferIndexRenderUniforms)]])
float4 PSSolidColor(VSVertexOut input) : SV_Target
{
    // Metal: (void)in;
    return color;
}

// Metal: fragment float4 fsScopeTrace(VSVertexOut in [[stage_in]],
//                                     texture2d<float> scopeTexture [[texture(VSTextureIndexScope)]],
//                                     constant VSScopeUniforms &u [[buffer(VSBufferIndexScopeUniforms)]])
float4 PSScopeTrace(VSVertexOut input) : SV_Target
{
    // Metal: constexpr sampler s(filter::linear, address::clamp_to_edge);
    // 把 b2 的全局 uScope 取成局部别名 u，使下面的代码与 Metal 原文逐字一致
    // （HLSL 的常量缓冲是全局对象，同一文件里 b1 的 VSRenderUniforms 已占用全局名 u）。
    VSScopeUniforms u = uScope;

    float4 c = scopeTexture.Sample(LinearClampSampler, input.uv);
    float intensity = u.params.y;
    // params.w：叠加倍率。实时轨迹传 1.0；冻结参考层传真不透明度（0~1），
    // 于是参考层就是一层淡淡的「幽灵」，不会把实时轨迹盖住。
    float ghost = max(u.params.w, 0.0);
    float energy = max(max(c.r, c.g), c.b);

    // 预乘颜色，配合加法混合（src = one, dst = one）得到经典示波器辉光效果
    return float4(c.rgb * intensity * u.color.rgb * ghost, energy * intensity * ghost);
}
