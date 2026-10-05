//
//  VideoRenderer.cs
//  VideoScopePad.Win
//
//  合成一帧：画面 + 示波器面板底色 + 示波器轨迹。
//  与 iPad 版 Render/VideoRenderer.swift 的 encodeOutput 一一对应，包括：
//    · 每一格都用 scissor 裁在自己格子里（画到邻格里是绝对不允许的）
//    · 轨迹用加法混合（辉光），面板底色用常规 alpha 混合
//    · 画面旋转在顶点着色器里绕采样区中心转 UV（几何矩形不变，所以不会溢出格子）
//
//  刻度栏（IRE 数字、色标框）在 iPad 版是 SwiftUI Canvas 画的，
//  Windows 版同样放在界面层（WPF 覆盖层）画 —— 两边用同一份 ScopeLayout，所以严格对齐。
//

using System.Numerics;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using VideoScopePad.Win.Core;

namespace VideoScopePad.Win.Render;

/// <summary>渲染选项（将来接到 AppSettings）</summary>
public sealed class RenderOptions
{
    public int DisplayMode { get; set; } = ShaderConstants.DisplayModeColor;
    public WaveformMode WaveformMode { get; set; } = WaveformMode.Luma;
    public double VectorscopeGain { get; set; } = 1.0;
    public double TraceIntensity { get; set; } = 1.0;
    public double PanelOpacity { get; set; } = 1.0;

    public Vector3 TraceColor { get; set; } = new(0.36f, 1.0f, 0.55f);

    /// <summary>参考层不透明度（冻结参考；1.0 = 实时轨迹）</summary>
    public double ReferenceOpacity { get; set; } = 0.55;
    public bool ShowReference { get; set; }

    // ---- 斑马纹（超白 / 黑切割）----
    // ---- LUT（.cube）----
    /// <summary>LUT 开关（与 iPad 版 lutEnabled 对应）</summary>
    public bool LutEnabled { get; set; }
    /// <summary>LUT 强度（0…1，0 = 原图、1 = 完全套用）</summary>
    public double LutStrength { get; set; } = 1.0;

    /// <summary>超白斑马纹开关</summary>
    public bool ZebraEnabled { get; set; }
    /// <summary>超白斑马纹阈值（IRE）</summary>
    public double ZebraThresholdIre { get; set; } = 100.0;
    /// <summary>黑切割斑马纹开关</summary>
    public bool ZebraBlackEnabled { get; set; }
    /// <summary>黑切割阈值（IRE）</summary>
    public double ZebraBlackThresholdIre { get; set; }

    /// <summary>IRE → 0–1 码值（本工程解码后是 full range：0 IRE = 0、100 IRE = 255 → 归一化后 1.0）</summary>
    public static float CodeFromIre(double ire) => (float)Math.Clamp(ire / 100.0, 0.0, 1.0);
}

