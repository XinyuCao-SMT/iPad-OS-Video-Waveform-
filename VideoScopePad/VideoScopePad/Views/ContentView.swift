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
                               onClearAlarms: { coordinator.clearAlarmsAndPeaks() })
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
                          showLUTImporter: $showLUTImporter)
        }
        .onAppear(perform: handleAppear)
        .onChange(of: scenePhase) { _, phase in
            handleScenePhase(phase)
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

            Spacer(minLength: 4)

            Button {
                settings.freeze.toggle()
            } label: {
                ChipLabel(title: settings.freeze ? "解除冻结" : "冻结",
                          systemImage: settings.freeze ? "play.fill" : "pause.fill",
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
                                             legacyPanels: settings.enabledPanels)

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

                    // 每个格子右上角的内容选择菜单（点它切换这一格显示什么）
                    PaneChromeOverlay(layout: layout,
                                      settings: settings,
                                      containerSize: geo.size)

                    // 超标报警：红色外框
                    if settings.warningAlarmEnabled {
                        AlarmBorderOverlay(measurement: coordinator.measurementHub)
                    }

                    if settings.showHUD {
                        HUDOverlay(capture: capture,
                                   settings: settings,
                                   lutStore: lutStore,
                                   measurement: coordinator.measurementHub)
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
