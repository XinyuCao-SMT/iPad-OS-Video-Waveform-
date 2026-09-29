//
//  StreamStatsView.swift
//  VideoScopePad
//
//  「推流状态」格子的内容：近 5 分钟的带宽 / 编码码率 / 延迟折线图。
//
//  为什么放在格子里而不是弹窗：
//    调信号的时候希望一眼同时看到画面、示波器和链路状况，所以它跟其他示波器一样是可选的格子内容
//    （全屏或四分割里都能选）。
//
//  曲线说明：
//    · 编码码率（黄）—— 我们 VideoToolbox 编码器每秒输出的码率，任何协议都有
//    · SRT 估计带宽（青）—— libsrt 根据 ACK/NAK 估算的可用带宽，是链路能承受的上限
//    · SRT 发送速率（绿）—— 实际发出的速率
//    · SRT 往返时延 RTT（品红）—— 走右轴（毫秒）
//    RTMP 没有链路反馈，所以只有编码码率一条曲线，并在图内注明。
//

import SwiftUI

struct StreamStatsView: View {

    @ObservedObject var metrics: StreamMetrics

    /// 采样点总时长（秒）：300 个点 × 1 秒
    private let window: Double = 300

    var body: some View {
        Canvas { context, size in
            let rect = CGRect(origin: .zero, size: size)
            guard rect.width > 40, rect.height > 40 else { return }

            drawBackground(&context, rect: rect)

            let fontSize = min(max(min(rect.width, rect.height) / 34, 8), 12)
            let legendHeight = fontSize * 2.2
            let plot = CGRect(x: rect.minX + fontSize * 4.0,
                              y: rect.minY + legendHeight + fontSize * 1.6,
                              width: max(rect.width - fontSize * 4.0 - fontSize * 4.6, 1),
                              height: max(rect.height - legendHeight - fontSize * 3.2, 1))

            drawLegend(&context, rect: rect, fontSize: fontSize, plot: plot)
            drawGrid(&context, plot: plot, fontSize: fontSize)
            drawSeries(&context, plot: plot, fontSize: fontSize)

            if metrics.samples.isEmpty {
                let message = metrics.isPublishing ? "正在采集…（每秒一个点）" : "未推流"
                context.draw(Text(message)
                    .font(.system(size: fontSize * 1.4, weight: .semibold))
                    .foregroundStyle(Color.white.opacity(0.55)),
                             at: CGPoint(x: plot.midX, y: plot.midY),
                             anchor: .center)
            }
        }
    }

    // MARK: - 背景

    private func drawBackground(_ ctx: inout GraphicsContext, rect: CGRect) {
        ctx.fill(Path(roundedRect: rect.insetBy(dx: 1, dy: 1), cornerRadius: 6),
                 with: .color(Color(red: 0.05, green: 0.052, blue: 0.06)))
        ctx.stroke(Path(roundedRect: rect.insetBy(dx: 1, dy: 1), cornerRadius: 6),
                   with: .color(.white.opacity(0.12)),
                   lineWidth: 1)
    }

    // MARK: - 图例与当前值

