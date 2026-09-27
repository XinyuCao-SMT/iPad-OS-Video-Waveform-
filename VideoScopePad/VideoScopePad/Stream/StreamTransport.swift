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

        var fieldTitle: String {
            switch self {
            case .rtmp: return "服务器地址"
            case .srt:  return "SRT 地址"
            }
        }

        /// 这个协议是否需要单独的「流密钥」栏（RTMP 要，SRT 把 streamid 写在地址里）
        var needsStreamKey: Bool { self == .rtmp }

        var summary: String {
            switch self {
            case .rtmp:
                return "RTMP（由 HaishinKit 实现）：地址写 rtmp://主机:端口/应用，流密钥单独一栏；https 之外的 rtmps:// 也支持。"
            case .srt:
                return "SRT（由 HaishinKit + libsrt 实现）：地址写 srt://主机:端口?mode=caller，串流标识用参数 streamid，例如 srt://1.2.3.4:9000?mode=caller&streamid=live/test。默认端口 9710。"
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

    // MARK: - 内部

    private enum Target {
        case rtmp(RTMPStream)
        case srt(SRTStream)
    }

    private let lock = NSLock()
    private var target: Target?
    private var publishing = false

    let kind: Kind

    private var rtmpConnection: RTMPConnection?
    private var rtmpStream: RTMPStream?
    private var srtConnection: SRTConnection?
    private var srtStream: SRTStream?
    private var statusTask: Task<Void, Never>?

    init(kind: Kind) {
        self.kind = kind
    }

    var isPublishing: Bool {
        lock.lock(); defer { lock.unlock() }
        return publishing
    }

    // MARK: - 地址解析

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

    /// 补上 srt:// 前缀并校验。SRT 的 mode / latency / streamid 都写在 query 里。
    static func parseSRT(urlString: String) -> URL? {
        var text = urlString.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else { return nil }
        if !text.lowercased().hasPrefix("srt://") {
            text = "srt://" + text
        }
        guard let url = URL(string: text), let scheme = url.scheme, scheme == "srt" else { return nil }
        // 至少要有一个主机名或端口，否则连不上
        guard url.host?.isEmpty == false || url.port != nil else { return nil }
        return url
    }

    // MARK: - 开始 / 停止

    func start(urlString: String,
               streamKey: String,
               videoSize: CGSize,
               bitrate: Int,
               frameRate: Int,
               keyframeSeconds: Double) {

        stop()

        let settings = VideoCodecSettings(videoSize: videoSize,
                                          bitRate: max(bitrate, 200_000),
                                          profileLevel: kVTProfileLevel_H264_High_AutoLevel as String,
                                          maxKeyFrameIntervalDuration: Int32(max(keyframeSeconds, 0.5).rounded()),
                                          allowFrameReordering: false,
                                          expectedFrameRate: Double(max(frameRate, 1)))

        switch kind {
        case .rtmp:
            startRTMP(urlString: urlString, streamKey: streamKey, settings: settings)
        case .srt:
            startSRT(urlString: urlString, settings: settings)
        }
    }

    private func startRTMP(urlString: String, streamKey: String, settings: VideoCodecSettings) {
        guard let remote = Self.parseRTMP(urlString: urlString, streamKey: streamKey) else {
            report(.failed("RTMP 地址不完整"),
                   "需要 rtmp://主机[:端口]/应用 这样的地址，并且填上流密钥")
            return
        }

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
            do {
                try await stream.setVideoSettings(settings)
                // RTMPStream 是在 init 里用一个 Task 把自己注册到 connection 的，
                // 而 connect() 只会给**已经注册**的流发 createStream。
                // 这里等一下，避免注册还没落定就去连接（否则 publish 会因为 streamId 没建立而失败）。
                try? await Task.sleep(nanoseconds: 120_000_000)
                _ = try await connection.connect(remote.connectCommand)
                _ = try await stream.publish(remote.streamName)
                await MainActor.run {
                    guard let self else { return }
                    self.markPublishing("已发布 \(remote.display)")
                }
            } catch {
                await MainActor.run {
                    self?.report(.failed("RTMP 连接失败"), error.localizedDescription)
                }
            }
        }
    }

    private func startSRT(urlString: String, settings: VideoCodecSettings) {
        guard let url = Self.parseSRT(urlString: urlString) else {
            report(.failed("SRT 地址不完整"),
                   "需要 srt://主机:端口 这样的地址，例如 srt://192.168.1.10:9000?mode=caller")
            return
        }

        report(.connecting, "连接 \(url.absoluteString) …（SRT 版本 \(SRTConnection.version)）")

        let connection = SRTConnection()
        let stream = SRTStream(connection: connection)
        srtConnection = connection
        srtStream = stream

        Task { [weak self] in
            do {
                try await stream.setVideoSettings(settings)
                // 我们是直接 append 已编码帧的，publish() 里靠 outgoing 判断「有哪些媒体」会落空，
                // 所以显式告诉它只发视频（否则 PAT/PMT 里没有流，接收端看不到画面）。
                await stream.setExpectedMedias([.video])
                try await connection.connect(url)
                await stream.publish("")
                await MainActor.run {
                    guard let self else { return }
                    self.startSRTPolling()
                    self.markPublishing("SRT 已发送到 \(url.host ?? "?")\(url.port.map { ":\($0)" } ?? "")")
                }
            } catch {
                await MainActor.run {
                    self?.report(.failed("SRT 连接失败"), error.localizedDescription)
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

        onState?(.publishing, detail)
        onReadyToPublish?()
    }

    private func markStopped(_ detail: String) {
        lock.lock()
        publishing = false
        target = nil
        lock.unlock()
        onState?(.failed(detail), "推流已停止（采集还在继续，填好地址可以重新开始）")
    }

    private func report(_ state: State, _ detail: String) {
        onState?(state, detail)
    }

    private func handleRTMPStatus(_ status: RTMPStatus) {
        let text = status.description.isEmpty ? status.code : "\(status.code)：\(status.description)"
        switch status.code {
        case "NetStream.Publish.Start":
            markPublishing("已发布，服务器已确认（\(status.code)）")
        case "NetStream.Publish.BadName", "NetConnection.Connect.Rejected",
             "NetConnection.Connect.Failed", "NetStream.Failed", "NetStream.Publish.Denied":
            report(.failed(text), "服务器拒绝了这次发布")
        case "NetConnection.Connect.Closed", "NetStream.Publish.Stop", "NetStream.Connect.Closed":
            markStopped(text)
        default:
            if status.level == "error" {
                report(.failed(text), "服务器返回错误")
            } else {
                report(.connecting, text)
            }
        }
    }
}
