//
//  RenderCoordinator.swift
//  VideoScopePad
//
//  把「采集 → 渲染 → 视图」串起来的中枢：
//   - 持有 MetalContext / ScopeEngine / VideoRenderer
//   - 采集帧回调直接投递给渲染器并请求重绘
//   - 接收 SwiftUI 计算好的布局
//

import Combine
import Foundation
import MetalKit
import SwiftUI
import UIKit

final class RenderCoordinator: ObservableObject {

    let context: MetalContext?
    let scopeEngine: ScopeEngine?
    private(set) var renderer: VideoRenderer?
    private(set) var startupError: String?

    /// 信号幅度数值读数（独立发布，避免 10Hz 刷新带动整个监视器界面重建）
    let measurementHub = MeasurementHub()

    /// 峰值保持 / 报警锁存的状态机（只在测量回调里更新）
    private let peakHold = PeakHoldTracker()
    private let warningLatch = WarningLatch()
    private var lastAlarmHaptic: Date?

    /// 录制 / 推流 / 抓帧 / 读数导出
    let stream = StreamController()

    /// 音频输入：音量柱 + 千周声起音检测
    let audio = AudioMonitor()
    /// 声画延时测量（千周声 vs 彩条）
    let avSync = AVSyncMeter()
    /// 视频侧：找出「黑场 → 彩条」的跳变帧
    private let barsDetector = VideoBarsDetector()
    private var signatureAnalyzer: FrameSignatureAnalyzer?

    private var placeholderLUT: LUTTextures?
    private var settings: AppSettings?
    private weak var view: MTKView?

    init() {
        // 注意：let 属性必须在所有路径上只赋值一次，
        // 所以先在局部变量里做可能抛错的事，最后统一赋给 self。
        var madeContext: MetalContext?
        var madeEngine: ScopeEngine?
        var madePlaceholder: LUTTextures?
        var failure: String?

        do {
            let context = try MetalContext()
            madeContext = context
            madeEngine = try ScopeEngine(context: context)
            madePlaceholder = try LUTTextureBuilder.makePlaceholder(device: context.device)
        } catch {
            failure = error.localizedDescription
        }

        self.context = madeContext
        self.scopeEngine = madeEngine
        self.placeholderLUT = madePlaceholder
        self.startupError = failure
    }

    var isReady: Bool { renderer != nil }

    /// 由 ContentView 在出现时调用一次
    func attach(settings: AppSettings, capture: CaptureController, lutStore: LUTStore) {
        guard self.settings == nil else { return }
        self.settings = settings

        guard let context, let scopeEngine, let placeholderLUT else { return }

        let renderer = VideoRenderer(context: context,
                                     scopeEngine: scopeEngine,
                                     settings: settings,
                                     lutSlot: lutStore.slot,
                                     placeholderLUT: placeholderLUT)
        renderer.sourceInfoProvider = { [weak capture] in
            FrameSourceInfo(colorMatrix: capture?.colorMatrix ?? .bt709,
                            isVideoRange: capture?.isVideoRange ?? true)
        }
        renderer.onMeasurement = { [weak self] measurement in
            DispatchQueue.main.async {
                guard let self else { return }
                self.measurementHub.value = measurement
                self.applyTracking(measurement)
            }
        }

        // 声画延时：视频侧每帧算一次画面签名（亮度 / 饱和度），找出「黑场 → 彩条」的跳变帧
        if let analyzer = try? FrameSignatureAnalyzer(device: context.device,
                                                      pipeline: context.pipelines.frameSignature) {
            analyzer.onSignature = { [weak self] signature in
                guard let self else { return }
                self.barsDetector.ingest(signature)
            }
            barsDetector.onBarsOnset = { [weak self] onsetHost, previousHost in
                guard let self else { return }
                let interval = max(onsetHost - previousHost, 0)
                DispatchQueue.main.async {
                    self.avSync.noteVideoOnset(host: onsetHost, frameInterval: interval)
                }
            }
            signatureAnalyzer = analyzer
            renderer.signatureAnalyzer = analyzer
        }

        // 声画延时：音频侧千周声起音
        audio.onToneOnset = { [weak self] hostTime in
            DispatchQueue.main.async {
                self?.avSync.noteAudioOnset(host: hostTime)
            }
        }
        if settings.audioEnabled {
            startAudio(preferredInputID: settings.audioInputID)
        }

        self.renderer = renderer
        view?.delegate = renderer

        capture.onFrame = { [weak self] pixelBuffer, presentationTime in
            guard let self else { return }
            self.renderer?.submit(pixelBuffer: pixelBuffer, presentationTime: presentationTime)
            self.stream.submit(pixelBuffer: pixelBuffer, presentationTime: presentationTime)
            DispatchQueue.main.async { [weak self] in
                self?.view?.setNeedsDisplay()
            }
        }

        lutStore.configure(device: context.device, placeholder: placeholderLUT)
        requestRedraw()
    }

