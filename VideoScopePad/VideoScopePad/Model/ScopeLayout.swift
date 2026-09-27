//
//  ScopeLayout.swift
//  VideoScopePad
//
//  唯一的一份布局计算：Metal 渲染（像素空间）与 SwiftUI 刻度叠加（点空间）共用，
//  保证波形轨迹与刻度线严格对齐。
//
//  所有输出矩形都在「归一化单位空间」中：x/y ∈ 0...1，左上角为原点、y 轴向下。
//  由于只使用相对比例计算，只要两个容器的宽高比一致，结果就完全一致。
//

import CoreGraphics

struct ScopeLayoutResult {
    /// 画面显示区域（单位空间）
    var monitorRect: CGRect = CGRect(x: 0, y: 0, width: 1, height: 1)
    /// 画面纹理采样区域（用于 fit / fill 裁切）
    var monitorUV: CGRect = CGRect(x: 0, y: 0, width: 1, height: 1)
    /// 每个已启用示波器面板的「绘图区」矩形（单位空间）
    var plots: [ScopePanelKind: CGRect] = [:]
    /// 面板是否浮在画面上（半透明背景）
    var isOverlay: Bool = false
}

enum ScopeLayout {

    /// 面板内绘图区的目标宽高比
    static func plotAspect(for kind: ScopePanelKind) -> CGFloat {
        switch kind {
        case .vectorscope: return 1.0
        case .waveform: return 1.45
        case .parade: return 1.6
        }
    }

    static func compute(containerSize: CGSize,
                        videoSize: CGSize,
                        preset: MonitorLayoutPreset,
                        aspectMode: AspectMode,
                        panels: [ScopePanelKind]) -> ScopeLayoutResult {

        var result = ScopeLayoutResult()

        guard containerSize.width > 1, containerSize.height > 1 else {
            return result
        }

        // 归一化工作盒：宽 = 容器宽高比，高 = 1
        let boxWidth = containerSize.width / containerSize.height
        let box = CGRect(x: 0, y: 0, width: boxWidth, height: 1)

        // 没有启用示波器，或用户选择仅画面
        var effectivePreset = preset
        if panels.isEmpty {
            effectivePreset = .monitorOnly
        }
        // 竖屏下右侧栏会太窄，自动退化为底部条
        if effectivePreset == .rightColumn && boxWidth < 1.05 && !panels.isEmpty {
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
        case .monitorOnly:
            let monitorBox = fittedRect(aspect: videoAspect, in: box, mode: aspectMode)
            result.monitorRect = normalize(monitorBox)
            result.monitorUV = uvRect(videoAspect: videoAspect, target: monitorBox, mode: aspectMode)

        case .overlay:
            result.isOverlay = true
            let monitorBox = fittedRect(aspect: videoAspect, in: box, mode: aspectMode)
            result.monitorRect = normalize(monitorBox)
            result.monitorUV = uvRect(videoAspect: videoAspect, target: monitorBox, mode: aspectMode)
            // 叠加模式：面板浮在画面底部
            let strip = stripRects(in: box, panels: panels)
            result.plots = strip.mapValues(normalize)

        case .bottomStrip:
            let strip = stripRects(in: box, panels: panels)
            result.plots = strip.mapValues(normalize)
            let usedHeight = strip.values.map { $0.height }.max() ?? 0
            let monitorBox = CGRect(x: box.minX, y: box.minY,
                                    width: box.width,
                                    height: max(box.height - usedHeight, box.height * 0.35))
            let fitted = fittedRect(aspect: videoAspect, in: monitorBox, mode: aspectMode)
            result.monitorRect = normalize(fitted)
            result.monitorUV = uvRect(videoAspect: videoAspect, target: fitted, mode: aspectMode)

        case .rightColumn:
            let columnWidth = min(max(box.width * 0.32, 0.42), box.width * 0.5)
            let column = CGRect(x: box.maxX - columnWidth, y: box.minY, width: columnWidth, height: box.height)
            var plots: [ScopePanelKind: CGRect] = [:]
            let panelHeight = box.height / CGFloat(max(panels.count, 1))
            for (index, kind) in panels.enumerated() {
                let panel = CGRect(x: column.minX + columnWidth * 0.05,
                                   y: column.minY + CGFloat(index) * panelHeight + box.height * 0.01,
                                   width: columnWidth * 0.9,
                                   height: panelHeight - box.height * 0.02)
                plots[kind] = normalize(centeredPlot(in: panel, kind: kind, boxWidth: boxWidth))
            }
            let monitorBox = CGRect(x: box.minX, y: box.minY,
                                    width: max(box.width - columnWidth, box.width * 0.35),
                                    height: box.height)
            let fitted = fittedRect(aspect: videoAspect, in: monitorBox, mode: aspectMode)
            result.plots = plots
            result.monitorRect = normalize(fitted)
            result.monitorUV = uvRect(videoAspect: videoAspect, target: fitted, mode: aspectMode)
        }

        return result
    }

    // MARK: - 辅助

    /// 底部条：n 个面板等宽并排
    private static func stripRects(in box: CGRect, panels: [ScopePanelKind]) -> [ScopePanelKind: CGRect] {
        let count = CGFloat(max(panels.count, 1))
        let gap = box.width * 0.006
        let panelWidth = (box.width - gap * (count + 1)) / count

        // 让最「方」的那个面板决定条带高度
        let required = panels.map { panelWidth / plotAspect(for: $0) + box.height * 0.06 }.max() ?? box.height * 0.3
        let stripHeight = min(max(required, box.height * 0.22), box.height * 0.5)

        var rects: [ScopePanelKind: CGRect] = [:]
        for (index, kind) in panels.enumerated() {
            let panel = CGRect(x: box.minX + gap * CGFloat(index + 1) + panelWidth * CGFloat(index),
                               y: box.maxY - stripHeight,
                               width: panelWidth,
                               height: stripHeight)
            rects[kind] = centeredPlot(in: panel, kind: kind, boxWidth: box.width)
        }
        return rects
    }

    /// 在面板中以目标宽高比居中放置绘图区
    private static func centeredPlot(in panel: CGRect, kind: ScopePanelKind, boxWidth: CGFloat) -> CGRect {
        let aspect = plotAspect(for: kind)
        let padX = boxWidth * 0.008
        let padY = boxWidth * 0.008

        var width = max(panel.width - padX * 2, 1)
        var height = max(panel.height - padY * 2, 1)

        if width / height > aspect {
            width = height * aspect
        } else {
            height = width / aspect
        }

        return CGRect(x: panel.midX - width / 2,
                      y: panel.midY - height / 2,
                      width: width,
                      height: height)
    }

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
                // 画面更宽：左右裁切
                let width = containerAspect / videoAspect
                return CGRect(x: (1 - width) / 2, y: 0, width: width, height: 1)
            } else {
                let height = videoAspect / max(containerAspect, 0.0001)
                return CGRect(x: 0, y: (1 - height) / 2, width: 1, height: height)
            }
        }
    }
}
