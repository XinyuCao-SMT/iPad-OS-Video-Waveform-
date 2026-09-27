//
//  MetalContext.swift
//  VideoScopePad
//
//  Metal 设备 / 命令队列 / 着色器库 / 管线状态 / CVMetalTextureCache 的集中管理。
//

import CoreVideo
import Foundation
import Metal
import MetalKit

enum MetalContextError: LocalizedError {
    case noDevice
    case noCommandQueue
    case noLibrary(String)
    case missingFunction(String)
    case pipelineFailed(String)
    case textureCacheFailed(CVReturn)

    var errorDescription: String? {
        switch self {
        case .noDevice:
            return "本设备没有可用的 Metal GPU"
        case .noCommandQueue:
            return "无法创建 Metal 命令队列"
        case .noLibrary(let message):
            return "无法加载 Metal 着色器库：\(message)"
        case .missingFunction(let name):
            return "着色器库里缺少函数 \(name)"
        case .pipelineFailed(let name):
            return "创建渲染管线失败：\(name)"
        case .textureCacheFailed(let code):
            return "创建 CVMetalTextureCache 失败（\(code)）"
        }
    }
}

struct MetalPipelines {
    let videoBiPlanar: MTLRenderPipelineState
    let videoBGRA: MTLRenderPipelineState
    let applyLUTAndGrade: MTLRenderPipelineState
    let display: MTLRenderPipelineState
    let solidColor: MTLRenderPipelineState
    let scopeTrace: MTLRenderPipelineState

    let accumulateHistogram: MTLComputePipelineState
    let normalizeWaveform: MTLComputePipelineState
    let normalizeOverlay: MTLComputePipelineState
    let normalizeParade: MTLComputePipelineState
    let normalizeVectorscope: MTLComputePipelineState
    /// 全局测量（CPU 数值读数的数据来源）
    let accumulateMeasurement: MTLComputePipelineState
}

final class MetalContext {

    let device: MTLDevice
    let commandQueue: MTLCommandQueue
    let library: MTLLibrary
    let textureCache: CVMetalTextureCache
    let pipelines: MetalPipelines

    init() throws {
        guard let device = MTLCreateSystemDefaultDevice() else {
            throw MetalContextError.noDevice
        }
        guard let queue = device.makeCommandQueue() else {
            throw MetalContextError.noCommandQueue
        }

        let library: MTLLibrary
        do {
            library = try device.makeDefaultLibrary(bundle: .main)
        } catch {
            throw MetalContextError.noLibrary(error.localizedDescription)
        }

        self.device = device
        self.commandQueue = queue
        self.library = library

        var cache: CVMetalTextureCache?
        let status = CVMetalTextureCacheCreate(kCFAllocatorDefault, nil, device, nil, &cache)
        guard status == kCVReturnSuccess, let textureCache = cache else {
            throw MetalContextError.textureCacheFailed(status)
        }
        self.textureCache = textureCache

        self.pipelines = try Self.makePipelines(device: device, library: library)
    }

