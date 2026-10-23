//
//  HangLog.swift
//  VideoScopePad
//
//  「点选择摄像头就卡死」这类问题的取证工具。
//
//  为什么需要它：卡死的时候界面已经不动了，日志只能落盘、事后用「文件」App 取出来看。
//  所以这里做两件事：
//    1) mark(step)：把「现在执行到哪一步」写进 Documents/hanglog.txt，
//       带时间戳线程标记（main / bg），并且**每次都立即 flush**（卡死时缓冲会丢）。
//    2) 主线程看门狗：后台每 0.5 s 检查一次主线程心跳；超过 2 s 没跳动就记一条
//       「MAIN BLOCKED」，并写清最后一步是哪一步 —— 这样能直接区分
//       「主线程被卡」还是「sessionQueue 卡住（主线程还活着）」。
//
//  取日志：设置 → 关于 → 「诊断日志」页首会显示路径；或者用「文件」App →
//  我的 iPad → VideoScopePad → hanglog.txt。
//

import Foundation

enum HangLog {

    private static let queue = DispatchQueue(label: "vsp.hanglog")
    /// 日志文件位置：优先 Documents（用户能用「文件」App 取）；
    /// 写不进去就退回临时目录 —— 但界面里的「诊断日志」页始终可读（不依赖文件共享）。
    private static var fileURL: URL = {
        let fm = FileManager.default
        let documents = fm.urls(for: .documentDirectory, in: .userDomainMask)[0]
        let preferred = documents.appendingPathComponent("hanglog.txt")
        if fm.isWritableFile(atPath: documents.path) {
            return preferred
        }
        return URL(fileURLWithPath: NSTemporaryDirectory()).appendingPathComponent("hanglog.txt")
    }()

    /// 主线程心跳计数（主线程上的定时器递增）
    private static var heartbeat: UInt64 = 0
    /// 最后一次记录「主线程阻塞」时的值（避免刷屏）
    private static var lastReportedBeat: UInt64 = .max
    private static var lastStep: String = "(未开始)"
    private static var started = false

    static var logPath: String { fileURL.path }

    /// 读日志尾部（界面里显示 / 一键复制用）
    static func tail(lines: Int) -> String {
        guard let text = try? String(contentsOf: fileURL, encoding: .utf8) else {
            return ""
        }
        let all = text.split(separator: "\n", omittingEmptySubsequences: false)
        let tailPart = all.suffix(max(lines, 1))
        return tailPart.joined(separator: "\n")
    }

    /// 启动看门狗（App 启动时调一次）
    static func start() {
        queue.sync {
            guard !started else { return }
            started = true
            // 每次启动清空旧日志，避免混在一起
            try? "".write(to: fileURL, atomically: true, encoding: .utf8)
        }
        write("=== 启动 \(Date()) ===")

        // 主线程心跳
        DispatchQueue.main.async {
            let timer = Timer(timeInterval: 0.25, repeats: true) { _ in
                queue.async {

                    heartbeat &+= 1
                    lastReportedBeat = .max
                }
            }
            RunLoop.main.add(timer, forMode: .common)
        }

        // 后台看门狗
        queue.async {
            var lastBeat: UInt64 = 0
            var lastBeatAt = Date()
            while true {
                Thread.sleep(forTimeInterval: 0.5)
                let beat = heartbeat
                if beat != lastBeat {
                    lastBeat = beat
                    lastBeatAt = Date()
                    continue
                }
                let blocked = Date().timeIntervalSince(lastBeatAt)
                if blocked > 2.0, lastReportedBeat != beat {
                    lastReportedBeat = beat
                    write("!! MAIN BLOCKED \u{2248}\(String(format: "%.1f", blocked))s｜最后一步：\(lastStep)")
                }
            }
        }
    }

    /// 记一步（可以在任意线程调）
    static func mark(_ step: String) {
        lastStep = step
        let thread = Thread.isMainThread ? "main" : "bg"
        write("[\(thread)] \(step)")
    }

    private static func write(_ line: String) {
        queue.async {
            let stamp = String(format: "%.3f", Date().timeIntervalSince1970)
            let text = "\(stamp) \(line)\n"
            guard let data = text.data(using: .utf8) else { return }
            if !FileManager.default.fileExists(atPath: fileURL.path) {
                try? text.write(to: fileURL, atomically: true, encoding: .utf8)
                return
            }
            if let handle = try? FileHandle(forWritingTo: fileURL) {
                handle.seekToEndOfFile()
                handle.write(data)
                // 卡死时缓冲区会丢，所以每次都同步到磁盘
                try? handle.synchronize()
                try? handle.close()
            } else {
                try? text.write(to: fileURL, atomically: true, encoding: .utf8)
            }
        }
    }
}
