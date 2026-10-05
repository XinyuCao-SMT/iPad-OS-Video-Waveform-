//
//  WasapiCapture.cs
//  VideoScopePad.Win
//
//  实时音频抓取（WASAPI 共享模式）+ 独占模式能力探测。
//
//  分工：
//    · 抓取：IAudioClient::Initialize(SHARED) → GetService(IAudioCaptureClient) → Start()，
//      轮询 GetNextPacketSize/GetBuffer 把样本搬出来，统一成 float（±1.0）并规整成通道数组。
//    · 探测：IsFormatSupported(EXCLUSIVE, 8ch@48k/32f) —— 回答"这张卡能不能出 8 声道"
//      （共享模式下实测本机所有端点都只有 2ch，所以这一问必须单独问）。
//
//  线程模型：抓取跑在自己的线程上（WASAPI 轮询 + 复制样本），把最新一帧样本交给上层；
//  上层（界面）只读"最新的那一帧"，不需要跟着音频节奏跑。
//

using System.Runtime.InteropServices;

namespace VideoScopePad.Win.Audio;

/// <summary>抓取到的一帧音频（已经是 float、按通道分开）</summary>
public sealed record AudioCaptureFrame(float[][] Channels, int SampleRate, long DevicePosition)
{
    public static AudioCaptureFrame Empty { get; } = new(Array.Empty<float[]>(), 0, 0);
}

/// <summary>独占模式探测结果</summary>
public sealed record ExclusiveFormatProbe(
    bool Supported,
    int RequestedChannels,
    int RequestedSampleRate,
    int HResult,
    int ClosestChannels,
    int ClosestSampleRate);

/// <summary>WASAPI 音频抓取器（共享模式；用完必须 Dispose）</summary>
public sealed class WasapiCapture : IDisposable
{
    private const int ClsCtxAll = 0x17;
    private const int Capture = 1;
    private const int ShareModeShared = 0;
    private const int ShareModeExclusive = 1;
    private const int StreamFlagsNoPersist = 0x00080000;
    private const int BufferFlagsSilent = 0x2;

    private static readonly Guid ClsidMmDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IidIAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    private static readonly Guid IidIAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");

    private IMMDeviceEnumerator? _enumerator;
    private IMMDevice? _device;
    private IAudioClient? _client;
    private IAudioCaptureClient? _capture;
    private IntPtr _format;
    private volatile bool _running;
    private Thread? _thread;

    /// <summary>抓到的通道数（等于设备混音格式的通道数）</summary>
    public int Channels { get; private set; }

    public int SampleRate { get; private set; }

    /// <summary>最近一次错误（没有错误时为空）</summary>
    public string LastError { get; private set; } = string.Empty;

    /// <summary>累计抓到的帧数（用来证明"真的在跑"）</summary>
    public long FramesCaptured;

    /// <summary>最近一帧（供界面读取）</summary>
    public AudioCaptureFrame Latest { get; private set; } = AudioCaptureFrame.Empty;

