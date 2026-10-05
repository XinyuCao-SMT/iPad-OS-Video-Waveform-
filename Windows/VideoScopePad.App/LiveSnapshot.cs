//
//  LiveSnapshot.cs
//  VideoScopePad.App
//
//  把「最新一帧 + 刻度层」一起渲染成 PNG。
//
//  ⚠️ 为什么不用 RenderTargetBitmap 直接渲染窗口里的那棵树：
//     窗口里的画面是 Uniform 缩放的，比例与合成分辨率不一致时会被拉伸；
//     而快照必须是**合成分辨率的原图**（1:1，方便逐像素核对）。
//     所以这里现搭一个离屏视觉树：位图铺满 (0,0,w,h)，刻度用同一份布局画在同样的矩形里 ——
//     出图与屏幕上看到的完全一致。
//
//  ⚠️ 通道顺序：RenderTargetBitmap 用 Pbgra32（预乘 BGRA）。画面本身不透明
//     （alpha 恒为 255），刻度是 source-over 叠上去的，所以预乘不影响结果，
//     只需要把 B↔R 换回来给 PngWriter（它要 RGBA）。
//

using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VideoScopePad.Win.Render;

namespace VideoScopePad.App;

public static class LiveSnapshot
{
    /// <summary>
    /// 渲染「最新一帧 + 刻度」到 BGRA8 缓冲（内存序 B,G,R,A —— 与实时链路一致，便于逐像素核对）。
    /// 返回的缓冲区就是写进 PNG 的那份，所以自检断言验的就是最终产物。
    /// </summary>
    public static byte[] RenderBgra(LiveSession session, GraticuleOptions options,
                                    bool includeGraticule, out int width, out int height)
    {
        ArgumentNullException.ThrowIfNull(session);

        width = session.Width;
        height = session.Height;
        var frame = new byte[width * height * 4];

        // ⚠️ 取帧要**等一下新帧**，不能"拿不到就抛"：TryCopyLatestFrame 只在
        //    "上一帧已经被取走过"时才返回 false —— 断言之间连着取两次、或者真实设备
        //    正好卡在两次交换之间，都会命中（实测：真实采集卡 60 fps 下冻结参考那一段
        //    偶发抛"还没有可保存的帧"，合成源 400 fps 时不出现）。
        //    这里退避重试，链路真的停了才抛。
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!session.TryCopyLatestFrame(frame))
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new InvalidOperationException(
                    "还没有可保存的帧（链路停了？3 秒内没等到新帧）"
                    + $"　链路最后状态：帧数 {session.Stats.Frames}、"
                    + $"消息：{(string.IsNullOrEmpty(session.Stats.Message) ? "（空）" : session.Stats.Message)}");
            }
            Thread.Sleep(4);
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, frame, width * 4);
        bitmap.Freeze();

        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawImage(bitmap, new Rect(0, 0, width, height));
            if (includeGraticule && session.Layout.Panes.Count > 0)
            {
                ScopeGraticule.Draw(dc, session.Layout, new Rect(0, 0, width, height), options);
            }
        }

        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);

        int stride = width * 4;
        var bgra = new byte[stride * height];
        target.CopyPixels(bgra, stride, 0);
        return bgra;
    }

    /// <summary>BGRA8 → PNG（PngWriter 要 RGBA，所以只在这里换一次通道）</summary>
    public static void WritePng(string path, byte[] bgra, int width, int height)
    {
        var rgba = new byte[bgra.Length];
        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            rgba[i] = bgra[i + 2];
            rgba[i + 1] = bgra[i + 1];
            rgba[i + 2] = bgra[i];
            rgba[i + 3] = 255;
        }

        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        PngWriter.Write(path, width, height, rgba);
    }

    /// <summary>存一帧（含刻度）。返回写出的路径。</summary>
    public static string Save(LiveSession session, string path, GraticuleOptions options, bool includeGraticule = true)
    {
        byte[] bgra = RenderBgra(session, options, includeGraticule, out int width, out int height);
        WritePng(path, bgra, width, height);
        return path;
    }
}
