//
//  AudioSpectrumView.swift
//  VideoScopePad
//
//  音频频谱（1/3 倍频程 RTA）+ 响度（LUFS）。
//
//  频谱：31 段 1/3 倍频程（20 Hz – 20 kHz），横轴按对数排布，纵轴 dBFS（−90…0）。
//        每段带峰值保持线；底部标 100 / 1k / 10k 的频率刻度。
//  响度：按 EBU R128 / ITU-R BS.1770 的做法给出
//        · M（Momentary，400 ms）
//        · S（Short-term，3 s）
//        · 参考：RMS 与采样峰值 dBFS
//        并用一条 −40…0 LUFS 的条形标出当前位置与 −23 / −24 LUFS 目标刻度。
//
//  数据来自 SpectrumAnalyzer（音频线程算好后每 100 ms 推一份快照），这里只画。
//

import SwiftUI

struct AudioSpectrumView: View {

    @ObservedObject var audio: AudioMonitor

    var body: some View {
        Canvas { context, size in
            let rect = CGRect(origin: .zero, size: size)
            guard rect.width > 90, rect.height > 80 else { return }

            drawBackground(&context, rect: rect)

            let snapshot = audio.spectrum
            let fontSize = min(max(min(rect.width, rect.height) / 26, 7), 12)
            let header = fontSize * 2.0
            let footer = fontSize * 3.4

            let plot = CGRect(x: rect.minX + fontSize * 3.6,
                              y: rect.minY + header,
                              width: max(rect.width - fontSize * 4.6, 20),
                              height: max(rect.height - header - footer, 20))

            context.draw(Text("音频频谱 / 响度（1/3 倍频程）")
                .font(.system(size: fontSize * 0.95, weight: .semibold))
                .foregroundStyle(Color.white.opacity(0.55)),
                         at: CGPoint(x: rect.minX + fontSize * 0.9, y: rect.minY + fontSize * 1.1),
                         anchor: .leading)

            drawGrid(&context, plot: plot, fontSize: fontSize, snapshot: snapshot)
            drawBands(&context, plot: plot, fontSize: fontSize, snapshot: snapshot)
            drawLoudness(&context, rect: rect, fontSize: fontSize, plot: plot, snapshot: snapshot)
        }
    }

    private func drawBackground(_ ctx: inout GraphicsContext, rect: CGRect) {
        ctx.fill(Path(roundedRect: rect.insetBy(dx: 1, dy: 1), cornerRadius: 6),
                 with: .color(Color(red: 0.05, green: 0.052, blue: 0.06)))
        ctx.stroke(Path(roundedRect: rect.insetBy(dx: 1, dy: 1), cornerRadius: 6),
                   with: .color(.white.opacity(0.12)),
                   lineWidth: 1)
    }

    // MARK: - 坐标

    /// 纵轴：−90…0 dBFS
    private func y(dB: Float, in plot: CGRect) -> CGFloat {
        let normalized = (min(max(dB, -90), 0) + 90) / 90
        return plot.maxY - CGFloat(normalized) * plot.height
    }

    /// 横轴：按对数排在 31 段之间
    private func x(band: Int, count: Int, in plot: CGRect) -> CGFloat {
        guard count > 1 else { return plot.midX }
        return plot.minX + plot.width * CGFloat(band) / CGFloat(count - 1)
    }

