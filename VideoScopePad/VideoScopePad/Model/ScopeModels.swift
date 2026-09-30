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
    case fullscreen
    case quad
    case bottomStrip
    case rightColumn
    case overlay

    var id: String { rawValue }

    var title: String {
        switch self {
        case .fullscreen: return "全屏（内容可选）"
        case .quad: return "四分割"
        case .bottomStrip: return "底部示波器"
        case .rightColumn: return "右侧示波器"
        case .overlay: return "叠加在画面上"
        }
    }

    var shortTitle: String {
        switch self {
        case .fullscreen: return "全屏"
        case .quad: return "四分割"
        case .bottomStrip: return "底部"
        case .rightColumn: return "右侧"
        case .overlay: return "叠加"
        }
    }

    var symbolName: String {
        switch self {
        case .fullscreen: return "rectangle.inset.filled"
        case .quad: return "square.grid.2x2"
        case .bottomStrip: return "rectangle.bottomthird.inset.filled"
        case .rightColumn: return "rectangle.rightthird.inset.filled"
        case .overlay: return "square.on.square"
        }
    }

    /// 全屏与四分割之间可以一键来回切
    var toggled: MonitorLayoutPreset {
        switch self {
        case .fullscreen: return .quad
        case .quad: return .fullscreen
        default: return .fullscreen
        }
    }
}

/// 一个「格子」里放什么内容
enum PaneContent: String, CaseIterable, Identifiable {
    case picture
    case vectorscope
    case waveform
    case parade
    case diamond
    case cie
    case streamStats
    case avSync
    case audioPhase

    var id: String { rawValue }

    var title: String {
        switch self {
        case .picture: return "实时画面"
        case .vectorscope: return "矢量示波器"
        case .waveform: return "亮度波形"
        case .parade: return "RGB 波形"
        case .diamond: return "钻石图（RGB 色域）"
        case .cie: return "马蹄图（CIE 色度）"
        case .streamStats: return "推流状态（近 5 分钟）"
        case .avSync: return "声画延时（A/V Sync）"
        case .audioPhase: return "声相（李萨如）"
        }
    }

    var shortTitle: String {
        switch self {
        case .picture: return "画面"
        case .vectorscope: return "矢量"
        case .waveform: return "波形"
        case .parade: return "RGB"
        case .diamond: return "钻石"
        case .cie: return "马蹄"
        case .streamStats: return "推流"
        case .avSync: return "声画"
        case .audioPhase: return "声相"
        }
    }

    var symbolName: String {
        switch self {
        case .picture: return "video"
        case .vectorscope: return "circle.grid.cross"
        case .waveform: return "waveform"
        case .parade: return "chart.bar.doc.horizontal"
        case .diamond: return "diamond"
        case .cie: return "chart.xyaxis.line"
        case .streamStats: return "chart.line.uptrend.xyaxis"
        case .avSync: return "waveform.badge.mic"
        case .audioPhase: return "circle.hexagongrid"
        }
    }

    /// 一句话说明（设置面板 / 菜单里做提示用）
    var detail: String {
        switch self {
        case .picture: return "采集卡的实时画面"
        case .vectorscope: return "Cb / Cr 平面，看色度落点与饱和度"
        case .waveform: return "亮度波形，纵向为标定过的 IRE 幅度轴"
        case .parade: return "RGB 三路波形并排"
        case .diamond: return "Tektronix 钻石图：上菱形画 G（左）与 B（右）、下菱形画 G（左）与 R（右），纯黑在两菱形交会的中心、灰阶是正中竖线；轨迹跑出菱形即 R'G'B' 色域越界"
        case .cie: return "CIE 1931 色度图：画面颜色在 xy 平面的分布 + 709 / 2020 色域三角"
        case .streamStats: return "编码码率 / SRT 估计带宽 / 网络延迟（RTT）近 5 分钟曲线"
        case .avSync: return "声画延时：测试设备周期发送「静音黑场 → 千周声 + 彩条」，这里测两者的到达时差"
        case .audioPhase: return "声相（李萨如）：立体声 L/R 关系图 —— 竖直中线=单声道/同相，水平=反相，并给出相关度与平衡"
        }
    }

    /// 画面之外的内容对应的示波器种类（推流状态与声画延时不是 GPU 示波器，所以为 nil）
    var scopeKind: ScopePanelKind? {
        switch self {
        case .picture, .streamStats, .avSync, .audioPhase: return nil
        case .vectorscope: return .vectorscope
        case .waveform: return .waveform
        case .parade: return .parade
        case .diamond: return .diamond
        case .cie: return .cie
        }
    }

    /// 绘图区是否必须是正方形（圆形/方形刻度不能被拉歪）
    var needsSquarePlot: Bool {
        self == .vectorscope || self == .diamond || self == .cie
    }

    /// 纯界面绘制（不走 Metal 示波器管线）
    var isInterfaceOnly: Bool { self == .streamStats || self == .avSync || self == .audioPhase }
}

