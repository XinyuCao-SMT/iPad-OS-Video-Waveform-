//
//  ScopeEngine.swift
//  VideoScopePad
//
//  示波器统计引擎：
//    1) 用 blit 清零直方图缓冲区
//    2) 用 compute kernel 从中间纹理（LUT 前 / LUT 后）做原子累计
//    3) 用 compute kernel 把直方图归一化成可供显示的纹理
//

import CoreGraphics
import Foundation
import Metal

enum ScopeEngineError: Error, LocalizedError {
    case bufferAllocationFailed
    case textureAllocationFailed

    var errorDescription: String? {
        switch self {
        case .bufferAllocationFailed: return "无法分配示波器直方图缓冲区"
        case .textureAllocationFailed: return "无法分配示波器输出纹理"
        }
    }
}

struct ScopeRenderSettings {
    var enabled: Set<ScopePanelKind> = []
    var waveformMode: WaveformMode = .luma
    var quality: ScopeQuality = .half
    var vectorscopeGain: Double = 1.0
    var traceIntensity: Double = 1.0
    var panelOpacity: Double = 0.85
    var traceColor: SIMD3<Float> = SIMD3(0.35, 1.0, 0.55)
}

final class ScopeEngine {

    let histogramBuffer: MTLBuffer
    let vectorscopeTexture: MTLTexture
    let waveformTexture: MTLTexture
    let overlayTexture: MTLTexture
    let paradeTexture: MTLTexture

    private let context: MetalContext

    /// 测量直方图的 uint 个数（init 里算一次，回读时复用）
    private let measureUintCount: Int

    /// 全局测量直方图（双缓冲：GPU 写一块时 CPU 可以读另一块）
    private var measureBuffers: [MTLBuffer] = []
    private var measureIndex = 0

    init(context: MetalContext) throws {
        self.context = context

        // 注意：ShaderTypes.h 里的 VS_HISTOGRAM_BYTE_SIZE / VS_MEASURE_BYTE_SIZE 嵌套太深，
        // Swift 的 Clang 导入器读不进来（Xcode 报 "cannot find in scope"），
        // 所以这里用最内层的单值宏自己算一遍，数值与头文件完全一致。
        let uintSize = MemoryLayout<UInt32>.size
        self.measureUintCount = Int(VS_MEASURE_PLANES) * Int(VS_MEASURE_BINS) + Int(VS_MEASURE_RADIAL_BINS)

        let length = (Int(VS_WAVEFORM_COLUMNS) * Int(VS_WAVEFORM_BINS) * Int(VS_WAVEFORM_PLANES)
            + Int(VS_VECTORSCOPE_SIZE) * Int(VS_VECTORSCOPE_SIZE)) * uintSize
        guard let buffer = context.device.makeBuffer(length: length, options: .storageModePrivate) else {
            throw ScopeEngineError.bufferAllocationFailed
        }
        buffer.label = "示波器直方图"
        histogramBuffer = buffer

        // 测量用的缓冲区必须是 CPU 可读的 .shared，且只有 4KB 出头，回读开销可以忽略
        let measureLength = measureUintCount * uintSize
        var buffers: [MTLBuffer] = []
        for index in 0..<2 {
            guard let measure = context.device.makeBuffer(length: measureLength,
                                                          options: .storageModeShared) else {
                throw ScopeEngineError.bufferAllocationFailed
            }
            measure.label = "信号测量直方图 \(index)"
            buffers.append(measure)
        }
        measureBuffers = buffers

        let scopeSize = Int(VS_VECTORSCOPE_SIZE)
        let columns = Int(VS_WAVEFORM_COLUMNS)
        let bins = Int(VS_WAVEFORM_BINS)

        guard let vector = context.makeScopeTexture(width: scopeSize, height: scopeSize, label: "矢量示波器"),
              let waveform = context.makeScopeTexture(width: columns, height: bins, label: "亮度波形"),
              let overlay = context.makeScopeTexture(width: columns, height: bins, label: "RGB 叠加波形"),
              let parade = context.makeScopeTexture(width: columns * 3, height: bins, label: "RGB Parade") else {
            throw ScopeEngineError.textureAllocationFailed
        }

        vectorscopeTexture = vector
        waveformTexture = waveform
        overlayTexture = overlay
        paradeTexture = parade
    }

