//
//  CaptureDeviceInfo.cs
//  VideoScopePad.Win
//
//  一次设备枚举的结果 —— 纯数据，不持有任何 COM 指针。
//
//  为什么不直接返回 IMFActivate：枚举时拿到的 IMFActivate 一旦 Dispose，
//  下面的对象就全悬空了；而界面/日志只想要「名字 + 路径」。真正要打开设备时
//  重新枚举一次拿新的 IMFActivate（见 CaptureDevice.OpenByName），代价可以忽略，
//  但能彻底避免「枚举结果被误当成长期句柄」这类 COM 生命周期坑。
//

namespace VideoScopePad.Win.Capture;

/// <summary>一台视频采集设备（UVC 采集卡 / 摄像头）的静态信息。</summary>
public sealed record CaptureDeviceInfo(
    int Index,
    string FriendlyName,
    string SymbolicLink,
    Guid SourceType,
    Guid Category,
    bool IsHardwareSource)
{
    public override string ToString() => $"[{Index}] {FriendlyName}";
}
