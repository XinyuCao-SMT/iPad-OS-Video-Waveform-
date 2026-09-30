//
//  ShaderTypes.h
//  VideoScopePad
//
//  该头文件同时被 Swift/Clang 与 Metal 编译器包含，
//  因此这里只允许出现 C 结构体、宏与 simd 类型。
//

#ifndef ShaderTypes_h
#define ShaderTypes_h

#include <simd/simd.h>

// 3x3 颜色矩阵：Metal 侧用 metal::float3x3，C/Swift 侧用 matrix_float3x3。
// 两者都是列主序、每列 16 字节（共 48 字节），内存布局完全一致。
//
// 注意：这里必须写全限定名 metal::float3x3。.metal 文件里 `using namespace metal;`
// 一般写在 include 之后，所以头文件里裸写 float3x3 会报 unknown type name。
#ifdef __METAL_VERSION__
typedef metal::float3x3 VSMatrix3x3;
#else
typedef matrix_float3x3 VSMatrix3x3;
#endif

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

/// 2D 四边形绘制的顶点参数。rect 使用「左上角为原点、y 轴向下」的单位空间，
/// 与 SwiftUI 的坐标系保持一致，避免两套坐标来回换算。
typedef struct VSQuadUniforms {
    vector_float4 rect;  // x, y, width, height (单位空间)
    vector_float4 uv;    // x, y, scaleX, scaleY
    vector_float4 misc;  // x: 画面顺时针旋转角度（0 / 90 / 180 / 270）；其余保留
} VSQuadUniforms;

typedef struct VSRenderUniforms {
    VSMatrix3x3     yuvToRGB;      // (Y, Cb, Cr) -> R'G'B'
    vector_float3   ycbcrScale;    // 量化范围展开
    vector_float3   ycbcrBias;
    vector_float3   primaryWeights; // 亮度权重 (R, G, B)
    vector_float4   grade;          // x: 曝光(档) y: 对比度 z: 饱和度 w: 伽马
    vector_float4   lutParams;      // x: 强度 y: 3D尺寸 z: 1D尺寸 w: 是否启用(1/0)
    vector_float4   lutDomain;      // x: domainMin y: domainMax
    vector_float4   flags;          // x: 显示模式 y: 源是否为双平面(yuv)
    // 斑马纹（只作用于显示通道，绝不影响示波器统计）
    // x: 高光阈值(0-1 码值) y: 高光斑马开关 z: 黑切割阈值(0-1) w: 黑斑马开关
    vector_float4   zebra;
} VSRenderUniforms;

typedef struct VSScopeUniforms {
    vector_float4 params;  // x: 矢量图放大倍率 y: 轨迹亮度 z: 采样步长 w: 面板不透明度
    vector_float4 color;   // rgb: 轨迹颜色
    vector_float4 refs;    // x: 波形强度参考 y: 矢量图强度参考 z: 要读的二维直方图分段（0 矢量/1 钻石/2 马蹄）
    vector_float4 flags;   // x: 波形模式 y: 需要累计的二维直方图位掩码（1 矢量 2 钻石 4 马蹄） z: 是否需要波形数据
} VSScopeUniforms;

#endif /* ShaderTypes_h */
