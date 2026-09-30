//
//  AVSyncMeter.swift
//  VideoScopePad
//
//  声画延时（A/V Sync / Lip-Sync）测量。
//
//  被测信号（用户描述的测试设备输出）：
//      静音黑场  →  千周声（1 kHz）与彩条画面**同时**出现  →  一起消失  →  周而复始
//  我们要测的是：这两件事到达本机的时刻差，也就是「声音快还是画面快、快多少毫秒」。
//
//  做法：
//    · 音频侧：AudioMonitor 用 1 kHz 带通 + 包络检波，逐样本找起音时刻（亚毫秒级）；
//    · 视频侧：FrameSignatureAnalyzer 每帧算亮度/饱和度，找出「黑场 → 彩条」的那一帧；
//    · 两个时刻都在 iPad 主机时钟上，直接相减即可。
//
//  关于精度：
//    · 音频起音是采样级（48 kHz 下约 0.02 ms 分辨），实际受输入设备与信号本身影响；
//    · 视频事件被量化到**帧边界**上 —— 60p 是 16.7 ms、30p 是 33.3 ms，
//      所以结果里会同时给出帧间隔，并注明 ±1 帧的量化误差；
//    · 多次测量取**中位数**（对偶发误检更稳），并给出极差看抖动。
//
//  关于符号：offset = 视频事件时刻 − 音频事件时刻
//      > 0 → 画面到得晚 → 声音快
//      < 0 → 画面到得早
//
//  关于偏差：如果视频与音频走的是**同一台采集设备**（例如采集卡把 HDMI 内嵌音频一起交给 iPad），
//  两条链路的时延基本一致，测到的就是信号源本身的声画差值；若音频走麦克风拾音，
//  还会叠加声程（约 3 ms/米）与声卡时延。所以界面上可以选音频输入，并带一个「补偿」值。
//

import Foundation

final class AVSyncMeter: ObservableObject {

    struct Sample {
        /// 视频事件（彩条出现）时刻，主机时钟秒
        var videoHost: TimeInterval
        /// 音频事件（千周声起音）时刻
        var audioHost: TimeInterval
        /// 视频帧间隔（这次测量的量化步长）
        var frameInterval: TimeInterval
        /// offset = video − audio，毫秒
        var offsetMs: Double
        var measuredAt: Date
    }

    enum State: Equatable {
        case idle                     // 音频没开
        case waiting                  // 等信号
        case waitingVideo             // 收到千周声，等彩条
        case waitingAudio             // 收到彩条，等千周声
        case measuring(count: Int)    // 已测到
    }

    @Published private(set) var state: State = .idle
    @Published private(set) var samples: [Sample] = []
    @Published private(set) var medianOffsetMs: Double?
    @Published private(set) var spreadMs: Double = 0
    @Published private(set) var lastFrameInterval: TimeInterval = 0
    /// 手动静默阈值：小于它就认为「同步」（默认 5 ms）
    @Published var syncThresholdMs: Double = 5
    /// 手动补偿（毫秒）：用来抵消已知的系统偏差（例如麦克风声程）
    @Published var compensationMs: Double = 0

    /// 配对窗口：音频与视频事件相差超过它就不配对（避免把上一周期的视频配到本周期的音频）
    private let pairingWindow: TimeInterval = 1.2
    /// 统计最近多少次
    private let windowSize = 10

    private var pendingVideo: (host: TimeInterval, frameInterval: TimeInterval)?
    private var pendingAudio: TimeInterval?

    // MARK: - 输入

    func setAudioAvailable(_ available: Bool) {
        if !available {
            state = .idle
            pendingAudio = nil
        } else if case .idle = state {
            state = .waiting
        }
    }

    /// 视频侧：彩条出现（host 为这一帧的时刻，frameInterval 为帧间隔）
    func noteVideoOnset(host: TimeInterval, frameInterval: TimeInterval) {
        lastFrameInterval = frameInterval
        pendingVideo = (host: host, frameInterval: frameInterval)

        if let audio = pendingAudio {
            finishPair(videoHost: host, frameInterval: frameInterval, audioHost: audio)
        } else {
            state = .waitingAudio
        }
    }

    /// 音频侧：千周声起音
    func noteAudioOnset(host: TimeInterval) {
        pendingAudio = host

        if let video = pendingVideo {
            finishPair(videoHost: video.host, frameInterval: video.frameInterval, audioHost: host)
        } else {
            state = .waitingVideo
        }
    }

    func reset() {
        samples.removeAll()
        medianOffsetMs = nil
        spreadMs = 0
        pendingVideo = nil
        pendingAudio = nil
        state = .waiting
    }

    // MARK: - 配对与统计

    private func finishPair(videoHost: TimeInterval, frameInterval: TimeInterval, audioHost: TimeInterval) {
        pendingVideo = nil
        pendingAudio = nil

        let delta = videoHost - audioHost
        guard abs(delta) <= pairingWindow else {
            // 差得太远：多半是配错了周期，丢弃这次
            state = .waiting
            return
        }

        let sample = Sample(videoHost: videoHost,
                            audioHost: audioHost,
                            frameInterval: frameInterval,
                            offsetMs: delta * 1000 - compensationMs,
                            measuredAt: Date())
        samples.append(sample)
        if samples.count > 40 { samples.removeFirst(samples.count - 40) }

        let recent = samples.suffix(windowSize).map(\.offsetMs)
        medianOffsetMs = Self.median(Array(recent))
        spreadMs = (recent.max() ?? 0) - (recent.min() ?? 0)
        state = .measuring(count: samples.count)
    }

    private static func median(_ values: [Double]) -> Double? {
        guard !values.isEmpty else { return nil }
        let sorted = values.sorted()
        let middle = sorted.count / 2
        if sorted.count % 2 == 0 {
            return (sorted[middle - 1] + sorted[middle]) / 2
        }
        return sorted[middle]
    }

    // MARK: - 给界面用的结论

    /// 结论文字：谁快、快多少
    var verdictText: String {
        guard let offset = medianOffsetMs else {
            switch state {
            case .idle: return "未启用音频输入"
            case .waitingVideo: return "已收到千周声，等彩条…"
            case .waitingAudio: return "已收到彩条，等千周声…"
            default: return "等待测试信号…"
            }
        }
        let value = abs(offset)
        if value < syncThresholdMs {
            return String(format: "已同步（差 %.1f ms）", value)
        }
        return offset > 0
            ? String(format: "声音快 %.0f ms", value)
            : String(format: "画面快 %.0f ms", value)
    }

    var verdictIsGood: Bool {
        guard let offset = medianOffsetMs else { return false }
        return abs(offset) < syncThresholdMs
    }

    /// 量化误差说明
    var quantizationText: String {
        guard lastFrameInterval > 0 else { return "—" }
        return String(format: "视频帧间隔 %.1f ms（±1 帧量化）", lastFrameInterval * 1000)
    }
}