    private func drawGrid(_ ctx: inout GraphicsContext,
                          plot: CGRect,
                          fontSize: CGFloat,
                          snapshot: SpectrumAnalyzer.Snapshot) {
        // 横向 dB 线：0 / −12 / −24 / −36 / −48 / −60 / −72
        for dB in stride(from: Float(0), through: -72, by: -12) {
            let lineY = y(dB: dB, in: plot)
            var line = Path()
            line.move(to: CGPoint(x: plot.minX, y: lineY))
            line.addLine(to: CGPoint(x: plot.maxX, y: lineY))
            ctx.stroke(line, with: .color(.white.opacity(dB == 0 ? 0.25 : 0.08)), lineWidth: 1)

            ctx.draw(Text(String(format: "%.0f", dB))
                .font(.system(size: fontSize * 0.8, design: .monospaced))
                .foregroundStyle(Color.white.opacity(0.45)),
                     at: CGPoint(x: plot.minX - fontSize * 0.5, y: lineY),
                     anchor: .trailing)
        }

        // 频率刻度：100 / 1k / 10k
        let centers = snapshot.frequencies.isEmpty ? SpectrumAnalyzer.bandCenters : snapshot.frequencies
        for (index, frequency) in centers.enumerated() {
            guard frequency >= 99, frequency <= 101
                || frequency >= 990, frequency <= 1010
                || frequency >= 9900, frequency <= 10100 else { continue }
            let lineX = x(band: index, count: centers.count, in: plot)
            var line = Path()
            line.move(to: CGPoint(x: lineX, y: plot.minY))
            line.addLine(to: CGPoint(x: lineX, y: plot.maxY))
            ctx.stroke(line, with: .color(.white.opacity(0.12)), lineWidth: 1)

            let label = frequency >= 1000 ? String(format: "%.0fk", frequency / 1000) : String(format: "%.0f", frequency)
            ctx.draw(Text(label)
                .font(.system(size: fontSize * 0.8, design: .monospaced))
                .foregroundStyle(Color.white.opacity(0.45)),
                     at: CGPoint(x: lineX, y: plot.maxY + fontSize * 0.7),
                     anchor: .center)
        }
    }

    private func drawBands(_ ctx: inout GraphicsContext,
                           plot: CGRect,
                           fontSize: CGFloat,
                           snapshot: SpectrumAnalyzer.Snapshot) {
        let count = snapshot.bands.count
        guard count > 1 else { return }

        let slot = plot.width / CGFloat(count - 1)
        let barWidth = max(slot * 0.62, 2)

        for (index, level) in snapshot.bands.enumerated() {
            let centerX = x(band: index, count: count, in: plot)
            let topY = y(dB: level, in: plot)
            guard topY < plot.maxY - 0.5 else { continue }

            let bar = CGRect(x: centerX - barWidth / 2, y: topY,
                             width: barWidth, height: plot.maxY - topY)
            ctx.fill(Path(roundedRect: bar, cornerRadius: barWidth * 0.2),
                     with: .linearGradient(Gradient(colors: [.green, .yellow, .red]),
                                           startPoint: CGPoint(x: 0, y: plot.maxY),
                                           endPoint: CGPoint(x: 0, y: plot.minY)))
        }
    }

