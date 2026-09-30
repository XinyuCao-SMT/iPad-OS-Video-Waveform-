//
//  AudioMonitor.swift
//  VideoScopePad
//
//  音频输入：音量柱（L / R）与 1 kHz 测试音的起音检测。
//
//  用途一：画面两侧的音柱。
//  用途二：声画延时（A/V Sync）测量 —— 测试设备周期性发送
//          「静音黑场 → 千周声 + 彩条同时出现 → 消失」，这里负责把**千周声出现的时刻**
//          精确找出来（采样级），交给 AVSyncMeter 与视频侧的彩条出现时刻比对。
//
//  为什么用带通 + 包络而不是 FFT：
//    · 只要测「1 kHz 有没有出现、什么时候出现」，二阶带通（RBJ bandpass，Q≈3）+ 包络检波
//      的计算量极小，可以逐样本跑，起音时刻能精确到亚毫秒；
//    · FFT 需要分帧，帧长/跳长会直接限制时间分辨率。
//
//  时钟：AVAudioTime 的 hostTime 与视频帧的 PTS 都在 iPad 主机时钟上（见 AVSyncMeter 的说明），
//  所以两者的时刻可以直接相减。
//

import AVFoundation
import Foundation

/// 二阶带通（RBJ cookbook），用于挑出 1 kHz 分量
private struct Biquad {
    var b0: Float = 1
    var b1: Float = 0
    var b2: Float = 0
    var a1: Float = 0
    var a2: Float = 0
    var x1: Float = 0
    var x2: Float = 0
    var y1: Float = 0
    var y2: Float = 0

    init?(center: Float, sampleRate: Float, q: Float) {
        guard sampleRate > 0, center > 0, center < sampleRate / 2, q > 0 else { return nil }
        let w0 = 2 * Float.pi * center / sampleRate
        let alpha = sin(w0) / (2 * q)
        let a0 = 1 + alpha
        b0 = alpha / a0
        b1 = 0
        b2 = -alpha / a0
        a1 = -2 * cos(w0) / a0
        a2 = (1 - alpha) / a0
    }

    mutating func process(_ x: Float) -> Float {
        let y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2
        x2 = x1; x1 = x
        y2 = y1; y1 = y
        return y
    }

    mutating func reset() {
        x1 = 0; x2 = 0; y1 = 0; y2 = 0
    }
}

final class AudioMonitor: ObservableObject {

    struct InputOption: Identifiable, Hashable {
        let id: String
        let name: String
    }

    // MARK: - 对外状态

    @Published private(set) var isRunning = false
    @Published private(set) var statusText = "未启动"
    @Published private(set) var inputName = "—"
    @Published private(set) var sampleRate: Double = 0
    @Published private(set) var channelCount = 0
    /// 0…1（-60 dBFS → 0，0 dBFS → 1）
    @Published private(set) var levelLeft: Float = 0
    @Published private(set) var levelRight: Float = 0
    @Published private(set) var peakLeft: Float = 0
    @Published private(set) var peakRight: Float = 0
    @Published private(set) var isClipping = false
    /// 1 kHz 分量电平（dBFS），用来确认测试音真的收到了
    @Published private(set) var toneLevelDB: Float = -120
    /// 声相（李萨如）快照：立体声的 L/R 关系图 + 相关度 + 平衡
    @Published private(set) var phase = PhaseSnapshot.empty

    /// 千周声起音 / 结束（主机时钟秒）
    var onToneOnset: ((TimeInterval) -> Void)?
    var onToneEnd: ((TimeInterval) -> Void)?

    // MARK: - 声相（李萨如）

    struct PhasePoint {
        /// 0…255：横向 = Side = (L − R)/2（右为正）
        var x: UInt8
        /// 0…255：纵向 = Mid = (L + R)/2（上为正）
        var y: UInt8
        /// 0…1，按累计次数对数压缩
        var intensity: Float
    }

    struct PhaseSnapshot {
        var points: [PhasePoint]
        /// 相关系数：+1 完全同相（单声道）、0 无关、−1 完全反相
        var correlation: Float
        /// 平衡：正 = 偏左，负 = 偏右（dB）
        var balanceDB: Float
        var isStereo: Bool

        static let empty = PhaseSnapshot(points: [], correlation: 1, balanceDB: 0, isStereo: false)
    }

    // MARK: - 内部

    private let engine = AVAudioEngine()
    private var biquad: Biquad?
    private var envelope: Float = 0
    private var noiseFloor: Float = 0.0004
    private var aboveCount = 0
    private var belowCount = 0
    private var inBurst = false
    private var burstStart: TimeInterval = 0

    private let processedQueue = DispatchQueue(label: "vsp.audio.analysis")
    private var clipHoldUntil = Date.distantPast

