//
//  DisplayShaders.metal
//  VideoScopePad
//
//  监视器显示链路：
//    1) YUV / BGRA  →  R'G'B'         (fsVideoBiPlanar / fsVideoBGRA)
//    2) 应用 1D+3D LUT 与调色          (fsApplyLUTAndGrade)
//    3) 显示模式（彩色/亮度/R/G/B）与合成 (fsDisplay / fsSolidColor / fsScopeTrace)
//

#include <metal_stdlib>
#include "ShaderTypes.h"

using namespace metal;

struct VSVertexOut {
    float4 position [[position]];
    float2 uv;
};

// MARK: - 顶点着色器
//
// rect 使用左上角为原点的单位空间（与 SwiftUI 一致），此处统一转换为 NDC。

vertex VSVertexOut vsQuadVertex(uint vid [[vertex_id]],
                                constant VSQuadUniforms &quad [[buffer(VSBufferIndexQuadUniforms)]])
{
    float2 corner = float2(float(vid & 1), float((vid >> 1) & 1));
    float2 unit = quad.rect.xy + corner * quad.rect.zw;

    VSVertexOut out;
    out.position = float4(unit.x * 2.0 - 1.0, 1.0 - unit.y * 2.0, 0.0, 1.0);

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
    out.uv = uv;
    return out;
}

// MARK: - 调色

inline float3 vsApplyGrade(float3 c, constant VSRenderUniforms &u)
{
    float3 rgb = c;

    // 曝光：以档为单位（±4EV 以内），在伽马空间直接缩放，符合监视器快捷调节的习惯
    rgb *= exp2(u.grade.x);

    // 对比度：以 0.5 为中点
    rgb = (rgb - 0.5) * u.grade.y + 0.5;

    // 饱和度：围绕亮度插值
    float luma = dot(rgb, u.primaryWeights);
    rgb = mix(float3(luma), rgb, u.grade.z);

    rgb = saturate(rgb);

    // 伽马（1.0 为中性）
    float g = max(u.grade.w, 0.01);
    rgb = pow(rgb, float3(1.0 / g));

    return rgb;
}

// MARK: - 输入转换

fragment float4 fsVideoBiPlanar(VSVertexOut in [[stage_in]],
                                texture2d<float> lumaTexture [[texture(VSTextureIndexSource)]],
                                texture2d<float> chromaTexture [[texture(VSTextureIndexChroma)]],
                                constant VSRenderUniforms &u [[buffer(VSBufferIndexRenderUniforms)]])
{
    constexpr sampler s(filter::linear, address::clamp_to_edge);

    float y = lumaTexture.sample(s, in.uv).r;
    float2 cbcr = chromaTexture.sample(s, in.uv).rg;

    // 量化范围还原（视频范围 16-235 / 16-240 或全范围）
    float3 ycbcr = float3(y, cbcr) * u.ycbcrScale + u.ycbcrBias;
    float3 rgb = u.yuvToRGB * ycbcr;

    return float4(saturate(rgb), 1.0);
}

fragment float4 fsVideoBGRA(VSVertexOut in [[stage_in]],
                            texture2d<float> sourceTexture [[texture(VSTextureIndexSource)]],
                            constant VSRenderUniforms &u [[buffer(VSBufferIndexRenderUniforms)]])
{
    constexpr sampler s(filter::linear, address::clamp_to_edge);
    (void)u;
    return float4(saturate(sourceTexture.sample(s, in.uv).rgb), 1.0);
}

// MARK: - LUT

inline float3 vsSampleLUT3D(float3 c, texture3d<float> lut, float size)
{
    constexpr sampler lutSampler(filter::linear, address::clamp_to_edge);
    float scale = (size - 1.0) / size;
    float offset = 1.0 / (2.0 * size);
    return lut.sample(lutSampler, saturate(c) * scale + offset).rgb;
}

