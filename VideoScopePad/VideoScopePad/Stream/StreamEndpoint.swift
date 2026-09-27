//
//  StreamEndpoint.swift
//  VideoScopePad
//
//  推流地址的「结构化」表示。
//
//  为什么要把地址拆开填：
//    手打完整 URL 太容易被符号坑到 —— 全角冒号、漏了斜杠、大小写 scheme、粘贴时带的
//    零宽字符、把 streamid 写成路径……在 SRT 那条链路上，只要 HaishinKit 内部的
//    `SRTSocketURL` 看见的 scheme 不是严格的小写 `srt`，它就直接抛
//    `SRTConnection.Error.unsupportedUri`（界面上的 `... error 1.`），完全看不出哪里错了。
//    所以界面改成「主机 / 端口 / 模式 / 串流标识 / …」逐栏填，由这里负责拼装与校验，
//    同时保留一个「粘贴完整地址自动填入」的入口给习惯粘地址的人。
//
//  这里同时承担两种输入路径：
//    · make(...)  各栏 → 可用的 endpoint（界面表单用）
//    · parse(...) 完整地址 → endpoint（粘贴导入用）
//  拼装一律用**小写** `srt://` / `rtmp://`，拼完复核 scheme。
//

import Foundation

// MARK: - SRT 模式

enum SRTModeOption: String, CaseIterable, Identifiable {
    case caller
    case listener
    case rendezvous

    var id: String { rawValue }

    var title: String {
        switch self {
        case .caller:     return "呼叫"
        case .listener:   return "监听"
        case .rendezvous: return "会合"
        }
    }

    var shortName: String {
        switch self {
        case .caller:     return "caller"
        case .listener:   return "listener"
        case .rendezvous: return "rendezvous"
        }
    }

    var detail: String {
        switch self {
        case .caller:
            return "本机主动连服务器。推流到 SRS / MediaMTX / OBS 的 SRT 监听端都选这个。"
        case .listener:
            return "本机开端口等对方来连，端口填本机要监听的端口。"
        case .rendezvous:
            return "双方同时向对方发起连接，两边要用同一个端口。"
        }
    }
}

// MARK: - 公共清洗

enum EndpointSanitizer {

    /// 去空白 / 折全角 / 去零宽字符。中文输入法很容易打出全角 `：／ｓｒｔ`，
    /// 而从微信、备忘录粘贴又常带 U+200B 这类隐形字符，两者都会让 URL 解析失败。
    static func clean(_ raw: String) -> String {
        var text = raw.folding(options: [.widthInsensitive], locale: nil)
        for junk in ["\u{200B}", "\u{FEFF}", "\u{200E}", "\u{200F}", "\u{2060}"] {
            text = text.replacingOccurrences(of: junk, with: "")
        }
        return text.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    /// 端口：空字符串表示「用默认值」，非法则返回 nil
    static func port(_ raw: String, default defaultValue: Int) -> Int? {
        let text = clean(raw)
        if text.isEmpty { return defaultValue }
        guard let value = Int(text), value > 0, value <= 65535 else { return nil }
        return value
    }

    /// 可选的整数（延迟、超时这类，留空就不写进地址）
    static func optionalInt(_ raw: String, range: ClosedRange<Int>) -> Int? {
        let text = clean(raw)
        if text.isEmpty { return nil }
        guard let value = Int(text), range.contains(value) else { return nil }
        return value
    }
}

// MARK: - RTMP

struct RTMPEndpoint {
    /// 传给 RTMPConnection.connect 的命令：rtmp://主机:端口/应用
    var connectCommand: String
    /// 传给 RTMPStream.publish 的流名
    var streamName: String
    var host: String
    var port: Int
    var app: String
    var secure: Bool

    var display: String { "\(connectCommand)/\(streamName)" }

