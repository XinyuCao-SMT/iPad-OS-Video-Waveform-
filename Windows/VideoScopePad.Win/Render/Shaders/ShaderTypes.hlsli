//
//  ShaderTypes.hlsli
//  VideoScopePad
//
//  本文件是 iOS 版 Shaders/ShaderTypes.h 的 HLSL 版本，逐行对应：
//  常量宏、buffer/texture 索引宏、uniform 结构体都保持**同名、同值、同顺序**。
//
//  与原版仅有的、纯语言层面的差异（不涉及任何数值或内存布局语义）：
//    · 原版 `#include <simd/simd.h>`：HLSL 里 vector_float4 / matrix_float3x3 是内置类型，
//      没有对应头文件，故这一行删除。
//    · VSMatrix3x3：原版 Metal 侧 typedef 成 metal::float3x3（列主序、每列 16 字节，共 48 字节）。
//      HLSL 侧 typedef 成**显式 column_major** 的 float3x3 —— 同样每列占满一个 16 字节
//      常量寄存器、共 48 字节，两边字节级布局完全一致。显式写 column_major 是为了不受
//      编译器默认矩阵打包顺序的影响（默认值在文档里换过版本，写死更安全）。
//    · vector_float4 / vector_float3 → HLSL 的 float4 / float3（float3 同样是 16 字节对齐、
//      在常量缓冲区里同样占满 16 字节，布局一致）。
//    · vector_float3 在 HLSL 里写在结构体中时不做隐式填充，填充由 cbuffer 打包规则完成，
//      与 C 版 simd 结构体的对齐规则结果一致（见下方 VSRenderUniforms 的偏移注释）。
//

#ifndef ShaderTypes_h
#define ShaderTypes_h

// 3x3 颜色矩阵：Metal 侧用 metal::float3x3，C/Swift 侧用 matrix_float3x3。
// 两者都是列主序、每列 16 字节（共 48 字节），内存布局完全一致。
//
// HLSL 侧：column_major float3x3 —— 列主序、每列 16 字节（共 48 字节），与上面完全一致。
// 注意：这里显式写出 column_major，等价于原版「必须写全限定名 metal::float3x3」的谨慎做法。
typedef column_major float3x3 VSMatrix3x3;

// MARK: - 缓冲区绑定索引

#define VSBufferIndexQuadUniforms    0
#define VSBufferIndexRenderUniforms  1
#define VSBufferIndexScopeUniforms   2
#define VSBufferIndexHistogram       3

// MARK: - 纹理绑定索引

#define VSTextureIndexSource   0
#define VSTextureIndexLUT3D    1
#define VSTextureIndexLUT1D    2
#define VSTextureIndexChroma   1
#define VSTextureIndexScope    0

// MARK: - 示波器直方图布局
//
// 单个 MTLBuffer 内分两块：
//   [0, VS_WAVEFORM_COUNT)                       波形数据：plane(4) x bin(256) x column(512)
//   [VS_WAVEFORM_COUNT, VS_HISTOGRAM_UINT_COUNT) 三种 256x256 二维直方图（同一套纹理布局）：
//       section 0 = 矢量示波器（Cb / Cr 平面）
//       section 1 = 钻石图（RGB 色域菱形图）
//       section 2 = 马蹄图（CIE 1931 色度图的 xy 平面）
//
//  plane: 0 = R, 1 = G, 2 = B, 3 = Y(亮度)
//

#define VS_WAVEFORM_COLUMNS 512
#define VS_WAVEFORM_BINS    256
#define VS_WAVEFORM_PLANES  4
#define VS_VECTORSCOPE_SIZE 256

#define VS_WAVEFORM_COUNT    (VS_WAVEFORM_COLUMNS * VS_WAVEFORM_BINS * VS_WAVEFORM_PLANES)
#define VS_VECTORSCOPE_COUNT (VS_VECTORSCOPE_SIZE * VS_VECTORSCOPE_SIZE)

// 二维直方图的分段（矢量 / 钻石 / 马蹄）
#define VS_GAMUT_SECTION_VECTORSCOPE 0
#define VS_GAMUT_SECTION_DIAMOND     1
#define VS_GAMUT_SECTION_CIE         2
#define VS_GAMUT_SECTION_COUNT       3

#define VS_GAMUT_COUNT       (VS_GAMUT_SECTION_COUNT * VS_VECTORSCOPE_COUNT)
#define VS_HISTOGRAM_UINT_COUNT (VS_WAVEFORM_COUNT + VS_GAMUT_COUNT)
#define VS_HISTOGRAM_BYTE_SIZE  (VS_HISTOGRAM_UINT_COUNT * 4)

#define VS_WAVEFORM_BYTE_OFFSET   0
#define VS_VECTORSCOPE_BYTE_OFFSET (VS_WAVEFORM_COUNT * 4)

// MARK: - 马蹄图（CIE 1931）的坐标映射
//
// xy 平面的可见色域约在 x ∈ [0, 0.75]、y ∈ [0, 0.85]；
// 这里取一个**正方形**窗口（x / y 用同一个比例尺，图像不会被拉歪），
// 原点各留一点余量：x,y ∈ [-ORIGIN, -ORIGIN + SPAN]。
// Metal 与 SwiftUI 刻度层都用这几个常量，保证轨迹与刻度严格对齐。

