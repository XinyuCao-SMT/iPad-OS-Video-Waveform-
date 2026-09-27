//
//  CaptureController.swift
//  VideoScopePad
//
//  采集卡（UVC）接入与帧分发。
//
//  iPadOS 17 起系统才通过 AVFoundation 暴露外接 UVC 视频设备
//  （AVCaptureDevice.DeviceType.external），因此本工程最低 iPadOS 17.0，
//  且必须使用 USB-C 接口的 iPad（iPad mini 6 及以后）。
//
//  线程模型：
//   - sessionQueue：设备枚举、格式协商、会话启停（AVCaptureSession 的全部操作）
//   - videoQueue  ：视频帧回调（零拷贝 CVPixelBuffer 直接交给渲染层）
//   - main        ：所有 @Published 状态的读写
//  会话相关状态一律保存在 sessionQueue 私有的 session* 变量中，避免跨线程读写 @Published。
//

import AVFoundation
import Combine
import CoreMedia
import CoreVideo
import Foundation
import os

/// AVFoundation 通知名统一使用字符串常量，
/// 避免不同 Xcode SDK 对 Swift 重命名（wasConnectedNotification 等）不一致导致编译失败。
enum CaptureNotifications {
    static let deviceConnected = Notification.Name("AVCaptureDeviceWasConnectedNotification")
    static let deviceDisconnected = Notification.Name("AVCaptureDeviceWasDisconnectedNotification")
    static let sessionRuntimeError = Notification.Name("AVCaptureSessionRuntimeErrorNotification")
    static let sessionInterrupted = Notification.Name("AVCaptureSessionWasInterruptedNotification")
    static let sessionInterruptionEnded = Notification.Name("AVCaptureSessionInterruptionEndedNotification")
}

final class CaptureController: NSObject, ObservableObject {

    // MARK: - 对外状态（主线程）

    @Published private(set) var devices: [CaptureDeviceInfo] = []
    @Published private(set) var formats: [VideoFormatInfo] = []
    @Published private(set) var stats = CaptureStats()
    @Published private(set) var selectedDeviceID: String?
    @Published private(set) var selectedFormatID: String?
    @Published private(set) var videoSize = CGSize(width: 1920, height: 1080)
    @Published private(set) var colorMatrix: ColorMatrixKind = .bt709
    /// 当前输出是否为视频范围（16-235）；BGRA 输出时为全范围
    @Published private(set) var isVideoRange = true
    @Published private(set) var statusMessage: String?
    @Published private(set) var isAuthorized = AVCaptureDevice.authorizationStatus(for: .video) == .authorized
    @Published private(set) var hasExternalDevice = false

    /// 每一帧回调（在 videoQueue 上）——零拷贝 CVPixelBuffer
    var onFrame: ((CVPixelBuffer) -> Void)?

    let session = AVCaptureSession()

    // MARK: - 私有

    private let sessionQueue = DispatchQueue(label: "com.videoscopepad.capture.session")
    private let videoQueue = DispatchQueue(label: "com.videoscopepad.capture.video", qos: .userInteractive)
    private let log = Logger(subsystem: "com.videoscopepad", category: "capture")

    // 仅 sessionQueue 访问
    private var deviceMap: [String: AVCaptureDevice] = [:]
    private var sessionFormats: [VideoFormatInfo] = []
    private var sessionDeviceID: String?
    private var sessionFormatID: String?
    private var videoOutput: AVCaptureVideoDataOutput?

    // 统计
    private let counterLock = NSLock()
    private var frameCounter = 0
    private var droppedCounter = 0
    private var wantsRunning = false

    // 仅主线程访问
    private var statsTimer: Timer?
    private var lastFrameCount = 0
    private var observersInstalled = false

    // MARK: - 生命周期

