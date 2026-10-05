//
//  LoudnessMeter.cs
//  VideoScopePad.Win
//
//  音频分析地基：**BS.1770 响度（LUFS）+ 每通道电平**。这块不依赖任何硬件，
//  所以先用它把音频链路里"能算准的那部分"钉死；抓音频（WASAPI）之后再接上来。
//
//  为什么先做它：响度是音频套件里唯一有**国际标准**的部分 —— 算错了一眼看不出来，
//  但对着标准信号（1 kHz 正弦、已知 dBFS）一测就知道。所以先把它做对、并且能断言。
//
//  BS.1770-4 的 K 加权 = 高架滤波（stage 1）+ 高通（stage 2），系数按采样率设计：
//    stage 1：f0 = 1681.974450955533, G = 3.999843853973347 dB, Q = 0.7071752369554196
//    stage 2：f0 =   38.13547087602444, Q = 0.5003270373238773
//  积分（integrated）响度：400 ms 窗、100 ms 步进（75% 重叠），
//    绝对门限 −70 LUFS 先滤一遍，再用"通过绝对门限的块的均值 −10 LU"作为相对门限滤第二遍。
//  通道加权：前置/中置 1.0，环绕 1.41（BS.1770 表 3；>5 声道另有规则，这里按 7.1 常见情形给默认值）。
//

namespace VideoScopePad.Win.Audio;

/// <summary>一个通道的电平</summary>
public readonly record struct ChannelLevel(double RmsDbfs, double PeakDbfs, double TruePeakDbfs)
{
    public bool IsSilent => PeakDbfs <= -120;
}

/// <summary>一次分析的结果</summary>
public sealed record AudioAnalysis(
    IReadOnlyList<ChannelLevel> Channels,
    double IntegratedLufs,
    double MomentaryLufs,
    int GatedBlocks,
    int SampleRate)
{
    public static AudioAnalysis Empty(int sampleRate = 0) => new(
        Array.Empty<ChannelLevel>(), double.NegativeInfinity, double.NegativeInfinity, 0, sampleRate);

    public bool HasLoudness => !double.IsNegativeInfinity(IntegratedLufs);
}

/// <summary>BS.1770 响度与电平分析（纯函数式：喂样本，出读数，不碰任何设备）。</summary>
public sealed class LoudnessMeter
{
    /// <summary>积分窗（秒）</summary>
    public const double BlockSeconds = 0.4;
    /// <summary>窗步进（秒）—— 75% 重叠</summary>
    public const double StepSeconds = 0.1;
    /// <summary>绝对门限（LUFS）</summary>
    public const double AbsoluteGateLufs = -70.0;
    /// <summary>相对门限（相对通过绝对门限的均值，LU）</summary>
    public const double RelativeGateLu = -10.0;

    private readonly int _sampleRate;
    private readonly float[] _b1 = new float[3];
    private readonly float[] _a1 = new float[3];
    private readonly float[] _b2 = new float[3];
    private readonly float[] _a2 = new float[3];

