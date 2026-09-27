//
//  AppSettings.swift
//  VideoScopePad
//
//  全局设置（自动持久化到 UserDefaults）。
//

import Combine
import Foundation

final class AppSettings: ObservableObject {

    private enum Key {
        static let prefix = "vsp."
        static let monitorLayout = prefix + "monitorLayout"
        static let aspectMode = prefix + "aspectMode"
        static let displayMode = prefix + "displayMode"
        static let showVectorscope = prefix + "showVectorscope"
        static let showWaveform = prefix + "showWaveform"
        static let showParade = prefix + "showParade"
        static let scopeSource = prefix + "scopeSource"
        static let waveformMode = prefix + "waveformMode"
        static let scopeQuality = prefix + "scopeQuality"
        static let vectorscopeGain = prefix + "vectorscopeGain"
        static let scopeIntensity = prefix + "scopeIntensity"
        static let scopeOpacity = prefix + "scopeOpacity"
        static let lutEnabled = prefix + "lutEnabled"
        static let lutIntensity = prefix + "lutIntensity"
        static let exposure = prefix + "exposure"
        static let contrast = prefix + "contrast"
        static let saturation = prefix + "saturation"
        static let gamma = prefix + "gamma"
        static let preventSleep = prefix + "preventSleep"
        static let showHUD = prefix + "showHUD"
        static let scopeColor = prefix + "scopeColor"
        static let showMeasurement = prefix + "showMeasurement"
        static let fullscreenContent = prefix + "fullscreenContent"
        static let quadContents = prefix + "quadContents"
        static let scaleUnit = prefix + "scaleUnit"
        static let autoFormat = prefix + "autoFormat"
    }

    // MARK: - 监视器

    @Published var monitorLayout: MonitorLayoutPreset = .bottomStrip {
        didSet { UserDefaults.standard.set(monitorLayout.rawValue, forKey: Key.monitorLayout) }
    }

    @Published var aspectMode: AspectMode = .fit {
        didSet { UserDefaults.standard.set(aspectMode.rawValue, forKey: Key.aspectMode) }
    }

    @Published var displayMode: DisplayMode = .color {
        didSet { UserDefaults.standard.set(displayMode.rawValue, forKey: Key.displayMode) }
    }

    @Published var showHUD = true {
        didSet { UserDefaults.standard.set(showHUD, forKey: Key.showHUD) }
    }

    /// 信号幅度数值读数（峰值白 / 黑位 / 平均值 / 超范围占比）
    @Published var showMeasurement = true {
        didSet { UserDefaults.standard.set(showMeasurement, forKey: Key.showMeasurement) }
    }

    @Published var preventSleep = true {
        didSet { UserDefaults.standard.set(preventSleep, forKey: Key.preventSleep) }
    }

    /// 冻结画面（便于用示波器读值）
    @Published var freeze = false

    // MARK: - 示波器

    @Published var showVectorscope = true {
        didSet { UserDefaults.standard.set(showVectorscope, forKey: Key.showVectorscope) }
    }

    @Published var showWaveform = true {
        didSet { UserDefaults.standard.set(showWaveform, forKey: Key.showWaveform) }
    }

    @Published var showParade = false {
        didSet { UserDefaults.standard.set(showParade, forKey: Key.showParade) }
    }

    @Published var scopeSource: ScopeSource = .postLUT {
        didSet { UserDefaults.standard.set(scopeSource.rawValue, forKey: Key.scopeSource) }
    }

    @Published var waveformMode: WaveformMode = .luma {
        didSet { UserDefaults.standard.set(waveformMode.rawValue, forKey: Key.waveformMode) }
    }

    @Published var scopeQuality: ScopeQuality = .half {
        didSet { UserDefaults.standard.set(scopeQuality.rawValue, forKey: Key.scopeQuality) }
    }

    @Published var vectorscopeGain: Double = 1.0 {
        didSet { UserDefaults.standard.set(vectorscopeGain, forKey: Key.vectorscopeGain) }
    }

    @Published var scopeIntensity: Double = 1.0 {
        didSet { UserDefaults.standard.set(scopeIntensity, forKey: Key.scopeIntensity) }
    }

