//
//  LutCube.cs
//  VideoScopePad.Win
//
//  .cube LUT 解析 + D3D11 纹理资源。逐条对应 iPad 版 `Render/LUTCube.swift`
//  的 CubeLUT / CubeLUTParser（同样的关键字、同样的域语义、同样的尺寸上限与报错措辞）。
//
//  .cube（Adobe/Resolve 通用）要点：
//    · TITLE "..."            可选标题
//    · LUT_1D_SIZE n          1D LUT 条目数（每行一个 RGB 三元组）
//    · LUT_3D_SIZE n          3D LUT 边长（n³ 个三元组）
//    · DOMAIN_MIN / DOMAIN_MAX          三个分量各自的定义域（默认 0…1）
//    · LUT_1D_INPUT_RANGE / LUT_3D_INPUT_RANGE  用两个值同时给 min/max（等价写法）
//    · 数据行的顺序：**红最快**，然后绿，最后蓝 —— 正好就是 D3D 3D 纹理
//      x=红、y=绿、z=蓝、x 最快的线性排布，所以可以直接照抄进纹理，不用重排。
//
//  纹理格式用 RGBA32F：65³ 也就 1.1 MB，换来的是"解析出来什么值就是什么值"，
//  不必再为 half 舍入误差解释半天（iPad 那边为了显存用了 Float16，规则本身一致）。
//

using System.Globalization;
using System.IO;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace VideoScopePad.Win.Render;

/// <summary>解析后的 LUT。</summary>
public sealed class CubeLut
{
    public string Title { get; init; } = string.Empty;
    public int Size1D { get; init; }
    public int Size3D { get; init; }
    public (float R, float G, float B) DomainMin { get; init; } = (0f, 0f, 0f);
    public (float R, float G, float B) DomainMax { get; init; } = (1f, 1f, 1f);

    /// <summary>1D LUT：Size1D × 4（RGBA，A 恒为 1）</summary>
    public float[] Data1D { get; init; } = Array.Empty<float>();

    /// <summary>3D LUT：Size3D³ × 4（RGBA，红最快）</summary>
    public float[] Data3D { get; init; } = Array.Empty<float>();

    public bool Has1D => Size1D > 0 && Data1D.Length >= Size1D * 4;
    public bool Has3D => Size3D > 1 && Data3D.Length >= Size3D * Size3D * Size3D * 4;
    public bool HasContent => Has1D || Has3D;

    public string Summary => Has3D
        ? $"{Title}（3D {Size3D}³" + (Has1D ? $" + 1D {Size1D}" : string.Empty) + "）"
        : Has1D ? $"{Title}（1D {Size1D}）" : $"{Title}（空）";
}

/// <summary>.cube 解析错误（措辞与 iPad 版一致，便于两边对照）。</summary>
public sealed class CubeLutException : Exception
{
    public CubeLutException(string message) : base(message) { }
}

public static class CubeLutParser
{
    /// <summary>3D LUT 边长上限（与 iPad 版一致；再大显存与采样开销都不划算）</summary>
    public const int MaxSize3D = 129;

    public static CubeLut Load(string path)
    {
        try
        {
            string text = File.ReadAllText(path);
            return Parse(text);
        }
        catch (CubeLutException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new CubeLutException($"无法读取文件：{ex.Message}");
        }
    }

