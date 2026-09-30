//
//  TopStatusBar.swift
//  VideoScopePad
//
//  顶部信息行的内容（原来的 HUD 覆盖层）。
//
//  以前这些信息是画在画面上方的，会挡住画面；现在统一收到最顶端一行：
//    · 左边是设备与信号信息（分辨率 / 帧率 / 像素格式 / 色彩空间 / 声明帧率）
//    · 中间是幅度读数（跟着「读数」开关）与 LUT / 调色 / 丢帧状态
//    · 报警用红色 chip 标出来
//  一行放不下时横向滚动，不再压在画面上。
//

import SwiftUI

/// 品牌 logo（资源名 BrandLogo，来自 Assets.xcassets）。
/// 固定放在顶部一行的最左边，不随信息 chip 横向滚动。
struct BrandLogoView: View {

    var height: CGFloat = 18

    var body: some View {
        Image("BrandLogo")
            .resizable()
            .interpolation(.high)
            .scaledToFit()
            .frame(height: height)
            .accessibilityLabel("SMG SMT")
    }
}

struct TopInfoChips: View {

    @ObservedObject var capture: CaptureController
    @ObservedObject var settings: AppSettings
    @ObservedObject var lutStore: LUTStore
    @ObservedObject var measurement: MeasurementHub

    var body: some View {
        HStack(spacing: 6) {
            ChipLabel(title: deviceName,
                      systemImage: "video.fill",
                      isActive: capture.hasExternalDevice,
                      tint: .green)

            if capture.stats.isRunning {
                ChipLabel(title: capture.signal.resolutionText)
                ChipLabel(title: "实测 " + String(format: "%.1f fps", capture.stats.fps),
                          isActive: capture.stats.fps > 1)
                ChipLabel(title: "\(capture.signal.pixelFormat) · \(capture.signal.rangeText)")
                ChipLabel(title: capture.signal.colorSpaceText)
                if capture.signal.declaredFrameRate > 0 {
                    ChipLabel(title: "声明 " + capture.signal.frameRateText + "p")
                }
            }

            if let value = measurement.value {
                ChipLabel(title: "峰值 " + settings.scaleUnit.formatPrecise(value.stableWhiteIRE),
                          isActive: true)
                ChipLabel(title: "黑位 " + settings.scaleUnit.formatPrecise(value.stableBlackIRE))
                ChipLabel(title: "平均 " + settings.scaleUnit.formatPrecise(value.averageIRE))
                ChipLabel(title: String(format: "色度 %.0f%%", value.peakSaturationPercent))

                // 冻结参考：把抓取那一刻的读数与差值一起摆出来（校色时对着调）
                if settings.freeze, let reference = measurement.reference {
                    ChipLabel(title: "参考 峰 \(settings.scaleUnit.formatPrecise(reference.stableWhiteIRE))"
                               + " · 黑 \(settings.scaleUnit.formatPrecise(reference.stableBlackIRE))"
                               + " · 均 \(settings.scaleUnit.formatPrecise(reference.averageIRE))",
                              systemImage: "pin.fill",
                              isActive: true,
                              tint: .orange)

                    ChipLabel(title: "Δ 峰 " + deltaText(value.stableWhiteIRE,
                                                        reference.stableWhiteIRE)
                               + " · 均 " + deltaText(value.averageIRE, reference.averageIRE),
                              systemImage: "plusminus",
                              tint: deltaTint(value: value, reference: reference))
                }
            }

            if settings.lutEnabled, let detail = lutStore.detailText {
                ChipLabel(title: "LUT \(Int(settings.lutIntensity * 100))% · \(detail)",
                          systemImage: "camera.filters",
                          isActive: true,
                          tint: .blue)
            }

            if !settings.gradeIsNeutral {
                ChipLabel(title: "已调色", systemImage: "slider.horizontal.3", tint: .purple)
            }

            if capture.stats.droppedFrames > 0 {
                ChipLabel(title: "丢帧 \(capture.stats.droppedFrames)",
                          systemImage: "exclamationmark.triangle.fill",
                          tint: .orange)
            }

            if !measurement.activeWarnings.isEmpty {
                ChipLabel(title: "已确认：" + measurement.activeWarnings.joined(separator: " · "),
                          systemImage: "exclamationmark.octagon.fill",
                          isActive: true,
                          tint: .red)
            }
        }
    }

    private var deviceName: String {
        capture.devices.first { $0.id == capture.selectedDeviceID }?.name ?? "未选择设备"
    }

    // MARK: - 冻结参考的差值

    /// 带符号的差值（跟着刻度单位走）
    private func deltaText(_ current: Double, _ reference: Double) -> String {
        let delta = current - reference
        switch settings.scaleUnit {
        case .ire:       return String(format: "%+.1f IRE", delta)
        case .millivolt: return String(format: "%+.0f mV", settings.scaleUnit.value(fromIRE: delta))
        case .percent:   return String(format: "%+.1f%%", delta)
        }
    }

    /// 差值很小（校色后基本吻合）显示绿色，明显偏离显示橙色
    private func deltaTint(value: SignalMeasurement, reference: SignalMeasurement) -> Color {
        let white = abs(value.stableWhiteIRE - reference.stableWhiteIRE)
        let black = abs(value.stableBlackIRE - reference.stableBlackIRE)
        let average = abs(value.averageIRE - reference.averageIRE)
        let worst = max(white, max(black, average))
        if worst < 0.5 { return .green }
        if worst < 2.0 { return .yellow }
        return .orange
    }
}
