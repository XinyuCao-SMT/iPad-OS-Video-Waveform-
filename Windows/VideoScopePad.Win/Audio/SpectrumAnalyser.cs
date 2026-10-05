//
//  SpectrumAnalyser.cs
//  VideoScopePad.Win
//
//  1/3 倍频程频谱（音频套件的"频谱"视图）。纯 DSP，不需要硬件，所以先用它把频谱这条链钉死。
//
//  为什么用 Goertzel 而不是 FFT：
//    · 视角只需要 ~31 条 1/3 倍频程带，逐带做 DFT 比"整段 FFT + 归并"更直接；
//    · 不需要 2 的幂长度、不需要窗函数的边界处理，代码短、结果可核对；
//    · 每条带在带内取几个探针频率求能量和（单点探针会漏掉带内偏心的分量）。
//
//  带中心频率取 ISO 266 / IEC 61260 的**标称** 1/3 倍频程中心（20 Hz…20 kHz 共 31 条），
//  带边界 = 中心 × 2^(±1/6)。
//

namespace VideoScopePad.Win.Audio;

/// <summary>一条 1/3 倍频程带</summary>
public readonly record struct SpectrumBand(double CenterHz, double LowHz, double HighHz, double Dbfs);

/// <summary>1/3 倍频程频谱分析</summary>
public static class SpectrumAnalyser
{
    /// <summary>
    /// ISO 1/3 倍频程标称中心频率（20 Hz–20 kHz，31 条）。
    /// 标称值是"好记的数字"（1.25 / 1.6 / 2 / 2.5 / 3.15…），不是 10^(n/10) 的精确值 ——
    /// 与声学仪表的刻度一致，别自己算一遍"更精确的"。
    /// </summary>
    public static readonly double[] ThirdOctaveCenters =
    {
        20, 25, 31.5, 40, 50, 63, 80, 100, 125, 160,
        200, 250, 315, 400, 500, 630, 800, 1000, 1250, 1600,
        2000, 2500, 3150, 4000, 5000, 6300, 8000, 10000, 12500, 16000,
        20000,
    };

    /// <summary>带内探针数（每条带取几个频率点求能量和）</summary>
    public const int ProbesPerBand = 5;

    public static double LowEdge(double centerHz) => centerHz / Math.Pow(2.0, 1.0 / 6.0);

    public static double HighEdge(double centerHz) => centerHz * Math.Pow(2.0, 1.0 / 6.0);

    /// <summary>
    /// 分析一段单通道样本的 1/3 倍频程频谱。
    /// 返回每条带的 dBFS（带内平均功率相对满刻度的 dB；静音给 −240 地板值）。
    /// </summary>
    public static IReadOnlyList<SpectrumBand> Analyse(float[] samples, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Length < 64 || sampleRate <= 0)
        {
            return ThirdOctaveCenters
                .Select(c => new SpectrumBand(c, LowEdge(c), HighEdge(c), -240.0))
                .ToArray();
        }

        var bands = new List<SpectrumBand>(ThirdOctaveCenters.Length);
        double nyquist = sampleRate / 2.0;

        foreach (double center in ThirdOctaveCenters)
        {
            double low = LowEdge(center);
            double high = HighEdge(center);

            // 超出奈奎斯特的带（低采样率时的高频段）：给地板值，但**保留带宽信息**，
            // 让界面能显示"这条带本次采样率下看不到"，而不是假装它是静音。
            if (low >= nyquist)
            {
                bands.Add(new SpectrumBand(center, low, high, -240.0));
                continue;
            }

            high = Math.Min(high, nyquist * 0.98);
            double power = 0;
            for (int probe = 0; probe < ProbesPerBand; probe++)
            {
                double t = ProbesPerBand == 1 ? 0.5 : probe / (double)(ProbesPerBand - 1);
                double frequency = low + (high - low) * t;
                power += GoertzelPower(samples, sampleRate, frequency);
            }
            power /= ProbesPerBand;

            bands.Add(new SpectrumBand(center, low, high, ToDbfs(power)));
        }

        return bands;
    }

    /// <summary>多通道：先按通道平均功率合成单通道（频谱看的是"整体能量分布"）</summary>
    public static IReadOnlyList<SpectrumBand> Analyse(IReadOnlyList<float[]> channels, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(channels);
        if (channels.Count == 0)
        {
            return Analyse(Array.Empty<float>(), sampleRate);
        }
        if (channels.Count == 1)
        {
            return Analyse(channels[0], sampleRate);
        }

        int count = channels.Min(c => c.Length);
        var mixed = new float[count];
        for (int i = 0; i < count; i++)
        {
            double sum = 0;
            foreach (float[] channel in channels)
            {
                sum += channel[i];
            }
            mixed[i] = (float)(sum / channels.Count);
        }
        return Analyse(mixed, sampleRate);
    }

    /// <summary>Goertzel：单个频率点的功率（归一化到"满刻度正弦 = 1.0"）</summary>
    private static double GoertzelPower(float[] samples, int sampleRate, double frequency)
    {
        double omega = 2.0 * Math.PI * frequency / sampleRate;
        double coefficient = 2.0 * Math.Cos(omega);
        double s0 = 0, s1 = 0, s2 = 0;
        foreach (float sample in samples)
        {
            s0 = sample + coefficient * s1 - s2;
            s2 = s1;
            s1 = s0;
        }

        double real = s1 - s2 * Math.Cos(omega);
        double imaginary = s2 * Math.Sin(omega);
        return (real * real + imaginary * imaginary) / (samples.Length * (double)samples.Length);
    }

    private static double ToDbfs(double power) => power <= 1e-24 ? -240.0 : 10.0 * Math.Log10(power);
}
