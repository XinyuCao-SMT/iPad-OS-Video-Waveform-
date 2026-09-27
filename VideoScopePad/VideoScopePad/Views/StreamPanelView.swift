//
//  StreamPanelView.swift
//  VideoScopePad
//
//  录制 / 推流（RTMP · SRT）/ 读数导出 / 抓帧 的操作面板。
//

import SwiftUI

struct StreamPanelView: View {

    @ObservedObject var stream: StreamController
    @ObservedObject var capture: CaptureController
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        NavigationStack {
            Form {
                recordSection
                streamSection
                logSection
                grabSection
                notesSection
            }
            .navigationTitle("录制 / 推流")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .confirmationAction) {
                    Button("完成") { dismiss() }
                }
            }
        }
    }

    // MARK: - 本机录制

    private var recordSection: some View {
        Section("本机录制（H.264 / MP4）") {
            HStack {
                Button {
                    if stream.isRecording {
                        stream.stopRecording()
                    } else {
                        stream.startRecording()
                    }
                } label: {
                    Label(stream.isRecording ? "停止录制" : "开始录制",
                          systemImage: stream.isRecording ? "stop.circle.fill" : "record.circle")
                }
                .buttonStyle(.borderedProminent)
                .tint(stream.isRecording ? .red : .accentColor)

                Spacer()

                if stream.isRecording {
                    Text("● 录制中").foregroundStyle(.red).font(.footnote)
                }
            }

            LabeledContent("状态", value: stream.statusText)
            if !stream.detailText.isEmpty {
                LabeledContent("编码", value: stream.detailText)
            }

            if let url = stream.lastRecordingURL {
                ShareLink(item: url) {
                    Label("导出最近一次录制：" + url.lastPathComponent, systemImage: "square.and.arrow.up")
                }
            }

            Text("文件存在「文件 → 本应用 → Recordings」。录制的是**输入信号**（采集卡原始画面）—— 这和广播监视器的做法一致：LUT 只是监看视图，素材应当留原始信号。")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }

    // MARK: - 推流（RTMP / SRT）

    private var streamSection: some View {
        Section("推流（视频）") {
            Picker("协议", selection: $stream.transportKind) {
                ForEach(StreamTransport.Kind.allCases) { kind in
                    Text(kind.title).tag(kind)
                }
            }
            .pickerStyle(.segmented)

            TextField(stream.transportKind.placeholder, text: Binding(
                get: { stream.activeURL },
                set: { stream.activeURL = $0 }
            ))
            .textInputAutocapitalization(.never)
            .autocorrectionDisabled()
            .keyboardType(.URL)

            if stream.transportKind.needsStreamKey {
                TextField("流密钥 / Stream Key", text: $stream.streamKey)
                    .textInputAutocapitalization(.never)
                    .autocorrectionDisabled()
            }

            LabeledSlider(title: "码率（Mb/s）", value: $stream.bitrateMbps, range: 1...40, format: "%.1f")
            LabeledSlider(title: "关键帧间隔（秒）", value: $stream.keyframeSeconds, range: 0.5...6, format: "%.1f")

            HStack {
                Button {
                    if stream.isStreaming || stream.isPreparing {
                        stream.stopStreaming()
                    } else {
                        stream.startStreaming()
                    }
                } label: {
                    Label(stream.isStreaming || stream.isPreparing ? "停止推流" : "开始推流",
                          systemImage: stream.isStreaming ? "stop.circle.fill" : "paperplane.fill")
                }
                .buttonStyle(.borderedProminent)
                .tint(stream.isStreaming ? .red : .accentColor)

                Spacer()

                Text(stream.statusText).font(.footnote).foregroundStyle(.secondary)
            }

            if let error = stream.lastError {
                Text(error).font(.footnote).foregroundStyle(.red)
            }

            Text("""
            \(stream.transportKind.summary)
            H.264 由 VideoToolbox 编码，编码结果同时给录制与推流用（HaishinKit 收到已压缩帧就直接封装，不会二次编码）。
            音频暂不支持：采集卡的 HDMI 内嵌音频不走视频设备的采集通道。
            """)
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }

    // MARK: - 读数导出

    private var logSection: some View {
        Section("读数记录（CSV）") {
            Toggle("记录读数", isOn: $stream.logEnabled)

            LabeledContent("已记录行数", value: "\(stream.logLineCount)")
            LabeledContent("采样间隔", value: String(format: "%.1f 秒/行", stream.log.interval))

            Button {
                stream.exportLog()
            } label: {
                Label("导出 CSV 到「文件」", systemImage: "square.and.arrow.down")
            }

            if let url = stream.lastLogURL {
                ShareLink(item: url) {
                    Label("分享最近导出的 CSV", systemImage: "square.and.arrow.up")
                }
            }

            Button(role: .destructive) {
                stream.clearLog()
            } label: {
                Label("清空已记录数据", systemImage: "trash")
            }

            Text("列包含：时间、峰值白/黑位/平均/动态范围（IRE 与等效 mV 同时给）、R/G/B 峰值、色度峰值、超白超黑占比、报警状态。")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }

    // MARK: - 抓帧

    private var grabSection: some View {
        Section("抓帧") {
            Button {
                stream.requestGrabFrame()
            } label: {
                Label("抓当前帧存进相册", systemImage: "camera.viewfinder")
            }

            if let message = stream.grabMessage {
                Text(message).font(.footnote).foregroundStyle(.secondary)
            }

            Text("抓的是输入信号的一帧。带 LUT 与示波器的合成截图见 NEXT-STEPS.md（那需要在 Metal 合成结果上做回读）。")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }

    // MARK: - 说明

    private var notesSection: some View {
        Section("当前输入") {
            LabeledContent("信号", value: capture.signal.resolutionText + " · " + capture.signal.frameRateText + "p")
            LabeledContent("像素格式", value: capture.signal.pixelFormat)
            LabeledContent("实测帧率", value: String(format: "%.1f fps", capture.stats.fps))
        }
    }
}
