//
//  VideoRenderer.swift
//  VideoScopePad
//
//  每帧的 Metal 渲染流程：
//    1) 采集帧 -> texturePreLUT        （YUV/BGRA 转 R'G'B'，零拷贝纹理）
//    2) texturePreLUT -> textureDisplay（应用 1D+3D LUT 与调色）
//    3) 从 LUT 前或 LUT 后的纹理统计示波器
//    4) 合成到 drawable：画面 + 示波器面板背景 + 示波器轨迹
//
//  布局由 SwiftUI 侧计算后传入（单位空间矩形），保证刻度线与轨迹严格对齐。
//

import AVFoundation
import CoreMedia
import CoreVideo
import Foundation
import Metal
import MetalKit
import simd

struct FrameSourceInfo {
    var colorMatrix: ColorMatrixKind = .bt709
    var isVideoRange: Bool = true
}

/// 冻结参考层的颜色：琥珀色。跟默认的绿色实时轨迹一眼就能分开，
/// 所以「哪个是现在的信号、哪个是冻结那一刻」不用猜。
let vsReferenceTint = SIMD3<Float>(1.0, 0.58, 0.12)

final class VideoRenderer: NSObject, MTKViewDelegate {

    private let context: MetalContext
    private let scopeEngine: ScopeEngine
    private let settings: AppSettings
    private let lutSlot: LUTSlot
    private let placeholderLUT: LUTTextures

    /// 色彩参数由协调器提供（主线程读取）
    var sourceInfoProvider: (() -> FrameSourceInfo)?

    /// 数值读数回调（在 Metal 完成回调线程上，调用方负责切主线程）
    var onMeasurement: ((SignalMeasurement) -> Void)?

    /// 画面签名分析（声画延时测量：找「黑场 → 彩条」的跳变帧）
    var signatureAnalyzer: FrameSignatureAnalyzer?

    /// 每多少帧做一次幅度测量（60fps 下 6 帧 ≈ 10Hz，足够读数又不占性能）
    private let measurementInterval = 6
    /// 测量采样步长（4 表示每 16 个像素取 1 个）
    private let measurementStride = 4
    private var frameIndex = 0

    // 跨线程：待渲染的最新一帧
    private let frameLock = NSLock()
    private var pendingPixelBuffer: CVPixelBuffer?
    /// 待渲染帧的时刻（主机时钟秒），供画面签名标注时间
    private var pendingFrameHostTime: TimeInterval?

    // 仅主线程
    private var texturePreLUT: MTLTexture?
    private var textureDisplay: MTLTexture?
    private var textureSize = CGSize.zero
    private var hasContent = false
    private var lastSourceInfo = FrameSourceInfo()

    /// 由 SwiftUI 计算好的单位空间布局
    var layout = ScopeLayoutResult()

    init(context: MetalContext,
         scopeEngine: ScopeEngine,
         settings: AppSettings,
         lutSlot: LUTSlot,
         placeholderLUT: LUTTextures) {
        self.context = context
        self.scopeEngine = scopeEngine
        self.settings = settings
        self.lutSlot = lutSlot
        self.placeholderLUT = placeholderLUT
        super.init()
    }

    var videoPixelSize: CGSize { textureSize }

    // MARK: - 帧投递

    func submit(pixelBuffer: CVPixelBuffer, presentationTime: CMTime = .invalid) {
        frameLock.lock()
        pendingPixelBuffer = pixelBuffer
        // 帧时刻：AVFoundation 给的 PTS 在**主机时钟**上，和音频的 AVAudioTime.hostTime 同一时基，
        // 所以声画延时可以直接相减。拿不到有效 PTS 时退回当前时刻。
        if presentationTime.isValid, presentationTime.seconds.isFinite {
            pendingFrameHostTime = presentationTime.seconds
        } else {
            pendingFrameHostTime = AVAudioTime.seconds(forHostTime: mach_absolute_time())
        }
        frameLock.unlock()
    }

    private func takePendingBuffer() -> CVPixelBuffer? {
        frameLock.lock()
        defer { frameLock.unlock() }
        let buffer = pendingPixelBuffer
        pendingPixelBuffer = nil
        return buffer
    }