    /// 供指定面板显示的纹理
    func texture(for kind: ScopePanelKind, waveformMode: WaveformMode) -> MTLTexture {
        switch kind {
        case .vectorscope:
            return vectorscopeTexture
        case .waveform:
            return waveformMode == .luma ? waveformTexture : overlayTexture
        case .parade:
            return paradeTexture
        }
    }

    /// 纹理采样区域：矢量示波器的放大倍率通过 UV 缩放实现
    func uvRect(for kind: ScopePanelKind, gain: Double) -> CGRect {
        switch kind {
        case .vectorscope:
            let g = max(gain, 0.25)
            let scale = 1.0 / g
            let offset = 0.5 - 0.5 * scale
            return CGRect(x: offset, y: offset, width: scale, height: scale)
        default:
            return CGRect(x: 0, y: 0, width: 1, height: 1)
        }
    }

    /// 每帧调用一次：统计 + 归一化
    func encode(commandBuffer: MTLCommandBuffer,
                source: MTLTexture,
                settings: ScopeRenderSettings) {

        let width = source.width
        let height = source.height
        guard width > 1, height > 1 else { return }

        // 1) 清零
        if let blit = commandBuffer.makeBlitCommandEncoder() {
            blit.label = "清零示波器直方图"
            blit.fill(buffer: histogramBuffer, range: 0..<histogramBuffer.length, value: 0)
            blit.endEncoding()
        }

        guard !settings.enabled.isEmpty else { return }

        let stride = max(settings.quality.stride, 1)
        var uniforms = makeUniforms(settings: settings, width: width, height: height)

        // 2) 累计
        if let encoder = commandBuffer.makeComputeCommandEncoder() {
            encoder.label = "统计示波器直方图"
            encoder.setComputePipelineState(context.pipelines.accumulateHistogram)
            encoder.setTexture(source, index: Int(VSTextureIndexSource))
            encoder.setBuffer(histogramBuffer, offset: 0, index: 0)
            encoder.setBytes(&uniforms, length: MemoryLayout<VSScopeUniforms>.stride, index: 1)

            let gridWidth = (width + stride - 1) / stride
            let gridHeight = (height + stride - 1) / stride
            encoder.dispatchThreads(MTLSize(width: gridWidth, height: gridHeight, depth: 1),
                                    threadsPerThreadgroup: MTLSize(width: 16, height: 16, depth: 1))
            encoder.endEncoding()
        }

        // 3) 归一化输出
        if let encoder = commandBuffer.makeComputeCommandEncoder() {
            encoder.label = "生成示波器图像"
            encoder.setBuffer(histogramBuffer, offset: 0, index: 0)

            for kind in ScopePanelKind.allCases where settings.enabled.contains(kind) {
                switch kind {
                case .vectorscope:
                    encodeNormalize(encoder: encoder,
                                    pipeline: context.pipelines.normalizeVectorscope,
                                    texture: vectorscopeTexture,
                                    uniforms: &uniforms)

                case .waveform:
                    encodeNormalize(encoder: encoder,
                                    pipeline: settings.waveformMode == .luma
                                        ? context.pipelines.normalizeWaveform
                                        : context.pipelines.normalizeOverlay,
                                    texture: settings.waveformMode == .luma ? waveformTexture : overlayTexture,
                                    uniforms: &uniforms)

                case .parade:
                    encodeNormalize(encoder: encoder,
                                    pipeline: context.pipelines.normalizeParade,
                                    texture: paradeTexture,
                                    uniforms: &uniforms)
                }
            }

            encoder.endEncoding()
        }
    }

    // MARK: - 数值读数（信号幅度测量）

