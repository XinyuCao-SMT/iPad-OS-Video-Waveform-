//
//  SettingsSheet.swift
//  VideoScopePad
//
//  详细设置：输入信息、显示、示波器参数、调色、LUT 管理与说明。
//

import SwiftUI

struct SettingsSheet: View {

    @ObservedObject var settings: AppSettings
    @ObservedObject var capture: CaptureController
    @ObservedObject var lutStore: LUTStore
    @Binding var showLUTImporter: Bool

    @Environment(\.dismiss) private var dismiss

    var body: some View {
        NavigationStack {
            Form {
                inputSection
                displaySection
                scopeSection
                gradeSection
                lutSection
                aboutSection
            }
            .navigationTitle("设置")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .confirmationAction) {
                    Button("完成") { dismiss() }
                }
            }
        }
    }

    // MARK: - 输入

    private var inputSection: some View {
        Section("输入信号") {
            LabeledContent("设备", value: deviceName)
            LabeledContent("格式", value: formatName)
            LabeledContent("像素格式", value: capture.stats.pixelFormatText)
            LabeledContent("色彩矩阵", value: capture.stats.colorMatrixTitle)
            LabeledContent("实测帧率", value: String(format: "%.1f fps", capture.stats.fps))
            LabeledContent("丢帧", value: "\(capture.stats.droppedFrames)")

            Button {
                capture.refresh()
            } label: {
                Label("刷新设备与格式", systemImage: "arrow.clockwise")
            }
        }
    }

    // MARK: - 显示

    private var displaySection: some View {
        Section("显示") {
            Picker("监视器布局", selection: $settings.monitorLayout) {
                ForEach(MonitorLayoutPreset.allCases) { preset in
                    Text(preset.title).tag(preset)
                }
            }

            Picker("画面缩放", selection: $settings.aspectMode) {
                ForEach(AspectMode.allCases) { mode in
                    Text(mode.title).tag(mode)
                }
            }

            Picker("显示通道", selection: $settings.displayMode) {
                ForEach(DisplayMode.allCases) { mode in
                    Text(mode.title).tag(mode)
                }
            }

            Toggle("显示信息层（HUD）", isOn: $settings.showHUD)
            Toggle("监视时防止息屏", isOn: $settings.preventSleep)
        }
    }

    // MARK: - 示波器

    private var scopeSection: some View {
        Section("示波器") {
            Toggle("矢量示波器", isOn: $settings.showVectorscope)
            Toggle("波形（亮度 / RGB）", isOn: $settings.showWaveform)
            Toggle("RGB Parade", isOn: $settings.showParade)

            Picker("波形模式", selection: $settings.waveformMode) {
                ForEach(WaveformMode.allCases) { mode in
                    Text(mode.title).tag(mode)
                }
            }

            Picker("取样位置", selection: $settings.scopeSource) {
                ForEach(ScopeSource.allCases) { source in
                    Text(source.title).tag(source)
                }
            }

            Picker("统计精度", selection: $settings.scopeQuality) {
                ForEach(ScopeQuality.allCases) { quality in
                    Text(quality.title).tag(quality)
                }
            }

            Toggle("显示信号幅度数值读数", isOn: $settings.showMeasurement)

            Text("""
            数值读数取自与示波器相同的取样点（当前：\(settings.scopeSource.title)），每秒更新约 10 次，
            给出峰值白、黑位、平均电平、R/G/B 分量峰值、色度峰值，以及超白/超黑的像素占比。
            它测的是数字化之后的码值幅度，采集卡内部的模拟电平（0.7Vpp / 同步头）无法通过 UVC 读取。
            """)
                .font(.caption)
                .foregroundStyle(.secondary)

            // ViewBuilder 最多 10 个子视图，用 Group 分组
            Group {
                LabeledSlider(title: "轨迹亮度", value: $settings.scopeIntensity, range: 0.2...3, format: "%.2f")
                LabeledSlider(title: "矢量图放大", value: $settings.vectorscopeGain, range: 0.5...4, format: "%.2f")
                LabeledSlider(title: "叠加模式不透明度", value: $settings.scopeOpacity, range: 0.3...1, format: "%.2f")

                Picker("轨迹颜色", selection: $settings.scopeColorIndex) {
                    Text("绿色").tag(0)
                    Text("白色").tag(1)
                    Text("琥珀").tag(2)
                }
                .pickerStyle(.segmented)

                Button {
                    settings.resetScopeSettings()
                } label: {
                    Label("复位示波器设置", systemImage: "arrow.uturn.backward")
                }
            }
        }
    }

    // MARK: - 调色

    private var gradeSection: some View {
        Section("调色（LUT 之后）") {
            LabeledSlider(title: "曝光（档）", value: $settings.exposure, range: -4...4, format: "%.2f")
            LabeledSlider(title: "对比度", value: $settings.contrast, range: 0.2...2.5, format: "%.2f")
            LabeledSlider(title: "饱和度", value: $settings.saturation, range: 0...2.5, format: "%.2f")
            LabeledSlider(title: "伽马", value: $settings.gamma, range: 0.4...2.5, format: "%.2f")
            LabeledSlider(title: "LUT 强度", value: $settings.lutIntensity, range: 0...1, format: "%.2f")

            Button {
                settings.resetGrade()
            } label: {
                Label("复位调色", systemImage: "arrow.uturn.backward")
            }
        }
    }

    // MARK: - LUT

    private var lutSection: some View {
        Section("LUT（.cube）") {
            Toggle("启用 LUT", isOn: $settings.lutEnabled)

            if lutStore.items.isEmpty {
                Text("还没有 LUT。点击下方按钮导入 .cube 文件（支持 1D shaper + 3D LUT，建议 33³）。")
                    .font(.footnote)
                    .foregroundStyle(.secondary)
            } else {
                ForEach(lutStore.items) { item in
                    Button {
                        lutStore.select(id: item.id)
                    } label: {
                        HStack {
                            VStack(alignment: .leading, spacing: 2) {
                                Text(item.name).foregroundStyle(.primary)
                                Text(item.sizeText)
                                    .font(.caption)
                                    .foregroundStyle(.secondary)
                            }
                            Spacer()
                            if lutStore.selectedID == item.id {
                                Image(systemName: "checkmark").foregroundStyle(.tint)
                            }
                        }
                    }
                    .swipeActions {
                        Button(role: .destructive) {
                            lutStore.delete(id: item.id)
                        } label: {
                            Label("删除", systemImage: "trash")
                        }
                    }
                }
            }

            Button {
                showLUTImporter = true
            } label: {
                Label("导入 .cube 文件…", systemImage: "square.and.arrow.down")
            }

            if lutStore.isLoading {
                HStack(spacing: 8) {
                    ProgressView()
                    Text("正在解析 LUT…").font(.footnote)
                }
            }

            if let detail = lutStore.detailText {
                Text("当前：\(detail)").font(.footnote).foregroundStyle(.secondary)
            }

            if let error = lutStore.errorText {
                Text(error).font(.footnote).foregroundStyle(.red)
            }

            Text("也可以把 .cube 放进「文件」App → 本应用 → LUTs 目录，然后回到这里刷新列表。")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }

    // MARK: - 关于

    private var aboutSection: some View {
        Section("关于") {
            LabeledContent("版本", value: appVersion)
            Text("""
            支持 UVC 采集卡的 iPad 监视器 + 示波器。

            • 需要 iPadOS 17 或更高版本（系统从 17 起才允许 App 读取外接 UVC 设备）
            • 需要 USB-C 接口的 iPad（iPad mini 6 及以后）
            • 采集卡供电不足时请使用带外部供电的 USB-C 扩展坞
            • 带 HDCP 保护的信号源无法通过采集卡显示
            • 示波器读数为显示伽马编码的 R'G'B' / Y'，IRE 刻度按当前量化范围（视频/全范围）标注
            """)
                .font(.footnote)
                .foregroundStyle(.secondary)
        }
    }

    // MARK: - 文案

    private var deviceName: String {
        capture.devices.first { $0.id == capture.selectedDeviceID }?.name ?? "未选择"
    }

    private var formatName: String {
        capture.formats.first { $0.id == capture.selectedFormatID }?.displayName ?? "未选择"
    }

    private var appVersion: String {
        let version = Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "1.0"
        let build = Bundle.main.infoDictionary?["CFBundleVersion"] as? String ?? "1"
        return "\(version) (\(build))"
    }
}
