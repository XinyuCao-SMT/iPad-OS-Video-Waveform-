//
//  SignalMeasurement.swift
//  VideoScopePad
//
//  信号幅度「数值读数」。
//
//  原理：GPU 每 N 帧把采样像素累加进一块很小的全局直方图缓冲区
//  （4 个通道各 256 bin + 64 bin 色度半径直方图），CPU 回读后精确算出：
//    · 峰值白 / 稳定白（99.9 分位，去噪点）/ 黑位 / 平均电平（IRE）
//    · 超白、超黑像素占比
//    · R / G / B 三个通道各自的峰值与黑位（用来查分量是否超范围）
//    · 色度峰值（矢量图半径 → 饱和度百分比）
//    · 是否整帧全黑（判断采集卡到底有没有在收信号）
//
//  注意：这里测的是「已经数字化、并经过 YUV->R'G'B' 转换后的码值幅度」。
//  采集卡内部的模拟电平（0.7Vpp、同步头幅度）以及 HDMI 是否丢信号的状态位
//  都不通过 UVC 暴露给 App，任何 iPad 应用都读不到。
//

import Combine
import Foundation

struct SignalMeasurement: Equatable {

    /// 绝对峰值白（IRE）——含噪点，仅作参考
    var peakWhiteIRE: Double = 0
    /// 稳定白：从最高码值往下累计到 0.1% 像素处的码值，去噪点后更接近真实白电平
    var stableWhiteIRE: Double = 0
    /// 绝对最低码值（IRE）
    var blackLevelIRE: Double = 0
    /// 稳定黑：从最低码值往上累计到 0.1% 像素处
    var stableBlackIRE: Double = 0
    /// 整帧平均电平（IRE）
    var averageIRE: Double = 0

    /// 高于 100 IRE 的像素占比（%）
    var aboveWhitePercent: Double = 0
    /// 低于 0 IRE 的像素占比（%）
    var belowBlackPercent: Double = 0

    var redPeakIRE: Double = 0
    var greenPeakIRE: Double = 0
    var bluePeakIRE: Double = 0

    var redBlackIRE: Double = 0
    var greenBlackIRE: Double = 0
    var blueBlackIRE: Double = 0

    /// 色度峰值（100% = 单通道满幅，超过 100% 即超出 BT.709 色域边界）
    var peakSaturationPercent: Double = 0

    /// 参与统计的采样像素数
    var sampledPixels: Int = 0

    /// 有帧但整帧全黑：多半是信号源没有输出，或者被 HDCP 挡了
    var isBlackFrame = false

    /// 已经命中的异常项（用于界面直接显示）
    var warnings: [String] = []

    var dynamicRangeIRE: Double { max(stableWhiteIRE - stableBlackIRE, 0) }
    var hasWarnings: Bool { !warnings.isEmpty }
}

/// 数值读数的发布槽（独立于 RenderCoordinator，避免 10Hz 刷新带动整个界面重建）
final class MeasurementHub: ObservableObject {
    @Published var value: SignalMeasurement?
}

enum SignalMeasurementBuilder {

    /// 超白/超黑判定的像素占比阈值（%）
    private static let percentThreshold = 0.05
    /// 稳定白/稳定的分位（0.1%）
    private static let tailFraction = 0.001

    static func make(counts: [UInt32], isVideoRange: Bool) -> SignalMeasurement? {
        let bins = Int(VS_MEASURE_BINS)
        let required = Int(VS_MEASURE_UINT_COUNT)
        guard counts.count >= required, bins > 1 else { return nil }

        // 1) 亮度平面（3）决定总量与总体读数
        let whiteThreshold = isVideoRange ? 235 : 254
        let blackThreshold = isVideoRange ? 16 : 1

        guard let luma = stats(counts: counts,
                               offset: 3 * bins,
                               bins: bins,
                               highThreshold: whiteThreshold,
                               lowThreshold: blackThreshold) else {
            return nil
        }

        func ire(_ code: Double) -> Double {
            isVideoRange ? (code - 16.0) / 219.0 * 100.0 : code / 255.0 * 100.0
        }

        var measurement = SignalMeasurement()
        measurement.sampledPixels = Int(luma.total)
        measurement.peakWhiteIRE = ire(luma.peakCode)
        measurement.stableWhiteIRE = ire(luma.stablePeakCode)
        measurement.blackLevelIRE = ire(luma.blackCode)
        measurement.stableBlackIRE = ire(luma.stableBlackCode)
        measurement.averageIRE = ire(luma.meanCode)
        measurement.aboveWhitePercent = percent(luma.highCount, of: luma.total)
        measurement.belowBlackPercent = percent(luma.lowCount, of: luma.total)

        // 2) R / G / B 分量
        if let red = stats(counts: counts, offset: 0, bins: bins,
                           highThreshold: whiteThreshold, lowThreshold: blackThreshold) {
            measurement.redPeakIRE = ire(red.stablePeakCode)
            measurement.redBlackIRE = ire(red.stableBlackCode)
        }
        if let green = stats(counts: counts, offset: bins, bins: bins,
                             highThreshold: whiteThreshold, lowThreshold: blackThreshold) {
            measurement.greenPeakIRE = ire(green.stablePeakCode)
            measurement.greenBlackIRE = ire(green.stableBlackCode)
        }
        if let blue = stats(counts: counts, offset: 2 * bins, bins: bins,
                            highThreshold: whiteThreshold, lowThreshold: blackThreshold) {
            measurement.bluePeakIRE = ire(blue.stablePeakCode)
            measurement.blueBlackIRE = ire(blue.stableBlackCode)
        }

        // 3) 色度峰值
        measurement.peakSaturationPercent = saturation(counts: counts,
                                                       total: luma.total,
                                                       radialIndex: Int(VS_MEASURE_RADIAL_INDEX),
                                                       radialBins: Int(VS_MEASURE_RADIAL_BINS))

        // 4) 判定
        measurement.isBlackFrame = measurement.stableWhiteIRE < 3 && measurement.averageIRE < 1

        var warnings: [String] = []
        if measurement.isBlackFrame {
            warnings.append("整帧全黑")
        }
        if measurement.aboveWhitePercent > percentThreshold {
            warnings.append(String(format: "超白 %.2f%%", measurement.aboveWhitePercent))
        }
        if measurement.belowBlackPercent > percentThreshold {
            warnings.append(String(format: "超黑 %.2f%%", measurement.belowBlackPercent))
        }
        if measurement.stableWhiteIRE > 103 {
            warnings.append(String(format: "白电平偏高 %.0f IRE", measurement.stableWhiteIRE))
        }
        if measurement.stableBlackIRE < -2 {
            warnings.append(String(format: "黑位被压缩 %.0f IRE", measurement.stableBlackIRE))
        }
        if measurement.stableBlackIRE > 8 {
            warnings.append(String(format: "黑位抬高 %.0f IRE", measurement.stableBlackIRE))
        }
        if measurement.peakSaturationPercent > 105 {
            warnings.append(String(format: "色度超范围 %.0f%%", measurement.peakSaturationPercent))
        }
        measurement.warnings = warnings

        return measurement
    }