    // 声相直方图：256×256（side × mid），每约 40 ms 清算一次并发布一份快照
    private var phaseBins = [UInt16](repeating: 0, count: 256 * 256)
    private var phasePending = 0
    private let phasePublishInterval = 2048          // 48 kHz 下约 43 ms ≈ 23 fps
    private var sumLR: Double = 0
    private var sumLL: Double = 0
    private var sumRR: Double = 0
    private var sumL2: Double = 0                    // 用于 L/R 平衡
    private var sumR2: Double = 0
    private let maxPhasePoints = 1400                // 每帧最多画这么多点

    /// 起音判据：带通包络要连续超过阈值这么多秒才算「千周声来了」
    private let confirmSeconds: Double = 0.004
    /// 结束判据：低于阈值一半持续这么久才算「千周声走了」
    private let releaseSeconds: Double = 0.012

    // MARK: - 设备与权限

    /// 当前可选的音频输入（界面上的选择器用）
    @Published private(set) var inputOptions: [InputOption] = []

    func refreshInputOptions() {
        inputOptions = Self.availableInputs()
    }

    static func availableInputs() -> [InputOption] {
        let session = AVAudioSession.sharedInstance()
        guard let inputs = session.availableInputs else { return [] }
        return inputs.map { InputOption(id: $0.uid, name: $0.portName) }
    }

    static func requestPermission(_ completion: @escaping (Bool) -> Void) {
        AVAudioApplication.requestRecordPermission { granted in
            DispatchQueue.main.async { completion(granted) }
        }
    }

    static var hasPermission: Bool {
        AVAudioApplication.shared.recordPermission == .granted
    }

    // MARK: - 启动 / 停止

    func start(preferredInputID: String?) {
        guard !isRunning else { return }
        guard Self.hasPermission else {
            statusText = "没有麦克风/音频输入权限"
            return
        }

        let session = AVAudioSession.sharedInstance()
        do {
            // .measurement 关闭系统音效处理（AGC / 降噪），测 1 kHz 测试音更准
            try session.setCategory(.record, mode: .measurement, options: [.allowBluetooth])
            try session.setActive(true)
            if let id = preferredInputID,
               let match = session.availableInputs?.first(where: { $0.uid == id }) {
                try session.setPreferredInput(match)
            }
        } catch {
            statusText = "音频会话失败：\(error.localizedDescription)"
            return
        }

        let input = engine.inputNode
        let format = input.inputFormat(forBus: 0)
        guard format.sampleRate > 0, format.channelCount > 0 else {
            statusText = "音频输入不可用（没有可用的输入设备？）"
            return
        }

        biquad = Biquad(center: 1000, sampleRate: Float(format.sampleRate), q: 3)
        envelope = 0
        inBurst = false

        sampleRate = format.sampleRate
        channelCount = Int(format.channelCount)
        inputName = session.preferredInput?.portName
            ?? session.currentRoute.inputs.first?.portName
            ?? "默认输入"
        statusText = "运行中"
        refreshInputOptions()

        input.removeTap(onBus: 0)
        // 512 帧（48 kHz 约 10.7 ms）——包络按样本扫描，起音时刻不受缓冲区长度影响
        input.installTap(onBus: 0, bufferSize: 512, format: format) { [weak self] buffer, when in
            self?.process(buffer: buffer, when: when)
        }

        do {
            engine.prepare()
            try engine.start()
            isRunning = true
        } catch {
            statusText = "启动音频引擎失败：\(error.localizedDescription)"
        }
    }

    func stop() {
        guard isRunning else { return }
        engine.inputNode.removeTap(onBus: 0)
        engine.stop()
        isRunning = false
        statusText = "已停止"
        levelLeft = 0; levelRight = 0; peakLeft = 0; peakRight = 0
        toneLevelDB = -120
        try? AVAudioSession.sharedInstance().setActive(false)
    }

    // MARK: - 分析（音频线程）