    /// 表单路径：主机 / 端口 / 应用 / 流密钥 / 是否加密。
    ///
    /// 「主机」栏写得宽松一点也没关系：允许连地址一起粘进来，这里会拆掉 `rtmp://` 前缀、
    /// 端口和路径（路径第一段当应用、后面的段当流名）。
    static func make(host rawHost: String,
                     port rawPort: String,
                     app rawApp: String,
                     streamKey rawKey: String,
                     secure: Bool) -> RTMPEndpoint? {

        var hostText = EndpointSanitizer.clean(rawHost)
        guard !hostText.isEmpty else { return nil }

        var secureFlag = secure
        if let range = hostText.range(of: "rtmps://", options: [.caseInsensitive, .anchored]) {
            secureFlag = true
            hostText = String(hostText[range.upperBound...])
        } else if let range = hostText.range(of: "rtmp://", options: [.caseInsensitive, .anchored]) {
            hostText = String(hostText[range.upperBound...])
        }

        let segments = hostText.split(separator: "/", omittingEmptySubsequences: true).map(String.init)
        var authority = segments.first ?? ""
        let pathApp = segments.count >= 2 ? segments[1] : ""
        let pathStream = segments.count >= 3 ? segments.dropFirst(2).joined(separator: "/") : ""

        var portValue = secureFlag ? 443 : 1935
        if let colon = authority.lastIndex(of: ":") {
            let text = String(authority[authority.index(after: colon)...])
            if let parsed = Int(text), parsed > 0, parsed <= 65535 {
                portValue = parsed
            }
            authority = String(authority[..<colon])
        }
        guard !authority.isEmpty else { return nil }

        // 端口栏填了就以它为准（覆盖地址里带的端口）
        let portText = EndpointSanitizer.clean(rawPort)
        if !portText.isEmpty {
            guard let parsed = EndpointSanitizer.port(portText, default: portValue) else { return nil }
            portValue = parsed
        }

        var app = EndpointSanitizer.clean(rawApp)
        if app.isEmpty { app = pathApp.isEmpty ? "live" : pathApp }

        var stream = EndpointSanitizer.clean(rawKey)
        if stream.isEmpty { stream = pathStream }
        guard !stream.isEmpty else { return nil }

        if app.hasPrefix("/") { app.removeFirst() }
        if stream.hasPrefix("/") { stream.removeFirst() }

        let scheme = secureFlag ? "rtmps" : "rtmp"
        return RTMPEndpoint(connectCommand: "\(scheme)://\(authority):\(portValue)/\(app)",
                            streamName: stream,
                            host: authority,
                            port: portValue,
                            app: app,
                            secure: secureFlag)
    }

    /// 粘贴路径：完整地址 → endpoint。接受 rtmp://主机[:端口]/应用[/流名]，流密钥栏填了优先用它。
    static func parse(urlString raw: String, streamKey rawKey: String) -> RTMPEndpoint? {
        var text = EndpointSanitizer.clean(raw)
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
        var port = secure ? 443 : 1935
        if let colon = hostPart.lastIndex(of: ":") {
            host = String(hostPart[hostPart.startIndex..<colon])
            if let parsed = Int(hostPart[hostPart.index(after: colon)...]), parsed > 0, parsed <= 65535 {
                port = parsed
            }
        }

        var app = "live"
        var stream = EndpointSanitizer.clean(rawKey)
        if parts.count >= 3 {
            app = parts[1]
            if stream.isEmpty { stream = parts.dropFirst(2).joined(separator: "/") }
        } else if parts.count == 2, stream.isEmpty {
            stream = parts[1]
        }
        guard !host.isEmpty, !stream.isEmpty else { return nil }

        let scheme = secure ? "rtmps" : "rtmp"
        return RTMPEndpoint(connectCommand: "\(scheme)://\(host):\(port)/\(app)",
                            streamName: stream,
                            host: host,
                            port: port,
                            app: app,
                            secure: secure)
    }
}

// MARK: - SRT

struct SRTEndpoint {
    var url: URL
    var host: String
    /// listener 模式下是「本机监听端口」；caller/rendezvous 下是「对方端口」
    var port: Int
    var mode: SRTModeOption
    var streamID: String?
    var latency: Int?
    var passphrase: String?
    var keyLength: Int?
    var connectTimeout: Int?

    /// 实际交给库的完整地址
    var display: String { url.absoluteString }

    var isEncrypted: Bool { !(passphrase ?? "").isEmpty }

