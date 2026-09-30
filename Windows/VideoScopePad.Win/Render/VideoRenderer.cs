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
}

public sealed class VideoRenderer : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly PipelineLibrary _pipelines;

    private readonly ID3D11Buffer _quadBuffer;      // b0：48 字节
    private readonly ID3D11Buffer _renderBuffer;    // b1：176 字节
    private readonly ID3D11Buffer _scopeBuffer;     // b2：64 字节
    private readonly ID3D11Buffer _solidBuffer;     // b1：16 字节（纯色）

    /// <summary>布局（由 ScopeLayout 算好传进来，界面层用的是同一份）</summary>
    public ScopeLayoutResult Layout { get; set; } = new();

    public Color4 Background { get; set; } = new(0.055f, 0.060f, 0.070f, 1.0f);
    public Color4 PanelColor { get; set; } = new(0.105f, 0.115f, 0.135f, 0.92f);

    public VideoRenderer(ID3D11Device device, PipelineLibrary pipelines)
    {
        _device = device;
        _pipelines = pipelines;

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
            context.UpdateSubresource(in render, _renderBuffer);

            context.PSSetShader(_pipelines.Display);
            context.PSSetConstantBuffer(1, _renderBuffer);
            context.PSSetShaderResource(0, sourceSrv);
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

            context.UpdateSubresource(in scope, _scopeBuffer);
            context.PSSetShader(_pipelines.ScopeTrace);
            context.PSSetConstantBuffer(2, _scopeBuffer);
            context.PSSetShaderResource(0, engine.SrvFor(kind, options.WaveformMode));

            var uv = kind == ScopePanelKind.Vectorscope
                ? UvRectForGain(options.VectorscopeGain)
                : new RectF(0, 0, 1, 1);
            DrawQuad(context, plot, uv, 0);
        }

        // 收尾：状态复位，免得影响后面的读回与下一帧
        context.RSSetScissorRect(0, 0, pixelWidth, pixelHeight);
        context.OMSetBlendState(null);
        context.PSSetShader(null);
        context.VSSetShader(null);
    }

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

    public void Dispose()
    {
        _quadBuffer.Dispose();
        _renderBuffer.Dispose();
        _scopeBuffer.Dispose();
        _solidBuffer.Dispose();
    }
}
