//
//  Diag.cs
//  VideoScopePad.App
//
//  诊断日志：只为了"用户说卡住，但我复现不出来"这种情况。
//
//  为什么需要它：界面卡住有两类完全不同的原因 ——
//    ① UI 线程被堵（窗口变灰、Windows 标"无响应"）；
//    ② 渲染线程被堵（窗口还能点，但画面不动、布局切换不生效）。
//  光看现象分不清，所以两边各装一个"心跳"：UI 线程用 DispatcherTimer，
//  渲染线程在循环里打点；谁停了、停在哪一步（换布局 / 开设备 / 渲染 / 回读），
//  日志里一眼就能看出来。
//
//  写文件用追加 + 简单限长，不进仓库（放在 %LOCALAPPDATA%）。
//

using System.IO;
using System.Text;

namespace VideoScopePad.App;

/// <summary>轻量诊断日志（线程安全，失败不影响主流程）。</summary>
public static class Diag
{
    private static readonly object Gate = new();
    private static readonly StringBuilder Pending = new();
    private static DateTime _lastFlushUtc = DateTime.UtcNow;

    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VideoScopePad", "diagnostics.log");

    /// <summary>写一行（自动带时间戳与线程标记）。</summary>
    public static void Log(string message)
    {
        try
        {
            string line = $"{DateTime.Now:HH:mm:ss.fff} [t{Environment.CurrentManagedThreadId:00}] {message}";
            lock (Gate)
            {
                Pending.AppendLine(line);
                // 攒够或超过 500 ms 才落盘：诊断日志不该成为性能问题
                if (Pending.Length < 2048 && (DateTime.UtcNow - _lastFlushUtc).TotalMilliseconds < 500)
                {
                    return;
                }

                Flush();
            }
        }
        catch (Exception)
        {
            // 日志写不了就算了，绝不能因为它影响监视
        }
    }

    /// <summary>把缓冲落盘，并在文件过大时砍掉前半（保留最近约 200 KB）。</summary>
    public static void Flush()
    {
        try
        {
            if (Pending.Length == 0)
            {
                return;
            }

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.AppendAllText(Path, Pending.ToString(), Encoding.UTF8);
            Pending.Clear();
            _lastFlushUtc = DateTime.UtcNow;

            var info = new FileInfo(Path);
            if (info.Exists && info.Length > 400_000)
            {
                string[] lines = File.ReadAllLines(Path);
                File.WriteAllLines(Path, lines.Skip(lines.Length / 2), Encoding.UTF8);
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>本次启动的分隔行（日志是追加的，方便分辨哪一段是这次跑的）。</summary>
    public static void Session(string title)
    {
        Log(new string('=', 20) + " " + title + " " + new string('=', 20));
        Flush();
    }
}
