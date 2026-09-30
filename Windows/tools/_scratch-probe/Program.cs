// 临时探针：确认「含中文注释的源码经字符串传给 D3DCompile 会被截断」。
using System;
using System.IO;
using System.Text;
using Vortice.D3DCompiler;
using Vortice.Direct3D;

internal static class Program
{
    private static int Main()
    {
        var ascii = new StringBuilder();
        for (int i = 0; i < 60; i++) ascii.Append("// padding comment line number " + i + " ................\n");
        ascii.Append("float4 main() : SV_Target { return float4(1,1,1,1); }\n");

        var chinese = new StringBuilder();
        for (int i = 0; i < 60; i++) chinese.Append("// 中文注释：用于测试编码与长度问题的一行注释，足够长，占字节数。\n");
        chinese.Append("float4 main() : SV_Target { return float4(1,1,1,1); }\n");

        Probe("纯 ASCII（字符串路径）", ascii.ToString());
        Probe("含中文（字符串路径）", chinese.ToString());

        // 把同样的中文源码写进临时文件，用 CompileFromFile 走文件路径
        string tmp = Path.Combine(Path.GetTempPath(), "vs_probe_utf8.hlsl");
        File.WriteAllText(tmp, chinese.ToString(), new UTF8Encoding(false));
        try
        {
            var blob = Compiler.CompileFromFile(tmp, null, null, "main", "ps_5_0", ShaderFlags.None, EffectFlags.None, out Blob code, out Blob err);
            string msg = err != null && err.BufferSize > 0 ? err.AsString().Trim() : "";
            Console.WriteLine((blob.Success && msg.Length == 0 ? "OK   " : "FAIL ") + "含中文（文件路径）  bytes=" + (code?.AsBytes().Length ?? 0));
            if (msg.Length > 0) Console.WriteLine("      " + msg);
        }
        catch (Exception ex) { Console.WriteLine("EX " + ex.Message); }
        File.Delete(tmp);
        return 0;
    }

    private static void Probe(string label, string source)
    {
        var result = Compiler.Compile(source, null, null, "main", "probe", "ps_5_0",
                                      ShaderFlags.None, EffectFlags.None, out Blob blob, out Blob err);
        string msg = err != null && err.BufferSize > 0 ? err.AsString().Trim() : "";
        Console.WriteLine((result.Success && msg.Length == 0 ? "OK   " : "FAIL ") + label +
                          "  chars=" + source.Length + " utf8bytes=" + Encoding.UTF8.GetByteCount(source) +
                          " bytes=" + (blob?.AsBytes().Length ?? 0));
        if (msg.Length > 0) Console.WriteLine("      " + msg.Replace("\n", "\n      "));
    }
}