    func mtkView(_ view: MTKView, drawableSizeWillChange size: CGSize) {
        // 下一帧会按新的 drawable 尺寸重新合成
    }

    // MARK: - 参考层（冻结）

    /// 参考层请求。渲染线程每帧只处理一次，抓取必须与「本帧的 encode」排在同一个 command buffer 里。
    enum ReferenceRequest {
        case none
        case capture
        case clear
    }

    private var referenceRequest: ReferenceRequest = .none

    /// 抓一份参考层（冻结）：图表继续实时跑，冻结那一刻的图形叠在上面
    func requestReferenceCapture() {
        referenceRequest = .capture
    }

    /// 丢掉参考层（解除冻结）
    func requestReferenceClear() {
        referenceRequest = .clear
    }

    // MARK: - 绘制

    func draw(in view: MTKView) {
        guard let drawable = view.currentDrawable,
              let renderPassDescriptor = view.currentRenderPassDescriptor,
              let commandBuffer = context.commandQueue.makeCommandBuffer() else {
            return
        }

        let drawableSize = view.drawableSize
        guard drawableSize.width > 1, drawableSize.height > 1 else {
            commandBuffer.commit()
            return
        }

        let sourceInfo = sourceInfoProvider?() ?? lastSourceInfo
        lastSourceInfo = sourceInfo

        var retainedFrameTextures: [CVMetalTexture] = []

        // 冻结语义（v1.11.0 起）：
        //   · 「冻结」= 抓一份**参考层**。实时图表照常刷新，冻结那一刻的图形以琥珀色叠在实时轨迹上，
        //     校色时可以直接前后对照；数值读数同样会存一份参考值并排显示。
        //   · 勾上「冻结时连画面一起冻住」才会丢弃新帧（画面也停住）。
        //   · 音频相关显示（音柱 / 声相 / 频谱 / 声画延时）永不冻结。
        let freezePicture = settings.freeze && settings.freezePictureToo

        if freezePicture {
            _ = takePendingBuffer()
        } else if let pixelBuffer = takePendingBuffer() {
            retainedFrameTextures = encodeFrameConversion(pixelBuffer,
                                                          sourceInfo: sourceInfo,
                                                          commandBuffer: commandBuffer)
        }

        let scopeSettings = makeScopeSettings()

        guard let preLUT = texturePreLUT, hasContent else {
            encodeOutput(renderPassDescriptor: renderPassDescriptor,
                         commandBuffer: commandBuffer,
                         displaySource: nil,
                         scopeSettings: scopeSettings,
                         drawableSize: drawableSize)
            finish(commandBuffer: commandBuffer,
                   drawable: drawable,
                   retainedFrameTextures: retainedFrameTextures)
            return
        }

        let lut = lutSlot.current()
        let lutActive = settings.lutEnabled && (lut?.hasContent ?? false)
        let needsLookPass = lutActive || !settings.gradeIsNeutral

        if needsLookPass {
            encodeLookPass(source: preLUT,
                           lut: lut ?? placeholderLUT,
                           lutActive: lutActive,
                           sourceInfo: sourceInfo,
                           commandBuffer: commandBuffer)
        }

        let displaySource = needsLookPass ? (textureDisplay ?? preLUT) : preLUT

        // 示波器统计源
        let scopeInput = settings.scopeSource == .postLUT ? displaySource : preLUT
        // 图表永远是实时的；「冻结」只是额外抓一份参考层（见 ScopeEngine.captureReference）
        if !scopeSettings.enabled.isEmpty {
            scopeEngine.encode(commandBuffer: commandBuffer, source: scopeInput, settings: scopeSettings)

            // 参考层抓取：必须紧跟在本帧的 encode 之后，才能拷到刚算完的这一帧
            if referenceRequest == .capture {
                scopeEngine.captureReference(commandBuffer: commandBuffer)
                referenceRequest = .none
            }
        } else if referenceRequest == .capture {
            // 一格示波器都没显示：没有图可抓，别把这个请求一直留着以后误抓
            referenceRequest = .none
        }
        if referenceRequest == .clear {
            scopeEngine.clearReference()
            referenceRequest = .none
        }

        // 信号幅度数值读数（低频回读，不阻塞渲染）
        frameIndex &+= 1
        if settings.showMeasurement, frameIndex % measurementInterval == 0 {
            let isVideoRange = sourceInfo.isVideoRange
            scopeEngine.encodeMeasurement(commandBuffer: commandBuffer,
                                          source: scopeInput,
                                          stride: measurementStride) { [weak self] counts in
                guard let self,
                      let measurement = SignalMeasurementBuilder.make(counts: counts,
                                                                      isVideoRange: isVideoRange) else {
                    return
                }
                self.onMeasurement?(measurement)
            }
        }

        // 画面签名（声画延时测量）：每帧一次，只回读 16 KB
        if let signatureAnalyzer, let frameHostTime = pendingFrameHostTime {
            signatureAnalyzer.encode(texture: scopeInput,
                                     hostTime: frameHostTime,
                                     commandBuffer: commandBuffer)
            pendingFrameHostTime = nil
        }

        encodeOutput(renderPassDescriptor: renderPassDescriptor,
                     commandBuffer: commandBuffer,
                     displaySource: displaySource,
                     scopeSettings: scopeSettings,
                     drawableSize: drawableSize,
                     hasScopes: !scopeSettings.enabled.isEmpty)

        finish(commandBuffer: commandBuffer,
               drawable: drawable,
               retainedFrameTextures: retainedFrameTextures)
    }

