//
//  YuvFrameUploader.cs
//  VideoScopePad.Win
//
//  把采集卡的原始帧（YUY2 / NV12）**原样**上传成 GPU 纹理，再用一趟 compute 转成 R'G'B'，
//  交给显示与示波器。对应 iPad 版 VideoRenderer 里那条「CVMetalTextureCache 零拷贝」的路 ——
//  差别是 MF 这边我们是 CPU 拷一份再传（见 Capture/CapturedFrame.cs 说明为什么不能锁着不放）。
//
//  为什么不是「CPU 转 RGB 再传」（虽然 Capture/YuvFrameConverter.cs 已经能转）：
//  1920×1080 一帧，CPU 转要多花十几毫秒，60 fps 根本来不及；上传原始 YUY2 只有 3.96 MB
//  的内存拷贝（约 1 ms），真正的换算交给 GPU（一趟 200 万线程的 compute，几十微秒）。
//  CPU 版仍然保留：一是自检要对拍两份实现，二是出 PNG 时不需要显卡。
//
//  ⚠️ 纹理布局的两个关键点（错了就是整幅花屏或斜掉）：
//    · **行跨距用驱动给的**（frame.Stride），Upload 时通过 rowPitch 传进去，
//      不要假设它等于 width×bpp —— UVC 驱动常按 4/16 字节对齐补行。
//    · YUY2 用 R8G8B8A8_UNORM 纹理装：一个 texel（4 字节）正好是 (Y0,U,Y1,V)，
//      所以**纹理宽度是图像宽度的一半**，一个 texel 管两个像素。
//      NV12 则是 Y（R8_UNORM，W×H）+ UV（R8G8_UNORM，W/2×H/2）两张纹理。
//

using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using VideoScopePad.Win.Capture;

namespace VideoScopePad.Win.Render;

/// <summary>采集帧 → GPU 纹理 → R'G'B' 纹理。</summary>
public sealed class YuvFrameUploader : IDisposable
{
    /// <summary>与 ConvertShaders.hlsl 里的 cbuffer 逐字段对应（48 字节，16 字节对齐）</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ConvertUniforms
    {
        public Vector4 ScaleBias;       // x = YScale, y = YOffset
        public Vector4 Coefficients;    // x = Rv, y = Gu, z = Gv, w = Bu
        public uint Width;
        public uint Height;
        public float Padding0;
        public float Padding1;
    }

    private readonly ID3D11Device _device;
    private readonly ShaderLibrary _shaders;
    private readonly ID3D11Buffer _uniformBuffer;

    // 输入（YUV）
    private ID3D11Texture2D? _packedYuy2;      // YUY2：(Y0,U,Y1,V) 装在 RGBA8 里，宽 = 图像宽/2
    private ID3D11ShaderResourceView? _packedYuy2Srv;
    private ID3D11Texture2D? _luma;            // NV12 的 Y
    private ID3D11ShaderResourceView? _lumaSrv;
    private ID3D11Texture2D? _chroma;          // NV12 的 UV
    private ID3D11ShaderResourceView? _chromaSrv;

    // 输出（R'G'B'）
    private ID3D11Texture2D? _rgb;
    private ID3D11UnorderedAccessView? _rgbUav;
    private ID3D11ShaderResourceView? _rgbSrv;

    private int _width;
    private int _height;
    private string _format = string.Empty;
    private bool _disposed;

    public YuvFrameUploader(ID3D11Device device, ShaderLibrary shaders)
    {
        _device = device;
        _shaders = shaders;
        _uniformBuffer = device.CreateBuffer(new BufferDescription(
            (uint)Marshal.SizeOf<ConvertUniforms>(),
            BindFlags.ConstantBuffer, ResourceUsage.Default, CpuAccessFlags.None, ResourceOptionFlags.None, 0));
    }

    /// <summary>转换出来的 R'G'B' 纹理（给 VideoRenderer 与 ScopeEngine 用）</summary>
    public ID3D11Texture2D? RgbTexture => _rgb;

    public ID3D11ShaderResourceView? RgbSrv => _rgbSrv;

    /// <summary>当前纹理的尺寸（未初始化时为 0）</summary>
    public int Width => _width;

    public int Height => _height;

    /// <summary>
    /// 上传一帧并转换，返回 R'G'B' 纹理的 SRV。
    /// 尺寸/像素格式变了会自动重建纹理（切分辨率时会走到这条路）。
    /// </summary>
    public ID3D11ShaderResourceView Convert(ID3D11DeviceContext context,
                                             CapturedFrame frame,
                                             string subtypeName,
                                             VideoColorInfo color)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ThrowIfDisposed();

        EnsureResources(frame.Width, frame.Height, subtypeName);

