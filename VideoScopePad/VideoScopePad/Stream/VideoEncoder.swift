//
//  VideoEncoder.swift
//  VideoScopePad
//
//  用 VideoToolbox 把 CVPixelBuffer 编成 H.264。
//
//  为什么是 H.264 + AVCC：
//    · 低延迟、硬件编码、iPad 上功耗可控
//    · VideoToolbox 输出的就是 AVCC（4 字节长度前缀的 NALU），
//      正好等于 RTMP/FLV 视频消息里要的格式，中间不需要重新封装
//    · SPS/PPS 从 format description 里取出来拼成 AVCDecoderConfigurationRecord，
//      就是 FLV 的 AVC sequence header
//
//  音频：暂不处理（UVC 采集卡的 HDMI 内嵌音频不走视频设备的采集通道）
//

import CoreMedia
import CoreVideo
import Foundation
import VideoToolbox

struct EncodedVideoFrame {
    var sampleBuffer: CMSampleBuffer
    var isKeyframe: Bool
    /// 相对起始时间的毫秒数（RTMP/FLV 用）
    var timestampMs: Int64
}

final class VideoEncoder {

    enum EncoderError: LocalizedError {
        case sessionCreateFailed(OSStatus)
        case propertyFailed(String, OSStatus)

        var errorDescription: String? {
            switch self {
            case .sessionCreateFailed(let status): return "创建 H.264 编码器失败（\(status)）"
            case .propertyFailed(let name, let status): return "设置编码参数 \(name) 失败（\(status)）"
            }
        }
    }

    var onFrame: ((EncodedVideoFrame) -> Void)?
    /// AVCDecoderConfigurationRecord（含 SPS/PPS），推流开始时发一次
    var onParameterSets: ((Data) -> Void)?
    var onError: ((String) -> Void)?

    private var session: VTCompressionSession?
    private let width: Int
    private let height: Int
    private var firstPresentationTime: CMTime?
    private var parameterSetsSent = false

    init(width: Int,
         height: Int,
         bitrate: Int,
         frameRate: Int,
         keyframeIntervalSeconds: Double,
         sourcePixelFormat: OSType) throws {
        self.width = max(width, 16)
        self.height = max(height, 16)

        var created: VTCompressionSession?
        let callback: VTCompressionSessionOutputCallback = { refCon, _, status, flags, sampleBuffer in
            guard let refCon else { return }
            let encoder = Unmanaged<VideoEncoder>.fromOpaque(refCon).takeUnretainedValue()
            encoder.handleEncodedFrame(status: status, flags: flags, sampleBuffer: sampleBuffer)
        }

        // 按采集卡实际给的像素格式配置，省掉一次 CPU 色彩转换
        let pixelAttributes: [String: Any] = [
            kCVPixelBufferPixelFormatTypeKey as String: sourcePixelFormat,
            kCVPixelBufferWidthKey as String: self.width,
            kCVPixelBufferHeightKey as String: self.height,
            kCVPixelBufferIOSurfacePropertiesKey as String: [String: Any]()
        ]

        let status = VTCompressionSessionCreate(allocator: kCFAllocatorDefault,
                                               width: Int32(self.width),
                                               height: Int32(self.height),
                                               codecType: kCMVideoCodecType_H264,
                                               encoderSpecification: nil,
                                               imageBufferAttributes: pixelAttributes as CFDictionary,
                                               compressedDataAllocator: nil,
                                               outputCallback: callback,
                                               refcon: Unmanaged.passUnretained(self).toOpaque(),
                                               compressionSessionOut: &created)

        guard status == noErr, let session = created else {
            throw EncoderError.sessionCreateFailed(status)
        }
        self.session = session

        func setProperty(_ key: CFString, _ value: CFTypeRef, _ name: String) throws {
            let result = VTSessionSetProperty(session, key: key, value: value)
            if result != noErr {
                throw EncoderError.propertyFailed(name, result)
            }
        }

        try setProperty(kVTCompressionPropertyKey_RealTime, kCFBooleanTrue, "RealTime")
        try setProperty(kVTCompressionPropertyKey_AllowFrameReordering, kCFBooleanFalse, "AllowFrameReordering")
        try setProperty(kVTCompressionPropertyKey_ProfileLevel,
                        kVTProfileLevel_H264_High_AutoLevel,
                        "ProfileLevel")
        try setProperty(kVTCompressionPropertyKey_AverageBitRate, bitrate as CFNumber, "AverageBitRate")
        try setProperty(kVTCompressionPropertyKey_MaxKeyFrameInterval,
                        Int(frameRate * Int(max(keyframeIntervalSeconds, 0.5))) as CFNumber,
                        "MaxKeyFrameInterval")
        try setProperty(kVTCompressionPropertyKey_ExpectedFrameRate, frameRate as CFNumber, "ExpectedFrameRate")

        VTCompressionSessionPrepareToEncodeFrames(session)
    }

    func invalidate() {
        guard let session else { return }
        VTCompressionSessionCompleteFrames(session, untilPresentationTimeStamp: .invalid)
        VTCompressionSessionInvalidate(session)
        self.session = nil
    }

