//
//  ContentView.swift
//  VideoScopePad
//

import SwiftUI
import UIKit
import UniformTypeIdentifiers

struct ContentView: View {

    @StateObject private var settings = AppSettings()
    @StateObject private var capture = CaptureController()
    @StateObject private var lutStore = LUTStore()
    @StateObject private var coordinator = RenderCoordinator()

    @Environment(\.scenePhase) private var scenePhase

    @State private var showSettings = false
    @State private var showLUTImporter = false
    @State private var showStreamPanel = false
    @State private var uiHidden = false
    @State private var coordinatorReady = false
    @State private var hasStarted = false

    private var lutContentType: UTType {
        UTType(filenameExtension: "cube") ?? .data
    }

    var body: some View {
        ZStack {
            Color.black.ignoresSafeArea()

            VStack(spacing: 0) {
                if !uiHidden {
                    topBar
                }

                monitorArea

                if !uiHidden {
                    ControlBar(settings: settings,
                               capture: capture,
                               lutStore: lutStore,
                               showSettings: $showSettings,
                               showLUTImporter: $showLUTImporter,
                               onClearAlarms: { coordinator.clearAlarmsAndPeaks() },
                               onSetReference: { coordinator.setReference($0) })
                }
            }
        }
        .preferredColorScheme(.dark)
        .statusBarHidden(uiHidden)
        .fileImporter(isPresented: $showLUTImporter,
                      allowedContentTypes: [lutContentType],
                      allowsMultipleSelection: false,
                      onCompletion: handleImport)
        .sheet(isPresented: $showSettings) {
            SettingsSheet(settings: settings,
                          capture: capture,
                          lutStore: lutStore,
                          audio: coordinator.audio,
                          showLUTImporter: $showLUTImporter)
        }
        .sheet(isPresented: $showStreamPanel) {
            StreamPanelView(stream: coordinator.stream, capture: capture)
        }
        .onAppear(perform: handleAppear)
        .onChange(of: scenePhase) { _, phase in
            handleScenePhase(phase)
        }
        .onChange(of: settings.audioEnabled) { _, newValue in
            if newValue {
                coordinator.startAudio(preferredInputID: settings.audioInputID)
            } else {
                coordinator.stopAudio()
            }
        }
        .onChange(of: settings.audioInputID) { _, newValue in
            coordinator.restartAudio(preferredInputID: newValue.isEmpty ? nil : newValue)
        }
        .onChange(of: settings.avSyncCompensationMs) { _, newValue in
            coordinator.avSync.compensationMs = newValue
        }
        .onChange(of: settings.preventSleep) { _, _ in
            applyIdleTimer()
        }
        .onChange(of: settings.autoFormat) { _, newValue in
            syncAutoFormat(preferred: newValue)
        }
        .onChange(of: capture.autoFormatEnabled) { _, newValue in
            // 用户手动选了格式 → 关掉自动；界面开关跟着同步
            if settings.autoFormat != newValue {
                settings.autoFormat = newValue
            }
        }
        .onReceive(settings.objectWillChange.receive(on: DispatchQueue.main)) { _ in
            coordinator.requestRedraw()
        }
    }

    // MARK: - 顶栏

