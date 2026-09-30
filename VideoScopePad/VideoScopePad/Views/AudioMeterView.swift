//
//  AudioMeterView.swift
//  VideoScopePad
//
//  画面左右两侧的音柱（L / R）+ 「声画延时」格子的内容。
//
//  音柱位置：贴在**画面格子**的左右边缘（用户要求「画面左右侧」），
//  随画面矩形走，所以全屏、四分割、底部条都能正确落位，也不会越出自己的格子。
//

import SwiftUI

// MARK: - 音柱

struct AudioMeterOverlay: View {

    let layout: ScopeLayoutResult
    @ObservedObject var audio: AudioMonitor
    let containerSize: CGSize

    var body: some View {
        Canvas { context, size in
            for pane in layout.panes where pane.content == .picture {
                guard let videoUnit = pane.video else { continue }
                let video = videoUnit.scaled(to: containerSize)
                let panel = pane.panel.scaled(to: containerSize)
                guard video.width > 60, video.height > 80 else { continue }

                // 整个格子裁一次，音柱绝不会跑到相邻格子里
                context.drawLayer { layer in
                    layer.clip(to: Path(panel))
                    drawMeters(&layer, video: video, size: size)
                }
            }
        }
        .allowsHitTesting(false)
    }

    private func drawMeters(_ ctx: inout GraphicsContext, video: CGRect, size: CGSize) {
        let barWidth = min(max(video.width * 0.022, 6), 18)
        let inset = max(barWidth * 0.5, 4)
        let top = video.minY + inset
        let bottom = video.maxY - inset
        let height = max(bottom - top, 1)

        let displayMode = audio.channelCount > 1 ? 2 : 1
        let fontSize = min(max(barWidth * 0.85, 7), 11)

        func bar(_ x: CGFloat, level: Float, peak: Float, label: String) {
            let track = CGRect(x: x, y: top, width: barWidth, height: height)

            // 底：半透明黑，保证在亮画面也看得见
            ctx.fill(Path(roundedRect: track, cornerRadius: barWidth * 0.25),
                     with: .color(.black.opacity(0.45)))
            ctx.stroke(Path(roundedRect: track, cornerRadius: barWidth * 0.25),
                       with: .color(.white.opacity(0.25)),
                       lineWidth: 1)

            // 电平（从底往上长）：-60…0 dBFS 映射到 0…1
            let levelHeight = height * CGFloat(min(max(level, 0), 1))
            if levelHeight > 0.5 {
                let fill = CGRect(x: x, y: bottom - levelHeight, width: barWidth, height: levelHeight)
                ctx.fill(Path(roundedRect: fill, cornerRadius: barWidth * 0.25),
                         with: .linearGradient(Gradient(colors: [.green, .yellow, .red]),
                                               startPoint: CGPoint(x: 0, y: bottom),
                                               endPoint: CGPoint(x: 0, y: top)))
            }

            // 峰值保持线
            let peakY = bottom - height * CGFloat(min(max(peak, 0), 1))
            var peakLine = Path()
            peakLine.move(to: CGPoint(x: x, y: peakY))
            peakLine.addLine(to: CGPoint(x: x + barWidth, y: peakY))
            ctx.stroke(peakLine, with: .color(.white.opacity(0.9)), lineWidth: 1.5)

            // 刻度线（够高才画）：-6 / -18 / -30 / -48 dBFS
            if height > 120 {
                for db in [-6.0, -18.0, -30.0, -48.0] {
                    let normalized = CGFloat((db + 60) / 60)
                    let y = bottom - height * normalized
                    var tick = Path()
                    tick.move(to: CGPoint(x: x + barWidth * 0.6, y: y))
                    tick.addLine(to: CGPoint(x: x + barWidth, y: y))
                    ctx.stroke(tick, with: .color(.white.opacity(0.35)), lineWidth: 1)
                }
            }

            // 通道名贴在柱子外侧
            ctx.draw(Text(label)
                .font(.system(size: fontSize, weight: .semibold, design: .monospaced))
                .foregroundStyle(Color.white.opacity(0.85)),
                     at: CGPoint(x: x + barWidth / 2, y: top - fontSize * 0.8),
                     anchor: .center)
        }

        bar(video.minX + inset, level: audio.levelLeft, peak: audio.peakLeft,
            label: displayMode == 2 ? "L" : "A")
        if displayMode == 2 {
            bar(video.maxX - inset - barWidth, level: audio.levelRight, peak: audio.peakRight, label: "R")
        }

        // 削波提示
        if audio.isClipping {
            ctx.draw(Text("CLIP")
                .font(.system(size: fontSize, weight: .heavy))
                .foregroundStyle(Color.red),
                     at: CGPoint(x: video.midX, y: video.minY + fontSize * 1.4),
                     anchor: .center)
        }
    }
}

