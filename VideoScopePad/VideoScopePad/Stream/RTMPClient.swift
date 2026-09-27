//
//  RTMPClient.swift
//  VideoScopePad
//
//  纯 Swift 的最小 RTMP 推流客户端（视频 only）。
//
//  流程（用显式状态机，避免漏步）：
//    TCP 连接 → RTMP 握手（C0/C1/C2 ↔ S0/S1/S2）
//      → connect    → 等 _result
//      → createStream → 等 _result（拿到 stream id）
//      → publish    → 等 onStatus(NetStream.Publish.Start)
//      → 发 Set Chunk Size(4096) + AVC sequence header(SPS/PPS) → 之后逐帧发 NALU
//
//  ⚠️ 诚实说明：这套协议代码是在 Windows 上写的，**没有对着真实服务器联调过**。
//     编译与静态检查都过，但第一次连你的服务器可能需要调一轮（界面会显示状态与错误）。
//
//  SRT 不在这里：SRT 是 libsrt 那套 ARQ/加密/握手，手写不现实，需要第三方库（见 NEXT-STEPS.md）。
//

import Foundation
import Network

final class RTMPClient {

    enum Status: Equatable {
        case idle
        case connecting
        case handshaking
        case negotiating
        case publishing
        case failed(String)

        var text: String {
            switch self {
            case .idle: return "未连接"
            case .connecting: return "连接中…"
            case .handshaking: return "握手中…"
            case .negotiating: return "协商中…"
            case .publishing: return "推流中"
            case .failed(let message): return "失败：" + message
            }
        }
    }

    private enum Phase {
        case handshake
        case connectSent
        case createStreamSent
        case publishSent
    }

    var onStatus: ((Status) -> Void)?
    /// 服务器确认 publish 之后再发 sequence header 与视频帧
    var onReadyToPublish: (() -> Void)?

    private let queue = DispatchQueue(label: "com.videoscopepad.rtmp")
    private var connection: NWConnection?

    private var host = ""
    private var port: UInt16 = 1935
    private var app = "live"
    private var streamName = ""

    private var receiveBuffer = Data()
    private var phase: Phase = .handshake
    private var chunkSize = 128
    private var streamID: UInt32 = 1
    private(set) var isPublishing = false

    // MARK: - 连接 / 断开

    func connect(urlString: String, streamKey: String) {
        guard let target = Self.parse(urlString: urlString, streamKey: streamKey) else {
            onStatus?(.failed("地址格式不对，应为 rtmp://主机:端口/应用/流名"))
            return
        }
        guard let nwPort = NWEndpoint.Port(rawValue: target.port) else {
            onStatus?(.failed("端口无效"))
            return
        }

        host = target.host
        port = target.port
        app = target.app
        streamName = target.stream
        receiveBuffer = Data()
        phase = .handshake
        chunkSize = 128
        streamID = 1
        isPublishing = false

        onStatus?(.connecting)
        let connection = NWConnection(host: NWEndpoint.Host(host), port: nwPort, using: .tcp)
        self.connection = connection

        connection.stateUpdateHandler = { [weak self] state in
            guard let self else { return }
            switch state {
            case .ready:
                self.onStatus?(.handshaking)
                self.sendHandshake()
            case .failed(let error):
                self.onStatus?(.failed(error.localizedDescription))
            default:
                break
            }
        }
        connection.start(queue: queue)
        receiveLoop()
    }

    func disconnect() {
        isPublishing = false
        phase = .handshake
        connection?.cancel()
        connection = nil
        onStatus?(.idle)
    }

    // MARK: - 底层收发

    private func send(_ data: Data, label: String) {
        guard let connection else { return }
        connection.send(content: data, completion: .contentProcessed { [weak self] error in
            if let error {
                self?.onStatus?(.failed(label + "：" + error.localizedDescription))
            }
        })
    }

    private func receiveLoop() {
        connection?.receive(minimumIncompleteLength: 1, maximumLength: 65536) { [weak self] data, _, isComplete, error in
            guard let self else { return }
            if let data, !data.isEmpty {
                self.receiveBuffer.append(data)
                self.processBuffer()
            }
            if let error {
                self.onStatus?(.failed(error.localizedDescription))
                return
            }
            if isComplete {
                self.onStatus?(.failed("服务器关闭了连接"))
                return
            }
            self.receiveLoop()
        }
    }

    // MARK: - 协议推进

    private func processBuffer() {
        if phase == .handshake {
            // S0(1) + S1(1536) + S2(1536)
            guard receiveBuffer.count >= 1 + 1536 + 1536 else { return }
            let s1 = receiveBuffer.subdata(in: (receiveBuffer.startIndex + 1)..<(receiveBuffer.startIndex + 1 + 1536))
            receiveBuffer.removeSubrange(0..<(1 + 1536 + 1536))

            send(s1, label: "发送 C2")      // C2 = 回显 S1
            sendConnectCommand()
            return
        }
        consumeChunks()
    }