public sealed class VideoRenderer : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly PipelineLibrary _pipelines;

    private readonly ID3D11Buffer _quadBuffer;      // b0：48 字节
    private readonly ID3D11Buffer _renderBuffer;    // b1：176 字节
    private readonly ID3D11Buffer _scopeBuffer;     // b2：64 字节
    private readonly ID3D11Buffer _solidBuffer;     // b1：16 字节（纯色）

    // ---- LUT（.cube）与 look pass ----
    /// <summary>已载入的 LUT（没载入时是 2³ 恒等，采样结果与原图一致）</summary>
    public LutResource Lut { get; }
    private ID3D11Texture2D? _lookTexture;
    private ID3D11RenderTargetView? _lookRtv;
    private ID3D11ShaderResourceView? _lookSrv;
    private int _lookWidth;
    private int _lookHeight;

    /// <summary>换一份 LUT（必须在渲染线程调用：会创建 D3D 纹理）</summary>
    public void SetLut(CubeLut cube) => Lut.Update(cube);

    /// <summary>look pass 的结果（LUT 生效时可供示波器/读数在 LUT 后取样）</summary>
    public ID3D11ShaderResourceView? LookSrv => _lookSrv;

    /// <summary>布局（由 ScopeLayout 算好传进来，界面层用的是同一份）</summary>
    public ScopeLayoutResult Layout { get; set; } = new();

    public Color4 Background { get; set; } = new(0.055f, 0.060f, 0.070f, 1.0f);
    public Color4 PanelColor { get; set; } = new(0.105f, 0.115f, 0.135f, 0.92f);

    public VideoRenderer(ID3D11Device device, PipelineLibrary pipelines)
    {
        _device = device;
        _pipelines = pipelines;
        Lut = new LutResource(device, null);

        _quadBuffer = device.CreateBuffer(new BufferDescription((uint)System.Runtime.InteropServices.Marshal.SizeOf<QuadUniforms>(),
                                                                BindFlags.ConstantBuffer, ResourceUsage.Default, CpuAccessFlags.None, ResourceOptionFlags.None, 0));
        _renderBuffer = device.CreateBuffer(new BufferDescription((uint)System.Runtime.InteropServices.Marshal.SizeOf<RenderUniforms>(),
                                                                  BindFlags.ConstantBuffer, ResourceUsage.Default, CpuAccessFlags.None, ResourceOptionFlags.None, 0));
        _scopeBuffer = device.CreateBuffer(new BufferDescription((uint)System.Runtime.InteropServices.Marshal.SizeOf<ScopeUniforms>(),
                                                                 BindFlags.ConstantBuffer, ResourceUsage.Default, CpuAccessFlags.None, ResourceOptionFlags.None, 0));
        _solidBuffer = device.CreateBuffer(new BufferDescription(16,
                                                                 BindFlags.ConstantBuffer, ResourceUsage.Default, CpuAccessFlags.None, ResourceOptionFlags.None, 0));
    }

    /// <summary>
    /// 渲染一帧到指定的渲染目标。
    /// <paramref name="sourceSrv"/> 是「已经转成 R'G'B' 的显示纹理」（画面格与示波器统计都用它）。
    /// </summary>
    public void Render(ID3D11DeviceContext context,
                       ID3D11RenderTargetView target,
                       int pixelWidth,
                       int pixelHeight,
                       ID3D11ShaderResourceView sourceSrv,
                       ScopeEngine engine,
                       RenderOptions options)
    {
        if (pixelWidth < 2 || pixelHeight < 2)
        {
            return;
        }

        context.OMSetRenderTargets(target);
        context.RSSetViewport(0, 0, pixelWidth, pixelHeight);
        context.RSSetScissorRect(0, 0, pixelWidth, pixelHeight);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        context.IASetInputLayout(null);
        context.VSSetShader(_pipelines.QuadVertex);
        context.VSSetConstantBuffer(0, _quadBuffer);
        context.PSSetSampler(0, _pipelines.Sampler);

        context.ClearRenderTargetView(target, Background);

        // 0) look pass：LUT 生效时先把画面渲染到中间纹理。若会话已经在本帧提前调过
        //    PrepareLookPass（为了让"LUT 后取样"拿到的就是本帧），这里直接用结果、不重复跑。
        ID3D11ShaderResourceView effectiveSource = _lookPrepared && _lookSrv is not null
            ? _lookSrv
            : PrepareLookPass(context, pixelWidth, pixelHeight, sourceSrv, options);
        _lookPrepared = false;

        // 1) 画面格
        foreach (var pane in Layout.Panes)
        {
            if (pane.Content != PaneContent.Picture || pane.Video is not { } video)
            {
                continue;
            }

            SetScissor(context, pane.Panel, pixelWidth, pixelHeight);
            context.OMSetBlendState(_pipelines.Opaque);

            var render = RenderUniforms.Default;
            render.Flags = new Vector4(options.DisplayMode, 0, 0, 0);

            // 斑马纹（超白 / 黑切割）：只在**显示通道**叠加，不进示波器与读数统计。
            // IRE → 码值：本工程在采集入口就把 limited 展开成 full，所以 0 IRE = 0、100 IRE = 255。
            // （着色器里比较的就是解码后的 full-range 亮度，两端对得上。）
            render.Zebra = new Vector4(
                RenderOptions.CodeFromIre(options.ZebraThresholdIre),
                options.ZebraEnabled ? 1f : 0f,
                RenderOptions.CodeFromIre(options.ZebraBlackThresholdIre),
                options.ZebraBlackEnabled ? 1f : 0f);
            context.UpdateSubresource(in render, _renderBuffer);

            context.PSSetShader(_pipelines.Display);
            context.PSSetConstantBuffer(1, _renderBuffer);
            context.PSSetShaderResource(0, effectiveSource);
            DrawQuad(context, video, pane.VideoUV ?? new RectF(0, 0, 1, 1), pane.Rotation);
        }

        // 2) 示波器格：面板底色 + 轨迹
        foreach (var pane in Layout.Panes)
        {
            if (pane.Content.ScopeKind() is not { } kind || pane.Plot is not { } plot)
            {
                continue;
            }

            SetScissor(context, pane.Panel, pixelWidth, pixelHeight);

            // 2a) 面板底色（叠加布局下半透明，让画面透出来）
            var panelColor = PanelColor;
            if (Layout.IsOverlay)
            {
                panelColor = new Color4(panelColor.R, panelColor.G, panelColor.B, (float)options.PanelOpacity);
            }
            context.OMSetBlendState(_pipelines.AlphaBlend);
            context.UpdateSubresource(new Vector4(panelColor.R, panelColor.G, panelColor.B, panelColor.A), _solidBuffer);
            context.PSSetShader(_pipelines.SolidColor);
            context.PSSetConstantBuffer(1, _solidBuffer);
            DrawQuad(context, pane.Panel, new RectF(0, 0, 1, 1), 0);

            // 2b) 轨迹（加法混合 = 辉光）
            SetScissor(context, pane.Panel, pixelWidth, pixelHeight);
            context.OMSetBlendState(_pipelines.Additive);

            var scope = ScopeUniforms.Default;
            scope.Params = new Vector4((float)Math.Max(options.VectorscopeGain, 0.25),
                                       (float)options.TraceIntensity,
                                       1.0f,
                                       1.0f);
            bool colorTrace = kind == ScopePanelKind.Parade
                || (kind == ScopePanelKind.Waveform && options.WaveformMode == WaveformMode.RgbOverlay);
            var tint = colorTrace ? Vector3.One : options.TraceColor;
            scope.Color = new Vector4(tint, 1.0f);
            scope.Refs = new Vector4(1, 1, 0, 0);
            scope.Flags = new Vector4(ShaderConstants.WaveformModeLuma, 0, 0, 0);
            if (options.WaveformMode == WaveformMode.RgbOverlay)
            {
                scope.Flags.X = ShaderConstants.WaveformModeOverlay;
            }

            var uv = kind == ScopePanelKind.Vectorscope
                ? UvRectForGain(options.VectorscopeGain)
                : new RectF(0, 0, 1, 1);

            // ⚠️ 像素着色器必须在**两次绘制之前**就设好：参考层这一趟如果继承了上一趟的
            //    PSSolidColor（面板底色用的那个），就会拿面板颜色把整个绘图区糊一遍 ——
            //    表现为「整块区域都变了颜色」而不是「轨迹上多了一层琥珀」。
            //    （自检里那条「差异必须全部落在轨迹上」就是专门抓这个的，实测抓到过。）
            context.PSSetShader(_pipelines.ScopeTrace);

            // 2b-1) 冻结参考层先画：琥珀色幽灵（params.w = 不透明度）
            //       —— 先画参考、后画实时，实时轨迹永远压在幽灵上面（与 iPad 版同序）。
            ID3D11ShaderResourceView? reference = options.ShowReference
                ? engine.SrvForReference(kind, options.WaveformMode)
                : null;
            if (reference is not null)
            {
                var ghost = scope;
                ghost.Color = new Vector4(ReferenceTint, 1.0f);
                ghost.Params = new Vector4(scope.Params.X, scope.Params.Y, scope.Params.Z,
                                           (float)Math.Clamp(options.ReferenceOpacity, 0.05, 1.0));
                context.UpdateSubresource(in ghost, _scopeBuffer);
                context.PSSetConstantBuffer(2, _scopeBuffer);
                context.PSSetShaderResource(0, reference);
                DrawQuad(context, plot, uv, 0);
            }

            // 2b-2) 实时轨迹（params.w = 1.0，满强度）
            context.UpdateSubresource(in scope, _scopeBuffer);
            context.PSSetConstantBuffer(2, _scopeBuffer);
            context.PSSetShaderResource(0, engine.SrvFor(kind, options.WaveformMode));
            DrawQuad(context, plot, uv, 0);
        }

        // 收尾：状态复位，免得影响后面的读回与下一帧
        context.RSSetScissorRect(0, 0, pixelWidth, pixelHeight);
        context.OMSetBlendState(null);
        context.PSSetShader(null);
        context.VSSetShader(null);
    }

    /// <summary>
    /// 参考层（冻结参考）的琥珀色 —— 与 iPad 版 v1.11.0 的 vsReferenceTint = (1.0, 0.58, 0.12) 一致。
    /// 选琥珀色的原因：实时轨迹是青绿色，琥珀与它在任何混合下都不会看混。
    /// </summary>
    private static readonly Vector3 ReferenceTint = new(1.0f, 0.58f, 0.12f);

    /// <summary>矢量图放大：UV 缩放（与 iPad 版 ScopeEngine.uvRect 一致）</summary>
    private static RectF UvRectForGain(double gain)
    {
        double g = Math.Max(gain, 0.25);
        double scale = 1.0 / g;
        double offset = 0.5 - 0.5 * scale;
        return new RectF(offset, offset, scale, scale);
    }

    /// <summary>把一个 quad 画到单位空间矩形里（顶点由 SV_VertexID 生成，4 个顶点一条 strip）</summary>
    private void DrawQuad(ID3D11DeviceContext context, RectF rect, RectF uv, int rotationDegrees)
    {
        var quad = new QuadUniforms
        {
            Rect = new Vector4((float)rect.MinX, (float)rect.MinY, (float)rect.Width, (float)rect.Height),
            Uv = new Vector4((float)uv.MinX, (float)uv.MinY, (float)uv.Width, (float)uv.Height),
            Misc = new Vector4(rotationDegrees, 0, 0, 0),
        };
        context.UpdateSubresource(in quad, _quadBuffer);
        context.Draw(4, 0);
    }

    /// <summary>
    /// 把「单位空间矩形（左上角原点，和布局一致）」设成 scissor（像素、左上角原点）。
    /// 有它兜底，任何一格的内容都不可能画到邻格里去。
    /// </summary>
    private static void SetScissor(ID3D11DeviceContext context, RectF unitRect, int pixelWidth, int pixelHeight)
    {
        int x = (int)Math.Max(0, Math.Min(unitRect.MinX * pixelWidth, pixelWidth - 1));
        int y = (int)Math.Max(0, Math.Min(unitRect.MinY * pixelHeight, pixelHeight - 1));
        int w = (int)Math.Max(1, Math.Min(unitRect.Width * pixelWidth, pixelWidth - x));
        int h = (int)Math.Max(1, Math.Min(unitRect.Height * pixelHeight, pixelHeight - y));
        context.RSSetScissorRect(x, y, w, h);
    }

    private bool _lookPrepared;

    /// <summary>
    /// 跑一次 look pass（LUT 生效时把画面渲染进中间纹理），返回"后续该用哪张纹理"。
    /// **必须在示波器 Encode 之前调用**：否则"示波器取样 = LUT 后"拿到的会是上一帧的结果。
    /// 没开 LUT 时直接返回原纹理，不做任何额外绘制。
    /// </summary>
    public ID3D11ShaderResourceView PrepareLookPass(ID3D11DeviceContext context,
                                                    int videoWidth,
                                                    int videoHeight,
                                                    ID3D11ShaderResourceView sourceSrv,
                                                    RenderOptions options)
    {
        // ⚠️ 坐标语义：look 纹理按**视频尺寸**建、整幅 0..1 绘制一次。
        //    这样它和源纹理同域，画面格沿用原来的 UV 数学即可（第一版按合成尺寸+分格位置画，
        //    结果被采样时又按源纹理 UV 映射一次 → 双重映射，恒等 LUT 都能差出 241）。
        int pixelWidth = videoWidth;
        int pixelHeight = videoHeight;        //    之后画面格与（可选的）示波器取样都读这张纹理 —— 这样"LUT 前 / LUT 后"就能对比着看。
        bool lutActive = options.LutEnabled && Lut.Cube.HasContent;
        if (lutActive)
        {
            EnsureLookTexture(pixelWidth, pixelHeight);
            if (_lookRtv is not null && _lookSrv is not null)
            {
                context.OMSetRenderTargets(_lookRtv);
                context.RSSetViewport(0, 0, pixelWidth, pixelHeight);
                context.RSSetScissorRect(0, 0, pixelWidth, pixelHeight);

                var look = RenderUniforms.Default;
                // ⚠️ 着色器里的 lutDomain 是**标量** min/max（x=min、y=max，内部算 1/(max-min)），
                //    不是逐通道的偏移/缩放 —— 第一版按逐通道传，采样坐标就完全错了。
                //    逐通道域不一致时这里用 R 通道的值（着色器本来也只支持标量域）。
                (float domainMin, float domainMax) = Lut.ScalarDomain;
                look.LutParams = new Vector4(
                    (float)Math.Clamp(options.LutStrength, 0, 1),
                    Lut.Size3D > 0 ? Lut.Size3D : 1,
                    Lut.Size1D > 0 ? Lut.Size1D : 1,
                    1f);
                look.LutDomain = new Vector4(domainMin, domainMax, 0, 0);
                look.Flags = new Vector4(options.DisplayMode, 0, 0, 0);
                context.UpdateSubresource(in look, _renderBuffer);

                context.PSSetShader(_pipelines.ApplyLutAndGrade);
                context.PSSetConstantBuffer(1, _renderBuffer);
                context.PSSetShaderResource(0, sourceSrv);
                if (Lut.Srv3D is not null) { context.PSSetShaderResource(1, Lut.Srv3D); }
                if (Lut.Srv1D is not null) { context.PSSetShaderResource(2, Lut.Srv1D); }
                context.OMSetBlendState(_pipelines.Opaque);

                context.RSSetScissorRect(0, 0, pixelWidth, pixelHeight);
                DrawQuad(context, new RectF(0, 0, 1, 1), new RectF(0, 0, 1, 1), 0);

                context.PSSetShaderResource(0, null);
                context.PSSetShaderResource(1, null);
                context.PSSetShaderResource(2, null);

                // 🔴 必须把 look 纹理从输出合并阶段解绑：紧接着示波器会把它当 SRV 采样，
                //    同一张纹理同时作 RTV 与 SRV 是非法状态，实测直接把 GPU 设备打挂
                //    （DXGI_ERROR_DEVICE_REMOVED）。
                //    ⚠️ 这一行必须在 if (lutActive) **里面**：放到外面就变成每帧都解绑，
                //    紧接着的合成绘制会画到"没有渲染目标"的地方，整屏只剩底色（实测踩过）。
                context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
            }
        }

        if (!lutActive)
        {
            // LUT 没生效：**绝不能**把上次的 look 纹理当结果返回
            //（第一版这里漏了判断，未启用 LUT 时画面被换成过期的中间纹理 —— 实测整屏发黑）。
            _lookPrepared = false;
            return sourceSrv;
        }

        _lookPrepared = true;
        return _lookSrv is not null ? _lookSrv : sourceSrv;
    }
    /// <summary>按需创建/重建 look pass 的中间纹理（画面尺寸变化时重建）</summary>
    private void EnsureLookTexture(int width, int height)
    {
        if (_lookTexture is not null && _lookWidth == width && _lookHeight == height)
        {
            return;
        }

        _lookSrv?.Dispose();
        _lookRtv?.Dispose();
        _lookTexture?.Dispose();

        var desc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };
        _lookTexture = _device.CreateTexture2D(desc);
        _lookRtv = _device.CreateRenderTargetView(_lookTexture);
        _lookSrv = _device.CreateShaderResourceView(_lookTexture);
        _lookWidth = width;
        _lookHeight = height;
    }

    public void Dispose()
    {
        _lookSrv?.Dispose();
        _lookRtv?.Dispose();
        _lookTexture?.Dispose();
        Lut.Dispose();
        _quadBuffer.Dispose();
        _renderBuffer.Dispose();
        _scopeBuffer.Dispose();
        _solidBuffer.Dispose();
    }
}
