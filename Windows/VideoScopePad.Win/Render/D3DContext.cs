//
//  D3DContext.cs
//  VideoScopePad.Win
//
//  D3D11 设备与「离屏出图」的公共部分。
//
//  为什么一上来就要有离屏出图：这台机器上我（AI）看不到窗口，
//  所有视觉结果必须能落到 PNG 里让我自己核对 —— 这也是 Windows 版比 iPad 版好做的地方，
//  不用再靠 CI 盲编 + 侧载 IPA 猜结果。
//

using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace VideoScopePad.Win.Render;

public sealed class D3DContext : IDisposable
{
    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public IDXGIFactory2 Factory { get; }
    public string AdapterName { get; }
    public FeatureLevel FeatureLevel { get; }

    private D3DContext(ID3D11Device device,
                       ID3D11DeviceContext context,
                       IDXGIFactory2 factory,
                       string adapterName,
                       FeatureLevel featureLevel)
    {
        Device = device;
        Context = context;
        Factory = factory;
        AdapterName = adapterName;
        FeatureLevel = featureLevel;
    }

    public static D3DContext Create(bool enableDebugLayer = false)
    {
        var flags = DeviceCreationFlags.BgraSupport;
        if (enableDebugLayer)
        {
            flags |= DeviceCreationFlags.Debug;
        }

        // 优先独显（本机是 RTX 4060），拿不到再退到默认适配器
        IDXGIAdapter? adapter = null;
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint i = 0; factory.EnumAdapters1(i, out var candidate).Success; i++)
            {
                // 注意：Vortice 的 EnumAdapters1 声明成 IDXGIAdapter，
                // 要读 Description1 得自己 QueryInterface 到 IDXGIAdapter1
                IDXGIAdapter1? adapter1 = null;
                try
                {
                    adapter1 = candidate.QueryInterface<IDXGIAdapter1>();
                }
                catch
                {
                    adapter1 = null;
                }

                var adapterFlags = adapter1?.Description1.Flags ?? AdapterFlags.None;
                adapter1?.Dispose();

                // 跳过软件适配器（WARP / 远程桌面虚拟显示）
                if ((adapterFlags & AdapterFlags.Software) == 0)
                {
                    adapter = candidate;
                    break;
                }
                candidate.Dispose();
            }
        }
        catch
        {
            adapter = null;
        }

        var levels = new[]
        {
            FeatureLevel.Level_11_1,
            FeatureLevel.Level_11_0,
            FeatureLevel.Level_10_1,
            FeatureLevel.Level_10_0,
        };

        D3D11.D3D11CreateDevice(adapter,
                                DriverType.Unknown,
                                flags,
                                levels,
                                out var device,
                                out var featureLevel,
                                out var context).CheckError();

        string adapterName = "未知适配器";
        try
        {
            using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
            using var parentAdapter = dxgiDevice.GetAdapter();
            using var parentAdapter1 = parentAdapter.QueryInterface<IDXGIAdapter1>();
            adapterName = parentAdapter1.Description1.Description.TrimEnd('\0');
        }
        catch
        {
            // 拿不到名字不影响使用
        }

        adapter?.Dispose();

        var factory2 = DXGI.CreateDXGIFactory2<IDXGIFactory2>(enableDebugLayer);
        return new D3DContext(device, context, factory2, adapterName, featureLevel);
    }

    /// <summary>建一张可以当渲染目标、也能被着色器采样的纹理</summary>
    public ID3D11Texture2D CreateRenderTarget(int width, int height, Format format = Format.R8G8B8A8_UNorm)
    {
        var desc = new Texture2DDescription
        {
            Width = (uint)Math.Max(width, 1),
            Height = (uint)Math.Max(height, 1),
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };
        return Device.CreateTexture2D(desc);
    }

    /// <summary>
    /// 建一张「给界面显示用」的合成纹理：内存序 B,G,R,A —— 正好是 WPF 的 Bgra32。
    /// 着色器照旧写 (R,G,B)（D3D 的分量按语义映射，格式只决定内存排列），
    /// 于是回读到 WPF 位图之间不需要任何通道交换。详见 ReadBackBgra8 的说明。
    /// </summary>
    public ID3D11Texture2D CreateDisplayTarget(int width, int height)
    {
        var desc = new Texture2DDescription
        {
            Width = (uint)Math.Max(width, 1),
            Height = (uint)Math.Max(height, 1),
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };
        return Device.CreateTexture2D(desc);
    }

    /// <summary>把 RGBA8 像素传到一张可采样的纹理（自检用的合成帧走这条路）</summary>
    public ID3D11Texture2D CreateTextureFromRgba8(int width, int height, ReadOnlySpan<byte> pixels, out int stride)
    {
        stride = width * 4;
        var desc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            CPUAccessFlags = CpuAccessFlags.None,
        };

        int length = stride * height;
        if (pixels.Length < length)
        {
            throw new ArgumentException("像素缓冲区不足", nameof(pixels));
        }

        unsafe
        {
            fixed (byte* dataPointer = pixels)
            {
                var texture = Device.CreateTexture2D(desc, new[] { new SubresourceData((nint)dataPointer, (uint)stride) });
                return texture;
            }
        }
    }

    /// <summary>把一张纹理读回 CPU（RGBA8）。自检出图与后面的读数回读都走它。</summary>
    public byte[] ReadBackRgba8(ID3D11Texture2D texture, out int width, out int height)
        => ReadBack(texture, Format.R8G8B8A8_UNorm, out width, out height);

    /// <summary>
    /// 把一张纹理读回 CPU，内存序为 **B,G,R,A**（= WPF 的 <c>PixelFormats.Bgra32</c>）。
    ///
    /// 为什么界面这条路要用 BGRA：WPF 没有 Rgba32 这种像素格式，而 Bgra32 是它的常客。
    /// 让 D3D 直接以 B8G8R8A8_UNORM 建合成纹理，着色器照旧写 (R,G,B)——D3D 的分量是按
    /// **语义**映射的（.x→R、.y→G、.z→B），格式只决定内存里的排列顺序 ——
    /// 于是回读出来的字节正好就是 WPF 要的顺序，界面那一层一次内存拷贝就够，
    /// 不用每帧在 UI 线程上做 200 万次通道交换。
    /// （反过来说：PNG 要 RGBA，所以存图那一步才需要交换，而存图是低频操作。）
    /// </summary>
    public byte[] ReadBackBgra8(ID3D11Texture2D texture, out int width, out int height)
        => ReadBack(texture, Format.B8G8R8A8_UNorm, out width, out height);

    /// <summary>按指定格式读回（staging 纹理格式必须与源一致）。</summary>
    public byte[] ReadBack(ID3D11Texture2D texture, Format format, out int width, out int height)
    {
        width = (int)texture.Description.Width;
        height = (int)texture.Description.Height;

        var desc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
        };

        using var staging = Device.CreateTexture2D(desc);
        Context.CopyResource(staging, texture);

        var mapped = Context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            int rowPitch = (int)mapped.RowPitch;
            var result = new byte[width * height * 4];
            unsafe
            {
                byte* src = (byte*)mapped.DataPointer;
                for (int y = 0; y < height; y++)
                {
                    new ReadOnlySpan<byte>(src + y * rowPitch, width * 4)
                        .CopyTo(result.AsSpan(y * width * 4, width * 4));
                }
            }
            return result;
        }
        finally
        {
            Context.Unmap(staging, 0);
        }
    }

    /// <summary>把一张渲染结果直接存成 PNG（自检出图）</summary>
    public void SavePng(ID3D11Texture2D texture, string path)
    {
        var pixels = ReadBackRgba8(texture, out int width, out int height);
        PngWriter.Write(path, width, height, pixels);
    }

    /// <summary>清成指定颜色（自检时先铺一层底色，便于看边界）</summary>
    public void ClearRenderTarget(ID3D11RenderTargetView view, Color4 color)
        => Context.ClearRenderTargetView(view, color);

    public void Dispose()
    {
        Context.ClearState();
        Context.Flush();
        Context.Dispose();
        Device.Dispose();
        Factory.Dispose();
    }
}