    /// 把当前帧累加进全局测量直方图，并在 GPU 完成后回读给 CPU。
    /// 调用频率由外部控制（例如每 6 帧一次），开销可以忽略。
    @discardableResult
    func encodeMeasurement(commandBuffer: MTLCommandBuffer,
                           source: MTLTexture,
                           stride: Int,
                           completion: @escaping ([UInt32]) -> Void) -> Bool {

        guard !measureBuffers.isEmpty, source.width > 1, source.height > 1 else { return false }

        measureIndex = (measureIndex + 1) % measureBuffers.count
        let buffer = measureBuffers[measureIndex]

        if let blit = commandBuffer.makeBlitCommandEncoder() {
            blit.label = "清零测量直方图"
            blit.fill(buffer: buffer, range: 0..<buffer.length, value: 0)
            blit.endEncoding()
        }

        var uniforms = VSScopeUniforms()
        uniforms.params = SIMD4<Float>(1, 1, Float(max(stride, 1)), 1)
        uniforms.color = SIMD4<Float>(1, 1, 1, 1)
        uniforms.refs = SIMD4<Float>(1, 1, 0, 0)
        uniforms.flags = SIMD4<Float>(0, 0, 0, 0)

        guard let encoder = commandBuffer.makeComputeCommandEncoder() else { return false }
        encoder.label = "统计信号幅度"
        encoder.setComputePipelineState(context.pipelines.accumulateMeasurement)
        encoder.setTexture(source, index: Int(VSTextureIndexSource))
        encoder.setBuffer(buffer, offset: 0, index: 0)
        encoder.setBytes(&uniforms, length: MemoryLayout<VSScopeUniforms>.stride, index: 1)

        let gridWidth = (source.width + stride - 1) / stride
        let gridHeight = (source.height + stride - 1) / stride
        encoder.dispatchThreads(MTLSize(width: gridWidth, height: gridHeight, depth: 1),
                                threadsPerThreadgroup: MTLSize(width: 16, height: 16, depth: 1))
        encoder.endEncoding()

        let count = measureUintCount
        commandBuffer.addCompletedHandler { _ in
            let pointer = buffer.contents().bindMemory(to: UInt32.self, capacity: count)
            let values = Array(UnsafeBufferPointer(start: pointer, count: count))
            completion(values)
        }
        return true
    }

    private func encodeNormalize(encoder: MTLComputeCommandEncoder,
                                 pipeline: MTLComputePipelineState,
                                 texture: MTLTexture,
                                 uniforms: inout VSScopeUniforms) {
        encoder.setComputePipelineState(pipeline)
        encoder.setTexture(texture, index: Int(VSTextureIndexScope))
        encoder.setBytes(&uniforms, length: MemoryLayout<VSScopeUniforms>.stride, index: 1)

        encoder.dispatchThreads(MTLSize(width: texture.width, height: texture.height, depth: 1),
                                threadsPerThreadgroup: MTLSize(width: 16, height: 16, depth: 1))
    }

    private func makeUniforms(settings: ScopeRenderSettings, width: Int, height: Int) -> VSScopeUniforms {
        var uniforms = VSScopeUniforms()

        let stride = Double(max(settings.quality.stride, 1))
        let samplesPerColumn = Double(height) / stride
        let totalSamples = Double(width) * Double(height) / (stride * stride)

        uniforms.params = SIMD4<Float>(Float(max(settings.vectorscopeGain, 0.25)),
                                       Float(max(settings.traceIntensity, 0.05)),
                                       Float(stride),
                                       Float(min(max(settings.panelOpacity, 0.0), 1.0)))
        uniforms.color = SIMD4<Float>(settings.traceColor.x, settings.traceColor.y, settings.traceColor.z, 1)
        uniforms.refs = SIMD4<Float>(Float(max(8.0, samplesPerColumn * 0.08)),
                                     Float(max(6.0, totalSamples / 20000.0)),
                                     0, 0)
        uniforms.flags = SIMD4<Float>(Float(settings.waveformMode.shaderValue), 0, 0, 0)
        return uniforms
    }
}
