//
//  CaptureDevice.cs
//  VideoScopePad.Win
//
//  打开一张采集卡并和它对话：列原生格式、设格式、读帧。
//  对应 iPad 版的 Capture/UVCDevice.swift（那边是 AVCaptureDevice + AVCaptureSession）。
//
//  打开链路（一步步都在这里，别再去翻文档）：
//    IMFActivate.ActivateObject<IMFMediaSource>()          ← 设备对象
//    MediaFactory.MFCreateSourceReaderFromMediaSource()   ← 源读取器（我们采集的唯一入口）
//    reader.SetStreamSelection(FirstVideoStream, true)     ← 只选视频流
//    reader.GetNativeMediaType(FirstVideoStream, i)        ← 第 i 条原生格式（越界会抛）
//    reader.SetCurrentMediaType(FirstVideoStream, mt)      ← 生效（传**完整原生类型** = 不做任何转换）
//    reader.GetCurrentMediaType(FirstVideoStream)          ← 回读实际生效格式
//
//  ⚠️ 为什么用「源读取器」而不是 IMFSourceReaderCallback 异步模式：
//  同步 ReadSample 足够（采集是连续的，一帧 16 ms），而且少了回调线程与
//  COM 封送的一堆坑；等接 WPF 实时窗口时再考虑异步版本（接口已按同样形状设计）。
//
//  ⚠️ 关键取舍：SetCurrentMediaType 传的是 GetNativeMediaType 拿到的**原始对象**，
//  不是自己拼一条 partial type。差别很大：拼 partial type 时 MF 可能悄悄插入
//  「视频处理器 MFT」做色彩转换（甚至给你转成 RGB32），那样示波器量到的就不是
//  卡的原始码流了 —— 我们要的正是 16–235 的原始 YUV。
//

using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace VideoScopePad.Win.Capture;

/// <summary>一台已打开的视频采集设备。</summary>
public sealed class CaptureDevice : IDisposable
{
    private readonly IMFMediaSource _source;
    private readonly IMFSourceReader _reader;
    private bool _disposed;

    private CaptureDevice(CaptureDeviceInfo info, IMFMediaSource source, IMFSourceReader reader)
    {
        Info = info;
        _source = source;
        _reader = reader;
    }

    /// <summary>枚举时拿到的静态信息。</summary>
    public CaptureDeviceInfo Info { get; }

    /// <summary>视频流序号（源读取器里的 FirstVideoStream）。</summary>
    public const SourceReaderIndex VideoStream = SourceReaderIndex.FirstVideoStream;

    /// <summary>
    /// 按枚举序号打开（界面下拉里选的就是这个序号）。
    /// </summary>
    public static CaptureDevice Open(int deviceIndex)
    {
        CaptureDeviceInfo? info = VideoDeviceEnumerator.Enumerate()
            .FirstOrDefault(d => d.Index == deviceIndex);
        if (info is null)
        {
            throw new InvalidOperationException(
                $"没有序号为 {deviceIndex} 的视频采集设备（可用 VideoDeviceEnumerator.Enumerate() 看清单）");
        }
        return Open(info);
    }

    /// <summary>按名字片段打开（大小写不敏感）。</summary>
    public static CaptureDevice OpenByName(string nameFragment)
    {
        CaptureDeviceInfo info = VideoDeviceEnumerator.FindByName(nameFragment)
            ?? throw new InvalidOperationException($"找不到名字里带「{nameFragment}」的视频采集设备");
        return Open(info);
    }

