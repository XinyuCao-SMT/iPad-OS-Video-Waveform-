//
//  ScopeLayout.swift
//  VideoScopePad
//
//  唯一的一份布局计算：Metal 渲染（像素空间）与 SwiftUI 刻度叠加（点空间）共用。
//
//  统一模型：整个画面被切成若干「格子」(PaneLayout)，每个格子的内容可以是
//  实时画面，也可以是矢量 / 亮度波形 / RGB 波形三种示波器之一：
//    · 全屏    : 1 个格子铺满，内容可选（画面或任意示波器）
//    · 四分割  : 2×2 四个格子，每格内容各自可选
//    · 底部 / 右侧 / 叠加 : 画面 + 若干示波器（旧预设，保留）
//
//  每个示波器格子额外给出两个矩形：
//    plot   轨迹绘制区（Metal 把示波器图画在这里）
//    gutter 刻度栏（SwiftUI 在这里画大号数字刻度，避免压在轨迹上）
//  两者都由本文件算出，所以轨迹与刻度严格对齐。
//
//  所有输出矩形都在「归一化单位空间」：x/y ∈ 0...1，左上角为原点、y 轴向下。
//  计算只使用相对比例，只要两个容器宽高比一致，点空间与像素空间结果就一致。
//

import CoreGraphics

/// 一个格子的布局
struct PaneLayout: Equatable {
    var content: PaneContent
    /// 第几个格子（0 起，四分割时用于界面标识）
    var slot: Int
    /// 整个格子
    var panel: CGRect
    /// 画面显示区（content == .picture 时有效）
    var video: CGRect?
    /// 画面纹理采样区（fit / fill 裁切）
    var videoUV: CGRect?
    /// 示波器轨迹区（content != .picture 时有效）
    var plot: CGRect?
    /// 示波器刻度栏（波形/RGB 才有）
    var gutter: CGRect?
}

struct ScopeLayoutResult: Equatable {
    var panes: [PaneLayout] = []
    /// 示波器是否浮在画面上（半透明背景）
    var isOverlay: Bool = false

    var hasPicture: Bool {
        panes.contains { $0.content == .picture }
    }

    /// 当前布局里实际用到的示波器种类（用来决定要统计哪些示波器）
    var visibleScopeKinds: Set<ScopePanelKind> {
        var kinds: Set<ScopePanelKind> = []
        for pane in panes {
            if let kind = pane.content.scopeKind {
                kinds.insert(kind)
            }
        }
        return kinds
    }

    func pane(slot: Int) -> PaneLayout? {
        panes.first { $0.slot == slot }
    }
}

enum ScopeLayout {

    /// 面板内轨迹区的目标宽高比
    static func plotAspect(for content: PaneContent) -> CGFloat {
        switch content {
        case .vectorscope: return 1.0
        case .waveform: return 1.45
        case .parade: return 1.6
        case .picture: return 16.0 / 9.0
        }
    }

    /// 该内容是否需要左侧刻度栏
    static func needsGutter(_ content: PaneContent) -> Bool {
        switch content {
        case .waveform, .parade: return true
        case .vectorscope, .picture: return false
        }
    }

    // MARK: - 入口