    private func finish(commandBuffer: MTLCommandBuffer,
                        drawable: CAMetalDrawable,
                        retainedFrameTextures: [CVMetalTexture]) {
        if !retainedFrameTextures.isEmpty {
            let retained = retainedFrameTextures
            commandBuffer.addCompletedHandler { _ in
                // 保持零拷贝纹理存活到 GPU 执行完毕
                _ = retained.count
            }
        }
        commandBuffer.present(drawable)
        commandBuffer.commit()
    }

    // MARK: - 1) 输入转换

    private func encodeFrameConversion(_ pixelBuffer: CVPixelBuffer,
                                       sourceInfo: FrameSourceInfo,
                                       commandBuffer: MTLCommandBuffer) -> [CVMetalTexture] {

        let width = CVPixelBufferGetWidth(pixelBuffer)
        let height = CVPixelBufferGetHeight(pixelBuffer)
        guard width > 1, height > 1 else { return [] }

        ensureIntermediateTextures(width: width, height: height)
        guard let destination = texturePreLUT else { return [] }

        let descriptor = MTLRenderPassDescriptor()
        descriptor.colorAttachments[0].texture = destination
        descriptor.colorAttachments[0].loadAction = .dontCare
        descriptor.colorAttachments[0].storeAction = .store

        guard let encoder = commandBuffer.makeRenderCommandEncoder(descriptor: descriptor) else { return [] }
        encoder.label = "输入转换"

        var retained: [CVMetalTexture] = []
        var uniforms = makeRenderUniforms(sourceInfo: sourceInfo,
                                         displayMode: .color,
                                         lut: nil,
                                         lutActive: false)

        let isPlanar = CVPixelBufferIsPlanar(pixelBuffer)
        let planeCount = CVPixelBufferGetPlaneCount(pixelBuffer)

        if isPlanar && planeCount >= 2 {
            let lumaWidth = CVPixelBufferGetWidthOfPlane(pixelBuffer, 0)
            let lumaHeight = CVPixelBufferGetHeightOfPlane(pixelBuffer, 0)
            let chromaWidth = CVPixelBufferGetWidthOfPlane(pixelBuffer, 1)
            let chromaHeight = CVPixelBufferGetHeightOfPlane(pixelBuffer, 1)

            guard let luma = makeTexture(from: pixelBuffer, plane: 0,
                                         pixelFormat: .r8Unorm,
                                         width: lumaWidth, height: lumaHeight),
                  let chroma = makeTexture(from: pixelBuffer, plane: 1,
                                           pixelFormat: .rg8Unorm,
                                           width: chromaWidth, height: chromaHeight) else {
                encoder.endEncoding()
                return []
            }

            retained.append(luma.reference)
            retained.append(chroma.reference)

            encoder.setRenderPipelineState(context.pipelines.videoBiPlanar)
            encoder.setFragmentTexture(luma.texture, index: Int(VSTextureIndexSource))
            encoder.setFragmentTexture(chroma.texture, index: Int(VSTextureIndexChroma))
        } else {
            guard let source = makeTexture(from: pixelBuffer, plane: 0,
                                           pixelFormat: .bgra8Unorm,
                                           width: width, height: height) else {
                encoder.endEncoding()
                return []
            }
            retained.append(source.reference)

            encoder.setRenderPipelineState(context.pipelines.videoBGRA)
            encoder.setFragmentTexture(source.texture, index: Int(VSTextureIndexSource))
        }

        uniforms.flags = SIMD4<Float>(Float(DisplayMode.color.shaderValue), isPlanar ? 1.0 : 0.0, 0, 0)
        encoder.setFragmentBytes(&uniforms, length: MemoryLayout<VSRenderUniforms>.stride,
                                 index: Int(VSBufferIndexRenderUniforms))
        encodeQuad(encoder: encoder, rect: fullRect, uv: fullRect)
        encoder.endEncoding()

        hasContent = true
        return retained
    }