    func start() {
        installObserversIfNeeded()
        startStatsTimer()

        switch AVCaptureDevice.authorizationStatus(for: .video) {
        case .authorized:
            isAuthorized = true
            setWantsRunning(true)
            sessionQueue.async { [weak self] in
                guard let self else { return }
                self.reloadDevicesLocked()
                self.configureLocked()
                self.startRunningLocked()
            }

        case .notDetermined:
            AVCaptureDevice.requestAccess(for: .video) { [weak self] granted in
                DispatchQueue.main.async {
                    guard let self else { return }
                    self.isAuthorized = granted
                    if granted {
                        self.start()
                    } else {
                        self.statusMessage = "没有相机权限，无法读取采集卡信号。请在「设置 → 隐私与安全性 → 相机」里允许本应用。"
                    }
                }
            }

        default:
            isAuthorized = false
            statusMessage = "没有相机权限，无法读取采集卡信号。请在「设置 → 隐私与安全性 → 相机」里允许本应用。"
        }
    }

    func pause() {
        setWantsRunning(false)
        sessionQueue.async { [weak self] in
            guard let self else { return }
            if self.session.isRunning {
                self.session.stopRunning()
            }
            DispatchQueue.main.async { self.stats.isRunning = false }
        }
    }

    func resume() {
        setWantsRunning(true)
        sessionQueue.async { [weak self] in
            guard let self else { return }
            self.reloadDevicesLocked()
            self.configureLocked()
            self.startRunningLocked()
        }
    }

    func stop() {
        setWantsRunning(false)
        DispatchQueue.main.async { self.stopStatsTimer() }
        sessionQueue.async { [weak self] in
            guard let self else { return }
            if self.session.isRunning {
                self.session.stopRunning()
            }
            DispatchQueue.main.async { self.stats.isRunning = false }
        }
    }

    // MARK: - 选择设备 / 格式

    func select(deviceID: String) {
        selectedDeviceID = deviceID
        sessionQueue.async { [weak self] in
            guard let self else { return }
            self.sessionDeviceID = deviceID
            self.session.stopRunning()
            self.loadFormatsLocked(deviceID: deviceID, preferredFormatID: nil)
            self.configureLocked()
            self.startRunningLocked()
        }
    }

    func select(formatID: String) {
        selectedFormatID = formatID
        sessionQueue.async { [weak self] in
            guard let self else { return }
            self.sessionFormatID = formatID
            self.session.stopRunning()
            self.configureLocked()
            self.startRunningLocked()
        }
    }

    /// 手动刷新设备列表（用户点「刷新」）
    func refresh() {
        sessionQueue.async { [weak self] in
            guard let self else { return }
            self.reloadDevicesLocked()
            self.session.stopRunning()
            self.configureLocked()
            self.startRunningLocked()
        }
    }

    // MARK: - 会话配置（全部在 sessionQueue 上执行）

    private func reloadDevicesLocked() {
        var types: [AVCaptureDevice.DeviceType] = [.external]
        types.append(.builtInWideAngleCamera)

        let discovery = AVCaptureDevice.DiscoverySession(deviceTypes: types,
                                                         mediaType: .video,
                                                         position: .unspecified)

        var map: [String: AVCaptureDevice] = [:]
        var infos: [CaptureDeviceInfo] = []

        for device in discovery.devices {
            let isExternal = device.deviceType == .external
            map[device.uniqueID] = device
            infos.append(CaptureDeviceInfo(id: device.uniqueID,
                                           name: device.localizedName,
                                           isExternal: isExternal,
                                           modelID: device.modelID))
        }

        infos.sort { lhs, rhs in
            if lhs.isExternal != rhs.isExternal { return lhs.isExternal }
            return lhs.name.localizedStandardCompare(rhs.name) == .orderedAscending
        }

        deviceMap = map

        if sessionDeviceID == nil || map[sessionDeviceID!] == nil {
            sessionDeviceID = infos.first?.id
        }

        loadFormatsLocked(deviceID: sessionDeviceID, preferredFormatID: sessionFormatID)

        let selectedDevice = sessionDeviceID
        let deviceInfos = infos
        let formatInfos = sessionFormats
        let selectedFormat = sessionFormats.first { $0.id == sessionFormatID } ?? sessionFormats.first

        DispatchQueue.main.async {
            self.devices = deviceInfos
            self.hasExternalDevice = deviceInfos.contains { $0.isExternal }
            self.selectedDeviceID = selectedDevice
            self.formats = formatInfos
            self.selectedFormatID = selectedFormat?.id
            if let selectedFormat {
                self.videoSize = CGSize(width: selectedFormat.width, height: selectedFormat.height)
                self.colorMatrix = selectedFormat.colorMatrix
                self.isVideoRange = selectedFormat.isVideoRange
            }
            self.updateStatusMessageLocked()
        }
    }

