//
//  compile-shaders —— Windows 版 HLSL 编译自检工具
//
//  用系统自带的 d3dcompiler_47.dll（经 Vortice.D3DCompiler 封装，不需要 Visual Studio / fxc.exe）
//  把 Render\Shaders 下的两套 HLSL 的**每一个入口点**都编译一遍：
//
//      ScopeKernels.hlsl   : 7 个 compute kernel      → cs_5_0
//      DisplayShaders.hlsl : 1 个顶点着色器 + 6 个片元 → vs_5_0 / ps_5_0
//
//  任何一个入口点编译失败就打印 HLSL 编译器的完整错误，最后返回非 0 退出码；
//  全部成功则打印每个入口点的字节码长度。
//
//  #include 的处理：
//    Vortice 暴露的 Include 回调接口（Vortice.Direct3D.Include）没法从托管侧塞进原生 D3DCompile
//    （实测编译器直接报 error X1505: No include handler specified），所以这里自己实现一层最小
//    #include 内联：按「被包含文件所在目录 → 下列 include 目录」的顺序查找文件，内联时插入
//    #line 指令，使报错的文件名/行号仍然指向真实的 .hlsl / .hlsli 文件。
//
//  为什么「内联完还要写临时文件，再按文件编译」：
//    Compiler.Compile(源码字符串, ...) 这条路在源码含非 ASCII 字符时会**截断**——
//    SharpGen 是按 .NET 字符数（而不是 UTF-8 字节数）告诉原生 D3DCompile 缓冲区长度的，
//    中文注释一个字 3 字节，于是编译器只看到前面一小段（实测 2154 字符 / 5874 字节的源码
//    报 error X3501: 'main': entrypoint not found）。本工程的 .hlsl/.hlsli 里有大量中文注释，
//    所以必须先把「已内联 #include 的完整源码」写成 UTF-8（无 BOM）临时文件，
//    再用 CompileFromFile 按文件编译（文件字节由 D3DCompile 自己读，不受这个问题影响）。
//    Windows 版渲染器运行时也要按同样的方式编译着色器（从文件读，别从字符串读）。
//

using System.Text;
using System.Text.RegularExpressions;
using Vortice.D3DCompiler;
using Vortice.Direct3D;

internal static class Program
{
    private sealed record ShaderFile(string RelativePath, (string Entry, string Profile)[] Entries);

    // 每个文件里的入口点清单：入口点名与 Metal 函数名一一对应（vs→CS / vs→VS / fs→PS 前缀），
    // 目标着色器模型按用途区分（compute / vertex / pixel = 5.0）。
    private static readonly ShaderFile[] Shaders =
    {
        new(@"VideoScopePad.Win\Render\Shaders\ScopeKernels.hlsl", new[]
        {
            ("CSAccumulateHistogram", "cs_5_0"),   // Metal: vsAccumulateHistogram
            ("CSFrameSignature", "cs_5_0"),        // Metal: vsFrameSignature
            ("CSAccumulateMeasurement", "cs_5_0"), // Metal: vsAccumulateMeasurement
            ("CSNormalizeWaveform", "cs_5_0"),     // Metal: vsNormalizeWaveform
            ("CSNormalizeOverlay", "cs_5_0"),      // Metal: vsNormalizeOverlay
            ("CSNormalizeParade", "cs_5_0"),       // Metal: vsNormalizeParade
            ("CSNormalizeVectorscope", "cs_5_0"),  // Metal: vsNormalizeVectorscope
        }),
        new(@"VideoScopePad.Win\Render\Shaders\DisplayShaders.hlsl", new[]
        {
            ("VSQuadVertex", "vs_5_0"),            // Metal: vsQuadVertex
            ("PSVideoBiPlanar", "ps_5_0"),         // Metal: fsVideoBiPlanar
            ("PSVideoBGRA", "ps_5_0"),             // Metal: fsVideoBGRA
            ("PSApplyLUTAndGrade", "ps_5_0"),      // Metal: fsApplyLUTAndGrade
            ("PSDisplay", "ps_5_0"),               // Metal: fsDisplay
            ("PSSolidColor", "ps_5_0"),            // Metal: fsSolidColor
            ("PSScopeTrace", "ps_5_0"),            // Metal: fsScopeTrace
        }),
    };

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        string windowsDir = args.Length > 0 ? Path.GetFullPath(args[0]) : FindWindowsDirectory();
        if (windowsDir == null)
        {
            Console.Error.WriteLine("找不到 Windows 目录（应当包含 VideoScopePad.Win\\Render\\Shaders）。");
            Console.Error.WriteLine("用法：compile-shaders [仓库里的 Windows 目录]");
            return 2;
        }

        string shaderDir = Path.Combine(windowsDir, "VideoScopePad.Win", "Render", "Shaders");
        // include 目录：源文件里写的是 #include "ShaderTypes.hlsli"（相对路径），
        // 编译器（本工具）按下面的顺序解析。
        string[] includeDirs = { shaderDir };
        // 内联 #include 之后的临时源码目录（编译失败时保留，便于对照编译器实际看到的内容）
        string preprocessDir = Path.Combine(Path.GetTempPath(), "videoscopepad-compile-shaders");

        Console.WriteLine("仓库 Windows 目录 : " + windowsDir);
        Console.WriteLine("着色器目录       : " + shaderDir);
        Console.WriteLine("include 目录     : " + string.Join(" ; ", includeDirs));
        Console.WriteLine("编译器           : d3dcompiler_47.dll（经 Vortice.D3DCompiler 调用）");
        Console.WriteLine("编译选项         : ShaderFlags.PackMatrixColumnMajor（与 .hlsli 里显式的 column_major 一致）");
        Console.WriteLine("预处理输出       : " + preprocessDir + "（内联 #include 之后的完整源码，UTF-8 无 BOM）");
        Console.WriteLine();

