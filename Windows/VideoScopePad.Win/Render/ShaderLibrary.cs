//
//  ShaderLibrary.cs
//  VideoScopePad.Win
//
//  运行时把 HLSL 编译好，并建出所有管线状态（D3D11 没有 PSО，着色器 + 混合/光栅状态就够）。
//
//  ⚠️ 三个必须记住的坑（都是实测出来的，见 Windows/tools/compile-shaders/Program.cs 头部）：
//   1) 不要用 `Compiler.Compile(源码字符串, ...)`：Vortice 那个重载把源码按**字符数**封送，
//      本工程的 HLSL 里有大量中文注释（UTF-8 多字节），字符串会被**截断**，
//      报出来的是莫名其妙的 "X3004 / X3000"。
//   2) 所以先用内联 `#include` 预处理成单文件，再 **写临时文件 + CompileFromFile**。
//   3) 必须带 `ShaderFlags.PackMatrixColumnMajor`：着色器里用的是 Metal 的列主序约定
//      （mul(M, v) 展开成 cb[0]*v.x + cb[1]*v.y + cb[2]*v.z），这个标志是数学正确性的前提。
//

using System.Text;
using System.Text.RegularExpressions;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Mathematics;

namespace VideoScopePad.Win.Render;

public sealed class ShaderLibrary : IDisposable
{
    public ID3D11ComputeShader AccumulateHistogram { get; }
    public ID3D11ComputeShader AccumulateMeasurement { get; }
    public ID3D11ComputeShader FrameSignature { get; }
    public ID3D11ComputeShader NormalizeWaveform { get; }
    public ID3D11ComputeShader NormalizeOverlay { get; }
    public ID3D11ComputeShader NormalizeParade { get; }
    public ID3D11ComputeShader NormalizeVectorscope { get; }

    /// <summary>采集卡的 YUV → RGB 转换（见 Render/Shaders/ConvertShaders.hlsl）</summary>
    public ID3D11ComputeShader Yuy2ToRgb { get; }
    public ID3D11ComputeShader Nv12ToRgb { get; }

    public ID3D11VertexShader QuadVertex { get; }
    public ID3D11PixelShader VideoBiPlanar { get; }
    public ID3D11PixelShader VideoBgra { get; }
    public ID3D11PixelShader ApplyLutAndGrade { get; }
    public ID3D11PixelShader Display { get; }
    public ID3D11PixelShader SolidColor { get; }
    public ID3D11PixelShader ScopeTrace { get; }

    public ID3D11SamplerState LinearClampSampler { get; }

    /// <summary>编译日志（自检里打出来，便于定位哪个入口点挂了）</summary>
    public IReadOnlyList<string> Log => _log;

    private readonly List<string> _log = new();
    private readonly string _tempDirectory;
    private readonly ShaderFlags _flags;