    private func makeTexture(from pixelBuffer: CVPixelBuffer,
                             plane: Int,
                             pixelFormat: MTLPixelFormat,
                             width: Int,
                             height: Int) -> (texture: MTLTexture, reference: CVMetalTexture)? {
        var cvTexture: CVMetalTexture?
        let status = CVMetalTextureCacheCreateTextureFromImage(kCFAllocatorDefault,
                                                              context.textureCache,
                                                              pixelBuffer,
                                                              nil,
                                                              pixelFormat,
                                                              width,
                                                              height,
                                                              plane,
                                                              &cvTexture)
        guard status == kCVReturnSuccess,
              let reference = cvTexture,
              let texture = CVMetalTextureGetTexture(reference) else {
            return nil
        }
        return (texture, reference)
    }

    private func ensureIntermediateTextures(width: Int, height: Int) {
        if let existing = texturePreLUT,
           existing.width == width,
           existing.height == height,
           textureDisplay != nil {
            return
        }
        texturePreLUT = context.makeIntermediateTexture(width: width, height: height, label: "LUT 之前")
        textureDisplay = context.makeIntermediateTexture(width: width, height: height, label: "显示信号")
        textureSize = CGSize(width: width, height: height)
        hasContent = false
    }

    // MARK: - 2) LUT 与调色

    private func encodeLookPass(source: MTLTexture,
                                lut: LUTTextures,
                                lutActive: Bool,
                                sourceInfo: FrameSourceInfo,
                                commandBuffer: MTLCommandBuffer) {
        guard let destination = textureDisplay else { return }

        let descriptor = MTLRenderPassDescriptor()
        descriptor.colorAttachments[0].texture = destination
        descriptor.colorAttachments[0].loadAction = .dontCare
        descriptor.colorAttachments[0].storeAction = .store

        guard let encoder = commandBuffer.makeRenderCommandEncoder(descriptor: descriptor) else { return }
        encoder.label = "LUT 与调色"
        encoder.setRenderPipelineState(context.pipelines.applyLUTAndGrade)

        var uniforms = makeRenderUniforms(sourceInfo: sourceInfo,
                                         displayMode: .color,
                                         lut: lut,
                                         lutActive: lutActive)

        encoder.setFragmentTexture(source, index: Int(VSTextureIndexSource))
        encoder.setFragmentTexture(lut.texture3D, index: Int(VSTextureIndexLUT3D))
        encoder.setFragmentTexture(lut.texture1D, index: Int(VSTextureIndexLUT1D))
        encoder.setFragmentBytes(&uniforms, length: MemoryLayout<VSRenderUniforms>.stride,
                                 index: Int(VSBufferIndexRenderUniforms))
        encodeQuad(encoder: encoder, rect: fullRect, uv: fullRect)
        encoder.endEncoding()
    }

