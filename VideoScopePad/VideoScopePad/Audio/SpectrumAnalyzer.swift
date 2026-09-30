//
//  SpectrumAnalyzer.swift
//  VideoScopePad
//
//  音频频谱 + 响度（LUFS）。
//
//  频谱：acelerate/vDSP 做 1024 点 FFT（Hann 窗），再聚合成 **1/3 倍频程**频带
//        （20 Hz – 20 kHz，31 段）。为什么用 1/3 倍频程而不是原始 FFT 线：
//        听觉与广播习惯都是按比例分频，1/3 倍频程既好看又稳定，而且与常见
//        音频分析仪（RTA）读数一致。每段带峰值保持与释放ballistics（快起慢落）。
//
//  响度：按 **ITU-R BS.1770 / EBU R128** 的做法：
//        · K 加权 = 高频搁架（+4 dB @ ~1.68 kHz）+ RLB 高通（~38 Hz）；
//          用 RBJ 双二阶按标准给的 f0 / Q / 增益设计，所以**与采样率无关**。
//        · 每 100 ms 累计一次加权均方值，放进环形缓冲：
//          Momentary（400 ms）= 最近 4 块，Short-term（3 s）= 最近 30 块；
//          LUFS = −0.691 + 10·log10(Σ 各声道 G·均方)，立体声 G = 1。
//        · 另给 RMS 与采样峰值（dBFS）做参考。
//
//  这些都是 CPU 计算（音频只有 48 kHz），不占 GPU。
//

import Accelerate
import AVFoundation
import Foundation

final class SpectrumAnalyzer {

    struct Snapshot {
        /// 1/3 倍频程各段电平（dBFS，−90…0），长度固定 31
        var bands: [Float]
        /// 各段中心频率（Hz），与 bands 一一对应
        var frequencies: [Float]
        /// 瞬时响度（400 ms 窗，LUFS）
        var momentaryLUFS: Float
        /// 短时响度（3 s 窗，LUFS）
        var shortTermLUFS: Float
        /// 采样峰值 / RMS（dBFS）
        var peakDBFS: Float
        var rmsDBFS: Float

        static let empty: Snapshot = {
            let bands = [Float](repeating: -90, count: SpectrumAnalyzer.bandCount)
            return Snapshot(bands: bands,
                            frequencies: SpectrumAnalyzer.bandCenters,
                            momentaryLUFS: -70,
                            shortTermLUFS: -70,
                            peakDBFS: -90,
                            rmsDBFS: -90)
        }()
    }

    /// 1/3 倍频程：从 20 Hz 到 20 kHz（含）共 31 段
    static let bandCount = 31
    static let bandCenters: [Float] = {
        var centers: [Float] = []
        let base = Float(pow(10.0, 1.0 / 10.0))            // 每段 ×10^(1/10) ≈ 1.2589（1/3 倍频程）
        var frequency: Float = 20
        for _ in 0..<bandCount {
            centers.append(frequency)
            frequency *= base
        }
        return centers
    }()

    private let fftSize = 1024
    private let log2n: vDSP_Length
    private let fftSetup: FFTSetup

    private var window: [Float]
    private var realBuffer: [Float]
    private var imagBuffer: [Float]
    private var windowed: [Float]
    private var magnitudes: [Float]        // 幅度（dBFS）
    private var bandRanges: [(lower: Int, upper: Int)]
    private var bandLevels: [Float]
    private var bandPeaks: [Float]

    // K 加权（两级双二阶）+ 100 ms 块累计
    private var shelf: KWeightBiquad
    private var highpass: KWeightBiquad
    private var blockSum: Double = 0
    private var blockCount = 0
    private var blockSamples = 0
    private var blockTarget: Int
    private var loudnessBlocks: [Double] = []
    private let shortTermBlocks = 30              // 30 × 100 ms = 3 s
    private let momentaryBlocks = 4               // 4 × 100 ms = 400 ms

    private var sampleRate: Float = 48000
    private var sumSquares: Double = 0
    private var sampleCount = 0
    private var peak: Float = 0

    /// 输出（在音频线程上调用）
    var onSnapshot: ((Snapshot) -> Void)?

    init() throws {
        guard let setup = vDSP_create_fftsetup(vDSP_Length(log2(Float(fftSize))), FFTRadix(kFFTRadix2)) else {
            throw ScopeEngineError.bufferAllocationFailed
        }
        fftSetup = setup
        log2n = vDSP_Length(log2(Float(fftSize)))

        window = [Float](repeating: 0, count: fftSize)
        vDSP_hann_window(&window, vDSP_Length(fftSize), Int32(vDSP_HANN_NORM))
        realBuffer = [Float](repeating: 0, count: fftSize / 2)
        imagBuffer = [Float](repeating: 0, count: fftSize / 2)
        windowed = [Float](repeating: 0, count: fftSize)
        magnitudes = [Float](repeating: -90, count: fftSize / 2)
        bandLevels = [Float](repeating: -90, count: Self.bandCount)
        bandPeaks = [Float](repeating: -90, count: Self.bandCount)
        bandRanges = []
        shelf = KWeightBiquad.shelf(sampleRate: sampleRate)
        highpass = KWeightBiquad.highpass(sampleRate: sampleRate)
        blockTarget = Int(sampleRate * 0.1)
        loudnessBlocks = []
    }

