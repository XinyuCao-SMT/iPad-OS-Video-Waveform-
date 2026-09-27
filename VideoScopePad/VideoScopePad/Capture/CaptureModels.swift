//
//  CaptureModels.swift
//  VideoScopePad
//

import AVFoundation
import CoreMedia
import CoreVideo
import Foundation

/// 输入信号的色彩矩阵（决定 YUV -> R'G'B' 的系数与亮度权重）
enum ColorMatrixKind: String {
    case bt601
    case bt709
    case bt2020

    var title: String {
        switch self {
        case .bt601: return "BT.601"
        case .bt709: return "BT.709"
        case .bt2020: return "BT.2020"
        }
    }

    /// 列主序矩阵：(Y', Cb, Cr) -> R'G'B'
    var yuvToRGBColumns: (SIMD3<Float>, SIMD3<Float>, SIMD3<Float>) {
        switch self {
        case .bt601:
            return (SIMD3(1.0, 1.0, 1.0),
                    SIMD3(0.0, -0.344136, 1.772),
                    SIMD3(1.402, -0.714136, 0.0))
        case .bt709:
            return (SIMD3(1.0, 1.0, 1.0),
                    SIMD3(0.0, -0.187324, 1.8556),
                    SIMD3(1.5748, -0.468124, 0.0))
        case .bt2020:
            return (SIMD3(1.0, 1.0, 1.0),
                    SIMD3(0.0, -0.164553, 1.8814),
                    SIMD3(1.4746, -0.571353, 0.0))
        }
    }

    /// BT.709 / BT.2020 的亮度权重
    var primaryWeights: SIMD3<Float> {
        switch self {
        case .bt601: return SIMD3(0.299, 0.587, 0.114)
        case .bt709: return SIMD3(0.2126, 0.7152, 0.0722)
        case .bt2020: return SIMD3(0.2627, 0.6780, 0.0593)
        }
    }
}

struct CaptureDeviceInfo: Identifiable, Hashable {
    let id: String
    let name: String
    let isExternal: Bool
    let modelID: String

    var badge: String { isExternal ? "UVC" : "内置" }
}

struct VideoFormatInfo: Identifiable, Hashable {
    let id: String
    let formatIndex: Int
    let width: Int
    let height: Int
    let frameRate: Double
    let subtype: FourCharCode
    let requestedPixelFormat: OSType
    let colorMatrix: ColorMatrixKind

    var usesBiPlanar: Bool {
        requestedPixelFormat == OSType(kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange)
            || requestedPixelFormat == OSType(kCVPixelFormatType_420YpCbCr8BiPlanarFullRange)
    }

    var isVideoRange: Bool {
        requestedPixelFormat == OSType(kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange)
    }

    var sizeText: String { "\(width)×\(height)" }

    var frameRateText: String {
        if abs(frameRate - frameRate.rounded()) < 0.01 {
            return "\(Int(frameRate.rounded()))p"
        }
        return String(format: "%.2fp", frameRate)
    }

    var pixelFormatText: String {
        CaptureFormatHelper.fourCCString(requestedPixelFormat)
    }

    var displayName: String {
        "\(sizeText) \(frameRateText) · \(pixelFormatText)"
    }
}

struct CaptureStats: Equatable {
    var isRunning = false
    var fps: Double = 0
    var droppedFrames: Int = 0
    var width: Int = 0
    var height: Int = 0
    var pixelFormatText: String = "-"
    var colorMatrixTitle: String = "-"
}

enum CaptureFormatHelper {

    static func fourCCString(_ code: FourCharCode) -> String {
        let bytes: [UInt8] = [
            UInt8((code >> 24) & 0xFF),
            UInt8((code >> 16) & 0xFF),
            UInt8((code >> 8) & 0xFF),
            UInt8(code & 0xFF)
        ]
        let text = String(bytes: bytes, encoding: .ascii) ?? "????"
        return text.trimmingCharacters(in: .whitespaces)
    }

    /// 采集输出所需的像素格式：
    /// - 设备原生就是双平面 YUV 时直接沿用，省掉一次转换
    /// - 其它情况（BGRA、MJPEG 等压缩格式）统一让系统转成 BGRA
    static func requestedPixelFormat(nativeSubtype: FourCharCode) -> OSType {
        let subtype = OSType(nativeSubtype)
        if subtype == OSType(kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange) {
            return OSType(kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange)
        }
        if subtype == OSType(kCVPixelFormatType_420YpCbCr8BiPlanarFullRange) {
            return OSType(kCVPixelFormatType_420YpCbCr8BiPlanarFullRange)
        }
        return OSType(kCVPixelFormatType_32BGRA)
    }

    static func colorMatrix(for formatDescription: CMFormatDescription) -> ColorMatrixKind {
        guard let primaries = CMFormatDescriptionGetExtension(
            formatDescription,
            extensionKey: kCMFormatDescriptionExtension_ColorPrimaries
        ) as? String else {
            return .bt709
        }

        if primaries == (kCMFormatDescriptionColorPrimaries_ITU_R_709_2 as String) {
            return .bt709
        }
        if primaries == (kCMFormatDescriptionColorPrimaries_ITU_R_2020 as String) {
            return .bt2020
        }
        if primaries == (kCMFormatDescriptionColorPrimaries_SMPTE_C as String)
            || primaries == (kCMFormatDescriptionColorPrimaries_EBU_3213 as String) {
            return .bt601
        }
        return .bt709
    }
}