        bool yuy2 = string.Equals(subtypeName, "YUY2", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(subtypeName, "YUYV", StringComparison.OrdinalIgnoreCase);

        if (yuy2)
        {
            // (Y0,U,Y1,V) → RGBA8：纹理宽 = 图像宽 / 2，行跨距就是驱动给的那个
            context.UpdateSubresource<byte>(frame.Data, _packedYuy2!, 0, (uint)frame.Stride, 0, null);
        }
        else
        {
            int lumaBytes = frame.Stride * frame.Height;
            context.UpdateSubresource<byte>(frame.Data.AsSpan(0, lumaBytes), _luma!, 0, (uint)frame.Stride, 0, null);

            // UV 平面紧跟 Y 平面之后，共用同一个行跨距（高度是图像的一半）
            int chromaHeight = frame.Height / 2;
            context.UpdateSubresource<byte>(
                frame.Data.AsSpan(lumaBytes, frame.Stride * chromaHeight), _chroma!, 0, (uint)frame.Stride, 0, null);
        }

        // 系数与 CPU 版同源（同一处选择逻辑），保证两条链的结果一致
        YuvFrameConverter.Coefficients coefficients = YuvFrameConverter.Coefficients.Select(color);
        var uniforms = new ConvertUniforms
        {
            ScaleBias = new Vector4((float)coefficients.YScale, (float)coefficients.YOffset, 0, 0),
            Coefficients = new Vector4(
                (float)coefficients.Rv, (float)coefficients.Gu, (float)coefficients.Gv, (float)coefficients.Bu),
            Width = (uint)frame.Width,
            Height = (uint)frame.Height,
        };
        context.UpdateSubresource(in uniforms, _uniformBuffer);

        context.CSSetShader(yuy2 ? _shaders.Yuy2ToRgb : _shaders.Nv12ToRgb);
        context.CSSetConstantBuffer(0, _uniformBuffer);
        if (yuy2)
        {
            context.CSSetShaderResource(0, _packedYuy2Srv);
        }
        else
        {
            context.CSSetShaderResource(0, _lumaSrv);
            context.CSSetShaderResource(1, _chromaSrv);
        }
        context.CSSetUnorderedAccessView(0, _rgbUav);

        uint groupsX = (uint)Math.Max((frame.Width + 15) / 16, 1);
        uint groupsY = (uint)Math.Max((frame.Height + 15) / 16, 1);
        context.Dispatch(groupsX, groupsY, 1);

        // 解绑：同一个纹理后面要当 SRV 采（D3D11 允许同时绑，但调试层会唠叨，且不利于后续改 D3D12）
        context.CSSetUnorderedAccessView(0, null);
        context.CSSetShaderResource(0, null);
        context.CSSetShaderResource(1, null);
        context.CSSetShader(null);
        context.CSSetConstantBuffer(0, null);

        return _rgbSrv!;
    }

    /// <summary>尺寸或像素格式变化时重建纹理</summary>
    private void EnsureResources(int width, int height, string subtypeName)
    {
        if (_rgb is not null && width == _width && height == _height &&
            string.Equals(subtypeName, _format, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ReleaseResources();

        _width = width;
        _height = height;
        _format = subtypeName;

        bool yuy2 = string.Equals(subtypeName, "YUY2", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(subtypeName, "YUYV", StringComparison.OrdinalIgnoreCase);

        if (yuy2)
        {
            _packedYuy2 = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)(width / 2),
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R8G8B8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
            });
            _packedYuy2Srv = _device.CreateShaderResourceView(_packedYuy2);
        }
        else
        {
            _luma = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
            });
            _lumaSrv = _device.CreateShaderResourceView(_luma);

            _chroma = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)(width / 2),
                Height = (uint)(height / 2),
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R8G8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
            });
            _chromaSrv = _device.CreateShaderResourceView(_chroma);
        }

        // 输出：要能被 compute 写（UAV）、被显示与直方图读（SRV）
        _rgb = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess,
            CPUAccessFlags = CpuAccessFlags.None,
        });
        _rgbUav = _device.CreateUnorderedAccessView(_rgb);
        _rgbSrv = _device.CreateShaderResourceView(_rgb);
    }

    private void ReleaseResources()
    {
        _packedYuy2Srv?.Dispose();
        _packedYuy2?.Dispose();
        _lumaSrv?.Dispose();
        _luma?.Dispose();
        _chromaSrv?.Dispose();
        _chroma?.Dispose();
        _rgbUav?.Dispose();
        _rgbSrv?.Dispose();
        _rgb?.Dispose();

        _packedYuy2Srv = null;
        _packedYuy2 = null;
        _lumaSrv = null;
        _luma = null;
        _chromaSrv = null;
        _chroma = null;
        _rgbUav = null;
        _rgbSrv = null;
        _rgb = null;
        _width = 0;
        _height = 0;
        _format = string.Empty;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        ReleaseResources();
        _uniformBuffer.Dispose();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
