//
//  AvSyncAnalyser.cs
//  VideoScopePad.Win
//
//  声画延时（A/V Sync）：**每一轨单独测算**。用户明确要"显示每一轨单独的延时量，分别作测算" ——
//  所以这里按通道逐个找"音频起音"，再与画面变化时刻相减，得到每轨自己的延时。
//
//  做法（不依赖硬件，可对着构造信号断言）：
//    · 把每个通道切成小窗（默认 1 ms），算窗内 RMS 得到能量包络；
//    · 用"相对本通道峰值"的门槛找出**起音**（第一次从安静越过门槛，且之前若干窗是安静的）；
//    · 延时 = 音频起音时刻 − 画面变化时刻（正 = 声音比画面慢，负 = 声音比画面快）。
//
//  ⚠️ 这不是"互相相关"那种重算法，而是**事件对齐**：现场校准时信源会打一个"拍手/场记板"
//     （画面跳变 + 声音爆音同时发生），这种一次性事件用能量门槛又快又准。
//     连续素材要做的是另一件事（漂移测量），那是后续可以用同一套包络做互相关扩展的地方。
//

namespace VideoScopePad.Win.Audio;

/// <summary>一轨的声画延时结果</summary>
public readonly record struct TrackDelay(
    int Channel,
    double DelayMs,
    double OnsetLevelDbfs,
    bool HasOnset)
{
    /// <summary>这一轨是不是"没找到起音"（没响 / 太安静）—— 界面上要显示成"无信号"而不是 0 ms</summary>
    public bool IsMissing => !HasOnset;
}

/// <summary>声画延时分析</summary>
public static class AvSyncAnalyser
{
    /// <summary>包络窗长（毫秒）</summary>
    public const double WindowMs = 1.0;

    /// <summary>起音门槛：相对本通道峰值低多少 dB 算"越过"</summary>
    public const double OnsetThresholdDbBelowPeak = 12.0;

    /// <summary>起音前需要保持安静的窗数（避免把持续噪声当成起音）</summary>
    public const int QuietWindowsBefore = 5;

    /// <summary>
    /// 逐轨测算延时。<paramref name="videoChangeSeconds"/> 是画面变化时刻（相对同一时间基准）。
    /// </summary>
    public static IReadOnlyList<TrackDelay> Analyse(IReadOnlyList<float[]> channels,
                                                   int sampleRate,
                                                   double videoChangeSeconds)
    {
        ArgumentNullException.ThrowIfNull(channels);
        var results = new List<TrackDelay>(channels.Count);
        if (sampleRate <= 0)
        {
            return results;
        }

        int windowSize = Math.Max((int)Math.Round(WindowMs / 1000.0 * sampleRate), 1);
        for (int ch = 0; ch < channels.Count; ch++)
        {
            results.Add(AnalyseTrack(channels[ch], sampleRate, windowSize, videoChangeSeconds, ch));
        }
        return results;
    }

    private static TrackDelay AnalyseTrack(float[] samples, int sampleRate, int windowSize,
                                           double videoChangeSeconds, int channel)
    {
        int windows = samples.Length / windowSize;
        if (windows < QuietWindowsBefore + 2)
        {
            return new TrackDelay(channel, 0, -240, false);
        }

        // 能量包络（窗内 RMS → dBFS）
        var envelope = new double[windows];
        double peak = 0;
        for (int w = 0; w < windows; w++)
        {
            double sum = 0;
            int start = w * windowSize;
            for (int i = start; i < start + windowSize; i++)
            {
                sum += (double)samples[i] * samples[i];
            }
            double rms = Math.Sqrt(sum / windowSize);
            envelope[w] = rms <= 1e-12 ? -240.0 : 20.0 * Math.Log10(rms);
            if (envelope[w] > peak)
            {
                peak = envelope[w];
            }
        }

        if (peak <= -60.0)
        {
            return new TrackDelay(channel, 0, peak, false);   // 整轨基本没声音
        }

        double threshold = peak - OnsetThresholdDbBelowPeak;
        for (int w = QuietWindowsBefore; w < windows; w++)
        {
            if (envelope[w] < threshold)
            {
                continue;
            }

            bool quietBefore = true;
            for (int back = 1; back <= QuietWindowsBefore; back++)
            {
                if (envelope[w - back] >= threshold)
                {
                    quietBefore = false;
                    break;
                }
            }
            if (!quietBefore)
            {
                continue;
            }

            double onsetSeconds = w * windowSize / (double)sampleRate;
            return new TrackDelay(channel, (onsetSeconds - videoChangeSeconds) * 1000.0, envelope[w], true);
        }

        return new TrackDelay(channel, 0, peak, false);
    }

    /// <summary>
    /// 造一轨测试信号：静音 + 在指定时刻一个短促脉冲（模拟场记板/拍手）。
    /// 画面变化时刻由调用方给出，脉冲放在 <paramref name="clickSeconds"/>。
    /// </summary>
    public static float[] MakeClickTrack(int sampleRate, double seconds, double clickSeconds,
                                         double dbfs = -6.0, double clickMs = 5.0)
    {
        int count = (int)Math.Round(seconds * sampleRate);
        var samples = new float[count];
        int start = (int)Math.Round(clickSeconds * sampleRate);
        int length = (int)Math.Round(clickMs / 1000.0 * sampleRate);
        double amplitude = Math.Pow(10.0, dbfs / 20.0);
        for (int i = 0; i < length && start + i < count; i++)
        {
            // 短促的宽带脉冲（半个正弦包络 × 高频），比纯正弦更像"拍手"
            double env = Math.Sin(Math.PI * i / (double)length);
            samples[start + i] = (float)(amplitude * env * Math.Sin(2.0 * Math.PI * 3000.0 * i / sampleRate));
        }
        return samples;
    }
}
