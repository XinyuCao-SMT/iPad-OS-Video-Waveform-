//
//  StreamMetrics.swift
//  VideoScopePad
//
//  推流状态的历史数据：每秒采样一次，保留近 5 分钟（300 个点），供「推流状态」格子画折线图。
//
//  三个数据来源：
//    1) 编码码率  —— 我们自己的 VideoToolbox 编码器每秒吐出的字节数（任何协议都有）
//    2) 网络带宽 / 延迟 —— SRT 的 performanceData：mbpsBandwidth（libsrt 估算的可用带宽）、
//       msRTT（往返时延）。这两个是真实测出来的链路指标，不是估的。
//    3) 发送速率 / 丢包 —— 同上的 mbpsSendRate、pktSndLoss / pktRetrans（重传次数反映链路质量）
//
//  注意：RTMP 没有等价的链路指标（协议本身不带 RTT 反馈），所以那种情况下只画编码码率，
//  并在图里注明「延迟/带宽需要 SRT」。
//

import Foundation

struct StreamMetricSample {
    /// 编码输出码率（Mb/s）
    var encodedMbps: Double
    /// libsrt 估算的可用带宽（Mb/s），仅 SRT
    var bandwidthMbps: Double?
    /// SRT 实际发送速率（Mb/s）
    var sendMbps: Double?
    /// 往返时延（毫秒），仅 SRT
    var rttMs: Double?
    /// 累计丢包 / 重传（用于判断链路质量）
    var lostPackets: Int?
    var retransmittedPackets: Int?

    static let empty = StreamMetricSample(encodedMbps: 0)
}

final class StreamMetrics: ObservableObject {

    /// 5 分钟 × 每秒 1 个点
    static let capacity = 300

    @Published private(set) var samples: [StreamMetricSample] = []
    /// 当前是否在推流（决定图上显示「未推流」还是曲线）
    @Published private(set) var isPublishing = false
    /// 当前协议名（RTMP / SRT）
    @Published private(set) var protocolName = "—"
    /// 附加说明（例如「延迟与带宽来自 SRT 统计」）
    @Published private(set) var note = ""

    // MARK: - 写入

    func append(_ sample: StreamMetricSample) {
        samples.append(sample)
        if samples.count > Self.capacity {
            samples.removeFirst(samples.count - Self.capacity)
        }
    }

    func setPublishing(_ publishing: Bool, protocolName: String) {
        self.isPublishing = publishing
        self.protocolName = protocolName
    }

    func reset() {
        samples.removeAll()
    }

    // MARK: - 给图表用的统计量

    var latest: StreamMetricSample? { samples.last }

    var peakEncodedMbps: Double {
        max(samples.map(\.encodedMbps).max() ?? 0, 0.5)
    }

    var peakBandwidthMbps: Double {
        max(samples.compactMap(\.bandwidthMbps).max() ?? 0, 0.5)
    }

    var peakSendMbps: Double {
        max(samples.compactMap(\.sendMbps).max() ?? 0, 0.5)
    }

    var peakRTTMs: Double {
        max(samples.compactMap(\.rttMs).max() ?? 0, 1)
    }

    /// 有没有链路层数据（SRT 才有）
    var hasLinkData: Bool {
        samples.contains { $0.rttMs != nil || $0.bandwidthMbps != nil }
    }

    /// 最近一次的平均 RTT（用来在图右上角显示一个稳定些的读数）
    var recentAverageRTTMs: Double? {
        let recent = samples.suffix(10).compactMap(\.rttMs)
        guard !recent.isEmpty else { return nil }
        return recent.reduce(0, +) / Double(recent.count)
    }
}
