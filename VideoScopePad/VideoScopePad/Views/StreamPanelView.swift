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

            if stream.transportKind == .rtmp {
                rtmpFields
            } else {
                srtFields
            }

            addressPreviewRow
            importRow

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
                .disabled(!(stream.isStreaming || stream.isPreparing) && stream.addressProblem != nil)

                Spacer()

                Text(stream.statusText).font(.footnote).foregroundStyle(.secondary)
            }

            if let error = stream.lastError {
                Text(error).font(.footnote).foregroundStyle(.red)
            }

            if !stream.diagnostics.isEmpty {
                diagnosticsBox
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

    // MARK: 地址分栏填写（避免手打完整 URL 被符号坑到）

    private var rtmpFields: some View {
        Group {
            TextField("服务器地址（主机名或 IP）", text: $stream.rtmpHost)
                .textInputAutocapitalization(.never)
                .autocorrectionDisabled()
                .keyboardType(.URL)

            HStack {
                TextField("端口（默认 1935）", text: $stream.rtmpPort)
                    .keyboardType(.numberPad)
                TextField("应用（默认 live）", text: $stream.rtmpApp)
                    .textInputAutocapitalization(.never)
                    .autocorrectionDisabled()
            }

            TextField("流密钥 / Stream Key", text: $stream.streamKey)
                .textInputAutocapitalization(.never)
                .autocorrectionDisabled()

            Toggle("使用 RTMPS 加密（端口默认 443）", isOn: $stream.rtmpSecure)
        }
    }

    private var srtFields: some View {
        Group {
            TextField(stream.srtMode == .listener ? "主机名或 IP（监听模式可留空）" : "主机名或 IP",
                      text: $stream.srtHost)
                .textInputAutocapitalization(.never)
                .autocorrectionDisabled()
                .keyboardType(.URL)

            TextField("端口（默认 9710）", text: $stream.srtPort)
                .keyboardType(.numberPad)

            Picker("模式", selection: $stream.srtMode) {
                ForEach(SRTModeOption.allCases) { mode in
                    Text(mode.title).tag(mode)
                }
            }
            .pickerStyle(.segmented)

            Text(stream.srtMode.detail)
                .font(.caption2)
                .foregroundStyle(.secondary)

            TextField("串流标识 streamid（不填就不带这个参数）", text: $stream.srtStreamID)
                .textInputAutocapitalization(.never)
                .autocorrectionDisabled()

            DisclosureGroup("高级（延迟 / 密码 / 连接超时）") {
                TextField("延迟 latency（毫秒，20–8000，留空不设置）", text: $stream.srtLatency)
                    .keyboardType(.numberPad)

                SecureField("密码 passphrase（服务器要求加密时填）", text: $stream.srtPassphrase)

                if !stream.srtPassphrase.isEmpty {
                    Picker("加密位数", selection: $stream.srtKeyLength) {
                        Text("16 位").tag(16)
                        Text("24 位").tag(24)
                        Text("32 位").tag(32)
                    }
                    .pickerStyle(.segmented)
                }

                TextField("连接超时 conntimeo（毫秒，留空用 5000）", text: $stream.srtConnectTimeout)
                    .keyboardType(.numberPad)

                Text("串流标识的写法各服务器不同：SRS 常用 live/xxx，OBS / MediaMTX 常用 publish:live/xxx。值里不要带 & 号。")
                    .font(.caption2)
                    .foregroundStyle(.secondary)
            }
        }
    }

    private var addressPreviewRow: some View {
        VStack(alignment: .leading, spacing: 4) {
            LabeledContent("将连接") {
                Text(stream.addressPreview)
                    .font(.system(.caption, design: .monospaced))
                    .multilineTextAlignment(.trailing)
                    .textSelection(.enabled)
            }
            if let problem = stream.addressProblem {
                Text(problem)
                    .font(.caption2)
                    .foregroundStyle(.orange)
            }
        }
    }

    private var importRow: some View {
        DisclosureGroup("或：粘贴完整地址自动填入上面各栏") {
            TextField(stream.transportKind == .rtmp ? "rtmp://主机:1935/应用/流密钥"
                                                    : "srt://主机:9000?mode=caller&streamid=live/test",
                      text: $stream.importText)
                .textInputAutocapitalization(.never)
                .autocorrectionDisabled()
                .keyboardType(.URL)
                .font(.system(.footnote, design: .monospaced))

            Button {
                stream.importAddress()
            } label: {
                Label("解析并填入", systemImage: "arrow.down.doc")
            }

            if let message = stream.importMessage {
                Text(message)
                    .font(.caption2)
                    .foregroundStyle(.secondary)
                    .textSelection(.enabled)
            }

            Text("全角字符（：／ｓｒｔ）、零宽字符、漏写 srt:// 都会被自动处理，不用手改。")
                .font(.caption2)
                .foregroundStyle(.secondary)
        }
    }

    /// 失败诊断：把「填的各栏 / 实际使用的地址 / 错误分支 / libsrt 版本」都列出来，可一键复制
    private var diagnosticsBox: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                Label("诊断信息", systemImage: "stethoscope")
                    .font(.footnote.weight(.semibold))
                Spacer()
                Button {
                    stream.copyDiagnostics()
                } label: {
                    Label(stream.diagnosticsCopied ? "已复制" : "复制",
                          systemImage: stream.diagnosticsCopied ? "checkmark.circle" : "doc.on.doc")
                        .font(.footnote)
                }
                .buttonStyle(.bordered)
                Button(role: .destructive) {
                    stream.clearDiagnostics()
                } label: {
                    Image(systemName: "xmark.circle")
                }
                .buttonStyle(.bordered)
            }

            Text(stream.diagnostics)
                .font(.system(.caption2, design: .monospaced))
                .textSelection(.enabled)
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(8)
                .background(Color.secondary.opacity(0.12), in: RoundedRectangle(cornerRadius: 8))
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