// MARK: - 声画延时格子

struct AVSyncPaneView: View {

    @ObservedObject var meter: AVSyncMeter
    @ObservedObject var audio: AudioMonitor

    var body: some View {
        Canvas { context, size in
            let rect = CGRect(origin: .zero, size: size)
            guard rect.width > 80, rect.height > 60 else { return }

            drawBackground(&context, rect: rect)

            let fontSize = min(max(min(rect.width, rect.height) / 22, 8), 15)
            let pad = fontSize * 0.9

            // 标题
            context.draw(Text("声画延时（千周声 vs 彩条）")
                .font(.system(size: fontSize * 0.95, weight: .semibold))
                .foregroundStyle(Color.white.opacity(0.55)),
                         at: CGPoint(x: rect.minX + pad, y: rect.minY + fontSize * 1.1),
                         anchor: .leading)

            // 大结论
            let verdict = meter.verdictText
            let color: Color = meter.verdictIsGood ? .green : .orange
            context.draw(Text(verdict)
                .font(.system(size: fontSize * 2.0, weight: .bold, design: .monospaced))
                .foregroundStyle(color),
                         at: CGPoint(x: rect.midX, y: rect.minY + rect.height * 0.30),
                         anchor: .center)

            // 细节
            var lines: [String] = []
            if let median = meter.medianOffsetMs {
                lines.append(String(format: "中位数 %+.1f ms（正 = 画面晚）", median))
            }
            lines.append("测量 \(meter.samples.count) 次 · 极差 \(String(format: "%.1f", meter.spreadMs)) ms")
            lines.append(meter.quantizationText)
            if let last = meter.samples.last {
                let formatter = DateFormatter()
                formatter.dateFormat = "HH:mm:ss"
                lines.append("最近一次 \(formatter.string(from: last.measuredAt))")
            }
            lines.append(audio.isRunning
                         ? "音频输入：\(audio.inputName) · 1 kHz \(String(format: "%.0f", audio.toneLevelDB)) dBFS"
                         : "音频未启用：设置 → 音频")

            for (index, line) in lines.enumerated() {
                context.draw(Text(line)
                    .font(.system(size: fontSize * 0.92, design: .monospaced))
                    .foregroundStyle(Color.white.opacity(0.6)),
                             at: CGPoint(x: rect.minX + pad,
                                         y: rect.minY + rect.height * 0.30 + fontSize * (1.4 + CGFloat(index) * 1.25)),
                             anchor: .leading)
            }

            drawTimeline(&context, rect: rect, fontSize: fontSize)
        }
    }

    private func drawBackground(_ ctx: inout GraphicsContext, rect: CGRect) {
        ctx.fill(Path(roundedRect: rect.insetBy(dx: 1, dy: 1), cornerRadius: 6),
                 with: .color(Color(red: 0.05, green: 0.052, blue: 0.06)))
        ctx.stroke(Path(roundedRect: rect.insetBy(dx: 1, dy: 1), cornerRadius: 6),
                   with: .color(.white.opacity(0.12)),
                   lineWidth: 1)
    }

