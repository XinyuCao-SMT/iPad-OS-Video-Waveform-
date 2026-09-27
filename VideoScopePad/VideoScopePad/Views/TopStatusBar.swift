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
}