    // MARK: - 单通道统计

    private struct PlaneStats {
        var total: UInt32 = 0
        var peakCode: Double = 0
        var stablePeakCode: Double = 0
        var blackCode: Double = 0
        var stableBlackCode: Double = 0
        var meanCode: Double = 0
        var highCount: UInt32 = 0
        var lowCount: UInt32 = 0
    }

    private static func stats(counts: [UInt32],
                              offset: Int,
                              bins: Int,
                              highThreshold: Int,
                              lowThreshold: Int) -> PlaneStats? {
        guard offset >= 0, offset + bins <= counts.count else { return nil }

        var total: UInt32 = 0
        for index in 0..<bins {
            total &+= counts[offset + index]
        }
        guard total > 0 else { return nil }

        var result = PlaneStats()
        result.total = total

        // 绝对峰值 / 绝对黑位
        var peak = 0
        for index in stride(from: bins - 1, through: 0, by: -1) where counts[offset + index] > 0 {
            peak = index
            break
        }
        var black = bins - 1
        for index in 0..<bins where counts[offset + index] > 0 {
            black = index
            break
        }

        // 稳定值：掐掉两端的 0.1%
        let tail = max(UInt32(1), UInt32(Double(total) * tailFraction))

        var cumulativeHigh: UInt32 = 0
        var stablePeak = peak
        for index in stride(from: bins - 1, through: 0, by: -1) {
            cumulativeHigh &+= counts[offset + index]
            if cumulativeHigh >= tail {
                stablePeak = index
                break
            }
        }

        var cumulativeLow: UInt32 = 0
        var stableBlack = black
        for index in 0..<bins {
            cumulativeLow &+= counts[offset + index]
            if cumulativeLow >= tail {
                stableBlack = index
                break
            }
        }

        // 平均
        var weighted = 0.0
        for index in 0..<bins {
            let count = counts[offset + index]
            if count > 0 {
                weighted += Double(index) * Double(count)
            }
        }

        // 超范围计数
        var high: UInt32 = 0
        if highThreshold + 1 < bins {
            for index in (highThreshold + 1)..<bins {
                high &+= counts[offset + index]
            }
        }
        var low: UInt32 = 0
        let lowUpper = min(max(lowThreshold, 0), bins)
        if lowUpper > 0 {
            for index in 0..<lowUpper {
                low &+= counts[offset + index]
            }
        }

        result.peakCode = Double(peak)
        result.stablePeakCode = Double(stablePeak)
        result.blackCode = Double(black)
        result.stableBlackCode = Double(stableBlack)
        result.meanCode = weighted / Double(total)
        result.highCount = high
        result.lowCount = low
        return result
    }

    private static func saturation(counts: [UInt32],
                                   total: UInt32,
                                   radialIndex: Int,
                                   radialBins: Int) -> Double {
        guard radialBins > 1, radialIndex >= 0, radialIndex + radialBins <= counts.count else {
            return 0
        }
        // 忽略占比低于 0.05% 的零星像素（噪点/压缩边缘）
        let minimum = max(UInt32(1), UInt32(Double(total) * percentThreshold / 100.0))

        var peakBin = 0
        for index in stride(from: radialBins - 1, through: 0, by: -1) {
            if counts[radialIndex + index] >= minimum {
                peakBin = index
                break
            }
        }
        return Double(peakBin) / Double(radialBins - 1) * 100.0
    }

    private static func percent(_ count: UInt32, of total: UInt32) -> Double {
        guard total > 0 else { return 0 }
        return Double(count) / Double(total) * 100.0
    }
}
