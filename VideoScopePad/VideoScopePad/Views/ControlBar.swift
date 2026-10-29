//
//  ControlBar.swift
//  VideoScopePad
//
//  底部控制栏：设备/格式选择、显示模式、布局、示波器开关、LUT、调色。
//

import SwiftUI

struct ControlBar: View {

    @ObservedObject var settings: AppSettings
    @ObservedObject var capture: CaptureController
    @ObservedObject var lutStore: LUTStore

    @Binding var showSettings: Bool
    @Binding var showLUTImporter: Bool
    /// 清报警与峰值游标（由 ContentView 接到协调器上）
    var onClearAlarms: () -> Void = {}

    /// 冻结 / 清除参考层（由 ContentView 转给 RenderCoordinator）
    var onSetReference: (Bool) -> Void = { _ in }

    @State private var showGrade = false

    /// LUT 相关界面**暂时隐藏**（用户：暂不考虑 LUT）。逻辑与文件都保留，改回 true 即恢复
    private let showsLUTUI = false
    /// 设备选择改成自绘 sheet：SwiftUI 的 Menu 在第二次点击时不弹（实测卡死），不用它了
    @State private var showDevicePicker = false
    /// 格式选择同样不用 Menu（与设备选择同一问题：菜单不弹、界面像卡死）
    @State private var showFormatPicker = false
    // 以下四个原来都是 Menu：SwiftUI 菜单在本 App 里会「点不开 / 第二次不弹」，统一换成
    // confirmationDialog 或 sheet（系统标准呈现，不受该问题影响）
    @State private var showAssistSheet = false
    @State private var showLayoutDialog = false
    @State private var showDisplayDialog = false
    @State private var showWaveformDialog = false
    @State private var showLUTDialog = false

    var body: some View {
        VStack(spacing: 8) {
            rowInput
            rowView
            if showGrade {
                gradeRow
            }
        }
        .padding(.horizontal, 10)
        .padding(.top, 8)
        .padding(.bottom, 10)
        .background(
            Rectangle()
                .fill(.ultraThinMaterial)
                .overlay(Rectangle().fill(Color.white.opacity(0.06)).frame(height: 1), alignment: .top)
        )
    }

    // MARK: - 第一行：输入与视图