    private func loadFormatsLocked(deviceID: String?, preferredFormatID: String?) {
        guard let deviceID, let device = deviceMap[deviceID] else {
            sessionFormats = []
            sessionFormatID = nil
            return
        }
        let list = Self.makeFormatList(for: device)
        sessionFormats = list
        if let preferredFormatID, list.contains(where: { $0.id == preferredFormatID }) {
            sessionFormatID = preferredFormatID
        } else {
            sessionFormatID = list.first?.id
        }
    }

    private func configureLocked() {
        session.beginConfiguration()
        defer { session.commitConfiguration() }

        session.sessionPreset = .inputPriority

        for input in session.inputs {
            session.removeInput(input)
        }
        for output in session.outputs {
            session.removeOutput(output)
        }
        videoOutput = nil

        guard let deviceID = sessionDeviceID, let device = deviceMap[deviceID] else {
            log.error("没有可用的采集设备")
            return
        }

        do {
            let input = try AVCaptureDeviceInput(device: device)
            guard session.canAddInput(input) else {
                log.error("无法添加输入设备")
                return
            }
            session.addInput(input)
        } catch {
            log.error("创建输入失败: \(error.localizedDescription)")
            let text = error.localizedDescription
            DispatchQueue.main.async { self.statusMessage = "无法打开设备：\(text)" }
            return
        }

        let format = sessionFormats.first { $0.id == sessionFormatID } ?? sessionFormats.first

        let output = AVCaptureVideoDataOutput()
        output.alwaysDiscardsLateVideoFrames = true
        output.videoSettings = [
            kCVPixelBufferPixelFormatTypeKey as String: format?.requestedPixelFormat ?? OSType(kCVPixelFormatType_32BGRA),
            kCVPixelBufferMetalCompatibilityKey as String: true,
            kCVPixelBufferIOSurfacePropertiesKey as String: [String: Any]()
        ]
        output.setSampleBufferDelegate(self, queue: videoQueue)

        guard session.canAddOutput(output) else {
            log.error("无法添加视频输出")
            return
        }
        session.addOutput(output)
        videoOutput = output

        do {
            try device.lockForConfiguration()

            if let format, format.formatIndex < device.formats.count {
                device.activeFormat = device.formats[format.formatIndex]
            }

            if let format {
                let rate = format.frameRate
                let supported = device.activeFormat.videoSupportedFrameRateRanges.contains {
                    rate >= $0.minFrameRate - 0.01 && rate <= $0.maxFrameRate + 0.01
                }
                if supported {
                    let timescale = CMTimeScale((rate * 1000).rounded())
                    let duration = CMTime(value: 1000, timescale: max(timescale, 1))
                    device.activeVideoMinFrameDuration = duration
                    device.activeVideoMaxFrameDuration = duration
                }
            }

            device.unlockForConfiguration()
        } catch {
            log.error("配置设备失败: \(error.localizedDescription)")
        }

        // 仅内置摄像头支持方向设置；外接 UVC 设备会忽略
        if let connection = output.connection(with: .video), connection.isVideoOrientationSupported {
            connection.videoOrientation = .landscapeRight
        }

        if let format {
            DispatchQueue.main.async {
                self.videoSize = CGSize(width: format.width, height: format.height)
                self.colorMatrix = format.colorMatrix
                self.isVideoRange = format.isVideoRange
            }
        }
    }