    private func drawLegend(_ ctx: inout GraphicsContext,
                            rect: CGRect,
                            fontSize: CGFloat,
                            plot: CGRect) {
        var x = rect.minX + fontSize * 0.9
        let y = rect.minY + fontSize * 1.2

        func entry(_ title: String, _ color: Color, _ value: String) {
            let swatch = CGRect(x: x, y: y - fontSize * 0.42, width: fontSize * 0.9, height: fontSize * 0.22)
            ctx.fill(Path(swatch), with: .color(color))
            let text = Text("\(title) \(value)")
                .font(.system(size: fontSize, weight: .semibold, design: .monospaced))
                .foregroundStyle(Color.white.opacity(0.92))
            ctx.draw(text, at: CGPoint(x: x + fontSize * 1.2, y: y), anchor: .leading)
            x += fontSize * (1.2 + CGFloat(title.count) * 0.72 + CGFloat(value.count) * 0.72 + 1.2)
        }

        let latest = metrics.latest
        entry("编码", .yellow, String(format: "%.1f Mb/s", latest?.encodedMbps ?? 0))
        if let bandwidth = latest?.bandwidthMbps {
            entry("带宽", .cyan, String(format: "%.1f Mb/s", bandwidth))
        }
        if let send = latest?.sendMbps {
            entry("发送", .green, String(format: "%.1f Mb/s", send))
        }
        if let rtt = metrics.recentAverageRTTMs {
            entry("RTT", .pink, String(format: "%.0f ms", rtt))
        }

        // 第二行：说明（RTMP 无链路数据时会写清楚）
        let note = metrics.samples.isEmpty ? "" : metrics.note
        if !note.isEmpty {
            ctx.draw(Text(note)
                .font(.system(size: fontSize * 0.92))
                .foregroundStyle(Color.white.opacity(0.5)),
                     at: CGPoint(x: rect.minX + fontSize * 0.9, y: rect.minY + fontSize * 2.6),
                     anchor: .leading)
        }

        // 右下角：时间窗口标识
        ctx.draw(Text("近 5 分钟")
            .font(.system(size: fontSize, weight: .semibold))
            .foregroundStyle(Color.white.opacity(0.45)),
                 at: CGPoint(x: rect.maxX - fontSize * 0.9, y: rect.minY + fontSize * 1.2),
                 anchor: .trailing)
    }

    // MARK: - 网格与坐标

    private func drawGrid(_ ctx: inout GraphicsContext, plot: CGRect, fontSize: CGFloat) {
        // 竖向：每分钟一条
        for minute in 0...5 {
            let x = plot.minX + plot.width * CGFloat(1 - Double(minute) / 5.0)
            var line = Path()
            line.move(to: CGPoint(x: x, y: plot.minY))
            line.addLine(to: CGPoint(x: x, y: plot.maxY))
            ctx.stroke(line, with: .color(.white.opacity(minute == 5 ? 0.22 : 0.10)), lineWidth: 1)

            let label = minute == 0 ? "现在" : "-\(minute)分"
            ctx.draw(Text(label)
                .font(.system(size: fontSize * 0.85))
                .foregroundStyle(Color.white.opacity(0.45)),
                     at: CGPoint(x: x, y: plot.maxY + fontSize * 0.9),
                     anchor: .center)
        }

        // 横向：4 等分
        for index in 0...4 {
            let y = plot.minY + plot.height * CGFloat(index) / 4
            var line = Path()
            line.move(to: CGPoint(x: plot.minX, y: y))
            line.addLine(to: CGPoint(x: plot.maxX, y: y))
            ctx.stroke(line, with: .color(.white.opacity(index == 4 ? 0.22 : 0.08)), lineWidth: 1)

            // 左轴：Mb/s
            let mbps = speedCeiling * Double(4 - index) / 4
            ctx.draw(Text(String(format: "%.0f", mbps))
                .font(.system(size: fontSize * 0.85, design: .monospaced))
                .foregroundStyle(Color.white.opacity(0.5)),
                     at: CGPoint(x: plot.minX - fontSize * 0.6, y: y),
                     anchor: .trailing)

            // 右轴：毫秒（只有在有 RTT 数据时才标）
            if metrics.hasLinkData {
                let ms = rttCeiling * Double(4 - index) / 4
                ctx.draw(Text(String(format: "%.0f", ms))
                    .font(.system(size: fontSize * 0.85, design: .monospaced))
                    .foregroundStyle(Color.pink.opacity(0.55)),
                         at: CGPoint(x: plot.maxX + fontSize * 0.6, y: y),
                         anchor: .leading)
            }
        }

        ctx.draw(Text("Mb/s")
            .font(.system(size: fontSize * 0.85, weight: .semibold))
            .foregroundStyle(Color.white.opacity(0.5)),
                 at: CGPoint(x: plot.minX - fontSize * 0.6, y: plot.minY - fontSize * 0.8),
                 anchor: .trailing)
        if metrics.hasLinkData {
            ctx.draw(Text("ms")
                .font(.system(size: fontSize * 0.85, weight: .semibold))
                .foregroundStyle(Color.pink.opacity(0.6)),
                     at: CGPoint(x: plot.maxX + fontSize * 0.6, y: plot.minY - fontSize * 0.8),
                     anchor: .leading)
        }
    }