    private static func makePipelines(device: MTLDevice, library: MTLLibrary) throws -> MetalPipelines {

        func function(_ name: String) throws -> MTLFunction {
            guard let fn = library.makeFunction(name: name) else {
                throw MetalContextError.missingFunction(name)
            }
            return fn
        }

        let vertexFunction = try function("vsQuadVertex")

        func makeRenderPipeline(fragment: String,
                                pixelFormat: MTLPixelFormat,
                                blending: (enabled: Bool, additive: Bool) = (false, false)) throws -> MTLRenderPipelineState {
            let descriptor = MTLRenderPipelineDescriptor()
            descriptor.label = "\(fragment)"
            descriptor.vertexFunction = vertexFunction
            descriptor.fragmentFunction = try function(fragment)
            descriptor.colorAttachments[0].pixelFormat = pixelFormat

            if blending.enabled {
                let attachment = descriptor.colorAttachments[0]!
                attachment.isBlendingEnabled = true
                attachment.rgbBlendOperation = .add
                attachment.alphaBlendOperation = .add
                if blending.additive {
                    // 示波器辉光：纯加法混合
                    attachment.sourceRGBBlendFactor = .one
                    attachment.destinationRGBBlendFactor = .one
                    attachment.sourceAlphaBlendFactor = .one
                    attachment.destinationAlphaBlendFactor = .one
                } else {
                    // 面板背景：常规 alpha 混合
                    attachment.sourceRGBBlendFactor = .sourceAlpha
                    attachment.destinationRGBBlendFactor = .oneMinusSourceAlpha
                    attachment.sourceAlphaBlendFactor = .one
                    attachment.destinationAlphaBlendFactor = .oneMinusSourceAlpha
                }
            }

            do {
                return try device.makeRenderPipelineState(descriptor: descriptor)
            } catch {
                throw MetalContextError.pipelineFailed("\(fragment): \(error.localizedDescription)")
            }
        }

        func makeComputePipeline(_ name: String) throws -> MTLComputePipelineState {
            do {
                return try device.makeComputePipelineState(function: try function(name))
            } catch {
                throw MetalContextError.pipelineFailed("\(name): \(error.localizedDescription)")
            }
        }

        let pipelines = MetalPipelines(
            videoBiPlanar: try makeRenderPipeline(fragment: "fsVideoBiPlanar", pixelFormat: .rgba16Float),
            videoBGRA: try makeRenderPipeline(fragment: "fsVideoBGRA", pixelFormat: .rgba16Float),
            applyLUTAndGrade: try makeRenderPipeline(fragment: "fsApplyLUTAndGrade", pixelFormat: .rgba16Float),
            display: try makeRenderPipeline(fragment: "fsDisplay", pixelFormat: .bgra8Unorm),
            solidColor: try makeRenderPipeline(fragment: "fsSolidColor",
                                               pixelFormat: .bgra8Unorm,
                                               blending: (true, false)),
            scopeTrace: try makeRenderPipeline(fragment: "fsScopeTrace",
                                               pixelFormat: .bgra8Unorm,
                                               blending: (true, true)),
            accumulateHistogram: try makeComputePipeline("vsAccumulateHistogram"),
            normalizeWaveform: try makeComputePipeline("vsNormalizeWaveform"),
            normalizeOverlay: try makeComputePipeline("vsNormalizeOverlay"),
            normalizeParade: try makeComputePipeline("vsNormalizeParade"),
            normalizeVectorscope: try makeComputePipeline("vsNormalizeVectorscope"),
            accumulateMeasurement: try makeComputePipeline("vsAccumulateMeasurement")
        )

        return pipelines
    }

    /// 创建供渲染使用的中间纹理（rgba16Float：调色/LUT 不损失精度）
    func makeIntermediateTexture(width: Int, height: Int, label: String) -> MTLTexture? {
        let descriptor = MTLTextureDescriptor.texture2DDescriptor(pixelFormat: .rgba16Float,
                                                                  width: max(width, 1),
                                                                  height: max(height, 1),
                                                                  mipmapped: false)
        descriptor.usage = [.renderTarget, .shaderRead]
        descriptor.storageMode = .private
        let texture = device.makeTexture(descriptor: descriptor)
        texture?.label = label
        return texture
    }

    /// 示波器输出纹理（8 位足够，便于 compute 直接写入）
    func makeScopeTexture(width: Int, height: Int, label: String) -> MTLTexture? {
        let descriptor = MTLTextureDescriptor.texture2DDescriptor(pixelFormat: .rgba8Unorm,
                                                                  width: max(width, 1),
                                                                  height: max(height, 1),
                                                                  mipmapped: false)
        descriptor.usage = [.shaderRead, .shaderWrite]
        descriptor.storageMode = .private
        let texture = device.makeTexture(descriptor: descriptor)
        texture?.label = label
        return texture
    }
}
