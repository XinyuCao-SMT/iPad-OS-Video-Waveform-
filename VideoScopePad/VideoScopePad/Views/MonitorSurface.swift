//
//  MonitorSurface.swift
//  VideoScopePad
//
//  SwiftUI 里的 MTKView 包装。
//  使用「暂停 + 按需重绘」模式：只有采集到新帧或设置变化时才画一帧，
//  这样端到端延迟最低，也不会白白耗电。
//

import MetalKit
import QuartzCore
import SwiftUI

struct MonitorSurface: UIViewRepresentable {

    let coordinator: RenderCoordinator

    func makeUIView(context: Context) -> MTKView {
        let view = MTKView(frame: .zero, device: coordinator.context?.device)
        view.colorPixelFormat = .bgra8Unorm
        view.framebufferOnly = true
        view.isPaused = true
        view.enableSetNeedsDisplay = true
        view.preferredFramesPerSecond = 60
        view.autoResizeDrawable = true
        view.isOpaque = true
        view.clearColor = MTLClearColorMake(0.016, 0.018, 0.022, 1)
        view.layer.isOpaque = true

        if let layer = view.layer as? CAMetalLayer {
            // 我们的信号是 BT.709 / sRGB 伽马编码的，交给系统做显示色彩匹配
            layer.colorspace = CGColorSpace(name: CGColorSpace.sRGB)
            layer.wantsExtendedDynamicRangeContent = false
        }

        coordinator.attach(view: view)
        return view
    }

    func updateUIView(_ uiView: MTKView, context: Context) {
        // 布局变化由 SwiftUI 侧计算并推送给协调器，这里无需处理
    }
}
