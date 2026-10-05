//
//  CaptureProbe.cs
//  VideoScopePad.Win
//
//  设备"能不能用"的事前探测 —— 为了解决一类要命的设备：
//  某些虚拟摄像头（实测 NDI Webcam Video N 在源没推流时）会让
//  IMFSourceReader.ReadSample **永久阻塞**。渲染线程一旦进到那个读取里就再也出不来，
//  表现是画面冻住、换源/换布局全都不再生效（因为都排在渲染线程上）。
//
//  试过的办法与结论：
//    · 从外部 Dispose 那个设备 → **打不断**阻塞中的读取（实测：Dispose 之后渲染线程仍未恢复）。
//    · 唯一可靠的办法是**别让渲染线程碰这种设备**：先在可丢弃的后台线程上试读几帧，
//      读得出来才允许渲染线程打开。探测线程若卡死就把它丢掉（不 Join），代价只是一个僵尸线程。
//
//  探测结果缓存 2 分钟（好的坏的都缓存），避免每次选设备都重探。
//

using System.Diagnostics;

namespace VideoScopePad.Win.Capture;

/// <summary>探测结果。</summary>
public sealed record CaptureProbeResult(bool Ok, string Detail);

public static class CaptureProbe
{
    private sealed record CacheEntry(CaptureProbeResult Result, DateTime When);

    private static readonly Dictionary<string, CacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    /// <summary>缓存有效期</summary>
    public static TimeSpan CacheLifetime { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// 在**后台线程**上试读若干帧，证明这台设备能出帧。
    /// 超时就返回失败并**丢掉那个线程**（不 Join —— 阻塞中的读取杀不掉，只能不要它）。
    /// </summary>
    public static CaptureProbeResult Probe(string deviceKeyOrName, int timeoutMs = 5000, int frames = 5)
    {
        if (string.IsNullOrWhiteSpace(deviceKeyOrName))
        {
            return new CaptureProbeResult(false, "设备标识为空");
        }

        lock (Gate)
        {
            if (Cache.TryGetValue(deviceKeyOrName, out CacheEntry? cached)
                && DateTime.UtcNow - cached.When < CacheLifetime)
            {
                return cached.Result;
            }
        }

        CaptureProbeResult? result = null;
        var done = new ManualResetEventSlim(false);
        var worker = new Thread(() =>
        {
            try
            {
                IReadOnlyList<CaptureDeviceInfo> devices = VideoDeviceEnumerator.Enumerate();
                CaptureDeviceInfo? target = devices.FirstOrDefault(
                    d => string.Equals(DeviceWatcher.KeyOf(d), deviceKeyOrName, StringComparison.OrdinalIgnoreCase))
                    ?? devices.FirstOrDefault(
                        d => d.FriendlyName.Contains(deviceKeyOrName, StringComparison.OrdinalIgnoreCase));
                if (target is null)
                {
                    result = new CaptureProbeResult(false, "设备不在列表里");
                    return;
                }

                using CaptureDevice device = CaptureDevice.Open(target);
                IReadOnlyList<CaptureFormat> formats = device.GetNativeFormats();
                CaptureFormat picked = CaptureFormat.PickPreferred(formats)
                    ?? throw new InvalidOperationException("设备没有可用格式");
                device.SetNativeFormat(picked.NativeIndex);

                int got = 0;
                for (int i = 0; i < 60 && got < frames; i++)      // 最多等 60 次读取机会
                {
                    CapturedFrame? frame = device.ReadFrame();
                    if (frame is not null)
                    {
                        got++;
                    }
                }

                result = got >= 1
                    ? new CaptureProbeResult(true, $"能出帧（试读到 {got} 帧，{picked.Describe()}）")
                    : new CaptureProbeResult(false, "打开了但一帧都读不到");
            }
            catch (Exception ex)
            {
                result = new CaptureProbeResult(false, $"{ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                done.Set();
            }
        })
        {
            IsBackground = true,
            Name = "VideoScopePad-Probe",
        };

        long start = Stopwatch.GetTimestamp();
        worker.Start();
        bool finished = done.Wait(timeoutMs);
        double elapsedMs = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;

        CaptureProbeResult final = finished && result is not null
            ? result
            : new CaptureProbeResult(false, $"试读超时（{elapsedMs:0} ms 没有出帧，判定为会卡死的设备）");

        lock (Gate)
        {
            Cache[deviceKeyOrName] = new CacheEntry(final, DateTime.UtcNow);
        }
        return final;
    }

    /// <summary>清掉缓存（界面上点"刷新设备"、或插拔之后重新探测）</summary>
    public static void Invalidate(string? deviceKey = null)
    {
        lock (Gate)
        {
            if (deviceKey is null)
            {
                Cache.Clear();
            }
            else
            {
                Cache.Remove(deviceKey);
            }
        }
    }
}
