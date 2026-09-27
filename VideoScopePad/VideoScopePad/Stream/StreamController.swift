//
//  StreamController.swift
//  VideoScopePad
//
//  录制 / 推流的总调度：
//     采集帧 → VideoEncoder(H.264) ─┬→ MP4Recorder（本机录制）
//                                   └→ StreamTransport（推流：RTMP / SRT，走 HaishinKit）
//
//  说明：
//    · 录制与推流共用同一次编码结果（MP4 用直通写入，推流把同一份 CMSampleBuffer
//      交给 HaishinKit，它检测到 isCompressed 就直接封装，不会二次编码）
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
    /// 失败时的诊断串（面板上可一键复制发给开发助手）
    @Published private(set) var diagnostics = ""
    private var diagnosticsBase = ""
    private var networkDiagnostics = ""
    @Published private(set) var diagnosticsTitle = ""
    @Published private(set) var diagnosticsCopied = false
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

    // MARK: 推流协议

    /// 推流协议：RTMP 或 SRT
    @Published var transportKind: StreamTransport.Kind = .rtmp {
        didSet {
            UserDefaults.standard.set(transportKind.rawValue, forKey: "vsp.stream.kind")
            // 协议换了，正在推的话先停掉，避免状态错乱
            if isStreaming || isPreparing { stopStreaming() }
        }
    }

    // MARK: RTMP 地址（拆成各栏填，避免手打完整 URL 被符号坑到）

    @Published var rtmpHost: String = "" {
        didSet { UserDefaults.standard.set(rtmpHost, forKey: "vsp.stream.rtmp.host") }
    }
    @Published var rtmpPort: String = "" {
        didSet { UserDefaults.standard.set(rtmpPort, forKey: "vsp.stream.rtmp.port") }
    }
    @Published var rtmpApp: String = "" {
        didSet { UserDefaults.standard.set(rtmpApp, forKey: "vsp.stream.rtmp.app") }
    }
    @Published var rtmpSecure = false {
        didSet { UserDefaults.standard.set(rtmpSecure, forKey: "vsp.stream.rtmp.secure") }
    }
    @Published var streamKey: String = "" {
        didSet { UserDefaults.standard.set(streamKey, forKey: "vsp.stream.key") }
    }

    // MARK: SRT 地址（同样拆开）

    @Published var srtHost: String = "" {
        didSet { UserDefaults.standard.set(srtHost, forKey: "vsp.stream.srt.host") }
    }
    @Published var srtPort: String = "" {
        didSet { UserDefaults.standard.set(srtPort, forKey: "vsp.stream.srt.port") }
    }
    @Published var srtMode: SRTModeOption = .caller {
        didSet { UserDefaults.standard.set(srtMode.rawValue, forKey: "vsp.stream.srt.mode") }
    }
    @Published var srtStreamID: String = "" {
        didSet { UserDefaults.standard.set(srtStreamID, forKey: "vsp.stream.srt.streamid") }
    }
    @Published var srtLatency: String = "" {
        didSet { UserDefaults.standard.set(srtLatency, forKey: "vsp.stream.srt.latency") }
    }
    @Published var srtPassphrase: String = "" {
        didSet { UserDefaults.standard.set(srtPassphrase, forKey: "vsp.stream.srt.passphrase") }
    }
    /// 加密位数：0 = 不加密（只在填了密码时有效）
    @Published var srtKeyLength: Int = 16 {
        didSet { UserDefaults.standard.set(srtKeyLength, forKey: "vsp.stream.srt.keylen") }
    }
    @Published var srtConnectTimeout: String = "" {
        didSet { UserDefaults.standard.set(srtConnectTimeout, forKey: "vsp.stream.srt.conntimeo") }
    }

    /// 「粘贴完整地址自动填入」用的临时输入（不持久化）
    @Published var importText: String = ""
    @Published private(set) var importMessage: String?

    init() {
        let defaults = UserDefaults.standard
        if let value = defaults.object(forKey: "vsp.stream.bitrate") as? Double { bitrateMbps = value }
        if let value = defaults.object(forKey: "vsp.stream.keyframe") as? Double { keyframeSeconds = value }
        if let raw = defaults.string(forKey: "vsp.stream.kind"),
           let kind = StreamTransport.Kind(rawValue: raw) {
            transportKind = kind
        }

        // 兼容 v1.3/v1.4 的旧键（那时是「一整条 URL」）
        if let legacyKey = defaults.string(forKey: "vsp.stream.key") { streamKey = legacyKey }
        let legacyRTMP = defaults.string(forKey: "vsp.stream.rtmpURL") ?? ""
        let legacySRT = defaults.string(forKey: "vsp.stream.srtURL") ?? ""

        rtmpHost = defaults.string(forKey: "vsp.stream.rtmp.host") ?? ""
        rtmpPort = defaults.string(forKey: "vsp.stream.rtmp.port") ?? ""
        rtmpApp = defaults.string(forKey: "vsp.stream.rtmp.app") ?? ""
        rtmpSecure = defaults.bool(forKey: "vsp.stream.rtmp.secure")

        srtHost = defaults.string(forKey: "vsp.stream.srt.host") ?? ""
        srtPort = defaults.string(forKey: "vsp.stream.srt.port") ?? ""
        if let raw = defaults.string(forKey: "vsp.stream.srt.mode"),
           let mode = SRTModeOption(rawValue: raw) {
            srtMode = mode
        }
        srtStreamID = defaults.string(forKey: "vsp.stream.srt.streamid") ?? ""
        srtLatency = defaults.string(forKey: "vsp.stream.srt.latency") ?? ""
        srtPassphrase = defaults.string(forKey: "vsp.stream.srt.passphrase") ?? ""
        if defaults.object(forKey: "vsp.stream.srt.keylen") != nil {
            srtKeyLength = defaults.integer(forKey: "vsp.stream.srt.keylen")
        }
        srtConnectTimeout = defaults.string(forKey: "vsp.stream.srt.conntimeo") ?? ""

        // 旧设置迁移：把一整条 URL 拆到各栏里
        if rtmpHost.isEmpty, !legacyRTMP.isEmpty,
           let endpoint = RTMPEndpoint.parse(urlString: legacyRTMP, streamKey: streamKey) {
            rtmpHost = endpoint.host
            rtmpPort = String(endpoint.port)
            rtmpApp = endpoint.app
            rtmpSecure = endpoint.secure
            if streamKey.isEmpty { streamKey = endpoint.streamName }
        }
        if srtHost.isEmpty, !legacySRT.isEmpty, let endpoint = SRTEndpoint.parse(urlString: legacySRT) {
            srtHost = endpoint.host
            srtPort = String(endpoint.port)
            srtMode = endpoint.mode
            srtStreamID = endpoint.streamID ?? ""
            srtLatency = endpoint.latency.map(String.init) ?? ""
            srtPassphrase = endpoint.passphrase ?? ""
            if let bits = endpoint.keyLength { srtKeyLength = bits }
        }
    }

    // MARK: - 地址预览与校验（界面直接显示）

    /// 当前协议、当前各栏拼出来的 endpoint（拼不出来就是 nil）
    var currentRTMPEndpoint: RTMPEndpoint? {
        RTMPEndpoint.make(host: rtmpHost,
                          port: rtmpPort,
                          app: rtmpApp,
                          streamKey: streamKey,
                          secure: rtmpSecure)
    }

    var currentSRTEndpoint: SRTEndpoint? {
        SRTEndpoint.make(host: srtHost,
                         port: srtPort,
                         mode: srtMode,
                         streamID: srtStreamID,
                         latency: srtLatency,
                         passphrase: srtPassphrase,
                         keyLength: srtKeyLength,
                         connectTimeout: srtConnectTimeout)
    }

    /// 界面上的「将连接」预览
    var addressPreview: String {
        switch transportKind {
        case .rtmp:
            return currentRTMPEndpoint?.display ?? "（还没填全：需要服务器地址 + 流密钥）"
        case .srt:
            return currentSRTEndpoint?.display ?? "（还没填全：需要主机名或 IP；端口要 1–65535 的数字）"
        }
    }

    /// 地址栏的问题（有问题时界面标红提示）
    var addressProblem: String? {
        switch transportKind {
        case .rtmp:
            let host = rtmpHost.trimmingCharacters(in: .whitespacesAndNewlines)
            if host.isEmpty { return "请填服务器地址（主机名或 IP）" }
            if currentRTMPEndpoint == nil {
                if streamKey.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                    return "请填流密钥，或者把完整地址（rtmp://主机/应用/流密钥）粘进「服务器地址」栏"
                }
                return "端口要是 1–65535 之间的数字"
            }
            return nil
        case .srt:
            if srtMode != .listener, srtHost.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                return "请填主机名或 IP（监听模式可以留空）"
            }
            if EndpointSanitizer.port(srtPort, default: 9710) == nil {
                return "端口要是 1–65535 之间的数字"
            }
            if EndpointSanitizer.optionalInt(srtLatency, range: 20...8000) == nil,
               !srtLatency.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                return "延迟要是 20–8000 之间的整数（毫秒），留空则不设置"
            }
            if EndpointSanitizer.optionalInt(srtConnectTimeout, range: 1000...60000) == nil,
               !srtConnectTimeout.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                return "连接超时要是 1000–60000 之间的整数（毫秒），留空则用 5000"
            }
            if currentSRTEndpoint == nil { return "地址拼装失败，请检查主机与端口" }
            return nil
        }
    }

    /// 把「粘贴完整地址」那一栏解析到各栏里
    func importAddress() {
        let text = importText.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else {
            importMessage = "先粘贴一条完整地址"
            return
        }
        switch transportKind {
        case .rtmp:
            guard let endpoint = RTMPEndpoint.parse(urlString: text, streamKey: streamKey) else {
                importMessage = "解析失败：RTMP 地址形如 rtmp://主机:1935/应用/流密钥"
                return
            }
            rtmpHost = endpoint.host
            rtmpPort = String(endpoint.port)
            rtmpApp = endpoint.app
            rtmpSecure = endpoint.secure
            streamKey = endpoint.streamName
            importMessage = "已填入：\(endpoint.display)"
        case .srt:
            guard let endpoint = SRTEndpoint.parse(urlString: text) else {
                importMessage = "解析失败：SRT 地址形如 srt://主机:9000?mode=caller&streamid=live/test"
                return
            }
            srtHost = endpoint.host
            srtPort = String(endpoint.port)
            srtMode = endpoint.mode
            srtStreamID = endpoint.streamID ?? ""
            srtLatency = endpoint.latency.map(String.init) ?? ""
            srtPassphrase = endpoint.passphrase ?? ""
            if let bits = endpoint.keyLength { srtKeyLength = bits }
            importMessage = "已填入：\(endpoint.display)"
        }
    }

    func clearImportMessage() {
        importMessage = nil
    }

    // MARK: - 内部

    private var encoder: VideoEncoder?
    private var encoderSourceFormat: OSType = 0
    private var encoderWidth = 0
    private var encoderHeight = 0
    /// 编码/推流使用的帧率（采集卡给出的声明帧率拿不到时按 60 处理）
    private var encoderFrameRate: Double = 60

    private var recorder: MP4Recorder?
    private var transport: StreamTransport?

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
                                           frameRate: Int(max(encoderFrameRate, 1)),
                                           keyframeIntervalSeconds: keyframeSeconds,
                                           sourcePixelFormat: format)
            created.onFrame = { [weak self] frame in
                self?.handleEncodedFrame(frame)
            }
            // 参数集（SPS/PPS）由 HaishinKit 从 sampleBuffer 的 formatDescription 里自己取，
            // 不需要我们再拼 FLV 的 AVC sequence header
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
        if let transport, transport.isPublishing {
            transport.append(frame.sampleBuffer)
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

    // MARK: - 推流（RTMP / SRT，走 HaishinKit）

    func startStreaming() {
        guard !isStreaming else { return }

        if let problem = addressProblem {
            lastError = problem
            return
        }

        lastError = nil
        isPreparing = true
        statusText = "连接服务器…"
        diagnostics = ""
        diagnosticsTitle = ""

        let created = StreamTransport(kind: transportKind,
                                      rtmp: currentRTMPEndpoint,
                                      srt: currentSRTEndpoint,
                                      rawInput: addressPreview,
                                      videoSize: CGSize(width: max(encoderWidth, 1280), height: max(encoderHeight, 720)),
                                      bitrate: Int(max(bitrateMbps, 0.5) * 1_000_000),
                                      frameRate: Int(max(encoderFrameRate, 1)),
                                      keyframeSeconds: keyframeSeconds)
        created.onState = { [weak self] state, detail in
            DispatchQueue.main.async {
                guard let self else { return }
                if !detail.isEmpty { self.statusText = detail }
                switch state {
                case .connecting:
                    break
                case .publishing:
                    self.isStreaming = true
                    self.isPreparing = false
                case .failed(let message):
                    self.lastError = message
                    self.isStreaming = false
                    self.isPreparing = false
                    self.stopEncoderIfIdle()
                case .closed:
                    self.isStreaming = false
                    self.isPreparing = false
                }
            }
        }
        created.onReadyToPublish = { [weak self] in
            // 服务器确认后强制一个关键帧，接收端秒开
            self?.encoder?.requestKeyframe()
        }
        created.onDiagnostics = { [weak self] text in
            DispatchQueue.main.async {
                guard let self else { return }
                self.setDiagnostics(text)
                // 失败之后再做一次网络层面的探测（本机地址 / 是否同网段 / UDP 能不能通），
                // 结果追加到诊断串末尾 —— 这几层的问题比协议本身更常见。
                self.attachNetworkDiagnostics()
            }
        }
        transport = created
        created.start()
    }

    private func setDiagnostics(_ text: String) {
        diagnosticsBase = text
        networkDiagnostics = ""
        diagnostics = text
        // 标题取「协议 + 时间」，方便在面板上一眼看出是哪一次尝试
        let formatter = DateFormatter()
        formatter.dateFormat = "HH:mm:ss"
        diagnosticsTitle = "\(transportKind.title) · \(formatter.string(from: Date()))"
    }

    /// 追加一段网络层面的探测结果（本机地址、是否同网段、UDP 是否可达）。
    /// 这几层的问题（局域网权限、跨网段、防火墙丢 UDP）比协议本身更常见。
    private func attachNetworkDiagnostics() {
        let target: (host: String, port: Int)? = {
            switch transportKind {
            case .rtmp:
                guard let endpoint = currentRTMPEndpoint else { return nil }
                return (host: endpoint.host, port: endpoint.port)
            case .srt:
                guard let endpoint = currentSRTEndpoint else { return nil }
                return (host: endpoint.host, port: endpoint.port)
            }
        }()

        let locals = NetworkDiagnostics.localIPv4Addresses()
        var lines: [String] = ["=== 网络探测 ==="]
        lines.append("本机地址: \(locals.isEmpty ? "(没读到，可能是接口未就绪)" : locals.joined(separator: ", "))")

        guard let target, !target.host.isEmpty else {
            lines.append("（没有可探测的目标主机）")
            networkDiagnostics = lines.joined(separator: "\n")
            rebuildDiagnostics()
            return
        }

        var sameSubnet: Bool?
        if let localPrefix = NetworkDiagnostics.localSubnetPrefix(),
           let remotePrefix = NetworkDiagnostics.subnetPrefix(of: target.host) {
            let same = (localPrefix == remotePrefix)
            sameSubnet = same
            if same {
                lines.append("网段: 本机 \(localPrefix).x 与服务器 \(remotePrefix).x 相同 ✓")
            } else {
                lines.append("网段: 本机 \(localPrefix).x ≠ 服务器 \(remotePrefix).x —— 不在同一网段，"
                             + "UDP 一般路由不过去（先确认服务器地址，或把 iPad 换到同一网络）")
            }
        }

        lines.append(Self.probeMarker)
        networkDiagnostics = lines.joined(separator: "\n")
        rebuildDiagnostics()

        let host = target.host
        let port = target.port
        Task { [weak self] in
            let result = await NetworkDiagnostics.probeUDP(host: host, port: port)
            await MainActor.run {
                guard let self else { return }
                var block = self.networkDiagnostics
                    .replacingOccurrences(of: Self.probeMarker, with: "UDP 探测 \(host):\(port): \(result)")
                block += "\n结论: " + Self.conclusion(for: result, sameSubnet: sameSubnet)
                self.networkDiagnostics = block
                self.rebuildDiagnostics()
            }
        }
    }

    private static let probeMarker = "UDP 探测: 进行中…"

    /// 按「探测结果 + 是否同网段」给一句能直接照做的结论
    private static func conclusion(for result: String, sameSubnet: Bool?) -> String {
        if result.contains("收到") {
            return "UDP 这一层是通的 —— 问题在 SRT 握手本身：检查服务器的串流标识 streamid / 密码 / 是否允许推流，"
                + "或换成服务器要求的模式（多数服务器要「呼叫 caller」）。"
        }
        if result.contains("明确拒绝") {
            return "主机可达但该端口没有 UDP 服务 —— 端口很可能填错了（对照服务器配置里的监听端口）。"
        }
        if sameSubnet == false {
            return "不同网段且无回应 —— 先把 iPad 与服务器放到同一网段（同一路由器/交换机），或确认服务器地址是内网地址。"
        }
        if sameSubnet == true {
            return "网段相同却完全没回应 —— 按可能性依次排查：①iPad 上「设置 → 隐私与安全性 → 本地网络」里"
                + "允许 VideoScopePad（iOS 14 起访问局域网设备必须授权，没授权时数据包会被静默丢弃，表现就是一直超时）；"
                + "②服务器（Windows 防火墙 / Linux iptables）放行 UDP 该端口；③服务器确实在监听该端口（SRS/MediaMTX/OBS 的 SRT 监听）。"
        }
        return "无回应 —— 确认服务器是否在监听该 UDP 端口、防火墙是否放行、以及 iPad 是否已获「本地网络」权限。"
    }

    private func rebuildDiagnostics() {
        diagnostics = networkDiagnostics.isEmpty
            ? diagnosticsBase
            : diagnosticsBase + "\n\n" + networkDiagnostics
    }

    func stopStreaming() {
        transport?.stop()
        transport = nil
        isStreaming = false
        isPreparing = false
        statusText = isRecording ? "录制中" : "空闲"
        stopEncoderIfIdle()
    }

    /// 复制诊断串到剪贴板
    func copyDiagnostics() {
        guard !diagnostics.isEmpty else { return }
        UIPasteboard.general.string = diagnostics
        diagnosticsCopied = true
        let reset = DispatchQueue.main
        reset.asyncAfter(deadline: .now() + 2) { [weak self] in
            self?.diagnosticsCopied = false
        }
    }

    func clearDiagnostics() {
        diagnostics = ""
        diagnosticsBase = ""
        networkDiagnostics = ""
        diagnosticsTitle = ""
        diagnosticsCopied = false
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
