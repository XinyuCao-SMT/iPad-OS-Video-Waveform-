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
        self.renderer = renderer
        view?.delegate = renderer

        capture.onFrame = { [weak self] pixelBuffer, presentationTime in
            guard let self else { return }
            self.renderer?.submit(pixelBuffer: pixelBuffer)
            self.stream.submit(pixelBuffer: pixelBuffer, presentationTime: presentationTime)
            DispatchQueue.main.async { [weak self] in
                self?.view?.setNeedsDisplay()
            }
        }

        lutStore.configure(device: context.device, placeholder: placeholderLUT)
        requestRedraw()
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