    /// <summary>用一条枚举结果打开。</summary>
    public static CaptureDevice Open(CaptureDeviceInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        IMFMediaSource source = CreateMediaSource(info);
        IMFSourceReader? reader = null;
        try
        {
            // 属性集：先给空集合，什么都不改 —— 保持「卡的原始输出」这个前提。
            // （以后要零拷贝 GPU 时在这里塞 MF_SOURCE_READER_D3D_MANAGER）
            using IMFAttributes attributes = MediaFactory.MFCreateAttributes(2);

            reader = MediaFactory.MFCreateSourceReaderFromMediaSource(source, attributes);

            // 只要视频流（采集卡上还挂着音频流时，不选它就不会读出来）
            reader.SetStreamSelection(VideoStream, true);

            return new CaptureDevice(info, source, reader);
        }
        catch
        {
            reader?.Dispose();
            source.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 建媒体源：两条路，优先后者（原因见下）。
    ///   ① 按符号链接 MFCreateDeviceSource —— 首选，一次调用、对象归我们所有
    ///   ② 枚举集合内 ActivateObject     —— 兜底（驱动不给符号链接时）
    ///
    /// 🔴 为什么不用「枚举拿到 IMFActivate 再带出来 ActivateObject」这条最直觉的路：
    ///   IMFActivateCollection 的 Dispose() 会把它交给你的子包装一起 Dispose，
    ///   带出去的 IMFActivate 指针已是 0，再 ActivateObject 会抛 NullReferenceException。
    ///   （tools/mf-capture probe 复现：集合内 ✓ / 带出后 ✗ / MFCreateDeviceSource ✓）
    /// </summary>
    private static IMFMediaSource CreateMediaSource(CaptureDeviceInfo info)
    {
        if (!string.IsNullOrEmpty(info.SymbolicLink))
        {
            using IMFAttributes attributes = MediaFactory.MFCreateAttributes(2);
            attributes.Set(CaptureDeviceAttributeKeys.SourceType, CaptureDeviceAttributeKeys.SourceTypeVidcap);
            attributes.Set(CaptureDeviceAttributeKeys.SourceTypeVidcapSymbolicLink, info.SymbolicLink);
            try
            {
                return MediaFactory.MFCreateDeviceSource(attributes);
            }
            catch (COMException)
            {
                // 符号链接可能已失效（设备重插后路径变了）→ 落到兜底路线
            }
        }

        return VideoDeviceEnumerator.ActivateSourceAt(info.Index);
    }

    /// <summary>
    /// 设备的**原生格式清单**（不做任何转换的那张表）。
    /// 越界时 MF 返回 MF_E_NO_MORE_TYPES，Vortice 抛 COMException —— 靠这个结束循环。
    /// </summary>
    public IReadOnlyList<CaptureFormat> GetNativeFormats()
    {
        ThrowIfDisposed();

        var formats = new List<CaptureFormat>();
        for (int index = 0; ; index++)
        {
            IMFMediaType? mediaType = null;
            try
            {
                mediaType = _reader.GetNativeMediaType(VideoStream, index);
            }
            catch (COMException)
            {
                break;      // MF_E_NO_MORE_TYPES / MF_E_INVALIDSTREAMNUMBER：列表到头了
            }
            catch (SharpGenException)
            {
                break;
            }

            using (mediaType)
            {
                formats.Add(CaptureFormat.FromMediaType(mediaType, index));
            }
        }
        return formats;
    }

    /// <summary>当前**实际生效**的格式（回读，不是我们以为设进去的那条）。</summary>
    public CaptureFormat GetCurrentFormat()
    {
        ThrowIfDisposed();
        using IMFMediaType current = _reader.GetCurrentMediaType(VideoStream);
        _currentFormat = CaptureFormat.FromMediaType(current, -1);
        return _currentFormat;
    }

    /// <summary>缓存住的当前格式（第一次访问时回读一次）。</summary>
    public CaptureFormat CurrentFormat => _currentFormat ??= GetCurrentFormat();

    /// <summary>
    /// 生效第 nativeIndex 条原生格式（把 GetNativeMediaType 的对象原样交给 SetCurrentMediaType，
    /// 因此不会发生任何转换）。返回回读到的实际格式。
    /// </summary>
    public CaptureFormat SetNativeFormat(int nativeIndex)
    {
        ThrowIfDisposed();

        using IMFMediaType? mediaType = _reader.GetNativeMediaType(VideoStream, nativeIndex);
        _reader.SetCurrentMediaType(VideoStream, mediaType);
        return GetCurrentFormat();
    }

    /// <summary>按 Spec（1920x1080@60:NV12）在原生清单里挑一条并生效；找不到抛。</summary>
    public CaptureFormat SetFormat(string spec)
    {
        IReadOnlyList<CaptureFormat> formats = GetNativeFormats();
        CaptureFormat match = formats.FirstOrDefault(f => f.MatchesSpec(spec))
            ?? throw new InvalidOperationException(
                $"设备没有匹配「{spec}」的原生格式。可用：{string.Join("、", formats.Select(f => f.Spec))}");
        return SetNativeFormat(match.NativeIndex);
    }

    /// <summary>把某条原生媒体类型的属性集整个摊开（键 / 类型 / 值）。</summary>
    public IReadOnlyList<(Guid Key, AttributeType Type, string Value)> DescribeNativeTypeAttributes(int nativeIndex)
    {
        ThrowIfDisposed();
        using IMFMediaType mediaType = _reader.GetNativeMediaType(VideoStream, nativeIndex);
        return DescribeAttributes(mediaType);
    }

    /// <summary>把当前生效媒体类型的属性集整个摊开。</summary>
    public IReadOnlyList<(Guid Key, AttributeType Type, string Value)> DescribeCurrentTypeAttributes()
    {
        ThrowIfDisposed();
        using IMFMediaType mediaType = _reader.GetCurrentMediaType(VideoStream);
        return DescribeAttributes(mediaType);
    }

    private static IReadOnlyList<(Guid Key, AttributeType Type, string Value)> DescribeAttributes(IMFAttributes attributes)
        => attributes.ListKeys()
            .Select(k => (k.Key, k.Type, attributes.DescribeValue(k.Key, k.Type)))
            .ToList();

    /// <summary>
    /// 读一个样本。返回 null = 这次没数据（正常现象：源读取器会穿插返回
    /// 「流 tick / 格式变化」这类不带样本的调用，必须继续读而不是当出错）。
    /// </summary>
    public IMFSample? ReadSample(out SourceReaderFlag flags, out long timestamp, out int actualStreamIndex)
    {
        ThrowIfDisposed();

        IMFSample? sample = _reader.ReadSample(
            VideoStream,
            SourceReaderControlFlag.None,
            out actualStreamIndex,
            out flags,
            out timestamp);

        if ((flags & SourceReaderFlag.Error) != 0)
        {
            sample?.Dispose();
            throw new InvalidOperationException("源读取器报告错误（MF_SOURCE_READERF_ERROR）—— 设备可能被拔掉或被别的程序抢走了");
        }
        return sample;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _reader.Dispose();
        _source.Dispose();
    }

    private CaptureFormat? _currentFormat;

    /// <summary>
    /// 读一帧（已拷进托管内存）。返回 null = 这一轮确实没有画面帧
    /// （源读取器返回的是「流 tick / 格式变化」这类无样本调用，或走到流末尾）。
    ///
    /// ⚠️ 同步模式下 ReadSample 会**阻塞**到有帧为止，所以这里的循环次数上限
    /// 只在「拿到无样本调用」时才消耗，不会变成忙等。
    /// </summary>
    public CapturedFrame? ReadFrame(int maxEmptyReads = 8)
    {
        ThrowIfDisposed();
        if (maxEmptyReads < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEmptyReads));
        }

        for (int attempt = 0; attempt < maxEmptyReads; attempt++)
        {
            IMFSample? sample = ReadSample(out SourceReaderFlag flags, out long timestamp, out _);
            if (sample is null)
            {
                if ((flags & SourceReaderFlag.EndOfStream) != 0)
                {
                    return null;
                }
                continue;   // NewStream / NativeMediaTypeChanged / StreamTick：继续读
            }

            using (sample)
            {
                return CopyOut(sample, timestamp);
            }
        }
        return null;
    }

