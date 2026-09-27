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
                        drawPeakHold(&layer, rect: plot)
                    case .parade:
                        drawWaveform(&layer, rect: plot, columns: 3, labels: ["R", "G", "B"])
                        drawPeakHold(&layer, rect: plot)
                    }
                }

                if let gutterUnit = pane.gutter {
                    drawGutter(context: &context,
                               gutter: gutterUnit.scaled(to: size),
                               plot: plot,
                               content: content,
                               pane: pane)
                }
            }
        }
        .allowsHitTesting(false)
    }

    // MARK: - 侧边刻度栏（随格子大小自适应）

    private func drawGutter(context: inout GraphicsContext,
                            gutter: CGRect,
                            plot: CGRect,
                            content: ScopePanelKind,
                            pane: PaneLayout) {
        guard gutter.width > 10, gutter.height > 18 else { return }

        let unit = settings.scaleUnit
        let values = unit.tickValues()

        // 字号跟着刻度栏宽度走：格子小就自动变小（<=7pt 就不再画数字，只留刻度线）
        let fontSize = min(max(gutter.width * 0.30, 7), 12)
        let showNumbers = gutter.width >= 22
        let tickRight = gutter.maxX - 3
        let numberAnchorX = gutter.maxX - max(fontSize * 0.5, 4)

        // 单位名（刻度栏够宽才画）
        if gutter.width >= 30 {
            context.draw(label(unit.shortTitle,
                               size: min(fontSize, 11),
                               weight: .bold,
                               opacity: 0.95),
                         at: CGPoint(x: gutter.midX, y: gutter.minY + fontSize),
                         anchor: .center)
        }

        for value in values {
            let ire = ireFromTick(value, unit: unit)
            let y = yPosition(forIRE: ire, in: plot)
            guard y >= plot.minY - 1, y <= plot.maxY + 1 else { continue }

            let major = unit.isMajorTick(value)

            var tick = Path()
            tick.move(to: CGPoint(x: tickRight - (major ? 9 : 5), y: y))
            tick.addLine(to: CGPoint(x: tickRight, y: y))
            context.stroke(tick,
                           with: .color(.white.opacity(major ? 0.75 : 0.35)),
                           style: StrokeStyle(lineWidth: major ? 1.5 : 1))

            guard showNumbers, isTickLabelVisible(value, unit: unit, plotHeight: plot.height) else { continue }

            context.draw(label(unit.format(value),
                               size: fontSize,
                               weight: .semibold,
                               opacity: 0.95,
                               monospaced: true),
                         at: CGPoint(x: numberAnchorX, y: y),
                         anchor: .trailing)
        }

        // 通道名（Parade 三列在底部标注）
        if content == .parade, plot.width > 120 {
            let columnWidth = plot.width / 3
            for (index, name) in ["R", "G", "B"].enumerated() {
                context.draw(label(name, size: max(fontSize - 1, 8), weight: .bold, opacity: 0.9),
                             at: CGPoint(x: plot.minX + columnWidth * (CGFloat(index) + 0.5),
                                         y: plot.maxY - max(fontSize, 9)))
            }
        }
    }

    /// 绘图区越矮，标注越稀，避免数字互相叠住
    private func isTickLabelVisible(_ value: Double, unit: ScaleUnit, plotHeight: CGFloat) -> Bool {
        if plotHeight >= 240 { return true }
        if plotHeight >= 130 { return unit.isMajorTick(value) }

        let values = unit.tickValues()
        guard let first = values.first, let last = values.last else { return false }
        let middle = values[values.count / 2]
        return value == first || value == middle || value == last
    }

    // MARK: - 峰值保持游标

    /// 把保持住的最高 / 最低电平用虚线钉在波形上（数值跟着刻度单位走）
    private func drawPeakHold(_ ctx: inout GraphicsContext, rect: CGRect) {
        guard settings.peakHoldEnabled else { return }
        let state = measurement.peakHold
        guard state.hasData else { return }

        let unit = settings.scaleUnit
        let rows: [(value: Double, color: Color, name: String)] = [
            (state.whitePeakIRE, Color.yellow.opacity(0.9), "峰值"),
            (state.blackFloorIRE, Color.cyan.opacity(0.9), "黑位")
        ]

        for row in rows {
            let y = yPosition(forIRE: row.value, in: rect)
            guard y >= rect.minY - 1, y <= rect.maxY + 1 else { continue }

            var path = Path()
            path.move(to: CGPoint(x: rect.minX, y: y))
            path.addLine(to: CGPoint(x: rect.maxX, y: y))
            stroke(&ctx, path, color: row.color, width: 1.2, dash: [5, 3])

            ctx.draw(label(row.name + " " + unit.formatPrecise(row.value),
                           size: 11,
                           weight: .semibold,
                           opacity: 0.95,
                           monospaced: true),
                     at: CGPoint(x: rect.maxX - 6, y: y - 11),
                     anchor: .trailing)
        }
    }

    // MARK: - 矢量示波器刻度

    private func drawVectorscope(_ ctx: inout GraphicsContext, rect: CGRect, gain: Double) {
        let center = CGPoint(x: rect.midX, y: rect.midY)
        let radius = min(rect.width, rect.height) / 2
        let g = CGFloat(max(gain, 0.25))

        // 字号跟着圆的大小走，格子小的时候自动变小、并减少标注
        let fontSize = min(max(radius / 9, 6.5), 11)
        let showRingLabels = radius >= 70

        let thin = Color.white.opacity(0.18)
        let normal = Color.white.opacity(0.32)
        let strong = Color.white.opacity(0.52)

        for fraction in [0.25, 0.5, 0.75, 1.0] {
            let r = radius * CGFloat(fraction) * g
            guard r <= radius * 1.8 else { continue }
            let circle = Path(ellipseIn: CGRect(x: center.x - r, y: center.y - r,
                                                width: r * 2, height: r * 2))
            stroke(&ctx, circle, color: fraction == 0.75 ? strong : thin, width: 1)

            if showRingLabels {
                ctx.draw(label(String(format: "%.0f%%", fraction * 100),
                               size: fontSize,
                               weight: .semibold,
                               opacity: 0.7,
                               monospaced: true),
                         at: CGPoint(x: center.x + fontSize * 0.9, y: center.y - r),
                         anchor: .leading)
            }
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

            let box = max(radius * 0.045, 4)
            stroke(&ctx, Path(CGRect(x: x - box, y: y - box, width: box * 2, height: box * 2)),
                   color: Color.white.opacity(0.6),
                   width: 1)
            if radius >= 60 {
                ctx.draw(label(target.name, size: fontSize, weight: .bold, opacity: 0.9),
                         at: CGPoint(x: x + box + 3, y: y - fontSize * 0.6),
                         anchor: .leading)
            }
        }

        // 肤色线（I 轴约 123°，广播标准刻度）
        if radius >= 60 {
            let angle = CGFloat(123.0 * Double.pi / 180.0)
            let length = radius * g * 0.9
            var skin = Path()
            skin.move(to: center)
            skin.addLine(to: CGPoint(x: center.x + cos(angle) * length,
                                     y: center.y - sin(angle) * length))
            stroke(&ctx, skin, color: Color.orange.opacity(0.55), width: 1.2, dash: [4, 3])
            ctx.draw(label("肤色", size: fontSize, weight: .semibold, opacity: 0.8),
                     at: CGPoint(x: center.x + cos(angle) * length * 0.8,
                                 y: center.y - sin(angle) * length * 0.8),
                     anchor: .leading)
        }

        if radius >= 80 {
            ctx.draw(label("B-Y", size: fontSize, weight: .semibold, opacity: 0.6),
                     at: CGPoint(x: rect.maxX - fontSize * 1.8, y: center.y - fontSize * 0.9))
            ctx.draw(label("R-Y", size: fontSize, weight: .semibold, opacity: 0.6),
                     at: CGPoint(x: center.x + fontSize * 2.2, y: rect.minY + fontSize))
        }
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
