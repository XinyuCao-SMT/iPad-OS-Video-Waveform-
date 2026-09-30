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

                Button {
                    settings.scopeSource = settings.scopeSource == .preLUT ? .postLUT : .preLUT
                } label: {
                    ChipLabel(title: settings.scopeSource == .preLUT ? "取样 LUT 前" : "取样 LUT 后",
                              systemImage: "scope")
                }
                .buttonStyle(.plain)

                lutMenu

                // 尾部按钮用 Group 包一层，给 ViewBuilder 的 10 个子视图上限留余量
                Group {
                    Button {
                        settings.lutEnabled.toggle()
                    } label: {
                        ChipLabel(title: settings.lutEnabled ? "LUT 开" : "LUT 关",
                                  systemImage: "camera.filters",
                                  isActive: settings.lutEnabled,
                                  tint: .blue)
                    }
                    .buttonStyle(.plain)

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
    private var assistMenu: some View {
        Menu {
            if settings.showsPeakHoldOption {
                Button {
                    settings.peakHoldEnabled.toggle()
                } label: {
                    Label("峰值保持游标", systemImage: settings.peakHoldEnabled ? "checkmark" : "waveform.path")
                }
            }

            if settings.showsZebraOption {
                Button {
                    settings.zebraEnabled.toggle()
                } label: {
                    Label("超白斑马纹（>" + String(format: "%.0f", settings.zebraThresholdIRE) + " IRE）",
                          systemImage: settings.zebraEnabled ? "checkmark" : "square.dashed")
                }

                Button {
                    settings.zebraBlackEnabled.toggle()
                } label: {
                    Label("黑切割斑马纹（<0 IRE）",
                          systemImage: settings.zebraBlackEnabled ? "checkmark" : "square.dashed")
                }
            }

            Button {
                settings.warningAlarmEnabled.toggle()
            } label: {
                Label("超标报警红框",
                      systemImage: settings.warningAlarmEnabled ? "checkmark" : "exclamationmark.triangle")
            }

            Divider()

            Button {
                onClearAlarms()
            } label: {
                Label("清报警与峰值游标", systemImage: "arrow.counterclockwise")
            }
        } label: {
            ChipLabel(title: "辅助",
                      systemImage: "sparkles",
                      isActive: settings.peakHoldEnabled || settings.zebraEnabled
                          || settings.zebraBlackEnabled || settings.warningAlarmEnabled)
        }
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
            LabeledSlider(title: "LUT 强度", value: $settings.lutIntensity, range: 0...1, format: "%.2f")

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
        Menu {
            if capture.devices.isEmpty {
                Text("未检测到设备")
            }
            ForEach(capture.devices) { device in
                if capture.selectedDeviceID == device.id {
                    Button {
                        capture.select(deviceID: device.id)
                    } label: {
                        Label("\(device.name)（\(device.badge)）", systemImage: "checkmark")
                    }
                } else {
                    Button("\(device.name)（\(device.badge)）") {
                        capture.select(deviceID: device.id)
                    }
                }
            }
            Divider()
            Button {
                capture.refresh()
            } label: {
                Label("刷新设备列表", systemImage: "arrow.clockwise")
            }
        } label: {
            ChipLabel(title: currentDeviceName,
                      systemImage: "video",
                      isActive: capture.hasExternalDevice,
                      tint: .green)
        }
    }

    private var formatMenu: some View {
        Menu {
            if capture.formats.isEmpty {
                Text("该设备没有可用格式")
            }
            ForEach(capture.formats.prefix(40)) { format in
                if capture.selectedFormatID == format.id {
                    Button {
                        capture.select(formatID: format.id)
                    } label: {
                        Label(format.displayName, systemImage: "checkmark")
                    }
                } else {
                    Button(format.displayName) {
                        capture.select(formatID: format.id)
                    }
                }
            }
        } label: {
            ChipLabel(title: currentFormatName, systemImage: "4k.tv")
        }
    }

    private var layoutMenu: some View {
        Menu {
            ForEach(MonitorLayoutPreset.allCases) { preset in
                if settings.monitorLayout == preset {
                    Button {
                        settings.monitorLayout = preset
                    } label: {
                        Label(preset.title, systemImage: "checkmark")
                    }
                } else {
                    Button(preset.title) {
                        settings.monitorLayout = preset
                    }
                }
            }
            Divider()
            ForEach(AspectMode.allCases) { mode in
                Button {
                    settings.aspectMode = mode
                } label: {
                    Label(mode.title, systemImage: settings.aspectMode == mode ? "checkmark" : "aspectratio")
                }
            }
        } label: {
            ChipLabel(title: settings.monitorLayout.title, systemImage: settings.monitorLayout.symbolName)
        }
    }

    private var displayModeMenu: some View {
        Menu {
            ForEach(DisplayMode.allCases) { mode in
                if settings.displayMode == mode {
                    Button {
                        settings.displayMode = mode
                    } label: {
                        Label(mode.title, systemImage: "checkmark")
                    }
                } else {
                    Button(mode.title) {
                        settings.displayMode = mode
                    }
                }
            }
        } label: {
            ChipLabel(title: settings.displayMode.title, systemImage: "circle.lefthalf.filled")
        }
    }

    private var waveformModeMenu: some View {
        Menu {
            ForEach(WaveformMode.allCases) { mode in
                if settings.waveformMode == mode {
                    Button {
                        settings.waveformMode = mode
                    } label: {
                        Label(mode.title, systemImage: "checkmark")
                    }
                } else {
                    Button(mode.title) {
                        settings.waveformMode = mode
                    }
                }
            }
        } label: {
            ChipLabel(title: "波形：\(settings.waveformMode.title)", systemImage: "waveform.path.ecg")
        }
    }

    private var lutMenu: some View {
        Menu {
            if lutStore.items.isEmpty {
                Text("还没有 LUT，先导入 .cube 文件")
            }
            ForEach(lutStore.items) { item in
                if lutStore.selectedID == item.id {
                    Button {
                        lutStore.select(id: item.id)
                    } label: {
                        Label(item.name, systemImage: "checkmark")
                    }
                } else {
                    Button(item.name) {
                        lutStore.select(id: item.id)
                    }
                }
            }
            Divider()
            Button {
                showLUTImporter = true
            } label: {
                Label("导入 .cube 文件…", systemImage: "square.and.arrow.down")
            }
            if lutStore.selectedID != nil {
                Button(role: .destructive) {
                    lutStore.select(id: nil)
                } label: {
                    Label("不使用 LUT", systemImage: "xmark")
                }
            }
        } label: {
            ChipLabel(title: currentLUTName, systemImage: "camera.filters", isActive: lutStore.selectedID != nil)
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