    /// ±200 ms 的时间轴：中心 0，左=画面快，右=声音快；阴影带表示多次测量的极差
    private func drawTimeline(_ ctx: inout GraphicsContext, rect: CGRect, fontSize: CGFloat) {
        let range: Double = 200
        let axisHeight = fontSize * 2.2
        let axis = CGRect(x: rect.minX + fontSize * 3.4,
                          y: rect.maxY - axisHeight - fontSize * 0.6,
                          width: max(rect.width - fontSize * 13.0, 20),
                          height: axisHeight)

        func x(_ ms: Double) -> CGFloat {
            axis.minX + axis.width * CGFloat((min(max(ms, -range), range) + range) / (range * 2))
        }

        // 轨道
        ctx.fill(Path(roundedRect: axis, cornerRadius: 3), with: .color(.white.opacity(0.06)))

        // ±50 / ±100 / ±150 刻度
        for value in [-150.0, -100.0, -50.0, 50.0, 100.0, 150.0] {
            var tick = Path()
            tick.move(to: CGPoint(x: x(value), y: axis.minY))
            tick.addLine(to: CGPoint(x: x(value), y: axis.maxY))
            ctx.stroke(tick, with: .color(.white.opacity(0.10)), lineWidth: 1)
        }

        // 同步区间（±阈值）
        let threshold = meter.syncThresholdMs
        let band = CGRect(x: x(-threshold), y: axis.minY,
                          width: x(threshold) - x(-threshold), height: axis.height)
        ctx.fill(Path(band), with: .color(.green.opacity(0.18)))

        // 中位数位置 + 极差带
        if let median = meter.medianOffsetMs {
            let spread = meter.spreadMs / 2
            let spreadRect = CGRect(x: x(median - spread), y: axis.minY,
                                    width: max(x(median + spread) - x(median - spread), 1.5),
                                    height: axis.height)
            ctx.fill(Path(spreadRect), with: .color(.orange.opacity(0.25)))

            var marker = Path()
            marker.move(to: CGPoint(x: x(median), y: axis.minY - fontSize * 0.35))
            marker.addLine(to: CGPoint(x: x(median), y: axis.maxY + fontSize * 0.35))
            ctx.stroke(marker, with: .color(.orange), lineWidth: 2)

            ctx.draw(Text(String(format: "%+.0f ms", median))
                .font(.system(size: fontSize * 0.9, weight: .semibold, design: .monospaced))
                .foregroundStyle(Color.orange),
                     at: CGPoint(x: x(median), y: axis.minY - fontSize * 0.9),
                     anchor: .center)
        }

        // 0 刻度与两侧标签
        var zero = Path()
        zero.move(to: CGPoint(x: x(0), y: axis.minY - fontSize * 0.2))
        zero.addLine(to: CGPoint(x: x(0), y: axis.maxY + fontSize * 0.2))
        ctx.stroke(zero, with: .color(.white.opacity(0.5)), lineWidth: 1)

        ctx.draw(Text("画面快")
            .font(.system(size: fontSize * 0.9, weight: .semibold))
            .foregroundStyle(Color.white.opacity(0.6)),
                 at: CGPoint(x: axis.minX - fontSize * 0.5, y: axis.midY),
                 anchor: .trailing)
        ctx.draw(Text("声音快")
            .font(.system(size: fontSize * 0.9, weight: .semibold))
            .foregroundStyle(Color.white.opacity(0.6)),
                 at: CGPoint(x: axis.maxX + fontSize * 0.5, y: axis.midY),
                 anchor: .leading)
        ctx.draw(Text("0")
            .font(.system(size: fontSize * 0.85, design: .monospaced))
            .foregroundStyle(Color.white.opacity(0.5)),
                 at: CGPoint(x: x(0), y: axis.midY),
                 anchor: .center)
    }
}

/// 把「声画延时」画到对应格子里（与推流状态格子同样是按布局矩形定位）
struct AVSyncPaneOverlay: View {

    let layout: ScopeLayoutResult
    @ObservedObject var meter: AVSyncMeter
    @ObservedObject var audio: AudioMonitor
    let containerSize: CGSize

    var body: some View {
        ZStack {
            ForEach(layout.panes, id: \.slot) { pane in
                if pane.content == .avSync {
                    let rect = (pane.plot ?? pane.panel).scaled(to: containerSize)
                    AVSyncPaneView(meter: meter, audio: audio)
                        .frame(width: rect.width, height: rect.height)
                        .position(x: rect.midX, y: rect.midY)
                }
            }
        }
        .frame(width: containerSize.width, height: containerSize.height, alignment: .topLeading)
        .allowsHitTesting(false)
    }
}
