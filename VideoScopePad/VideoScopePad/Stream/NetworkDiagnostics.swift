//
//  NetworkDiagnostics.swift
//  VideoScopePad
//
//  推流失败时用来判断「到底卡在哪一层」的小工具。
//
//  为什么要它：
//    SRT（以及 RTMP）连局域网里的服务器时，失败原因往往不在协议本身，而在下面几层：
//      · iOS 14 起访问**局域网设备**要用户授权（Info.plist 的 NSLocalNetworkUsageDescription），
//        没有这个键系统连权限框都弹不出来，数据包会被静默丢弃 —— 表现就是一直超时；
//      · iPad 与服务器不在同一网段（比如 iPad 在 192.168.1.x、服务器在 192.168.6.x），
//        UDP 根本路由不过去；
//      · 服务器防火墙没放行 UDP，或者那个端口上压根没有服务。
//    所以这里给出「本机地址 + 是否同网段 + 一次 UDP 探测」，让用户一眼看出是哪一层的问题。
//

import Darwin
import Foundation
import Network

enum NetworkDiagnostics {

    // MARK: - 本机地址

    /// 本机所有非回环 IPv4 地址，形如 "en0 192.168.1.23"
    static func localIPv4Addresses() -> [String] {
        var results: [String] = []
        var head: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&head) == 0, let first = head else { return results }
        defer { freeifaddrs(head) }

        var cursor: UnsafeMutablePointer<ifaddrs>? = first
        while let current = cursor {
            let entry = current.pointee
            if let address = entry.ifa_addr, address.pointee.sa_family == UInt8(AF_INET) {
                let name = String(cString: entry.ifa_name)
                var host = [CChar](repeating: 0, count: Int(NI_MAXHOST))
                let status = getnameinfo(address,
                                         socklen_t(address.pointee.sa_len),
                                         &host,
                                         socklen_t(host.count),
                                         nil,
                                         0,
                                         NI_NUMERICHOST)
                if status == 0 {
                    let text = String(cString: host)
                    if !text.isEmpty, !text.hasPrefix("127.") {
                        results.append("\(name) \(text)")
                    }
                }
            }
            cursor = entry.ifa_next
        }
        return results
    }

    /// 取本机的第一个私有网段前缀，例如 "192.168.1"
    static func localSubnetPrefix() -> String? {
        for entry in localIPv4Addresses() {
            guard let address = entry.split(separator: " ").last else { continue }
            let parts = address.split(separator: ".")
            if parts.count == 4 {
                return parts.prefix(3).joined(separator: ".")
            }
        }
        return nil
    }

    static func subnetPrefix(of address: String) -> String? {
        let parts = address.split(separator: ".")
        guard parts.count == 4 else { return nil }
        return parts.prefix(3).joined(separator: ".")
    }

    // MARK: - UDP 探测

    /// 向目标发一个小包看有没有回应。
    ///
    /// 注意这只回答「UDP 这一层通不通」，不代表 SRT 一定能连上：
    ///   · 收到回应 → 该端口有 UDP 服务（很可能就是 SRT 服务器）
    ///   · 明确报错（ICMP 端口不可达）→ 主机可达，但那个端口上没有服务 → 端口填错了
    ///   · 完全没动静 → 网段不通 / 被防火墙静默丢弃 / 没有服务
    static func probeUDP(host: String,
                         port: Int,
                         timeout: TimeInterval = 2.0) async -> String {
        guard port > 0, port <= 65535, !host.isEmpty else {
            return "主机或端口不合法"
        }
        guard let nwPort = NWEndpoint.Port(rawValue: UInt16(port)) else {
            return "端口号非法"
        }
        let connection = NWConnection(host: NWEndpoint.Host(host), port: nwPort, using: .udp)
        let queue = DispatchQueue(label: "vsp.network.probe")

        return await withCheckedContinuation { continuation in
            let box = FinishBox()
            func finish(_ text: String) {
                guard box.claim() else { return }
                connection.cancel()
                continuation.resume(returning: text)
            }

            connection.stateUpdateHandler = { (state: NWConnection.State) in
                switch state {
                case .ready:
                    // 一个 SRT 风格的 48 位控制包头（HSv5 induction）：
                    // 真正的 SRT 服务器会回包；普通 UDP 服务一般不理，所以「有回应」很有参考价值。
                    var payload = Data()
                    payload.append(contentsOf: [0x80, 0x00])                  // control + type 0 (handshake)
                    payload.append(contentsOf: [0x00, 0x00, 0x00, 0x01])      // subtype 1 = induction
                    payload.append(contentsOf: [0x00, 0x00, 0x00, 0x05])      // version 5
                    payload.append(contentsOf: [0x00, 0x00, 0x00, 0x00])      // socket id
                    payload.append(contentsOf: [0x00, 0x00, 0x00, 0x00])      // cookie
                    payload.append(contentsOf: [UInt8](repeating: 0, count: 16))  // peer ip

                    connection.send(content: payload, completion: .contentProcessed { (error: NWError?) in
                        if let error {
                            finish("发送失败：\(error.localizedDescription)")
                            return
                        }
                        connection.receiveMessage { (data: Data?, _: NWConnection.ContentContext?, _: Bool, error: NWError?) in
                            if let error {
                                finish("目标明确拒绝：\(error.localizedDescription)（主机可达，但那个端口上没有 UDP 服务 → 端口填错了？）")
                            } else if let data, !data.isEmpty {
                                finish("收到 \(data.count) 字节回应 —— 该端口有 UDP 服务在响应 ✓")
                            } else {
                                finish("没有回应")
                            }
                        }
                    })
                case .failed(let error):
                    finish("无法建立：\(error.localizedDescription)")
                case .waiting(let error):
                    finish("网络不可用：\(error.localizedDescription)")
                default:
                    break
                }
            }

            connection.start(queue: queue)
            queue.asyncAfter(deadline: .now() + timeout) {
                finish("\(String(format: "%.0f", timeout)) 秒内没有任何回应（最常见：iPad 与服务器不同网段 / 防火墙丢弃 UDP / 该端口没有服务）")
            }
        }
    }
}

/// 让超时与回调只结算一次
private final class FinishBox {
    private let lock = NSLock()
    private var done = false

    func claim() -> Bool {
        lock.lock()
        defer { lock.unlock() }
        if done { return false }
        done = true
        return true
    }
}
