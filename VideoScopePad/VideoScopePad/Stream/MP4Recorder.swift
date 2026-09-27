//
//  MP4Recorder.swift
//  VideoScopePad
//
//  本机录制：把 VideoEncoder 编好的 H.264 帧直接写进 MP4（直通模式，不二次编码）。
//
//  用直通（outputSettings: nil）的好处：
//    · 只编一次码，录制与推流共用同一份编码结果，CPU/功耗几乎不增加
//    · 录到的就是推出去的那一帧
//

import AVFoundation
import CoreMedia
import Foundation

final class MP4Recorder {

    enum RecorderError: LocalizedError {
        case writerCreateFailed(String)
        case inputCreateFailed
        case startFailed(String)

        var errorDescription: String? {
            switch self {
            case .writerCreateFailed(let text): return "创建录制文件失败：\(text)"
            case .inputCreateFailed: return "创建视频写入通道失败"
            case .startFailed(let text): return "开始录制失败：\(text)"
            }
        }
    }

    let url: URL
    private var writer: AVAssetWriter?
    private var input: AVAssetWriterInput?
    private var started = false
    private var frameCount = 0

    init() {
        let documents = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0]
        let folder = documents.appendingPathComponent("Recordings", isDirectory: true)
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)

        let formatter = DateFormatter()
        formatter.dateFormat = "yyyyMMdd-HHmmss"
        url = folder.appendingPathComponent("VideoScopePad-\(formatter.string(from: Date())).mp4")
    }

    var recordedFrames: Int { frameCount }

    /// 第一帧编码完成后才知道 format description，所以这里懒创建
    func append(_ frame: EncodedVideoFrame) throws {
        if writer == nil {
            try createWriter(formatHint: CMSampleBufferGetFormatDescription(frame.sampleBuffer))
        }
        guard let writer, let input, writer.status == .writing else { return }
        guard input.isReadyForMoreMediaData else { return }
        if input.append(frame.sampleBuffer) {
            frameCount += 1
        }
    }

    private func createWriter(formatHint: CMFormatDescription?) throws {
        let writer: AVAssetWriter
        do {
            writer = try AVAssetWriter(outputURL: url, fileType: .mp4)
        } catch {
            throw RecorderError.writerCreateFailed(error.localizedDescription)
        }

        // outputSettings 传 nil = 直通，直接把已经编好的 H.264 写进文件
        let input: AVAssetWriterInput
        if let formatHint {
            input = AVAssetWriterInput(mediaType: .video, outputSettings: nil, sourceFormatHint: formatHint)
        } else {
            input = AVAssetWriterInput(mediaType: .video, outputSettings: nil)
        }
        input.expectsMediaDataInRealTime = true

        guard writer.canAdd(input) else {
            throw RecorderError.inputCreateFailed
        }
        writer.add(input)

        guard writer.startWriting() else {
            throw RecorderError.startFailed(writer.error?.localizedDescription ?? "未知错误")
        }
        writer.startSession(atSourceTime: .zero)

        self.writer = writer
        self.input = input
        self.started = true
    }

    /// 结束录制，返回文件地址（失败返回 nil）
    func finish() async -> URL? {
        guard let writer, let input, started else { return nil }
        input.markAsFinished()
        await writer.finishWriting()
        let ok = writer.status == .completed
        self.writer = nil
        self.input = nil
        started = false
        return ok ? url : nil
    }
}
