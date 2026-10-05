//
//  AudioDeviceEnumerator.cs
//  VideoScopePad.Win
//
//  音频采集端点侦察 —— 音频套件的第一步：**先知道硬件到底给几声道**，再谈 8ch 怎么接。
//
//  为什么先侦察而不是直接写 WASAPI：多声道能不能看到，取决于设备驱动暴露了几路。
//  很多 SDI 采集卡的 8 声道要走厂商 SDK，Windows 音频栈只给 2ch ——
//  不先问清楚就写 8 条电平表，做出来也是空的。
//
//  实现走**注册表**（不依赖 COM 互操作，离线也能跑）：
//    HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture\{id}\
//      Properties\{f19f064d-082c-4e27-bc73-6882a1bb8e4c},0 = PKEY_AudioEngine_DeviceFormat
//          → 一个 WAVEFORMATEX(TENSIBLE) 二进制块：wFormatTag(2) nChannels(2) nSamplesPerSec(4) …
//      Properties\{f19f064d-082c-4e27-bc73-6882a1bb8e4c},1 = PKEY_AudioEngine_OEMFormat
//          → 驱动给的 OEM 格式（有些卡在这里才写出真实声道数）
//      Properties\{a45c254e-df1c-4efd-8020-67d146a850e0},2 = PKEY_Device_FriendlyName
//      DeviceState：1 = 在用，2 = 已禁用，4 = 未插入，8 = 不可用
//
//  ⚠️ 注册表只反映**系统当前保存的格式**。真接 8ch 设备时最终仍要以
//     IAudioClient::GetMixFormat 为准（那是 WASAPI 会真正给你的格式）——
//     所以本侦察的结论写法是"系统保存的格式是 N 声道"，而不是"这卡就是 N 声道"。
//

using Microsoft.Win32;

namespace VideoScopePad.Win.Audio;

/// <summary>一个音频采集端点</summary>
public sealed record AudioDeviceInfo(
    string Id,
    string FriendlyName,
    int DeviceChannels,
    int DeviceSampleRate,
    int OemChannels,
    int OemSampleRate,
    int DeviceState)
{
    /// <summary>能拿到的最多声道数（设备格式与 OEM 格式取大者）</summary>
    public int Channels => Math.Max(DeviceChannels, OemChannels);

    /// <summary>对应的采样率（取有值的那个）</summary>
    public int SampleRate => DeviceChannels > 0 ? DeviceSampleRate : OemSampleRate;

    public bool IsActive => DeviceState == 1;

    public string StateText => DeviceState switch
    {
        1 => "在用",
        2 => "已禁用",
        4 => "未插入",
        8 => "不可用",
        _ => $"状态 {DeviceState}",
    };

    public bool IsMultichannel => Channels > 2;

    public string Summary => $"{FriendlyName}　{Channels}ch @ {SampleRate} Hz（{StateText}）";
}

public static class AudioDeviceEnumerator
{
    private const string CaptureRoot =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture";

    // PKEY 的值名形如 "{GUID},PID"
    private const string PkeyFriendlyName = "{a45c254e-df1c-4efd-8020-67d146a850e0},2";
    private const string PkeyDeviceFormat = "{f19f064d-082c-4e27-bc73-6882a1bb8e4c},0";
    private const string PkeyOemFormat = "{f19f064d-082c-4e27-bc73-6882a1bb8e4c},1";

    /// <summary>诊断：打印某个端点 Properties 下的值名与二进制块长度（用来核对 C# 与注册表视图的差异）</summary>
    public static IReadOnlyList<string> Describe(int index = 0)
    {
        var lines = new List<string>();
        using RegistryKey? root = Registry.LocalMachine.OpenSubKey(CaptureRoot);
        if (root is null) { lines.Add("读不到 Capture 根键"); return lines; }

        string[] ids = root.GetSubKeyNames();
        if (index >= ids.Length) { lines.Add($"端点序号 {index} 超范围（共 {ids.Length}）"); return lines; }

        using RegistryKey? deviceKey = root.OpenSubKey(ids[index]);
        using RegistryKey? properties = deviceKey?.OpenSubKey("Properties");
        if (properties is null) { lines.Add("没有 Properties 子键"); return lines; }

        string[] names = properties.GetValueNames();
        lines.Add($"端点 {ids[index]}：Properties 下 {names.Length} 个值");
        foreach (string name in names)
        {
            object? value = properties.GetValue(name);
            string kind = value is byte[] blob ? $"byte[{blob.Length}]" :
                          value is null ? "null" : $"{value.GetType().Name}";
            string extra = string.Empty;
            if (value is byte[] b && b.Length >= 8)
            {
                extra = $"  → nChannels={BitConverter.ToUInt16(b, 2)} rate={BitConverter.ToUInt32(b, 4)}"
                      + $" 前8字节={BitConverter.ToString(b, 0, 8)}";
            }
            lines.Add($"   {name} = {kind}{extra}");
        }
        return lines;
    }

