//
//  StreamTransport.swift
//  VideoScopePad
//
//  推流传输层：RTMP 与 SRT 都交给 **HaishinKit**（SPM 依赖，2.2.5）——
//  RTMP 走 RTMPHaishinKit，SRT 走 SRTHaishinKit（内含 libsrt 的 xcframework）。
//
//  本文件只负责「连接 + 发帧 + 报错」，地址怎么来的（表单拼装 or 粘贴解析）
//  由 StreamEndpoint.swift 负责。
//
//  重要：**我们不让 HaishinKit 编码**
//    它的 `RTMPStream.append(_:)` / `SRTStream.append(_:)` 在 sampleBuffer 的
//    formatDescription.isCompressed == true 时，会直接把这一帧当 RTMP 视频消息 /
//    MPEG-TS PES 发出去（不经过它内部的 VideoCodec）。
//    所以流程依旧是：
//        采集帧 → 我们的 VideoEncoder(H.264) ─┬→ MP4Recorder（录制）
//                                            └→ StreamTransport（推流）
//    一次编码，两处共用；也避免了 MediaMixer 那一层额外的离屏渲染。
//
//  失败诊断：
//    HaishinKit 的 SRTConnection.Error / RTMPConnection.Error 都是没有 LocalizedError 的
//    Swift 枚举，直接显示 localizedDescription 只会得到
//    「The operation couldn't be completed. (SRTHaishinKit.SRTConnection.Error error 1.)」
//    这种裸错误码。所以这里把每个分支翻译成中文，并把「填的各栏 / 实际使用的地址 /
//    libsrt 版本 / NSError 的 domain 与 code」写进 diagnostics，界面可一键复制。
//
//  音频仍然不支持：UVC 采集卡的 HDMI 内嵌音频不走视频设备的采集通道。
//

import AVFoundation
import Foundation
import HaishinKit
import RTMPHaishinKit
import SRTHaishinKit
import VideoToolbox

final class StreamTransport {

    // MARK: - 协议

    enum Kind: String, CaseIterable, Identifiable {
        case rtmp
        case srt

        var id: String { rawValue }

        var title: String {
            switch self {
            case .rtmp: return "RTMP"
            case .srt:  return "SRT"
            }
        }

        var summary: String {
            switch self {
            case .rtmp:
                return "RTMP（HaishinKit 实现）：填服务器地址、端口、应用名与流密钥即可，勾上加密走 rtmps://。"
            case .srt:
                return "SRT（HaishinKit + libsrt 实现）：填主机、端口和模式即可，串流标识与密码按服务器要求填。SRT 走 UDP，防火墙要放行 UDP；本 App 会自动补 mode 与 conntimeo。"
            }
        }
    }

    enum State: Equatable {
        case connecting
        case publishing
        case failed(String)
        case closed
    }

    /// 状态回调（已经在主线程上）
    var onState: ((State, String) -> Void)?
    /// 服务器确认发布后回调（用来强制一个关键帧，保证接收端秒开）
    var onReadyToPublish: (() -> Void)?
    /// 失败时把诊断串带出来（界面可复制发给开发助手）
    var onDiagnostics: ((String) -> Void)?

    // MARK: - 内部

    private enum Target {
        case rtmp(RTMPStream)
        case srt(SRTStream)
    }

    private let lock = NSLock()
    private var target: Target?
    private var publishing = false

    let kind: Kind
    private let rtmp: RTMPEndpoint?
    private let srt: SRTEndpoint?
    private let rawInput: String
    private let videoSize: CGSize
    private let bitrate: Int
    private let frameRate: Int
    private let keyframeSeconds: Double

    private var rtmpConnection: RTMPConnection?
    private var rtmpStream: RTMPStream?
    private var srtConnection: SRTConnection?
    private var srtStream: SRTStream?
    private var statusTask: Task<Void, Never>?

    private var resolvedTargetText = "(未解析)"
    private var phase = "初始化"

