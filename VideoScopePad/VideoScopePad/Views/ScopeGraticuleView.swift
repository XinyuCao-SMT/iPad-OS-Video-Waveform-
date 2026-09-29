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
                    // 裁到绘图区（外扩 1.5pt，免得绘图区边框线被裁掉一半看起来「缺一条边」）
                    layer.clip(to: Path(plot.insetBy(dx: -1.5, dy: -1.5)))
                    switch content {
                    case .vectorscope:
                        drawVectorscope(&layer, rect: plot, gain: settings.vectorscopeGain)
                    case .diamond:
                        drawDiamond(&layer, rect: plot)
                    case .cie:
                        drawChromaticity(&layer, rect: plot)
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
                    // 刻度栏整体裁在**格子**内：数字与刻度线绝不会跑到相邻格子里去
                    context.drawLayer { layer in
                        layer.clip(to: Path(pane.panel.scaled(to: size)))
                        drawGutter(context: &layer,
                                   gutter: gutterUnit.scaled(to: size),
                                   plot: plot,
                                   content: content,
                                   pane: pane)
                    }
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

            // 标注放在放得下的一侧（绘图区窄的时候放右边会被裁掉）
            let narrow = rect.width < 170
            ctx.draw(label(row.name + " " + unit.formatPrecise(row.value),
                           size: 11,
                           weight: .semibold,
                           opacity: 0.95,
                           monospaced: true),
                     at: CGPoint(x: narrow ? rect.minX + 6 : rect.maxX - 6, y: y - 11),
                     anchor: narrow ? .leading : .trailing)
        }
    }

    // MARK: - 矢量示波器刻度

    private func drawVectorscope(_ ctx: inout GraphicsContext, rect: CGRect, gain: Double) {
        let center = CGPoint(x: rect.midX, y: rect.midY)
        let radius = min(rect.width, rect.height) / 2
        let maxRadius = radius          // 绘图区半边长：超出它的圈/目标框一律不画
        let g = CGFloat(max(gain, 0.25))

        // 字号跟着圆的大小走，格子小的时候自动变小、并减少标注
        let fontSize = min(max(radius / 9, 6.5), 11)
        let showRingLabels = radius >= 70

        let thin = Color.white.opacity(0.18)
        let normal = Color.white.opacity(0.32)
        let strong = Color.white.opacity(0.52)

        for fraction in [0.25, 0.5, 0.75, 1.0] {
            let r = radius * CGFloat(fraction) * g
            // 放大（gain > 1）时外圈会超出绘图区：整圈不画，避免只画出半圈像「图形缺失」
            guard r <= maxRadius + 0.5 else { continue }
            let circle = Path(ellipseIn: CGRect(x: center.x - r, y: center.y - r,
                                                width: r * 2, height: r * 2))
            stroke(&ctx, circle, color: fraction == 0.75 ? strong : thin, width: 1)

            // 标注也要留得下才画
            if showRingLabels, r <= maxRadius - fontSize * 1.4 {
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

        // 75% 彩条目标框（完整落在绘图区内才画）
        for target in Self.colorTargets75 {
            let x = center.x + CGFloat(target.value.x / 0.5) * radius * g
            let y = center.y - CGFloat(target.value.y / 0.5) * radius * g

            let box = max(radius * 0.045, 4)
            let boxRect = CGRect(x: x - box, y: y - box, width: box * 2, height: box * 2)
            guard rect.contains(boxRect) else { continue }

            stroke(&ctx, Path(boxRect),
                   color: Color.white.opacity(0.6),
                   width: 1)
            if radius >= 60 {
                // 名字画在左边还是右边，看哪边放得下
                let placeRight = (x + box + 3 + fontSize * 3.2) <= rect.maxX
                ctx.draw(label(target.name, size: fontSize, weight: .bold, opacity: 0.9),
                         at: CGPoint(x: placeRight ? x + box + 3 : x - box - 3,
                                     y: y - fontSize * 0.6),
                         anchor: placeRight ? .leading : .trailing)
            }
        }

        // 肤色线（I 轴约 123°，广播标准刻度）：长度裁到不出绘图区
        if radius >= 60 {
            let angle = CGFloat(123.0 * Double.pi / 180.0)
            let dx = cos(angle)
            let dy = -sin(angle)
            let reach = maxLength(from: center, dx: dx, dy: dy, in: rect)
            let length = min(radius * g * 0.9, reach * 0.96)
            if length > fontSize * 2 {
                var skin = Path()
                skin.move(to: center)
                skin.addLine(to: CGPoint(x: center.x + dx * length, y: center.y + dy * length))
                stroke(&ctx, skin, color: Color.orange.opacity(0.55), width: 1.2, dash: [4, 3])
                ctx.draw(label("肤色", size: fontSize, weight: .semibold, opacity: 0.8),
                         at: CGPoint(x: center.x + dx * length * 0.8,
                                     y: center.y + dy * length * 0.8),
                         anchor: .leading)
            }
        }

        if radius >= 80 {
            ctx.draw(label("B-Y", size: fontSize, weight: .semibold, opacity: 0.6),
                     at: CGPoint(x: rect.maxX - fontSize * 1.8, y: center.y - fontSize * 0.9))
            ctx.draw(label("R-Y", size: fontSize, weight: .semibold, opacity: 0.6),
                     at: CGPoint(x: center.x + fontSize * 2.2, y: rect.minY + fontSize))
        }
    }

    /// 从矩形中心沿 (dx, dy) 方向到边界的最大距离（用来把参考线裁在绘图区内）
    private func maxLength(from center: CGPoint, dx: CGFloat, dy: CGFloat, in rect: CGRect) -> CGFloat {
        var limit = CGFloat.greatestFiniteMagnitude
        if dx > 0.0001 { limit = min(limit, (rect.maxX - center.x) / dx) }
        if dx < -0.0001 { limit = min(limit, (rect.minX - center.x) / dx) }
        if dy > 0.0001 { limit = min(limit, (rect.maxY - center.y) / dy) }
        if dy < -0.0001 { limit = min(limit, (rect.minY - center.y) / dy) }
        return max(limit, 0)
    }

    // MARK: - 钻石图（RGB 色域）

    /// 钻石图刻度：RGB 立方体沿白轴投影后，0–100% 的合法区域是「上下两个菱形叠起来」的六边形。
    ///   顶点 R(0,1)、右 M(1,0.5)、右下 B(1,-0.5)、底 C(0,-1)、左下 G(-1,-0.5)、左 Y(-1,0.5)
    ///   白色 (1,1,1) 落在中心。任何分量超出 0–100% 都会把点推到六边形外 → 色域越界。
    /// 纹理里的坐标是 0–1 的归一化值（x: ±√3/2 归一化、y: ±1 归一化），所以这里直接映射。
    private func drawDiamond(_ ctx: inout GraphicsContext, rect: CGRect) {
        let fontSize = min(max(min(rect.width, rect.height) / 22, 7), 11)

        /// 归一化坐标（x/y ∈ -1...1，y 向上）→ 绘图区坐标
        func point(_ x: CGFloat, _ y: CGFloat) -> CGPoint {
            CGPoint(x: rect.midX + x * rect.width / 2,
                    y: rect.midY - y * rect.height / 2)
        }

        let vertices: [(CGFloat, CGFloat)] = [
            (0, 1),      // R
            (1, 0.5),    // M
            (1, -0.5),   // B
            (0, -1),     // C
            (-1, -0.5),  // G
            (-1, 0.5)    // Y
        ]

        // 外框（100% 边界）
        var hex = Path()
        hex.move(to: point(vertices[0].0, vertices[0].1))
        for vertex in vertices.dropFirst() { hex.addLine(to: point(vertex.0, vertex.1)) }
        hex.closeSubpath()
        stroke(&ctx, hex, color: Color.white.opacity(0.35), width: 1.2)

        // 内框（75% 彩条边界）—— 灰阶以外的区域是否越界一眼可见
        var inner = Path()
        for (index, vertex) in vertices.enumerated() {
            let p = point(vertex.0 * 0.75, vertex.1 * 0.75)
            if index == 0 { inner.move(to: p) } else { inner.addLine(to: p) }
        }
        inner.closeSubpath()
        stroke(&ctx, inner, color: Color.white.opacity(0.16), width: 1)

        // 三条轴（中心 → R / G / B）
        for vertex in [vertices[0], vertices[3 + 1], vertices[2]] {
            var axis = Path()
            axis.move(to: point(0, 0))
            axis.addLine(to: point(vertex.0, vertex.1))
            stroke(&ctx, axis, color: Color.white.opacity(0.20), width: 1)
        }

        // 中心十字（白色所在位置）
        var cross = Path()
        cross.move(to: point(-0.12, 0))
        cross.addLine(to: point(0.12, 0))
        cross.move(to: point(0, -0.12))
        cross.addLine(to: point(0, 0.12))
        stroke(&ctx, cross, color: Color.white.opacity(0.30), width: 1)

        // 240 网格（25% 间隔的辅助菱形边）
        for level in [0.25, 0.5] as [CGFloat] {
            var grid = Path()
            for (index, vertex) in vertices.enumerated() {
                let p = point(vertex.0 * level, vertex.1 * level)
                if index == 0 { grid.move(to: p) } else { grid.addLine(to: p) }
            }
            grid.closeSubpath()
            stroke(&ctx, grid, color: Color.white.opacity(0.10), width: 1)
        }

        // 轴与顶点标注
        let labels: [(String, CGFloat, CGFloat, UnitPoint)] = [
            ("R", 0, 1.06, .center),
            ("M", 1.06, 0.5, .leading),
            ("B", 1.06, -0.5, .leading),
            ("C", 0, -1.06, .center),
            ("G", -1.06, -0.5, .trailing),
            ("Y", -1.06, 0.5, .trailing)
        ]
        for label in labels {
            ctx.draw(self.label(label.0, size: fontSize, weight: .bold, opacity: 0.85),
                     at: point(label.1, label.2),
                     anchor: label.3)
        }

        ctx.draw(self.label("W", size: fontSize * 0.9, weight: .semibold, opacity: 0.5),
                 at: point(0.06, 0.05),
                 anchor: .leading)
    }

    // MARK: - 马蹄图（CIE 1931 色度）

    /// CIE 刻度：光谱轨迹（马蹄形）+ 709 / 2020 色域三角 + D65 白点。
    /// 坐标映射与 Metal 侧完全一致（用 ShaderTypes.h 里的 VS_CIE_ORIGIN_* / VS_CIE_SPAN）。
    private func drawChromaticity(_ ctx: inout GraphicsContext, rect: CGRect) {
        let fontSize = min(max(min(rect.width, rect.height) / 26, 7), 11)
        let span = Double(VS_CIE_SPAN)
        let originX = Double(VS_CIE_ORIGIN_X)
        let originY = Double(VS_CIE_ORIGIN_Y)

        /// xy 色度坐标 → 绘图区坐标
        func point(_ x: Double, _ y: Double) -> CGPoint {
            let nx = (x + originX) / span
            let ny = (y + originY) / span
            return CGPoint(x: rect.minX + CGFloat(nx) * rect.width,
                           y: rect.maxY - CGFloat(ny) * rect.height)
        }

        // 坐标框 + 0.1 网格
        stroke(&ctx, Path(rect), color: Color.white.opacity(0.25), width: 1)
        var grid = Path()
        var step = 0.1
        while step < 0.9 {
            grid.move(to: point(step, -originY))
            grid.addLine(to: point(step, -originY + span))
            grid.move(to: point(-originX, step))
            grid.addLine(to: point(-originX + span, step))
            step += 0.1
        }
        stroke(&ctx, grid, color: Color.white.opacity(0.08), width: 1)

        // 光谱轨迹（380–700nm，5nm 间隔的 CIE 1931 2° 标准观察者数据）
        var locus = Path()
        for (index, sample) in Self.spectralLocus.enumerated() {
            let p = point(sample.x, sample.y)
            if index == 0 { locus.move(to: p) } else { locus.addLine(to: p) }
        }
        locus.closeSubpath()          // 补上紫边（700nm → 380nm）
        stroke(&ctx, locus, color: Color.white.opacity(0.55), width: 1.2)

        // 高清 BT.709 与 BT.2020 色域三角
        // 注意：这里返回 Path 而不是在嵌套函数里改 ctx —— 嵌套函数捕获 inout 参数有额外限制
        func trianglePath(_ r: (Double, Double),
                          _ g: (Double, Double),
                          _ b: (Double, Double)) -> Path {
            var path = Path()
            path.move(to: point(r.0, r.1))
            path.addLine(to: point(g.0, g.1))
            path.addLine(to: point(b.0, b.1))
            path.closeSubpath()
            return path
        }

        func drawTriangle(_ name: String,
                          _ r: (Double, Double),
                          _ g: (Double, Double),
                          _ b: (Double, Double),
                          opacity: Double) {
            stroke(&ctx, trianglePath(r, g, b), color: Color.white.opacity(opacity), width: 1.2)

            let center = point((r.0 + g.0 + b.0) / 3, (r.1 + g.1 + b.1) / 3)
            ctx.draw(label(name, size: fontSize * 0.9, weight: .bold, opacity: opacity + 0.15),
                     at: CGPoint(x: center.x, y: center.y + fontSize * 0.7))
        }

        drawTriangle("BT.709", (0.640, 0.330), (0.300, 0.600), (0.150, 0.060), opacity: 0.42)
        drawTriangle("BT.2020", (0.708, 0.292), (0.170, 0.797), (0.131, 0.046), opacity: 0.22)

        // D65 白点
        let white = point(0.3127, 0.3290)
        let r: CGFloat = max(fontSize * 0.4, 3)
        stroke(&ctx, Path(ellipseIn: CGRect(x: white.x - r, y: white.y - r, width: r * 2, height: r * 2)),
               color: Color.white.opacity(0.9),
               width: 1.2)
        ctx.draw(label("D65", size: fontSize * 0.85, weight: .semibold, opacity: 0.75),
                 at: CGPoint(x: white.x + r + 2, y: white.y - fontSize * 0.6),
                 anchor: .leading)

        // 轴标注
        ctx.draw(label("x", size: fontSize, weight: .semibold, opacity: 0.6),
                 at: CGPoint(x: rect.maxX - fontSize * 1.6, y: rect.maxY - fontSize * 1.1),
                 anchor: .center)
        ctx.draw(label("y", size: fontSize, weight: .semibold, opacity: 0.6),
                 at: CGPoint(x: rect.minX + fontSize * 1.4, y: rect.minY + fontSize * 1.1),
                 anchor: .center)
    }

    /// CIE 1931 2° 光谱轨迹（380–700nm，5nm 步长，x / y 色度坐标）
    static let spectralLocus: [(x: Double, y: Double)] = [
        (0.1741, 0.0050), (0.1740, 0.0050), (0.1738, 0.0049), (0.1736, 0.0049),
        (0.1733, 0.0048), (0.1730, 0.0048), (0.1726, 0.0048), (0.1721, 0.0048),
        (0.1714, 0.0051), (0.1703, 0.0058), (0.1689, 0.0069), (0.1669, 0.0086),
        (0.1644, 0.0109), (0.1611, 0.0138), (0.1566, 0.0177), (0.1510, 0.0227),
        (0.1440, 0.0297), (0.1355, 0.0399), (0.1241, 0.0578), (0.1096, 0.0868),
        (0.0913, 0.1327), (0.0687, 0.2007), (0.0454, 0.2950), (0.0235, 0.4127),
        (0.0082, 0.5384), (0.0039, 0.6548), (0.0139, 0.7502), (0.0389, 0.8120),
        (0.0743, 0.8338), (0.1142, 0.8262), (0.1547, 0.8059), (0.1929, 0.7816),
        (0.2292, 0.7543), (0.2658, 0.7243), (0.3016, 0.6923), (0.3373, 0.6589),
        (0.3731, 0.6245), (0.4087, 0.5896), (0.4441, 0.5547), (0.4788, 0.5202),
        (0.5125, 0.4866), (0.5448, 0.4544), (0.5752, 0.4242), (0.6029, 0.3965),
        (0.6270, 0.3725), (0.6482, 0.3514), (0.6658, 0.3340), (0.6801, 0.3197),
        (0.6915, 0.3083), (0.7006, 0.2993), (0.7079, 0.2920), (0.7140, 0.2859),
        (0.7190, 0.2809), (0.7230, 0.2770), (0.7260, 0.2740), (0.7283, 0.2717),
        (0.7300, 0.2700), (0.7311, 0.2689), (0.7320, 0.2680), (0.7327, 0.2673),
        (0.7334, 0.2666), (0.7340, 0.2660), (0.7344, 0.2656), (0.7346, 0.2654),
        (0.7347, 0.2653)
    ]


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