    public static CubeLut Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new CubeLutException("文件里没有任何 LUT 内容");
        }

        string title = string.Empty;
        int size1D = 0, size3D = 0;
        (float, float, float) domainMin = (0f, 0f, 0f);
        (float, float, float) domainMax = (1f, 1f, 1f);
        var raw1D = new List<float>();
        var raw3D = new List<float>();

        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            string upper = line.ToUpperInvariant();

            if (upper.StartsWith("TITLE", StringComparison.Ordinal))
            {
                title = FirstQuoted(line) ?? string.Empty;
                continue;
            }
            if (upper.StartsWith("LUT_1D_SIZE", StringComparison.Ordinal))
            {
                size1D = LastInteger(line);
                continue;
            }
            if (upper.StartsWith("LUT_3D_SIZE", StringComparison.Ordinal))
            {
                size3D = LastInteger(line);
                continue;
            }
            if (upper.StartsWith("LUT_1D_INPUT_RANGE", StringComparison.Ordinal)
                || upper.StartsWith("LUT_3D_INPUT_RANGE", StringComparison.Ordinal))
            {
                List<float> values = Floats(line);
                if (values.Count >= 2)
                {
                    domainMin = (values[0], values[0], values[0]);
                    domainMax = (values[1], values[1], values[1]);
                }
                continue;
            }
            if (upper.StartsWith("DOMAIN_MIN", StringComparison.Ordinal))
            {
                List<float> values = Floats(line);
                if (values.Count >= 3)
                {
                    domainMin = (values[0], values[1], values[2]);
                }
                continue;
            }
            if (upper.StartsWith("DOMAIN_MAX", StringComparison.Ordinal))
            {
                List<float> values = Floats(line);
                if (values.Count >= 3)
                {
                    domainMax = (values[0], values[1], values[2]);
                }
                continue;
            }

            // 其余以字母开头的行按关键字忽略（兼容各家生成器的私有声明）
            if (line.Length > 0 && char.IsLetter(line[0]))
            {
                continue;
            }

            List<float> numbers = Floats(line);
            if (numbers.Count >= 3)
            {
                if (size1D > 0 && raw1D.Count < size1D * 3)
                {
                    raw1D.Add(numbers[0]);
                    raw1D.Add(numbers[1]);
                    raw1D.Add(numbers[2]);
                }
                else
                {
                    raw3D.Add(numbers[0]);
                    raw3D.Add(numbers[1]);
                    raw3D.Add(numbers[2]);
                }
            }
        }

        if (size1D <= 0 && size3D <= 0)
        {
            throw new CubeLutException("文件里没有 LUT_1D_SIZE 或 LUT_3D_SIZE 声明");
        }
        if (size3D > MaxSize3D)
        {
            throw new CubeLutException($"LUT 尺寸 {size3D} 过大（建议 ≤ 65，上限 {MaxSize3D}）");
        }

        float[] data1D = Array.Empty<float>();
        if (size1D > 0)
        {
            int expected = size1D * 3;
            if (raw1D.Count < expected)
            {
                throw new CubeLutException($"LUT 数据不完整：1D LUT 需要 {expected} 个数值，实际 {raw1D.Count}");
            }
            data1D = new float[size1D * 4];
            for (int index = 0; index < size1D; index++)
            {
                data1D[index * 4 + 0] = raw1D[index * 3 + 0];
                data1D[index * 4 + 1] = raw1D[index * 3 + 1];
                data1D[index * 4 + 2] = raw1D[index * 3 + 2];
                data1D[index * 4 + 3] = 1f;
            }
        }

        float[] data3D = Array.Empty<float>();
        if (size3D > 1)
        {
            int count = size3D * size3D * size3D;
            int expected = count * 3;
            if (raw3D.Count < expected)
            {
                throw new CubeLutException(
                    $"LUT 数据不完整：3D LUT 需要 {expected} 个数值（{size3D}³），实际 {raw3D.Count}");
            }
            data3D = new float[count * 4];
            for (int index = 0; index < count; index++)
            {
                data3D[index * 4 + 0] = raw3D[index * 3 + 0];
                data3D[index * 4 + 1] = raw3D[index * 3 + 1];
                data3D[index * 4 + 2] = raw3D[index * 3 + 2];
                data3D[index * 4 + 3] = 1f;
            }
        }

        return new CubeLut
        {
            Title = title,
            Size1D = size1D,
            Size3D = size3D,
            DomainMin = domainMin,
            DomainMax = domainMax,
            Data1D = data1D,
            Data3D = data3D,
        };
    }

    /// <summary>生成一个恒等 3D LUT（没载入 LUT 时占位用，保证采样结果与原图一致）</summary>
    public static CubeLut Identity3D(int size = 2)
    {
        int count = size * size * size;
        var data = new float[count * 4];
        int index = 0;
        for (int b = 0; b < size; b++)
        {
            for (int g = 0; g < size; g++)
            {
                for (int r = 0; r < size; r++)      // 红最快
                {
                    data[index++] = size == 1 ? 0f : r / (float)(size - 1);
                    data[index++] = size == 1 ? 0f : g / (float)(size - 1);
                    data[index++] = size == 1 ? 0f : b / (float)(size - 1);
                    data[index++] = 1f;
                }
            }
        }
        return new CubeLut { Title = "恒等", Size3D = size, Data3D = data };
    }

    private static string? FirstQuoted(string line)
    {
        int start = line.IndexOf('"');
        if (start < 0)
        {
            return null;
        }
        int end = line.IndexOf('"', start + 1);
        return end < 0 ? line[(start + 1)..] : line[(start + 1)..end];
    }

    private static int LastInteger(string line)
    {
        int result = 0;
        foreach (string token in line.Split(' ', '\t'))
        {
            if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            {
                result = value;
            }
        }
        return result;
    }

    private static List<float> Floats(string line)
    {
        var values = new List<float>();
        foreach (string token in line.Split(' ', '\t'))
        {
            if (float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
            {
                values.Add(value);
            }
        }
        return values;
    }
}

