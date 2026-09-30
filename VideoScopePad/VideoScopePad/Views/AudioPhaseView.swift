//
//  AudioPhaseView.swift
//  VideoScopePad
//
//  声相（李萨如 / goniometer）：把立体声的 L / R 关系画成二维分布。
//
//  坐标：横向 = Side = (L − R)/2（右为正），纵向 = Mid = (L + R)/2（上为正）。
//  这样判读方式与专业 goniometer 一致：
//    · 单声道 / 完全同相 → 一条**竖直中线**
//    · 完全反相 → 一条**水平线**
//    · 只有左声道 → 左上 45° 方向；只有右声道 → 右上 45° 方向
//    · 中间偏上的宽云团 = 正常立体声；云团越宽表示 Side 分量越大
//  另外给出**相关度**（+1 同相 / 0 无关 / −1 反相）与 **L/R 平衡**（dB）。
//
//  数据来自 AudioMonitor：它按样本累加 (Side, Mid) 直方图，每约 40 ms 发布一份
//  点列表快照（只保留非零点），所以这里只做绘制，不做统计。
//

import SwiftUI

struct AudioPhasePaneView: View {

    @ObservedObject var audio: AudioMonitor

    var body: some View {
        Canvas { context, size in
            let rect = CGRect(origin: .zero, size: size)
            guard rect.width > 80, rect.height > 80 else { return }

            drawBackground(&context, rect: rect)

            let fontSize = min(max(min(rect.width, rect.height) / 30, 7), 12)
            let topArea = fontSize * 2.4
            let bottomArea = fontSize * 4.6
            let side = min(rect.width - fontSize * 2, rect.height - topArea - bottomArea)
            guard side > 40 else { return }

            let plot = CGRect(x: rect.midX - side / 2,
                              y: rect.minY + topArea + (rect.height - topArea - bottomArea - side) / 2,
                              width: side,
                              height: side)

            // 标题
            context.draw(Text(audio.phase.isStereo ? "声相（李萨如 · 立体声）" : "声相（李萨如 · 单声道输入）")
                .font(.system(size: fontSize * 0.95, weight: .semibold))
                .foregroundStyle(Color.white.opacity(0.55)),
                         at: CGPoint(x: rect.minX + fontSize * 0.9, y: rect.minY + fontSize * 1.1),
                         anchor: .leading)

            drawGraticule(&context, plot: plot, fontSize: fontSize)
            drawPoints(&context, plot: plot, fontSize: fontSize)
            drawReadouts(&context, rect: rect, fontSize: fontSize)
        }
    }

    private func drawBackground(_ ctx: inout GraphicsContext, rect: CGRect) {
        ctx.fill(Path(roundedRect: rect.insetBy(dx: 1, dy: 1), cornerRadius: 6),
                 with: .color(Color(red: 0.05, green: 0.052, blue: 0.06)))
        ctx.stroke(Path(roundedRect: rect.insetBy(dx: 1, dy: 1), cornerRadius: 6),
                   with: .color(.white.opacity(0.12)),
                   lineWidth: 1)
    }

    /// 刻度：竖直中线（单声道）、水平线（反相）、两条 45° 对角线（只有 L / 只有 R）、单位圆
    private func drawGraticule(_ ctx: inout GraphicsContext, plot: CGRect, fontSize: CGFloat) {
        let center = CGPoint(x: plot.midX, y: plot.midY)
        let half = plot.width / 2

        ctx.stroke(Path(plot), with: .color(.white.opacity(0.25)), lineWidth: 1)

        // 单位圆（满刻度）与 1/2 圆
        for fraction in [0.5, 1.0] as [CGFloat] {
            let radius = half * fraction
            ctx.stroke(Path(ellipseIn: CGRect(x: center.x - radius, y: center.y - radius,
                                              width: radius * 2, height: radius * 2)),
                       with: .color(.white.opacity(fraction == 1.0 ? 0.22 : 0.10)),
                       lineWidth: 1)
        }

        var cross = Path()
        cross.move(to: CGPoint(x: center.x, y: plot.minY))
        cross.addLine(to: CGPoint(x: center.x, y: plot.maxY))     // 单声道 / 同相
        cross.move(to: CGPoint(x: plot.minX, y: center.y))
        cross.addLine(to: CGPoint(x: plot.maxX, y: center.y))     // 反相
        ctx.stroke(cross, with: .color(.white.opacity(0.28)), lineWidth: 1)

        var diagonals = Path()
        diagonals.move(to: CGPoint(x: plot.minX, y: plot.maxY))
        diagonals.addLine(to: CGPoint(x: plot.maxX, y: plot.minY))
        diagonals.move(to: CGPoint(x: plot.minX, y: plot.minY))
        diagonals.addLine(to: CGPoint(x: plot.maxX, y: plot.maxY))
        ctx.stroke(diagonals, with: .color(.white.opacity(0.12)), lineWidth: 1)

        // 标注
        let small = fontSize * 0.85
        ctx.draw(Text("单声道 / 同相")
            .font(.system(size: small, weight: .semibold))
            .foregroundStyle(Color.white.opacity(0.5)),
                 at: CGPoint(x: center.x, y: plot.minY - small * 0.75),
                 anchor: .center)
        ctx.draw(Text("L")
            .font(.system(size: small, weight: .bold))
            .foregroundStyle(Color.white.opacity(0.5)),
                 at: CGPoint(x: plot.minX + small * 0.9, y: plot.minY + small * 0.9),
                 anchor: .center)
        ctx.draw(Text("R")
            .font(.system(size: small, weight: .bold))
            .foregroundStyle(Color.white.opacity(0.5)),
                 at: CGPoint(x: plot.maxX - small * 0.9, y: plot.minY + small * 0.9),
                 anchor: .center)
        ctx.draw(Text("反相")
            .font(.system(size: small, weight: .semibold))
            .foregroundStyle(Color.white.opacity(0.4)),
                 at: CGPoint(x: plot.maxX - small * 1.6, y: center.y - small * 0.8),
                 anchor: .trailing)
    }