    // MARK: - 4) 合成输出

    private func encodeOutput(renderPassDescriptor: MTLRenderPassDescriptor,
                              commandBuffer: MTLCommandBuffer,
                              displaySource: MTLTexture?,
                              scopeSettings: ScopeRenderSettings,
                              drawableSize: CGSize,
                              hasScopes: Bool = false) {

        let attachment = renderPassDescriptor.colorAttachments[0]!
        attachment.loadAction = .clear
        attachment.clearColor = MTLClearColorMake(0.016, 0.018, 0.022, 1)

        guard let encoder = commandBuffer.makeRenderCommandEncoder(descriptor: renderPassDescriptor) else { return }
        encoder.label = "合成输出"

        let panes = layout.panes
        let panelAlpha = layout.isOverlay ? Float(scopeSettings.panelOpacity) : 1.0

        // 1) 画面格子（每个 picture 格子都画一次实时画面）
        if let displaySource {
            var uniforms = makeRenderUniforms(sourceInfo: lastSourceInfo,
                                             displayMode: settings.displayMode,
                                             lut: nil,
                                             lutActive: false)
            encoder.setRenderPipelineState(context.pipelines.display)
            encoder.setFragmentTexture(displaySource, index: Int(VSTextureIndexSource))
            encoder.setFragmentBytes(&uniforms, length: MemoryLayout<VSRenderUniforms>.stride,
                                     index: Int(VSBufferIndexRenderUniforms))

            for pane in panes where pane.content == .picture {
                guard let video = pane.video else { continue }
                // 画面永远不越出自己那一格（四分割下贴邻格会很难看）
                setScissor(encoder, unitRect: pane.panel, drawableSize: drawableSize)
                encodeQuad(encoder: encoder,
                           rect: video,
                           uv: pane.videoUV ?? fullRect,
                           rotationDegrees: pane.rotation)
                clearScissor(encoder, drawableSize: drawableSize)
            }
        }

        // 2) 示波器格子的底：整个格子铺一层暗色，刻度栏再压深一点，保证数字看得清
        if !panes.isEmpty {
            encoder.setRenderPipelineState(context.pipelines.solidColor)
            for pane in panes where pane.content.scopeKind != nil {
                setScissor(encoder, unitRect: pane.panel, drawableSize: drawableSize)

                var color = SIMD4<Float>(0.05, 0.052, 0.06, panelAlpha)
                encoder.setFragmentBytes(&color, length: MemoryLayout<SIMD4<Float>>.stride,
                                         index: Int(VSBufferIndexRenderUniforms))
                encodeQuad(encoder: encoder, rect: pane.panel, uv: fullRect)

                if let gutter = pane.gutter {
                    var gutterColor = SIMD4<Float>(0.10, 0.105, 0.12, panelAlpha)
                    encoder.setFragmentBytes(&gutterColor, length: MemoryLayout<SIMD4<Float>>.stride,
                                             index: Int(VSBufferIndexRenderUniforms))
                    encodeQuad(encoder: encoder, rect: gutter, uv: fullRect)
                }
                clearScissor(encoder, drawableSize: drawableSize)
            }
        }

        // 3) 示波器轨迹
        if hasScopes && displaySource != nil {
            encoder.setRenderPipelineState(context.pipelines.scopeTrace)
            // 参考层：冻结着且有内容时才叠（琥珀色幽灵，叠在实时轨迹上面）
            let showReference = settings.freeze
                && settings.freezeReference
                && scopeEngine.hasReference
                && settings.referenceOpacity > 0.01
            for pane in panes {
                guard let kind = pane.content.scopeKind, let plot = pane.plot else { continue }
                // 轨迹只允许画在自己那格的绘图区里（矢量图放大后也不许溢到邻格）
                setScissor(encoder, unitRect: pane.panel, drawableSize: drawableSize)
                let texture = scopeEngine.texture(for: kind, waveformMode: settings.waveformMode)
                let uv = scopeEngine.uvRect(for: kind, gain: settings.vectorscopeGain)

                var scopeUniforms = VSScopeUniforms()
                scopeUniforms.params = SIMD4<Float>(1,
                                                    Float(scopeSettings.traceIntensity),
                                                    1,
                                                    1)
                // RGB Parade / RGB 叠加波形自带通道配色，这里不能再上色，否则颜色会偏
                let isColorTrace = kind == .parade
                    || (kind == .waveform && settings.waveformMode == .rgbOverlay)
                let tint = isColorTrace ? SIMD3<Float>(1, 1, 1) : scopeSettings.traceColor
                scopeUniforms.color = SIMD4<Float>(tint.x, tint.y, tint.z, 1)
                scopeUniforms.refs = SIMD4<Float>(1, 1, 0, 0)
                scopeUniforms.flags = SIMD4<Float>(Float(settings.waveformMode.shaderValue), 0, 0, 0)

                encoder.setFragmentTexture(texture, index: Int(VSTextureIndexScope))
                encoder.setFragmentBytes(&scopeUniforms, length: MemoryLayout<VSScopeUniforms>.stride,
                                         index: Int(VSBufferIndexScopeUniforms))
                encodeQuad(encoder: encoder, rect: plot, uv: uv)

                // 冻结参考层：跟实时轨迹用完全相同的矩形与 UV，只是换成琥珀色、按不透明度变淡
                if showReference {
                    var ghost = scopeUniforms
                    ghost.color = SIMD4<Float>(vsReferenceTint.x, vsReferenceTint.y, vsReferenceTint.z, 1)
                    ghost.params.w = Float(settings.referenceOpacity)
                    encoder.setFragmentTexture(scopeEngine.referenceTexture(for: kind,
                                                                           waveformMode: settings.waveformMode),
                                               index: Int(VSTextureIndexScope))
                    encoder.setFragmentBytes(&ghost, length: MemoryLayout<VSScopeUniforms>.stride,
                                             index: Int(VSBufferIndexScopeUniforms))
                    encodeQuad(encoder: encoder, rect: plot, uv: uv)
                }

                clearScissor(encoder, drawableSize: drawableSize)
            }
        }

        encoder.endEncoding()
    }

