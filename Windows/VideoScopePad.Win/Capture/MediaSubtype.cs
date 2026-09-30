//
//  MediaSubtype.cs
//  VideoScopePad.Win
//
//  像素格式（MF 的「subtype」GUID）→ 人能看懂的名字。
//
//  两件事都在这里解决：
//   1) 常见格式用 Vortice.MediaFoundation.VideoFormatGuids 里的字段做精确匹配
//      （NV12 / YUY2 / MJPG / H264 / P010 …），这些是 MF 头文件里 DEFINE_MFVideoFormat 定义的标准值；
//   2) 没列进表的，按 MF 的编码规则**反解 FourCC**：
//        Data1 = FourCC 按小端打包的 32 位值
//        Data2 = 0x0000、Data3 = 0x0010、Data4 = 80 00 00 AA 00 38 9B 71
//      （也就是 {XXXXXXXX-0000-0010-8000-00AA00389B71} 这个尾巴）
//      —— 采集卡偶发的私有 FourCC 也能打印成 'XXXX' 而不是一串 GUID。
//
//  ⚠️ Vortice 里没有公开的 FourCC 结构体可用（VideoFormatGuids.FromFourCC 的参数类型
//  不在 dump 里暴露，别去猜），所以这里自己解码，反而更可控。
//

namespace VideoScopePad.Win.Capture;

/// <summary>MF 像素格式 GUID 的名字解析。</summary>
public static class MediaSubtype
{
    // MF 未压缩视频格式 GUID 的固定尾巴
    private static readonly byte[] MfVideoFormatTail = { 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71 };

    /// <summary>常见格式的精确名字。键取自 Vortice.MediaFoundation.VideoFormatGuids。</summary>
    private static readonly Dictionary<Guid, string> Known = new()
    {
        [Vortice.MediaFoundation.VideoFormatGuids.NV12] = "NV12",
        [Vortice.MediaFoundation.VideoFormatGuids.NV11] = "NV11",
        [Vortice.MediaFoundation.VideoFormatGuids.YUY2] = "YUY2",
        [Vortice.MediaFoundation.VideoFormatGuids.Uyvy] = "UYVY",
        [Vortice.MediaFoundation.VideoFormatGuids.Yvyu] = "YVYU",
        [Vortice.MediaFoundation.VideoFormatGuids.Yv12] = "YV12",
        [Vortice.MediaFoundation.VideoFormatGuids.I420] = "I420",
        [Vortice.MediaFoundation.VideoFormatGuids.Iyuv] = "IYUV",
        [Vortice.MediaFoundation.VideoFormatGuids.Mjpg] = "MJPG",
        [Vortice.MediaFoundation.VideoFormatGuids.H264] = "H264",
        [Vortice.MediaFoundation.VideoFormatGuids.H264Es] = "H264(ES)",
        [Vortice.MediaFoundation.VideoFormatGuids.H265] = "H265",
        [Vortice.MediaFoundation.VideoFormatGuids.Hevc] = "HEVC",
        [Vortice.MediaFoundation.VideoFormatGuids.P010] = "P010",
        [Vortice.MediaFoundation.VideoFormatGuids.P016] = "P016",
        [Vortice.MediaFoundation.VideoFormatGuids.P210] = "P210",
        [Vortice.MediaFoundation.VideoFormatGuids.P216] = "P216",
        [Vortice.MediaFoundation.VideoFormatGuids.Y210] = "Y210",
        [Vortice.MediaFoundation.VideoFormatGuids.Y216] = "Y216",
        [Vortice.MediaFoundation.VideoFormatGuids.Y410] = "Y410",
        [Vortice.MediaFoundation.VideoFormatGuids.Y416] = "Y416",
        [Vortice.MediaFoundation.VideoFormatGuids.Rgb24] = "RGB24",
        [Vortice.MediaFoundation.VideoFormatGuids.Rgb32] = "RGB32",
        [Vortice.MediaFoundation.VideoFormatGuids.Argb32] = "ARGB32",
        [Vortice.MediaFoundation.VideoFormatGuids.A2R10G10B10] = "A2R10G10B10",
        [Vortice.MediaFoundation.VideoFormatGuids.Rgb565] = "RGB565",
        [Vortice.MediaFoundation.VideoFormatGuids.Rgb555] = "RGB555",
        [Vortice.MediaFoundation.VideoFormatGuids.L8] = "L8",
        [Vortice.MediaFoundation.VideoFormatGuids.L16] = "L16",
        [Vortice.MediaFoundation.VideoFormatGuids.Mp4v] = "MP4V",
        [Vortice.MediaFoundation.VideoFormatGuids.Mpeg2] = "MPEG2",
    };

    /// <summary>格式名（表里查不到就反解 FourCC，再不行给 GUID）。</summary>
    public static string Name(Guid subtype)
    {
        if (Known.TryGetValue(subtype, out string? name))
        {
            return name;
        }
        if (TryDecodeFourCc(subtype, out string fourCc))
        {
            return fourCc;
        }
        return subtype.ToString("D").ToUpperInvariant();
    }

    /// <summary>MF 标准视频格式 GUID → FourCC 字符串。</summary>
    public static bool TryDecodeFourCc(Guid subtype, out string fourCc)
    {
        fourCc = string.Empty;

        // System.Guid 没有 Data1/Data2/Data3 属性（那是 Win32 GUID 结构体的字段），
        // 只能按字节还原：Data1 = 前 4 字节小端、Data2 = 5-6 字节、Data3 = 7-8 字节。
        byte[] bytes = subtype.ToByteArray();
        uint data1 = (uint)(bytes[0] | (bytes[1] << 8) | (bytes[2] << 16) | (bytes[3] << 24));
        ushort data2 = (ushort)(bytes[4] | (bytes[5] << 8));
        ushort data3 = (ushort)(bytes[6] | (bytes[7] << 8));

        if (data2 != 0x0000 || data3 != 0x0010)
        {
            return false;
        }
        for (int i = 0; i < 8; i++)
        {
            if (bytes[8 + i] != MfVideoFormatTail[i])
            {
                return false;
            }
        }

        Span<char> chars = stackalloc char[4];
        for (int i = 0; i < 4; i++)
        {
            byte b = (byte)(data1 >> (8 * i));
            chars[i] = b >= 0x20 && b < 0x7F ? (char)b : '?';
        }
        fourCc = new string(chars);
        return true;
    }

    /// <summary>
    /// 这个格式是不是「压缩码流」（MJPEG / H.264 这类，采集卡内部先编码再送出来）。
    /// 对示波器很关键：压缩格式的色度是 4:2:0 且经过有损编码，
    /// 但如果我们要的是「卡的原生输出」，那 MJPEG 就是必须解的一条路。
    /// </summary>
    public static bool IsCompressed(Guid subtype)
    {
        return subtype == Vortice.MediaFoundation.VideoFormatGuids.Mjpg
            || subtype == Vortice.MediaFoundation.VideoFormatGuids.H264
            || subtype == Vortice.MediaFoundation.VideoFormatGuids.H264Es
            || subtype == Vortice.MediaFoundation.VideoFormatGuids.H265
            || subtype == Vortice.MediaFoundation.VideoFormatGuids.Hevc
            || subtype == Vortice.MediaFoundation.VideoFormatGuids.Mpeg2
            || subtype == Vortice.MediaFoundation.VideoFormatGuids.Mp4v;
    }
}