    private func consumeChunks() {
        while true {
            guard receiveBuffer.count >= 12 else { return }
            let first = receiveBuffer[receiveBuffer.startIndex]
            let format = (first >> 6) & 0x03
            guard format == 0 else { return }          // 只处理 fmt=0 的消息头

            let base = receiveBuffer.startIndex
            let messageLength = Int(Self.readUInt24(receiveBuffer, at: 4))
            let messageType = receiveBuffer[base + 7]
            let messageStreamID = UInt32(receiveBuffer[base + 8])
                | (UInt32(receiveBuffer[base + 9]) << 8)
                | (UInt32(receiveBuffer[base + 10]) << 16)
                | (UInt32(receiveBuffer[base + 11]) << 24)

            guard messageLength > 0, receiveBuffer.count >= 12 + messageLength else { return }

            let payload = receiveBuffer.subdata(in: (base + 12)..<(base + 12 + messageLength))
            receiveBuffer.removeSubrange(0..<(12 + messageLength))

            _ = messageStreamID

            if messageType == 20 {                     // AMF0 命令
                handleCommand(payload)
            }
        }
    }

    private func handleCommand(_ payload: Data) {
        let command = AMF0.firstString(in: payload) ?? ""

        switch command {
        case "_result":
            switch phase {
            case .connectSent:
                sendCreateStreamCommand()
            case .createStreamSent:
                if let value = AMF0.firstNumber(in: payload, skippingFirst: 1), value >= 1 {
                    streamID = UInt32(value)
                }
                sendPublishCommand()
            default:
                break
            }

        case "onStatus":
            // 期望 NetStream.Publish.Start
            if !isPublishing {
                isPublishing = true
                onStatus?(.publishing)
                sendSetChunkSize(4096)
                chunkSize = 4096
                onReadyToPublish?()
            }

        case "_error":
            onStatus?(.failed("服务器拒绝：" + (AMF0.firstString(in: payload) ?? "未知原因")))

        default:
            break
        }
    }

    // MARK: - 命令

    private func sendConnectCommand() {
        var payload = Data()
        payload.append(AMF0.string("connect"))
        payload.append(AMF0.number(1))
        payload.append(AMF0.object([
            "app": app,
            "type": "nonprivate",
            "tcUrl": "rtmp://\(host):\(port)/\(app)",
            "flashVer": "FMLE/3.0 (compatible; FMSc/1.0)",
            "fpad": false,
            "capabilities": 15
        ]))
        sendMessage(type: 20, streamID: 0, timestamp: 0, payload: payload)
        phase = .connectSent
    }

    private func sendCreateStreamCommand() {
        var payload = Data()
        payload.append(AMF0.string("createStream"))
        payload.append(AMF0.number(2))
        payload.append(AMF0.null())
        sendMessage(type: 20, streamID: 0, timestamp: 0, payload: payload)
        phase = .createStreamSent
    }

    private func sendPublishCommand() {
        var payload = Data()
        payload.append(AMF0.string("publish"))
        payload.append(AMF0.number(3))
        payload.append(AMF0.null())
        payload.append(AMF0.string(streamName))
        payload.append(AMF0.string("live"))
        sendMessage(type: 20, streamID: streamID, timestamp: 0, payload: payload)
        phase = .publishSent
    }

    private func sendSetChunkSize(_ size: Int) {
        var payload = Data()
        var value = UInt32(size).bigEndian
        withUnsafeBytes(of: &value) { payload.append(contentsOf: $0.prefix(4)) }
        sendMessage(type: 1, streamID: 0, timestamp: 0, payload: payload)
    }

    // MARK: - 视频

    /// AVC sequence header（SPS/PPS）
    func sendAVCSequenceHeader(_ record: Data) {
        var payload = Data([0x17, 0x00, 0x00, 0x00, 0x00])
        payload.append(record)
        sendMessage(type: 9, streamID: streamID, timestamp: 0, payload: payload)
    }

    /// 一帧视频（payload 已是 4 字节长度前缀的 AVCC 数据）
    func sendVideoFrame(_ avcc: Data, isKeyframe: Bool, timestampMs: Int64) {
        var payload = Data([isKeyframe ? 0x17 : 0x27, 0x01, 0x00, 0x00, 0x00])
        payload.append(avcc)
        sendMessage(type: 9,
                    streamID: streamID,
                    timestamp: UInt32(truncatingIfNeeded: max(timestampMs, 0)),
                    payload: payload)
    }

    // MARK: - chunk 封装

