//
//  StreamController.swift
//  VideoScopePad
//
//  录制 / 推流的总调度：
//     采集帧 → VideoEncoder(H.264) ─┬→ MP4Recorder（本机录制）
//                                   └→ RTMPClient（推流）
//
//  说明：
//    · 录制与推流共用同一次编码结果（MP4 用直通写入），所以两个一起开也不会翻倍耗电
//    · 默认录/推的是**输入信号**（采集卡原始画面）；套 LUT 之后的画面是监看用的显示信号，
//      录制原始信号更符合"留一份原始素材"的做法（post-LUT 录制见 NEXT-STEPS.md）
//    · 音频暂不支持：采集卡的 HDMI 内嵌音频不走视频设备的采集通道
//

import AVFoundation
import Combine
import CoreImage
import CoreMedia
import CoreVideo
import Foundation
import Photos
import UIKit

final class StreamController: ObservableObject {

    // MARK: - 对外状态

    @Published private(set) var isRecording = false
    @Published private(set) var isStreaming = false
    @Published private(set) var isPreparing = false
    @Published private(set) var statusText = "空闲"
    @Published private(set) var detailText = ""
    @Published private(set) var lastError: String?
    @Published private(set) var lastRecordingURL: URL?
    @Published private(set) var lastLogURL: URL?

    /// 读数 CSV 记录器（界面读它的行数与开关）
    let log = MeasurementLog()

    /// 是否记录读数（绑定到界面）
    @Published var logEnabled = false {
        didSet { log.isEnabled = logEnabled }
    }
    /// 已记录行数（每秒刷新一次，避免 10Hz 刷界面）
    @Published private(set) var logLineCount = 0
    private var lastLogCountUpdate = Date.distantPast

    // MARK: - 参数（界面绑定，自动持久化）

    @Published var bitrateMbps: Double = 8 {
        didSet { UserDefaults.standard.set(bitrateMbps, forKey: "vsp.stream.bitrate") }
    }
    @Published var keyframeSeconds: Double = 2 {
        didSet { UserDefaults.standard.set(keyframeSeconds, forKey: "vsp.stream.keyframe") }
    }
    @Published var rtmpURL: String = "" {
        didSet { UserDefaults.standard.set(rtmpURL, forKey: "vsp.stream.rtmpURL") }
    }
    @Published var streamKey: String = "" {
        didSet { UserDefaults.standard.set(streamKey, forKey: "vsp.stream.key") }
    }

    init() {
        let defaults = UserDefaults.standard
        if let value = defaults.object(forKey: "vsp.stream.bitrate") as? Double { bitrateMbps = value }
        if let value = defaults.object(forKey: "vsp.stream.keyframe") as? Double { keyframeSeconds = value }
        if let value = defaults.string(forKey: "vsp.stream.rtmpURL") { rtmpURL = value }
        if let value = defaults.string(forKey: "vsp.stream.key") { streamKey = value }
    }

    // MARK: - 内部

    private var encoder: VideoEncoder?
    private var encoderSourceFormat: OSType = 0
    private var encoderWidth = 0
    private var encoderHeight = 0

    private var recorder: MP4Recorder?
    private var rtmp: RTMPClient?

    private var startDate: Date?
    private var encodedFrameCount = 0
    private var lastStatUpdate = Date.distantPast

    // MARK: - 采集帧入口（在采集线程上调用）