    private func startRunningLocked() {
        guard wantsRunning else { return }
        if !session.isRunning {
            session.startRunning()
        }
        let running = session.isRunning
        DispatchQueue.main.async { self.stats.isRunning = running }
    }

    // MARK: - 格式枚举

    private static func makeFormatList(for device: AVCaptureDevice) -> [VideoFormatInfo] {
        let candidateRates: [Double] = [60, 59.94, 50, 30, 29.97, 25, 24, 23.976]
        var infos: [VideoFormatInfo] = []

        for (index, format) in device.formats.enumerated() {
            let dimensions = CMVideoFormatDescriptionGetDimensions(format.formatDescription)
            let subtype = CMFormatDescriptionGetMediaSubType(format.formatDescription)
            let pixelFormat = CaptureFormatHelper.requestedPixelFormat(nativeSubtype: subtype)
            let matrix = CaptureFormatHelper.colorMatrix(for: format.formatDescription)
            let ranges = format.videoSupportedFrameRateRanges

            var rates: [Double] = []
            for rate in candidateRates where ranges.contains(where: {
                rate >= $0.minFrameRate - 0.01 && rate <= $0.maxFrameRate + 0.01
            }) {
                rates.append(rate)
            }
            if rates.isEmpty, let maxRate = ranges.map({ $0.maxFrameRate }).max() {
                rates = [maxRate.rounded()]
            }

            for rate in rates {
                infos.append(VideoFormatInfo(id: "\(index)-\(Int((rate * 1000).rounded()))",
                                             formatIndex: index,
                                             width: Int(dimensions.width),
                                             height: Int(dimensions.height),
                                             frameRate: rate,
                                             subtype: subtype,
                                             requestedPixelFormat: pixelFormat,
                                             colorMatrix: matrix))
            }
        }

        var seen = Set<String>()
        var unique: [VideoFormatInfo] = []
        for info in infos {
            let key = "\(info.width)x\(info.height)@\(info.frameRate)-\(info.subtype)"
            if seen.contains(key) { continue }
            seen.insert(key)
            unique.append(info)
        }

        unique.sort { lhs, rhs in
            func rank(_ info: VideoFormatInfo) -> Int {
                if info.height == 1080 { return 0 }
                if info.height > 1080 { return 1 }
                return 2
            }
            let lr = rank(lhs), rr = rank(rhs)
            if lr != rr { return lr < rr }
            if abs(lhs.frameRate - rhs.frameRate) > 0.01 { return lhs.frameRate > rhs.frameRate }
            return lhs.width * lhs.height > rhs.width * rhs.height
        }

        return unique
    }

    // MARK: - 状态文案

    private func updateStatusMessageLocked() {
        if devices.isEmpty {
            statusMessage = "未检测到视频输入设备。请把支持 UVC 的采集卡插到 iPad 的 USB-C 口（供电不足时请用带外部供电的扩展坞）。"
        } else if !hasExternalDevice {
            statusMessage = "当前只找到内置摄像头，没有检测到 UVC 采集卡。"
        } else {
            statusMessage = nil
        }
    }

    // MARK: - 统计

    private func startStatsTimer() {
        guard statsTimer == nil else { return }
        let timer = Timer(timeInterval: 0.5, repeats: true) { [weak self] _ in
            self?.publishStats()
        }
        RunLoop.main.add(timer, forMode: .common)
        statsTimer = timer
    }

    private func stopStatsTimer() {
        statsTimer?.invalidate()
        statsTimer = nil
    }

