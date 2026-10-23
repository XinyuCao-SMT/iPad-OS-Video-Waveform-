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

// 这几处原来都是 Menu。SwiftUI 的 Menu 在本 App 里会「点不开 / 第二次不弹」，
// 表现就是点格子菜单界面卡死，所以统一改成 ChipChoiceSheet 弹窗。

struct PaneContentPicker: View {
    let title: String
    @Binding var selection: PaneContent
    @State private var showPicker = false

    var body: some View {
        Button {
            HangLog.mark("点开内容选择：\(title)")
            showPicker = true
        } label: {
            ChipLabel(title: title + "：" + selection.shortTitle,
                      systemImage: selection.symbolName)
        }
        .buttonStyle(.plain)
        .sheet(isPresented: $showPicker) {
            ChipChoiceSheet.single(title: title, rows: PaneContent.allCases.map { content in
                .init(title: content.title,
                      isSelected: selection == content,
                      action: { selection = content })
            })
        }
    }
}

struct ScaleUnitPicker: View {
    @Binding var unit: ScaleUnit
    @State private var showPicker = false

    var body: some View {
        Button {
            HangLog.mark("点开刻度单位")
            showPicker = true
        } label: {
            ChipLabel(title: "刻度：" + unit.shortTitle, systemImage: "ruler")
        }
        .buttonStyle(.plain)
        .sheet(isPresented: $showPicker) {
            ChipChoiceSheet.single(title: "刻度单位",
                                   rows: ScaleUnit.allCases.map { candidate in
                .init(title: candidate.title,
                      isSelected: unit == candidate,
                      action: { unit = candidate })
            },
                                   footer: "mV 为按 100 IRE = 700 mV 换算的等效电平")
        }
    }
}

// MARK: - 格子内容选择（全屏 / 四分割）

/// 在监视区上方铺一层「每格右上角一个内容按钮」的交互层。
/// 只有全屏和四分割模式会显示；旧预设（底部/右侧/叠加）不显示，避免和示波器开关打架。
/// 亮度格额外支持整块拖动 —— 拖动即改手动参考线的电平（调光基准）。
struct PaneChromeOverlay: View {

    let layout: ScopeLayoutResult
    @ObservedObject var settings: AppSettings
    let containerSize: CGSize
    /// 视频范围（参考线的 IRE 换算要用，与刻度层同一个来源）
    let videoRange: Bool

    /// 正在选内容的格子（nil = 没弹出）
    @State private var pickerPane: PaneLayout?

    private var showsChrome: Bool {
        settings.monitorLayout == .fullscreen || settings.monitorLayout == .quad
    }

    var body: some View {
        ZStack {
            if showsChrome {
                ForEach(layout.panes, id: \.slot) { pane in
                    let rect = pane.panel.scaled(to: containerSize)
                    // 内边距按格子尺寸缩放（iPad mini 到 iPad Pro 都留出同样的观感），
                    // 并且整个按钮被限制在格子矩形内 —— 固定像素偏移会让它在小格子上跑到格子外。
                    let inset = min(max(rect.width * 0.02, 3), 12)

                    // 亮度格：整块绘图区可拖动 → 改参考线
                    if pane.content == .waveform, let plot = pane.plot {
                        let plotRect = plot.scaled(to: containerSize)
                        Color.clear
                            .contentShape(Rectangle())
                            .frame(width: plotRect.width, height: plotRect.height)
                            .position(x: plotRect.midX, y: plotRect.midY)
                            .gesture(guideDrag(in: plotRect))
                    }

                    PaneContentButton(pane: pane) {
                        HangLog.mark("点开格子内容菜单：格 \(pane.slot)")
                        pickerPane = pane
                    }
                    .frame(maxWidth: max(rect.width - inset * 2, 44), alignment: .trailing)
                    .frame(width: rect.width, height: rect.height, alignment: .topTrailing)
                    .padding(inset)
                    .clipped()
                    .position(x: rect.midX, y: rect.midY)
                }
            }
        }
        .frame(width: containerSize.width, height: containerSize.height, alignment: .topLeading)
        .sheet(isPresented: Binding(get: { pickerPane != nil },
                                    set: { if !$0 { pickerPane = nil } })) {
            if let pane = pickerPane {
                ChipChoiceSheet(title: "格 \(pane.slot + 1) 显示内容",
                                sections: [
                    .init(title: "内容", rows: PaneContent.allCases.map { content in
                        .init(title: content.title,
                              isSelected: isCurrent(pane, content),
                              action: { apply(pane, content) })
                    }),
                    .init(title: "布局", rows: [
                        .init(title: "切到" + settings.monitorLayout.toggled.shortTitle,
                              isSelected: false,
                              action: { settings.monitorLayout = settings.monitorLayout.toggled }),
                    ]),
                ])
            }
        }
    }

    /// 拖动亮度格的参考线：屏幕 y → 码值 → IRE（与刻度层 yPosition 互逆）
    private func guideDrag(in rect: CGRect) -> some Gesture {
        DragGesture(minimumDistance: 0)
            .onChanged { value in
                guard rect.height > 1 else { return }
                let code = Double((rect.maxY - value.location.y) / rect.height) * 255
                let ire = videoRange ? (code - 16) / 219 * 100 : code / 255 * 100
                settings.waveformGuideIRE = min(max(ire, 0), 110)
                if !settings.waveformGuideEnabled { settings.waveformGuideEnabled = true }
            }
    }

    private func isCurrent(_ pane: PaneLayout, _ content: PaneContent) -> Bool {
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

    private func apply(_ pane: PaneLayout, _ content: PaneContent) {
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

/// 每格右上角那个小按钮（只负责外观与点击，弹窗在 PaneChromeOverlay 里统一处理）
struct PaneContentButton: View {

    let pane: PaneLayout
    let onTap: () -> Void

    var body: some View {
        Button(action: onTap) {
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
        .buttonStyle(.plain)
    }
}
