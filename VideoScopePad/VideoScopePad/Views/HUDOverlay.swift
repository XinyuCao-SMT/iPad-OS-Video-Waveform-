//
//  HUDOverlay.swift
//  VideoScopePad
//
//  画面上的信息层：设备/格式/帧率/色彩矩阵/LUT/丢帧 + 状态提示。
//

import SwiftUI

struct HUDOverlay: View {

    @ObservedObject var capture: CaptureController
    @ObservedObject var settings: AppSettings
    @ObservedObject var lutStore: LUTStore
    @ObservedObject var measurement: MeasurementHub

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(alignment: .top, spacing: 6) {
                chips
                Spacer(minLength: 8)
                if settings.showMeasurement {
                    measurementPanel
                }
                if settings.freeze {
                    ChipLabel(title: "已冻结", systemImage: "pause.fill", tint: .orange)
                }
            }

            Spacer(minLength: 0)

            if let message = capture.statusMessage {
                Text(message)
                    .font(.system(size: 12))
                    .foregroundStyle(.white.opacity(0.88))
                    .padding(.horizontal, 10)
                    .padding(.vertical, 8)
                    .background(Color.black.opacity(0.55))
                    .clipShape(RoundedRectangle(cornerRadius: 8, style: .continuous))
            }
        }
        .padding(10)
        .allowsHitTesting(false)
    }

    // MARK: - 信号幅度数值读数

    @ViewBuilder
    private var measurementPanel: some View {
        if let value = measurement.value {
            VStack(alignment: .leading, spacing: 3) {
                HStack(spacing: 6) {
                    Text("信号幅度")
                        .font(.system(size: 11, weight: .semibold))
                        .foregroundStyle(.white.opacity(0.85))
                    Text(settings.scopeSource == .postLUT ? "LUT 后" : "LUT 前")
                        .font(.system(size: 10))
                        .foregroundStyle(.white.opacity(0.45))
                    Text(settings.scaleUnit == .millivolt ? "等效 mV" : settings.scaleUnit.shortTitle)
                        .font(.system(size: 10, weight: .medium))
                        .foregroundStyle(.white.opacity(0.55))
                }

                divider

                // ViewBuilder 最多 10 个子视图，用 Group 分组
                Group {
                    row("峰值白", settings.scaleUnit.formatPrecise(value.stableWhiteIRE),
                        warn: value.stableWhiteIRE > 103)
                    row("最高码值", settings.scaleUnit.formatPrecise(value.peakWhiteIRE), warn: false)
                    row("黑位", settings.scaleUnit.formatPrecise(value.stableBlackIRE),
                        warn: value.stableBlackIRE < -2 || value.stableBlackIRE > 8)
                    row("平均", settings.scaleUnit.formatPrecise(value.averageIRE), warn: false)
                    row("动态范围", settings.scaleUnit.formatPrecise(value.dynamicRangeIRE), warn: false)
                }

                divider

                Group {
                    row("R / G / B",
                        String(format: "%.0f / %.0f / %.0f",
                               settings.scaleUnit.value(fromIRE: value.redPeakIRE),
                               settings.scaleUnit.value(fromIRE: value.greenPeakIRE),
                               settings.scaleUnit.value(fromIRE: value.bluePeakIRE)),
                        warn: max(value.redPeakIRE, max(value.greenPeakIRE, value.bluePeakIRE)) > 103)
                    row("色度峰值", String(format: "%.0f%%", value.peakSaturationPercent),
                        warn: value.peakSaturationPercent > 105)
                    row("超白 / 超黑",
                        String(format: "%.2f%% / %.2f%%", value.aboveWhitePercent, value.belowBlackPercent),
                        warn: value.aboveWhitePercent > 0.05 || value.belowBlackPercent > 0.05)
                }

                if !warningText.isEmpty {
                    divider
                    HStack(spacing: 4) {
                        Image(systemName: measurement.activeWarnings.isEmpty
                              ? "exclamationmark.triangle.fill"
                              : "exclamationmark.octagon.fill")
                            .font(.system(size: 10))
                        Text(warningText)
                            .font(.system(size: 11, weight: .semibold))
                    }
                    .foregroundStyle(measurement.activeWarnings.isEmpty ? Color.orange : Color.red)
                }
            }
            .padding(.horizontal, 9)
            .padding(.vertical, 7)
            .background(Color.black.opacity(0.55))
            .clipShape(RoundedRectangle(cornerRadius: 8, style: .continuous))
        }
    }

    private var divider: some View {
        Rectangle()
            .fill(Color.white.opacity(0.14))
            .frame(height: 1)
    }

    /// 报警文案：优先显示「已确认」的（边沿触发锁存），否则显示当前瞬时的
    private var warningText: String {
        if !measurement.activeWarnings.isEmpty {
            return "已确认：" + measurement.activeWarnings.joined(separator: " · ")
        }
        return (measurement.value?.warnings ?? []).joined(separator: " · ")
    }

    private func row(_ title: String, _ value: String, warn: Bool) -> some View {
        HStack(spacing: 8) {
            Text(title)
                .font(.system(size: 11))
                .foregroundStyle(.white.opacity(0.6))
            Spacer(minLength: 8)
            Text(value)
                .font(.system(size: 11, weight: .medium, design: .monospaced))
                .foregroundStyle(warn ? Color.orange : Color.white.opacity(0.92))
        }
        .frame(minWidth: 178, alignment: .leading)
    }

    private var chips: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack(spacing: 6) {
                ChipLabel(title: deviceName,
                          systemImage: "video.fill",
                          isActive: capture.hasExternalDevice,
                          tint: .green)

                if capture.stats.isRunning {
                    ChipLabel(title: signalResolutionText)
                    ChipLabel(title: "实测 " + String(format: "%.1f fps", capture.stats.fps),
                              isActive: capture.stats.fps > 1)
                }
            }

            HStack(spacing: 6) {
                if capture.stats.isRunning {
                    // 强制显示输入信号信息：像素格式 · 量化范围 · 原色/传输/矩阵
                    ChipLabel(title: "\(capture.signal.pixelFormat) · \(capture.signal.rangeText)")
                    ChipLabel(title: capture.signal.colorSpaceText)
                    if capture.signal.declaredFrameRate > 0 {
                        ChipLabel(title: "声明 " + capture.signal.frameRateText + "p")
                    }
                }

                if settings.lutEnabled, let detail = lutStore.detailText {
                    ChipLabel(title: "LUT \(Int(settings.lutIntensity * 100))%",
                              systemImage: "camera.filters",
                              isActive: true,
                              tint: .blue)
                    ChipLabel(title: detail)
                }

                if !settings.gradeIsNeutral {
                    ChipLabel(title: "已调色", systemImage: "slider.horizontal.3", tint: .purple)
                }

                if capture.stats.droppedFrames > 0 {
                    ChipLabel(title: "丢帧 \(capture.stats.droppedFrames)",
                              systemImage: "exclamationmark.triangle.fill",
                              tint: .orange)
                }
            }
        }
    }

    private var deviceName: String {
        capture.devices.first { $0.id == capture.selectedDeviceID }?.name ?? "未选择设备"
    }

    /// 优先用实际收到的帧尺寸（采集卡换输入时会变）
    private var signalResolutionText: String {
        capture.signal.resolutionText
    }

    private var formatText: String {
        if capture.stats.width > 0 {
            return "\(capture.stats.width)×\(capture.stats.height)"
        }
        return "\(Int(capture.videoSize.width))×\(Int(capture.videoSize.height))"
    }
}