    private func publishStats() {
        counterLock.lock()
        let frames = frameCounter
        let dropped = droppedCounter
        counterLock.unlock()

        let delta = max(frames - lastFrameCount, 0)
        lastFrameCount = frames

        var updated = stats
        updated.fps = Double(delta) / 0.5
        updated.droppedFrames = dropped
        updated.isRunning = session.isRunning

        if let format = formats.first(where: { $0.id == selectedFormatID }) {
            updated.width = format.width
            updated.height = format.height
            updated.pixelFormatText = format.pixelFormatText + (format.isVideoRange ? " 视频范围" : " 全范围")
            updated.colorMatrixTitle = format.colorMatrix.title
        }

        if updated != stats {
            stats = updated
        }
    }

    private func setWantsRunning(_ value: Bool) {
        counterLock.lock()
        wantsRunning = value
        counterLock.unlock()
    }

    private func currentWantsRunning() -> Bool {
        counterLock.lock()
        defer { counterLock.unlock() }
        return wantsRunning
    }

    // MARK: - 通知

    private func installObserversIfNeeded() {
        guard !observersInstalled else { return }
        observersInstalled = true

        let center = NotificationCenter.default
        center.addObserver(self, selector: #selector(handleDeviceListChanged),
                           name: CaptureNotifications.deviceConnected, object: nil)
        center.addObserver(self, selector: #selector(handleDeviceListChanged),
                           name: CaptureNotifications.deviceDisconnected, object: nil)
        center.addObserver(self, selector: #selector(handleRuntimeError(_:)),
                           name: CaptureNotifications.sessionRuntimeError, object: session)
        center.addObserver(self, selector: #selector(handleInterruption),
                           name: CaptureNotifications.sessionInterrupted, object: session)
        center.addObserver(self, selector: #selector(handleInterruptionEnded),
                           name: CaptureNotifications.sessionInterruptionEnded, object: session)
    }

    @objc private func handleDeviceListChanged() {
        sessionQueue.async { [weak self] in
            guard let self else { return }
            self.reloadDevicesLocked()
            self.session.stopRunning()
            self.configureLocked()
            self.startRunningLocked()
        }
    }

    @objc private func handleRuntimeError(_ note: Notification) {
        let error = note.userInfo?[AVCaptureSessionErrorKey] as? Error
        let text = error?.localizedDescription ?? "未知错误"
        log.error("会话运行错误: \(text)")

        DispatchQueue.main.async {
            self.statusMessage = "采集会话中断：\(text)，正在重试…"
        }

        sessionQueue.async { [weak self] in
            guard let self, self.currentWantsRunning() else { return }
            self.session.stopRunning()
            self.configureLocked()
            self.startRunningLocked()
        }
    }

    @objc private func handleInterruption() {
        DispatchQueue.main.async {
            self.statusMessage = "采集被系统中断（通常是采集卡被其它应用占用）。"
        }
    }

    @objc private func handleInterruptionEnded() {
        DispatchQueue.main.async { self.statusMessage = nil }
        sessionQueue.async { [weak self] in
            self?.startRunningLocked()
        }
    }
}

// MARK: - AVCaptureVideoDataOutputSampleBufferDelegate

extension CaptureController: AVCaptureVideoDataOutputSampleBufferDelegate {

    func captureOutput(_ output: AVCaptureOutput,
                       didOutput sampleBuffer: CMSampleBuffer,
                       from connection: AVCaptureConnection) {
        guard let pixelBuffer = CMSampleBufferGetImageBuffer(sampleBuffer) else { return }

        counterLock.lock()
        frameCounter += 1
        counterLock.unlock()

        onFrame?(pixelBuffer)
    }

    func captureOutput(_ output: AVCaptureOutput,
                       didDrop sampleBuffer: CMSampleBuffer,
                       from connection: AVCaptureConnection) {
        counterLock.lock()
        droppedCounter += 1
        counterLock.unlock()
    }
}