    private ShaderLibrary(ID3D11Device device, string shaderDirectory, bool debug)
    {
        _flags = ShaderFlags.PackMatrixColumnMajor
               | (debug ? ShaderFlags.Debug | ShaderFlags.SkipOptimization : ShaderFlags.OptimizationLevel3);

        _tempDirectory = Path.Combine(Path.GetTempPath(), "vsp-shaders-" + Environment.ProcessId);
        Directory.CreateDirectory(_tempDirectory);

        string scopeKernels = Prepare(Path.Combine(shaderDirectory, "ScopeKernels.hlsl"));
        string display = Prepare(Path.Combine(shaderDirectory, "DisplayShaders.hlsl"));
        string convert = Prepare(Path.Combine(shaderDirectory, "ConvertShaders.hlsl"));

        AccumulateHistogram = device.CreateComputeShader(Compile(scopeKernels, "cs_5_0", "CSAccumulateHistogram"));
        AccumulateMeasurement = device.CreateComputeShader(Compile(scopeKernels, "cs_5_0", "CSAccumulateMeasurement"));
        FrameSignature = device.CreateComputeShader(Compile(scopeKernels, "cs_5_0", "CSFrameSignature"));
        NormalizeWaveform = device.CreateComputeShader(Compile(scopeKernels, "cs_5_0", "CSNormalizeWaveform"));
        NormalizeOverlay = device.CreateComputeShader(Compile(scopeKernels, "cs_5_0", "CSNormalizeOverlay"));
        NormalizeParade = device.CreateComputeShader(Compile(scopeKernels, "cs_5_0", "CSNormalizeParade"));
        NormalizeVectorscope = device.CreateComputeShader(Compile(scopeKernels, "cs_5_0", "CSNormalizeVectorscope"));

        Yuy2ToRgb = device.CreateComputeShader(Compile(convert, "cs_5_0", "CSYuy2ToRgb"));
        Nv12ToRgb = device.CreateComputeShader(Compile(convert, "cs_5_0", "CSNv12ToRgb"));

        QuadVertex = device.CreateVertexShader(Compile(display, "vs_5_0", "VSQuadVertex"));
        VideoBiPlanar = device.CreatePixelShader(Compile(display, "ps_5_0", "PSVideoBiPlanar"));
        VideoBgra = device.CreatePixelShader(Compile(display, "ps_5_0", "PSVideoBGRA"));
        ApplyLutAndGrade = device.CreatePixelShader(Compile(display, "ps_5_0", "PSApplyLUTAndGrade"));
        Display = device.CreatePixelShader(Compile(display, "ps_5_0", "PSDisplay"));
        SolidColor = device.CreatePixelShader(Compile(display, "ps_5_0", "PSSolidColor"));
        ScopeTrace = device.CreatePixelShader(Compile(display, "ps_5_0", "PSScopeTrace"));

        LinearClampSampler = device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            ComparisonFunc = ComparisonFunction.Never,
            MinLOD = 0,
            MaxLOD = float.MaxValue,
        });
    }

    public static ShaderLibrary Create(ID3D11Device device, string shaderDirectory, bool debug = false)
        => new(device, shaderDirectory, debug);

    /// <summary>内联 `#include "*.hlsli"` 并写到临时文件（原因见文件头第 1、2 条）</summary>
    private string Prepare(string hlslPath)
    {
        if (!File.Exists(hlslPath))
        {
            throw new FileNotFoundException(
                $"找不到着色器文件：{hlslPath}（确认 Render/Shaders/*.hlsl 已拷到输出目录）", hlslPath);
        }

        string directory = Path.GetDirectoryName(hlslPath)!;
        string source = File.ReadAllText(hlslPath, Encoding.UTF8);

        source = Regex.Replace(source, @"^\s*#include\s+""([^""]+)""\s*$", match =>
        {
            string includePath = Path.Combine(directory, match.Groups[1].Value);
            if (!File.Exists(includePath))
            {
                throw new FileNotFoundException($"着色器 #include 找不到：{includePath}", includePath);
            }
            string included = File.ReadAllText(includePath, Encoding.UTF8);
            return $"// ---- 内联自 {match.Groups[1].Value} ----\n{included}\n// ---- 内联结束 ----";
        }, RegexOptions.Multiline);

        string target = Path.Combine(_tempDirectory, Path.GetFileName(hlslPath));
        File.WriteAllText(target, source, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return target;
    }

    private Blob Compile(string preprocessedPath, string profile, string entryPoint)
    {
        var result = Compiler.CompileFromFile(preprocessedPath,
                                              null,
                                              null,
                                              entryPoint,
                                              profile,
                                              _flags,
                                              EffectFlags.None,
                                              out Blob blob,
                                              out Blob errorBlob);

        string message = errorBlob != null && errorBlob.BufferSize > 0 ? errorBlob.AsString().Trim() : "";

        if (!result.Success || blob is null || blob.BufferSize == 0)
        {
            _log.Add($"  ✗ [{profile}] {entryPoint}：{message}");
            throw new InvalidOperationException(
                $"着色器编译失败：{Path.GetFileName(preprocessedPath)} → {entryPoint} ({profile})\n" +
                (message.Length > 0 ? message : result.Description));
        }

        if (message.Length > 0)
        {
            // 编译警告也别悄悄吞掉
            _log.Add($"  ! [{profile}] {entryPoint}：{message}");
        }

        _log.Add($"  ✓ [{profile}] {entryPoint,-26} {blob.AsBytes().Length,6} 字节");
        return blob;
    }

    public void Dispose()
    {
        AccumulateHistogram.Dispose();
        AccumulateMeasurement.Dispose();
        FrameSignature.Dispose();
        NormalizeWaveform.Dispose();
        NormalizeOverlay.Dispose();
        NormalizeParade.Dispose();
        NormalizeVectorscope.Dispose();
        Yuy2ToRgb.Dispose();
        Nv12ToRgb.Dispose();
        QuadVertex.Dispose();
        VideoBiPlanar.Dispose();
        VideoBgra.Dispose();
        ApplyLutAndGrade.Dispose();
        Display.Dispose();
        SolidColor.Dispose();
        ScopeTrace.Dispose();
        LinearClampSampler.Dispose();

        try
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
        catch
        {
            // 临时目录删不掉不影响使用
        }
    }
}

