//
//  VideoDeviceEnumerator.cs
//  VideoScopePad.Win
//
//  枚举视频采集设备（UVC 采集卡 / 摄像头），对应 iPad 版的 AVCaptureDevice.DiscoverySession。
//
//  查证结论（已固化在 Windows/docs/vortice-mediafoundation-api.txt，不用再翻）：
//    · 属性键在 Vortice.MediaFoundation.CaptureDeviceAttributeKeys 里，**是裸 Guid 字段**
//      （不是 MediaAttributeKey<T>）：SourceTypeVidcap / FriendlyName /
//      SourceTypeVidcapSymbolicLink / SourceTypeVidcapCategory / SourceTypeVidcapHwSource …
//    · 枚举入口：MediaFactory.MFEnumVideoDeviceSources() → IMFActivateCollection（可 foreach、可 Dispose）
//    · 真正的「一台设备」是 IMFActivate，要打开就 ActivateObject&lt;IMFMediaSource&gt;()
//    · 这些 GUID 常量本身住在 mfobjects.h / mfidl.h，托管侧名字与 C 宏一一对应：
//        MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID          → SourceTypeVidcap
//        MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE                     → SourceType
//        MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME                   → FriendlyName
//        MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK → SourceTypeVidcapSymbolicLink
//
//  ⚠️ 调用前必须 MFStartup（见 MediaFoundationRuntime）。
//

using Vortice.MediaFoundation;

namespace VideoScopePad.Win.Capture;

/// <summary>视频采集设备枚举。</summary>
public static class VideoDeviceEnumerator
{
    /// <summary>
    /// 列出本机所有视频采集设备。
    /// </summary>
    public static IReadOnlyList<CaptureDeviceInfo> Enumerate()
    {
        var devices = new List<CaptureDeviceInfo>();

        // videoDeviceCategory 留空 = 默认类别（MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_CATEGORY 的默认值）
        using IMFActivateCollection collection = MediaFactory.MFEnumVideoDeviceSources();

        int index = 0;
        foreach (IMFActivate activate in collection)
        {
            using (activate)
            {
                devices.Add(new CaptureDeviceInfo(
                    Index: index,
                    FriendlyName: activate.TryGetString(CaptureDeviceAttributeKeys.FriendlyName) ?? $"（无名称设备 #{index}）",
                    SymbolicLink: activate.TryGetString(CaptureDeviceAttributeKeys.SourceTypeVidcapSymbolicLink) ?? string.Empty,
                    SourceType: activate.TryGetGuid(CaptureDeviceAttributeKeys.SourceType) ?? Guid.Empty,
                    Category: activate.TryGetGuid(CaptureDeviceAttributeKeys.SourceTypeVidcapCategory) ?? Guid.Empty,
                    IsHardwareSource: activate.GetUInt32OrDefault(CaptureDeviceAttributeKeys.SourceTypeVidcapHwSource) != 0));
            }
            index++;
        }

        return devices;
    }

    /// <summary>
    /// 按名字里的片段找设备（大小写不敏感）。界面里的「输入设备」下拉就是这么标的。
    /// 返回 null = 没找到。
    /// </summary>
    public static CaptureDeviceInfo? FindByName(string nameFragment)
    {
        ArgumentException.ThrowIfNullOrEmpty(nameFragment);

        foreach (CaptureDeviceInfo device in Enumerate())
        {
            if (device.FriendlyName.Contains(nameFragment, StringComparison.OrdinalIgnoreCase))
            {
                return device;
            }
        }
        return null;
    }

    /// <summary>
    /// 取出指定设备的 IMFActivate（**调用方负责 Dispose 与全部后续生命周期**）。
    /// 找不到返回 null。
    /// </summary>
    public static IMFActivate? TakeActivate(string nameFragment)
    {
        ArgumentException.ThrowIfNullOrEmpty(nameFragment);

        using IMFActivateCollection collection = MediaFactory.MFEnumVideoDeviceSources();
        int index = 0;
        foreach (IMFActivate activate in collection)
        {
            string name = activate.TryGetString(CaptureDeviceAttributeKeys.FriendlyName) ?? string.Empty;
            if (name.Contains(nameFragment, StringComparison.OrdinalIgnoreCase))
            {
                return activate;   // 不 Dispose：交出去
            }
            activate.Dispose();    // 不是它 → 立刻放掉
            index++;
        }
        return null;
    }

    /// <summary>
    /// 同上，按枚举序号取（界面下拉里选的就是这个序号）。
    /// </summary>
    public static IMFActivate? TakeActivateAt(int deviceIndex)
    {
        using IMFActivateCollection collection = MediaFactory.MFEnumVideoDeviceSources();
        int index = 0;
        foreach (IMFActivate activate in collection)
        {
            if (index == deviceIndex)
            {
                return activate;
            }
            activate.Dispose();
            index++;
        }
        return null;
    }
}