    static func compute(containerSize: CGSize,
                        videoSize: CGSize,
                        preset: MonitorLayoutPreset,
                        aspectMode: AspectMode,
                        fullscreenContent: PaneContent,
                        quadContents: [PaneContent],
                        legacyPanels: [ScopePanelKind]) -> ScopeLayoutResult {

        var result = ScopeLayoutResult()

        guard containerSize.width > 1, containerSize.height > 1 else {
            return result
        }

        // 归一化工作盒：宽 = 容器宽高比，高 = 1
        let boxWidth = containerSize.width / containerSize.height
        let box = CGRect(x: 0, y: 0, width: boxWidth, height: 1)

        var effectivePreset = preset
        // 竖屏下右侧栏太窄，退化成底部条
        if effectivePreset == .rightColumn && boxWidth < 1.05 && !legacyPanels.isEmpty {
            effectivePreset = .bottomStrip
        }

        let videoAspect: CGFloat = (videoSize.width > 1 && videoSize.height > 1)
            ? videoSize.width / videoSize.height
            : 16.0 / 9.0

        func normalize(_ rect: CGRect) -> CGRect {
            CGRect(x: rect.minX / boxWidth,
                   y: rect.minY,
                   width: rect.width / boxWidth,
                   height: rect.height)
        }

        switch effectivePreset {
        case .fullscreen:
            result.panes = [makePane(content: fullscreenContent,
                                     slot: 0,
                                     panel: box,
                                     videoAspect: videoAspect,
                                     aspectMode: aspectMode)]
            result.panes = result.panes.map { normalized($0, normalize) }

        case .quad:
            let gap = box.width * 0.004
            let cellWidth = (box.width - gap * 3) / 2
            let cellHeight = (box.height - gap * 3) / 2

            var panes: [PaneLayout] = []
            for slot in 0..<4 {
                let row = slot / 2
                let column = slot % 2
                let panel = CGRect(x: box.minX + gap * CGFloat(column + 1) + cellWidth * CGFloat(column),
                                   y: box.minY + gap * CGFloat(row + 1) + cellHeight * CGFloat(row),
                                   width: cellWidth,
                                   height: cellHeight)
                let content = slot < quadContents.count ? quadContents[slot] : .picture
                panes.append(makePane(content: content,
                                      slot: slot,
                                      panel: panel,
                                      videoAspect: videoAspect,
                                      aspectMode: aspectMode))
            }
            result.panes = panes.map { normalized($0, normalize) }

        case .bottomStrip:
            let panels = legacyPanels
            let strip = stripPanels(in: box, panels: panels)
            let stripHeight = strip.map { $0.panel.height }.max() ?? 0

            let monitorBox = CGRect(x: box.minX, y: box.minY,
                                    width: box.width,
                                    height: max(box.height - stripHeight, box.height * 0.35))
            var panes: [PaneLayout] = [makePane(content: .picture,
                                                slot: 0,
                                                panel: monitorBox,
                                                videoAspect: videoAspect,
                                                aspectMode: aspectMode)]
            panes.append(contentsOf: strip)
            result.panes = panes.map { normalized($0, normalize) }

        case .rightColumn:
            let columnWidth = min(max(box.width * 0.32, 0.42), box.width * 0.5)
            let column = CGRect(x: box.maxX - columnWidth, y: box.minY,
                                width: columnWidth, height: box.height)

            let monitorBox = CGRect(x: box.minX, y: box.minY,
                                    width: max(box.width - columnWidth, box.width * 0.35),
                                    height: box.height)

            var panes: [PaneLayout] = [makePane(content: .picture,
                                                slot: 0,
                                                panel: monitorBox,
                                                videoAspect: videoAspect,
                                                aspectMode: aspectMode)]

            let count = max(legacyPanels.count, 1)
            let panelHeight = box.height / CGFloat(count)
            for (index, kind) in legacyPanels.enumerated() {
                let panel = CGRect(x: column.minX + columnWidth * 0.05,
                                   y: column.minY + CGFloat(index) * panelHeight + box.height * 0.01,
                                   width: columnWidth * 0.9,
                                   height: panelHeight - box.height * 0.02)
                panes.append(makePane(content: content(for: kind),
                                      slot: index + 1,
                                      panel: panel,
                                      videoAspect: videoAspect,
                                      aspectMode: aspectMode))
            }
            result.panes = panes.map { normalized($0, normalize) }

        case .overlay:
            result.isOverlay = true
            var panes: [PaneLayout] = [makePane(content: .picture,
                                                slot: 0,
                                                panel: box,
                                                videoAspect: videoAspect,
                                                aspectMode: aspectMode)]
            panes.append(contentsOf: stripPanels(in: box, panels: legacyPanels))
            result.panes = panes.map { normalized($0, normalize) }
        }

        return result
    }

    // MARK: - 生成单个格子