    /// 送一帧进去（BGR A 像素缓冲）
    func encode(_ pixelBuffer: CVPixelBuffer, presentationTime: CMTime) {
        guard let session else { return }
        if firstPresentationTime == nil {
            firstPresentationTime = presentationTime
        }

        var flags: VTEncodeInfoFlags = []
        let status = VTCompressionSessionEncodeFrame(session,
                                                    imageBuffer: pixelBuffer,
                                                    presentationTimeStamp: presentationTime,
                                                    duration: .invalid,
                                                    frameProperties: nil,
                                                    sourceFrameRefcon: nil,
                                                    infoFlagsOut: &flags)
        if status != noErr {
            onError?("编码送帧失败（\(status)）")
        }
    }

    func requestKeyframe() {
        guard let session else { return }
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_ForceKeyFrame, value: kCFBooleanTrue)
    }

    // MARK: - 回调

    private func handleEncodedFrame(status: OSStatus,
                                    flags: VTEncodeInfoFlags,
                                    sampleBuffer: CMSampleBuffer?) {
        guard status == noErr, let sampleBuffer, CMSampleBufferDataIsReady(sampleBuffer) else {
            if status != noErr { onError?("编码回调出错（\(status)）") }
            return
        }

        // SPS/PPS 只在第一帧里完整，拿出来拼成 FLV 需要的 sequence header
        if !parameterSetsSent, let format = CMSampleBufferGetFormatDescription(sampleBuffer) {
            if let record = Self.makeAVCDecoderConfigurationRecord(from: format) {
                parameterSetsSent = true
                onParameterSets?(record)
            }
        }

        let pts = CMSampleBufferGetPresentationTimeStamp(sampleBuffer)
        let base = firstPresentationTime ?? pts
        let ms = Int64((CMTimeGetSeconds(CMTimeSubtract(pts, base)) * 1000).rounded())

        onFrame?(EncodedVideoFrame(sampleBuffer: sampleBuffer,
                                  isKeyframe: Self.isKeyframe(sampleBuffer),
                                  timestampMs: max(ms, 0)))
    }

    private static func isKeyframe(_ sampleBuffer: CMSampleBuffer) -> Bool {
        guard let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: false)
                as? [[CFString: Any]], let first = attachments.first else {
            return true
        }
        let notSync = first[kCMSampleAttachmentKey_NotSync] as? Bool ?? false
        return !notSync
    }

    /// AVCDecoderConfigurationRecord（FLV AVC sequence header 的负载部分）
    static func makeAVCDecoderConfigurationRecord(from format: CMFormatDescription) -> Data? {
        var spsPointer: UnsafePointer<UInt8>?
        var spsSize = 0
        var ppsPointer: UnsafePointer<UInt8>?
        var ppsSize = 0
        var nalLength: Int32 = 0
        var count = 0

        guard CMVideoFormatDescriptionGetH264ParameterSetAtIndex(format,
                                                                parameterSetIndex: 0,
                                                                parameterSetPointerOut: &spsPointer,
                                                                parameterSetSizeOut: &spsSize,
                                                                parameterSetCountOut: &count,
                                                                nalUnitHeaderLengthOut: &nalLength) == noErr,
              let spsPointer, spsSize > 0 else {
            return nil
        }
        guard CMVideoFormatDescriptionGetH264ParameterSetAtIndex(format,
                                                                parameterSetIndex: 1,
                                                                parameterSetPointerOut: &ppsPointer,
                                                                parameterSetSizeOut: &ppsSize,
                                                                parameterSetCountOut: nil,
                                                                nalUnitHeaderLengthOut: nil) == noErr,
              let ppsPointer, ppsSize > 0 else {
            return nil
        }

        var data = Data()
        data.append(0x01)                       // configurationVersion
        data.append(spsPointer[1])              // AVCProfileIndication
        data.append(spsPointer[2])              // profile_compatibility
        data.append(spsPointer[3])              // AVCLevelIndication
        data.append(0xFF)                       // lengthSizeMinusOne = 3（4 字节长度前缀）
        data.append(0xE1)                       // numOfSequenceParameterSets = 1

        var spsLength = UInt16(spsSize).bigEndian
        withUnsafeBytes(of: &spsLength) { data.append(contentsOf: $0) }
        data.append(spsPointer, count: spsSize)

        data.append(0x01)                       // numOfPictureParameterSets = 1
        var ppsLength = UInt16(ppsSize).bigEndian
        withUnsafeBytes(of: &ppsLength) { data.append(contentsOf: $0) }
        data.append(ppsPointer, count: ppsSize)

        return data
    }

    /// 把 CMSampleBuffer 里的 AVCC 数据取出来（已是 4 字节长度前缀，可直接用于 FLV）
    static func avccPayload(from sampleBuffer: CMSampleBuffer) -> Data? {
        guard let blockBuffer = CMSampleBufferGetDataBuffer(sampleBuffer) else { return nil }
        var length = 0
        var pointer: UnsafeMutablePointer<Int8>?
        guard CMBlockBufferGetDataPointer(blockBuffer,
                                         atOffset: 0,
                                         lengthAtOffsetOut: nil,
                                         totalLengthOut: &length,
                                         dataPointerOut: &pointer) == noErr,
              let pointer, length > 0 else {
            return nil
        }
        return Data(bytes: pointer, count: length)
    }
}
