//
//  ScopeEngine.cs
//  VideoScopePad.Win
//
//  示波器引擎：GPU 直方图 → 归一化成示波器纹理。
//  与 iPad 版 Render/ScopeEngine.swift 的分工完全一致，只是把 Metal 换成 D3D11：
//
//    · 一块大直方图 buffer（720 896 个 uint，2.75 MB）：波形 512×256×4 平面 + 三块 256² 二维直方图
//    · 每帧先清零 → 一次 compute 累计 → 再按需要跑若干次归一化，把直方图变成可采样的纹理
//    · 测量用的另一块小 buffer（4×256 bin + 64 径向 bin）单独累计、CPU 回读成数值读数
//
//  采样步长（stride）由 compute 着色器自己乘（见 CSAccumulateHistogram 里的 params.z），
//  所以 dispatch 的线程数要按「源尺寸 / stride」算，不是源尺寸 —— 这点弄错的话波形会只画左上角一块。
//

using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace VideoScopePad.Win.Render;

/// <summary>一帧示波器统计需要知道的东西（对应 iPad 版的 ScopeRenderSettings）</summary>
public sealed class ScopeRenderSettings
{
    /// <summary>是否需要波形数据（flags.z）—— Parade / 亮度波形 / RGB 叠加都靠它</summary>
    public bool NeedWaveform { get; set; } = true;
    public bool NeedVectorscope { get; set; }
    public bool NeedDiamond { get; set; }
    public bool NeedCie { get; set; }

    /// <summary>采样步长（每 stride×stride 个像素取一个，1 = 全采）</summary>
    public int Stride { get; set; } = 2;

    /// <summary>波形归一化强度参考（refs.x）</summary>
    public float WaveformIntensity { get; set; } = 1.0f;
    /// <summary>矢量图归一化强度参考（refs.y）</summary>
    public float VectorscopeIntensity { get; set; } = 1.0f;

    public bool NeedAnyGamut => NeedVectorscope || NeedDiamond || NeedCie;

    public int GamutMask
    {
        get
        {
            int mask = 0;
            if (NeedVectorscope) mask |= ShaderConstants.GamutMaskVectorscope;
            if (NeedDiamond) mask |= ShaderConstants.GamutMaskDiamond;
            if (NeedCie) mask |= ShaderConstants.GamutMaskCie;
            return mask;
        }
    }
}

public sealed class ScopeEngine : IDisposable
{
    /// <summary>一块示波器纹理 + 它的 UAV（归一化写入）与 SRV（绘制采样）</summary>
    private sealed class ScopeTexture : IDisposable
    {
        public ID3D11Texture2D Texture { get; }
        public ID3D11UnorderedAccessView Uav { get; }
        public ID3D11ShaderResourceView Srv { get; }

        public ScopeTexture(ID3D11Device device, int width, int height, string label)
        {
            var desc = new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R8G8B8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
            };
            Texture = device.CreateTexture2D(desc);
            Texture.DebugName = label;
            Uav = device.CreateUnorderedAccessView(Texture);
            Srv = device.CreateShaderResourceView(Texture);
        }

