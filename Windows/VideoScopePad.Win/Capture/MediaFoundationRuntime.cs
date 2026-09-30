//
//  MediaFoundationRuntime.cs
//  VideoScopePad.Win
//
//  Media Foundation 的启动/关闭。
//
//  为什么要包一层：MFStartup 是**进程级引用计数**的，采集与（以后的）音频、
//  编码链路会各自调用一次，谁先退出都不该把别人的 MF 关掉 —— 所以这里自己
//  维护一个计数，0 → 1 时才真的 MFStartup，1 → 0 时才真的 MFShutdown。
//
//  ⚠️ MFStartup 返回 Result（失败会抛），必须在**创建任何 MF 对象之前**调用；
//  设备枚举（MFEnumVideoDeviceSources）也一样受这个约束。
//

using Vortice.MediaFoundation;

namespace VideoScopePad.Win.Capture;

/// <summary>
/// Media Foundation 运行时租约：<c>using var mf = MediaFoundationRuntime.Start();</c>
/// </summary>
public sealed class MediaFoundationRuntime : IDisposable
{
    private static readonly object Gate = new();
    private static int _leases;

    private bool _disposed;

    private MediaFoundationRuntime()
    {
    }

    /// <summary>当前是否有活动的 MF 运行时（调试用）。</summary>
    public static bool IsStarted
    {
        get
        {
            lock (Gate)
            {
                return _leases > 0;
            }
        }
    }

    /// <summary>
    /// 启动（或复用）Media Foundation。第一个调用者真正启动，之后只加计数。
    /// </summary>
    public static MediaFoundationRuntime Start()
    {
        lock (Gate)
        {
            if (_leases == 0)
            {
                // useLightVersion: false —— 我们用采集（不是只做转码），要完整版本
                MediaFactory.MFStartup(useLightVersion: false);
            }
            _leases++;
        }
        return new MediaFoundationRuntime();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        lock (Gate)
        {
            _leases--;
            if (_leases <= 0)
            {
                _leases = 0;
                MediaFactory.MFShutdown();
            }
        }
    }
}
