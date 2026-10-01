//
//  SignalMeasurement.cs
//  VideoScopePad.Win
//
//  信号幅度读数 —— 与 iPad 版 Render/SignalMeasurement.swift 逐条对应
//  （同一套统计口径；两平台读数必须一致，否则"同一个信号两台机器读数不同"就是 bug）。
//
//  数据来源：ScopeEngine.EncodeMeasurement 回读的测量直方图（GPU 累加），布局：
//      [0..255]     R 的 256 个 bin
//      [256..511]   G
//      [512..767]   B
//      [768..1023]  Y（亮度，vsLuma 加权）
//      [1024..1087] 色度径向 bin（64 个，0 = 灰，1 = 100% 饱和度）
//
//  ⚠️ 量程口径：iPad 版用 isVideoRange 区分 16–235 / 0–255；**Windows 侧恒为全范围**
//     —— 采集链在入口就把 limited 展开成 full（Capture/YuvFrameConverter.cs），
//     直方图也是按展开后的码值分箱的。所以 ire(code) = code/255*100。
//     （刻度层那条一样的规矩，见 App/ScopeGraticule.cs 的说明。）
//
//  ⚠️ 「峰值」与「稳定值」的区别（读数好不好用的关键）：
//     峰值 = 最高/最低的**非空 bin** —— 一个孤立噪声点就能把它顶到 100 IRE；
//     稳定值 = 从两端往里累加到总量的 0.1% 才认，抗单点噪声。
//     所以界面主读数两个并列显示（iPad 版也是这么做的）。
//

namespace VideoScopePad.Win.Render;

/// <summary>一帧的幅度读数（IRE；色度是百分比）。</summary>
public sealed record SignalMeasurement
{
    public double PeakWhiteIre { get; init; }
    public double StableWhiteIre { get; init; }
    public double BlackLevelIre { get; init; }
    public double StableBlackIre { get; init; }
    public double AverageIre { get; init; }
    public double RedPeakIre { get; init; }
    public double GreenPeakIre { get; init; }
    public double BluePeakIre { get; init; }

    /// <summary>色度峰值（0…100%，径向直方图里最高的非空 bin）</summary>
    public double PeakSaturationPercent { get; init; }

    /// <summary>超白像素占比（%）</summary>
    public double AboveWhitePercent { get; init; }

    /// <summary>超黑像素占比（%）</summary>
    public double BelowBlackPercent { get; init; }

    public long SampledPixels { get; init; }

    /// <summary>动态范围（稳定白 − 稳定黑）</summary>
    public double DynamicRangeIre => StableWhiteIre - StableBlackIre;

    public bool HasData => SampledPixels > 0;

    public static SignalMeasurement Empty { get; } = new();
}

/// <summary>从测量直方图算读数（与 iPad 版 SignalMeasurementBuilder.make 同一套规则）。</summary>
public static class SignalMeasurementBuilder
{
    /// <summary>这个码值以上的像素算超白</summary>
    private const int WhiteThreshold = 254;

    /// <summary>这个码值以下的像素算超黑</summary>
    private const int BlackThreshold = 1;

    /// <summary>稳定值的分位（0.1%）</summary>
    private const double TailFraction = 0.001;