    private func drawPoints(_ ctx: inout GraphicsContext, plot: CGRect, fontSize: CGFloat) {
        let points = audio.phase.points
        guard !points.isEmpty else { return }

        let dot = max(plot.width / 220, 1.4)
        var layer = ctx
        layer.blendMode = .plusLighter

        for point in points {
            let x = plot.minX + CGFloat(point.x) / 255 * plot.width
            // 图像坐标 y 向上，屏幕向下 → 翻转
            let y = plot.maxY - CGFloat(point.y) / 255 * plot.height
            let alpha = 0.25 + 0.75 * Double(point.intensity)
            let rect = CGRect(x: x - dot / 2, y: y - dot / 2, width: dot, height: dot)
            layer.fill(Path(ellipseIn: rect),
                       with: .color(Color(red: 0.45, green: 1.0, blue: 0.65).opacity(alpha)))
        }
        ctx.draw(layer, in: plot)
    }

    /// 底部读数：相关度（数字 + 从 −1 到 +1 的条形）与 L/R 平衡
    private func drawReadouts(_ ctx: inout GraphicsContext, rect: CGRect, fontSize: CGFloat) {
        let snapshot = audio.phase
        let y = rect.maxY - fontSize * 2.6

        // 相关度数字
        let correlationText = String(format: "%+.2f", snapshot.correlation)
        ctx.draw(Text("相关度 \(correlationText)")
            .font(.system(size: fontSize * 1.05, weight: .semibold, design: .monospaced))
            .foregroundStyle(Color.white.opacity(0.85)),
                 at: CGPoint(x: rect.minX + fontSize * 0.9, y: rect.minY + fontSize * 3.2),
                 anchor: .leading)

        // 相关度条：−1 … +1
        let bar = CGRect(x: rect.minX + fontSize * 0.9,
                         y: y,
                         width: max(rect.width - fontSize * 8.5, 30),
                         height: fontSize * 0.85)
        ctx.fill(Path(roundedRect: bar, cornerRadius: bar.height / 2),
                 with: .color(.white.opacity(0.10)))

        let zeroX = bar.midX
        let valueX = bar.minX + bar.width * CGFloat((Double(snapshot.correlation) + 1) / 2)

        // 从 0 到当前值的填充（偏左红、偏右绿）
        let fill = CGRect(x: min(zeroX, valueX), y: bar.minY,
                          width: abs(valueX - zeroX), height: bar.height)
        ctx.fill(Path(roundedRect: fill, cornerRadius: bar.height / 2),
                 with: .color(snapshot.correlation >= 0 ? .green.opacity(0.75) : .red.opacity(0.75)))

        var zeroLine = Path()
        zeroLine.move(to: CGPoint(x: zeroX, y: bar.minY - 2))
        zeroLine.addLine(to: CGPoint(x: zeroX, y: bar.maxY + 2))
        ctx.stroke(zeroLine, with: .color(.white.opacity(0.4)), lineWidth: 1)

        for (label, value) in [("-1", -1.0), ("0", 0.0), ("+1", 1.0)] {
            ctx.draw(Text(label)
                .font(.system(size: fontSize * 0.8, design: .monospaced))
                .foregroundStyle(Color.white.opacity(0.45)),
                     at: CGPoint(x: bar.minX + bar.width * CGFloat((value + 1) / 2), y: bar.maxY + fontSize * 0.7),
                     anchor: .center)
        }

        // 平衡
        let balance = snapshot.balanceDB
        let balanceText: String
        if abs(balance) < 0.5 {
            balanceText = "平衡 居中"
        } else {
            balanceText = String(format: "平衡 %.1f dB %@", abs(balance), balance > 0 ? "偏左" : "偏右")
        }
        ctx.draw(Text(balanceText)
            .font(.system(size: fontSize * 0.95, design: .monospaced))
            .foregroundStyle(Color.white.opacity(0.6)),
                 at: CGPoint(x: rect.maxX - fontSize * 0.9, y: rect.minY + fontSize * 3.2),
                 anchor: .trailing)

        if !audio.isRunning {
            ctx.draw(Text("音频未启用：设置 → 音频")
                .font(.system(size: fontSize, weight: .semibold))
                .foregroundStyle(Color.orange),
                     at: CGPoint(x: rect.midX, y: rect.maxY - fontSize * 0.9),
                     anchor: .center)
        }
    }
}

/// 把「声相」画到对应格子里
struct AudioPhasePaneOverlay: View {

    let layout: ScopeLayoutResult
    @ObservedObject var audio: AudioMonitor
    let containerSize: CGSize

    var body: some View {
        ZStack {
            ForEach(layout.panes, id: \.slot) { pane in
                if pane.content == .audioPhase {
                    let rect = (pane.plot ?? pane.panel).scaled(to: containerSize)
                    AudioPhasePaneView(audio: audio)
                        .frame(width: rect.width, height: rect.height)
                        .position(x: rect.midX, y: rect.midY)
                }
            }
        }
        .frame(width: containerSize.width, height: containerSize.height, alignment: .topLeading)
        .allowsHitTesting(false)
    }
}