inline float3 vsSampleLUT1D(float3 c, texture2d<float> lut, float size)
{
    constexpr sampler lutSampler(filter::linear, address::clamp_to_edge);
    float scale = (size - 1.0) / size;
    float offset = 1.0 / (2.0 * size);
    float r = lut.sample(lutSampler, float2(saturate(c.r) * scale + offset, 0.5)).r;
    float g = lut.sample(lutSampler, float2(saturate(c.g) * scale + offset, 0.5)).r;
    float b = lut.sample(lutSampler, float2(saturate(c.b) * scale + offset, 0.5)).r;
    return float3(r, g, b);
}

fragment float4 fsApplyLUTAndGrade(VSVertexOut in [[stage_in]],
                                   texture2d<float> sourceTexture [[texture(VSTextureIndexSource)]],
                                   texture3d<float> lut3D [[texture(VSTextureIndexLUT3D)]],
                                   texture2d<float> lut1D [[texture(VSTextureIndexLUT1D)]],
                                   constant VSRenderUniforms &u [[buffer(VSBufferIndexRenderUniforms)]])
{
    constexpr sampler s(filter::linear, address::clamp_to_edge);

    float3 c = sourceTexture.sample(s, in.uv).rgb;

    if (u.lutParams.w > 0.5) {
        float invRange = 1.0 / max(u.lutDomain.y - u.lutDomain.x, 1e-5);
        float3 d = saturate((c - u.lutDomain.x) * invRange);

        if (u.lutParams.z > 1.5) {
            d = vsSampleLUT1D(d, lut1D, u.lutParams.z);
        }
        if (u.lutParams.y > 1.5) {
            d = vsSampleLUT3D(d, lut3D, u.lutParams.y);
        }
        c = mix(c, d, saturate(u.lutParams.x));
    }

    return float4(vsApplyGrade(c, u), 1.0);
}

// MARK: - 显示

fragment float4 fsDisplay(VSVertexOut in [[stage_in]],
                          texture2d<float> sourceTexture [[texture(VSTextureIndexSource)]],
                          constant VSRenderUniforms &u [[buffer(VSBufferIndexRenderUniforms)]])
{
    constexpr sampler s(filter::linear, address::clamp_to_edge);
    float4 c = sourceTexture.sample(s, in.uv);

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
        float stripe = step(0.5, fract((in.position.x + in.position.y) / period));

        bool overWhite = (u.zebra.y > 0.5) && (luma >= u.zebra.x);
        bool underBlack = (u.zebra.w > 0.5) && (luma <= u.zebra.z);

        if (overWhite) {
            // 超白：斜纹用醒目黄色
            c = float4(mix(c.rgb, float3(1.0, 0.92, 0.1), stripe * 0.85), 1.0);
        } else if (underBlack) {
            // 黑切割：斜纹用蓝色区分
            c = float4(mix(c.rgb, float3(0.2, 0.55, 1.0), stripe * 0.85), 1.0);
        }
    }

    return float4(c.rgb, 1.0);
}

fragment float4 fsSolidColor(VSVertexOut in [[stage_in]],
                             constant vector_float4 &color [[buffer(VSBufferIndexRenderUniforms)]])
{
    (void)in;
    return color;
}

fragment float4 fsScopeTrace(VSVertexOut in [[stage_in]],
                             texture2d<float> scopeTexture [[texture(VSTextureIndexScope)]],
                             constant VSScopeUniforms &u [[buffer(VSBufferIndexScopeUniforms)]])
{
    constexpr sampler s(filter::linear, address::clamp_to_edge);

    float4 c = scopeTexture.sample(s, in.uv);
    float intensity = u.params.y;
    // params.w：叠加倍率。实时轨迹传 1.0；冻结参考层传真不透明度（0~1），
    // 于是参考层就是一层淡淡的「幽灵」，不会把实时轨迹盖住。
    float ghost = max(u.params.w, 0.0);
    float energy = max(max(c.r, c.g), c.b);

    // 预乘颜色，配合加法混合（src = one, dst = one）得到经典示波器辉光效果
    return float4(c.rgb * intensity * u.color.rgb * ghost, energy * intensity * ghost);
}