    // MARK: - 曲线

    private func drawSeries(_ ctx: inout GraphicsContext, plot: CGRect, fontSize: CGFloat) {
        let count = metrics.samples.count
        guard count > 1 else { return }

        /// 第 index 个采样点的横向位置：最新点贴右边，最老点贴左边（不足 5 分钟时只占右侧一部分）
        func x(_ index: Int) -> CGFloat {
            let offset = Double(metrics.samples.count - 1 - index)
            return plot.maxX - CGFloat(offset / window) * plot.width
        }

        func line(_ values: [Double?], color: Color, ceiling: Double) {
            var path = Path()
            var started = false
            for (index, value) in values.enumerated() {
                guard let value else { continue }
                let y = plot.maxY - CGFloat(min(max(value, 0), ceiling) / ceiling) * plot.height
                let point = CGPoint(x: x(index), y: y)
                if started {
                    path.addLine(to: point)
                } else {
                    path.move(to: point)
                    started = true
                }
            }
            guard started else { return }
            ctx.stroke(path, with: .color(color), lineWidth: 1.6)
        }

        line(metrics.samples.map { $0.encodedMbps as Double? }, color: .yellow, ceiling: speedCeiling)
        if metrics.hasLinkData {
            line(metrics.samples.map(\.bandwidthMbps), color: .cyan, ceiling: speedCeiling)
            line(metrics.samples.map(\.sendMbps), color: .green, ceiling: speedCeiling)
            line(metrics.samples.map(\.rttMs), color: .pink, ceiling: rttCeiling)
        }
    }

    // MARK: - 坐标上限

    /// 码率/带宽的纵轴上限：取峰值上浮 20%，至少 2 Mb/s，并按 1/2/5 取整好读
    private var speedCeiling: Double {
        let peak = max(metrics.peakEncodedMbps, metrics.peakBandwidthMbps, metrics.peakSendMbps)
        return niceCeiling(peak * 1.2, minimum: 2)
    }

    /// 延迟纵轴上限（毫秒）
    private var rttCeiling: Double {
        let peak = metrics.peakRTTMs
        return niceCeiling(peak * 1.2, minimum: 20)
    }

    private func niceCeiling(_ value: Double, minimum: Double) -> Double {
        let target = max(value, minimum)
        let steps: [Double] = [1, 2, 5, 10, 20, 50, 100, 200, 500, 1000, 2000]
        for step in steps where target <= step { return step }
        return 5000
    }
}

/// 把「推流状态」画到对应格子里（与刻度叠加层一样按布局矩形定位，保证和别的格子对齐）
struct StreamStatsPaneOverlay: View {

    let layout: ScopeLayoutResult
    @ObservedObject var metrics: StreamMetrics
    let containerSize: CGSize

    var body: some View {
        ZStack {
            ForEach(layout.panes, id: \.slot) { pane in
                if pane.content == .streamStats {
                    let rect = (pane.plot ?? pane.panel).scaled(to: containerSize)
                    StreamStatsView(metrics: metrics)
                        .frame(width: rect.width, height: rect.height)
                        .position(x: rect.midX, y: rect.midY)
                }
            }
        }
        .frame(width: containerSize.width, height: containerSize.height, alignment: .topLeading)
        .allowsHitTesting(false)
    }
}