        public void Dispose()
        {
            Uav.Dispose();
            Srv.Dispose();
            Texture.Dispose();
        }
    }

    private readonly ID3D11Device _device;
    private readonly ShaderLibrary _shaders;

    private readonly ID3D11Buffer _histogramBuffer;
    private readonly ID3D11UnorderedAccessView _histogramUav;

    private readonly ID3D11Buffer _measureBuffer;
    private readonly ID3D11UnorderedAccessView _measureUav;
    private readonly ID3D11Buffer _measureStaging;
    private readonly ID3D11Buffer _uniformBuffer;          // 64 字节，VSScopeUniforms

    private readonly ScopeTexture _waveform;
    private readonly ScopeTexture _overlay;
    private readonly ScopeTexture _parade;
    private readonly ScopeTexture _vectorscope;
    private readonly ScopeTexture _diamond;
    private readonly ScopeTexture _cie;

    /// <summary>测量直方图的内容（EncodeMeasurement 之后有效）</summary>
    private readonly uint[] _measureData = new uint[ShaderConstants.MeasureUintCount];

    /// <summary>上一帧提交的测量回读是否已完成（避免阻塞渲染线程）</summary>
    private bool _measurePending;

    public ScopeEngine(ID3D11Device device, ShaderLibrary shaders)
    {
        _device = device;
        _shaders = shaders;

        _histogramBuffer = device.CreateBuffer(new BufferDescription((uint)ShaderConstants.HistogramByteSize,
            BindFlags.UnorderedAccess | BindFlags.ShaderResource,
            ResourceUsage.Default,
            CpuAccessFlags.None,
            ResourceOptionFlags.BufferStructured,
            (uint)sizeof(uint)));
        _histogramUav = device.CreateUnorderedAccessView(_histogramBuffer,
                                                         new UnorderedAccessViewDescription
                                                         {
                                                             Format = Format.Unknown,
                                                             ViewDimension = UnorderedAccessViewDimension.Buffer,
                                                             Buffer = new BufferUnorderedAccessView
                                                             {
                                                                 FirstElement = 0,
                                                                 NumElements = ShaderConstants.HistogramUintCount,
                                                                 Flags = BufferUnorderedAccessViewFlags.None,
                                                             },
                                                         });

        _measureBuffer = device.CreateBuffer(new BufferDescription((uint)(ShaderConstants.MeasureUintCount * sizeof(uint)),
            BindFlags.UnorderedAccess,
            ResourceUsage.Default,
            CpuAccessFlags.None,
            ResourceOptionFlags.BufferStructured,
            (uint)sizeof(uint)));
        _measureUav = device.CreateUnorderedAccessView(_measureBuffer,
                                                       new UnorderedAccessViewDescription
                                                       {
                                                           Format = Format.Unknown,
                                                           ViewDimension = UnorderedAccessViewDimension.Buffer,
                                                           Buffer = new BufferUnorderedAccessView
                                                           {
                                                               FirstElement = 0,
                                                               NumElements = ShaderConstants.MeasureUintCount,
                                                               Flags = BufferUnorderedAccessViewFlags.None,
                                                           },
                                                       });

        _measureStaging = device.CreateBuffer(new BufferDescription(
            ShaderConstants.MeasureUintCount * sizeof(uint),
            BindFlags.None,
            ResourceUsage.Staging,
            CpuAccessFlags.Read,
            ResourceOptionFlags.BufferStructured,
            (uint)sizeof(uint)));

        _uniformBuffer = device.CreateBuffer(new BufferDescription((uint)Marshal.SizeOf<ScopeUniforms>(),
            BindFlags.ConstantBuffer,
            ResourceUsage.Default,
            CpuAccessFlags.None,
            ResourceOptionFlags.None,
            0));

        _waveform = new ScopeTexture(device, ShaderConstants.WaveformColumns, ShaderConstants.WaveformBins, "波形纹理");
        _overlay = new ScopeTexture(device, ShaderConstants.WaveformColumns, ShaderConstants.WaveformBins, "RGB 叠加波形纹理");
        _parade = new ScopeTexture(device, ShaderConstants.WaveformColumns * 3, ShaderConstants.WaveformBins, "Parade 纹理");
        _vectorscope = new ScopeTexture(device, ShaderConstants.VectorscopeSize, ShaderConstants.VectorscopeSize, "矢量示波器纹理");
        _diamond = new ScopeTexture(device, ShaderConstants.VectorscopeSize, ShaderConstants.VectorscopeSize, "钻石图纹理");
        _cie = new ScopeTexture(device, ShaderConstants.VectorscopeSize, ShaderConstants.VectorscopeSize, "马蹄图纹理");
    }

    /// <summary>供指定面板采样显示的纹理</summary>
    public ID3D11Texture2D TextureFor(Core.ScopePanelKind kind, Core.WaveformMode waveformMode) => kind switch
    {
        Core.ScopePanelKind.Vectorscope => _vectorscope.Texture,
        Core.ScopePanelKind.Diamond => _diamond.Texture,
        Core.ScopePanelKind.Cie => _cie.Texture,
        Core.ScopePanelKind.Waveform => waveformMode == Core.WaveformMode.Luma ? _waveform.Texture : _overlay.Texture,
        Core.ScopePanelKind.Parade => _parade.Texture,
        _ => _waveform.Texture,
    };

    public ID3D11ShaderResourceView SrvFor(Core.ScopePanelKind kind, Core.WaveformMode waveformMode) => kind switch
    {
        Core.ScopePanelKind.Vectorscope => _vectorscope.Srv,
        Core.ScopePanelKind.Diamond => _diamond.Srv,
        Core.ScopePanelKind.Cie => _cie.Srv,
        Core.ScopePanelKind.Waveform => waveformMode == Core.WaveformMode.Luma ? _waveform.Srv : _overlay.Srv,
        Core.ScopePanelKind.Parade => _parade.Srv,
        _ => _waveform.Srv,
    };

    /// <summary>一帧调用一次：清零 → 累计 → 归一化</summary>
    public void Encode(ID3D11DeviceContext context,
                       ID3D11ShaderResourceView source,
                       ScopeRenderSettings settings)
    {
        var uniforms = MakeUniforms(settings);

        // 1) 清零（iPad 版用 blit fill，D3D11 上用 ClearUnorderedAccessView 的 UINT 版本）
        context.ClearUnorderedAccessView(_histogramUav, new Int4(0, 0, 0, 0));
        context.UpdateSubresource(in uniforms, _uniformBuffer);

        // 2) 累计：注意线程数按「源尺寸 / stride」算（stride 由着色器内部乘）
        int stride = Math.Max(settings.Stride, 1);
        var sourceDesc = GetDescription(source);
        int sampleWidth = ((int)sourceDesc.Width + stride - 1) / stride;
        int sampleHeight = ((int)sourceDesc.Height + stride - 1) / stride;

        context.CSSetShader(_shaders.AccumulateHistogram);
        context.CSSetConstantBuffer(1, _uniformBuffer);
        context.CSSetShaderResource(0, source);
        context.CSSetUnorderedAccessView(0, _histogramUav);
        Dispatch(context, sampleWidth, sampleHeight);
        context.CSSetUnorderedAccessView(0, null);
        context.CSSetShaderResource(0, null);

        // 3) 归一化：把直方图变成可以采样的示波器纹理
        if (settings.NeedWaveform)
        {
            Normalize(context, _shaders.NormalizeWaveform, _waveform, uniforms);
        }
        if (settings.NeedWaveform)
        {
            Normalize(context, _shaders.NormalizeOverlay, _overlay, uniforms);
        }
        if (settings.NeedWaveform)
        {
            Normalize(context, _shaders.NormalizeParade, _parade, uniforms);
        }

        if (settings.NeedVectorscope)
        {
            uniforms.Refs.Z = ShaderConstants.GamutSectionVectorscope;
            Normalize(context, _shaders.NormalizeVectorscope, _vectorscope, uniforms);
        }
        if (settings.NeedDiamond)
        {
            uniforms.Refs.Z = ShaderConstants.GamutSectionDiamond;
            Normalize(context, _shaders.NormalizeVectorscope, _diamond, uniforms);
        }
        if (settings.NeedCie)
        {
            uniforms.Refs.Z = ShaderConstants.GamutSectionCie;
            Normalize(context, _shaders.NormalizeVectorscope, _cie, uniforms);
        }

        context.CSSetShader(null);
        context.CSSetConstantBuffer(1, null);
    }

    private void Normalize(ID3D11DeviceContext context,
                           ID3D11ComputeShader shader,
                           ScopeTexture target,
                           ScopeUniforms uniforms)
    {
        context.UpdateSubresource(in uniforms, _uniformBuffer);
        context.CSSetShader(shader);
        context.CSSetConstantBuffer(1, _uniformBuffer);
        context.CSSetUnorderedAccessView(0, _histogramUav);
        context.CSSetUnorderedAccessView(1, target.Uav);
        Dispatch(context, (int)target.Texture.Description.Width, (int)target.Texture.Description.Height);
        context.CSSetUnorderedAccessView(0, null);
        context.CSSetUnorderedAccessView(1, null);
    }

    private static ScopeUniforms MakeUniforms(ScopeRenderSettings settings) => new()
    {
        Params = new System.Numerics.Vector4(1.0f, 1.0f, Math.Max(settings.Stride, 1), 1.0f),
        Color = new System.Numerics.Vector4(1, 1, 1, 1),
        Refs = new System.Numerics.Vector4(settings.WaveformIntensity, settings.VectorscopeIntensity, 0, 0),
        Flags = new System.Numerics.Vector4(ShaderConstants.WaveformModeLuma,
                                            settings.GamutMask,
                                            settings.NeedWaveform ? 1 : 0,
                                            0),
    };

    private static void Dispatch(ID3D11DeviceContext context, int width, int height, int groupX = 16, int groupY = 16)
    {
        uint groupsX = (uint)Math.Max((width + groupX - 1) / groupX, 1);
        uint groupsY = (uint)Math.Max((height + groupY - 1) / groupY, 1);
        context.Dispatch(groupsX, groupsY, 1);
    }

    private static Texture2DDescription GetDescription(ID3D11ShaderResourceView srv)
    {
        using var resource = srv.Resource;
        using var texture = resource.QueryInterface<ID3D11Texture2D>();
        return texture.Description;
    }

    // MARK: - 测量（数值读数）

    public delegate void MeasurementCallback(uint[] counts);

    /// <summary>
    /// 累计测量直方图并回读。
    /// 回读用「上一帧的拷贝 + DoNotWait」：拿不到就跳过这次，绝不阻塞渲染线程
    /// （iPad 版也是这个思路，用的是双 buffer）。
    /// </summary>
    public void EncodeMeasurement(ID3D11DeviceContext context,
                                  ID3D11ShaderResourceView source,
                                  int stride,
                                  MeasurementCallback onResult)
    {
        // 1) 先把上一帧的结果读出来（没就绪就算了，下一帧再取）
        if (_measurePending && TryReadMeasure(context))
        {
            onResult(_measureData);
            _measurePending = false;
        }

        stride = Math.Max(stride, 1);
        var uniforms = new ScopeUniforms
        {
            Params = new System.Numerics.Vector4(1.0f, 1.0f, stride, 1.0f),
            Color = new System.Numerics.Vector4(1, 1, 1, 1),
            Refs = new System.Numerics.Vector4(1, 1, 0, 0),
            Flags = System.Numerics.Vector4.Zero,
        };
        context.UpdateSubresource(in uniforms, _uniformBuffer);
        context.ClearUnorderedAccessView(_measureUav, new Int4(0, 0, 0, 0));

        // 2) 累计（同样注意：线程数按「源尺寸 / stride」算）
        int width = (int)GetDescription(source).Width;
        int height = (int)GetDescription(source).Height;
        int sampleWidth = (width + stride - 1) / stride;
        int sampleHeight = (height + stride - 1) / stride;

        context.CSSetShader(_shaders.AccumulateMeasurement);
        context.CSSetConstantBuffer(1, _uniformBuffer);
        context.CSSetShaderResource(0, source);
        context.CSSetUnorderedAccessView(0, _measureUav);
        Dispatch(context, sampleWidth, sampleHeight);
        context.CSSetUnorderedAccessView(0, null);
        context.CSSetShaderResource(0, null);
        context.CSSetShader(null);
        context.CSSetConstantBuffer(1, null);

        // 3) 拷到 staging，下一帧再取（避免为了 4 KB 读数把 GPU 卡住）
        context.CopyResource(_measureStaging, _measureBuffer);
        _measurePending = true;
    }

    /// <summary>尝试把 staging 里的测量结果读出来。没就绪返回 false（此时不要 Unmap）。</summary>
    private unsafe bool TryReadMeasure(ID3D11DeviceContext context)
    {
        var mapped = context.Map(_measureStaging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.DoNotWait);
        if (mapped.DataPointer == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            uint* src = (uint*)mapped.DataPointer;
            for (int i = 0; i < _measureData.Length; i++)
            {
                _measureData[i] = src[i];
            }
            return true;
        }
        finally
        {
            context.Unmap(_measureStaging, 0);
        }
    }

    /// <summary>把一块示波器纹理读回 CPU（自检用：断言直方图落在预期位置）</summary>
    public byte[] ReadBackTexture(ID3D11DeviceContext context,
                                  ID3D11Texture2D texture,
                                  out int width,
                                  out int height)
    {
        width = (int)texture.Description.Width;
        height = (int)texture.Description.Height;

        var desc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
        };

        using var staging = _device.CreateTexture2D(desc);
        context.CopyResource(staging, texture);
        var mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
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
            context.Unmap(staging, 0);
        }
    }

    /// <summary>读回整块直方图（自检用：可直接断言某个 bin 的计数）</summary>
    public uint[] ReadBackHistogram(ID3D11DeviceContext context)
    {
        var desc = new BufferDescription(
            ShaderConstants.HistogramByteSize,
            BindFlags.None,
            ResourceUsage.Staging,
            CpuAccessFlags.Read,
            ResourceOptionFlags.BufferStructured,
            sizeof(uint));
        using var staging = _device.CreateBuffer(desc);
        context.CopyResource(staging, _histogramBuffer);

        var mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var result = new uint[ShaderConstants.HistogramUintCount];
            unsafe
            {
                uint* src = (uint*)mapped.DataPointer;
                for (int i = 0; i < result.Length; i++)
                {
                    result[i] = src[i];
                }
            }
            return result;
        }
        finally
        {
            context.Unmap(staging, 0);
        }
    }

    public void Dispose()
    {
        _waveform.Dispose();
        _overlay.Dispose();
        _parade.Dispose();
        _vectorscope.Dispose();
        _diamond.Dispose();
        _cie.Dispose();
        _histogramUav.Dispose();
        _histogramBuffer.Dispose();
        _measureUav.Dispose();
        _measureBuffer.Dispose();
        _measureStaging.Dispose();
        _uniformBuffer.Dispose();
    }
}