    func submit(pixelBuffer: CVPixelBuffer, presentationTime: CMTime) {
        // 抓帧请求优先处理（即使没在录制/推流，也能抓）
        if pendingGrab {
            pendingGrab = false
            grabFrame(from: pixelBuffer)
        }

        guard isRecording || isStreaming else { return }
        guard let encoder = ensureEncoder(for: pixelBuffer) else { return }

        encoder.encode(pixelBuffer, presentationTime: presentationTime)

        // 轻量统计（每秒最多更新一次状态，避免高频刷新界面）
        encodedFrameCount += 1
        let now = Date()
        if now.timeIntervalSince(lastStatUpdate) >= 1 {
            lastStatUpdate = now
            let seconds = startDate.map { now.timeIntervalSince($0) } ?? 0
            let count = encodedFrameCount
            DispatchQueue.main.async {
                self.detailText = String(format: "%.1f fps · %.1f Mb/s · 已录 %d 帧 · %.0f 秒",
                                         Double(count) / max(seconds, 0.001),
                                         self.bitrateMbps,
                                         count,
                                         seconds)
            }
        }
    }

    private func ensureEncoder(for pixelBuffer: CVPixelBuffer) -> VideoEncoder? {
        let format = CVPixelBufferGetPixelFormatType(pixelBuffer)
        let width = CVPixelBufferGetWidth(pixelBuffer)
        let height = CVPixelBufferGetHeight(pixelBuffer)

        if let encoder, encoderSourceFormat == format, encoderWidth == width, encoderHeight == height {
            return encoder
        }

        encoder?.invalidate()
        encoder = nil

        let bitrate = Int(max(bitrateMbps, 0.5) * 1_000_000)
        do {
            let created = try VideoEncoder(width: width,
                                           height: height,
                                           bitrate: bitrate,
                                           frameRate: 60,
                                           keyframeIntervalSeconds: keyframeSeconds,
                                           sourcePixelFormat: format)
            created.onFrame = { [weak self] frame in
                self?.handleEncodedFrame(frame)
            }
            created.onParameterSets = { [weak self] record in
                self?.rtmp?.sendAVCSequenceHeader(record)
            }
            created.onError = { [weak self] message in
                DispatchQueue.main.async { self?.lastError = message }
            }
            encoder = created
            encoderSourceFormat = format
            encoderWidth = width
            encoderHeight = height
        } catch {
            DispatchQueue.main.async {
                self.lastError = error.localizedDescription
                self.statusText = "编码器启动失败"
            }
            return nil
        }
        return encoder
    }

    private func handleEncodedFrame(_ frame: EncodedVideoFrame) {
        // 录制
        if let recorder {
            do {
                try recorder.append(frame)
            } catch {
                DispatchQueue.main.async {
                    self.lastError = error.localizedDescription
                }
            }
        }

        // 推流
        if let rtmp, rtmp.isPublishing {
            if let payload = VideoEncoder.avccPayload(from: frame.sampleBuffer) {
                rtmp.sendVideoFrame(payload, isKeyframe: frame.isKeyframe, timestampMs: frame.timestampMs)
            }
        }
    }

    // MARK: - 录制

    func startRecording() {
        guard !isRecording else { return }
        let newRecorder = MP4Recorder()
        recorder = newRecorder
        isRecording = true
        if startDate == nil { startDate = Date() }
        statusText = isStreaming ? "录制 + 推流中" : "录制中"
        lastError = nil
        objectWillChange.send()
    }

    func stopRecording() {
        guard isRecording else { return }
        isRecording = false
        let current = recorder
        recorder = nil

        Task { [weak self] in
            let url = await current?.finish()
            await MainActor.run {
                guard let self else { return }
                self.lastRecordingURL = url
                if url == nil {
                    self.lastError = "录制文件收尾失败"
                }
                self.statusText = self.isStreaming ? "推流中" : "空闲"
                if url == nil, !self.isStreaming { self.stopEncoderIfIdle() }
            }
        }
    }

    // MARK: - 推流（RTMP）