    deinit {
        vDSP_destroy_fftsetup(fftSetup)
    }

    /// 采样率变化时重建（系数与频带映射都依赖采样率）
    func configure(sampleRate: Float) {
        guard sampleRate > 0, sampleRate != self.sampleRate || bandRanges.isEmpty else { return }
        self.sampleRate = sampleRate
        shelf = KWeightBiquad.shelf(sampleRate: sampleRate)
        highpass = KWeightBiquad.highpass(sampleRate: sampleRate)
        blockTarget = max(Int(sampleRate * 0.1), 1)
        blockSamples = 0
        blockSum = 0
        blockCount = 0
        loudnessBlocks.removeAll()

        // FFT bin 频率 = i * sampleRate / fftSize；把每段的 [f/√2^(1/6), f·√2^(1/6)] 映射到 bin
        let binWidth = sampleRate / Float(fftSize)
        let halfBand = Float(pow(2.0, 1.0 / 6.0))
        bandRanges = Self.bandCenters.map { center in
            let lower = max(Int((center / halfBand) / binWidth), 1)
            let upper = min(max(Int((center * halfBand) / binWidth), lower), fftSize / 2 - 1)
            return (lower, upper)
        }
    }

    /// 处理一块音频（单声道或立体声取平均做频谱；响度按声道分别加权后相加）
    func process(channels: UnsafePointer<UnsafeMutablePointer<Float>>,
                 channelCount: Int,
                 frames: Int) {
        guard frames > 0 else { return }

        // ---- 频谱：单声道混合（多声道取平均，保证读数与听感一致）----
        let usable = min(frames, fftSize)
        for index in 0..<fftSize { windowed[index] = 0 }
        for index in 0..<usable {
            var sum: Float = 0
            for channel in 0..<channelCount { sum += channels[channel][index] }
            windowed[index] = sum / Float(channelCount)
        }
        vDSP_vmul(windowed, 1, window, 1, &windowed, 1, vDSP_Length(fftSize))

        windowed.withUnsafeBufferPointer { pointer in
            pointer.baseAddress!.withMemoryRebound(to: DSPComplex.self, capacity: fftSize / 2) { complexPointer in
                realBuffer.withUnsafeMutableBufferPointer { realPointer in
                    imagBuffer.withUnsafeMutableBufferPointer { imagPointer in
                        var split = DSPSplitComplex(realp: realPointer.baseAddress!,
                                                    imagp: imagPointer.baseAddress!)
                        vDSP_ctoz(complexPointer, 2, &split, 1, vDSP_Length(fftSize / 2))
                        vDSP_fft_zrip(fftSetup, &split, 1, log2n, FFTDirection(FFT_FORWARD))
                        // 幅度 → dBFS（归一化到窗长）
                        var scale = Float(1.0) / Float(fftSize)
                        vDSP_vsmul(realPointer.baseAddress!, 1, &scale,
                                   realPointer.baseAddress!, 1, vDSP_Length(fftSize / 2))
                        vDSP_vsmul(imagPointer.baseAddress!, 1, &scale,
                                   imagPointer.baseAddress!, 1, vDSP_Length(fftSize / 2))
                        vDSP_zvabs(&split, 1, &magnitudes, 1, vDSP_Length(fftSize / 2))
                    }
                }
            }
        }

        for index in 0..<(fftSize / 2) {
            magnitudes[index] = 20 * log10f(max(magnitudes[index], 1e-7))
        }

        // 聚合成 1/3 倍频程（各段取最大值，避免窄带信号被平均掉）
        for (band, range) in bandRanges.enumerated() {
            var level: Float = -120
            if range.upper > range.lower {
                for bin in range.lower...range.upper where magnitudes[bin] > level {
                    level = magnitudes[bin]
                }
            }
            // 快起慢落：新值更高就立刻跟上，否则按 24 dB/s 释放
            let release: Float = 24 * Float(usable) / sampleRate
            bandLevels[band] = max(level, bandLevels[band] - release)
            bandPeaks[band] = max(bandLevels[band], bandPeaks[band] - release * 0.5)
        }

        // ---- 响度：K 加权 + 100 ms 块均方 ----
        for index in 0..<frames {
            var weighted: Double = 0
            for channel in 0..<channelCount {
                let value = channels[channel][index]
                let first = shelf.process(value)
                let second = highpass.process(first)
                weighted += Double(second) * Double(second)
            }
            blockSum += weighted
            blockSamples += 1
            if blockSamples >= blockTarget {
                loudnessBlocks.append(blockSum / Double(max(blockSamples, 1)))
                if loudnessBlocks.count > shortTermBlocks { loudnessBlocks.removeFirst(loudnessBlocks.count - shortTermBlocks) }
                blockSum = 0
                blockSamples = 0
            }
        }

        // 峰值 / RMS
        var blockPeak: Float = 0
        var blockSumSquares: Double = 0
        for index in 0..<frames {
            for channel in 0..<channelCount {
                let value = abs(channels[channel][index])
                if value > blockPeak { blockPeak = value }
                blockSumSquares += Double(value) * Double(value)
            }
        }
        peak = max(blockPeak, peak * 0.9)
        sumSquares += blockSumSquares
        sampleCount += frames * channelCount

        guard blockSamples == 0 else { return }        // 只在 100 ms 块边界发布

        let rms = sampleCount > 0 ? (sumSquares / Double(sampleCount)).squareRoot() : 0
        sumSquares = 0
        sampleCount = 0

        var snapshot = Snapshot.empty
        snapshot.bands = bandLevels
        snapshot.peakDBFS = peak > 1e-6 ? 20 * log10f(peak) : -90
        snapshot.rmsDBFS = rms > 1e-6 ? Float(20 * log10(max(rms, 1e-7))) : -90
        snapshot.momentaryLUFS = loudness(from: loudnessBlocks.suffix(momentaryBlocks))
        snapshot.shortTermLUFS = loudness(from: loudnessBlocks)
        onSnapshot?(snapshot)
    }