    private func sendMessage(type: UInt8, streamID: UInt32, timestamp: UInt32, payload: Data) {
        let chunkStreamID: UInt8 = 3
        var data = Data()

        // 基础头：fmt=0 + csid
        data.append((0 << 6) | chunkStreamID)
        // 消息头（12 字节）
        data.append(UInt8((timestamp >> 16) & 0xFF))
        data.append(UInt8((timestamp >> 8) & 0xFF))
        data.append(UInt8(timestamp & 0xFF))
        let length = UInt32(payload.count)
        data.append(UInt8((length >> 16) & 0xFF))
        data.append(UInt8((length >> 8) & 0xFF))
        data.append(UInt8(length & 0xFF))
        data.append(type)
        data.append(UInt8(streamID & 0xFF))              // message stream id：小端
        data.append(UInt8((streamID >> 8) & 0xFF))
        data.append(UInt8((streamID >> 16) & 0xFF))
        data.append(UInt8((streamID >> 24) & 0xFF))

        var remaining = Data(payload)
        var isFirstChunk = true
        while !remaining.isEmpty {
            if !isFirstChunk {
                data.append((3 << 6) | chunkStreamID)    // 续块：fmt=3，只有基础头
            }
            let take = min(chunkSize, remaining.count)
            data.append(remaining.prefix(take))
            remaining.removeFirst(take)
            isFirstChunk = false
        }

        send(data, label: "发送 RTMP 消息")
    }

    // MARK: - 地址解析

    struct RTMPTarget {
        var host: String
        var port: UInt16
        var app: String
        var stream: String
    }

    /// 支持 rtmp://host:port/app/streamKey，也支持 rtmp://host/app/streamKey
    static func parse(urlString: String, streamKey: String) -> RTMPTarget? {
        var text = urlString.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else { return nil }
        for prefix in ["rtmp://", "rtmps://"] where text.hasPrefix(prefix) {
            text.removeFirst(prefix.count)
        }

        let parts = text.split(separator: "/", omittingEmptySubsequences: true).map(String.init)
        guard let hostPart = parts.first, !hostPart.isEmpty else { return nil }

        var host = hostPart
        var port: UInt16 = 1935
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
        return RTMPTarget(host: host, port: port, app: app, stream: stream)
    }

    private static func readUInt24(_ data: Data, at offset: Int) -> UInt32 {
        let base = data.startIndex + offset
        guard base + 2 < data.endIndex else { return 0 }
        return (UInt32(data[base]) << 16) | (UInt32(data[base + 1]) << 8) | UInt32(data[base + 2])
    }
}

// MARK: - AMF0 编码 / 解析

enum AMF0 {

    static func string(_ value: String) -> Data {
        var data = Data([0x02])
        let utf8 = Data(value.utf8)
        var length = UInt16(clamping: utf8.count).bigEndian
        withUnsafeBytes(of: &length) { data.append(contentsOf: $0) }
        data.append(utf8)
        return data
    }

    static func number(_ value: Double) -> Data {
        var data = Data([0x00])
        var bits = value.bitPattern.bigEndian
        withUnsafeBytes(of: &bits) { data.append(contentsOf: $0) }
        return data
    }

    static func boolean(_ value: Bool) -> Data {
        Data([0x01, value ? 0x01 : 0x00])
    }

    static func null() -> Data {
        Data([0x05])
    }

    static func object(_ values: [String: Any]) -> Data {
        var data = Data([0x03])
        for (key, value) in values {
            data.append(string(key))
            switch value {
            case let text as String: data.append(string(text))
            case let flag as Bool: data.append(boolean(flag))
            case let numeric as Int: data.append(number(Double(numeric)))
            case let numeric as Double: data.append(number(numeric))
            default: data.append(null())
            }
        }
        data.append(contentsOf: [0x00, 0x00, 0x09])
        return data
    }

    /// 取第一个字符串（命令名）
    static func firstString(in data: Data) -> String? {
        guard data.count > 3, data[data.startIndex] == 0x02 else { return nil }
        let length = Int(data[data.startIndex + 1]) << 8 | Int(data[data.startIndex + 2])
        let start = data.startIndex + 3
        guard start + length <= data.endIndex else { return nil }
        return String(data: data.subdata(in: start..<(start + length)), encoding: .utf8)
    }

    /// 取第 n 个数字（createStream 的返回里第 2 个数字是 stream id）
    static func firstNumber(in data: Data, skippingFirst: Int) -> Double? {
        var seen = 0
        var index = data.startIndex
        while index < data.endIndex {
            switch data[index] {
            case 0x00:
                guard index + 8 < data.endIndex else { return nil }
                var bits: UInt64 = 0
                for offset in 0..<8 {
                    bits = (bits << 8) | UInt64(data[index + 1 + offset])
                }
                if seen >= skippingFirst { return Double(bitPattern: bits) }
                seen += 1
                index += 9
            case 0x02:
                guard index + 2 < data.endIndex else { return nil }
                let length = Int(data[index + 1]) << 8 | Int(data[index + 2])
                index += 3 + length
            case 0x01:
                index += 2
            case 0x05:
                index += 1
            default:
                return nil
            }
        }
        return nil
    }
}
