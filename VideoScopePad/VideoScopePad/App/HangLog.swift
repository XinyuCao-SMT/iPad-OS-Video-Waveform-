//
//  HangLog.swift
//  VideoScopePad
//
//  「点选择摄像头就卡死」这类问题的取证工具。
//
//  为什么需要它：卡死时界面已经不动了，日志必须落盘、事后能取出来看。
//  所以这里做两件事：
//    1) mark(step)：把「现在执行到哪一步」写进 hanglog.txt，带时间戳与线程标记，
//       每次都立即 synchronize 落盘（卡死时内存缓冲会丢）。
//    2) 主线程看门狗：后台每 0.5 s 检查主线程心跳；超过 2 s 没跳动就记一条
//       「MAIN BLOCKED ≈Ns｜最后一步：xxx」—— 用来区分「主线程被卡」还是
//       「sessionQueue 卡住（主线程还活着）」。
//
//  ⚠️ 踩过的坑（务必保留这个结构）：
//  看门狗**不能**跑在写日志用的那个串行队列上。第一版把 `while true { sleep }` 直接
//  放在写队列里，结果队列被死循环独占，后面所有 write 都排不进去 —— 日志永远是空的，
//  现场表现就是「根本没有生成文件」。现在看门狗在独立队列，写日志在另一个队列。
//
//  取日志：设置 → 关于 → 「诊断日志」（可滚动、可一键复制）。
//

import Foundation

enum HangLog {

    // MARK: - 队列

    /// 只负责写文件
    private static let writeQueue = DispatchQueue(label: "vsp.hanglog.write")
    /// 只负责看门狗（独立队列，避免与写日志抢）
    private static let watchdogQueue = DispatchQueue(label: "vsp.hanglog.watchdog")
    /// 保护共享状态
    private static let stateLock = NSLock()

    // MARK: - 状态

    private static var fileURL: URL = {
        let fm = FileManager.default
        let documents = fm.urls(for: .documentDirectory, in: .userDomainMask)[0]
        let preferred = documents.appendingPathComponent("hanglog.txt")
        if fm.isWritableFile(atPath: documents.path) {
            return preferred
        }
        return URL(fileURLWithPath: NSTemporaryDirectory()).appendingPathComponent("hanglog.txt")
    }()

    private static var heartbeat: UInt64 = 0
    private static var lastReportedBeat: UInt64 = .max
    private static var lastStep: String = "(未开始)"
    private static var started = false

    static var logPath: String { fileURL.path }

    // MARK: - 对外

    /// 启动看门狗（App 启动时调一次）
    static func start() {
        stateLock.lock()
        let already = started
        started = true
        stateLock.unlock()
        guard !already else { return }

        // 清空旧日志并写第一行（同步写完，确保文件真的存在）
        writeQueue.sync {
            try? "".write(to: fileURL, atomically: true, encoding: .utf8)
        }
        write("=== 启动 \(Date()) ===")

        // 主线程心跳：每 0.25 s 加一（Timer 挂在 .common 模式，滚动/拖动时也跳）
        DispatchQueue.main.async {
            let timer = Timer(timeInterval: 0.25, repeats: true) { _ in
                stateLock.lock()
                heartbeat &+= 1
                lastReportedBeat = .max
                stateLock.unlock()
            }
            RunLoop.main.add(timer, forMode: .common)
        }

        // 看门狗：独立队列上的死循环，只读心跳 + 偶尔写日志（写日志走 writeQueue）
        watchdogQueue.async {
            var lastBeat: UInt64 = 0
            var lastBeatAt = Date()
            while true {
                Thread.sleep(forTimeInterval: 0.5)

                stateLock.lock()
                let beat = heartbeat
                let step = lastStep
                stateLock.unlock()

                if beat != lastBeat {
                    lastBeat = beat
                    lastBeatAt = Date()
                    continue
                }
                let blocked = Date().timeIntervalSince(lastBeatAt)
                if blocked > 2.0 {
                    stateLock.lock()
                    let shouldReport = (lastReportedBeat != beat)
                    if shouldReport { lastReportedBeat = beat }
                    stateLock.unlock()
                    if shouldReport {
                        write("!! MAIN BLOCKED ≈\(String(format: "%.1f", blocked))s｜最后一步：\(step)")
                    }
                }
            }
        }
    }

    /// 记一步（任意线程可调）
    static func mark(_ step: String) {
        stateLock.lock()
        lastStep = step
        stateLock.unlock()
        let thread = Thread.isMainThread ? "main" : "bg"
        write("[\(thread)] \(step)")
    }

    /// 读日志尾部（界面显示 / 一键复制用）
    static func tail(lines: Int) -> String {
        guard let text = try? String(contentsOf: fileURL, encoding: .utf8) else {
            return ""
        }
        let all = text.split(separator: "\n", omittingEmptySubsequences: false)
        return all.suffix(max(lines, 1)).joined(separator: "\n")
    }

    // MARK: - 写文件

    private static func write(_ line: String) {
        let stamp = String(format: "%.3f", Date().timeIntervalSince1970)
        let text = "\(stamp) \(line)\n"
        writeQueue.async {
            guard let data = text.data(using: .utf8) else { return }
            let fm = FileManager.default
            if !fm.fileExists(atPath: fileURL.path) {
                // 文件还不存在：直接创建（append 型写入要求文件已存在）
                try? text.write(to: fileURL, atomically: true, encoding: .utf8)
                return
            }
            if let handle = try? FileHandle(forWritingTo: fileURL) {
                handle.seekToEndOfFile()
                handle.write(data)
                try? handle.synchronize()
                try? handle.close()
            }
        }
    }
}
