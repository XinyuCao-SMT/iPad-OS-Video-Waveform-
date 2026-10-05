//
//  ChannelLevelMeter.cs
//  VideoScopePad.Win
//
//  8 声道电平表的数据模型：每通道的 当前电平 / 峰值保持 / CLIP 锁存。
//  用户明确要"8 条电平表，每通道一根，含峰值保持与 CLIP" —— 所以这里把这三件事的
//  状态机做对并验过；接上 WASAPI 之后界面只是照它画。
//
//  峰值保持的语义（与 iPad 版、以及所有专业电平表一致）：
//    · 电平创新高 → 立即钉住；
//    · 保持 holdSeconds 不动（默认 1.2 s，肉眼来得及看）；
//    · 之后按 decayDbPerSecond 衰减（默认 12 dB/s），直到与当前电平重合。
//  CLIP 是**锁存**的：只要有一帧达到 clipThresholdDbfs（默认 −0.1 dBFS，即幅度 ≥ 0.988），
//  就一直亮着，直到用户显式清除 —— "刚才削顶了"这件事不能自己消失。
//

namespace VideoScopePad.Win.Audio;

/// <summary>单通道电平表状态</summary>
public readonly record struct ChannelMeterState(
    double LevelDbfs,
    double PeakHoldDbfs,
    bool Clipped);

/// <summary>多通道电平表（最多 8 声道，也可以给更少）</summary>
public sealed class ChannelLevelMeter
{
    private readonly double[] _hold;
    private readonly double[] _holdAge;
    private readonly bool[] _clipped;

    public ChannelLevelMeter(int channels = 8)
    {
        Channels = Math.Clamp(channels, 1, 64);
        _hold = new double[Channels];
        _holdAge = new double[Channels];
        _clipped = new bool[Channels];
        for (int ch = 0; ch < Channels; ch++)
        {
            _hold[ch] = double.NegativeInfinity;
        }
    }

    public int Channels { get; }

    /// <summary>峰值保持时长（秒）</summary>
    public double HoldSeconds { get; set; } = 1.2;

    /// <summary>保持之后的衰减速度（dB/秒）</summary>
    public double DecayDbPerSecond { get; set; } = 12.0;

    /// <summary>判定削顶的门槛（dBFS；默认 −0.1，即幅度 ≥ 0.988）</summary>
    public double ClipThresholdDbfs { get; set; } = -0.1;

    /// <summary>喂一帧多通道样本，推进所有状态机</summary>
    public IReadOnlyList<ChannelMeterState> Update(IReadOnlyList<float[]> channels, double elapsedSeconds)
    {
        ArgumentNullException.ThrowIfNull(channels);

        var result = new ChannelMeterState[Channels];
        for (int ch = 0; ch < Channels; ch++)
        {
            double level = ch < channels.Count ? PeakDbfs(channels[ch]) : -240.0;

            // 新高 → 钉住并重置保持计时
            if (level > _hold[ch] || double.IsNegativeInfinity(_hold[ch]))
            {
                _hold[ch] = level;
                _holdAge[ch] = 0;
            }
            else
            {
                _holdAge[ch] += elapsedSeconds;
                if (_holdAge[ch] > HoldSeconds)
                {
                    double decay = (_holdAge[ch] - HoldSeconds) * DecayDbPerSecond;
                    _hold[ch] = Math.Max(_hold[ch] - decay, level);
                    // 衰减到与当前电平重合后就跟着当前电平走（别再往下掉）
                    if (_hold[ch] < level)
                    {
                        _hold[ch] = level;
                    }
                }
            }

            if (level >= ClipThresholdDbfs)
            {
                _clipped[ch] = true;
            }

            result[ch] = new ChannelMeterState(level, _hold[ch], _clipped[ch]);
        }

        return result;
    }

    /// <summary>清除 CLIP 锁存（用户点"清除"时调）</summary>
    public void ClearClip()
    {
        Array.Clear(_clipped);
    }

    /// <summary>全部复位（换设备 / 重新开始时调）</summary>
    public void Reset()
    {
        Array.Clear(_clipped);
        for (int ch = 0; ch < Channels; ch++)
        {
            _hold[ch] = double.NegativeInfinity;
            _holdAge[ch] = 0;
        }
    }

    private static double PeakDbfs(float[] samples)
    {
        double peak = 0;
        foreach (float sample in samples)
        {
            double value = Math.Abs(sample);
            if (value > peak)
            {
                peak = value;
            }
        }
        return peak <= 1e-12 ? -240.0 : 20.0 * Math.Log10(peak);
    }
}
