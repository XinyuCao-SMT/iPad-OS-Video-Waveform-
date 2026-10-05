//
//  GoniometerAnalyser.cs
//  VideoScopePad.Win
//
//  声相（goniometer / 李萨如）：把选定的一对声道画成 XY 图。
//     x = (L − R)/√2（右为正）　y = (L + R)/√2（上为正；单声道信号落在正上方）
//  同时给出相关系数（+1 = 完全同相单声道，0 = 无关，−1 = 完全反相）——
//  现场最常看的两个数就是"图偏不偏"和"相关性正不正"。
//
//  纯 DSP，不依赖硬件：这一层做好并验过，接 WASAPI 之后只是把样本喂进来。
//

namespace VideoScopePad.Win.Audio;

/// <summary>声相分析结果</summary>
public sealed record GoniometerResult(
    IReadOnlyList<(float X, float Y)> Points,
    double Correlation,
    double PeakRadius,
    double RmsLevelDbfs,
    long SampleCount)
{
    public static GoniometerResult Empty { get; } =
        new(Array.Empty<(float, float)>(), 0, 0, -240, 0);

    /// <summary>图上有没有内容（全静音时界面要显示"无信号"而不是一个点）</summary>
    public bool HasSignal => RmsLevelDbfs > -80;
}

public static class GoniometerAnalyser
{
    /// <summary>最多保留多少个点（界面画点用；多了只是浪费）</summary>
    public const int MaxPoints = 2048;

    /// <summary>
    /// 分析一对声道。返回下采样后的点集（x/y ∈ −1…1，已按峰值归一化到 0.9 以内便于显示），
    /// 相关系数、峰值半径与 RMS 电平。
    /// </summary>
    public static GoniometerResult Analyse(float[] left, float[] right, int maxPoints = MaxPoints)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        int count = Math.Min(left.Length, right.Length);
        if (count == 0 || maxPoints <= 0)
        {
            return GoniometerResult.Empty;
        }

        double sumLr = 0, sumLl = 0, sumRr = 0;
        double peakRadius = 0;
        for (int i = 0; i < count; i++)
        {
            double l = left[i], r = right[i];
            sumLr += l * r;
            sumLl += l * l;
            sumRr += r * r;

            double x = (l - r) / Math.Sqrt(2.0);
            double y = (l + r) / Math.Sqrt(2.0);
            // ⚠️ 峰值半径要逐点算 √(x²+y²) 再取最大，**不能**用 √(max|x|² + max|y|²)：
            //    后者在 90° 相位差（圆周）时给出 √2 而不是 1 —— 归一化被放大，圆周就被画小了
            //    （实测画到 0.636 而不是 0.9；这个 bug 就是被自检里那条"接近圆周"抓出来的）。
            double radius = Math.Sqrt(x * x + y * y);
            if (radius > peakRadius) { peakRadius = radius; }
        }

        double denominator = Math.Sqrt(sumLl * sumRr);
        double correlation = denominator <= 1e-12 ? 0 : sumLr / denominator;
        double rms = Math.Sqrt((sumLl + sumRr) / (2.0 * count));
        double scale = peakRadius <= 1e-9 ? 0 : 0.9 / peakRadius;

        int stride = Math.Max(count / maxPoints, 1);
        var points = new List<(float, float)>(Math.Min(maxPoints, count / stride + 1));
        for (int i = 0; i < count; i += stride)
        {
            double x = (left[i] - right[i]) / Math.Sqrt(2.0) * scale;
            double y = (left[i] + right[i]) / Math.Sqrt(2.0) * scale;
            points.Add(((float)x, (float)y));
        }

        return new GoniometerResult(
            points,
            correlation,
            peakRadius,
            rms <= 1e-12 ? -240 : 20.0 * Math.Log10(rms),
            count);
    }

    /// <summary>分析多通道里的某一对（声道号从 0 起；越界时返回空结果）</summary>
    public static GoniometerResult Analyse(IReadOnlyList<float[]> channels, int leftChannel, int rightChannel)
    {
        ArgumentNullException.ThrowIfNull(channels);
        if (leftChannel < 0 || rightChannel < 0
            || leftChannel >= channels.Count || rightChannel >= channels.Count)
        {
            return GoniometerResult.Empty;
        }
        return Analyse(channels[leftChannel], channels[rightChannel]);
    }

    /// <summary>造一对相位差为 <paramref name="phaseDegrees"/> 的同频正弦（用来验相关性）</summary>
    public static (float[] Left, float[] Right) MakePair(int sampleRate, double seconds, double frequency,
                                                        double dbfs, double phaseDegrees)
    {
        int count = (int)Math.Round(seconds * sampleRate);
        double amplitude = Math.Pow(10.0, dbfs / 20.0);
        var left = new float[count];
        var right = new float[count];
        double phase = phaseDegrees * Math.PI / 180.0;
        for (int i = 0; i < count; i++)
        {
            double t = 2.0 * Math.PI * frequency * i / sampleRate;
            left[i] = (float)(amplitude * Math.Sin(t));
            right[i] = (float)(amplitude * Math.Sin(t + phase));
        }
        return (left, right);
    }
}
