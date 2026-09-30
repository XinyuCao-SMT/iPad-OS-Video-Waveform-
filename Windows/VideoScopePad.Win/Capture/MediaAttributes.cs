//
//  MediaAttributes.cs
//  VideoScopePad.Win
//
//  属性读取的安全封装。
//
//  为什么需要：Vortice 的 GetString/GetGUID 这类实例方法在**属性不存在**时是抛
//  COMException（0x80070490 Element not found）而不是返回 null —— 采集卡是第三方
//  驱动，某些属性（比如硬件来源标记、类别）不给是常态，不能让它把枚举整个打断。
//  所以统一走这里：缺失就返回默认值，调用方自己判断「有没有」。
//
//  ⚠️ 数值属性统一用 MediaFactory.MFGetAttributeUInt32/UInt64 这几个静态方法，
//  它们自带 default 参数、缺失不抛（这是 MF 原生行为，比实例方法省一层 try）。
//

using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace VideoScopePad.Win.Capture;

/// <summary>IMFAttributes 的安全读取。</summary>
public static class MediaAttributes
{
    /// <summary>读字符串；属性不存在（或类型不对）时返回 null。</summary>
    public static string? TryGetString(this IMFAttributes attributes, Guid key)
    {
        try
        {
            return attributes.GetString(key);
        }
        catch (COMException)
        {
            return null;
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
            return null;
        }
    }

    /// <summary>读 GUID；属性不存在时返回 null。</summary>
    public static Guid? TryGetGuid(this IMFAttributes attributes, Guid key)
    {
        try
        {
            return attributes.GetGUID(key);
        }
        catch (COMException)
        {
            return null;
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
            return null;
        }
    }

    /// <summary>读 UInt32；缺失返回 default。</summary>
    public static uint GetUInt32OrDefault(this IMFAttributes attributes, Guid key, uint defaultValue = 0)
        => MediaFactory.MFGetAttributeUInt32(attributes, key, defaultValue);

    /// <summary>读 UInt64；缺失返回 default。</summary>
    public static ulong GetUInt64OrDefault(this IMFAttributes attributes, Guid key, ulong defaultValue = 0)
        => MediaFactory.MFGetAttributeUInt64(attributes, key, defaultValue);

    /// <summary>
    /// 读「打包成 UInt64 的两个 UInt32」（MF 的 size / ratio 都是这种编码）。
    /// 缺失返回 false。
    /// </summary>
    public static bool TryGetPackedPair(this IMFAttributes attributes, Guid key, out uint high, out uint low)
    {
        high = 0;
        low = 0;
        try
        {
            MediaFactory.MFGetAttribute2UInt32asUInt64(attributes, key, out high, out low);
            return true;
        }
        catch (COMException)
        {
            return false;
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
            return false;
        }
    }

    /// <summary>读「尺寸」(MF_MT_FRAME_SIZE 这类：high = 宽 / low = 高)。</summary>
    public static bool TryGetSize(this IMFAttributes attributes, Guid key, out uint width, out uint height)
    {
        width = 0;
        height = 0;
        if (!attributes.TryGetPackedPair(key, out uint high, out uint low))
        {
            return false;
        }
        width = high;
        height = low;
        return true;
    }

    /// <summary>读「比例」(MF_MT_FRAME_RATE 这类：high = 分子 / low = 分母)。</summary>
    public static bool TryGetRatio(this IMFAttributes attributes, Guid key, out uint numerator, out uint denominator)
    {
        numerator = 0;
        denominator = 1;
        if (!attributes.TryGetPackedPair(key, out uint high, out uint low))
        {
            return false;
        }
        numerator = high;
        denominator = low == 0 ? 1 : low;
        return true;
    }

    /// <summary>调试用：把属性键的 GUID 打印成 MF 头文件那种形式。</summary>
    public static string Describe(Guid key) => key.ToString("D").ToUpperInvariant();
}
