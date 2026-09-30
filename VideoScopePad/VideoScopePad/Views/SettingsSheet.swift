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
    /// 音频输入设备列表与状态（音柱 / 声画延时）
    @ObservedObject var audio: AudioMonitor
    @Binding var showLUTImporter: Bool

    @Environment(\.dismiss) private var dismiss

    var body: some View {
        NavigationStack {
            Form {
                inputSection
                manualFormatSection
                displaySection
                audioSection
                scopeSection
                assistSection
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
        Section("输入信号（强制显示实测信息）") {
            // ViewBuilder 最多 10 个子视图，用 Group 分组
            Group {
                LabeledContent("设备", value: deviceName)
                LabeledContent("分辨率", value: capture.signal.resolutionText)
                LabeledContent("声明帧率", value: capture.signal.frameRateText + "p")
                LabeledContent("实测帧率", value: String(format: "%.1f fps", capture.stats.fps))
                LabeledContent("像素格式", value: capture.signal.pixelFormat)
                LabeledContent("量化范围", value: capture.signal.rangeText)
                LabeledContent("色彩原色", value: capture.signal.primaries)
                LabeledContent("传输函数", value: capture.signal.transfer)
                LabeledContent("YCbCr 矩阵", value: capture.signal.matrix)
                LabeledContent("丢帧", value: "\(capture.stats.droppedFrames)")
            }

            Toggle("自动选择输入格式（推荐）", isOn: $settings.autoFormat)

            Text("""
            自动模式会自己挑最合适的格式（1080p 优先、帧率高的优先、未压缩优先），不需要手动选分辨率与帧率。
            上面显示的分辨率 / 帧率是**实际收到的流**；如果采集卡做了帧率转换，信号源本身的帧率无法通过 UVC 读出。
            """)
                .font(.caption)
                .foregroundStyle(.secondary)

            Button {
                capture.refresh()
            } label: {
                Label("刷新设备与格式", systemImage: "arrow.clockwise")
            }
        }
    }

    /// 高级：手动指定输入格式（默认收起来，避免日常误操作）
    private var manualFormatSection: some View {
        Section("高级：手动指定输入格式") {
            Picker("当前格式", selection: manualFormatBinding) {
                ForEach(capture.formats.prefix(60)) { format in
                    Text(format.displayName).tag(format.id)
                }
            }
            .disabled(capture.formats.isEmpty)

            Button {
                settings.autoFormat = true
                capture.useAutomaticFormat()
            } label: {
                Label("恢复自动跟随输入信号", systemImage: "wand.and.stars")
            }
        }
    }

    private var manualFormatBinding: Binding<String> {
        Binding(
            get: { capture.selectedFormatID ?? capture.formats.first?.id ?? "" },
            set: { newValue in
                guard !newValue.isEmpty else { return }
                settings.autoFormat = false
                capture.select(formatID: newValue)
            }
        )
    }

    // MARK: - 音频（音柱 / 声画延时）

    private var audioSection: some View {
        Section("音频（音柱 / 声画延时）") {
            Toggle("启用音频输入", isOn: $settings.audioEnabled)

            if settings.audioEnabled {
                Toggle("画面两侧显示音柱", isOn: $settings.showAudioMeters)

                Picker("音频输入", selection: $settings.audioInputID) {
                    Text("系统默认").tag("")
                    ForEach(audio.inputOptions) { option in
                        Text(option.name).tag(option.id)
                    }
                }

                LabeledContent("当前输入", value: audio.isRunning ? audio.inputName : audio.statusText)
                LabeledContent("1 kHz 电平", value: String(format: "%.0f dBFS", audio.toneLevelDB))

                HStack {
                    Text("测量补偿")
                    Spacer()
                    Stepper(value: $settings.avSyncCompensationMs, in: -100...100, step: 1) {
                        Text(String(format: "%+.0f ms", settings.avSyncCompensationMs))
                            .font(.system(.body, design: .monospaced))
                    }
                }

                Text("""
                · 音柱是左右两个电平柱（L/R，-60…0 dBFS）；只有单声道输入时显示一根。
                · 声画延时测量需要测试设备周期输出「静音黑场 → 千周声 + 彩条」。
                  测量用音频起音（1 kHz，采样级）与彩条出现的那一帧（约 ±1 帧）相减。
                · 想让读数最准，音频输入请选**与画面同一路来源**（采集卡的 HDMI 内嵌音频 / USB 音频）；
                  用 iPad 麦克风拾音会把声程（约 3 ms/米）算进去，可用「测量补偿」抵消。
                · 若读数有一个固定的系统偏差，也可以用「测量补偿」一次性校零。
                """)
                    .font(.caption2)
                    .foregroundStyle(.secondary)
            } else {
                Text("打开后会请求麦克风/音频输入权限。")
                    .font(.caption2)
                    .foregroundStyle(.secondary)
            }
        }
    }

    // MARK: - 看守辅助（峰值保持 / 斑马纹 / 报警）

    private var assistSection: some View {
        Section("看守辅助") {
            if settings.showsPeakHoldOption {
                Toggle("峰值保持游标", isOn: $settings.peakHoldEnabled)
                LabeledSlider(title: "峰值保持时间（秒）", value: $settings.peakHoldSeconds, range: 0.5...15, format: "%.1f")
            }

            if settings.showsZebraOption {
                Toggle("超白斑马纹", isOn: $settings.zebraEnabled)
                LabeledSlider(title: "斑马纹阈值（IRE）", value: $settings.zebraThresholdIRE, range: 60...109, format: "%.0f")
                Toggle("黑切割斑马纹（<0 IRE）", isOn: $settings.zebraBlackEnabled)
            }

            Toggle("超标报警红框 + 振动", isOn: $settings.warningAlarmEnabled)
            Picker("报警确认门槛", selection: $settings.warningRaiseCount) {
                Text("1 次（最灵敏）").tag(1)
                Text("3 次（推荐）").tag(3)
                Text("6 次").tag(6)
                Text("12 次（最稳）").tag(12)
            }

            if !settings.showsPeakHoldOption && !settings.showsZebraOption {
                Text("切到带画面 / 波形格子的布局后，这里会出现对应的斑马纹与峰值保持选项。")
                    .font(.caption)
                    .foregroundStyle(.secondary)
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

            Picker("画面方向", selection: $settings.pictureRotation) {
                ForEach(PictureRotation.allCases) { rotation in
                    Text(rotation.title).tag(rotation)
                }
            }
            Text(settings.pictureRotation.detail)
                .font(.caption2)
                .foregroundStyle(.secondary)

            Picker("显示通道", selection: $settings.displayMode) {
                ForEach(DisplayMode.allCases) { mode in
                    Text(mode.title).tag(mode)
                }
            }

            Toggle("顶部显示信号 / 读数信息行", isOn: $settings.showHUD)
            Toggle("监视时防止息屏", isOn: $settings.preventSleep)

            Toggle("布局调试叠加层（每格边界与尺寸）", isOn: $settings.showLayoutDebug)
            if settings.showLayoutDebug {
                Text("打开后画面上会画出每个格子的实际边界：绿=格子、青=示波器绘图区、黄=刻度栏、品红=画面区，并标出尺寸。排查「不居中 / 显示不全 / 跑到别的格子」时截图即可。")
                    .font(.caption2)
                    .foregroundStyle(.secondary)
            }
        }
    }

    // MARK: - 示波器

    private var scopeSection: some View {
        Section("示波器") {
            // 这三个开关只在「底部 / 右侧 / 叠加」预设下生效（全屏与四分割按格子内容走）
            if settings.monitorLayout == .fullscreen || settings.monitorLayout == .quad {
                Text(settings.monitorLayout == .quad
                     ? "四分割每格的内容在画面格子里选（或控制栏按格子选）。"
                     : "全屏内容在控制栏选：画面或任一种示波器。")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            } else {
                Group {
                    Toggle("矢量示波器", isOn: $settings.showVectorscope)
                    Toggle("波形（亮度 / RGB）", isOn: $settings.showWaveform)
                    Toggle("RGB Parade", isOn: $settings.showParade)
                }
            }

            if settings.showsWaveformModeOption {
                Picker("波形模式", selection: $settings.waveformMode) {
                    ForEach(WaveformMode.allCases) { mode in
                        Text(mode.title).tag(mode)
                    }
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

            if settings.showsScaleUnitOption {
                Picker("侧边刻度单位", selection: $settings.scaleUnit) {
                    ForEach(ScaleUnit.allCases) { unit in
                        Text(unit.title).tag(unit)
                    }
                }
            }

            Text("""
            数值读数取自与示波器相同的取样点（当前：\(settings.scopeSource.title)），每秒更新约 10 次，
            给出峰值白、黑位、平均电平、R/G/B 分量峰值、色度峰值，以及超白/超黑的像素占比。
            刻度单位可切 IRE / mV / %：mV 是按广播规范换算的等效电平（100 IRE = 700 mV）。
            真实模拟电压（0.7Vpp / 同步头）不通过 UVC 暴露，任何 iPad 应用都读不到。
            """)
                .font(.caption)
                .foregroundStyle(.secondary)

            // ViewBuilder 最多 10 个子视图，用 Group 分组
            Group {
                if settings.showsAnyScopePane {
                    LabeledSlider(title: "轨迹亮度", value: $settings.scopeIntensity, range: 0.2...3, format: "%.2f")
                }
                if settings.showsVectorscopeGainOption {
                    LabeledSlider(title: "矢量图放大", value: $settings.vectorscopeGain, range: 0.5...4, format: "%.2f")
                }
                if settings.monitorLayout == .overlay {
                    LabeledSlider(title: "叠加模式不透明度", value: $settings.scopeOpacity, range: 0.3...1, format: "%.2f")
                }

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

                Button {
                    settings.resetPaneContents()
                } label: {
                    Label("复位格子内容", systemImage: "square.grid.2x2")
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