    private func loudness<S: Collection>(from blocks: S) -> Float where S.Element == Double {
        guard !blocks.isEmpty else { return -70 }
        let mean = blocks.reduce(0, +) / Double(blocks.count)
        guard mean > 1e-10 else { return -70 }
        return Float(-0.691 + 10 * log10(mean))
    }
}

/// BS.1770 的 K 加权滤波器（RBJ 双二阶，按标准给的 f0 / Q / 增益设计，与采样率无关）
struct KWeightBiquad {
    var b0: Float = 1, b1: Float = 0, b2: Float = 0
    var a1: Float = 0, a2: Float = 0
    private var x1: Float = 0, x2: Float = 0, y1: Float = 0, y2: Float = 0

    /// 高频搁架：f0 = 1681.974450955533 Hz、G = +3.999843853973347 dB、Q = 0.7071752369554196
    static func shelf(sampleRate: Float) -> KWeightBiquad {
        design(sampleRate: sampleRate,
               frequency: 1681.974450955533,
               q: 0.7071752369554196,
               gainDB: 3.999843853973347,
               isShelf: true)
    }

    /// RLB 高通：f0 = 38.13547087602444 Hz、Q = 0.5003270373238773
    static func highpass(sampleRate: Float) -> KWeightBiquad {
        design(sampleRate: sampleRate,
               frequency: 38.13547087602444,
               q: 0.5003270373238773,
               gainDB: 0,
               isShelf: false)
    }

    private static func design(sampleRate: Float,
                               frequency: Double,
                               q: Double,
                               gainDB: Double,
                               isShelf: Bool) -> KWeightBiquad {
        var filter = KWeightBiquad()
        guard sampleRate > 0 else { return filter }

        let w0 = 2 * Double.pi * frequency / Double(sampleRate)
        let cosW0 = cos(w0)
        let sinW0 = sin(w0)
        let alpha = sinW0 / (2 * q)
        let a0 = 1 + alpha

        if isShelf {
            let amplitude = pow(10, gainDB / 40)
            let twoSqrtAAlpha = 2 * sqrt(amplitude) * alpha
            filter.b0 = Float((amplitude * ((amplitude + 1) + (amplitude - 1) * cosW0 + twoSqrtAAlpha)) / a0)
            filter.b1 = Float((-2 * amplitude * ((amplitude - 1) + (amplitude + 1) * cosW0)) / a0)
            filter.b2 = Float((amplitude * ((amplitude + 1) + (amplitude - 1) * cosW0 - twoSqrtAAlpha)) / a0)
            filter.a1 = Float((-2 * ((amplitude - 1) - (amplitude + 1) * cosW0)) / a0)
            filter.a2 = Float(((amplitude + 1) - (amplitude - 1) * cosW0 - twoSqrtAAlpha) / a0)
        } else {
            filter.b0 = Float((1 + cosW0) / 2 / a0)
            filter.b1 = Float(-(1 + cosW0) / a0)
            filter.b2 = filter.b0
            filter.a1 = Float(-2 * cosW0 / a0)
            filter.a2 = Float((1 - alpha) / a0)
        }
        return filter
    }

    mutating func process(_ x: Float) -> Float {
        let y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2
        x2 = x1; x1 = x
        y2 = y1; y1 = y
        return y
    }
}