/// 波形/矢量图侧边刻度的显示单位
///
/// 说明：UVC 交给我们的是已经数字化并做过钳位/增益的码流，
/// **采集卡输入端的真实模拟电压读不到**。这里的 mV 是按广播规范换算的等效电平：
/// 100 IRE（视频范围码值 235）= 700 mV，即 7 mV/IRE，这也是数字波形监视器标 mV 刻度的标准做法。
enum ScaleUnit: String, CaseIterable, Identifiable {
    case ire
    case millivolt
    case percent

    var id: String { rawValue }

    var title: String {
        switch self {
        case .ire: return "IRE"
        case .millivolt: return "mV（等效）"
        case .percent: return "%"
        }
    }

    var shortTitle: String {
        switch self {
        case .ire: return "IRE"
        case .millivolt: return "mV"
        case .percent: return "%"
        }
    }

    /// 每 100 IRE 对应的等效毫伏数（广播规范）
    static let millivoltPerHundredIRE: Double = 700

    /// 把 IRE 换算成当前单位
    func value(fromIRE ire: Double) -> Double {
        switch self {
        case .ire: return ire
        case .millivolt: return ire / 100 * Self.millivoltPerHundredIRE
        case .percent: return ire
        }
    }

    /// 刻度轴上要标注的位置（单位：当前单位）
    func tickValues() -> [Double] {
        switch self {
        case .ire, .percent:
            return [0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100]
        case .millivolt:
            return [0, 70, 140, 210, 280, 350, 420, 490, 560, 630, 700]
        }
    }

    /// 主要刻度（画粗线、写大字）
    func isMajorTick(_ value: Double) -> Bool {
        switch self {
        case .ire, .percent: return value.truncatingRemainder(dividingBy: 25) == 0
        case .millivolt: return (value / 70).rounded() == value / 70 && Int(value) % 175 == 0
        }
    }

    func format(_ value: Double) -> String {
        switch self {
        case .ire, .percent: return String(format: "%.0f", value)
        case .millivolt: return String(format: "%.0f", value)
        }
    }

    /// 小数形式的读数（数值面板用）
    func formatPrecise(_ ire: Double) -> String {
        switch self {
        case .ire: return String(format: "%.1f IRE", ire)
        case .millivolt: return String(format: "%.0f mV", value(fromIRE: ire))
        case .percent: return String(format: "%.1f%%", ire)
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

/// 画面方向：UVC 采集卡的画面方向是固定的，而 iPad 转屏时界面会跟着转，
/// 于是竖屏下画面还是横的（两边留黑、显得小）。这里给几种选择：
///   自动 = 界面竖屏时把画面转 90°（跟随设备方向），横屏不动
enum PictureRotation: String, CaseIterable, Identifiable {
    case automatic
    case none
    case clockwise90
    case counterClockwise90
    case rotate180

    var id: String { rawValue }

    var title: String {
        switch self {
        case .automatic: return "自动跟随界面"
        case .none: return "不旋转"
        case .clockwise90: return "顺时针 90°"
        case .counterClockwise90: return "逆时针 90°"
        case .rotate180: return "旋转 180°"
        }
    }

    var detail: String {
        switch self {
        case .automatic:
            return "界面是竖屏时把画面转 90°（画面跟着设备方向走），横屏时保持原样。"
        case .none:
            return "始终按信号原本的方向显示 —— 监视器最忠于信号的做法。"
        case .clockwise90:
            return "画面顺时针转 90°，适合竖屏使用但自动方向不对的情况。"
        case .counterClockwise90:
            return "画面逆时针转 90°。"
        case .rotate180:
            return "画面上下颠倒（采集卡或安装方向倒置时用）。"
        }
    }

    /// 顺时针角度
    var degrees: Int {
        switch self {
        case .automatic: return 0
        case .none: return 0
        case .clockwise90: return 90
        case .counterClockwise90: return 270
        case .rotate180: return 180
        }
    }

    /// 结合容器方向解析出实际角度（containerIsPortrait 为 true 表示界面是竖屏）
    func resolvedDegrees(containerIsPortrait: Bool) -> Int {
        switch self {
        case .automatic: return containerIsPortrait ? 90 : 0
        default: return degrees
        }
    }
}

enum ScopePanelKind: String, CaseIterable, Identifiable {    case vectorscope
    case waveform
    case parade
    /// 钻石图：RGB 立方体沿白轴投影的色域菱形图
    case diamond
    /// 马蹄图：CIE 1931 色度图
    case cie

    var id: String { rawValue }

    var title: String {
        switch self {
        case .vectorscope: return "矢量示波器"
        case .waveform: return "亮度波形"
        case .parade: return "RGB 波形"
        case .diamond: return "钻石图（RGB 色域）"
        case .cie: return "马蹄图（CIE 色度）"
        }
    }

    var shortTitle: String {
        switch self {
        case .vectorscope: return "矢量"
        case .waveform: return "波形"
        case .parade: return "RGB"
        case .diamond: return "钻石"
        case .cie: return "马蹄"
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
