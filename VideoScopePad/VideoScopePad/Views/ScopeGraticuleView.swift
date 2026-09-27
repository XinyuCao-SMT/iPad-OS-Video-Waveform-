//
//  ScopeGraticuleView.swift
//  VideoScopePad
//
//  示波器刻度叠加层：用 SwiftUI Canvas 画在 Metal 画面之上（文字清晰、不占 GPU 通道）。
//
//  与轨迹共用同一份 ScopeLayout，所以：
//    · plot   → Metal 把示波器轨迹画在这里
//    · gutter → 这里画「大号数字刻度」（波形/RGB 才有）
//  两者严格对齐，数字不会压在轨迹上。
//
//  刻度单位可以切换 IRE / mV / %：
//    mV 是按广播规范换算的**等效电平**（100 IRE = 700 mV），
//    因为 UVC 交给我们的是已数字化的码流，采集卡输入端的真实模拟电压读不到。
//

import SwiftUI

struct ScopeGraticuleView: View {

    let layout: ScopeLayoutResult
    @ObservedObject var settings: AppSettings
    @ObservedObject var measurement: MeasurementHub
    let videoRange: Bool

    var body: some View {
        Canvas { context, size in
            for pane in layout.panes {
                guard let content = pane.content.scopeKind, let plotUnit = pane.plot else { continue }
                let plot = plotUnit.scaled(to: size)
                guard plot.width > 16, plot.height > 16 else { continue }

                context.drawLayer { layer in
                    layer.clip(to: Path(plot))
                    switch content {
                    case .vectorscope:
                        drawVectorscope(&layer, rect: plot, gain: settings.vectorscopeGain)
                    case .waveform:
                        drawWaveform(&layer,
                                     rect: plot,
                                     columns: 1,
                                     labels: settings.waveformMode == .luma ? ["Y"] : ["RGB"])
                    case .parade:
                        drawWaveform(&layer, rect: plot, columns: 3, labels: ["R", "G", "B"])
                    }
                }

                if let gutterUnit = pane.gutter {
                    drawGutter(context: &context,
                               gutter: gutterUnit.scaled(to: size),
                               plot: plot,
                               content: content,
                               pane: pane)
                }

                drawLiveReadout(context: &context,
                                pane: pane,
                                panel: pane.panel.scaled(to: size),
                                content: content)
            }
        }
        .allowsHitTesting(false)
    }

    // MARK: - 侧边刻度栏（要大、要清楚）

    private func drawGutter(context: inout GraphicsContext,
                            gutter: CGRect,
                            plot: CGRect,
                            content: ScopePanelKind,
                            pane: PaneLayout) {
        guard gutter.width > 12, gutter.height > 20 else { return }

        let unit = settings.scaleUnit
        let values = unit.tickValues()
        let tickRight = gutter.maxX - 3
        let numberAnchorX = gutter.maxX - 8

        // 单位名（画在刻度栏顶部）
        context.draw(label(unit.shortTitle, size: 11, weight: .bold, opacity: 0.95),
                     at: CGPoint(x: gutter.midX, y: gutter.minY + 10))

        for value in values {
            let ire = ireFromTick(value, unit: unit)
            let y = yPosition(forIRE: ire, in: plot)
            guard y >= plot.minY - 1, y <= plot.maxY + 1 else { continue }

            let major = unit.isMajorTick(value)

            // 刻度短横线
            var tick = Path()
            tick.move(to: CGPoint(x: tickRight - (major ? 9 : 5), y: y))
            tick.addLine(to: CGPoint(x: tickRight, y: y))
            context.stroke(tick,
                           with: .color(.white.opacity(major ? 0.75 : 0.35)),
                           style: StrokeStyle(lineWidth: major ? 1.5 : 1))

            guard major else { continue }

            context.draw(label(unit.format(value),
                               size: 12,
                               weight: .semibold,
                               opacity: 0.95,
                               monospaced: true),
                         at: CGPoint(x: numberAnchorX, y: y),
                         anchor: .trailing)
        }

        // 通道名（Parade 三列在底部标注）
        if content == .parade {
            let columnWidth = plot.width / 3
            for (index, name) in ["R", "G", "B"].enumerated() {
                context.draw(label(name, size: 12, weight: .bold, opacity: 0.9),
                             at: CGPoint(x: plot.minX + columnWidth * (CGFloat(index) + 0.5),
                                         y: plot.maxY - 11))
            }
        }
    }

    /// 格子右上角的实时读数（数值标识）
    private func drawLiveReadout(context: inout GraphicsContext,
                                 pane: PaneLayout,
                                 panel: CGRect,
                                 content: ScopePanelKind) {
        guard let value = measurement.value else { return }

        let text: String
        switch content {
        case .waveform, .parade:
            text = String(format: "▲ %@    ▼ %@",
                          settings.scaleUnit.formatPrecise(value.stableWhiteIRE),
                          settings.scaleUnit.formatPrecise(value.stableBlackIRE))
        case .vectorscope:
            text = String(format: "色度峰值 %.0f%%   平均 %@",
                          value.peakSaturationPercent,
                          settings.scaleUnit.formatPrecise(value.averageIRE))
        }

        let point = CGPoint(x: panel.minX + 8, y: panel.minY + 12)
        context.draw(label(text, size: 11, weight: .semibold, opacity: 0.92, monospaced: true),
                     at: point,
                     anchor: .leading)
    }

    // MARK: - 矢量示波器刻度

