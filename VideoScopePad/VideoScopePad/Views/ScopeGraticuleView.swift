//
//  ScopeGraticuleView.swift
//  VideoScopePad
//
//  示波器刻度（矢量示波器的圆环/色标框/肤色线、波形的 IRE 刻度）。
//  用 SwiftUI Canvas 画在 Metal 画面之上——文字清晰、不占用 GPU 渲染通道。
//  刻度与轨迹共用同一份 ScopeLayout，保证严格对齐。
//

import SwiftUI

struct ScopeGraticuleView: View {

    let layout: ScopeLayoutResult
    @ObservedObject var settings: AppSettings
    let videoRange: Bool

    var body: some View {
        Canvas { context, size in
            for kind in settings.enabledPanels {
                guard let unit = layout.plots[kind] else { continue }
                let rect = unit.scaled(to: size)
                guard rect.width > 12, rect.height > 12 else { continue }

                context.drawLayer { layer in
                    layer.clip(to: Path(rect))
                    switch kind {
                    case .vectorscope:
                        drawVectorscope(&layer, rect: rect, gain: settings.vectorscopeGain)
                    case .waveform:
                        drawWaveform(&layer,
                                     rect: rect,
                                     columns: 1,
                                     labels: settings.waveformMode == .luma ? ["Y"] : ["RGB"],
                                     videoRange: videoRange)
                    case .parade:
                        drawWaveform(&layer,
                                     rect: rect,
                                     columns: 3,
                                     labels: ["R", "G", "B"],
                                     videoRange: videoRange)
                    }
                }
            }
        }
        .allowsHitTesting(false)
    }

    // MARK: - 矢量示波器刻度

    private func drawVectorscope(_ ctx: inout GraphicsContext, rect: CGRect, gain: Double) {
        let center = CGPoint(x: rect.midX, y: rect.midY)
        let radius = min(rect.width, rect.height) / 2
        let g = CGFloat(max(gain, 0.25))

        let thin = Color.white.opacity(0.16)
        let normal = Color.white.opacity(0.30)
        let strong = Color.white.opacity(0.48)

        for fraction in [0.25, 0.5, 0.75, 1.0] {
            let r = radius * CGFloat(fraction) * g
            guard r <= radius * 1.8 else { continue }
            let circle = Path(ellipseIn: CGRect(x: center.x - r, y: center.y - r,
                                                width: r * 2, height: r * 2))
            stroke(&ctx, circle, color: fraction == 0.75 ? strong : thin, width: 1)
        }

        var cross = Path()
        cross.move(to: CGPoint(x: rect.minX, y: center.y))
        cross.addLine(to: CGPoint(x: rect.maxX, y: center.y))
        cross.move(to: CGPoint(x: center.x, y: rect.minY))
        cross.addLine(to: CGPoint(x: center.x, y: rect.maxY))
        stroke(&ctx, cross, color: normal, width: 1)

        // 75% 彩条目标框
        for target in Self.colorTargets75 {
            let x = center.x + CGFloat(target.value.x / 0.5) * radius * g
            let y = center.y - CGFloat(target.value.y / 0.5) * radius * g

            let box = CGRect(x: x - 5, y: y - 5, width: 10, height: 10)
            stroke(&ctx, Path(box), color: Color.white.opacity(0.55), width: 1)
            ctx.draw(text(target.name, size: 8),
                     at: CGPoint(x: x + 8, y: y - 5),
                     anchor: .leading)
        }

        // 肤色线（I 轴约 123°，广播标准刻度）
        let angle = CGFloat(123.0 * Double.pi / 180.0)
        let length = radius * g * 0.9
        var skin = Path()
        skin.move(to: center)
        skin.addLine(to: CGPoint(x: center.x + cos(angle) * length,
                                 y: center.y - sin(angle) * length))
        stroke(&ctx, skin, color: Color.orange.opacity(0.5), width: 1, dash: [4, 3])
        ctx.draw(text("肤色", size: 8),
                 at: CGPoint(x: center.x + cos(angle) * length * 0.78,
                             y: center.y - sin(angle) * length * 0.78),
                 anchor: .leading)

        ctx.draw(text("B-Y", size: 8), at: CGPoint(x: rect.maxX - 16, y: center.y - 8))
        ctx.draw(text("R-Y", size: 8), at: CGPoint(x: center.x + 22, y: rect.minY + 8))
    }