    private var rowInput: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 6) {
                deviceMenu
                formatMenu

                if !capture.hasExternalDevice {
                    Button {
                        capture.refresh()
                    } label: {
                        ChipLabel(title: "刷新", systemImage: "arrow.clockwise")
                    }
                    .buttonStyle(.plain)
                }

                Spacer(minLength: 4)

                layoutMenu
                displayModeMenu

                Button {
                    showSettings = true
                } label: {
                    ChipLabel(title: "设置", systemImage: "gearshape.fill")
                }
                .buttonStyle(.plain)
            }
            .frame(height: 32)
        }
        .frame(height: 32)
    }

    private var rowView: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 6) {
                // 一键在「全屏 ⇄ 四分割」之间来回切
                Button {
                    withAnimation(.easeInOut(duration: 0.18)) {
                        settings.monitorLayout = settings.monitorLayout.toggled
                    }
                } label: {
                    ChipLabel(title: settings.monitorLayout.shortTitle,
                              systemImage: settings.monitorLayout.symbolName,
                              isActive: true)
                }
                .buttonStyle(.plain)

                contentPickers

                // 只有当前布局里确实有波形/矢量格子时，才显示对应的二级菜单
                Group {
                    if settings.showsWaveformModeOption {
                        waveformModeMenu
                    }
                    if settings.showsScaleUnitOption {
                        ScaleUnitPicker(unit: $settings.scaleUnit)
                    }
                    assistMenu
                }

                // LUT 相关界面暂时隐藏（showsLUTUI = false）：取样前后、选择 LUT、LUT 开关都不再显示
                if showsLUTUI {
                    Button {
                        settings.scopeSource = settings.scopeSource == .preLUT ? .postLUT : .preLUT
                    } label: {
                        ChipLabel(title: settings.scopeSource == .preLUT ? "取样 LUT 前" : "取样 LUT 后",
                                  systemImage: "scope")
                    }
                    .buttonStyle(.plain)

                    lutMenu

                    Button {
                        settings.lutEnabled.toggle()
                    } label: {
                        ChipLabel(title: settings.lutEnabled ? "LUT 开" : "LUT 关",
                                  systemImage: "camera.filters",
                                  isActive: settings.lutEnabled,
                                  tint: .blue)
                    }
                    .buttonStyle(.plain)
                }

                // 尾部按钮用 Group 包一层，给 ViewBuilder 的 10 个子视图上限留余量
                Group {
                    Button {
                        settings.showMeasurement.toggle()
                    } label: {
                        ChipLabel(title: "读数",
                                  systemImage: "ruler",
                                  isActive: settings.showMeasurement,
                                  tint: .teal)
                    }
                    .buttonStyle(.plain)

                    Button {
                        onSetReference(!settings.freeze)
                    } label: {
                        ChipLabel(title: settings.freeze ? "清除参考" : "冻结参考",
                                  systemImage: settings.freeze ? "pin.slash.fill" : "pin.fill",
                                  isActive: settings.freeze,
                                  tint: .orange)
                    }
                    .buttonStyle(.plain)

                    Button {
                        withAnimation(.easeInOut(duration: 0.18)) {
                            showGrade.toggle()
                        }
                    } label: {
                        ChipLabel(title: "调色", systemImage: "slider.horizontal.3", isActive: showGrade)
                    }
                    .buttonStyle(.plain)
                }
            }
            .frame(height: 32)
        }
        .frame(height: 32)
    }

    /// 看守辅助：只显示当前布局用得上的项
    /// 格式选择列表（替代 Menu）
    private var formatPickerSheet: some View {
        NavigationStack {
            List {
                Section {
                    Button {
                        settings.autoFormat = true
                        capture.useAutomaticFormat()
                        showFormatPicker = false
                    } label: {
                        Label("自动跟随输入信号", systemImage: "wand.and.stars")
                    }
                }
                Section("格式（只列 1920×1080）") {
                    if capture.formats.isEmpty {
                        Text("该设备没有可用格式").foregroundStyle(.secondary)
                    }
                    ForEach(capture.formats) { format in
                        Button {
                            HangLog.mark("选中格式：\(format.displayName)")
                            capture.select(formatID: format.id)
                            showFormatPicker = false
                        } label: {
                            HStack {
                                Text(format.displayName)
                                Spacer()
                                if capture.selectedFormatID == format.id {
                                    Image(systemName: "checkmark").foregroundStyle(.tint)
                                }
                            }
                        }
                        .buttonStyle(.plain)
                    }
                }
            }
            .navigationTitle("选择格式")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("关闭") { showFormatPicker = false }
                }
            }
            .onAppear { HangLog.mark("格式列表已显示（\(capture.formats.count) 项）") }
        }
    }

    /// 辅助项（原来是 Menu，换成 sheet + Toggle）
    private var assistSheet: some View {
        NavigationStack {
            List {
                if settings.showsPeakHoldOption {
                    Toggle("峰值保持游标", isOn: $settings.peakHoldEnabled)
                }
                if settings.showsZebraOption {
                    Toggle("超白斑马纹（>\(Int(settings.zebraThresholdIRE)) IRE）", isOn: $settings.zebraEnabled)
                    Toggle("黑切割斑马纹（<0 IRE）", isOn: $settings.zebraBlackEnabled)
                }
                Toggle("超标报警红框", isOn: $settings.warningAlarmEnabled)
                Section {
                    Button("清报警与峰值游标") { onClearAlarms() }
                }
            }
            .navigationTitle("辅助")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .confirmationAction) {
                    Button("完成") { showAssistSheet = false }
                }
            }
        }
    }

    /// 设备选择列表（替代 Menu）
    private var devicePickerSheet: some View {
        NavigationStack {
            List {
                Section("输入源") {
                    if capture.devices.isEmpty {
                        Text("未检测到设备").foregroundStyle(.secondary)
                    }
                    ForEach(capture.devices) { device in
                        Button {
                            HangLog.mark("选中设备：\(device.name)")
                            capture.select(deviceID: device.id)
                            showDevicePicker = false
                        } label: {
                            HStack {
                                VStack(alignment: .leading, spacing: 2) {
                                    Text(device.name)
                                    Text(device.badge).font(.caption).foregroundStyle(.secondary)
                                }
                                Spacer()
                                if capture.selectedDeviceID == device.id {
                                    Image(systemName: "checkmark").foregroundStyle(.tint)
                                }
                            }
                        }
                        .buttonStyle(.plain)
                    }
                }
                if !capture.hasExternalDevice && UIDevice.current.userInterfaceIdiom == .phone {
                    Section {
                        Text("iPhone 不支持外接 UVC 采集卡（AVFoundation 的 .external 仅 iPadOS 提供）。请使用 iPad 或 Windows 版。")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                }
                Section {
                    Button {
                        HangLog.mark("刷新设备列表")
                        capture.refresh()
                    } label: {
                        Label("刷新设备列表", systemImage: "arrow.clockwise")
                    }
                }
            }
            .navigationTitle("选择输入源")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("关闭") { showDevicePicker = false }
                }
            }
            .onAppear { HangLog.mark("设备列表已显示（\(capture.devices.count) 个）") }
        }
    }

    private var assistMenu: some View {
        Button {
            HangLog.mark("点开辅助")
            showAssistSheet = true
        } label: {
            ChipLabel(title: "辅助", systemImage: "sparkles", isActive: true)
        }
        .buttonStyle(.plain)
        .sheet(isPresented: $showAssistSheet) { assistSheet }
    }

    /// 全屏 / 四分割 时的内容选择；旧预设时显示示波器开关
    @ViewBuilder
    private var contentPickers: some View {
        switch settings.monitorLayout {
        case .fullscreen:
            PaneContentPicker(title: "全屏", selection: $settings.fullscreenContent)

        case .quad:
            ForEach(0..<4, id: \.self) { index in
                PaneContentPicker(title: quadSlotTitle(index), selection: quadBinding(index))
            }

        default:
            Group {
                scopeToggle(.vectorscope, isOn: $settings.showVectorscope)
                scopeToggle(.waveform, isOn: $settings.showWaveform)
                scopeToggle(.parade, isOn: $settings.showParade)
            }
        }
    }

    private func quadSlotTitle(_ index: Int) -> String {
        switch index {
        case 0: return "左上"
        case 1: return "右上"
        case 2: return "左下"
        default: return "右下"
        }
    }

    private func quadBinding(_ index: Int) -> Binding<PaneContent> {
        Binding(
            get: {
                let list = settings.normalizedQuadContents
                return index < list.count ? list[index] : .picture
            },
            set: { newValue in
                var list = settings.normalizedQuadContents
                if index < list.count {
                    list[index] = newValue
                    settings.quadContents = list
                }
            }
        )
    }

    private var gradeRow: some View {
        HStack(alignment: .bottom, spacing: 12) {
            LabeledSlider(title: "曝光 (档)", value: $settings.exposure, range: -4...4, format: "%.2f")
            LabeledSlider(title: "对比度", value: $settings.contrast, range: 0.2...2.5, format: "%.2f")
            LabeledSlider(title: "饱和度", value: $settings.saturation, range: 0...2.5, format: "%.2f")
            LabeledSlider(title: "伽马", value: $settings.gamma, range: 0.4...2.5, format: "%.2f")
            if showsLUTUI {
            LabeledSlider(title: "LUT 强度", value: $settings.lutIntensity, range: 0...1, format: "%.2f")
            }

            Button {
                settings.resetGrade()
            } label: {
                ChipLabel(title: "复位", systemImage: "arrow.uturn.backward")
            }
            .buttonStyle(.plain)
            .padding(.bottom, 2)
        }
    }

    // MARK: - 菜单

    private var deviceMenu: some View {
        // 不用 Menu：实测第二次点击时 SwiftUI 的菜单不再弹出（界面像卡死，日志里却没有
        // MAIN BLOCKED —— 说明主线程没阻塞，是菜单呈现本身的问题）。改成自绘 sheet。
        Button {
            HangLog.mark("点开设备选择")
            showDevicePicker = true
        } label: {
            ChipLabel(title: currentDeviceName,
                      systemImage: "video",
                      isActive: capture.hasExternalDevice,
                      tint: .green)
        }
        .buttonStyle(.plain)
        .sheet(isPresented: $showDevicePicker) {
            devicePickerSheet
        }
    }

    private var formatMenu: some View {
        Button {
            HangLog.mark("点开格式选择")
            showFormatPicker = true
        } label: {
            ChipLabel(title: currentFormatName, systemImage: "4k.tv")
        }
        .buttonStyle(.plain)
        .sheet(isPresented: $showFormatPicker) {
            formatPickerSheet
        }
    }

    private var layoutMenu: some View {
        Button {
            HangLog.mark("点开布局")
            showLayoutDialog = true
        } label: {
            ChipLabel(title: settings.monitorLayout.title, systemImage: settings.monitorLayout.symbolName)
        }
        .buttonStyle(.plain)
        .sheet(isPresented: $showLayoutDialog) {
            ChipChoiceSheet(title: "布局", sections: [
                .init(title: "布局", rows: MonitorLayoutPreset.allCases.map { preset in
                    .init(title: preset.title,
                          isSelected: settings.monitorLayout == preset,
                          action: { settings.monitorLayout = preset })
                }),
                .init(title: "缩放", rows: AspectMode.allCases.map { mode in
                    .init(title: mode.title,
                          isSelected: settings.aspectMode == mode,
                          action: { settings.aspectMode = mode })
                }),
            ])
        }
    }

    private var displayModeMenu: some View {
        Button {
            HangLog.mark("点开显示通道")
            showDisplayDialog = true
        } label: {
            ChipLabel(title: settings.displayMode.title, systemImage: "circle.lefthalf.filled")
        }
        .buttonStyle(.plain)
        .sheet(isPresented: $showDisplayDialog) {
            ChipChoiceSheet.single(title: "显示通道", rows: DisplayMode.allCases.map { mode in
                .init(title: mode.title,
                      isSelected: settings.displayMode == mode,
                      action: { settings.displayMode = mode })
            })
        }
    }

    private var waveformModeMenu: some View {
        Button {
            HangLog.mark("点开波形模式")
            showWaveformDialog = true
        } label: {
            ChipLabel(title: "波形：\(settings.waveformMode.title)", systemImage: "waveform.path.ecg")
        }
        .buttonStyle(.plain)
        .sheet(isPresented: $showWaveformDialog) {
            ChipChoiceSheet.single(title: "波形模式", rows: WaveformMode.allCases.map { mode in
                .init(title: mode.title,
                      isSelected: settings.waveformMode == mode,
                      action: { settings.waveformMode = mode })
            })
        }
    }

    private var lutMenu: some View {
        Button {
            HangLog.mark("点开 LUT")
            showLUTDialog = true
        } label: {
            ChipLabel(title: currentLUTName, systemImage: "camera.filters", isActive: lutStore.selectedID != nil)
        }
        .buttonStyle(.plain)
        .sheet(isPresented: $showLUTDialog) {
            ChipChoiceSheet(title: "LUT", sections: [
                .init(title: "已导入", rows: lutStore.items.map { item in
                    .init(title: item.name,
                          isSelected: lutStore.selectedID == item.id,
                          action: { lutStore.select(id: item.id) })
                }),
                .init(title: "操作", rows: [
                    .init(title: "导入 .cube 文件…", isSelected: false,
                          action: { showLUTImporter = true }),
                    .init(title: "不使用 LUT", isSelected: lutStore.selectedID == nil,
                          action: { lutStore.select(id: nil) }),
                ]),
            ])
        }
    }

    private func scopeToggle(_ kind: ScopePanelKind, isOn: Binding<Bool>) -> some View {
        Button {
            isOn.wrappedValue.toggle()
        } label: {
            ChipLabel(title: kind.shortTitle,
                      systemImage: kind == .vectorscope ? "circle.grid.cross" : "waveform",
                      isActive: isOn.wrappedValue)
        }
        .buttonStyle(.plain)
    }

    // MARK: - 文案

    private var currentDeviceName: String {
        capture.devices.first { $0.id == capture.selectedDeviceID }?.name ?? "选择采集设备"
    }

    private var currentFormatName: String {
        capture.formats.first { $0.id == capture.selectedFormatID }?.displayName ?? "选择输入格式"
    }

    private var currentLUTName: String {
        guard let id = lutStore.selectedID,
              let item = lutStore.items.first(where: { $0.id == id }) else {
            return "LUT：无"
        }
        return item.name
    }
}
