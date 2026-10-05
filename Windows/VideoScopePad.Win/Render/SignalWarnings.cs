//
//  SignalWarnings.cs
//  VideoScopePad.Win
//
//  超标报警：规则判据 + 边沿触发的锁存。逐条移植 iPad 版
//  `Render/SignalMeasurement.swift` 里的 make() 判定与 WarningLatch。
//
//  为什么要有锁存：一闪而过的超范围（比如切换镜头那一两帧）也要能看到，
//  但每帧都报又会闪得没法看 —— 所以「同一问题连续命中 N 次才算确认，
//  确认后持续显示，连续消失 M 次才解除」。
//

namespace VideoScopePad.Win.Render;

/// <summary>超标判定的规则（纯函数，便于用构造出来的读数直接断言）。</summary>
public static class SignalMeasurementRules
{
    /// <summary>
    /// 百分比类判据的门槛（%）。与 iPad 版一致：**0.05%**（不是 0.5%）——
    /// 差一个数量级会让"只有几个像素超白"被吞掉，校色时这种小面积超白恰恰最要命。
    /// </summary>
    public const double PercentThreshold = 0.05;

    /// <summary>按 iPad 版同一套规则给出报警项（空 = 正常）。</summary>
    public static IReadOnlyList<string> Evaluate(SignalMeasurement measurement,
                                                 double percentThreshold = PercentThreshold)
    {
        ArgumentNullException.ThrowIfNull(measurement);

        var warnings = new List<string>();

        bool blackFrame = measurement.StableWhiteIre < 3 && measurement.AverageIre < 1;
        if (blackFrame)
        {
            warnings.Add("整帧全黑");
        }

        if (measurement.AboveWhitePercent > percentThreshold)
        {
            warnings.Add($"超白 {measurement.AboveWhitePercent:0.00}%");
        }

        if (measurement.BelowBlackPercent > percentThreshold)
        {
            warnings.Add($"超黑 {measurement.BelowBlackPercent:0.00}%");
        }

        if (measurement.StableWhiteIre > 103)
        {
            warnings.Add($"白电平偏高 {measurement.StableWhiteIre:0} IRE");
        }

        if (measurement.StableBlackIre < -2)
        {
            warnings.Add($"黑位被压缩 {measurement.StableBlackIre:0} IRE");
        }

        if (measurement.StableBlackIre > 8)
        {
            warnings.Add($"黑位抬高 {measurement.StableBlackIre:0} IRE");
        }

        if (measurement.PeakSaturationPercent > 105)
        {
            warnings.Add($"色度超范围 {measurement.PeakSaturationPercent:0}%");
        }

        return warnings;
    }
}

/// <summary>
/// 报警锁存（逐条对应 iPad 版 WarningLatch.update 的语义）：
///   · 命中一次计数 +1；连续命中到 raiseThreshold 才「确认」并进入显示列表；
///   · 未命中的一次计数 -1（不减到负数）；
///   · 已确认的项，只要计数还 > clearThreshold 就继续显示（默认 0 = 计数归零才解除）；
///   · didRaise 只在「从未确认 → 确认」的那一次为 true（界面拿它做一次红框闪/提示音）。
/// </summary>
public sealed class WarningLatch
{
    private readonly Dictionary<string, int> _counters = new(StringComparer.Ordinal);
    private List<string> _active = new();

    /// <summary>当前处于确认状态的报警项（已排序，便于比对与显示）</summary>
    public IReadOnlyList<string> ActiveWarnings => _active;

    /// <summary>本次 Update 是否出现了新的确认事件（边沿）</summary>
    public bool DidRaise { get; private set; }

    /// <summary>累计确认次数（界面可以拿它做"闪烁"计数，避免逐帧重绘）</summary>
    public int RaiseCount { get; private set; }

    public void Reset()
    {
        _active = new List<string>();
        _counters.Clear();
        DidRaise = false;
    }

    public IReadOnlyList<string> Update(IReadOnlyList<string> warnings, int raiseThreshold, int clearThreshold = 0)
    {
        ArgumentNullException.ThrowIfNull(warnings);

        DidRaise = false;

        var current = new HashSet<string>(warnings, StringComparer.Ordinal);
        int raise = Math.Max(raiseThreshold, 1);
        int clear = Math.Max(clearThreshold, 0);

        // 先在快照上算计数，最后整体替换 —— 避免"边遍历边改字典"
        var updated = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string key in _counters.Keys.Union(current, StringComparer.Ordinal))
        {
            int count = _counters.TryGetValue(key, out int existing) ? existing : 0;
            if (current.Contains(key))
            {
                updated[key] = count + 1;
            }
            else
            {
                int next = Math.Max(0, count - 1);
                if (next > 0)
                {
                    updated[key] = next;
                }
            }
        }

        var active = new List<string>();
        foreach ((string key, int count) in updated)
        {
            if (count >= raise)
            {
                active.Add(key);
                if (!_active.Contains(key, StringComparer.Ordinal))
                {
                    DidRaise = true;
                    RaiseCount++;
                }
            }
            else if (count > clear && _active.Contains(key, StringComparer.Ordinal))
            {
                active.Add(key);      // 还没消到门槛以下，保持显示
            }
        }

        _counters.Clear();
        foreach ((string key, int count) in updated)
        {
            _counters[key] = count;
        }

        active.Sort(StringComparer.Ordinal);
        _active = active;
        return _active;
    }
}
