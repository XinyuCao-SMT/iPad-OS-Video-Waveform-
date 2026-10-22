//
//  IAudioSource.cs / SyntheticToneSource.cs
//  VideoScopePad.Win
//
//  「音频跟着视频源走」的最后一块：**测试信号自带音频**。
//
//  用户要求：切到「合成测试信号」时，视频是 1920×1080 彩条、音频就应该是 1 kHz 千周，
//  不该继续用上一个硬件设备的音频端点。
//
//  所以把音频源抽象出来（IAudioSource），由两种实现：
//    · WasapiCapture      —— 真实设备（内置话筒 / 采集卡音频 / NDI 音频）
//    · SyntheticToneSource—— 测试信号的 1 kHz 千周（周期脉冲，方便看起音/电平/频谱/声相）
//

using System.Diagnostics;

namespace VideoScopePad.Win.Audio;

/// <summary>音频源（真实设备或测试信号内置千周）</summary>
public interface IAudioSource : IDisposable
{
    int Channels { get; }
    int SampleRate { get; }
    string LastError { get; }
    AudioCaptureFrame Latest { get; }
    /// <summary>最近一次起音的时刻（秒，相对本对象构造时刻；-1 = 还没有）</summary>
    double LastOnsetSeconds { get; }
    /// <summary>本对象的时钟（秒，相对构造时刻）—— 用来把音频事件对齐到会话时钟</summary>
    double ClockSeconds { get; }
    bool Open(string? endpointId);
    bool Start();
}

public sealed class SyntheticToneSource : IAudioSource
{
    /// <summary>千周频率</summary>
    public const double ToneHz = 1000.0;
    /// <summary>每轮：响 0.7 s、静 1.3 s（2 s 一个周期，方便看起音与电平表）</summary>
    public const double BurstSeconds = 0.7;
    public const double PeriodSeconds = 2.0;
    /// <summary>幅度（约 -12 dBFS，不削顶）</summary>
    public const double Amplitude = 0.25;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _lock = new();

    private long _samplesWritten;
    private double _lastOnsetSeconds = -1;

    public int Channels { get; private set; } = 2;
    public int SampleRate { get; private set; } = 48000;
    public string LastError { get; private set; } = string.Empty;
    public AudioCaptureFrame Latest { get; private set; } = AudioCaptureFrame.Empty;
    public double LastOnsetSeconds { get { lock (_lock) { return _lastOnsetSeconds; } } }
    public double ClockSeconds => _clock.Elapsed.TotalSeconds;

    public bool Open(string? endpointId)
    {
        // endpointId 没有意义：测试信号就是内置千周
        Channels = 2;
        SampleRate = 48000;
        LastError = string.Empty;
        return true;
    }

    public bool Start()
    {
        _samplesWritten = 0;
        lock (_lock) { _lastOnsetSeconds = -1; }
        Latest = AudioCaptureFrame.Empty;
        return true;
    }

    /// <summary>
    /// 按"距上次取用过了多久"生成样本（LiveSession 每 100 ms 取一次）。
    /// 这样既不占线程，也不需要真实的采集回调。
    /// </summary>
    public void Pump(double nowSeconds)
    {
        int wanted = (int)Math.Round(nowSeconds * SampleRate);
        int count = wanted - (int)_samplesWritten;
        if (count <= 0)
        {
            return;
        }
        count = Math.Min(count, SampleRate);   // 一次最多补 1 秒，避免卡顿后猛冲

        var left = new float[count];
        var right = new float[count];
        double phase = 2.0 * Math.PI * ToneHz / SampleRate;
        long startIndex = _samplesWritten;

        for (int i = 0; i < count; i++)
        {
            long index = startIndex + i;
            double t = index / (double)SampleRate;
            double inPeriod = t % PeriodSeconds;
            if (inPeriod < BurstSeconds)
            {
                float value = (float)(Amplitude * Math.Sin(phase * index));
                left[i] = value;
                right[i] = value;      // 等幅同相：声相应显示为"正中竖线"
            }
            // 静音段保持 0

            // 起音：每轮脉冲的第一个样本
            if (inPeriod < 1.0 / SampleRate)
            {
                lock (_lock) { _lastOnsetSeconds = index / (double)SampleRate; }
            }
        }

        SampleRate = 48000;
        Latest = new AudioCaptureFrame(new[] { left, right }, SampleRate, startIndex);
        _samplesWritten = startIndex + count;
    }

    public void Dispose()
    {
        Latest = AudioCaptureFrame.Empty;
    }
}