    /// 表单路径：主机 / 端口 / 模式 / 串流标识 /（可选）延迟、密码、加密位数、连接超时。
    static func make(host rawHost: String,
                     port rawPort: String,
                     mode: SRTModeOption,
                     streamID rawStreamID: String,
                     latency rawLatency: String,
                     passphrase rawPassphrase: String,
                     keyLength: Int,
                     connectTimeout rawTimeout: String) -> SRTEndpoint? {

        var host = EndpointSanitizer.clean(rawHost)
        // 允许把完整地址粘进主机栏：拆掉前缀、端口、路径与查询
        var queryFromHost = ""
        if let range = host.range(of: "srt://", options: [.caseInsensitive, .anchored]) {
            host = String(host[range.upperBound...])
        } else if let range = host.range(of: "srt:", options: [.caseInsensitive, .anchored]) {
            host = String(host[range.upperBound...])
            if host.hasPrefix("//") { host.removeFirst(2) }
        }
        if let mark = host.firstIndex(of: "?") {
            queryFromHost = String(host[host.index(after: mark)...])
            host = String(host[..<mark])
        }
        var pathFromHost = ""
        if let slash = host.firstIndex(of: "/") {
            pathFromHost = String(host[host.index(after: slash)...])
            host = String(host[..<slash])
        }

        var hostPort: Int?
        if host.hasPrefix("[") {
            if let close = host.firstIndex(of: "]") {
                let rest = host[host.index(after: close)...]
                if rest.hasPrefix(":") { hostPort = Int(rest.dropFirst()) }
                host = String(host[host.index(after: host.startIndex)..<close])
            }
        } else if let colon = host.lastIndex(of: ":") {
            let text = String(host[host.index(after: colon)...])
            hostPort = Int(text)
            if hostPort != nil { host = String(host[..<colon]) }
        }
        host = host.trimmingCharacters(in: .whitespaces)

        // 监听模式只要端口，主机可以留空
        guard !host.isEmpty || mode == .listener else { return nil }

        var portValue = hostPort ?? (mode == .listener ? 9000 : 9710)
        let portText = EndpointSanitizer.clean(rawPort)
        if !portText.isEmpty {
            guard let parsed = EndpointSanitizer.port(portText, default: portValue) else { return nil }
            portValue = parsed
        }
        guard portValue > 0, portValue <= 65535 else { return nil }

        // 主机栏里粘的查询参数也认（优先级低于下面各栏）
        var inherited: [String: String] = [:]
        for chunk in queryFromHost.split(separator: "&") {
            let pieces = chunk.split(separator: "=", maxSplits: 1, omittingEmptySubsequences: false)
            if let name = pieces.first, !name.isEmpty {
                inherited[String(name)] = pieces.count > 1 ? String(pieces[1]) : ""
            }
        }

        var streamID = EndpointSanitizer.clean(rawStreamID)
        if streamID.isEmpty {
            streamID = EndpointSanitizer.clean(inherited["streamid"] ?? "")
        }
        if streamID.isEmpty, !pathFromHost.isEmpty {
            streamID = EndpointSanitizer.clean(pathFromHost)
        }

        var pairs: [(String, String)] = [("mode", mode.shortName)]
        if !streamID.isEmpty { pairs.append(("streamid", streamID)) }

        var latencyValue = EndpointSanitizer.optionalInt(rawLatency, range: 20...8000)
        if latencyValue == nil, let text = inherited["latency"] { latencyValue = Int(text) }
        if let latencyValue { pairs.append(("latency", String(latencyValue))) }

        let passphrase = EndpointSanitizer.clean(rawPassphrase).isEmpty
            ? EndpointSanitizer.clean(inherited["passphrase"] ?? "")
            : EndpointSanitizer.clean(rawPassphrase)
        if !passphrase.isEmpty {
            pairs.append(("passphrase", passphrase))
            let bits = keyLength > 0 ? keyLength : 16
            pairs.append(("pbkeylen", String(bits)))
        }

        var timeoutValue = EndpointSanitizer.optionalInt(rawTimeout, range: 1000...60000) ?? 5000
        if let text = inherited["conntimeo"], let parsed = Int(text), parsed >= 1000 { timeoutValue = parsed }
        pairs.append(("conntimeo", String(timeoutValue)))

        let rebuilt = pairs.map { "\($0.0)=\($0.1)" }.joined(separator: "&")
        var urlText = "srt://"
        urlText += host
        urlText += ":\(portValue)"
        urlText += "?" + rebuilt

        guard let url = URL(string: urlText), url.scheme?.lowercased() == "srt" else { return nil }

        return SRTEndpoint(url: url,
                           host: host,
                           port: portValue,
                           mode: mode,
                           streamID: streamID.isEmpty ? nil : streamID,
                           latency: latencyValue,
                           passphrase: passphrase.isEmpty ? nil : passphrase,
                           keyLength: passphrase.isEmpty ? nil : (keyLength > 0 ? keyLength : 16),
                           connectTimeout: timeoutValue)
    }

