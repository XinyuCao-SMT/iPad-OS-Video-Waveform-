//
//  LUTTextures.swift
//  VideoScopePad
//
//  把解析出来的 .cube 数据上传成 Metal 纹理：
//   - 3D LUT 使用 MTLTextureType.type3D + 线性过滤，三轴插值一次完成
//   - 1D shaper LUT 使用 N×1 纹理（log 转 linear 等前置整形）
//  无论有没有加载 LUT，着色器都会绑定一组占位纹理，避免空绑定。
//

import Foundation
import Metal

enum LUTTextureError: LocalizedError {
    case noData
    case textureCreationFailed(String)

    var errorDescription: String? {
        switch self {
        case .noData: return "LUT 里没有有效数据"
        case .textureCreationFailed(let text): return "创建 LUT 纹理失败：\(text)"
        }
    }
}

struct LUTTextures {
    let title: String
    let texture3D: MTLTexture
    let size3D: Int
    let texture1D: MTLTexture
    let size1D: Int
    let domainMin: SIMD3<Float>
    let domainMax: SIMD3<Float>

    var hasContent: Bool { size3D > 0 || size1D > 0 }

    var description: String {
        var parts: [String] = []
        if size1D > 0 { parts.append("1D×\(size1D)") }
        if size3D > 0 { parts.append("3D×\(size3D)") }
        if parts.isEmpty { return "未加载" }
        return parts.joined(separator: " + ")
    }
}

enum LUTTextureBuilder {

    static func make(from cube: CubeLUT, device: MTLDevice) throws -> LUTTextures {
        guard cube.size3D > 1 || cube.size1D > 0 else {
            throw LUTTextureError.noData
        }

        var texture3D = try makeIdentity3D(device: device)
        var size3D = 0
        if cube.size3D > 1 {
            texture3D = try make3D(data: cube.data3D, size: cube.size3D, device: device)
            size3D = cube.size3D
        }

        var texture1D = try makeIdentity1D(device: device)
        var size1D = 0
        if cube.size1D > 0 {
            texture1D = try make1D(data: cube.data1D, size: cube.size1D, device: device)
            size1D = cube.size1D
        }

        return LUTTextures(title: cube.title.isEmpty ? "未命名 LUT" : cube.title,
                           texture3D: texture3D,
                           size3D: size3D,
                           texture1D: texture1D,
                           size1D: size1D,
                           domainMin: cube.domainMin,
                           domainMax: cube.domainMax)
    }

    /// 未启用 LUT 时绑定的占位纹理（hasContent == false）
    static func makePlaceholder(device: MTLDevice) throws -> LUTTextures {
        LUTTextures(title: "关闭",
                    texture3D: try makeIdentity3D(device: device),
                    size3D: 0,
                    texture1D: try makeIdentity1D(device: device),
                    size1D: 0,
                    domainMin: SIMD3<Float>(0, 0, 0),
                    domainMax: SIMD3<Float>(1, 1, 1))
    }

    // MARK: - 纹理创建

    private static func make3D(data: [Float16], size: Int, device: MTLDevice) throws -> MTLTexture {
        let descriptor = MTLTextureDescriptor()
        descriptor.textureType = .type3D
        descriptor.pixelFormat = .rgba16Float
        descriptor.width = size
        descriptor.height = size
        descriptor.depth = size
        descriptor.mipmapLevelCount = 1
        descriptor.usage = .shaderRead
        descriptor.storageMode = .shared

        guard let texture = device.makeTexture(descriptor: descriptor) else {
            throw LUTTextureError.textureCreationFailed("3D \(size)³")
        }
        texture.label = "LUT 3D \(size)"

        let bytesPerRow = size * MemoryLayout<Float16>.size * 4
        let bytesPerImage = bytesPerRow * size
        let region = MTLRegionMake3D(0, 0, 0, size, size, size)

        data.withUnsafeBytes { raw in
            if let base = raw.baseAddress {
                texture.replace(region: region,
                                mipmapLevel: 0,
                                withBytes: base,
                                bytesPerRow: bytesPerRow,
                                bytesPerImage: bytesPerImage)
            }
        }
        return texture
    }

    private static func make1D(data: [Float16], size: Int, device: MTLDevice) throws -> MTLTexture {
        let descriptor = MTLTextureDescriptor.texture2DDescriptor(pixelFormat: .rgba16Float,
                                                                  width: size,
                                                                  height: 1,
                                                                  mipmapped: false)
        descriptor.usage = .shaderRead
        descriptor.storageMode = .shared

        guard let texture = device.makeTexture(descriptor: descriptor) else {
            throw LUTTextureError.textureCreationFailed("1D \(size)")
        }
        texture.label = "LUT 1D \(size)"

        let bytesPerRow = size * MemoryLayout<Float16>.size * 4
        let region = MTLRegionMake2D(0, 0, size, 1)

        data.withUnsafeBytes { raw in
            if let base = raw.baseAddress {
                texture.replace(region: region,
                                mipmapLevel: 0,
                                withBytes: base,
                                bytesPerRow: bytesPerRow)
            }
        }
        return texture
    }

    private static func makeIdentity3D(device: MTLDevice) throws -> MTLTexture {
        var data = [Float16](repeating: 0, count: 2 * 2 * 2 * 4)
        var index = 0
        for b in 0..<2 {
            for g in 0..<2 {
                for r in 0..<2 {
                    data[index + 0] = Float16(r)
                    data[index + 1] = Float16(g)
                    data[index + 2] = Float16(b)
                    data[index + 3] = 1
                    index += 4
                }
            }
        }
        return try make3D(data: data, size: 2, device: device)
    }

    private static func makeIdentity1D(device: MTLDevice) throws -> MTLTexture {
        var data = [Float16](repeating: 0, count: 2 * 4)
        for i in 0..<2 {
            data[i * 4 + 0] = Float16(i)
            data[i * 4 + 1] = Float16(i)
            data[i * 4 + 2] = Float16(i)
            data[i * 4 + 3] = 1
        }
        return try make1D(data: data, size: 2, device: device)
    }
}

/// 渲染线程与 UI 线程之间的 LUT 纹理槽（加锁保证安全）
final class LUTSlot {
    private let lock = NSLock()
    private var textures: LUTTextures?

    func set(_ value: LUTTextures?) {
        lock.lock()
        textures = value
        lock.unlock()
    }

    func current() -> LUTTextures? {
        lock.lock()
        defer { lock.unlock() }
        return textures
    }
}