/// <summary>
/// 把解析好的 LUT 变成 D3D11 纹理（3D → t1、1D → t2，与 DisplayShaders.hlsl 里的槽位一致）。
/// 只在载入 LUT 时创建，之后每帧只是绑定。
/// </summary>
public sealed class LutResource : IDisposable
{
    private readonly ID3D11Device _device;

    public ID3D11Texture3D? Texture3D { get; private set; }
    public ID3D11ShaderResourceView? Srv3D { get; private set; }
    public ID3D11Texture2D? Texture1D { get; private set; }
    public ID3D11ShaderResourceView? Srv1D { get; private set; }

    public CubeLut Cube { get; private set; }
    public int Size3D => Cube.Has3D ? Cube.Size3D : 0;
    public int Size1D => Cube.Has1D ? Cube.Size1D : 0;

    /// <summary>
    /// 着色器里的 lutDomain 是标量：x = min、y = max（内部 d = saturate((c - min) * 1/(max-min))）。
    /// 逐通道定义域不一致时取 R 通道（着色器本来不支持逐通道），并在摘要里标注。
    /// </summary>
    public (float Min, float Max) ScalarDomain => (Cube.DomainMin.R, Cube.DomainMax.R);

    /// <summary>
    /// 旧写法（逐通道偏移/缩放），保留给需要它的调用方；着色器用的是 <see cref="ScalarDomain"/>。
    /// </summary>
    public (float OffsetR, float OffsetG, float OffsetB, float ScaleR, float ScaleG, float ScaleB) DomainMapping
    {
        get
        {
            (float minR, float minG, float minB) = Cube.DomainMin;
            (float maxR, float maxG, float maxB) = Cube.DomainMax;
            static (float Offset, float Scale) One(float min, float max)
            {
                float span = max - min;
                if (MathF.Abs(span) < 1e-6f)
                {
                    return (0f, 1f);
                }
                return (-min / span, 1f / span);
            }
            (float offsetR, float scaleR) = One(minR, maxR);
            (float offsetG, float scaleG) = One(minG, maxG);
            (float offsetB, float scaleB) = One(minB, maxB);
            return (offsetR, offsetG, offsetB, scaleR, scaleG, scaleB);
        }
    }

    public LutResource(ID3D11Device device, CubeLut? cube)
    {
        _device = device;
        Cube = cube ?? CubeLutParser.Identity3D(2);
        Upload();
    }