/// <summary>
/// 可复用的读回缓冲：staging 纹理与目标数组都只建一次。
///
/// 为什么必须复用：实时预览每秒回读几十次，每次现场建 staging 纹理 + 分配 8 MB 数组，
/// 光 GC 就能把帧率吃掉一半（实测这条路是实时链路最容易被忽略的开销）。
/// </summary>
public sealed class ReadbackBuffer : IDisposable
{
    private readonly ID3D11Texture2D _staging;
    private readonly byte[] _pixels;
    private readonly int _width;
    private readonly int _height;
    private readonly int _rowBytes;

    public ReadbackBuffer(ID3D11Device device, int width, int height, Format format)
    {
        _width = width;
        _height = height;
        _rowBytes = width * 4;
        _pixels = new byte[_rowBytes * height];

        _staging = device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
        });
    }

    public int Width => _width;

    public int Height => _height;

    /// <summary>回读到内部缓冲并返回它（内容下一次 Read 就会被覆盖）</summary>
    public byte[] Read(ID3D11DeviceContext context, ID3D11Texture2D source)
    {
        context.CopyResource(_staging, source);
        var mapped = context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            int rowPitch = (int)mapped.RowPitch;
            unsafe
            {
                byte* src = (byte*)mapped.DataPointer;
                for (int y = 0; y < _height; y++)
                {
                    new ReadOnlySpan<byte>(src + y * rowPitch, _rowBytes)
                        .CopyTo(_pixels.AsSpan(y * _rowBytes, _rowBytes));
                }
            }
            return _pixels;
        }
        finally
        {
            context.Unmap(_staging, 0);
        }
    }

    public void Dispose() => _staging.Dispose();
}
