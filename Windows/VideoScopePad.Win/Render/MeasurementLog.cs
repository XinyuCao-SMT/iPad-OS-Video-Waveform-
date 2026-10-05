//
//  MeasurementLog.cs
//  VideoScopePad.Win
//
//  读数 CSV 记录。逐条移植 iPad 版 `Stream/MeasurementLog.swift`：
//    · 表头与列序**完全一致**（同一份 CSV 在两边看起来要是一样的）
//    · 每秒最多记一行（interval = 1s），避免逐帧写盘
//    · 文件开头带 UTF-8 BOM —— Excel 打开中文列名才不会乱码（iPad 那边也是 "\u{FEFF}"）
//    · 报警列里若含逗号则整体加引号（它本身用 " / " 分隔，一般不会触发，但格式要对）
//
//  mV 列按 iPad 版同一换算：0 IRE = 0 mV、100 IRE = 700 mV（见 ScaleUnitExtensions）。
//

using System.Globalization;
using System.IO;
using System.Text;

namespace VideoScopePad.Win.Render;

/// <summary>一行读数记录</summary>
public sealed record MeasurementLogRow(
    DateTime TimeUtc,
    SignalMeasurement Measurement,
    IReadOnlyList<string> Warnings);

/// <summary>读数记录器（内存里攒行，导出时一次写文件）</summary>
public sealed class MeasurementLog
{
    private readonly List<MeasurementLogRow> _rows = new();
    private DateTime _lastAppendUtc = DateTime.MinValue;

    /// <summary>两次记录之间的最小间隔（秒）。默认 1 秒，与 iPad 版 interval 一致。</summary>
    public double IntervalSeconds { get; set; } = 1.0;

    public IReadOnlyList<MeasurementLogRow> Rows => _rows;

    public int Count => _rows.Count;

    public void Clear()
    {
        _rows.Clear();
        _lastAppendUtc = DateTime.MinValue;
    }

    /// <summary>
    /// 记一行（按 <see cref="IntervalSeconds"/> 节流）。返回是否真的记了 ——
    /// 自检就靠这个返回值验"节流生效"。
    /// </summary>
    public bool Append(SignalMeasurement measurement, IReadOnlyList<string> warnings, DateTime? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(measurement);
        warnings ??= Array.Empty<string>();

        DateTime now = nowUtc ?? DateTime.UtcNow;
        if (_lastAppendUtc != DateTime.MinValue && (now - _lastAppendUtc).TotalSeconds < IntervalSeconds)
        {
            return false;
        }

        _lastAppendUtc = now;
        _rows.Add(new MeasurementLogRow(now, measurement, warnings.ToArray()));
        return true;
    }

    /// <summary>CSV 全文（BOM + 表头 + 各行）。列序与 iPad 版逐字一致。</summary>
    public string CsvText()
    {
        var text = new StringBuilder();
        text.Append('\uFEFF');
        text.Append("时间,峰值白(IRE),峰值白(mV),黑位(IRE),黑位(mV),平均(IRE),动态范围(IRE),");
        text.Append("R峰值(IRE),G峰值(IRE),B峰值(IRE),色度峰值(%),超白(%),超黑(%),采样像素,报警\n");

        foreach (MeasurementLogRow row in _rows)
        {
            SignalMeasurement m = row.Measurement;
            string warnings = row.Warnings.Count == 0 ? string.Empty : string.Join(" / ", row.Warnings);
            if (warnings.Contains(','))
            {
                warnings = "\"" + warnings + "\"";
            }

            text.Append(string.Join(',',
                row.TimeUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                Ire(m.StableWhiteIre), Mv(m.StableWhiteIre),
                Ire(m.StableBlackIre), Mv(m.StableBlackIre),
                Ire(m.AverageIre), Ire(m.DynamicRangeIre),
                Ire(m.RedPeakIre), Ire(m.GreenPeakIre), Ire(m.BluePeakIre),
                m.PeakSaturationPercent.ToString("0", CultureInfo.InvariantCulture),
                m.AboveWhitePercent.ToString("0.000", CultureInfo.InvariantCulture),
                m.BelowBlackPercent.ToString("0.000", CultureInfo.InvariantCulture),
                m.SampledPixels.ToString(CultureInfo.InvariantCulture),
                warnings));
            text.Append('\n');
        }

        return text.ToString();
    }

    /// <summary>写到指定路径（UTF-8 **带 BOM**，与 iPad 版一致）。返回写出的路径。</summary>
    public string WriteToFile(string path)
    {
        string? folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        File.WriteAllText(path, CsvText(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }

    /// <summary>默认文件名：VideoScopePad-读数-yyyyMMdd-HHmmss.csv（与 iPad 版命名一致）</summary>
    public static string DefaultFileName(DateTime localNow)
        => $"VideoScopePad-读数-{localNow:yyyyMMdd-HHmmss}.csv";

    private static string Ire(double value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Mv(double ire)
        => (ire / 100.0 * Core.ScaleUnitExtensions.MillivoltPerHundredIre)
            .ToString("0", CultureInfo.InvariantCulture);
}