    // MARK: - 波形刻度

    private func drawWaveform(_ ctx: inout GraphicsContext,
                              rect: CGRect,
                              columns: Int,
                              labels: [String],
                              videoRange: Bool) {

        func y(forIRE ire: CGFloat) -> CGFloat {
            let code = videoRange ? (16 + ire / 100 * 219) : (ire / 100 * 255)
            return rect.maxY - (code / 255) * rect.height
        }

        for step in stride(from: 0, through: 100, by: 10) {
            let ire = CGFloat(step)
            let yy = y(forIRE: ire)
            let isMajor = step % 25 == 0

            var path = Path()
            path.move(to: CGPoint(x: rect.minX, y: yy))
            path.addLine(to: CGPoint(x: rect.maxX, y: yy))
            stroke(&ctx, path, color: Color.white.opacity(isMajor ? 0.40 : 0.14), width: 1)

            if isMajor {
                ctx.draw(text("\(step)", size: 8),
                         at: CGPoint(x: rect.minX + 3, y: yy - 6),
                         anchor: .leading)
            }
        }

        let columnWidth = rect.width / CGFloat(max(columns, 1))

        if columns > 1 {
            for index in 1..<columns {
                let xx = rect.minX + columnWidth * CGFloat(index)
                var path = Path()
                path.move(to: CGPoint(x: xx, y: rect.minY))
                path.addLine(to: CGPoint(x: xx, y: rect.maxY))
                stroke(&ctx, path, color: Color.white.opacity(0.32), width: 1)
            }
        }

        for fraction in [0.25, 0.5, 0.75] {
            for column in 0..<max(columns, 1) {
                let xx = rect.minX + columnWidth * (CGFloat(column) + CGFloat(fraction))
                var path = Path()
                path.move(to: CGPoint(x: xx, y: rect.minY))
                path.addLine(to: CGPoint(x: xx, y: rect.maxY))
                stroke(&ctx, path, color: Color.white.opacity(0.10), width: 1)
            }
        }

        if columns > 1 {
            for (index, label) in labels.enumerated() {
                ctx.draw(text(label, size: 9),
                         at: CGPoint(x: rect.minX + columnWidth * (CGFloat(index) + 0.5),
                                     y: rect.maxY - 9))
            }
        }

        ctx.draw(text("IRE", size: 8),
                 at: CGPoint(x: rect.minX + 26, y: rect.minY + 8),
                 anchor: .leading)

        stroke(&ctx, Path(rect), color: Color.white.opacity(0.26), width: 1)
    }

    // MARK: - 工具

    private func stroke(_ ctx: inout GraphicsContext,
                        _ path: Path,
                        color: Color,
                        width: CGFloat,
                        dash: [CGFloat] = []) {
        ctx.stroke(path, with: .color(color), style: StrokeStyle(lineWidth: width, dash: dash))
    }

    private func text(_ string: String, size: CGFloat) -> Text {
        Text(string)
            .font(.system(size: size, weight: .regular, design: .monospaced))
            .foregroundStyle(Color.white.opacity(0.62))
    }

    /// 75% 彩条在 (Cb, Cr) 平面上的位置（BT.709 系数）
    static let colorTargets75: [(name: String, value: SIMD2<Float>)] = {
        func chroma(_ r: Float, _ g: Float, _ b: Float) -> SIMD2<Float> {
            let cb = -0.114572 * r - 0.385428 * g + 0.5 * b
            let cr = 0.5 * r - 0.454153 * g - 0.045847 * b
            return SIMD2(cb, cr)
        }
        let level: Float = 0.75
        return [
            ("R", chroma(level, 0, 0)),
            ("Mg", chroma(level, 0, level)),
            ("B", chroma(0, 0, level)),
            ("Cy", chroma(0, level, level)),
            ("G", chroma(0, level, 0)),
            ("Yl", chroma(level, level, 0))
        ]
    }()
}
