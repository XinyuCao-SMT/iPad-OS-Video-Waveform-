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

final class RenderCoordinator: ObservableObject {

    let context: MetalContext?
    let scopeEngine: ScopeEngine?
    private(set) var renderer: VideoRenderer?
    private(set) var startupError: String?

    /// 信号幅度数值读数（独立发布，避免 10Hz 刷新带动整个监视器界面重建）
    let measurementHub = MeasurementHub()

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
        renderer.onMeasurement = { [weak hub = measurementHub] measurement in
            DispatchQueue.main.async {
                hub?.value = measurement
            }
        }
        self.renderer = renderer
        view?.delegate = renderer

        capture.onFrame = { [weak self] pixelBuffer in
            guard let self else { return }
            self.renderer?.submit(pixelBuffer: pixelBuffer)
            DispatchQueue.main.async { [weak self] in
                self?.view?.setNeedsDisplay()
            }
        }

        lutStore.configure(device: context.device, placeholder: placeholderLUT)
        requestRedraw()
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