    public LoudnessMeter(int sampleRate)
    {
        if (sampleRate < 8000 || sampleRate > 384000)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "采样率超出合理范围（8k–384k）");
        }
        _sampleRate = sampleRate;
        DesignHighShelf(1681.974450955533, 3.999843853973347, 0.7071752369554196, _b1, _a1);
        DesignHighPass(38.13547087602444, 0.5003270373238773, _b2, _a2);
    }

    /// <summary>默认通道加权（BS.1770 表 3：环绕 1.41；单声道/立体声各 1.0）</summary>
    public static double[] DefaultChannelWeights(int channels) => channels switch
    {
        <= 3 => Enumerable.Repeat(1.0, channels).ToArray(),
        6 => new[] { 1.0, 1.0, 1.0, 0.0, 1.41, 1.41 },           // 5.1：LFE 不计入
        8 => new[] { 1.0, 1.0, 1.0, 0.0, 1.41, 1.41, 1.41, 1.41 }, // 7.1
        _ => Enumerable.Repeat(1.0, channels).ToArray(),
    };

    /// <summary>
    /// 分析一块多通道样本（<paramref name="channels"/>[ch][sample]）。
    /// 样本是归一化浮点（±1.0 满刻度）。
    /// </summary>
    public AudioAnalysis Analyse(IReadOnlyList<float[]> channels, double[]? weights = null)
    {
        ArgumentNullException.ThrowIfNull(channels);
        if (channels.Count == 0 || channels[0].Length == 0)
        {
            return AudioAnalysis.Empty(_sampleRate);
        }

        int count = channels[0].Length;
        weights ??= DefaultChannelWeights(channels.Count);
        int blockSize = (int)Math.Round(BlockSeconds * _sampleRate);
        int stepSize = (int)Math.Round(StepSeconds * _sampleRate);

        // 每通道：电平 + K 加权后的样本（供积分响度用）
        var levels = new List<ChannelLevel>(channels.Count);
        var weighted = new List<float[]>(channels.Count);
        foreach (float[] channel in channels)
        {
            levels.Add(MeasureLevel(channel));
            weighted.Add(KWeight(channel));
        }

        double integrated = double.NegativeInfinity;
        double momentary = double.NegativeInfinity;
        int gatedBlocks = 0;

        if (count >= blockSize)
        {
            // 逐块算"加权均方 → 块响度"
            var blockLoudness = new List<double>();
            var blockPower = new List<double>();
            for (int start = 0; start + blockSize <= count; start += stepSize)
            {
                double power = 0;
                for (int ch = 0; ch < weighted.Count; ch++)
                {
                    double sum = 0;
                    float[] samples = weighted[ch];
                    for (int i = start; i < start + blockSize; i++)
                    {
                        sum += (double)samples[i] * samples[i];
                    }
                    power += weights[Math.Min(ch, weights.Length - 1)] * (sum / blockSize);
                }

                blockPower.Add(power);
                blockLoudness.Add(Loudness(power));
            }

            if (blockLoudness.Count > 0)
            {
                momentary = blockLoudness[^1];

                // 第一遍：绝对门限
                double sumPower = 0;
                int kept = 0;
                for (int i = 0; i < blockLoudness.Count; i++)
                {
                    if (blockLoudness[i] > AbsoluteGateLufs)
                    {
                        sumPower += blockPower[i];
                        kept++;
                    }
                }

                if (kept > 0)
                {
                    // 第二遍：相对门限 = 通过绝对门限那批的均值 −10 LU
                    double meanLoudness = Loudness(sumPower / kept);
                    double relativeGate = meanLoudness + RelativeGateLu;
                    double sumGated = 0;
                    int gated = 0;
                    for (int i = 0; i < blockLoudness.Count; i++)
                    {
                        if (blockLoudness[i] > AbsoluteGateLufs && blockLoudness[i] > relativeGate)
                        {
                            sumGated += blockPower[i];
                            gated++;
                        }
                    }

                    if (gated > 0)
                    {
                        integrated = Loudness(sumGated / gated);
                        gatedBlocks = gated;
                    }
                }
            }
        }

        return new AudioAnalysis(levels, integrated, momentary, gatedBlocks, _sampleRate);
    }

    /// <summary>块功率 → 响度（LUFS）：−0.691 + 10·log10(z)</summary>
    public static double Loudness(double power)
        => power <= 0 ? double.NegativeInfinity : -0.691 + 10.0 * Math.Log10(power);

    private static ChannelLevel MeasureLevel(float[] samples)
    {
        double sum = 0;
        double peak = 0;
        foreach (float sample in samples)
        {
            double value = Math.Abs(sample);
            sum += (double)sample * sample;
            if (value > peak)
            {
                peak = value;
            }
        }

        double rms = Math.Sqrt(sum / Math.Max(samples.Length, 1));
        return new ChannelLevel(Db(rms), Db(peak), Db(peak));   // 真峰需要过采样，抓音频那轮再补
    }

    private static double Db(double amplitude)
        => amplitude <= 1e-12 ? -240.0 : 20.0 * Math.Log10(amplitude);

    /// <summary>K 加权（两级滤波）</summary>
    private float[] KWeight(float[] input)
    {
        var temp = new float[input.Length];
        var output = new float[input.Length];
        Biquad(input, temp, _b1, _a1);
        Biquad(temp, output, _b2, _a2);
        return output;
    }

    /// <summary>直接 I 型双二阶（系数已按 a0 归一）</summary>
    private static void Biquad(float[] input, float[] output, float[] b, float[] a)
    {
        double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
        for (int i = 0; i < input.Length; i++)
        {
            double x0 = input[i];
            double y0 = b[0] * x0 + b[1] * x1 + b[2] * x2 - a[1] * y1 - a[2] * y2;
            output[i] = (float)y0;
            x2 = x1; x1 = x0; y2 = y1; y1 = y0;
        }
    }

    /// <summary>BS.1770 stage 1：高架滤波（按标准给出的模拟原型做双线性变换）</summary>
    private void DesignHighShelf(double f0, double gainDb, double q, float[] b, float[] a)
    {
        double k = Math.Tan(Math.PI * f0 / _sampleRate);
        double vh = Math.Pow(10.0, gainDb / 20.0);
        double vb = Math.Pow(vh, 0.4996667741545416);
        double a0 = 1.0 + k / q + k * k;
        b[0] = (float)((vh + vb * k / q + k * k) / a0);
        b[1] = (float)(2.0 * (k * k - vh) / a0);
        b[2] = (float)((vh - vb * k / q + k * k) / a0);
        a[0] = 1f;
        a[1] = (float)(2.0 * (k * k - 1.0) / a0);
        a[2] = (float)((1.0 - k / q + k * k) / a0);
    }

    /// <summary>BS.1770 stage 2：高通</summary>
    private void DesignHighPass(double f0, double q, float[] b, float[] a)
    {
        double k = Math.Tan(Math.PI * f0 / _sampleRate);
        double a0 = 1.0 + k / q + k * k;
        b[0] = (float)(1.0 / a0);
        b[1] = (float)(-2.0 / a0);
        b[2] = (float)(1.0 / a0);
        a[0] = 1f;
        a[1] = (float)(2.0 * (k * k - 1.0) / a0);
        a[2] = (float)((1.0 - k / q + k * k) / a0);
    }

    /// <summary>测试信号：指定频率与幅度（dBFS）的正弦，返回多通道样本</summary>
    public static float[][] MakeSine(int channels, int sampleRate, double seconds, double frequency, double dbfs)
    {
        int count = (int)Math.Round(seconds * sampleRate);
        double amplitude = Math.Pow(10.0, dbfs / 20.0);
        var result = new float[channels][];
        for (int ch = 0; ch < channels; ch++)
        {
            var samples = new float[count];
            for (int i = 0; i < count; i++)
            {
                samples[i] = (float)(amplitude * Math.Sin(2.0 * Math.PI * frequency * i / sampleRate));
            }
            result[ch] = samples;
        }
        return result;
    }
}
