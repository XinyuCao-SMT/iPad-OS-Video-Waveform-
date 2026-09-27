//
//  Controls.swift
//  VideoScopePad
//
//  控制栏里复用的小控件。
//

import SwiftUI

struct ChipLabel: View {
    let title: String
    var systemImage: String?
    var isActive: Bool = true
    var tint: Color?

    var body: some View {
        HStack(spacing: 4) {
            if let systemImage {
                Image(systemName: systemImage)
                    .font(.system(size: 11, weight: .semibold))
            }
            Text(title)
                .font(.system(size: 12, weight: .medium))
                .lineLimit(1)
        }
        .padding(.horizontal, 9)
        .padding(.vertical, 6)
        .background(background)
        .foregroundStyle(foreground)
        .clipShape(RoundedRectangle(cornerRadius: 7, style: .continuous))
    }

    private var background: Color {
        if let tint, isActive { return tint.opacity(0.30) }
        return isActive ? Color.white.opacity(0.14) : Color.white.opacity(0.06)
    }

    private var foreground: Color {
        isActive ? Color.white : Color.white.opacity(0.5)
    }
}

struct LabeledSlider: View {
    let title: String
    @Binding var value: Double
    let range: ClosedRange<Double>
    let format: String
    var onReset: (() -> Void)?

    var body: some View {
        VStack(alignment: .leading, spacing: 1) {
            HStack(spacing: 4) {
                Text(title)
                    .font(.system(size: 11))
                    .foregroundStyle(.white.opacity(0.7))
                Spacer(minLength: 4)
                Text(String(format: format, value))
                    .font(.system(size: 11, design: .monospaced))
                    .foregroundStyle(.white.opacity(0.9))
            }
            Slider(value: $value, in: range)
                .controlSize(.mini)
                .tint(.white.opacity(0.8))
        }
    }
}

struct MenuSectionTitle: View {
    let text: String
    var body: some View {
        Text(text)
            .font(.system(size: 11, weight: .semibold))
            .foregroundStyle(.white.opacity(0.45))
    }
}

// MARK: - 报警红框

/// 超标报警的红色外框：只在有「已确认」的报警时出现。
/// 单独观察 hub，避免让整个监视界面跟着 10Hz 重建。
struct AlarmBorderOverlay: View {
    @ObservedObject var measurement: MeasurementHub

    private var isAlarming: Bool { !measurement.activeWarnings.isEmpty }

    var body: some View {
        RoundedRectangle(cornerRadius: 6, style: .continuous)
            .strokeBorder(Color.red.opacity(isAlarming ? 0.9 : 0), lineWidth: 4)
            .animation(.easeInOut(duration: 0.15), value: isAlarming)
            .allowsHitTesting(false)
            .padding(2)
    }
}

// MARK: - 内容 / 单位选择器

struct PaneContentPicker: View {
    let title: String
    @Binding var selection: PaneContent

    var body: some View {
        Menu {
            ForEach(PaneContent.allCases) { content in
                if selection == content {
                    Button {
                        selection = content
                    } label: {
                        Label(content.title, systemImage: "checkmark")
                    }
                } else {
                    Button {
                        selection = content
                    } label: {
                        Label(content.title, systemImage: content.symbolName)
                    }
                }
            }
        } label: {
            ChipLabel(title: title + "：" + selection.shortTitle,
                      systemImage: selection.symbolName)
        }
    }
}

struct ScaleUnitPicker: View {
    @Binding var unit: ScaleUnit

    var body: some View {
        Menu {
            ForEach(ScaleUnit.allCases) { candidate in
                if unit == candidate {
                    Button {
                        unit = candidate
                    } label: {
                        Label(candidate.title, systemImage: "checkmark")
                    }
                } else {
                    Button(candidate.title) {
                        unit = candidate
                    }
                }
            }
            Divider()
            Text("mV 为按 100 IRE = 700 mV 换算的等效电平")
        } label: {
            ChipLabel(title: "刻度：" + unit.shortTitle, systemImage: "ruler")
        }
    }
}

// MARK: - 格子内容选择（全屏 / 四分割）

/// 在监视区上方铺一层「每格右上角一个内容菜单」的交互层。
/// 只有全屏和四分割模式会显示；旧预设（底部/右侧/叠加）不显示，避免和示波器开关打架。
struct PaneChromeOverlay: View {

    let layout: ScopeLayoutResult
    @ObservedObject var settings: AppSettings
    let containerSize: CGSize

    private var showsChrome: Bool {
        settings.monitorLayout == .fullscreen || settings.monitorLayout == .quad
    }

    var body: some View {
        ZStack {
            if showsChrome {
                ForEach(layout.panes, id: \.slot) { pane in
                    let rect = pane.panel.scaled(to: containerSize)
                    PaneContentMenu(pane: pane, settings: settings)
                        .position(x: rect.maxX - 44, y: rect.minY + 16)
                }
            }
        }
        .frame(width: containerSize.width, height: containerSize.height, alignment: .topLeading)
    }
}

struct PaneContentMenu: View {

    let pane: PaneLayout
    @ObservedObject var settings: AppSettings

    var body: some View {
        Menu {
            ForEach(PaneContent.allCases) { content in
                if isCurrent(content) {
                    Button {
                        apply(content)
                    } label: {
                        Label(content.title, systemImage: "checkmark")
                    }
                } else {
                    Button {
                        apply(content)
                    } label: {
                        Label(content.title, systemImage: content.symbolName)
                    }
                }
            }

            Divider()

            Button {
                settings.monitorLayout = settings.monitorLayout.toggled
            } label: {
                Label("切到" + settings.monitorLayout.toggled.shortTitle,
                      systemImage: settings.monitorLayout.toggled.symbolName)
            }
        } label: {
            HStack(spacing: 4) {
                Image(systemName: pane.content.symbolName)
                    .font(.system(size: 10, weight: .semibold))
                Text(pane.content.shortTitle)
                    .font(.system(size: 11, weight: .medium))
                Image(systemName: "chevron.down")
                    .font(.system(size: 8, weight: .bold))
            }
            .padding(.horizontal, 7)
            .padding(.vertical, 5)
            .background(Color.black.opacity(0.55))
            .foregroundStyle(Color.white.opacity(0.92))
            .clipShape(RoundedRectangle(cornerRadius: 6, style: .continuous))
        }
    }

    private func isCurrent(_ content: PaneContent) -> Bool {
        switch settings.monitorLayout {
        case .fullscreen:
            return settings.fullscreenContent == content
        case .quad:
            let list = settings.normalizedQuadContents
            return pane.slot < list.count && list[pane.slot] == content
        default:
            return false
        }
    }

    private func apply(_ content: PaneContent) {
        switch settings.monitorLayout {
        case .fullscreen:
            settings.fullscreenContent = content
        case .quad:
            var list = settings.normalizedQuadContents
            if pane.slot < list.count {
                list[pane.slot] = content
                settings.quadContents = list
            }
        default:
            break
        }
    }
}
