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
    /// 在枚举集合**存活期内**对目标设备调用 ActivateObject，拿到独立的 IMFMediaSource。
    ///
    /// 🔴 血泪坑（已用 tools/mf-capture probe 复现，别再犯）：
    ///   **不能把 IMFActivate 带出 IMFActivateCollection 的作用域。**
    ///   集合的 Dispose() 会连它交给你的子包装一起 Dispose —— 带出去的那个 IMFActivate
    ///   其 NativePointer 已经变成 0，再 ActivateObject 只会得到
    ///   `NullReferenceException`（SharpGen 的 ComObject 在指针为 0 时取 Vtbl 的表现，
    ///   不是标准的 COMException，看着特别像"代码写错了"）。
    ///   实测：集合内 ActivateObject ✓、带出后再 ActivateObject ✗（NativePointer = 0）。
    ///
    ///   本项目**首选**按符号链接走 MFCreateDeviceSource（见 CaptureDevice），
    ///   这个方法是「驱动不给符号链接」时的兜底。
    /// </summary>
    public static IMFMediaSource ActivateSourceAt(int deviceIndex)
    {
        using IMFActivateCollection collection = MediaFactory.MFEnumVideoDeviceSources();

        int index = 0;
        foreach (IMFActivate activate in collection)
        {
            if (index == deviceIndex)
            {
                // 返回的 IMFMediaSource 是独立对象，不受集合 Dispose 影响
                return activate.ActivateObject<IMFMediaSource>();
            }
            index++;
        }

        throw new InvalidOperationException($"没有序号为 {deviceIndex} 的视频采集设备");
    }
}