    // MARK: - 音频（音柱 / 声画延时）

    func startAudio(preferredInputID: String?) {
        AudioMonitor.requestPermission { [weak self] granted in
            guard let self else { return }
            guard granted else {
                self.avSync.setAudioAvailable(false)
                return
            }
            self.audio.start(preferredInputID: preferredInputID)
            self.avSync.setAudioAvailable(true)
        }
    }

    func stopAudio() {
        audio.stop()
        avSync.setAudioAvailable(false)
    }

    /// 音频输入设备换了：重启一次引擎
    func restartAudio(preferredInputID: String?) {
        guard settings?.audioEnabled ?? false else { return }
        audio.stop()
        startAudio(preferredInputID: preferredInputID)
    }

    // MARK: - 峰值保持 / 报警（主线程，由测量回调驱动）

    private func applyTracking(_ measurement: SignalMeasurement) {
        guard let settings else { return }

        // 峰值保持
        if settings.peakHoldEnabled {
            measurementHub.peakHold = peakHold.update(with: measurement,
                                                      holdSeconds: settings.peakHoldSeconds)
        } else if measurementHub.peakHold.hasData {
            peakHold.reset()
            measurementHub.peakHold = PeakHoldState()
        }

        // 超标报警（边沿触发）
        guard settings.warningAlarmEnabled else {
            if !measurementHub.activeWarnings.isEmpty {
                warningLatch.reset()
                measurementHub.activeWarnings = []
            }
            return
        }

        let active = warningLatch.update(with: measurement.warnings,
                                        raiseThreshold: settings.warningRaiseCount,
                                        clearThreshold: 0)
        measurementHub.activeWarnings = active

        // 读数 CSV 记录（开关在推流面板里）
        stream.noteMeasurement(measurement, activeWarnings: active)

        if warningLatch.didRaise {
            triggerAlarmFeedback()
        }
    }

    /// 只在报警「确认」的那一下给一次触感，不逐帧震
    private func triggerAlarmFeedback() {
        let now = Date()
        if let last = lastAlarmHaptic, now.timeIntervalSince(last) < 2 { return }
        lastAlarmHaptic = now
        let generator = UINotificationFeedbackGenerator()
        generator.notificationOccurred(.warning)
    }

    /// 手动清掉报警与峰值游标
    func clearAlarmsAndPeaks() {
        warningLatch.reset()
        peakHold.reset()
        measurementHub.activeWarnings = []
        measurementHub.peakHold = PeakHoldState()
    }

    // MARK: - 参考层（冻结）

    /// 冻结 / 解除冻结。
    ///
    /// 「冻结」= 把当前这一帧的示波器图形与数值读数各存一份参考，
    /// 实时图表与实时读数**继续刷新**，参考层以琥珀色叠在实时图表上（见 VideoRenderer 的参考层绘制），
    /// 顶部读数区同时给出参考值与差值 —— 校色的时候就能对着冻结前后的图形/数值调。
    func setReference(_ on: Bool) {
        guard let settings else { return }
        settings.freeze = on

        if on {
            renderer?.requestReferenceCapture()
            // 读数参考：拿当前这一份实时读数当基准（还没读到就当没有）
            measurementHub.reference = measurementHub.value
            measurementHub.referencePeakHold = measurementHub.peakHold.hasData
                ? measurementHub.peakHold
                : nil
        } else {
            renderer?.requestReferenceClear()
            measurementHub.reference = nil
            measurementHub.referencePeakHold = nil
        }
        requestRedraw()
    }

    /// 冻结开关的当前状态（界面按钮直接读它）
    var isReferenceActive: Bool { settings?.freeze ?? false }

    func attach(view: MTKView) {
        self.view = view
        view.delegate = renderer
    }

    /// SwiftUI 侧算好的单位空间布局
    func updateLayout(_ layout: ScopeLayoutResult) {
        guard let renderer else { return }
        if renderer.layout == layout { return }
        renderer.layout = layout
        requestRedraw()
    }

    func requestRedraw() {
        DispatchQueue.main.async { [weak self] in
            self?.view?.setNeedsDisplay()
        }
    }
}