    private static func makePane(content: PaneContent,
                                 slot: Int,
                                 panel: CGRect,
                                 videoAspect: CGFloat,
                                 aspectMode: AspectMode) -> PaneLayout {

        if content == .picture {
            let video = fittedRect(aspect: videoAspect, in: panel, mode: aspectMode)
            return PaneLayout(content: .picture,
                              slot: slot,
                              panel: panel,
                              video: video,
                              videoUV: uvRect(videoAspect: videoAspect, target: video, mode: aspectMode))
        }

        let pad = panel.width * 0.02
        var plotArea = CGRect(x: panel.minX + pad, y: panel.minY + pad,
                              width: max(panel.width - pad * 2, 1),
                              height: max(panel.height - pad * 2, 1))
        var gutter: CGRect?

        if needsGutter(content) {
            let gutterWidth = min(max(panel.width * 0.17, 0.022), panel.width * 0.3)
            gutter = CGRect(x: panel.minX, y: panel.minY, width: gutterWidth, height: panel.height)
            plotArea = CGRect(x: panel.minX + gutterWidth + pad * 0.4,
                              y: panel.minY + pad,
                              width: max(panel.width - gutterWidth - pad * 1.4, 1),
                              height: max(panel.height - pad * 2, 1))
        }

        let aspect = plotAspect(for: content)
        var width = plotArea.width
        var height = plotArea.height
        if width / height > aspect {
            width = height * aspect
        } else {
            height = width / aspect
        }

        let plot = CGRect(x: plotArea.midX - width / 2,
                          y: plotArea.midY - height / 2,
                          width: width,
                          height: height)

        return PaneLayout(content: content,
                          slot: slot,
                          panel: panel,
                          video: nil,
                          videoUV: nil,
                          plot: plot,
                          gutter: gutter)
    }

    /// 底部条：n 个示波器等宽并排
    private static func stripPanels(in box: CGRect, panels: [ScopePanelKind]) -> [PaneLayout] {
        guard !panels.isEmpty else { return [] }

        let count = CGFloat(panels.count)
        let gap = box.width * 0.006
        let panelWidth = (box.width - gap * (count + 1)) / count

        // 让最「方」的那个面板决定条带高度（刻度栏要占位，所以多留一点）
        let required = panels.map { panelWidth / plotAspect(for: content(for: $0)) + box.height * 0.06 }
            .max() ?? box.height * 0.3
        let stripHeight = min(max(required, box.height * 0.24), box.height * 0.54)

        var result: [PaneLayout] = []
        for (index, kind) in panels.enumerated() {
            let panel = CGRect(x: box.minX + gap * CGFloat(index + 1) + panelWidth * CGFloat(index),
                               y: box.maxY - stripHeight,
                               width: panelWidth,
                               height: stripHeight)
            result.append(makePane(content: content(for: kind),
                                   slot: index + 1,
                                   panel: panel,
                                   videoAspect: 16.0 / 9.0,
                                   aspectMode: .fit))
        }
        return result
    }

    private static func content(for kind: ScopePanelKind) -> PaneContent {
        switch kind {
        case .vectorscope: return .vectorscope
        case .waveform: return .waveform
        case .parade: return .parade
        }
    }

    private static func normalized(_ pane: PaneLayout, _ normalize: (CGRect) -> CGRect) -> PaneLayout {
        var copy = pane
        copy.panel = normalize(pane.panel)
        if let video = pane.video { copy.video = normalize(video) }
        if let plot = pane.plot { copy.plot = normalize(plot) }
        if let gutter = pane.gutter { copy.gutter = normalize(gutter) }
        return copy
    }

    // MARK: - 辅助

    /// fit：完整显示；fill：铺满（返回可溢出的矩形，由 UV 裁切控制）
    private static func fittedRect(aspect: CGFloat, in rect: CGRect, mode: AspectMode) -> CGRect {
        switch mode {
        case .fit:
            let targetWidth = rect.height * aspect
            if targetWidth <= rect.width {
                return CGRect(x: rect.midX - targetWidth / 2, y: rect.minY,
                              width: targetWidth, height: rect.height)
            } else {
                let height = rect.width / aspect
                return CGRect(x: rect.minX, y: rect.midY - height / 2,
                              width: rect.width, height: height)
            }
        case .fill:
            return rect
        }
    }

    /// 计算纹理 UV 采样区域
    private static func uvRect(videoAspect: CGFloat, target: CGRect, mode: AspectMode) -> CGRect {
        switch mode {
        case .fit:
            return CGRect(x: 0, y: 0, width: 1, height: 1)
        case .fill:
            let containerAspect = target.width / max(target.height, 1)
            if videoAspect > containerAspect {
                let width = containerAspect / videoAspect
                return CGRect(x: (1 - width) / 2, y: 0, width: width, height: 1)
            } else {
                let height = videoAspect / max(containerAspect, 0.0001)
                return CGRect(x: 0, y: (1 - height) / 2, width: 1, height: height)
            }
        }
    }
}