    func startStreaming() {
        guard !isStreaming else { return }
        let trimmedURL = rtmpURL.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmedURL.isEmpty else {
            lastError = "请先填 RTMP 地址"
            return
        }

        lastError = nil
        isPreparing = true
        statusText = "连接服务器…"

        let client = RTMPClient()
        client.onStatus = { [weak self] status in
            DispatchQueue.main.async {
                guard let self else { return }
                self.statusText = status.text
                if case .failed(let message) = status {
                    self.lastError = message
                    self.isStreaming = false
                    self.isPreparing = false
                    self.stopEncoderIfIdle()
                }
                if case .publishing = status {
                    self.isStreaming = true
                    self.isPreparing = false
                }
            }
        }
        client.onReadyToPublish = { [weak self] in
            // 服务器确认后把 SPS/PPS 与关键帧重新发一遍，确保秒开
            self?.encoder?.requestKeyframe()
        }
        rtmp = client
        client.connect(urlString: trimmedURL, streamKey: streamKey)
    }

    func stopStreaming() {
        rtmp?.disconnect()
        rtmp = nil
        isStreaming = false
        isPreparing = false
        statusText = isRecording ? "录制中" : "空闲"
        stopEncoderIfIdle()
    }

    // MARK: - 收尾

    private func stopEncoderIfIdle() {
        guard !isRecording, !isStreaming else { return }
        encoder?.invalidate()
        encoder = nil
        encodedFrameCount = 0
        startDate = nil
        detailText = ""
    }

    func stopAll() {
        if isStreaming { stopStreaming() }
        if isRecording { stopRecording() }
    }

    // MARK: - 读数 CSV

    func noteMeasurement(_ measurement: SignalMeasurement, activeWarnings: [String]) {
        log.append(measurement, activeWarnings: activeWarnings)

        let now = Date()
        if now.timeIntervalSince(lastLogCountUpdate) >= 1 {
            lastLogCountUpdate = now
            if logEnabled {
                logLineCount = log.lineCount
            }
        }
    }

    func exportLog() {
        do {
            let url = try log.writeToFile()
            lastLogURL = url
            statusText = "读数已导出"
        } catch {
            lastError = "导出失败：\(error.localizedDescription)"
        }
    }

    func clearLog() {
        log.reset()
        logLineCount = 0
    }

    // MARK: - 抓帧（把当前输入帧存成图片）

    private var ciContext: CIContext?
    @Published private(set) var grabMessage: String?
    private var isGrabbing = false
    /// 抓帧请求：下一帧到来时执行（不长期持有像素缓冲，避免占用缓冲池）
    private var pendingGrab = false

    func requestGrabFrame() {
        pendingGrab = true
        grabMessage = "等待下一帧…"
    }

    /// 抓一帧存进相册。注意：内容是**输入信号**（采集卡原始画面），
    /// 带 LUT 与示波器的合成截图见 NEXT-STEPS.md。
    func grabFrame(from pixelBuffer: CVPixelBuffer) {
        guard !isGrabbing else { return }
        isGrabbing = true

        let context = ciContext ?? CIContext(options: [.useSoftwareRenderer: false])
        ciContext = context

        let image = CIImage(cvPixelBuffer: pixelBuffer)
        guard let cgImage = context.createCGImage(image, from: image.extent) else {
            isGrabbing = false
            DispatchQueue.main.async { self.grabMessage = "抓帧失败：无法生成图片" }
            return
        }
        let uiImage = UIImage(cgImage: cgImage)

        PHPhotoLibrary.requestAuthorization(for: .addOnly) { [weak self] status in
            guard status == .authorized || status == .limited else {
                DispatchQueue.main.async {
                    self?.isGrabbing = false
                    self?.grabMessage = "抓帧失败：没有「照片」写入权限（设置 → 隐私与安全性 → 照片）"
                }
                return
            }
            PHPhotoLibrary.shared().performChanges {
                PHAssetChangeRequest.creationRequestForAsset(from: uiImage)
            } completionHandler: { success, error in
                DispatchQueue.main.async {
                    self?.isGrabbing = false
                    if success {
                        self?.grabMessage = "已抓帧并存入相册"
                    } else {
                        self?.grabMessage = "抓帧保存失败：" + (error?.localizedDescription ?? "未知错误")
                    }
                }
            }
        }
    }
}
