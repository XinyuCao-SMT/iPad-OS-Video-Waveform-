//
//  ScopeModels.swift
//  VideoScopePad
//
//  界面与渲染层共用的枚举定义。
//

import CoreGraphics
import Foundation

// MARK: - 监视器布局

enum MonitorLayoutPreset: String, CaseIterable, Identifiable {
    case monitorOnly
    case bottomStrip
    case rightColumn
    case overlay

    var id: String { rawValue }

    var title: String {
        switch self {
        case .monitorOnly: return "仅画面"
        case .bottomStrip: return "底部示波器"
        case .rightColumn: return "右侧示波器"
        case .overlay: return "叠加在画面上"
        }
    }

    var symbolName: String {
        switch self {
        case .monitorOnly: return "rectangle"
        case .bottomStrip: return "rectangle.bottomthird.inset.filled"
        case .rightColumn: return "rectangle.rightthird.inset.filled"
        case .overlay: return "square.on.square"
        }
    }
}

enum AspectMode: String, CaseIterable, Identifiable {
    case fit
    case fill

    var id: String { rawValue }

    var title: String {
        switch self {
        case .fit: return "完整显示"
        case .fill: return "铺满裁切"
        }
    }
}

// MARK: - 显示模式

enum DisplayMode: String, CaseIterable, Identifiable {
    case color
    case luma
    case red
    case green
    case blue

    var id: String { rawValue }

    var title: String {
        switch self {
        case .color: return "彩色"
        case .luma: return "亮度"
        case .red: return "R 通道"
        case .green: return "G 通道"
        case .blue: return "B 通道"
        }
    }

    /// 与 ShaderTypes.h 中的 VS_DISPLAY_MODE_* 对应
    var shaderValue: Int32 {
        switch self {
        case .color: return Int32(VS_DISPLAY_MODE_COLOR)
        case .luma: return Int32(VS_DISPLAY_MODE_LUMA)
        case .red: return Int32(VS_DISPLAY_MODE_RED)
        case .green: return Int32(VS_DISPLAY_MODE_GREEN)
        case .blue: return Int32(VS_DISPLAY_MODE_BLUE)
        }
    }
}

// MARK: - 示波器

enum ScopePanelKind: String, CaseIterable, Identifiable {
    case vectorscope
    case waveform
    case parade

    var id: String { rawValue }

    var title: String {
        switch self {
        case .vectorscope: return "矢量示波器"
        case .waveform: return "亮度波形"
        case .parade: return "RGB 波形"
        }
    }

    var shortTitle: String {
        switch self {
        case .vectorscope: return "矢量"
        case .waveform: return "波形"
        case .parade: return "RGB"
        }
    }
}

enum WaveformMode: String, CaseIterable, Identifiable {
    case luma
    case rgbOverlay

    var id: String { rawValue }

    var title: String {
        switch self {
        case .luma: return "亮度"
        case .rgbOverlay: return "RGB 叠加"
        }
    }

    /// 与 ShaderTypes.h 中的 VS_WAVEFORM_MODE_* 对应
    var shaderValue: Int32 {
        switch self {
        case .luma: return Int32(VS_WAVEFORM_MODE_LUMA)
        case .rgbOverlay: return Int32(VS_WAVEFORM_MODE_OVERLAY)
        }
    }
}

/// 示波器测量的是 LUT 之前（原始 log / 相机信号）还是之后（显示信号）
enum ScopeSource: String, CaseIterable, Identifiable {
    case preLUT
    case postLUT

    var id: String { rawValue }

    var title: String {
        switch self {
        case .preLUT: return "LUT 之前"
        case .postLUT: return "LUT 之后"
        }
    }
}

/// 示波器统计的采样密度（越密越准，GPU 开销越大）
enum ScopeQuality: String, CaseIterable, Identifiable {
    case full
    case half
    case quarter

    var id: String { rawValue }

    var title: String {
        switch self {
        case .full: return "精确（每像素）"
        case .half: return "标准（1/2）"
        case .quarter: return "省电（1/4）"
        }
    }

    var stride: Int {
        switch self {
        case .full: return 1
        case .half: return 2
        case .quarter: return 4
        }
    }
}