    /// <summary>列出所有音频采集端点（含未插入/已禁用的，方便判断"卡没插好"）</summary>
    public static IReadOnlyList<AudioDeviceInfo> Enumerate()
    {
        var devices = new List<AudioDeviceInfo>();

        using RegistryKey? root = Registry.LocalMachine.OpenSubKey(CaptureRoot);
        if (root is null)
        {
            return devices;
        }

        foreach (string id in root.GetSubKeyNames())
        {
            using RegistryKey? deviceKey = root.OpenSubKey(id);
            using RegistryKey? properties = deviceKey?.OpenSubKey("Properties");
            if (deviceKey is null || properties is null)
            {
                continue;
            }

            // ⚠️ DeviceState 是个位掩码（低位才表示状态：1 在用 / 2 禁用 / 4 未插入 / 8 不可用），
            //    实测直接读会拿到 0x31000004 这种值 —— 必须只看低 4 位。
            int state = deviceKey.GetValue("DeviceState") is int value ? value & 0xF : 0;

            // 🔴 注册表里的**值名大小写不固定**（同一台机器上友好名是小写 GUID、格式块是大写 GUID），
            //    而 RegistryKey.GetValue 是**大小写敏感**的 —— 直接按硬编码字符串取会拿到 null
            //    （实测：声道数全解析成 0）。所以这里枚举值名、按"GUID 部分 + PID"不区分大小写匹配。
            var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (string valueName in properties.GetValueNames())
            {
                values[valueName] = properties.GetValue(valueName);
            }

            string name = FindByPkey(values, "a45c254e-df1c-4efd-8020-67d146a850e0", 2) as string
                          ?? $"（无名称端点 {id}）";
            (int channels, int rate) = ParseFormat(FindByPkey(values, "f19f064d-082c-4e27-bc73-6882a1bb8e4c", 0) as byte[]);
            (int oemChannels, int oemRate) = ParseFormat(FindByPkey(values, "f19f064d-082c-4e27-bc73-6882a1bb8e4c", 1) as byte[]);

            // 兜底：值名在各机器上不统一（大小写、PID 都可能不一样），
            // 所以再扫一遍所有二进制值，凡是能解析出"合理采样率 + 合理声道数"的 WAVEFORMATEX 就采用。
            // 这一条不依赖任何命名约定，比按 PKEY 死找稳。
            if (channels <= 0)
            {
                foreach (object? candidate in values.Values)
                {
                    if (candidate is not byte[] blob)
                    {
                        continue;
                    }
                    (int c, int r) = ParseFormat(blob);
                    if (c >= 1 && c <= 64 && r >= 8000 && r <= 384000)
                    {
                        channels = c;
                        rate = r;
                        break;
                    }
                }
            }

            devices.Add(new AudioDeviceInfo(id, name, channels, rate, oemChannels, oemRate, state));
        }

        // 声道多的排前面（要凑 8ch 时一眼能看到哪台设备够用），再按名字稳定排序
        return devices
            .OrderByDescending(d => d.Channels)
            .ThenBy(d => d.FriendlyName, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// 按 PKEY 的 "GUID,PID" 取值，**不区分大小写**（注册表里的值名大小写不固定）。
    /// 找不到返回 null —— 调用方按"这个端点没写这一项"处理，不当错误。
    /// </summary>
    private static object? FindByPkey(IReadOnlyDictionary<string, object?> values, string guid, int pid)
    {
        string suffix = "," + pid;
        foreach ((string key, object? value) in values)
        {
            if (!key.EndsWith(suffix, StringComparison.Ordinal))
            {
                continue;
            }
            if (key.TrimStart('{').StartsWith(guid, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }
        return null;
    }

    /// <summary>
    /// 解析 WAVEFORMATEX（含 EXTENSIBLE）：wFormatTag(0,2) nChannels(2,2) nSamplesPerSec(4,4)…
    /// 只取我们关心的前 8 个字节；格式不对就返回 (0,0) 而不是抛异常。
    /// </summary>
    private static (int Channels, int SampleRate) ParseFormat(byte[]? blob)
    {
        if (blob is null || blob.Length < 8)
        {
            return (0, 0);
        }

        int channels = BitConverter.ToUInt16(blob, 2);
        int sampleRate = (int)BitConverter.ToUInt32(blob, 4);
        return (channels, sampleRate);
    }
}