    // MARK: - 工具

    private var fullRect: CGRect { CGRect(x: 0, y: 0, width: 1, height: 1) }

    /// 把「单位空间矩形（左上角原点，和 SwiftUI 一致）」设成 Metal 的裁剪框（像素，左上角原点）。
    /// 有它兜底，任何一格的内容都不可能画到邻格里去 —— 换 iPad、竖屏、矢量图放大都不会越界。
    private func setScissor(_ encoder: MTLRenderCommandEncoder,
                            unitRect: CGRect,
                            drawableSize: CGSize) {
        let width = Float(drawableSize.width)
        let height = Float(drawableSize.height)
        guard width > 1, height > 1 else { return }

        let x = max(0.0, min(Float(unitRect.minX) * width, width - 1))
        let y = max(0.0, min(Float(unitRect.minY) * height, height - 1))
        let w = max(1.0, min(Float(unitRect.width) * width, width - x))
        let h = max(1.0, min(Float(unitRect.height) * height, height - y))

        encoder.setScissorRect(MTLScissorRect(x: Int(x.rounded(.down)),
                                               y: Int(y.rounded(.down)),
                                               width: Int(w.rounded(.up)),
                                               height: Int(h.rounded(.up))))
    }

    private func clearScissor(_ encoder: MTLRenderCommandEncoder, drawableSize: CGSize) {
        let width = max(1, Int(drawableSize.width))
        let height = max(1, Int(drawableSize.height))
        encoder.setScissorRect(MTLScissorRect(x: 0, y: 0, width: width, height: height))
    }

