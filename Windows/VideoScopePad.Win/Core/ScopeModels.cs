//
//  ScopeModels.cs
//  VideoScopePad.Win
//
//  格内容与布局相关的模型。数值全部与 iPad 版 `Model/ScopeModels.swift` 保持一致 ——
//  这是两个平台「看起来是同一个软件」的基础，改这里必须两边一起改。
//

namespace VideoScopePad.Win.Core;

/// <summary>一个格子显示什么</summary>
public enum PaneContent
{
    Picture,
    Vectorscope,
    Waveform,
    Parade,
    Diamond,
    Cie,
    StreamStats,
    AvSync,
    AudioPhase,
    AudioSpectrum,
}

/// <summary>示波器种类（与 ShaderTypes.h 里的 VS_GAMUT_SECTION_* 对应）</summary>
public enum ScopePanelKind
{
    Vectorscope = 0,
    Waveform = 1,
    Parade = 2,
    Diamond = 3,
    Cie = 4,
}

public enum MonitorLayoutPreset
{
    Fullscreen,
    Quad,
    BottomStrip,
    RightColumn,
    Overlay,
}

public enum AspectMode
{
    Fit,
    Fill,
}

public enum PictureRotation
{
    Automatic,
    None,
    Clockwise90,
    CounterClockwise90,
    Rotate180,
}

/// <summary>波形显示模式（对应 flags.x 里的 shaderValue）</summary>
public enum WaveformMode
{
    Luma = 0,
    RgbOverlay = 1,
}

/// <summary>示波器取样位置</summary>
public enum ScopeSource
{
    PreLut,
    PostLut,
}

/// <summary>刻度单位（IRE / 等效 mV / 百分比），数值与刻度换算沿用 iPad 版</summary>
public enum ScaleUnit
{
    Ire,
    Millivolt,
    Percent,
}

public static class PaneContentExtensions
{
    /// <summary>该内容对应的示波器种类（画面与音频类为 null）</summary>
    public static ScopePanelKind? ScopeKind(this PaneContent content) => content switch
    {
        PaneContent.Vectorscope => ScopePanelKind.Vectorscope,
        PaneContent.Waveform => ScopePanelKind.Waveform,
        PaneContent.Parade => ScopePanelKind.Parade,
        PaneContent.Diamond => ScopePanelKind.Diamond,
        PaneContent.Cie => ScopePanelKind.Cie,
        _ => null,
    };

    /// <summary>绘图区必须是正方形（圆形与色域刻度不能被拉歪）</summary>
    public static bool NeedsSquarePlot(this PaneContent content) => content switch
    {
        PaneContent.Vectorscope => true,
        PaneContent.Diamond => true,
        PaneContent.Cie => true,
        _ => false,
    };

    /// <summary>纯界面绘制、不需要 GPU 示波器（音频三件套与推流状态）</summary>
    public static bool IsInterfaceOnly(this PaneContent content) => content switch
    {
        PaneContent.Picture => false,
        PaneContent.StreamStats => true,
        PaneContent.AvSync => true,
        PaneContent.AudioPhase => true,
        PaneContent.AudioSpectrum => true,
        _ => false,
    };

    /// <summary>是否需要在纹理被拉伸时保持「数据密度」不变（暂未使用，留作后续对齐 iPad 版）</summary>
    public static bool KeepsTextureDensity(this PaneContent content) => false;
}

public static class PictureRotationExtensions
{
    public static int Degrees(this PictureRotation rotation) => rotation switch
    {
        PictureRotation.Automatic => 0,
        PictureRotation.None => 0,
        PictureRotation.Clockwise90 => 90,
        PictureRotation.CounterClockwise90 => 270,
        PictureRotation.Rotate180 => 180,
        _ => 0,
    };

    /// <summary>
    /// 自动模式：界面是竖屏（高 &gt; 宽）时把画面顺时针转 90°。
    /// 与 iPad 版 `resolvedDegrees(containerIsPortrait:)` 完全一致。
    /// </summary>
    public static int ResolvedDegrees(this PictureRotation rotation, bool containerIsPortrait)
        => rotation == PictureRotation.Automatic ? (containerIsPortrait ? 90 : 0) : rotation.Degrees();
}

public static class ScaleUnitExtensions
{
    /// <summary>IRE → 该单位的数值（0 IRE = 0 mV，100 IRE = 700 mV；百分比即 IRE 本身）</summary>
    public static double FromIre(this ScaleUnit unit, double ire) => unit switch
    {
        ScaleUnit.Millivolt => ire * 7.0,
        _ => ire,
    };

    public static double ToIre(this ScaleUnit unit, double value) => unit switch
    {
        ScaleUnit.Millivolt => value / 7.0,
        _ => value,
    };

    public static string Suffix(this ScaleUnit unit) => unit switch
    {
        ScaleUnit.Ire => " IRE",
        ScaleUnit.Millivolt => " mV",
        _ => "%",
    };

    public static string Format(this ScaleUnit unit, double ire) => unit switch
    {
        ScaleUnit.Millivolt => unit.FromIre(ire).ToString("0") + " mV",
        ScaleUnit.Percent => ire.ToString("0") + "%",
        _ => ire.ToString("0") + " IRE",
    };

    public static string FormatPrecise(this ScaleUnit unit, double ire) => unit switch
    {
        ScaleUnit.Millivolt => unit.FromIre(ire).ToString("0") + " mV",
        ScaleUnit.Percent => ire.ToString("0.0") + "%",
        _ => ire.ToString("0.0") + " IRE",
    };

    /// <summary>刻度栏顶部那一行单位名（iPad 版同为 IRE / mV / %）</summary>
    public static string ShortTitle(this ScaleUnit unit) => unit switch
    {
        ScaleUnit.Millivolt => "mV",
        ScaleUnit.Percent => "%",
        _ => "IRE",
    };

    /// <summary>
    /// 刻度轴上要标注的位置（单位 = 当前单位）。与 iPad 版 ScaleUnit.tickValues() 逐一对应：
    /// IRE / % 每 10 一格（0…100），mV 每 70 一格（0…700，等效电平 100 IRE = 700 mV）。
    /// </summary>
    public static IReadOnlyList<double> TickValues(this ScaleUnit unit) => unit switch
    {
        ScaleUnit.Millivolt => new double[] { 0, 70, 140, 210, 280, 350, 420, 490, 560, 630, 700 },
        _ => new double[] { 0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 },
    };

    /// <summary>主要刻度（画粗线、写大字）：IRE/% 是 25 的倍数，mV 是 175 的倍数</summary>
    public static bool IsMajorTick(this ScaleUnit unit, double value) => unit switch
    {
        ScaleUnit.Millivolt => Math.Abs(value / 70 - Math.Round(value / 70)) < 1e-9
                               && Math.Abs((int)value % 175) == 0,
        _ => Math.Abs(value % 25) < 1e-9,
    };
}