    public static SignalMeasurement? FromMeasureBuffer(uint[] counts)
    {
        ArgumentNullException.ThrowIfNull(counts);

        int bins = ShaderConstants.MeasureBins;
        int required = ShaderConstants.MeasurePlanes * bins + ShaderConstants.MeasureRadialBins;
        if (counts.Length < required || bins < 2)
        {
            return null;
        }

        // 亮度平面（3）决定总量与总体读数
        PlaneStats luma = Stats(counts, offset: 3 * bins, bins);
        if (luma.Total == 0)
        {
            return null;
        }

        PlaneStats red = Stats(counts, 0, bins);
        PlaneStats green = Stats(counts, bins, bins);
        PlaneStats blue = Stats(counts, 2 * bins, bins);

        return new SignalMeasurement
        {
            PeakWhiteIre = Ire(luma.PeakCode),
            StableWhiteIre = Ire(luma.StablePeakCode),
            BlackLevelIre = Ire(luma.BlackCode),
            StableBlackIre = Ire(luma.StableBlackCode),
            AverageIre = Ire(luma.MeanCode),
            RedPeakIre = Ire(red.StablePeakCode),
            GreenPeakIre = Ire(green.StablePeakCode),
            BluePeakIre = Ire(blue.StablePeakCode),
            PeakSaturationPercent = Saturation(counts),
            AboveWhitePercent = Percent(luma.HighCount, luma.Total),
            BelowBlackPercent = Percent(luma.LowCount, luma.Total),
            SampledPixels = luma.Total,
        };
    }

    /// <summary>码值 → IRE（全范围：0 → 0 IRE、255 → 100 IRE）</summary>
    public static double Ire(double code) => code / 255.0 * 100.0;

    private static double Percent(long part, long total) => total > 0 ? part * 100.0 / total : 0.0;

    private readonly record struct PlaneStats(
        double PeakCode, double StablePeakCode, double BlackCode, double StableBlackCode,
        double MeanCode, long Total, long HighCount, long LowCount);

    private static PlaneStats Stats(uint[] counts, int offset, int bins)
    {
        long total = 0;
        for (int i = 0; i < bins; i++)
        {
            total += counts[offset + i];
        }
        if (total == 0)
        {
            return default;
        }

        // 峰值：最高的非空 bin
        int peak = 0;
        for (int index = bins - 1; index >= 0; index--)
        {
            if (counts[offset + index] > 0)
            {
                peak = index;
                break;
            }
        }

        // 黑位：最低的非空 bin
        int black = bins - 1;
        for (int index = 0; index < bins; index++)
        {
            if (counts[offset + index] > 0)
            {
                black = index;
                break;
            }
        }

        // 稳定值：掐掉两端的 0.1%
        long tail = Math.Max(1, (long)(total * TailFraction));

        long cumulativeHigh = 0;
        int stablePeak = peak;
        for (int index = bins - 1; index >= 0; index--)
        {
            cumulativeHigh += counts[offset + index];
            if (cumulativeHigh >= tail)
            {
                stablePeak = index;
                break;
            }
        }

        long cumulativeLow = 0;
        int stableBlack = black;
        for (int index = 0; index < bins; index++)
        {
            cumulativeLow += counts[offset + index];
            if (cumulativeLow >= tail)
            {
                stableBlack = index;
                break;
            }
        }

        double weighted = 0;
        for (int index = 0; index < bins; index++)
        {
            uint count = counts[offset + index];
            if (count > 0)
            {
                weighted += (double)index * count;
            }
        }

        long high = 0;
        for (int index = WhiteThreshold + 1; index < bins; index++)
        {
            high += counts[offset + index];
        }

        long low = 0;
        for (int index = 0; index < Math.Clamp(BlackThreshold, 0, bins); index++)
        {
            low += counts[offset + index];
        }

        return new PlaneStats(peak, stablePeak, black, stableBlack, weighted / total, total, high, low);
    }

    /// <summary>色度峰值：径向直方图里最高的非空 bin（0…1 → 百分比）</summary>
    private static double Saturation(uint[] counts)
    {
        int radialBins = ShaderConstants.MeasureRadialBins;
        int offset = ShaderConstants.MeasurePlanes * ShaderConstants.MeasureBins;
        if (offset + radialBins > counts.Length)
        {
            return 0;
        }

        for (int index = radialBins - 1; index >= 0; index--)
        {
            if (counts[offset + index] > 0)
            {
                return index / (double)(radialBins - 1) * 100.0;
            }
        }
        return 0;
    }
}

