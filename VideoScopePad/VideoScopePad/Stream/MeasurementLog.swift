//
//  MeasurementLog.swift
//  VideoScopePad
//
//  把幅度读数记成 CSV，方便导出后做记录或发人。
//
//  记录内容：时间戳 + 峰值白 / 黑位 / 平均 / 动态范围（同时给 IRE 与等效 mV）
//          + R/G/B 峰值 + 色度峰值 + 超白超黑占比 + 报警状态
//  采样节奏：默认每 0.5 秒一行（读数是 10Hz 更新的，按时间抽样写盘，避免文件过大）
//

import Foundation

final class MeasurementLog {

    struct Row {
        var date: Date
        var measurement: SignalMeasurement
        var activeWarnings: [String]
    }

    private(set) var rows: [Row] = []
    private var lastAppend: Date?
    private let maximumRows = 20000

    var isEnabled = false
    /// 每行最小间隔（秒）
    var interval: Double = 0.5

    func reset() {
        rows.removeAll()
        lastAppend = nil
    }

    func append(_ measurement: SignalMeasurement, activeWarnings: [String]) {
        guard isEnabled else { return }
        let now = Date()
        if let last = lastAppend, now.timeIntervalSince(last) < interval { return }
        lastAppend = now

        rows.append(Row(date: now, measurement: measurement, activeWarnings: activeWarnings))
        if rows.count > maximumRows {
            rows.removeFirst(rows.count - maximumRows)
        }
    }

    var lineCount: Int { rows.count }

    /// CSV 文本（带 UTF-8 BOM，Excel 打开中文表头不乱码）
    func csvText() -> String {
        var text = "\u{FEFF}"
        text += "时间,峰值白(IRE),峰值白(mV),黑位(IRE),黑位(mV),平均(IRE),动态范围(IRE),"
        text += "R峰值(IRE),G峰值(IRE),B峰值(IRE),色度峰值(%),超白(%),超黑(%),采样像素,报警\n"

        let formatter = ISO8601DateFormatter()
        for row in rows {
            let m = row.measurement
            func ire(_ value: Double) -> String { String(format: "%.2f", value) }
            func mv(_ value: Double) -> String {
                String(format: "%.0f", value / 100 * ScaleUnit.millivoltPerHundredIRE)
            }
            let warnings = row.activeWarnings.isEmpty ? "" : row.activeWarnings.joined(separator: " / ")
            text += [
                formatter.string(from: row.date),
                ire(m.stableWhiteIRE), mv(m.stableWhiteIRE),
                ire(m.stableBlackIRE), mv(m.stableBlackIRE),
                ire(m.averageIRE), ire(m.dynamicRangeIRE),
                ire(m.redPeakIRE), ire(m.greenPeakIRE), ire(m.bluePeakIRE),
                String(format: "%.0f", m.peakSaturationPercent),
                String(format: "%.3f", m.aboveWhitePercent),
                String(format: "%.3f", m.belowBlackPercent),
                String(m.sampledPixels),
                warnings.contains(",") ? "\"" + warnings + "\"" : warnings
            ].joined(separator: ",") + "\n"
        }
        return text
    }

    /// 落到 Documents/Logs/ 下，返回文件地址
    func writeToFile() throws -> URL {
        let documents = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0]
        let folder = documents.appendingPathComponent("Logs", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)

        let formatter = DateFormatter()
        formatter.dateFormat = "yyyyMMdd-HHmmss"
        let url = folder.appendingPathComponent("VideoScopePad-读数-\(formatter.string(from: Date())).csv")
        try csvText().write(to: url, atomically: true, encoding: .utf8)
        return url
    }
}