    private var topBar: some View {
        HStack(spacing: 8) {
            Image(systemName: "tv.inset.filled")
                .font(.system(size: 13, weight: .semibold))
                .foregroundStyle(.white.opacity(0.85))

            Text("VideoScopePad")
                .font(.system(size: 13, weight: .semibold))
                .foregroundStyle(.white.opacity(0.9))

            if capture.hasExternalDevice {
                ChipLabel(title: "UVC 已连接", systemImage: "cable.connector", tint: .green)
            }

            // 信号 / 读数 / 状态信息全部收在这一行里，不再画在画面上
            if settings.showHUD && coordinatorReady {
                // logo 固定在左边，不跟着 chip 滚动
                BrandLogoView(height: 18)

                ScrollView(.horizontal, showsIndicators: false) {
                    TopInfoChips(capture: capture,
                                 settings: settings,
                                 lutStore: lutStore,
                                 measurement: coordinator.measurementHub)
                        .frame(height: 30)
                }
                .frame(height: 30)
                .layoutPriority(-1)
            }

            Spacer(minLength: 4)

            // 录制/推流入口 + 录制指示灯
            if coordinatorReady {
                Button {
                    showStreamPanel = true
                } label: {
                    ChipLabel(title: coordinator.stream.isRecording || coordinator.stream.isStreaming
                              ? "录制/推流中" : "录制/推流",
                              systemImage: coordinator.stream.isRecording ? "record.circle.fill" : "paperplane",
                              isActive: coordinator.stream.isRecording || coordinator.stream.isStreaming,
                              tint: coordinator.stream.isRecording ? .red : .blue)
                }
                .buttonStyle(.plain)
            }

            Button {
                coordinator.setReference(!settings.freeze)
            } label: {
                ChipLabel(title: settings.freeze ? "清除参考" : "冻结参考",
                          systemImage: settings.freeze ? "pin.slash.fill" : "pin.fill",
                          isActive: settings.freeze,
                          tint: .orange)
            }
            .buttonStyle(.plain)

            Button {
                withAnimation(.easeInOut(duration: 0.18)) {
                    uiHidden = true
                }
            } label: {
                ChipLabel(title: "全屏监视", systemImage: "arrow.up.left.and.arrow.down.right")
            }
            .buttonStyle(.plain)
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
        .background(
            Rectangle()
                .fill(.ultraThinMaterial)
                .overlay(Rectangle().fill(Color.white.opacity(0.06)).frame(height: 1), alignment: .bottom)
        )
    }

    // MARK: - 监视区

    private var monitorArea: some View {
        GeometryReader { geo in
            let layout = ScopeLayout.compute(containerSize: geo.size,
                                             videoSize: capture.videoSize,
                                             preset: settings.monitorLayout,
                                             aspectMode: settings.aspectMode,
                                             fullscreenContent: settings.fullscreenContent,
                                             quadContents: settings.normalizedQuadContents,
                                             legacyPanels: settings.enabledPanels,
                                             pictureRotation: settings.pictureRotation)

            ZStack {
                if coordinatorReady {
                    MonitorSurface(coordinator: coordinator)
                } else {
                    Color.black
                    VStack(spacing: 8) {
                        Image(systemName: "exclamationmark.triangle")
                            .font(.system(size: 24))
                        Text(coordinator.startupError ?? "正在初始化 Metal…")
                            .font(.system(size: 13))
                            .multilineTextAlignment(.center)
                    }
                    .foregroundStyle(.white.opacity(0.7))
                    .padding(24)
                }

                if coordinatorReady {
                    ScopeGraticuleView(layout: layout,
                                       settings: settings,
                                       measurement: coordinator.measurementHub,
                                       videoRange: capture.isVideoRange)
                        .allowsHitTesting(false)

                    // 「推流状态」格子：近 5 分钟带宽 / 码率 / 延迟曲线（不走 Metal 示波器管线）
                    StreamStatsPaneOverlay(layout: layout,
                                           metrics: coordinator.stream.metrics,
                                           containerSize: geo.size)
                        .allowsHitTesting(false)

                    // 「声画延时」格子：千周声 vs 彩条的测量结果
                    AVSyncPaneOverlay(layout: layout,
                                      meter: coordinator.avSync,
                                      audio: coordinator.audio,
                                      containerSize: geo.size)
                        .allowsHitTesting(false)

                    // 「音频频谱 / 响度」格子
                    AudioSpectrumPaneOverlay(layout: layout,
                                             audio: coordinator.audio,
                                             containerSize: geo.size)
                        .allowsHitTesting(false)
                    // 「声相（李萨如）」格子
                    AudioPhasePaneOverlay(layout: layout,
                                          audio: coordinator.audio,
                                          containerSize: geo.size)
                        .allowsHitTesting(false)
                    // 画面左右两侧的音柱
                    if settings.showAudioMeters {
                        AudioMeterOverlay(layout: layout,
                                          audio: coordinator.audio,
                                          containerSize: geo.size)
                            .allowsHitTesting(false)
                    }

                    // 每个格子右上角的内容选择菜单（点它切换这一格显示什么）
                    PaneChromeOverlay(layout: layout,
                                      settings: settings,
                                      containerSize: geo.size)

                    // 布局调试叠加层：画出每格的 格子/绘图区/刻度栏/画面区 实际矩形与尺寸
                    if settings.showLayoutDebug {
                        LayoutDebugOverlay(layout: layout, containerSize: geo.size)
                            .allowsHitTesting(false)
                    }

                    // 超标报警：红色外框
                    if settings.warningAlarmEnabled {
                        AlarmBorderOverlay(measurement: coordinator.measurementHub)
                    }
                }
            }
            .contentShape(Rectangle())
            .onTapGesture {
                withAnimation(.easeInOut(duration: 0.18)) {
                    uiHidden.toggle()
                }
            }
            .onAppear {
                coordinator.updateLayout(layout)
            }
            .onChange(of: layout) { _, newValue in
                coordinator.updateLayout(newValue)
            }
        }
        .background(Color.black)
    }

    // MARK: - 生命周期

    private func handleAppear() {
        coordinator.attach(settings: settings, capture: capture, lutStore: lutStore)
        coordinatorReady = coordinator.isReady
        // 把「自动选格式」偏好交给采集层（默认开启：不用手挑分辨率和帧率）
        capture.autoFormatEnabled = settings.autoFormat
        capture.start()
        hasStarted = true
        applyIdleTimer()
    }

    private func syncAutoFormat(preferred: Bool) {
        if preferred {
            capture.useAutomaticFormat()
        } else {
            capture.autoFormatEnabled = false
        }
    }

    private func handleScenePhase(_ phase: ScenePhase) {
        switch phase {
        case .active:
            if hasStarted {
                capture.resume()
            }
        case .background:
            // 退到后台时采集会停，录制/推流必须一起收尾，否则文件会坏、推流会假死
            coordinator.stream.stopAll()
            capture.pause()
        default:
            break
        }
        applyIdleTimer()
    }

    private func applyIdleTimer() {
        UIApplication.shared.isIdleTimerDisabled = settings.preventSleep
    }

    private func handleImport(_ result: Result<[URL], Error>) {
        switch result {
        case .success(let urls):
            guard let url = urls.first else { return }
            lutStore.importFile(from: url)
        case .failure(let error):
            print("导入 LUT 失败: \(error.localizedDescription)")
        }
    }
}