    @Published var scopeOpacity: Double = 0.82 {
        didSet { UserDefaults.standard.set(scopeOpacity, forKey: Key.scopeOpacity) }
    }

    /// 轨迹配色：0 = 绿色，1 = 白色，2 = 琥珀
    @Published var scopeColorIndex: Int = 0 {
        didSet { UserDefaults.standard.set(scopeColorIndex, forKey: Key.scopeColor) }
    }

    // MARK: - 全屏 / 四分割内容

    /// 全屏时显示什么（画面 or 任一种示波器）
    @Published var fullscreenContent: PaneContent = .picture {
        didSet { UserDefaults.standard.set(fullscreenContent.rawValue, forKey: Key.fullscreenContent) }
    }

    /// 四分割每一格显示什么（顺序：左上、右上、左下、右下）
    @Published var quadContents: [PaneContent] = [.picture, .vectorscope, .waveform, .parade] {
        didSet {
            let encoded = quadContents.map { $0.rawValue }.joined(separator: ",")
            UserDefaults.standard.set(encoded, forKey: Key.quadContents)
        }
    }

    /// 波形 / 矢量示波器侧边刻度的单位
    @Published var scaleUnit: ScaleUnit = .ire {
        didSet { UserDefaults.standard.set(scaleUnit.rawValue, forKey: Key.scaleUnit) }
    }

    /// 自动选择输入格式（默认开）：
    /// 打开后不需要手动挑分辨率和帧率，程序自己选最合适的一个，并把手动选择降级成高级选项。
    @Published var autoFormat = true {
        didSet { UserDefaults.standard.set(autoFormat, forKey: Key.autoFormat) }
    }

    // MARK: - LUT 与调色

    @Published var lutEnabled = true {
        didSet { UserDefaults.standard.set(lutEnabled, forKey: Key.lutEnabled) }
    }

    @Published var lutIntensity: Double = 1.0 {
        didSet { UserDefaults.standard.set(lutIntensity, forKey: Key.lutIntensity) }
    }

    @Published var exposure: Double = 0 {
        didSet { UserDefaults.standard.set(exposure, forKey: Key.exposure) }
    }

    @Published var contrast: Double = 1.0 {
        didSet { UserDefaults.standard.set(contrast, forKey: Key.contrast) }
    }

    @Published var saturation: Double = 1.0 {
        didSet { UserDefaults.standard.set(saturation, forKey: Key.saturation) }
    }

    @Published var gamma: Double = 1.0 {
        didSet { UserDefaults.standard.set(gamma, forKey: Key.gamma) }
    }

    // MARK: - 派生值

    /// 旧预设（底部 / 右侧 / 叠加）里要显示的示波器
    var enabledPanels: [ScopePanelKind] {
        var panels: [ScopePanelKind] = []
        if showVectorscope { panels.append(.vectorscope) }
        if showWaveform { panels.append(.waveform) }
        if showParade { panels.append(.parade) }
        return panels
    }

    /// 当前布局实际需要统计的示波器：
    /// 全屏 / 四分割按格子内容决定，其余预设按开关决定。
    var requiredScopes: Set<ScopePanelKind> {
        switch monitorLayout {
        case .fullscreen:
            if let kind = fullscreenContent.scopeKind { return [kind] }
            return []
        case .quad:
            return Set(quadContents.compactMap { $0.scopeKind })
        default:
            return Set(enabledPanels)
        }
    }

    /// 当前布局是否需要显示实时画面
    var needsPicture: Bool {
        switch monitorLayout {
        case .fullscreen: return fullscreenContent == .picture
        case .quad: return quadContents.contains(.picture)
        default: return true
        }
    }

    /// 传给 ScopeLayout 的四分割内容（保证 4 个）
    var normalizedQuadContents: [PaneContent] {
        var list = Array(quadContents.prefix(4))
        while list.count < 4 { list.append(.picture) }
        return list
    }

    var gradeIsNeutral: Bool {
        abs(exposure) < 0.001
            && abs(contrast - 1) < 0.001
            && abs(saturation - 1) < 0.001
            && abs(gamma - 1) < 0.001
    }