#define VS_CIE_ORIGIN_X 0.02
#define VS_CIE_ORIGIN_Y 0.02
#define VS_CIE_SPAN     0.89

// MARK: - 全局测量直方图（供 CPU 读取「数值读数」）
//
// 与上面的示波器直方图分开，单独一小块缓冲区，低频回读：
//   [0, 1024)                       4 个通道各 256 bin 的全局直方图（0=R 1=G 2=B 3=Y）
//   [VS_MEASURE_RADIAL_INDEX, ...)  64 bin 的色度半径直方图（0 = 灰色，63 = 100% 饱和度）
//
// 有了全局直方图，峰值白 / 黑位 / 平均值 / 超白超黑占比 / 各通道峰值 / 色度峰值
// 都能在 CPU 上精确算出来，GPU 侧不需要做 min/max 原子操作。

#define VS_MEASURE_PLANES      4
#define VS_MEASURE_BINS        256
#define VS_MEASURE_RADIAL_BINS 64
#define VS_MEASURE_CHANNEL_COUNT (VS_MEASURE_PLANES * VS_MEASURE_BINS)
#define VS_MEASURE_RADIAL_INDEX  (VS_MEASURE_CHANNEL_COUNT)
#define VS_MEASURE_UINT_COUNT    (VS_MEASURE_CHANNEL_COUNT + VS_MEASURE_RADIAL_BINS)
#define VS_MEASURE_BYTE_SIZE     (VS_MEASURE_UINT_COUNT * 4)

// MARK: - 显示模式

#define VS_DISPLAY_MODE_COLOR 0
#define VS_DISPLAY_MODE_LUMA  1
#define VS_DISPLAY_MODE_RED   2
#define VS_DISPLAY_MODE_GREEN 3
#define VS_DISPLAY_MODE_BLUE  4

// MARK: - 波形模式

#define VS_WAVEFORM_MODE_LUMA    0
#define VS_WAVEFORM_MODE_OVERLAY 2

// MARK: - 结构体
//
// 注释里给出的是**常量缓冲区偏移**（与 iOS 版 C/simd 结构体的布局逐字节一致）。
// cbuffer 打包规则：float4 占 16 字节；float3 占 12 字节、但下一个成员必须从 16 字节边界开始；
// column_major float3x3 每列占满 16 字节，共 48 字节。

// 说明：原版 C/Swift 侧写的是 `typedef struct X { ... } X;`，HLSL 里 struct 本身就
// 声明了类型名，再加 typedef 会报 error X3003: redefinition，所以这里只写 `struct X { ... };`。
// 结构体名、成员名、成员顺序、类型与注释都保持原样。

/// 2D 四边形绘制的顶点参数。rect 使用「左上角为原点、y 轴向下」的单位空间，
/// 与 SwiftUI 的坐标系保持一致，避免两套坐标来回换算。
struct VSQuadUniforms {
    float4 rect;  // +0   x, y, width, height (单位空间)
    float4 uv;    // +16  x, y, scaleX, scaleY
    float4 misc;  // +32  x: 画面顺时针旋转角度（0 / 90 / 180 / 270）；其余保留
};                  // 共 48 字节

struct VSRenderUniforms {
    VSMatrix3x3     yuvToRGB;      // +0   (Y, Cb, Cr) -> R'G'B'（列主序：列 0 在 +0，列 1 在 +16，列 2 在 +32）
    float3          ycbcrScale;    // +48  量化范围展开
    float3          ycbcrBias;     // +64
    float3          primaryWeights; // +80 亮度权重 (R, G, B)
    float4          grade;          // +96  x: 曝光(档) y: 对比度 z: 饱和度 w: 伽马
    float4          lutParams;      // +112 x: 强度 y: 3D尺寸 z: 1D尺寸 w: 是否启用(1/0)
    float4          lutDomain;      // +128 x: domainMin y: domainMax
    float4          flags;          // +144 x: 显示模式 y: 源是否为双平面(yuv)
    // 斑马纹（只作用于显示通道，绝不影响示波器统计）
    // x: 高光阈值(0-1 码值) y: 高光斑马开关 z: 黑切割阈值(0-1) w: 黑斑马开关
    float4          zebra;          // +160
};                  // 共 176 字节

struct VSScopeUniforms {
    float4 params;  // +0  x: 矢量图放大倍率 y: 轨迹亮度 z: 采样步长 w: 面板不透明度
    float4 color;   // +16 rgb: 轨迹颜色
    float4 refs;    // +32 x: 波形强度参考 y: 矢量图强度参考 z: 要读的二维直方图分段（0 矢量/1 钻石/2 马蹄）
    float4 flags;   // +48 x: 波形模式 y: 需要累计的二维直方图位掩码（1 矢量 2 钻石 4 马蹄） z: 是否需要波形数据
};                  // 共 64 字节

#endif /* ShaderTypes_h */
