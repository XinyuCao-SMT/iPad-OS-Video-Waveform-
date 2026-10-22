//
//  AudioEndpointMatcher.cs
//  VideoScopePad.Win
//
//  「音频跟着视频源走」的挑端点逻辑 —— 从界面里抽出来，原因有两个：
//    1. 用户要求**不提供音频源选择**，所以这段逻辑必须可靠、可测，不能在 UI 里靠试；
//    2. 抽成纯函数后，`mf-capture audiomatch` 可以用构造数据断言四种匹配路径，
//       不用真的插一台采集卡才知道对不对。
//
//  匹配优先级（写死，没有"用户手动选"这条退路）：
//    ① VID/PID 相同  —— 采集卡的视频端点与音频端点在 USB 上共用 VID/PID，最可靠
//    ② 名字长 token  —— 型号 / 序列号（如 00K0601910）出现在音频端点名里
//    ③ 系统默认采集端点 —— 内置摄像头没有"同型号"的音频端点，落到内置话筒
//    ④ 第一个可用端点 —— 保底，宁可采到一个也别静音
//

using System.Text.RegularExpressions;

namespace VideoScopePad.Win.Audio;

/// <summary>匹配原因（写进日志，也用于自检断言）</summary>
public enum AudioMatchKind
{
    None,
    DeviceId,      // ① VID/PID
    NameToken,     // ② 名字里的型号/序列号
    SystemDefault, // ③ 系统默认端点
    Fallback,      // ④ 第一个可用
}

public readonly record struct AudioMatch(WasapiEndpointFormat? Endpoint, AudioMatchKind Kind, string Detail)
{
    public bool Found => Endpoint is not null;
}

public static class AudioEndpointMatcher
{
    /// <summary>
    /// 按视频源挑音频端点。全部失败时返回 Fallback（第一个可用），没有端点时返回 None。
    /// </summary>
    public static AudioMatch Pick(IReadOnlyList<WasapiEndpointFormat> endpoints,
                                   string videoName,
                                   string? deviceKey,
                                   IReadOnlyList<AudioDeviceInfo> registry)
    {
        if (endpoints.Count == 0)
        {
            return new AudioMatch(null, AudioMatchKind.None, "没有可用的音频采集端点");
        }

        // ① VID/PID：视频符号链接里有 vid_1f6a&pid_15ae，
        //   音频端点的 PKEY_Device_InstanceId 是 USB\VID_1F6A&PID_15AE&... —— 同一台设备的可靠依据
        var vidPid = Regex.Match(deviceKey ?? string.Empty,
                                 "vid_([0-9a-f]{4})&pid_([0-9a-f]{4})",
                                 RegexOptions.IgnoreCase);
        if (vidPid.Success)
        {
            string vid = vidPid.Groups[1].Value;
            string pid = vidPid.Groups[2].Value;
            foreach (WasapiEndpointFormat endpoint in endpoints)
            {
                AudioDeviceInfo? match = registry.FirstOrDefault(d =>
                    GuidOf(d.Id).Equals(GuidOf(endpoint.EndpointId), StringComparison.OrdinalIgnoreCase));
                if (match is null || match.InstanceId.Length == 0)
                {
                    continue;
                }
                if (match.InstanceId.Contains($"VID_{vid}", StringComparison.OrdinalIgnoreCase)
                    && match.InstanceId.Contains($"PID_{pid}", StringComparison.OrdinalIgnoreCase))
                {
                    return new AudioMatch(endpoint, AudioMatchKind.DeviceId,
                                          $"VID/PID {vid}/{pid} → {match.FriendlyName}");
                }
            }
        }

        // ② 名字里的长 token（型号 / 序列号）
        var tokens = Regex.Matches(videoName, "[A-Za-z0-9]{6,}")
                          .Select(m => m.Value)
                          .Where(token => !token.All(char.IsLetter))
                          .ToList();
        if (tokens.Count > 0)
        {
            foreach (WasapiEndpointFormat endpoint in endpoints)
            {
                if (tokens.Any(token => endpoint.FriendlyName.Contains(token, StringComparison.OrdinalIgnoreCase)))
                {
                    return new AudioMatch(endpoint, AudioMatchKind.NameToken,
                                          $"名字含 {string.Join("/", tokens)}");
                }
            }
        }

        // ③ 系统默认采集端点（内置摄像头 ↔ 内置话筒）
        WasapiEndpointFormat? systemDefault = endpoints.FirstOrDefault(e => e.IsDefault);
        if (systemDefault is not null)
        {
            return new AudioMatch(systemDefault, AudioMatchKind.SystemDefault, "系统默认采集端点");
        }

        // ④ 保底
        return new AudioMatch(endpoints[0], AudioMatchKind.Fallback, "没有匹配项，用第一个可用端点");
    }

    /// <summary>
    /// 取端点 ID 末尾的 GUID（去大括号）。
    /// ⚠️ 不能整串比：WASAPI 给的是 "{0.0.1.00000000}.{guid}"，注册表子键是 "{0.0.0.00000000}.{guid}"
    /// —— 第三段不同，整串比会全部对不上（实测 31 个端点全都匹配失败）。只比最后的 GUID 才对。
    /// </summary>
    private static string GuidOf(string deviceId)
    {
        int index = deviceId.LastIndexOf('{');
        return (index >= 0 ? deviceId[index..] : deviceId).Trim('{', '}');
    }
}
