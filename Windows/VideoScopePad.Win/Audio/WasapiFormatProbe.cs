//
//  WasapiFormatProbe.cs
//  VideoScopePad.Win
//
//  音频端点的**真实格式**侦察：用 IAudioClient::GetMixFormat 问系统"这台设备到底给什么格式"。
//
//  为什么必须走 WASAPI（第 7 轮的教训）：
//    注册表里的 PKEY_AudioEngine_DeviceFormat 在 C# 侧读不到（子键里就没有那个值），
//    而 GetMixFormat 才是"共享模式下系统实际会给你的格式" —— 也就是"能不能拿到 8 声道"的最终依据。
//
//  取巧之处（省掉一大块编组代码）：
//    设备名字不去读 PROPVARIANT（要自己搭结构体、容易错），而是用 IMMDevice.GetId() 拿到端点 ID
//    （形如 "{0.0.0.00000000}.{guid}"），它**正好等于注册表里 Capture 下的子键名** ——
//    所以名字用已有且已验证的 AudioDeviceEnumerator 提供，WASAPI 只负责回答"几声道"。
//

using System.Runtime.InteropServices;

namespace VideoScopePad.Win.Audio;

/// <summary>一个端点的真实（WASAPI 共享模式）格式</summary>
public sealed record WasapiEndpointFormat(
    string EndpointId,
    string FriendlyName,
    int Channels,
    int SampleRate,
    int BitsPerSample,
    int DeviceState,
    bool IsActive);

public static class WasapiFormatProbe
{
    private const int ClsCtxAll = 0x17;          // CLSCTX_ALL
    private const int Capture = 1;               // EDataFlow.eCapture
    private const int DeviceStateAll = 0x0F;     // DEVICE_STATE_* 全部

    private static readonly Guid ClsidMmDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IidIMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IidIAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");

    /// <summary>
    /// 枚举所有音频**采集**端点并问出真实格式。
    /// 失败时返回空列表（不抛异常）：音频侦察不该让整个程序出错。
    /// </summary>
    public static IReadOnlyList<WasapiEndpointFormat> Enumerate()
    {
        var result = new List<WasapiEndpointFormat>();
        IReadOnlyList<AudioDeviceInfo> registry = AudioDeviceEnumerator.Enumerate();
        // ⚠️ 端点 ID 不能整串比：WASAPI 给的是 "{0.0.1.00000000}.{guid}"，
        //    而注册表子键名是 "{0.0.0.00000000}.{guid}"（第三段不同）—— 整串比会全部对不上，
        //    实测 31 个端点全变成"注册表里没有的端点"。改为只按末尾的 {guid} 匹配。
        var names = new Dictionary<string, AudioDeviceInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (AudioDeviceInfo device in registry)
        {
            names[GuidPart(device.Id)] = device;
        }

        IMMDeviceEnumerator? enumerator = null;
        IMMDeviceCollection? collection = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(
                Type.GetTypeFromCLSID(ClsidMmDeviceEnumerator)!)!;
            if (enumerator.EnumAudioEndpoints(Capture, DeviceStateAll, out collection) != 0 || collection is null)
            {
                return result;
            }
            if (collection.GetCount(out int count) != 0)
            {
                return result;
            }

            for (int index = 0; index < count; index++)
            {
                if (collection.Item(index, out IMMDevice? device) != 0 || device is null)
                {
                    continue;
                }

                try
                {
                    if (device.GetId(out string id) != 0 || string.IsNullOrEmpty(id))
                    {
                        continue;
                    }
                    device.GetState(out int state);

                    string name = names.TryGetValue(GuidPart(id), out AudioDeviceInfo? info) && info is not null
                        ? info.FriendlyName
                        : $"（注册表里没有的端点 {id}）";

                    (int channels, int rate, int bits) = QueryMixFormat(device);
                    result.Add(new WasapiEndpointFormat(id, name, channels, rate, bits, state & 0xF, (state & 0xF) == 1));
                }
                finally
                {
                    Marshal.ReleaseComObject(device);
                }
            }
        }
        catch (Exception)
        {
            // 枚举失败就给已经拿到的部分：侦察工具不该因为一台坏设备整个失败
        }
        finally
        {
            if (collection is not null) { Marshal.ReleaseComObject(collection); }
            if (enumerator is not null) { Marshal.ReleaseComObject(enumerator); }
        }

        return result
            .OrderByDescending(d => d.Channels)
            .ThenBy(d => d.FriendlyName, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>取端点 ID 末尾的 {guid} 部分（用于与注册表子键名对应）</summary>
    private static string GuidPart(string endpointId)
    {
        // 🔴 根因：注册表子键名是**裸 GUID**（没有点），老写法在"没有点"的分支里没去大括号，
        //    于是 key 是 "{guid}" 而 WASAPI 侧是 "guid" → 永远对不上（表现为 31 个端点全叫"注册表里没有的端点"）。
        //    修法：两条分支都去大括号。
        int index = endpointId.LastIndexOf('.');
        string part = index >= 0 ? endpointId[(index + 1)..] : endpointId;
        return part.Trim('{', '}');
    }

    /// <summary>问这台设备的共享模式混音格式（拿不到就给 0，由调用方显示"未知"）</summary>
    private static (int Channels, int SampleRate, int Bits) QueryMixFormat(IMMDevice device)
    {
        IAudioClient? client = null;
        IntPtr format = IntPtr.Zero;
        try
        {
            Guid iid = IidIAudioClient;
            if (device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out client) != 0 || client is null)
            {
                return (0, 0, 0);
            }
            if (client.GetMixFormat(out format) != 0 || format == IntPtr.Zero)
            {
                return (0, 0, 0);
            }

            // WAVEFORMATEX：wFormatTag(0,2) nChannels(2,2) nSamplesPerSec(4,4)
            //               nAvgBytesPerSec(8,4) nBlockAlign(12,2) wBitsPerSample(14,2) cbSize(16,2)
            int channels = Marshal.ReadInt16(format, 2);
            int sampleRate = Marshal.ReadInt32(format, 4);
            int bits = Marshal.ReadInt16(format, 14);
            return (channels, sampleRate, bits);
        }
        catch (Exception)
        {
            return (0, 0, 0);
        }
        finally
        {
            if (format != IntPtr.Zero) { Marshal.FreeCoTaskMem(format); }
            if (client is not null) { Marshal.ReleaseComObject(client); }
        }
    }

    // ---------------- COM 声明（只声明用得到的方法，顺序必须与 vtable 一致） ----------------

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection? devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? device);
        int GetDevice(string id, out IMMDevice? device);
        int RegisterEndpointNotificationCallback(IntPtr client);
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        int GetCount(out int count);
        int Item(int index, out IMMDevice? device);
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
}