    init(kind: Kind,
         rtmp: RTMPEndpoint?,
         srt: SRTEndpoint?,
         rawInput: String,
         videoSize: CGSize,
         bitrate: Int,
         frameRate: Int,
         keyframeSeconds: Double) {
        self.kind = kind
        self.rtmp = rtmp
        self.srt = srt
        self.rawInput = rawInput
        self.videoSize = videoSize
        self.bitrate = bitrate
        self.frameRate = frameRate
        self.keyframeSeconds = keyframeSeconds
    }

    var isPublishing: Bool {
        lock.lock(); defer { lock.unlock() }
        return publishing
    }

    // MARK: - SRT 链路统计（带宽 / 延迟）

    struct SRTStatistics {
        /// 往返时延（毫秒）
        var rttMs: Double
        /// libsrt 估算的可用带宽（Mb/s）
        var bandwidthMbps: Double
        /// 实际发送速率（Mb/s）
        var sendRateMbps: Double
        /// 发送侧累计丢包 / 重传（重传多说明链路在丢包）
        var lostPackets: Int
        var retransmittedPackets: Int
    }

    /// 只有 SRT 有链路统计；未连接或走 RTMP 时返回 nil
    func srtStatistics() async -> SRTStatistics? {
        guard let connection = srtConnection else { return nil }
        guard let data = await connection.performanceData else { return nil }
        return SRTStatistics(rttMs: data.msRTT,
                             bandwidthMbps: data.mbpsBandwidth,
                             sendRateMbps: data.mbpsSendRate,
                             lostPackets: Int(data.pktSndLossTotal),
                             retransmittedPackets: Int(data.pktRetransTotal))
    }

    // MARK: - 开始 / 停止

    func start() {
        stop()

        phase = "准备编码参数"
        let settings = VideoCodecSettings(videoSize: videoSize,
                                          bitRate: max(bitrate, 200_000),
                                          profileLevel: kVTProfileLevel_H264_High_AutoLevel as String,
                                          maxKeyFrameIntervalDuration: Int32(max(keyframeSeconds, 0.5).rounded()),
                                          allowFrameReordering: false,
                                          expectedFrameRate: Double(max(frameRate, 1)))

        switch kind {
        case .rtmp:
            startRTMP(settings: settings)
        case .srt:
            startSRT(settings: settings)
        }
    }

    private func startRTMP(settings: VideoCodecSettings) {
        guard let remote = rtmp else {
            let message = "RTMP 地址不完整"
            phase = "地址解析失败"
            resolvedTargetText = "(解析失败)"
            report(.failed(message),
                   "请填服务器地址，并填上流密钥（应用名可以留空，默认 live）",
                   diagnostics: diagnosticsText(error: message, detail: "各栏拼装不出可用地址；输入：\(rawInput)"))
            return
        }

        resolvedTargetText = remote.display
        phase = "连接服务器"
        report(.connecting, "连接 \(remote.connectCommand) …")

        let connection = RTMPConnection()
        let stream = RTMPStream(connection: connection)
        rtmpConnection = connection
        rtmpStream = stream

        // 服务器的 NetStatus 事件（发布成功 / 失败原因）
        statusTask = Task { [weak self] in
            for await status in await stream.status {
                guard let self else { return }
                await MainActor.run { self.handleRTMPStatus(status) }
            }
        }

        Task { [weak self] in
            guard let self else { return }
            do {
                try await stream.setVideoSettings(settings)
                // RTMPStream 是在 init 里用一个 Task 把自己注册到 connection 的，
                // 而 connect() 只会给**已经注册**的流发 createStream。
                // 这里等一下，避免注册还没落定就去连接（否则 publish 会因为 streamId 没建立而失败）。
                try? await Task.sleep(nanoseconds: 120_000_000)
                try await Self.withTimeout(seconds: 20, label: "RTMP 连接/发布超时（20 秒）") {
                    _ = try await connection.connect(remote.connectCommand)
                    _ = try await stream.publish(remote.streamName)
                }
                await MainActor.run {
                    self.phase = "已发布"
                    self.markPublishing("已发布 \(remote.display)")
                }
            } catch {
                await MainActor.run {
                    let text = Self.describe(error)
                    self.phase = "失败"
                    self.report(.failed("RTMP 连接失败"), text,
                                diagnostics: self.diagnosticsText(error: "RTMP 连接失败", detail: text))
                }
            }
        }
    }

