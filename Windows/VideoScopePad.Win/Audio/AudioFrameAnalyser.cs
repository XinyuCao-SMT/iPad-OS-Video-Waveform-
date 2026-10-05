//
//  AudioFrameAnalyser.cs
//  VideoScopePad.Win
//
//  音频**一帧报告**：把六个已验过的模块组装成界面只需读一次的产物。
//  接 WASAPI 时只做两件事 —— 把样本喂进来、照报告画出来；分析口径全部在这里，不撒到界面里。
//
//  输入：多通道样本（≤8 声道，也可以是 2ch）、采样率、参考时刻（可选，用于逐轨声画延时）。
//  输出：每通道电平与峰值保持、BS.1770 响度、1/3 倍频程频谱、指定声道对的声相、逐轨延时。
//

namespace VideoScopePad.Win.Audio;

/// <summary>一帧音频报告</summary>
public sealed record AudioFrameReport(
    int SampleRate,
    int ChannelCount,
    IReadOnlyList<ChannelMeterState> Meters,
    AudioAnalysis Loudness,
    IReadOnlyList<SpectrumBand> Spectrum,
    GoniometerResult Phase,
    IReadOnlyList<TrackDelay> Delays,
    int PhaseLeftChannel,
    int PhaseRightChannel)
{
    public static AudioFrameReport Empty { get; } = new(
        0, 0,
        Array.Empty<ChannelMeterState>(),
        AudioAnalysis.Empty(),
        Array.Empty<SpectrumBand>(),
        GoniometerResult.Empty,
        Array.Empty<TrackDelay>(),
        0, 0);

    public bool HasSignal => Meters.Any(m => m.LevelDbfs > -80);
}

/// <summary>把多通道样本变成一帧报告（有状态：电平表的峰值保持/CLIP 跨帧累积）</summary>
public sealed class AudioFrameAnalyser
{
    private readonly ChannelLevelMeter _meters;
    private double _elapsedSeconds;

    public AudioFrameAnalyser(int channels = 8)
    {
        _meters = new ChannelLevelMeter(channels);
    }

    /// <summary>电平表（界面上的"清除 CLIP"按钮直接调它）</summary>
    public ChannelLevelMeter Meters => _meters;

    /// <summary>声相用哪一对声道（默认 0/1；8ch 素材里可改成环绕对）</summary>
    public int PhaseLeftChannel { get; set; }
    public int PhaseRightChannel { get; set; } = 1;

    /// <summary>画面的变化时刻（秒，相对同一时间基准）；null = 本帧不做声画延时测算</summary>
    public double? VideoChangeSeconds { get; set; }

    /// <summary>是否算 1/3 倍频程频谱（频谱面板不显示时可以关掉省算力）</summary>
    public bool MeasureSpectrum { get; set; } = true;

    /// <summary>
    /// 分析一帧。<paramref name="elapsedSeconds"/> 是距上一帧的时长（电平表的保持/衰减用它）。
    /// </summary>
    public AudioFrameReport Analyse(IReadOnlyList<float[]> channels, int sampleRate, double elapsedSeconds)
    {
        ArgumentNullException.ThrowIfNull(channels);
        if (channels.Count == 0 || sampleRate <= 0)
        {
            return AudioFrameReport.Empty;
        }

        _elapsedSeconds += elapsedSeconds;
        IReadOnlyList<ChannelMeterState> meters = _meters.Update(channels, elapsedSeconds);
        AudioAnalysis loudness = new LoudnessMeter(sampleRate).Analyse(channels);
        IReadOnlyList<SpectrumBand> spectrum = MeasureSpectrum
            ? SpectrumAnalyser.Analyse(channels, sampleRate)
            : Array.Empty<SpectrumBand>();
        GoniometerResult phase = GoniometerAnalyser.Analyse(channels, PhaseLeftChannel, PhaseRightChannel);
        IReadOnlyList<TrackDelay> delays = VideoChangeSeconds is { } videoChange
            ? AvSyncAnalyser.Analyse(channels, sampleRate, videoChange)
            : Array.Empty<TrackDelay>();

        return new AudioFrameReport(
            sampleRate,
            channels.Count,
            meters,
            loudness,
            spectrum,
            phase,
            delays,
            PhaseLeftChannel,
            PhaseRightChannel);
    }
}