    /// <summary>打开指定端点（endpointId 为空时用默认采集设备）</summary>
    public bool Open(string? endpointId)
    {
        try
        {
            _enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(
                Type.GetTypeFromCLSID(ClsidMmDeviceEnumerator)!)!;

            int hr = string.IsNullOrEmpty(endpointId)
                ? _enumerator.GetDefaultAudioEndpoint(Capture, 0, out _device)
                : _enumerator.GetDevice(endpointId!, out _device);
            if (hr != 0 || _device is null)
            {
                LastError = $"打开端点失败（HRESULT 0x{hr:X8}）";
                return false;
            }

            Guid iid = IidIAudioClient;
            hr = _device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out _client);
            if (hr != 0 || _client is null)
            {
                LastError = $"激活 IAudioClient 失败（HRESULT 0x{hr:X8}）";
                return false;
            }

            hr = _client.GetMixFormat(out _format);
            if (hr != 0 || _format == IntPtr.Zero)
            {
                LastError = $"取混音格式失败（HRESULT 0x{hr:X8}）";
                return false;
            }

            Channels = Marshal.ReadInt16(_format, 2);
            SampleRate = Marshal.ReadInt32(_format, 4);

            // 共享模式：1 秒缓冲（对表头显示足够，也不吃太多内存）
            hr = _client.Initialize(ShareModeShared, StreamFlagsNoPersist,
                                    10_000_000L, 0, _format, IntPtr.Zero);
            if (hr != 0)
            {
                LastError = $"Initialize(共享) 失败（HRESULT 0x{hr:X8}）—— 设备可能被独占占用";
                return false;
            }

            Guid captureIid = IidIAudioCaptureClient;
            hr = _client.GetService(ref captureIid, out object service);
            if (hr != 0 || service is not IAudioCaptureClient capture)
            {
                LastError = $"取 IAudioCaptureClient 失败（HRESULT 0x{hr:X8}）";
                return false;
            }
            _capture = capture;
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    /// <summary>开始抓取（Open 成功后调用）</summary>
    public bool Start()
    {
        if (_client is null || _capture is null)
        {
            LastError = "尚未打开端点";
            return false;
        }

        int hr = _client.Start();
        if (hr != 0)
        {
            LastError = $"Start 失败（HRESULT 0x{hr:X8}）";
            return false;
        }

        _running = true;
        _thread = new Thread(CaptureLoop) { IsBackground = true, Name = "VspAudioCapture" };
        _thread.Start();
        return true;
    }

    private void CaptureLoop()
    {
        var pending = new List<float>();
        while (_running)
        {
            try
            {
                if (_capture is null) { break; }
                if (_capture.GetNextPacketSize(out int available) != 0) { break; }

                bool gotSomething = false;
                while (available > 0)
                {
                    int hr = _capture.GetBuffer(out IntPtr data, out int frames, out int flags,
                                                out long devicePosition, out _);
                    if (hr != 0) { break; }

                    int count = frames * Channels;
                    var block = new float[Math.Max(count, 0)];
                    if ((flags & BufferFlagsSilent) != 0 || data == IntPtr.Zero)
                    {
                        // 静音包：WASAPI 明确告诉我们没有数据，按零填充（不要读那块内存）
                    }
                    else
                    {
                        Marshal.Copy(data, block, 0, count);
                    }
                    _capture.ReleaseBuffer(frames);

                    pending.AddRange(block);
                    Interlocked.Add(ref FramesCaptured, frames);
                    gotSomething = true;
                    if (_capture.GetNextPacketSize(out available) != 0) { break; }
                }

                // 每凑够 ~50 ms 就发布一帧（界面按自己的节奏读，不必跟音频块同频）
                int perFrame = Math.Max((int)(SampleRate * 0.05), 1) * Channels;
                if (pending.Count >= perFrame)
                {
                    Publish(pending, perFrame);
                }
                else if (!gotSomething)
                {
                    Thread.Sleep(5);
                }
            }
            catch (Exception ex)
            {
                LastError = $"{ex.GetType().Name}: {ex.Message}";
                break;
            }
        }
    }

    private void Publish(List<float> pending, int perFrame)
    {
        int frames = pending.Count / Channels;
        var channels = new float[Channels][];
        for (int ch = 0; ch < Channels; ch++)
        {
            channels[ch] = new float[frames];
        }
        for (int i = 0; i < frames; i++)
        {
            for (int ch = 0; ch < Channels; ch++)
            {
                channels[ch][i] = pending[i * Channels + ch];
            }
        }
        pending.RemoveRange(0, frames * Channels);
        Latest = new AudioCaptureFrame(channels, SampleRate, 0);
    }

    /// <summary>
    /// 探测独占模式下能否用指定通道数（回答"8 声道到底能不能拿到"）。
    /// 注意：只问 IsFormatSupported，**不做 Initialize**，所以不会独占设备、不影响别人。
    /// </summary>
    public static ExclusiveFormatProbe ProbeExclusive(string? endpointId, int channels, int sampleRate)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioClient? client = null;
        IntPtr format = IntPtr.Zero;
        try
        {
            enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(
                Type.GetTypeFromCLSID(ClsidMmDeviceEnumerator)!)!;
            int hr = string.IsNullOrEmpty(endpointId)
                ? enumerator.GetDefaultAudioEndpoint(Capture, 0, out device)
                : enumerator.GetDevice(endpointId!, out device);
            if (hr != 0 || device is null)
            {
                return new ExclusiveFormatProbe(false, channels, sampleRate, hr, 0, 0);
            }

            Guid iid = IidIAudioClient;
            hr = device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out client);
            if (hr != 0 || client is null)
            {
                return new ExclusiveFormatProbe(false, channels, sampleRate, hr, 0, 0);
            }

            format = MakeFloatFormat(channels, sampleRate);
            hr = client.IsFormatSupported(ShareModeExclusive, format, out IntPtr closest);

            int closestChannels = 0, closestRate = 0;
            if (closest != IntPtr.Zero)
            {
                closestChannels = Marshal.ReadInt16(closest, 2);
                closestRate = Marshal.ReadInt32(closest, 4);
                Marshal.FreeCoTaskMem(closest);
            }
            return new ExclusiveFormatProbe(hr == 0, channels, sampleRate, hr, closestChannels, closestRate);
        }
        catch (Exception ex)
        {
            return new ExclusiveFormatProbe(false, channels, sampleRate, ex.HResult, 0, 0);
        }
        finally
        {
            if (format != IntPtr.Zero) { Marshal.FreeCoTaskMem(format); }
            if (client is not null) { Marshal.ReleaseComObject(client); }
            if (device is not null) { Marshal.ReleaseComObject(device); }
            if (enumerator is not null) { Marshal.ReleaseComObject(enumerator); }
        }
    }

    /// <summary>造一个 WAVEFORMATEXTENSIBLE（32 位浮点、N 声道）</summary>
    private static IntPtr MakeFloatFormat(int channels, int sampleRate)
    {
        const int size = 40;                       // 18 + 22
        IntPtr format = Marshal.AllocCoTaskMem(size);
        Marshal.WriteInt16(format, 0, unchecked((short)0xFFFE));   // ⚠️ 要转型：不然会被解析到 WriteInt16(…, char) 重载                              // WAVE_FORMAT_EXTENSIBLE
        Marshal.WriteInt16(format, 2, (short)channels);
        Marshal.WriteInt32(format, 4, sampleRate);
        Marshal.WriteInt32(format, 8, sampleRate * channels * 4);           // nAvgBytesPerSec
        Marshal.WriteInt16(format, 12, (short)(channels * 4));              // nBlockAlign
        Marshal.WriteInt16(format, 14, (short)32);                                 // wBitsPerSample
        Marshal.WriteInt16(format, 16, (short)22);                                 // cbSize
        Marshal.WriteInt16(format, 18, (short)32);                                 // wValidBitsPerSample
        Marshal.WriteInt32(format, 20, channels == 8 ? 0x63F : 0x3);        // 7.1 或 立体声掩码
        // SubFormat = KSDATAFORMAT_SUBTYPE_IEEE_FLOAT
        byte[] floatSubFormat =
        {
            0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x10, 0x00,
            0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71,
        };
        Marshal.Copy(floatSubFormat, 0, format + 24, floatSubFormat.Length);
        return format;
    }

    public void Dispose()
    {
        _running = false;
        try { _thread?.Join(500); } catch (Exception) { /* 线程退出失败不阻塞关闭 */ }
        try { _client?.Stop(); } catch (Exception) { /* 已停就忽略 */ }
        if (_capture is not null) { Marshal.ReleaseComObject(_capture); _capture = null; }
        if (_client is not null) { Marshal.ReleaseComObject(_client); _client = null; }
        if (_device is not null) { Marshal.ReleaseComObject(_device); _device = null; }
        if (_enumerator is not null) { Marshal.ReleaseComObject(_enumerator); _enumerator = null; }
        if (_format != IntPtr.Zero) { Marshal.FreeCoTaskMem(_format); _format = IntPtr.Zero; }
    }

    // ---------------- COM 声明 ----------------

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? device);
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice? device);
        int RegisterEndpointNotificationCallback(IntPtr client);
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
                     [MarshalAs(UnmanagedType.Interface)] out IAudioClient? audioClient);
        int OpenPropertyStore(int access, out IntPtr properties);
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        int GetState(out int state);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity,
                       IntPtr format, IntPtr sessionGuid);
        int GetBufferSize(out int frames);
        int GetStreamLatency(out long latency);
        int GetCurrentPadding(out int frames);
        int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
        int GetMixFormat(out IntPtr format);
        int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        int Start();
        int Stop();
        int Reset();
        int SetEventHandle(IntPtr handle);
        int GetService(ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        int GetBuffer(out IntPtr data, out int frames, out int flags,
                      out long devicePosition, out long qpcPosition);
        int ReleaseBuffer(int frames);
        int GetNextPacketSize(out int frames);
    }
}
