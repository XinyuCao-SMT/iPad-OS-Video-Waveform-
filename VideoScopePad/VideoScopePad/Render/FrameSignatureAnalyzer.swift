//
//  FrameSignatureAnalyzer.swift
//  VideoScopePad
//
//  每帧一个「画面签名」：平均亮度 + 平均饱和度（64×64 采样，回读 16 KB）。
//
//  用途：找出测试信号里「黑场 → 彩条」的那一帧 —— 那就是声画延时测量中的**画面事件**。
//    静音黑场：亮度低、饱和度低
//    彩条画面：亮度高、饱和度高
//  所以判据简单可靠，不需要认彩条的图案，也就不怕分辨率/码率变化。
//
//  为什么不用已有的示波器直方图：
//    那是给示波器显示用的（大缓冲区、低频回读）；这里需要**每帧**都要有结果，
//    所以用一个极小的 kernel + 16 KB 回读，开销可以忽略。
//

import Foundation
import Metal

final class FrameSignatureAnalyzer {

    struct Signature {
        var luma: Float
        var saturation: Float
        /// 这一帧的时刻（主机时钟秒）
        var hostTime: TimeInterval
    }

    /// 采样网格边长（64 × 64 = 4096 个采样点 × 2 个值）
    private let grid = 64

    private let device: MTLDevice
    private let pipeline: MTLComputePipelineState
    private let buffer: MTLBuffer
    private let floatCount: Int

    /// 每帧回调（在 Metal 完成回调线程上）
    var onSignature: ((Signature) -> Void)?

    init(device: MTLDevice, pipeline: MTLComputePipelineState) throws {
        self.device = device
        self.pipeline = pipeline
        self.floatCount = grid * grid * 2
        guard let buffer = device.makeBuffer(length: floatCount * MemoryLayout<Float>.size,
                                             options: .storageModeShared) else {
            throw ScopeEngineError.bufferAllocationFailed
        }
        buffer.label = "画面签名"
        self.buffer = buffer
    }

    /// 在命令缓冲里加一个签名统计（必须在源纹理已经写好之后调用）
    func encode(texture: MTLTexture,
                hostTime: TimeInterval,
                commandBuffer: MTLCommandBuffer) {

        guard let encoder = commandBuffer.makeComputeCommandEncoder() else { return }
        encoder.label = "画面签名"
        encoder.setComputePipelineState(pipeline)
        encoder.setTexture(texture, index: Int(VSTextureIndexSource))
        encoder.setBuffer(buffer, offset: 0, index: 0)
        var gridSize = UInt32(grid)
        encoder.setBytes(&gridSize, length: MemoryLayout<UInt32>.size, index: 1)
        encoder.dispatchThreads(MTLSize(width: grid, height: grid, depth: 1),
                                threadsPerThreadgroup: MTLSize(width: 8, height: 8, depth: 1))
        encoder.endEncoding()

        let count = floatCount
        let target = buffer
        let callback = onSignature
        let time = hostTime
        commandBuffer.addCompletedHandler { _ in
            let pointer = target.contents().bindMemory(to: Float.self, capacity: count)
            var lumaSum: Float = 0
            var saturationSum: Float = 0
            for index in stride(from: 0, to: count, by: 2) {
                lumaSum += pointer[index]
                saturationSum += pointer[index + 1]
            }
            let samples = Float(count / 2)
            callback?(Signature(luma: lumaSum / samples,
                                saturation: saturationSum / samples,
                                hostTime: time))
        }
    }
}

/// 从画面签名里找出「黑场 → 彩条」的跳变
final class VideoBarsDetector {

    /// 彩条出现（onsetHost 为这一帧的时刻，previousHost 为上一帧的时刻，
    /// 两者之差就是这次测量的量化步长）
    var onBarsOnset: ((TimeInterval, TimeInterval) -> Void)?

    /// 判据：亮度与饱和度同时超过阈值
    private let lumaThreshold: Float = 0.22
    private let saturationThreshold: Float = 0.20
    /// 黑场至少要持续这么久，才算「一段黑场之后出现彩条」（避免画面里偶发的暗块误判）
    private let minimumBlackSeconds: TimeInterval = 0.15

    private var isBars = false
    private var blackSince: TimeInterval?
    private var previousHost: TimeInterval?

    func ingest(_ signature: FrameSignatureAnalyzer.Signature) {
        let bars = signature.luma > lumaThreshold && signature.saturation > saturationThreshold

        if bars, !isBars {
            if let blackSince, signature.hostTime - blackSince >= minimumBlackSeconds {
                onBarsOnset?(signature.hostTime, previousHost ?? signature.hostTime)
            }
        }
        if !bars {
            if blackSince == nil { blackSince = signature.hostTime }
        } else {
            blackSince = nil
        }
        isBars = bars
        previousHost = signature.hostTime
    }

    func reset() {
        isBars = false
        blackSince = nil
        previousHost = nil
    }
}
