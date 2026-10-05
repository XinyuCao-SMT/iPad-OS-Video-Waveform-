//
//  DeviceWatcher.cs
//  VideoScopePad.Win
//
//  采集设备的「在场监视」——解决两件事：
//    ① 不再假设只有一张卡：设备列表来自实时枚举，界面按列表给用户选；
//    ② 热插拔：拔掉时状态要立刻变（而不是继续用失效的流），插回来要能自动恢复。
//
//  设计要点（为"一进多源"预留）：
//    · 标识用**符号链接**（跨插拔稳定），没有符号链接才退回设备名 —— 见 KeyOf。
//      用「序号」当标识是错的：插拔一次序号就会整体平移，选中的卡会变成另一台。
//    · 差异计算做成**纯函数** Diff(before, after)：不碰硬件就能断言，
//      自检里那几条设备逻辑就是这么验的（本机插拔不可自动化）。
//    · 监视器本身不持有设备句柄，只持有"最后一次枚举的列表 + 版本号"，
//      所以它天然可以每路源各持一个（多路预留）。
//

namespace VideoScopePad.Win.Capture;

/// <summary>一次刷新的结果：谁进来了、谁走了。</summary>
public sealed record DeviceListChange(
    IReadOnlyList<CaptureDeviceInfo> Added,
    IReadOnlyList<CaptureDeviceInfo> Removed)
{
    public bool AnyChange => Added.Count > 0 || Removed.Count > 0;

    public static DeviceListChange None { get; } =
        new(Array.Empty<CaptureDeviceInfo>(), Array.Empty<CaptureDeviceInfo>());
}

/// <summary>
/// 视频采集设备列表的在场监视器。渲染线程每秒刷新一次即可（枚举本身是毫秒级，
/// 但它会碰 Media Foundation，别在 UI 线程上做）。
/// </summary>
public sealed class DeviceWatcher
{
    private List<CaptureDeviceInfo> _devices = new();

    /// <summary>当前设备列表（最近一次刷新的快照）</summary>
    public IReadOnlyList<CaptureDeviceInfo> Devices => _devices;

    /// <summary>列表版本号：有增删才 +1。界面靠它决定要不要重建下拉，不必每帧比对。</summary>
    public int Revision { get; private set; }

    /// <summary>最近一次成功刷新的时刻（本地时间）</summary>
    public DateTime LastRefreshLocal { get; private set; } = DateTime.MinValue;

    /// <summary>最近一次刷新失败的原因（枚举本身失败时才有值；设备为 0 不算失败）</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// 设备的稳定标识：**优先符号链接**（同一张卡插到哪个口都是它自己），
    /// 没有符号链接才退回名字。⚠️ 千万别用序号：插拔一次序号整体平移，
    /// 用户"选中的卡"会悄悄变成另一台设备。
    /// </summary>
    public static string KeyOf(CaptureDeviceInfo device)
        => string.IsNullOrWhiteSpace(device.SymbolicLink) ? device.FriendlyName : device.SymbolicLink;

    /// <summary>
    /// 纯函数：两份键列表的差异（保持 after/before 的顺序，输出稳定、可断言）。
    /// 用「集合」而不是「出现次数」比较 —— 同名设备重复出现（NDI 那类虚拟摄像头）时
    /// 也能算对，不会把"仍在场"误判成"走了又来"。
    /// </summary>
    public static (IReadOnlyList<string> Added, IReadOnlyList<string> Removed) Diff(
        IReadOnlyList<string> before,
        IReadOnlyList<string> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var beforeSet = new HashSet<string>(before, StringComparer.Ordinal);
        var afterSet = new HashSet<string>(after, StringComparer.Ordinal);

        var added = new List<string>();
        foreach (string key in after)
        {
            if (beforeSet.Add(key))
            {
                added.Add(key);
            }
        }

        var removed = new List<string>();
        foreach (string key in before)
        {
            if (afterSet.Add(key))
            {
                removed.Add(key);
            }
        }

        return (added, removed);
    }

    /// <summary>重新枚举并更新列表；返回本次的增删（无变化时 <see cref="DeviceListChange.None"/>）。</summary>
    public DeviceListChange Refresh()
    {
        IReadOnlyList<CaptureDeviceInfo> fresh;
        try
        {
            fresh = VideoDeviceEnumerator.Enumerate();
            LastError = null;
        }
        catch (Exception ex)
        {
            // 枚举失败（例如 Media Foundation 被别的进程搞崩 / 驱动正在重启）：
            // 保留上一次的列表，只记错误 —— 这时候清空列表会让界面闪成"没有设备"。
            LastError = $"{ex.GetType().Name}: {ex.Message}";
            return DeviceListChange.None;
        }

        LastRefreshLocal = DateTime.Now;

        var beforeKeys = _devices.Select(KeyOf).ToList();
        var afterKeys = fresh.Select(KeyOf).ToList();
        (IReadOnlyList<string> addedKeys, IReadOnlyList<string> removedKeys) = Diff(beforeKeys, afterKeys);

        if (addedKeys.Count == 0 && removedKeys.Count == 0)
        {
            // 列表内容没变，但序号/名称可能有变（同一批设备重新枚举），直接换掉快照
            _devices = fresh.ToList();
            return DeviceListChange.None;
        }

        var addedSet = new HashSet<string>(addedKeys, StringComparer.Ordinal);
        var removedSet = new HashSet<string>(removedKeys, StringComparer.Ordinal);

        var added = SelectByKeys(fresh, addedSet);
        var removed = SelectByKeys(_devices, removedSet);

        _devices = fresh.ToList();
        Revision++;

        return new DeviceListChange(added, removed);
    }

    private static List<CaptureDeviceInfo> SelectByKeys(
        IReadOnlyList<CaptureDeviceInfo> source,
        HashSet<string> keys)
    {
        var list = new List<CaptureDeviceInfo>();
        foreach (CaptureDeviceInfo device in source)
        {
            if (keys.Contains(KeyOf(device)))
            {
                list.Add(device);
            }
        }
        return list;
    }

    /// <summary>按标识找设备；找不到返回 null（= 拔掉了或还没插上）。</summary>
    public CaptureDeviceInfo? Find(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        foreach (CaptureDeviceInfo device in _devices)
        {
            if (string.Equals(KeyOf(device), key, StringComparison.OrdinalIgnoreCase))
            {
                return device;
            }
        }
        return null;
    }

    /// <summary>
    /// 按标识或名字片段找设备：界面里记住的是标识，命令行里给的往往是片段。
    /// </summary>
    public CaptureDeviceInfo? FindByKeyOrName(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        CaptureDeviceInfo? byKey = Find(text);
        if (byKey is not null)
        {
            return byKey;
        }

        foreach (CaptureDeviceInfo device in _devices)
        {
            if (device.FriendlyName.Contains(text, StringComparison.OrdinalIgnoreCase))
            {
                return device;
            }
        }
        return null;
    }

    /// <summary>按序号找（命令行的兜底写法，序号会随插拔变化，只用于当次运行）</summary>
    public CaptureDeviceInfo? FindByIndex(int index)
        => _devices.FirstOrDefault(device => device.Index == index);
}
