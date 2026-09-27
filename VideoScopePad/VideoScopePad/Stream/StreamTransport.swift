//
//  StreamTransport.swift
//  VideoScopePad
//
//  推流传输层：RTMP 与 SRT 都交给 **HaishinKit**（SPM 依赖，2.2.5）——
//  RTMP 走 RTMPHaishinKit，SRT 走 SRTHaishinKit（内含 libsrt 的 xcframework）。
//
//  为什么换成 HaishinKit：
//    · RTMP 我先手写过一版（握手 + AMF0 + FLV 封装），能编译但没对真实服务器联调过；
//    · SRT 是 libsrt 那套 ARQ 重传 + 加密 + 握手，不是几百行能写对的，必须用库。
//    HaishinKit 两个协议都实现，而且支持 `rtmps://`，省掉自己维护协议栈。
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
//  失败诊断（v1.4.1 起）：
//    HaishinKit 的 SRTConnection.Error / RTMPConnection.Error 都是没有 LocalizedError 的
//    Swift 枚举，直接显示 localizedDescription 只会得到
//    「The operation couldn't be completed. (SRTHaishinKit.SRTConnection.Error error 1.)」
//    这种裸错误码。所以这里把每个分支翻译成中文，并把「用户输入的原文 / 解析出来的 URL 与
//    scheme / libsrt 版本 / NSError 的 domain 与 code」一起写进 diagnostics，界面可一键复制。
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

        var placeholder: String {
            switch self {
            case .rtmp: return "rtmp://主机:1935/应用"
            case .srt:  return "srt://主机:9000?mode=caller"
            }
        }

        /// 这个协议是否需要单独的「流密钥」栏（RTMP 要，SRT 把 streamid 写在地址里）
        var needsStreamKey: Bool { self == .rtmp }

        var summary: String {
            switch self {
            case .rtmp:
                return "RTMP（由 HaishinKit 实现）：地址写 rtmp://主机:端口/应用，流密钥单独一栏；rtmps:// 也支持。"
            case .srt:
                return "SRT（由 HaishinKit + libsrt 实现）：地址写 srt://主机:端口，串流标识用参数 streamid（不同服务器写法不同：SRS 常用 streamid=live/xxx，OBS/MediaMTX 常用 streamid=publish:live/xxx）。默认端口 9710；本 App 会自动补 mode=caller 与 conntimeo=5000。"
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
    private let inputURL: String
    private let streamKey: String
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
    private var lastStatusText = ""

    init(kind: Kind,
         inputURL: String,
         streamKey: String,
         videoSize: CGSize,
         bitrate: Int,
         frameRate: Int,
         keyframeSeconds: Double) {
        self.kind = kind
        self.inputURL = inputURL
        self.streamKey = streamKey
        self.videoSize = videoSize
        self.bitrate = bitrate
        self.frameRate = frameRate
        self.keyframeSeconds = keyframeSeconds
    }

    var isPublishing: Bool {
        lock.lock(); defer { lock.unlock() }
        return publishing
    }

    // MARK: - RTMP 地址解析

    struct RTMPTarget {
        /// 传给 RTMPConnection.connect 的命令：rtmp://主机:端口/应用
        var connectCommand: String
        /// 传给 RTMPStream.publish 的流名
        var streamName: String
        /// 显示用
        var display: String
    }

    /// 接受 rtmp://主机[:端口]/应用[/流名]；流密钥栏填了就优先用它。
    static func parseRTMP(urlString: String, streamKey: String) -> RTMPTarget? {
        var text = urlString.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else { return nil }
        // 同上：全角字符（中文输入法）先折成半角
        text = text.folding(options: [.widthInsensitive], locale: nil)
            .trimmingCharacters(in: .whitespacesAndNewlines)

        var secure = false
        for prefix in ["rtmps://", "rtmp://"] where text.lowercased().hasPrefix(prefix) {
            secure = (prefix == "rtmps://")
            text.removeFirst(prefix.count)
            break
        }

        let parts = text.split(separator: "/", omittingEmptySubsequences: true).map(String.init)
        guard let hostPart = parts.first, !hostPart.isEmpty else { return nil }

        var host = hostPart
        var port: UInt16 = secure ? 443 : 1935
        if let colon = hostPart.lastIndex(of: ":") {
            host = String(hostPart[hostPart.startIndex..<colon])
            if let parsed = UInt16(hostPart[hostPart.index(after: colon)...]) {
                port = parsed
            }
        }

        var app = "live"
        var stream = streamKey.trimmingCharacters(in: .whitespacesAndNewlines)

        if parts.count >= 3 {
            app = parts[1]
            if stream.isEmpty {
                stream = parts.dropFirst(2).joined(separator: "/")
            }
        } else if parts.count == 2, stream.isEmpty {
            stream = parts[1]
        }

        guard !host.isEmpty, !stream.isEmpty else { return nil }

        let scheme = secure ? "rtmps" : "rtmp"
        let command = "\(scheme)://\(host):\(port)/\(app)"
        return RTMPTarget(connectCommand: command,
                          streamName: stream,
                          display: "\(command)/\(stream)")
    }

    // MARK: - SRT 地址解析

    struct SRTTarget {
        var url: URL
        var host: String
        var port: Int?
        var mode: String
        var streamID: String?
        var display: String
    }

    /// 把用户输入变成**保证 scheme 是 `srt`** 的 URL。
    ///
    /// 为什么要自己拼：HaishinKit 的 `SRTSocketURL` 只做一件事 ——
    /// `guard url.scheme == "srt"`，不满足就直接抛出 `SRTConnection.Error.unsupportedUri`
    /// （在界面上表现为 `... error 1.` 这种裸错误码）。它**不会**帮你补前缀、也不认大小写不同的
    /// scheme，所以这里统一：去掉不可见字符 → 剥掉已有前缀 → 手工拼 `host:port?query` →
    /// 用小写 `srt://` 重新组装，并顺手补上 `mode` 与 `conntimeo` 默认值。
    static func parseSRT(urlString: String) -> SRTTarget? {
        var text = urlString.trimmingCharacters(in: .whitespacesAndNewlines)
        // 中文输入法很容易打出全角字符（：／ｓｒｔ１９２…），统一折成半角再解析
        text = text.folding(options: [.widthInsensitive], locale: nil)
        // 从聊天软件/备忘录粘贴时常带这些零宽字符，它们既不是空白也没法进 URL
        for junk in ["\u{200B}", "\u{FEFF}", "\u{200E}", "\u{200F}", "\u{2060}"] {
            text = text.replacingOccurrences(of: junk, with: "")
        }
        text = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else { return nil }

        var body = text
        if let range = body.range(of: "srt://", options: [.caseInsensitive, .anchored]) {
            body = String(body[range.upperBound...])
        } else if let range = body.range(of: "srt:", options: [.caseInsensitive, .anchored]) {
            body = String(body[range.upperBound...])
            if body.hasPrefix("//") { body.removeFirst(2) }
        }

        var query = ""
        if let mark = body.firstIndex(of: "?") {
            query = String(body[body.index(after: mark)...])
            body = String(body[..<mark])
        }
        // srt://host:port/live/xxx 这种「路径写法」在 SRT 里没有语法意义，
        // 但不少人会这么粘地址；这里把路径当 streamid 用（SRS 一类服务器正好是这个约定）。
        var pathStreamID = ""
        if let slash = body.firstIndex(of: "/") {
            pathStreamID = String(body[body.index(after: slash)...])
            body = String(body[..<slash])
        }

        var host = ""
        var port: Int?
        if body.hasPrefix("[") {
            if let close = body.firstIndex(of: "]") {
                host = String(body[body.index(after: body.startIndex)..<close])
                let rest = body[body.index(after: close)...]
                if rest.hasPrefix(":") { port = Int(rest.dropFirst()) }
            }
        } else if let colon = body.lastIndex(of: ":") {
            host = String(body[..<colon]).trimmingCharacters(in: .whitespaces)
            port = Int(String(body[body.index(after: colon)...]).trimmingCharacters(in: .whitespaces))
        } else {
            host = body.trimmingCharacters(in: .whitespaces)
        }

        guard !host.isEmpty || port != nil else { return nil }

        // query 参数：HaishinKit 是按名字匹配 SRTSocketOption 的，未知参数会被忽略
        var pairs: [(String, String)] = []
        var seen: Set<String> = []
        for chunk in query.split(separator: "&") {
            let pieces = chunk.split(separator: "=", maxSplits: 1, omittingEmptySubsequences: false)
            let name = String(pieces.first ?? "")
            guard !name.isEmpty, !seen.contains(name) else { continue }
            let value = pieces.count > 1 ? String(pieces[1]) : ""
            pairs.append((name, value))
            seen.insert(name)
        }

        let mode = pairs.first { $0.0 == "mode" }?.1 ?? (host.isEmpty ? "listener" : "caller")
        if !seen.contains("mode") { pairs.append(("mode", mode)) }
        // 地址里写的路径（host:port/xxx）当 streamid 用
        if !seen.contains("streamid"), !pathStreamID.isEmpty {
            pairs.append(("streamid", pathStreamID))
            seen.insert("streamid")
        }
        // conntimeo 是「连接超时（毫秒）」，默认 3 秒对跨网/跨机房偏短，这里放宽到 5 秒
        if !seen.contains("conntimeo") { pairs.append(("conntimeo", "5000")) }

        let rebuilt = pairs.map { "\($0.0)=\($0.1)" }.joined(separator: "&")
        var urlText = "srt://"
        urlText += host
        if let port { urlText += ":\(port)" }
        if !rebuilt.isEmpty { urlText += "?" + rebuilt }

        guard let url = URL(string: urlText) else { return nil }
        // scheme 是我们自己拼的小写 srt，这里再核一次（万一被系统改写）
        guard url.scheme?.lowercased() == "srt" else { return nil }

        return SRTTarget(url: url,
                         host: host,
                         port: port,
                         mode: mode,
                         streamID: pairs.first { $0.0 == "streamid" }?.1,
                         display: urlText)
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
        guard let remote = Self.parseRTMP(urlString: inputURL, streamKey: streamKey) else {
            let message = "RTMP 地址不完整"
            phase = "地址解析失败"
            resolvedTargetText = "(解析失败)"
            report(.failed(message),
                   "需要 rtmp://主机[:端口]/应用 这样的地址，并且填上流密钥",
                   diagnostics: diagnosticsText(error: message, detail: "地址解析返回 nil"))
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
        guard let remote = Self.parseSRT(urlString: inputURL) else {
            let message = "SRT 地址不完整"
            phase = "地址解析失败"
            resolvedTargetText = "(解析失败)"
            report(.failed(message),
                   "需要 srt://主机:端口 这样的地址，例如 srt://192.168.1.10:9000?mode=caller",
                   diagnostics: diagnosticsText(error: message, detail: "地址解析返回 nil；原始输入：\(inputURL)"))
            return
        }

        resolvedTargetText = remote.display
        phase = "连接服务器"
        let targetText = "\(remote.host.isEmpty ? "(监听模式)" : remote.host)\(remote.port.map { ":\($0)" } ?? "")"
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
                    self.markPublishing("SRT 已发送到 \(targetText)\(remote.streamID.map { "（streamid=\($0)）" } ?? "")")
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
                        self.lastStatusText = "SRT 发送中"
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

        let rtmp = rtmpStream
        let rtmpC = rtmpConnection
        let srt = srtStream
        let srtC = srtConnection

        rtmpStream = nil
        rtmpConnection = nil
        srtStream = nil
        srtConnection = nil

        lock.lock()
        publishing = false
        target = nil
        lock.unlock()

        Task {
            if let rtmp { try? await rtmp.close() }
            if let rtmpC { try? await rtmpC.close() }
            if let srt { await srt.close() }
            if let srtC { await srtC.close() }
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

        lastStatusText = detail
        onState?(.publishing, detail)
        onReadyToPublish?()
    }

    private func markStopped(_ detail: String) {
        lock.lock()
        publishing = false
        target = nil
        lock.unlock()
        lastStatusText = detail
        onState?(.failed(detail), "推流已停止（采集还在继续，填好地址可以重新开始）")
    }

    private func report(_ state: State, _ detail: String, diagnostics: String? = nil) {
        lastStatusText = detail
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
                return "连不上服务器：SRT 套接字建立失败或被中断。SRT 走的是 **UDP**（不是 TCP），请确认地址/端口、服务器已在运行、防火墙放行 UDP。"
            case .unsupportedUri(let uri):
                return "地址被库拒绝：'\(uri?.absoluteString ?? "nil")'（scheme=\(uri?.scheme ?? "nil")）。正常情况下本 App 已经强制写成小写 srt://，出现这个请把诊断信息发我。"
            case .failedToConnect(let reason):
                return "服务器无法建立/拒绝连接：\(describe(reason))"
            }
        }
        if error is TimeoutError {
            return error.localizedDescription + "。检查 IP/端口是否正确、服务器是否在监听 UDP、以及是否有防火墙/NAT 拦截。"
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
        case .peer:       return "对端拒绝（常见于 streamid 不对、或服务器不允许推流）"
        case .resource:   return "对端资源不足"
        case .rogue:      return "被对端判定为异常连接"
        case .backlog:    return "服务器连接队列已满"
        case .ipe:        return "对端内部错误"
        case .close:      return "连接被关闭"
        case .version:    return "SRT 版本不兼容"
        case .rdvcookie:  return "握手 cookie 校验失败"
        case .badsecret:  return "密码（passphrase）不对"
        case .unsecure:   return "对端要求加密，但本次是明文"
        case .messageapi: return "消息模式不匹配"
        case .congestion: return "网络拥塞被拒"
        case .filter:     return "被对端的过滤器拒绝"
        case .group:      return "组播/组配置问题"
        case .timeout:    return "对端超时（地址或端口很可能不对）"
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
        lines.append("输入原文: \(inputURL.isEmpty ? "(空)" : inputURL)")
        if kind == .rtmp, !streamKey.isEmpty {
            lines.append("流密钥: \(streamKey)")
        }
        lines.append("解析结果: \(resolvedTargetText)")
        lines.append("失败阶段: \(phase)")
        lines.append("错误: \(error)")
        lines.append("说明: \(detail)")
        if kind == .srt {
            lines.append("libsrt 版本: \(SRTConnection.version)")
            if let remote = Self.parseSRT(urlString: inputURL) {
                lines.append("  scheme=\(remote.url.scheme ?? "nil")")
                lines.append("  host=\(remote.host.isEmpty ? "(空)" : remote.host)  port=\(remote.port.map(String.init) ?? "(默认 9710)")")
                lines.append("  mode=\(remote.mode)  streamid=\(remote.streamID ?? "(未设置)")")
                lines.append("  完整地址=\(remote.url.absoluteString)")
            } else {
                lines.append("  地址仍无法解析（原始输入里可能有不合法字符）")
            }
        }
        lines.append("视频: \(Int(videoSize.width))x\(Int(videoSize.height)) @\(frameRate)fps, \(bitrate / 1_000_000) Mb/s, 关键帧 \(keyframeSeconds)s")
        lines.append("编码: VideoToolbox H.264（AVCC 直通，HaishinKit 不再二次编码）")
        return lines.joined(separator: "\n")
    }
}
