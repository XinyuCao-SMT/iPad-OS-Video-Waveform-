//
//  CaptureRateMeter.cs
//  VideoScopePad.Win
//
//  「实测帧率」的算法（iPad 版顶栏里「声明 60 / 实测 59.9」那个实测值）。
//
//  为什么要自己算而不用声明帧率：采集卡常常做帧率转换，
//  声明 60 fps 实际只给 30 fps（USB 带宽不够时第一件事就是降帧率），
//  或者把 59.94 的源补成 60。界面上必须显示**真实收到的**帧率，
//  否则示波器的时间轴与真实信号对不上。
//
//  算法：只用**展示时间戳**（MF 的 hns，100 ns）算，不用墙上时钟 ——
//  时间戳是驱动给的，不受我们这边线程调度影响；用首尾两点算平均帧率，
//  同时统计相邻间隔的最小/最大值，能看出抖动（UVC 的等时传输抖动很常见）。
//

namespace VideoScopePad.Win.Capture;

/// <summary>实测帧率统计（滑动窗口）。</summary>
public sealed class CaptureRateMeter
{
    private readonly int _capacity;
    private readonly Queue<long> _timestamps = new();

    private long _firstTimestamp = long.MinValue;
    private long _lastTimestamp = long.MinValue;
    private long _minInterval = long.MaxValue;
    private long _maxInterval = long.MinValue;

    /// <summary>窗口内计数的帧数（含首帧）。</summary>
    public int SampleCount { get; private set; }

    public CaptureRateMeter(int capacity = 240)
    {
        if (capacity < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "滑动窗口至少要有 2 帧");
        }
        _capacity = capacity;
    }

    /// <summary>这一轮统计里最早一帧的时间戳（秒）。</summary>
    public double FirstTimestampSeconds => _firstTimestamp == long.MinValue ? 0 : _firstTimestamp / 10_000_000.0;

    public double LastTimestampSeconds => _lastTimestamp == long.MinValue ? 0 : _lastTimestamp / 10_000_000.0;

    /// <summary>首尾时间戳跨度（秒）—— 实测帧率的分母。</summary>
    public double ElapsedSeconds => SampleCount < 2 ? 0 : (_lastTimestamp - _firstTimestamp) / 10_000_000.0;

    /// <summary>实测帧率（帧/秒）；不足两帧返回 0。</summary>
    public double MeasuredFps
    {
        get
        {
            double elapsed = ElapsedSeconds;
            return elapsed > 0 ? (SampleCount - 1) / elapsed : 0.0;
        }
    }

    public double MinIntervalMs => _minInterval == long.MaxValue ? 0 : _minInterval / 10_000.0;

    public double MaxIntervalMs => _maxInterval == long.MinValue ? 0 : _maxInterval / 10_000.0;

    /// <summary>最近一次相邻间隔（毫秒）。</summary>
    public double LastIntervalMs { get; private set; }

    /// <summary>记一帧。</summary>
    public void Add(long timestampHns)
    {
        if (_firstTimestamp == long.MinValue)
        {
            _firstTimestamp = timestampHns;
        }
        else if (timestampHns > _lastTimestamp || _lastTimestamp == long.MinValue)
        {
            long interval = timestampHns - _lastTimestamp;
            if (interval > 0)
            {
                LastIntervalMs = interval / 10_000.0;
                _minInterval = Math.Min(_minInterval, interval);
                _maxInterval = Math.Max(_maxInterval, interval);
            }
        }

        _lastTimestamp = timestampHns;
        SampleCount++;
        _timestamps.Enqueue(timestampHns);

        // 滑动窗口：超过容量就把头部丢掉，并把 first 重新指向新的头部
        while (_timestamps.Count > _capacity)
        {
            _timestamps.Dequeue();
            _firstTimestamp = _timestamps.Peek();
        }
    }

    public void Reset()
    {
        _timestamps.Clear();
        _firstTimestamp = long.MinValue;
        _lastTimestamp = long.MinValue;
        _minInterval = long.MaxValue;
        _maxInterval = long.MinValue;
        LastIntervalMs = 0;
        SampleCount = 0;
    }

    public override string ToString()
        => $"{MeasuredFps:0.###} fps（{SampleCount} 帧 / {ElapsedSeconds:0.000} 秒，"
         + $"间隔 {MinIntervalMs:0.00}–{MaxIntervalMs:0.00} ms）";
}
