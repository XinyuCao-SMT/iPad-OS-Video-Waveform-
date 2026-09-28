//
//  LayoutDebugOverlay.swift
//  VideoScopePad
//
//  调试用：把布局算出来的每个矩形直接画在画面上，并标出格子编号、内容与实时尺寸。
//
//  为什么需要它：
//    「内容不居中 / 显示不全 / 跑到别的格子」这类问题，只靠描述很难定位 —— 是布局算错了、
//    还是某处绘制没裁剪、还是画面本身的缩放模式（完整显示 / 铺满裁切）造成的？
//    打开这个叠加层截图，就能一眼看出每个格子实际拿到了多大的框。
//
//  颜色约定：
//    绿色 = 整个格子 panel
//    青色 = 示波器绘图区 plot
//    黄色 = 刻度栏 gutter
//    品红 = 画面显示区 video
//    灰色 = 画面纹理采样区（fill 模式下会被裁切，所以可能只有中间一部分）
//
//  在「设置 → 显示 → 布局调试叠加层」里开关（默认关闭）。
//

import SwiftUI

struct LayoutDebugOverlay: View {

    let layout: ScopeLayoutResult
    let containerSize: CGSize

    var body: some View {
        Canvas { context, _ in
            for pane in layout.panes {
                let panel = pane.panel.scaled(to: containerSize)

                drawBox(&context, rect: panel, color: .green, width: 1.5, label: panelLabel(pane, panel))

                if let plot = pane.plot?.scaled(to: containerSize) {
                    drawBox(&context, rect: plot, color: .cyan, width: 1.2,
                            label: "plot \(sizeText(plot))")
                }
                if let gutter = pane.gutter?.scaled(to: containerSize) {
                    drawBox(&context, rect: gutter, color: .yellow, width: 1.2, label: nil)
                }
                if let video = pane.video?.scaled(to: containerSize) {
                    drawBox(&context, rect: video, color: .pink, width: 1.2, label: nil)
                }
            }
        }
        .allowsHitTesting(false)
    }

    private func panelLabel(_ pane: PaneLayout, _ rect: CGRect) -> String {
        let name: String
        switch pane.content {
        case .picture:      name = "画面"
        case .vectorscope:  name = "矢量"
        case .waveform:     name = "波形"
        case .parade:       name = "Parade"
        }
        return "格 \(pane.slot) \(name)  \(sizeText(rect))"
    }

    private func sizeText(_ rect: CGRect) -> String {
        String(format: "%.0f×%.0f @%.0f,%.0f",
               rect.width, rect.height, rect.minX, rect.minY)
    }

    private func drawBox(_ ctx: inout GraphicsContext,
                         rect: CGRect,
                         color: Color,
                         width: CGFloat,
                         label: String?) {
        ctx.stroke(Path(rect), with: .color(color.opacity(0.9)), lineWidth: width)

        guard let label else { return }
        let text = Text(label)
            .font(.system(size: 10, weight: .semibold, design: .monospaced))
            .foregroundStyle(.black)

        // 标签贴在格子左上角内侧，垫一块同色底，避免和画面糊在一起
        let badge = CGRect(x: rect.minX + 3, y: rect.minY + 3, width: 8 * CGFloat(label.count) * 0.62 + 8, height: 14)
        ctx.fill(Path(roundedRect: badge, cornerRadius: 3), with: .color(color.opacity(0.85)))
        ctx.draw(text, at: CGPoint(x: badge.midX, y: badge.midY), anchor: .center)
    }
}