    private func startSRT(settings: VideoCodecSettings) {
        guard let remote = srt else {
            let message = "SRT 地址不完整"
            phase = "地址解析失败"
            resolvedTargetText = "(解析失败)"
            report(.failed(message),
                   "请填主机名或 IP（监听模式只需端口），端口要 1–65535 之间的数字",
                   diagnostics: diagnosticsText(error: message, detail: "各栏拼装不出可用地址；输入：\(rawInput)"))
            return
        }

        resolvedTargetText = remote.display
        phase = "连接服务器"
        let targetText = remote.host.isEmpty
            ? "监听端口 \(remote.port)"
            : "\(remote.host):\(remote.port)"
        report(.connecting, "连接 \(remote.display) …（libsrt \(SRTConnection.version)）")

        let connection = SRTConnection()
        let stream = SRTStream(connection: connection)
        srtConnection = connection
        srtStream = stream

        Task { [weak self] in
            guard let self else { return }
            do {
                try await stream.setVideoSettings(settings)
                // 我们是直接 append 已编码帧的，publish() 里靠 outgoing 判断「有哪些媒体」会落空，
                // 所以显式告诉它只发视频（否则 PAT/PMT 里没有流，接收端看不到画面）。
                await stream.setExpectedMedias([.video])

                self.phase = "建立 SRT 连接"
                try await Self.withTimeout(seconds: 20, label: "SRT 连接超时（20 秒）") {
                    try await connection.connect(remote.url)
                }

                let connected = await connection.connected
                guard connected else {
                    let text = "socket 已建立但 connected == false（对端未确认握手）"
                    self.phase = "失败"
                    await MainActor.run {
                        self.report(.failed("SRT 连接失败"), text,
                                    diagnostics: self.diagnosticsText(error: "SRT 连接失败", detail: text))
                    }
                    return
                }

                self.phase = "发布"
                await stream.publish("")

                await MainActor.run {
                    self.phase = "已发送"
                    self.startSRTPolling()
                    let streamIDSuffix = remote.streamID.map { "（streamid=\($0)）" } ?? ""
                    self.markPublishing("SRT 已发送到 \(targetText)\(streamIDSuffix)")
                }
            } catch {
                await MainActor.run {
                    self.phase = "失败"
                    let text = Self.describeSRT(error)
                    self.report(.failed("SRT 连接失败"), text,
                                diagnostics: self.diagnosticsText(error: "SRT 连接失败", detail: text))
                }
            }
        }
    }