/// <summary>
/// 峰值保持状态（与 iPad 版 PeakHoldTracker 同一套语义）：
///   新峰值**立即钉住**，保持 <see cref="HoldSeconds"/> 秒不动，之后按 <see cref="DecayIrePerSecond"/> 衰减。
/// 为什么要保持再衰减：广播监视器看的是"刚过去这段有没有冒过白"，
/// 直接跟随实时峰值会一直抖，纯保持又看不出什么时候降下来了。
/// </summary>
public sealed class PeakHoldTracker
{
    /// <summary>保持时长（秒）</summary>
    public double HoldSeconds { get; init; } = 3.0;

    /// <summary>衰减速度（IRE/秒）</summary>
    public double DecayIrePerSecond { get; init; } = 12.0;

    public double WhitePeakIre { get; private set; }
    public double BlackFloorIre { get; private set; }
    public double RedPeakIre { get; private set; }
    public double GreenPeakIre { get; private set; }
    public double BluePeakIre { get; private set; }
    public bool HasData { get; private set; }

    private double _whiteHoldUntil;
    private double _blackHoldUntil;
    private double _lastTime;

    /// <summary>用一帧读数更新（<paramref name="now"/> 为秒，自渲染线程计时）</summary>
    public void Update(SignalMeasurement measurement, double now)
    {
        if (!measurement.HasData)
        {
            return;
        }

        double delta = _lastTime > 0 ? Math.Max(now - _lastTime, 0) : 0;
        _lastTime = now;

        if (!HasData)
        {
            WhitePeakIre = measurement.PeakWhiteIre;
            BlackFloorIre = measurement.BlackLevelIre;
            RedPeakIre = measurement.RedPeakIre;
            GreenPeakIre = measurement.GreenPeakIre;
            BluePeakIre = measurement.BluePeakIre;
            _whiteHoldUntil = now + HoldSeconds;
            _blackHoldUntil = now + HoldSeconds;
            HasData = true;
            return;
        }

        // 白峰：创新高立即钉住；否则保持期内不动，过期后按 IRE/秒 衰减
        if (measurement.PeakWhiteIre >= WhitePeakIre)
        {
            WhitePeakIre = measurement.PeakWhiteIre;
            _whiteHoldUntil = now + HoldSeconds;
        }
        else if (now > _whiteHoldUntil)
        {
            WhitePeakIre = Math.Max(measurement.PeakWhiteIre,
                                    WhitePeakIre - DecayIrePerSecond * delta);
        }

        // 黑位：创新低立即钉住；否则同样保持后向上衰减
        if (measurement.BlackLevelIre <= BlackFloorIre)
        {
            BlackFloorIre = measurement.BlackLevelIre;
            _blackHoldUntil = now + HoldSeconds;
        }
        else if (now > _blackHoldUntil)
        {
            BlackFloorIre = Math.Min(measurement.BlackLevelIre,
                                     BlackFloorIre + DecayIrePerSecond * delta);
        }

        RedPeakIre = Math.Max(RedPeakIre, measurement.RedPeakIre);
        GreenPeakIre = Math.Max(GreenPeakIre, measurement.GreenPeakIre);
        BluePeakIre = Math.Max(BluePeakIre, measurement.BluePeakIre);
    }

    public void Reset()
    {
        WhitePeakIre = 0;
        BlackFloorIre = 0;
        RedPeakIre = 0;
        GreenPeakIre = 0;
        BluePeakIre = 0;
        HasData = false;
        _whiteHoldUntil = 0;
        _blackHoldUntil = 0;
        _lastTime = 0;
    }

    /// <summary>快照（界面读它画游标；避免读到更新到一半的状态）</summary>
    public PeakHoldState Snapshot() => new(
        WhitePeakIre, BlackFloorIre, RedPeakIre, GreenPeakIre, BluePeakIre, HasData);
}

/// <summary>峰值保持的不可变快照</summary>
public readonly record struct PeakHoldState(
    double WhitePeakIre,
    double BlackFloorIre,
    double RedPeakIre,
    double GreenPeakIre,
    double BluePeakIre,
    bool HasData);
