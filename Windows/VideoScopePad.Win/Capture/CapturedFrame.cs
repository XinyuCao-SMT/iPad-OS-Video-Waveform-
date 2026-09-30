//
//  CapturedFrame.cs
//  VideoScopePad.Win
//
//  从采集卡读出来的一帧（**已经拷进托管内存**）。
//
//  为什么立刻拷贝、而不是把 IMFMediaBuffer 的锁一直握着：
//  源读取器的媒体缓冲是设备驱动池子里循环复用的 —— 锁着不放会直接把采集管线堵死
//  （表现为帧率掉到很低或干脆不再来帧）。拷一份 1920×1080 的 YUY2 是 4 MB，
//  在 60 fps 下就是 248 MB/s 的内存带宽，现代机器上完全无感。
//  真正的零拷贝要等接进 D3D11（DXGI 缓冲 → 纹理），那是后面一步的事。
//
//  ⚠️ 时间戳单位是 **100 纳秒**（MF 的 hns，Hundred NanoSeconds），不是纳秒也不是毫秒。
//     实测帧率就是从相邻两帧的 hns 差算出来的（见 CaptureStats）。
//

namespace VideoScopePad.Win.Capture;

/// <summary>一帧原始码流 + 它的时间戳。</summary>
public sealed class CapturedFrame
{
    public CapturedFrame(byte[] data, int width, int height, int stride, long timestampHns, long durationHns, int rows = 0)
    {
        Data = data;
        Width = width;
        Height = height;
        Stride = stride;
        TimestampHns = timestampHns;
        DurationHns = durationHns;
        Rows = rows > 0 ? rows : height;
    }

    /// <summary>原始码流（YUY2 / NV12 等），按 Stride 分行存放。</summary>
    public byte[] Data { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>行跨距（字节）。**用的是驱动实际给的值**，不是 width×bpp 推出来的。</summary>
    public int Stride { get; }

    /// <summary>
    /// 拷进来的**缓冲行数**。YUY2 = 图像高度；NV12 = 高度 × 1.5
    /// （UV 交织平面跟在 Y 平面后面，共用同一个行跨距）。
    /// </summary>
    public int Rows { get; }

    /// <summary>展示时间戳（100 ns 单位）。</summary>
    public long TimestampHns { get; }

    /// <summary>声明时长（100 ns 单位）。</summary>
    public long DurationHns { get; }

    public double TimestampSeconds => TimestampHns / 10_000_000.0;

    /// <summary>这一帧拷进来的字节数。</summary>
    public int Length => Math.Abs(Stride) * Rows;

    public override string ToString()
        => $"{Width}×{Height} stride {Stride} rows {Rows} @ {TimestampSeconds:0.000000}s";
}