    private func drawVectorscope(_ ctx: inout GraphicsContext, rect: CGRect, gain: Double) {
        let center = CGPoint(x: rect.midX, y: rect.midY)
        let radius = min(rect.width, rect.height) / 2
        let g = CGFloat(max(gain, 0.25))

        let thin = Color.white.opacity(0.18)
        let normal = Color.white.opacity(0.32)
        let strong = Color.white.opacity(0.52)

        for fraction in [0.25, 0.5, 0.75, 1.0] {
            let r = radius * CGFloat(fraction) * g
            guard r <= radius * 1.8 else { continue }
            let circle = Path(ellipseIn: CGRect(x: center.x - r, y: center.y - r,
                                                width: r * 2, height: r * 2))
            stroke(&ctx, circle, color: fraction == 0.75 ? strong : thin, width: 1)

            // 圆环百分比标注（画在竖直轴上方，比之前更大更明显）
            ctx.draw(label(String(format: "%.0f%%", fraction * 100),
                           size: 10,
                           weight: .semibold,
                           opacity: 0.7,
                           monospaced: true),
                     at: CGPoint(x: center.x + 12, y: center.y - r),
                     anchor: .leading)
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

            let box = CGRect(x: x - 5.5, y: y - 5.5, width: 11, height: 11)
            stroke(&ctx, Path(box), color: Color.white.opacity(0.6), width: 1)
            ctx.draw(label(target.name, size: 10, weight: .bold, opacity: 0.9),
                     at: CGPoint(x: x + 9, y: y - 6),
                     anchor: .leading)
        }

        // 肤色线（I 轴约 123°，广播标准刻度）
        let angle = CGFloat(123.0 * Double.pi / 180.0)
        let length = radius * g * 0.9
        var skin = Path()
        skin.move(to: center)
        skin.addLine(to: CGPoint(x: center.x + cos(angle) * length,
                                 y: center.y - sin(angle) * length))
        stroke(&ctx, skin, color: Color.orange.opacity(0.55), width: 1.2, dash: [4, 3])
        ctx.draw(label("肤色", size: 10, weight: .semibold, opacity: 0.8),
                 at: CGPoint(x: center.x + cos(angle) * length * 0.8,
                             y: center.y - sin(angle) * length * 0.8),
                 anchor: .leading)

        ctx.draw(label("B-Y", size: 10, weight: .semibold, opacity: 0.6),
                 at: CGPoint(x: rect.maxX - 18, y: center.y - 9))
        ctx.draw(label("R-Y", size: 10, weight: .semibold, opacity: 0.6),
                 at: CGPoint(x: center.x + 24, y: rect.minY + 10))
    }

    // MARK: - 波形刻度

    private func drawWaveform(_ ctx: inout GraphicsContext,
                              rect: CGRect,
                              columns: Int,
                              labels: [String]) {
        let unit = settings.scaleUnit

        for value in unit.tickValues() {
            let ire = ireFromTick(value, unit: unit)
            let y = yPosition(forIRE: ire, in: rect)
            guard y >= rect.minY - 1, y <= rect.maxY + 1 else { continue }

            let major = unit.isMajorTick(value)
            var path = Path()
            path.move(to: CGPoint(x: rect.minX, y: y))
            path.addLine(to: CGPoint(x: rect.maxX, y: y))
            stroke(&ctx, path, color: Color.white.opacity(major ? 0.42 : 0.14), width: 1)
        }

        let columnWidth = rect.width / CGFloat(max(columns, 1))

        if columns > 1 {
            for index in 1..<columns {
                let x = rect.minX + columnWidth * CGFloat(index)
                var path = Path()
                path.move(to: CGPoint(x: x, y: rect.minY))
                path.addLine(to: CGPoint(x: x, y: rect.maxY))
                stroke(&ctx, path, color: Color.white.opacity(0.34), width: 1)
            }
        }

        for fraction in [0.25, 0.5, 0.75] {
            for column in 0..<max(columns, 1) {
                let x = rect.minX + columnWidth * (CGFloat(column) + CGFloat(fraction))
                var path = Path()
                path.move(to: CGPoint(x: x, y: rect.minY))
                path.addLine(to: CGPoint(x: x, y: rect.maxY))
                stroke(&ctx, path, color: Color.white.opacity(0.10), width: 1)
            }
        }

        if columns == 1 {
            ctx.draw(label(labels.first ?? "Y", size: 11, weight: .bold, opacity: 0.75),
                     at: CGPoint(x: rect.maxX - 12, y: rect.minY + 11))
        }

        stroke(&ctx, Path(rect), color: Color.white.opacity(0.3), width: 1)
    }

    // MARK: - 单位换算与坐标

    /// 刻度值 → IRE
    private func ireFromTick(_ value: Double, unit: ScaleUnit) -> Double {
        switch unit {
        case .ire, .percent: return value
        case .millivolt: return value / (ScaleUnit.millivoltPerHundredIRE / 100)
        }
    }

    /// IRE → 纵向坐标（视频范围 0 IRE = 码值 16、100 IRE = 235；全范围 0/255）
    private func yPosition(forIRE ire: Double, in rect: CGRect) -> CGFloat {
        let code = videoRange ? (16 + ire / 100 * 219) : (ire / 100 * 255)
        return rect.maxY - CGFloat(code / 255) * rect.height
    }

    // MARK: - 工具

    private func stroke(_ ctx: inout GraphicsContext,
                        _ path: Path,
                        color: Color,
                        width: CGFloat,
                        dash: [CGFloat] = []) {
        ctx.stroke(path, with: .color(color), style: StrokeStyle(lineWidth: width, dash: dash))
    }

    private func label(_ string: String,
                       size: CGFloat,
                       weight: Font.Weight = .regular,
                       opacity: Double = 0.62,
                       monospaced: Bool = false) -> Text {
        Text(string)
            .font(.system(size: size,
                          weight: weight,
                          design: monospaced ? .monospaced : .default))
            .foregroundStyle(Color.white.opacity(opacity))
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