    /// 粘贴路径：完整地址 → endpoint（界面上的「粘贴完整地址自动填入」用它）。
    static func parse(urlString raw: String) -> SRTEndpoint? {
        var text = EndpointSanitizer.clean(raw)
        guard !text.isEmpty else { return nil }

        if let range = text.range(of: "srt://", options: [.caseInsensitive, .anchored]) {
            text = String(text[range.upperBound...])
        } else if let range = text.range(of: "srt:", options: [.caseInsensitive, .anchored]) {
            text = String(text[range.upperBound...])
            if text.hasPrefix("//") { text.removeFirst(2) }
        }

        var query = ""
        if let mark = text.firstIndex(of: "?") {
            query = String(text[text.index(after: mark)...])
            text = String(text[..<mark])
        }
        var pathStreamID = ""
        if let slash = text.firstIndex(of: "/") {
            pathStreamID = String(text[text.index(after: slash)...])
            text = String(text[..<slash])
        }

        var host = ""
        var port: Int?
        if text.hasPrefix("[") {
            if let close = text.firstIndex(of: "]") {
                host = String(text[text.index(after: text.startIndex)..<close])
                let rest = text[text.index(after: close)...]
                if rest.hasPrefix(":") { port = Int(rest.dropFirst()) }
            }
        } else if let colon = text.lastIndex(of: ":") {
            host = String(text[..<colon]).trimmingCharacters(in: .whitespaces)
            port = Int(String(text[text.index(after: colon)...]).trimmingCharacters(in: .whitespaces))
        } else {
            host = text.trimmingCharacters(in: .whitespaces)
        }
        guard !host.isEmpty || port != nil else { return nil }

        var items: [String: String] = [:]
        var order: [String] = []
        for chunk in query.split(separator: "&") {
            let pieces = chunk.split(separator: "=", maxSplits: 1, omittingEmptySubsequences: false)
            guard let name = pieces.first, !name.isEmpty else { continue }
            let key = String(name)
            if items[key] == nil { order.append(key) }
            items[key] = pieces.count > 1 ? String(pieces[1]) : ""
        }

        let mode = SRTModeOption(rawValue: items["mode"] ?? "") ?? (host.isEmpty ? .listener : .caller)
        var streamID = EndpointSanitizer.clean(items["streamid"] ?? "")
        if streamID.isEmpty { streamID = EndpointSanitizer.clean(pathStreamID) }
        let passphrase = EndpointSanitizer.clean(items["passphrase"] ?? "")
        let latency = items["latency"].flatMap { Int($0) }
        let keyLength = items["pbkeylen"].flatMap { Int($0) }
        let timeout = items["conntimeo"].flatMap { Int($0) }
        let portValue = port ?? (mode == .listener ? 9000 : 9710)

        // 原样重拼一次（保持用户给的其它参数不丢）
        var pairs: [(String, String)] = []
        var seen: Set<String> = []
        for name in order {
            pairs.append((name, items[name] ?? ""))
            seen.insert(name)
        }
        if !seen.contains("mode") { pairs.append(("mode", mode.shortName)) }
        if !seen.contains("conntimeo") { pairs.append(("conntimeo", "5000")) }

        let rebuilt = pairs.map { "\($0.0)=\($0.1)" }.joined(separator: "&")
        var urlText = "srt://" + host + ":\(portValue)"
        if !rebuilt.isEmpty { urlText += "?" + rebuilt }

        guard let url = URL(string: urlText), url.scheme?.lowercased() == "srt" else { return nil }

        return SRTEndpoint(url: url,
                           host: host,
                           port: portValue,
                           mode: mode,
                           streamID: streamID.isEmpty ? nil : streamID,
                           latency: latency,
                           passphrase: passphrase.isEmpty ? nil : passphrase,
                           keyLength: keyLength,
                           connectTimeout: timeout)
    }
}
