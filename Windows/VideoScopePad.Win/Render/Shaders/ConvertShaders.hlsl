//
//  ConvertShaders.hlsl
//  VideoScopePad.Win
//
//  采集卡的原始 YUV → R'G'B' 纹理（RGBA8）。**为什么要有这一趟**：
//  示波器是 GPU 直方图（compute 读一张纹理累加），它只认「已经转好的 RGB 纹理」，
//  没法边采样边转。所以先把整帧转成 RGB，后面的显示与统计（VideoRenderer / ScopeEngine）
//  一行都不用改 —— 合成信号那条路走的就是同一张 RGB 纹理。
//
//  ⚠️ 与 CPU 版（Capture/YuvFrameConverter.cs）必须严格一致的两件事：
//
//   1) **系数只在这里选一次**：YScale/YOffset（量化范围）与 Rv/Gu/Gv/Bu（矩阵）
//      都由 cbuffer 传进来，值来自驱动声明的色彩元数据（见 VideoColorInfo）。
//      采样给的是 0…1 归一化值，这里先乘 255 还原成**8 位码值**再做运算 ——
//      这样着色器里的数字和 CPU 版、和 mfidl.h 里的公式是同一套，便于对拍。
//
//   2) **不要自己 round()/int()**：输出目标是 R8G8B8A8_UNORM，硬件写入时按
//      「就近取偶」自动量化（190.90 → 191），正好等于 CPU 版的「+0.5 再截断」。
//      如果这里再手动 int() 截断，75% 白会变成 190，与合成信号的 191 差 1 ——
//      采集与合成两条链就对不上了。
//
//  ⚠️ 越界保护是必须的：分组是 16×16，1080 高会多出 8 行线程，
//     不判 outputWidth/outputHeight 就会写穿 UAV。
//

cbuffer VSYuvConvertUniforms : register(b0)
{
    float4 yuvScaleBias;      // x = YScale（limited 1.164 / full 1.0），y = YOffset（limited 16 / full 0）
    float4 yuvCoefficients;   // x = Rv, y = Gu, z = Gv, w = Bu（BT.601 / BT.709 各一套，见 C# 侧）
    uint   outputWidth;
    uint   outputHeight;
    float2 yuvPadding;        // 只为满足 16 字节对齐
};

RWTexture2D<float4> convertedOutput : register(u0);

Texture2D<float4> yuy2Input : register(t0);     // YUY2：每个 texel = 2 个像素 (Y0, U, Y1, V)
Texture2D<float>  lumaInput : register(t0);     // NV12：Y 平面
Texture2D<float2> chromaInput : register(t1);   // NV12：UV 交织平面（U 在前、V 在后）

// 与 CPU 版 YuvFrameConverter.WritePixel 逐行对应
static inline float3 vsYuvToRgb(float yNormalized, float uNormalized, float vNormalized)
{
    // 归一化 → 8 位码值（与 CPU 版同一套数字）
    float y = yNormalized * 255.0;
    float u = uNormalized * 255.0;
    float v = vNormalized * 255.0;

    float luma = (y - yuvScaleBias.y) * yuvScaleBias.x;
    float du = u - 128.0;
    float dv = v - 128.0;

    float r = luma + yuvCoefficients.x * dv;
    float g = luma - yuvCoefficients.y * du - yuvCoefficients.z * dv;
    float b = luma + yuvCoefficients.w * du;

    // 夹到 0…1 后交给 UNORM 写入做量化（超白/超黑在这里被截断，见文件头说明）
    return saturate(float3(r, g, b) * (1.0 / 255.0));
}

// YUY2 → RGBA8：每 texel 两个像素，按 x 的奇偶取 Y0 或 Y1；U/V 两像素共用
[numthreads(16, 16, 1)]
void CSYuy2ToRgb(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= outputWidth || id.y >= outputHeight)
    {
        return;
    }

    float4 texel = yuy2Input.Load(int3((int)(id.x >> 1), (int)id.y, 0));
    float y = ((id.x & 1u) == 0u) ? texel.r : texel.b;

    convertedOutput[id.xy] = float4(vsYuvToRgb(y, texel.g, texel.a), 1.0);
}

// NV12 → RGBA8：Y 平面逐像素，UV 平面横竖都减半（4:2:0）
[numthreads(16, 16, 1)]
void CSNv12ToRgb(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= outputWidth || id.y >= outputHeight)
    {
        return;
    }

    float y = lumaInput.Load(int3((int)id.x, (int)id.y, 0));
    float2 uv = chromaInput.Load(int3((int)(id.x >> 1), (int)(id.y >> 1), 0));

    convertedOutput[id.xy] = float4(vsYuvToRgb(y, uv.x, uv.y), 1.0);
}