    /// <summary>
    /// 把样本里的像素拷进托管数组。
    /// 行跨距**用驱动实际给的**（IMF2DBuffer.Lock2D 的 pitch），不是 width×bpp 推的 ——
    /// UVC 驱动经常按 4/16 字节对齐补行，按推算值读会整幅图斜掉（这个坑很难看出来）。
    /// </summary>
    private CapturedFrame CopyOut(IMFSample sample, long timestamp)
    {
        CaptureFormat format = CurrentFormat;
        int width = (int)format.Width;
        int height = (int)format.Height;

        using IMFMediaBuffer buffer = sample.GetBufferByIndex(0);

        IMF2DBuffer? buffer2D = null;
        try
        {
            buffer2D = buffer.QueryInterface<IMF2DBuffer>();
        }
        catch (SharpGenException)
        {
            buffer2D = null;   // 少数缓冲不是 2D 的，走一维 Lock
        }

        try
        {
            if (buffer2D is not null)
            {
                buffer2D.Lock2D(out IntPtr scanline0, out int pitch);
                try
                {
                    int rows = RowsFor(pitch, height, buffer.CurrentLength);
                    byte[] data = ReadRows(scanline0, pitch, rows);
                    return new CapturedFrame(data, width, height, pitch, timestamp, sample.SampleDuration, rows);
                }
                finally
                {
                    buffer2D.Unlock2D();
                }
            }
            else
            {
                buffer.Lock(out IntPtr pointer, out _, out int currentLength);
                try
                {
                    int pitch = height > 0 ? currentLength / height : 0;
                    if (pitch <= 0)
                    {
                        throw new InvalidOperationException($"缓冲长度 {currentLength} 与高度 {height} 算不出行跨距");
                    }
                    int rows = RowsFor(pitch, height, currentLength);
                    byte[] data = ReadRows(pointer, pitch, rows);
                    return new CapturedFrame(data, width, height, pitch, timestamp, sample.SampleDuration, rows);
                }
                finally
                {
                    buffer.Unlock();
                }
            }
        }
        finally
        {
            buffer2D?.Dispose();
        }
    }

    /// <summary>
    /// 该拷多少行：用**缓冲实际长度 ÷ 行跨距**算，而不是按格式猜。
    /// YUY2 得到 height；NV12 得到 height×1.5（UV 交织平面跟在 Y 后面）——
    /// 按格式硬编码的话，NV12 会只拷到 Y 平面，转换出来就是一片灰。
    /// </summary>
    private static int RowsFor(int pitch, int height, int currentLength)
    {
        int absolutePitch = Math.Abs(pitch);
        if (absolutePitch <= 0 || currentLength <= 0)
        {
            return height;
        }
        int rows = currentLength / absolutePitch;
        return rows >= height ? rows : height;
    }

    private static byte[] ReadRows(IntPtr scanline0, int pitch, int rows)
    {
        int absolutePitch = Math.Abs(pitch);
        var data = new byte[absolutePitch * rows];
        for (int y = 0; y < rows; y++)
        {
            IntPtr row = IntPtr.Add(scanline0, y * pitch);   // pitch 为负时自动往回走（自下而上）
            Marshal.Copy(row, data, y * absolutePitch, absolutePitch);
        }
        return data;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
