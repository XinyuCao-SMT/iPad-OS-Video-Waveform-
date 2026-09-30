//
//  PngWriter.cs
//  VideoScopePad.Win
//
//  极简 PNG 编码器（8 位 RGBA）。为什么要自己写：
//  本机没有 Visual Studio，也不想去引 System.Drawing / WIC 这套东西 ——
//  离屏自检要把渲染结果存成 PNG 给我（和用户）直接看图核对，
//  而 PNG 本身只需要 zlib deflate + CRC32，.NET 里都有，几十行就够。
//

using System.Buffers.Binary;
using System.IO.Compression;

namespace VideoScopePad.Win.Render;

public static class PngWriter
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFFu;
        foreach (byte b in data)
        {
            c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        }
        return c ^ 0xFFFFFFFFu;
    }

    /// <summary>
    /// 写 PNG。pixels 为 RGBA8、行优先、每行 width*4 字节（左上角为原点）。
    /// </summary>
    public static void Write(string path, int width, int height, ReadOnlySpan<byte> pixels)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "PNG 尺寸必须为正");
        }
        int stride = width * 4;
        if (pixels.Length < stride * height)
        {
            throw new ArgumentException("像素缓冲区不足", nameof(pixels));
        }

        // 每行前面加一个滤波字节（0 = None）
        var raw = new byte[(stride + 1) * height];
        for (int y = 0; y < height; y++)
        {
            raw[y * (stride + 1)] = 0;
            pixels.Slice(y * stride, stride).CopyTo(raw.AsSpan(y * (stride + 1) + 1, stride));
        }

        byte[] compressed;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            {
                z.Write(raw, 0, raw.Length);
            }
            compressed = ms.ToArray();
        }

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        fs.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header[..4], width);
        BinaryPrimitives.WriteInt32BigEndian(header.Slice(4, 4), height);
        header[8] = 8;   // bit depth
        header[9] = 6;   // color type: RGBA
        header[10] = 0;  // compression
        header[11] = 0;  // filter
        header[12] = 0;  // interlace
        WriteChunk(fs, "IHDR", header);

        WriteChunk(fs, "IDAT", compressed);
        WriteChunk(fs, "IEND", ReadOnlySpan<byte>.Empty);
    }

    private static void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);

        Span<byte> typeBytes = stackalloc byte[4];
        for (int i = 0; i < 4; i++)
        {
            typeBytes[i] = (byte)type[i];
        }
        stream.Write(typeBytes);
        stream.Write(data);

        var crcInput = new byte[4 + data.Length];
        typeBytes.CopyTo(crcInput);
        data.CopyTo(crcInput.AsSpan(4));
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(crcInput));
        stream.Write(crc);
    }
}