    private func process(buffer: AVAudioPCMBuffer, when: AVAudioTime) {
        guard let channels = buffer.floatChannelData else { return }
        let frames = Int(buffer.frameLength)
        guard frames > 0 else { return }

        let rate = Float(buffer.format.sampleRate)
        let startHost = when.isHostTimeValid
            ? AVAudioTime.seconds(forHostTime: when.hostTime)
            : AVAudioTime.seconds(forHostTime: mach_absolute_time())

        // ---- 音量柱（每声道 RMS → dBFS → 0…1）----
        let channelCount = Int(buffer.format.channelCount)
        var levels: [Float] = []
        var peak: Float = 0
        for channel in 0..<channelCount {
            let data = channels[channel]
            var sum: Float = 0
            for index in 0..<frames {
                let value = data[index]
                sum += value * value
                if abs(value) > peak { peak = abs(value) }
            }
            let rms = sqrtf(sum / Float(frames))
            let db = rms > 1e-6 ? 20 * log10f(rms) : -120
            levels.append(min(max((db + 60) / 60, 0), 1))
        }

        // ---- 声相（李萨如）：按样本累加 (Side, Mid) 直方图，并累计相关度统计量 ----
        let isStereo = channelCount > 1
        let right = isStereo ? channels[1] : channels[0]
        var phaseReady = false

        for index in 0..<frames {
            let l = channels[0][index]
            let r = right[index]

            let side = (l - r) * 0.5
            let mid = (l + r) * 0.5
            let bx = Int(min(max((side + 1) * 0.5, 0), 1) * 255)
            let by = Int(min(max((mid + 1) * 0.5, 0), 1) * 255)
            phaseBins[by * 256 + bx] &+= 1

            sumLR += Double(l) * Double(r)
            sumLL += Double(l) * Double(l)
            sumRR += Double(r) * Double(r)
            sumL2 += Double(l) * Double(l)
            sumR2 += Double(r) * Double(r)
        }

        phasePending += frames
        if phasePending >= phasePublishInterval {
            phasePending = 0
            phaseReady = true
        }

        // ---- 1 kHz 起音检测（逐样本带通 + 包络）----
        var onset: TimeInterval?
        var end: TimeInterval?
        if var filter = biquad {
            let decay = expf(-1 / (rate * 0.002))          // 2 ms 释放
            let confirmSamples = Int(rate * Float(confirmSeconds))
            let releaseSamples = Int(rate * Float(releaseSeconds))

            for index in 0..<frames {
                let filtered = abs(filter.process(channels[0][index]))
                envelope = filtered > envelope ? filtered : envelope * decay

                let threshold = max(noiseFloor * 8, 0.0015)
                if envelope > threshold {
                    aboveCount += 1
                    belowCount = 0
                    if !inBurst, aboveCount >= confirmSamples {
                        inBurst = true
                        let offsetSamples = aboveCount - 1
                        burstStart = startHost + Double(index - offsetSamples) / Double(rate)
                        onset = burstStart
                    }
                } else {
                    belowCount += 1
                    if inBurst, belowCount >= releaseSamples {
                        inBurst = false
                        end = startHost + Double(index) / Double(rate)
                    }
                    if !inBurst { aboveCount = 0 }
                    // 没有音时缓慢跟踪本底噪声
                    noiseFloor = noiseFloor * 0.999 + envelope * 0.001
                }
            }
            biquad = filter
        }

        let toneDB = envelope > 1e-6 ? 20 * log10f(envelope) : -120
        let clipped = peak >= 0.999
        if clipped { clipHoldUntil = Date().addingTimeInterval(2) }
        let showClip = Date() < clipHoldUntil

        // ---- 清算声相直方图并生成快照 ----
        var snapshot: PhaseSnapshot?
        if phaseReady {
            snapshot = makePhaseSnapshot(isStereo: isStereo)
            phaseBins.withUnsafeMutableBufferPointer { buffer in
                if let base = buffer.baseAddress {
                    memset(base, 0, buffer.count * MemoryLayout<UInt16>.size)
                }
            }
            sumLR = 0; sumLL = 0; sumRR = 0; sumL2 = 0; sumR2 = 0
        }

        DispatchQueue.main.async { [weak self] in
            guard let self else { return }
            self.toneLevelDB = toneDB
            self.isClipping = showClip
            let left = levels.first ?? 0
            let rightLevel = levels.count > 1 ? levels[1] : left
            self.levelLeft = left
            self.levelRight = rightLevel
            // 峰值保持：缓慢回落
            self.peakLeft = max(left, self.peakLeft * 0.97)
            self.peakRight = max(rightLevel, self.peakRight * 0.97)
            if let snapshot { self.phase = snapshot }
        }

        if let onset { onToneOnset?(onset) }
        if let end { onToneEnd?(end) }
    }

    /// 把直方图压成点列表（只保留非零点，按强度对数压缩），并算相关度与平衡
    private func makePhaseSnapshot(isStereo: Bool) -> PhaseSnapshot {
        var counts: [(index: Int, count: UInt16)] = []
        counts.reserveCapacity(maxPhasePoints)
        var maxCount: UInt16 = 1

        for index in 0..<phaseBins.count {
            let count = phaseBins[index]
            if count == 0 { continue }
            if count > maxCount { maxCount = count }
            if counts.count < maxPhasePoints {
                counts.append((index, count))
            }
        }

        let denominator = log(1 + Double(maxCount))
        let points = counts.map { item -> PhasePoint in
            let intensity = denominator > 0 ? log(1 + Double(item.count)) / denominator : 0
            return PhasePoint(x: UInt8(item.index & 0xFF),
                              y: UInt8((item.index >> 8) & 0xFF),
                              intensity: Float(intensity))
        }

        // 相关系数
        var correlation: Float = 1
        let denominator2 = (sumLL * sumRR).squareRoot()
        if denominator2 > 1e-9 {
            correlation = Float(min(max(sumLR / denominator2, -1), 1))
        }

        // 平衡（正 = 偏左）
        var balance: Float = 0
        if sumR2 > 1e-9, sumL2 > 1e-9 {
            balance = Float(10 * log10(sumL2 / sumR2))
        }

        return PhaseSnapshot(points: points,
                             correlation: correlation,
                             balanceDB: min(max(balance, -60), 60),
                             isStereo: isStereo)
    }
}
