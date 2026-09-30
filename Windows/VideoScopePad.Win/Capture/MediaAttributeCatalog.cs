//
//  MediaAttributeCatalog.cs
//  VideoScopePad.Win
//
//  「这个 GUID 是哪个 MF 属性？」—— 用**反射**从 Vortice 自己的属性键类里建表，
//  而不是手抄一份 mfapi.h。
//
//  为什么用反射：属性键类里的字段名与 MF 宏名是一一对应的
//  （MediaTypeAttributeKeys.VideoNominalRange ←→ MF_MT_VIDEO_NOMINAL_RANGE），
//  手抄一份迟早和包版本对不上；反射则永远和当前引用的 Vortice 一致。
//  代价是首次调用几百微秒，缓存一次就完了。
//
//  用途：formats 命令把某个媒体类型的属性集整个摊开打印，
//  于是「驱动到底给了哪些色彩元数据」是可查证的（而不是靠猜它没给）。
//

using System.Reflection;

namespace VideoScopePad.Win.Capture;

/// <summary>MF 属性 GUID ↔ 托管字段名。</summary>
public static class MediaAttributeCatalog
{
    private static readonly Lazy<IReadOnlyDictionary<Guid, string>> Names = new(BuildMap);

    private static IReadOnlyDictionary<Guid, string> BuildMap()
    {
        var map = new Dictionary<Guid, string>();

        // 我们关心的四类属性键（顺序 = 同名时谁先注册谁赢，这里不会重名）
        Collect(map, typeof(Vortice.MediaFoundation.MediaTypeAttributeKeys));
        Collect(map, typeof(Vortice.MediaFoundation.CaptureDeviceAttributeKeys));
        Collect(map, typeof(Vortice.MediaFoundation.SourceReaderAttributeKeys));
        Collect(map, typeof(Vortice.MediaFoundation.SampleAttributeKeys));

        return map;
    }

    private static void Collect(IDictionary<Guid, string> map, Type keyClass)
    {
        foreach (FieldInfo field in keyClass.GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.FieldType == typeof(Guid) && field.GetValue(null) is Guid guid)
            {
                map.TryAdd(guid, field.Name);
            }
        }
    }

    /// <summary>GUID → 字段名（形如 MediaTypeAttributeKeys.VideoNominalRange）。查不到返回 null。</summary>
    public static string? TryGetName(Guid key)
        => Names.Value.TryGetValue(key, out string? name) ? name : null;

    /// <summary>打印用：字段名，查不到就退化成 GUID。</summary>
    public static string Describe(Guid key)
        => TryGetName(key) is { } name ? $"{name}（{key:D}）" : key.ToString("D").ToUpperInvariant();
}