    private func encodeQuad(encoder: MTLRenderCommandEncoder,
                            rect: CGRect,
                            uv: CGRect,
                            rotationDegrees: Int = 0) {
        var quad = VSQuadUniforms()
        quad.rect = SIMD4<Float>(Float(rect.minX), Float(rect.minY),
                                 Float(rect.width), Float(rect.height))
        quad.uv = SIMD4<Float>(Float(uv.minX), Float(uv.minY),
                               Float(uv.width), Float(uv.height))
        quad.misc = SIMD4<Float>(Float(rotationDegrees), 0, 0, 0)
        encoder.setVertexBytes(&quad, length: MemoryLayout<VSQuadUniforms>.stride,
                               index: Int(VSBufferIndexQuadUniforms))
        encoder.drawPrimitives(type: .triangleStrip, vertexStart: 0, vertexCount: 4)
    }

    private func makeScopeSettings() -> ScopeRenderSettings {
        var value = ScopeRenderSettings()
        value.enabled = settings.requiredScopes
        value.waveformMode = settings.waveformMode
        value.quality = settings.scopeQuality
        value.vectorscopeGain = settings.vectorscopeGain
        value.traceIntensity = settings.scopeIntensity
        value.panelOpacity = settings.scopeOpacity
        value.traceColor = settings.traceColor
        return value
    }

    private func makeRenderUniforms(sourceInfo: FrameSourceInfo,
                                    displayMode: DisplayMode,
                                    lut: LUTTextures?,
                                    lutActive: Bool) -> VSRenderUniforms {
        var uniforms = VSRenderUniforms()

        let columns = sourceInfo.colorMatrix.yuvToRGBColumns
        uniforms.yuvToRGB = simd_float3x3(columns: (columns.0, columns.1, columns.2))
        uniforms.primaryWeights = sourceInfo.colorMatrix.primaryWeights

        if sourceInfo.isVideoRange {
            let lumaScale: Float = 255.0 / 219.0
            let chromaScale: Float = 255.0 / 224.0
            uniforms.ycbcrScale = SIMD3<Float>(lumaScale, chromaScale, chromaScale)
            uniforms.ycbcrBias = SIMD3<Float>(-(16.0 / 255.0) * lumaScale,
                                              -(128.0 / 255.0) * chromaScale,
                                              -(128.0 / 255.0) * chromaScale)
        } else {
            uniforms.ycbcrScale = SIMD3<Float>(1, 1, 1)
            uniforms.ycbcrBias = SIMD3<Float>(0, -0.5, -0.5)
        }

        uniforms.grade = SIMD4<Float>(Float(settings.exposure),
                                      Float(settings.contrast),
                                      Float(settings.saturation),
                                      Float(max(settings.gamma, 0.05)))

        let size3D = lutActive ? (lut?.size3D ?? 0) : 0
        let size1D = lutActive ? (lut?.size1D ?? 0) : 0
        uniforms.lutParams = SIMD4<Float>(Float(min(max(settings.lutIntensity, 0), 1)),
                                          Float(size3D),
                                          Float(size1D),
                                          lutActive ? 1.0 : 0.0)
        uniforms.lutDomain = SIMD4<Float>(lut?.domainMin.x ?? 0,
                                          lut?.domainMax.x ?? 1,
                                          0, 0)
        uniforms.flags = SIMD4<Float>(Float(displayMode.shaderValue), 0, 0, 0)

        // 斑马纹参数（只在 fsDisplay 里生效，不进示波器统计）
        let videoRange = sourceInfo.isVideoRange
        uniforms.zebra = SIMD4<Float>(codeValue(fromIRE: settings.zebraThresholdIRE, videoRange: videoRange),
                                      settings.zebraEnabled ? 1 : 0,
                                      codeValue(fromIRE: 0, videoRange: videoRange),
                                      settings.zebraBlackEnabled ? 1 : 0)
        return uniforms
    }

    /// IRE → 0-1 码值（和 ShaderTypes.h 里 zebra.x / zebra.z 的约定一致）
    private func codeValue(fromIRE ire: Double, videoRange: Bool) -> Float {
        let code = videoRange ? (16 + ire / 100 * 219) : (ire / 100 * 255)
        return Float(min(max(code / 255, 0), 1))
    }
}