/// <summary>管线状态：着色器 + 混合 + 光栅（D3D11 没有 Metal 那种 PSO，这里只是打包一下）</summary>
public sealed class PipelineLibrary : IDisposable
{
    public ID3D11VertexShader QuadVertex { get; }
    public ID3D11SamplerState Sampler { get; }

    public ID3D11PixelShader VideoBiPlanar { get; }
    public ID3D11PixelShader VideoBgra { get; }
    public ID3D11PixelShader ApplyLutAndGrade { get; }
    public ID3D11PixelShader Display { get; }
    public ID3D11PixelShader SolidColor { get; }
    public ID3D11PixelShader ScopeTrace { get; }

    /// <summary>画面显示：不透明</summary>
    public ID3D11BlendState Opaque { get; }
    /// <summary>面板背景：常规 alpha 混合（对应 iPad 版 blending: (true, false)）</summary>
    public ID3D11BlendState AlphaBlend { get; }
    /// <summary>示波器辉光：纯加法混合（对应 iPad 版 blending: (true, true)）</summary>
    public ID3D11BlendState Additive { get; }

    /// <summary>打开 ScissorEnable —— 每一格的内容绝对不许画到邻格里去</summary>
    public ID3D11RasterizerState Rasterizer { get; }

    public PipelineLibrary(ID3D11Device device, ShaderLibrary shaders)
    {
        QuadVertex = shaders.QuadVertex;
        Sampler = shaders.LinearClampSampler;
        VideoBiPlanar = shaders.VideoBiPlanar;
        VideoBgra = shaders.VideoBgra;
        ApplyLutAndGrade = shaders.ApplyLutAndGrade;
        Display = shaders.Display;
        SolidColor = shaders.SolidColor;
        ScopeTrace = shaders.ScopeTrace;

        Opaque = device.CreateBlendState(BlendDescription.Opaque);
        AlphaBlend = device.CreateBlendState(BlendDescription.NonPremultiplied);
        Additive = device.CreateBlendState(BlendDescription.Additive);

        Rasterizer = device.CreateRasterizerState(new RasterizerDescription
        {
            FillMode = FillMode.Solid,
            CullMode = CullMode.None,
            FrontCounterClockwise = false,
            DepthClipEnable = true,
            ScissorEnable = true,
            MultisampleEnable = false,
            AntialiasedLineEnable = false,
        });
    }

    public void Dispose()
    {
        Opaque.Dispose();
        AlphaBlend.Dispose();
        Additive.Dispose();
        Rasterizer.Dispose();
    }
}