    /// SRT 没有 NetStatus 事件，靠定期读 readyState / connected 反馈状态。
    private func startSRTPolling() {
        statusTask = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(nanoseconds: 1_500_000_000)
                guard let self, let connection = self.srtConnection, let stream = self.srtStream else { return }
                let connected = await connection.connected
                let ready = await stream.readyState
                await MainActor.run {
                    guard self.srtStream != nil else { return }
                    if !connected {
                        self.markStopped("SRT 连接已断开")
                    } else if ready == .publishing {
                        // 保持 publishing 状态即可，不刷界面
                    }
                }
            }
        }
    }

    /// 送一帧**已编码**的 H.264（来自我们的 VideoEncoder）
    func append(_ sampleBuffer: CMSampleBuffer) {
        lock.lock()
        let current = publishing ? target : nil
        lock.unlock()

        switch current {
        case .rtmp(let stream):
            Task { await stream.append(sampleBuffer) }
        case .srt(let stream):
            Task { await stream.append(sampleBuffer) }
        case nil:
            break
        }
    }

    func stop() {
        statusTask?.cancel()
        statusTask = nil

        let rtmpStreamRef = rtmpStream
        let rtmpConnectionRef = rtmpConnection
        let srtStreamRef = srtStream
        let srtConnectionRef = srtConnection

        rtmpStream = nil
        rtmpConnection = nil
        srtStream = nil
        srtConnection = nil

        lock.lock()
        publishing = false
        target = nil
        lock.unlock()

        Task {
            if let rtmpStreamRef { try? await rtmpStreamRef.close() }
            if let rtmpConnectionRef { try? await rtmpConnectionRef.close() }
            if let srtStreamRef { await srtStreamRef.close() }
            if let srtConnectionRef { await srtConnectionRef.close() }
        }
    }

    // MARK: - 状态

    private func markPublishing(_ detail: String) {
        lock.lock()
        publishing = true
        if rtmpStream != nil {
            target = rtmpStream.map { .rtmp($0) }
        } else if srtStream != nil {
            target = srtStream.map { .srt($0) }
        }
        lock.unlock()

        onState?(.publishing, detail)
        onReadyToPublish?()
    }

    private func markStopped(_ detail: String) {
        lock.lock()
        publishing = false
        target = nil
        lock.unlock()
        onState?(.failed(detail), "推流已停止（采集还在继续，改好设置可以重新开始）")
    }

    private func report(_ state: State, _ detail: String, diagnostics: String? = nil) {
        onState?(state, detail)
        if let diagnostics {
            onDiagnostics?(diagnostics)
        }
    }

    private func handleRTMPStatus(_ status: RTMPStatus) {
        let text = status.description.isEmpty ? status.code : "\(status.code)：\(status.description)"
        switch status.code {
        case "NetStream.Publish.Start":
            phase = "已发布"
            markPublishing("已发布，服务器已确认（\(status.code)）")
        case "NetStream.Publish.BadName", "NetConnection.Connect.Rejected",
             "NetConnection.Connect.Failed", "NetStream.Failed", "NetStream.Publish.Denied":
            phase = "失败"
            report(.failed(text), "服务器拒绝了这次发布",
                   diagnostics: diagnosticsText(error: text, detail: "服务器 NetStatus 拒绝"))
        case "NetConnection.Connect.Closed", "NetStream.Publish.Stop", "NetStream.Connect.Closed":
            markStopped(text)
        default:
            if status.level == "error" {
                phase = "失败"
                report(.failed(text), "服务器返回错误",
                       diagnostics: diagnosticsText(error: text, detail: "服务器 NetStatus error"))
            } else {
                report(.connecting, text)
            }
        }
    }

    // MARK: - 超时看门狗

    private struct TimeoutError: Error, LocalizedError {
        let label: String
        var errorDescription: String? { label }
    }

    /// 给 connect/publish 这类可能长时间不返回的调用加个上限，避免界面一直停在「连接中」。
    private static func withTimeout(seconds: Double,
                                    label: String,
                                    _ work: @escaping () async throws -> Void) async throws {
        try await withThrowingTaskGroup(of: Void.self) { group in
            group.addTask { try await work() }
            group.addTask {
                try await Task.sleep(nanoseconds: UInt64(seconds * 1_000_000_000))
                throw TimeoutError(label: label)
            }
            defer { group.cancelAll() }
            try await group.next()
        }
    }

    // MARK: - 错误翻译

    /// 把 HaishinKit 的裸错误码翻译成人话（SRT 专用，包含服务器拒绝原因）
    static func describeSRT(_ error: Error) -> String {
        if let srtError = error as? SRTConnection.Error {
            switch srtError {
            case .invalidState:
                return "连不上服务器：SRT 套接字建立失败或被中断。SRT 走的是 **UDP**（不是 TCP），请确认主机/端口、服务器已在运行、防火墙放行 UDP。"
            case .unsupportedUri(let uri):
                return "地址被库拒绝：'\(uri?.absoluteString ?? "nil")'（scheme=\(uri?.scheme ?? "nil")）。正常情况下本 App 拼出来的地址一定是小写 srt://，出现这个请把诊断信息发我。"
            case .failedToConnect(let reason):
                return "服务器无法建立/拒绝连接：\(describe(reason))"
            }
        }
        if error is TimeoutError {
            return error.localizedDescription + "。检查主机/端口是否正确、服务器是否在监听 UDP、以及是否有防火墙/NAT 拦截。"
        }
        let ns = error as NSError
        return "\(ns.domain) error \(ns.code)：\(error.localizedDescription)"
    }

    static func describe(_ error: Error) -> String {
        if error is TimeoutError {
            return error.localizedDescription
        }
        let ns = error as NSError
        return "\(ns.domain) error \(ns.code)：\(error.localizedDescription)"
    }

    /// SRT 服务器拒绝/超时的原因码（对应 libsrt 的 SRT_REJ_*）
    static func describe(_ reason: SRTRejectReason) -> String {
        switch reason {
        case .unknown:    return "未知原因（可能是对端没有监听该端口）"
        case .system:     return "对端系统错误"
        case .peer:       return "对端拒绝（常见于串流标识 streamid 不对、或服务器不允许推流）"
        case .resource:   return "对端资源不足"
        case .rogue:      return "被对端判定为异常连接"
        case .backlog:    return "服务器连接队列已满"
        case .ipe:        return "对端内部错误"
        case .close:      return "连接被关闭"
        case .version:    return "SRT 版本不兼容"
        case .rdvcookie:  return "握手 cookie 校验失败"
        case .badsecret:  return "密码（passphrase）不对"
        case .unsecure:   return "对端要求加密，但本次是明文（请填密码）"
        case .messageapi: return "消息模式不匹配"
        case .congestion: return "网络拥塞被拒"
        case .filter:     return "被对端的过滤器拒绝"
        case .group:      return "组播/组配置问题"
        case .timeout:    return "对端超时（主机或端口很可能不对）"
        case .crypto:     return "加密参数不匹配"
        @unknown default: return "未知拒绝原因（\(reason.rawValue)）"
        }
    }

    // MARK: - 诊断串

    private func diagnosticsText(error: String, detail: String) -> String {
        let formatter = DateFormatter()
        formatter.dateFormat = "yyyy-MM-dd HH:mm:ss"
        var lines: [String] = []
        lines.append("=== VideoScopePad 推流诊断 ===")
        lines.append("时间: \(formatter.string(from: Date()))")
        lines.append("协议: \(kind.title)")
        if !rawInput.isEmpty {
            lines.append("地址输入: \(rawInput)")
        }
        lines.append("失败阶段: \(phase)")
        lines.append("错误: \(error)")
        lines.append("说明: \(detail)")

        switch kind {
        case .rtmp:
            if let remote = rtmp {
                lines.append("实际连接: \(remote.connectCommand)")
                lines.append("  主机=\(remote.host)  端口=\(remote.port)  应用=\(remote.app)  加密=\(remote.secure ? "是" : "否")")
                lines.append("  流名=\(remote.streamName)")
            } else {
                lines.append("  各栏拼装不出可用地址（主机为空，或流密钥为空）")
            }
        case .srt:
            lines.append("libsrt 版本: \(SRTConnection.version)")
            if let remote = srt {
                lines.append("实际连接: \(remote.display)")
                lines.append("  主机=\(remote.host.isEmpty ? "(监听模式，不需填)" : remote.host)  端口=\(remote.port)")
                lines.append("  模式=\(remote.mode.shortName)  串流标识=\(remote.streamID ?? "(未填)")")
                lines.append("  延迟=\(remote.latency.map { "\($0)ms" } ?? "(未填)")  加密=\(remote.isEncrypted ? "passphrase/\(remote.keyLength ?? 16) 位" : "无")")
                lines.append("  连接超时=\(remote.connectTimeout ?? 5000)ms")
            } else {
                lines.append("  各栏拼装不出可用地址（主机为空，或端口不是 1–65535 的数字）")
            }
        }

        lines.append("视频: \(Int(videoSize.width))x\(Int(videoSize.height)) @\(frameRate)fps, \(bitrate / 1_000_000) Mb/s, 关键帧 \(keyframeSeconds)s")
        lines.append("编码: VideoToolbox H.264（AVCC 直通，HaishinKit 不再二次编码）")
        return lines.joined(separator: "\n")
    }
}