        int total = 0, failed = 0;
        var counts = new SortedDictionary<string, int>();

        Directory.CreateDirectory(preprocessDir);

        foreach (var shader in Shaders)
        {
            string path = Path.Combine(windowsDir, shader.RelativePath);
            Console.WriteLine("== " + shader.RelativePath);

            if (!File.Exists(path))
            {
                Console.Error.WriteLine("   文件不存在：" + path);
                failed += shader.Entries.Length;
                total += shader.Entries.Length;
                continue;
            }

            string preprocessedPath = Path.Combine(preprocessDir, Path.GetFileNameWithoutExtension(path) + ".pp.hlsl");
            try
            {
                string source = InlineIncludes(File.ReadAllText(path), path, includeDirs);
                // 必须写成文件再编译，理由见文件头「为什么内联完还要写临时文件」
                File.WriteAllText(preprocessedPath, source, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("   预处理 #include 失败：" + ex.Message);
                failed += shader.Entries.Length;
                total += shader.Entries.Length;
                continue;
            }

            foreach (var (entry, profile) in shader.Entries)
            {
                total++;
                counts.TryGetValue(profile, out int n);
                counts[profile] = n + 1;

                var result = Compiler.CompileFromFile(preprocessedPath, null, null, entry, profile,
                                                      ShaderFlags.PackMatrixColumnMajor, EffectFlags.None,
                                                      out Blob blob, out Blob errorBlob);

                string message = errorBlob != null && errorBlob.BufferSize > 0 ? errorBlob.AsString().Trim() : "";
                if (!result.Success || message.Length > 0)
                {
                    failed++;
                    Console.WriteLine($"   [{profile}] {entry}  —— 编译失败");
                    if (message.Length > 0)
                    {
                        Console.WriteLine(Indent(message));
                    }
                    else
                    {
                        Console.WriteLine("       " + result.Description + "（" + result.ApiCode + "）");
                    }
                }
                else
                {
                    Console.WriteLine($"   [{profile}] {entry,-24} ok  {blob.AsBytes().Length,6} 字节");
                }
            }
            Console.WriteLine();
        }

        Console.WriteLine("---- 汇总：" + (total - failed) + "/" + total + " 个入口点编译成功" +
                          "（" + string.Join(" / ", counts.Select(kv => kv.Key + " × " + kv.Value)) + "）");
        if (failed > 0)
        {
            Console.Error.WriteLine("有 " + failed + " 个入口点编译失败；编译器实际看到的源码保留在：" + preprocessDir);
            return 1;
        }

        try { Directory.Delete(preprocessDir, recursive: true); } catch { /* 临时目录删不掉不影响结果 */ }
        Console.WriteLine("全部入口点编译通过。");
        return 0;
    }

    // 从可执行文件目录一路向上找包含 VideoScopePad.Win\Render\Shaders 的目录。
    private static string FindWindowsDirectory()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "VideoScopePad.Win", "Render", "Shaders")))
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }
        }
        return null;
    }

    // 只处理「整行就是一个 #include "x" / <x>」的情况，其余 # 开头的行原样保留。
    private static readonly Regex IncludeRegex = new(@"^[ \t]*#[ \t]*include[ \t]*[<""]([^>""]+)[>""]",
                                                      RegexOptions.Multiline);

    private static string InlineIncludes(string source, string sourcePath, IReadOnlyList<string> includeDirs, int depth = 0)
    {
        if (depth > 16)
        {
            throw new InvalidOperationException("#include 嵌套太深（超过 16 层）：" + sourcePath);
        }

        string sourceDir = Path.GetDirectoryName(sourcePath) ?? ".";
        var builder = new StringBuilder();
        int lineNumber = 0;

        foreach (string line in Regex.Split(source, "\r\n|\n|\r"))
        {
            lineNumber++;
            Match match = IncludeRegex.Match(line);
            if (!match.Success)
            {
                builder.Append(line).Append('\n');
                continue;
            }

            string name = match.Groups[1].Value;
            string found = null;
            foreach (string dir in EnumerateSearchDirs(sourceDir, includeDirs))
            {
                string candidate = Path.GetFullPath(Path.Combine(dir, name));
                if (File.Exists(candidate))
                {
                    found = candidate;
                    break;
                }
            }

            if (found == null)
            {
                throw new FileNotFoundException($"找不到 #include \"{name}\"（{sourcePath}:{lineNumber}）");
            }

            // 内联后插入 #line，保证编译器报错仍然落在真实文件/行号上
            builder.Append("#line 1 \"").Append(found).Append("\"\n");
            builder.Append(InlineIncludes(File.ReadAllText(found), found, includeDirs, depth + 1));
            builder.Append("#line ").Append(lineNumber + 1).Append(" \"").Append(sourcePath).Append("\"\n");
        }

        return builder.ToString();
    }

    private static IEnumerable<string> EnumerateSearchDirs(string sourceDir, IReadOnlyList<string> includeDirs)
    {
        yield return sourceDir;
        foreach (string dir in includeDirs)
        {
            if (!string.Equals(Path.GetFullPath(dir), Path.GetFullPath(sourceDir), StringComparison.OrdinalIgnoreCase))
            {
                yield return dir;
            }
        }
    }

    private static string Indent(string text) =>
        "      " + text.Replace("\r\n", "\n").Replace("\n", "\n      ").TrimEnd();
}