    /// <summary>
    /// 补齐占位：**1D 与 3D 都必须各有一张纹理**。
    /// 着色器里 `if (lutParams.z > 1.5) 采样 1D`、`if (lutParams.y > 1.5) 采样 3D`，
    /// 但为了让槽位永远有合法资源（iPad 版也是这么做的：lut ?? placeholderLUT），
    /// 缺哪一路就补一张恒等纹理；否则绑空 SRV 采样是未定义行为。
    /// </summary>
    private void UploadPlaceholders()
    {
        if (Srv3D is null)
        {
            var identity = CubeLutParser.Identity3D(2);
            var desc = new Texture3DDescription
            {
                Width = 2, Height = 2, Depth = 2, MipLevels = 1,
                Format = Format.R32G32B32A32_Float,
                Usage = ResourceUsage.Immutable,
                BindFlags = BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None,
            };
            unsafe
            {
                fixed (float* data = identity.Data3D)
                {
                    uint rowPitch = (uint)(2 * 4 * sizeof(float));
                    var subresource = new SubresourceData((nint)data, rowPitch, rowPitch * 2);
                    Texture3D = _device.CreateTexture3D(desc, new[] { subresource });
                }
            }
            Srv3D = _device.CreateShaderResourceView(Texture3D);
        }

        if (Srv1D is null)
        {
            float[] ramp = { 0, 0, 0, 1, 1, 1, 1, 1 };
            var desc = new Texture2DDescription
            {
                Width = 2, Height = 1, MipLevels = 1, ArraySize = 1,
                Format = Format.R32G32B32A32_Float,
                Usage = ResourceUsage.Immutable,
                BindFlags = BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None,
            };
            unsafe
            {
                fixed (float* data = ramp)
                {
                    var subresource = new SubresourceData((nint)data, (uint)(2 * 4 * sizeof(float)));
                    Texture1D = _device.CreateTexture2D(desc, new[] { subresource });
                }
            }
            Srv1D = _device.CreateShaderResourceView(Texture1D);
        }

        UploadPlaceholders();
    }

    /// <summary>换一份 LUT（界面载入新文件时调用）</summary>
    public void Update(CubeLut cube)
    {
        ArgumentNullException.ThrowIfNull(cube);
        Cube = cube;
        Release();
        Upload();
    }

    /// <summary>上一次纹理上传失败的原因（空 = 正常）。LUT 出问题绝不能把监视拖下水。</summary>
    public string UploadError { get; private set; } = string.Empty;

    private void Upload()
    {
        try
        {
            UploadCore();
        }
        catch (Exception ex)
        {
            UploadError = $"{ex.GetType().Name}: {ex.Message}";
            Release();
        }
    }

    private void UploadCore()
    {
        if (Cube.Has3D)
        {
            int size = Cube.Size3D;
            var desc = new Texture3DDescription
            {
                Width = (uint)size,
                Height = (uint)size,
                Depth = (uint)size,
                MipLevels = 1,
                Format = Format.R32G32B32A32_Float,
                Usage = ResourceUsage.Immutable,
                BindFlags = BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None,
            };

            // ⚠️ 必须用 SubresourceData 一次性给全（Immutable 不能 UpdateSubresource），
            //    而且不能把 pin 的指针留到调用之后 —— 这里用 fixed 包住整段调用。
            unsafe
            {
                fixed (float* data = Cube.Data3D)
                {
                    // ⚠️ 3D 纹理的 SubresourceData 必须给**三**个参数（rowPitch 与 slicePitch），
                    //    只给 rowPitch 会得到 E_INVALIDARG（实测：程序启动就报参数错误）。
                    uint rowPitch = (uint)(size * 4 * sizeof(float));
                    uint slicePitch = (uint)(size * size * 4 * sizeof(float));
                    var subresource = new SubresourceData((nint)data, rowPitch, slicePitch);
                    Texture3D = _device.CreateTexture3D(desc, new[] { subresource });
                }
            }
            Srv3D = _device.CreateShaderResourceView(Texture3D);
        }

        if (Cube.Has1D)
        {
            int size = Cube.Size1D;
            var desc = new Texture2DDescription
            {
                Width = (uint)size,
                Height = 1,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R32G32B32A32_Float,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Immutable,
                BindFlags = BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None,
            };
            unsafe
            {
                fixed (float* data = Cube.Data1D)
                {
                    var subresource = new SubresourceData((nint)data, (uint)(size * 4 * sizeof(float)));
                    Texture1D = _device.CreateTexture2D(desc, new[] { subresource });
                }
            }
            Srv1D = _device.CreateShaderResourceView(Texture1D);
        }

        UploadPlaceholders();
    }

    private void Release()
    {
        Srv3D?.Dispose();
        Texture3D?.Dispose();
        Srv1D?.Dispose();
        Texture1D?.Dispose();
        Srv3D = null;
        Texture3D = null;
        Srv1D = null;
        Texture1D = null;
    }

    public void Dispose() => Release();
}