    private func drawLoudness(_ ctx: inout GraphicsContext,
                              rect: CGRect,
                              fontSize: CGFloat,
                              plot: CGRect,
                              snapshot: SpectrumAnalyzer.Snapshot) {
        let y0 = rect.maxY - fontSize * 2.9
        let left = rect.minX + fontSize * 0.9

        // 条形：−40 … 0 LUFS
        let bar = CGRect(x: left, y: y0, width: max(plot.width * 0.62, 40), height: fontSize * 0.9)
        ctx.fill(Path(roundedRect: bar, cornerRadius: bar.height / 2),
                 with: .color(.white.opacity(0.10)))

        func xLUFS(_ value: Float) -> CGFloat {
            let normalized = (min(max(value, -40), 0) + 40) / 40
            return bar.minX + bar.width * CGFloat(normalized)
        }

        // 目标刻度：−23（EBU R128）/ −24（ATSC A/85）
        for (target, label) in [(-23.0 as Float, "-23"), (-24.0 as Float, "-24")] {
            var mark = Path()
            mark.move(to: CGPoint(x: xLUFS(target), y: bar.minY - 2))
            mark.addLine(to: CGPoint(x: xLUFS(target), y: bar.maxY + 2))
            ctx.stroke(mark, with: .color(.cyan.opacity(0.7)), lineWidth: 1)
            ctx.draw(Text(label)
                .font(.system(size: fontSize * 0.7, design: .monospaced))
                .foregroundStyle(Color.cyan.opacity(0.75)),
                     at: CGPoint(x: xLUFS(target), y: bar.minY - fontSize * 0.5),
                     anchor: .center)
        }

        // 短时响度填充
        let shortTerm = snapshot.shortTermLUFS
        if shortTerm > -40 {
            let fill = CGRect(x: bar.minX, y: bar.minY,
                              width: max(xLUFS(shortTerm) - bar.minX, 1), height: bar.height)
            ctx.fill(Path(roundedRect: fill, cornerRadius: bar.height / 2),
                     with: .color(.green.opacity(0.7)))
        }
        // 瞬时响度游标
        var marker = Path()
        marker.move(to: CGPoint(x: xLUFS(snapshot.momentaryLUFS), y: bar.minY - 1))
        marker.addLine(to: CGPoint(x: xLUFS(snapshot.momentaryLUFS), y: bar.maxY + 1))
        ctx.stroke(marker, with: .color(.white.opacity(0.85)), lineWidth: 1.5)

        ctx.draw(Text("-40")
            .font(.system(size: fontSize * 0.75, design: .monospaced))
            .foregroundStyle(Color.white.opacity(0.4)),
                 at: CGPoint(x: bar.minX, y: bar.maxY + fontSize * 0.7), anchor: .center)
        ctx.draw(Text("0 LUFS")
            .font(.system(size: fontSize * 0.75, design: .monospaced))
            .foregroundStyle(Color.white.opacity(0.4)),
                 at: CGPoint(x: bar.maxX, y: bar.maxY + fontSize * 0.7), anchor: .center)

        // 数字读数
        let readout = String(format: "M %.1f  S %.1f LUFS", snapshot.momentaryLUFS, snapshot.shortTermLUFS)
        let reference = String(format: "RMS %.1f  Peak %.1f dBFS", snapshot.rmsDBFS, snapshot.peakDBFS)
        ctx.draw(Text(readout)
            .font(.system(size: fontSize * 1.0, weight: .semibold, design: .monospaced))
            .foregroundStyle(Color.white.opacity(0.85)),
                 at: CGPoint(x: rect.maxX - fontSize * 0.9, y: y0), anchor: .trailing)
        ctx.draw(Text(reference)
            .font(.system(size: fontSize * 0.85, design: .monospaced))
            .foregroundStyle(Color.white.opacity(0.5)),
                 at: CGPoint(x: rect.maxX - fontSize * 0.9, y: y0 + fontSize * 1.3), anchor: .trailing)

        if !audio.isRunning {
            ctx.draw(Text("音频未启用：设置 → 音频")
                .font(.system(size: fontSize, weight: .semibold))
                .foregroundStyle(Color.orange),
                     at: CGPoint(x: rect.midX, y: rect.minY + rect.height * 0.5),
                     anchor: .center)
        }
    }
}

/// 把「音频频谱 / 响度」画到对应格子里
struct AudioSpectrumPaneOverlay: View {

    let layout: ScopeLayoutResult
    @ObservedObject var audio: AudioMonitor
    let containerSize: CGSize

    var body: some View {
        ZStack {
            ForEach(layout.panes, id: \.slot) { pane in
                if pane.content == .audioSpectrum {
                    let rect = (pane.plot ?? pane.panel).scaled(to: containerSize)
                    AudioSpectrumView(audio: audio)
                        .frame(width: rect.width, height: rect.height)
                        .position(x: rect.midX, y: rect.midY)
                }
            }
        }
        .frame(width: containerSize.width, height: containerSize.height, alignment: .topLeading)
        .allowsHitTesting(false)
    }
}