    var traceColor: SIMD3<Float> {
        switch scopeColorIndex {
        case 1: return SIMD3(0.95, 0.97, 1.0)
        case 2: return SIMD3(1.0, 0.78, 0.30)
        default: return SIMD3(0.35, 1.0, 0.55)
        }
    }

    // MARK: - 初始化

    init() {
        let defaults = UserDefaults.standard

        monitorLayout = MonitorLayoutPreset(rawValue: defaults.string(forKey: Key.monitorLayout) ?? "") ?? .fullscreen
        aspectMode = AspectMode(rawValue: defaults.string(forKey: Key.aspectMode) ?? "") ?? .fit
        displayMode = DisplayMode(rawValue: defaults.string(forKey: Key.displayMode) ?? "") ?? .color
        scopeSource = ScopeSource(rawValue: defaults.string(forKey: Key.scopeSource) ?? "") ?? .postLUT
        waveformMode = WaveformMode(rawValue: defaults.string(forKey: Key.waveformMode) ?? "") ?? .luma
        scopeQuality = ScopeQuality(rawValue: defaults.string(forKey: Key.scopeQuality) ?? "") ?? .half
        fullscreenContent = PaneContent(rawValue: defaults.string(forKey: Key.fullscreenContent) ?? "") ?? .picture
        scaleUnit = ScaleUnit(rawValue: defaults.string(forKey: Key.scaleUnit) ?? "") ?? .ire

        // 四分割内容：存成 "picture,vectorscope,waveform,parade"
        if let raw = defaults.string(forKey: Key.quadContents) {
            let parts = raw.split(separator: ",").compactMap { PaneContent(rawValue: String($0)) }
            if parts.count == 4 {
                quadContents = parts
            }
        }

        autoFormat = Self.bool(defaults, Key.autoFormat, true)
        showVectorscope = Self.bool(defaults, Key.showVectorscope, true)
        showWaveform = Self.bool(defaults, Key.showWaveform, true)
        showParade = Self.bool(defaults, Key.showParade, false)
        showHUD = Self.bool(defaults, Key.showHUD, true)
        showMeasurement = Self.bool(defaults, Key.showMeasurement, true)
        preventSleep = Self.bool(defaults, Key.preventSleep, true)
        lutEnabled = Self.bool(defaults, Key.lutEnabled, true)

        vectorscopeGain = Self.double(defaults, Key.vectorscopeGain, 1.0)
        scopeIntensity = Self.double(defaults, Key.scopeIntensity, 1.0)
        scopeOpacity = Self.double(defaults, Key.scopeOpacity, 0.82)
        lutIntensity = Self.double(defaults, Key.lutIntensity, 1.0)
        exposure = Self.double(defaults, Key.exposure, 0)
        contrast = Self.double(defaults, Key.contrast, 1.0)
        saturation = Self.double(defaults, Key.saturation, 1.0)
        gamma = Self.double(defaults, Key.gamma, 1.0)
        scopeColorIndex = defaults.object(forKey: Key.scopeColor) as? Int ?? 0
    }

    private static func bool(_ defaults: UserDefaults, _ key: String, _ fallback: Bool) -> Bool {
        defaults.object(forKey: key) as? Bool ?? fallback
    }

    private static func double(_ defaults: UserDefaults, _ key: String, _ fallback: Double) -> Double {
        defaults.object(forKey: key) as? Double ?? fallback
    }

    // MARK: - 操作

    func resetGrade() {
        exposure = 0
        contrast = 1
        saturation = 1
        gamma = 1
        lutIntensity = 1
    }

    func resetScopeSettings() {
        vectorscopeGain = 1
        scopeIntensity = 1
        scopeOpacity = 0.82
        scopeQuality = .half
        scopeColorIndex = 0
        scaleUnit = .ire
    }

    /// 复位全屏 / 四分割的内容
    func resetPaneContents() {
        fullscreenContent = .picture
        quadContents = [.picture, .vectorscope, .waveform, .parade]
    }
}
