using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

sealed class Renderer : IDisposable
{
    // NV12 path: two planes. YUY2 path (what the HD60 S offers): one RGBA8 texture, each texel = Y0 U Y1 V, read with Load (no converter MFT in between).
    const string Hlsl = @"
Texture2D Y : register(t0); Texture2D UV : register(t1);
SamplerState SL : register(s0); SamplerState SC : register(s1);
struct V { float4 p : SV_Position; float2 t : TEXCOORD0; };
V VS(uint id : SV_VertexID) { V o; float2 t = float2((id << 1) & 2, id & 2); o.t = t; o.p = float4(t * float2(2, -2) + float2(-1, 1), 0, 1); return o; }
float3 Rgb(float y, float u, float v) {
    y = (y - 16.0 / 255.0) * (255.0 / 219.0);
    float2 c = (float2(u, v) - 128.0 / 255.0) * (255.0 / 224.0);
    return saturate(float3(y + 1.5748 * c.y, y - 0.1873 * c.x - 0.4681 * c.y, y + 1.8556 * c.x));
}
float4 PS(V i) : SV_Target { float2 c = UV.Sample(SC, i.t).rg; return float4(Rgb(Y.Sample(SL, i.t).r, c.x, c.y), 1); }
float Ly(int2 q) {
    uint w, h; Y.GetDimensions(w, h);
    q = clamp(q, int2(0, 0), int2(w * 2 - 1, h - 1));
    float4 t = Y.Load(int3(q.x >> 1, q.y, 0));
    return (q.x & 1) ? t.b : t.r;
}
float2 Cc(int2 k) {
    uint w, h; Y.GetDimensions(w, h);
    return Y.Load(int3(clamp(k, int2(0, 0), int2(w - 1, h - 1)), 0)).ga;
}
// mode 0 = Catmull-Rom bicubic luma, 1 = pixel-exact, 2 = bilinear luma. Chroma is 4:2:2 (half width): interpolated unless pixel-exact.
float4 Yuy(V i, int mode) {
    uint w, h; Y.GetDimensions(w, h);
    float2 p = i.t * float2(w * 2, h);
    int2 q = (int2)floor(p);
    float2 p0 = p - 0.5; int2 q0 = (int2)floor(p0); float2 f = p0 - floor(p0);
    float l;
    if (mode == 1) l = Ly(q);
    else if (mode == 2) l = lerp(lerp(Ly(q0), Ly(q0 + int2(1, 0)), f.x), lerp(Ly(q0 + int2(0, 1)), Ly(q0 + int2(1, 1)), f.x), f.y);
    else {
        float4 wx = float4(f.x * (-0.5 + f.x * (1.0 - 0.5 * f.x)), 1.0 + f.x * f.x * (-2.5 + 1.5 * f.x), f.x * (0.5 + f.x * (2.0 - 1.5 * f.x)), f.x * f.x * (-0.5 + 0.5 * f.x));
        float4 wy = float4(f.y * (-0.5 + f.y * (1.0 - 0.5 * f.y)), 1.0 + f.y * f.y * (-2.5 + 1.5 * f.y), f.y * (0.5 + f.y * (2.0 - 1.5 * f.y)), f.y * f.y * (-0.5 + 0.5 * f.y));
        l = 0;
        [unroll] for (int j = 0; j < 4; j++) {
            float r = 0;
            [unroll] for (int k = 0; k < 4; k++) r += wx[k] * Ly(q0 + int2(k - 1, j - 1));
            l += wy[j] * r;
        }
    }
    float2 c;
    if (mode == 1) c = Cc(int2(q.x >> 1, q.y));
    else {
        float2 cp = float2((p.x - 0.5) * 0.5, p.y - 0.5); int2 k0 = (int2)floor(cp); float2 cf = cp - floor(cp);
        c = lerp(lerp(Cc(k0), Cc(k0 + int2(1, 0)), cf.x), lerp(Cc(k0 + int2(0, 1)), Cc(k0 + int2(1, 1)), cf.x), cf.y);
    }
    return float4(Rgb(l, c.x, c.y), 1);
}
float4 PSYc(V i) : SV_Target { return Yuy(i, 0); }
float4 PSYp(V i) : SV_Target { return Yuy(i, 1); }
float4 PSYl(V i) : SV_Target { return Yuy(i, 2); }";

    readonly ID3D11Device dev;
    readonly ID3D11DeviceContext ctx;
    readonly IDXGISwapChain1 sc;
    readonly ID3D11VertexShader vs;
    readonly ID3D11PixelShader psNv12, psYuyCubic, psYuyPoint, psYuyLinear;
    readonly ID3D11SamplerState point, linear;
    readonly ID3D11ShaderResourceView?[] srvs = new ID3D11ShaderResourceView?[2];
    readonly ID3D11SamplerState[] samplers = new ID3D11SamplerState[2];
    ID3D11RenderTargetView? rtv;
    ID3D11Texture2D? yTex, uvTex;
    int bw, bh, vw, vh;
    bool vYuy2;
    volatile int tw, th;
    public volatile bool Vsync;
    // Exclusive fullscreen on the capture thread (the driver's frame generation, AFMF on RX 6000, only hooks exclusive fullscreen)
    volatile bool wantFs;
    bool isFs;
    public bool Exclusive { set => wantFs = value; }
    public volatile int Scaler; // 0 = bicubic (default), 1 = pixel-exact, 2 = bilinear

    public Renderer(IntPtr hwnd, int w, int h)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory6>();
        // RX 6800 XT, not the Meta/Parsec virtual adapters
        factory.EnumAdapterByGpuPreference(0, GpuPreference.HighPerformance, out IDXGIAdapter1? adapter).CheckError();
        using (adapter)
        {
            D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 }, out dev!, out ctx!).CheckError();
        }
        using (var dxgi = dev.QueryInterface<IDXGIDevice1>()) { dxgi.MaximumFrameLatency = 1; dxgi.GPUThreadPriority = 7; }

        tw = bw = Math.Max(1, w); th = bh = Math.Max(1, h);
        sc = factory.CreateSwapChainForHwnd(dev, hwnd, new SwapChainDescription1
        {
            Width = (uint)bw, Height = (uint)bh, Format = Format.R8G8B8A8_UNorm,
            BufferUsage = Usage.RenderTargetOutput, BufferCount = 2, SampleDescription = new(1, 0),
            SwapEffect = SwapEffect.FlipDiscard, Flags = SwapChainFlags.AllowTearing,
        });
        factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAll);
        MakeRtv();

        vs = dev.CreateVertexShader(Compiler.Compile(Hlsl, "VS", "s", "vs_5_0").Span);
        psNv12 = dev.CreatePixelShader(Compiler.Compile(Hlsl, "PS", "s", "ps_5_0").Span);
        psYuyCubic = dev.CreatePixelShader(Compiler.Compile(Hlsl, "PSYc", "s", "ps_5_0").Span);
        psYuyPoint = dev.CreatePixelShader(Compiler.Compile(Hlsl, "PSYp", "s", "ps_5_0").Span);
        psYuyLinear = dev.CreatePixelShader(Compiler.Compile(Hlsl, "PSYl", "s", "ps_5_0").Span);
        point = dev.CreateSamplerState(new SamplerDescription(Filter.MinMagMipPoint, TextureAddressMode.Clamp, TextureAddressMode.Clamp, TextureAddressMode.Clamp));
        linear = dev.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp, TextureAddressMode.Clamp, TextureAddressMode.Clamp));
        samplers[1] = linear; // chroma is half-res: always filtered
    }

    public void SetTarget(int w, int h) { tw = Math.Max(1, w); th = Math.Max(1, h); }

    void MakeRtv()
    {
        using var bb = sc.GetBuffer<ID3D11Texture2D>(0);
        rtv = dev.CreateRenderTargetView(bb);
    }

    void Textures(int fw, int fh, bool yuy2)
    {
        if (yTex != null && vw == fw && vh == fh && vYuy2 == yuy2) return;
        srvs[0]?.Dispose(); srvs[1]?.Dispose(); srvs[1] = null; yTex?.Dispose(); uvTex?.Dispose(); uvTex = null;
        if (yuy2)
            yTex = dev.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UNorm, (uint)fw / 2, (uint)fh, 1, 1, BindFlags.ShaderResource));
        else
        {
            yTex = dev.CreateTexture2D(new Texture2DDescription(Format.R8_UNorm, (uint)fw, (uint)fh, 1, 1, BindFlags.ShaderResource));
            uvTex = dev.CreateTexture2D(new Texture2DDescription(Format.R8G8_UNorm, (uint)fw / 2, (uint)fh / 2, 1, 1, BindFlags.ShaderResource));
            srvs[1] = dev.CreateShaderResourceView(uvTex);
        }
        srvs[0] = dev.CreateShaderResourceView(yTex);
        vw = fw; vh = fh; vYuy2 = yuy2;
    }

    void Resize()
    {
        bool fsChange = wantFs != isFs;
        if (tw == bw && th == bh && !fsChange) return;
        ctx.ClearState();
        rtv?.Dispose();
        try
        {
            if (fsChange) { bool target = wantFs; sc.SetFullscreenState(target, null).CheckError(); isFs = target; }
            sc.ResizeBuffers(0, (uint)tw, (uint)th, Format.Unknown, SwapChainFlags.AllowTearing).CheckError();
            bw = tw; bh = th;
        }
        catch { wantFs = isFs; } // driver refused: keep the current mode and keep drawing
        finally { MakeRtv(); }
    }

    // No signal: a flip-model window that never presents is invisible, so show black. Capture thread only.
    public void Black()
    {
        Resize();
        ctx.OMSetRenderTargets(rtv!);
        ctx.ClearRenderTargetView(rtv!, new Vortice.Mathematics.Color4(0f, 0f, 0f, 1f));
        sc.Present(1u, PresentFlags.None);
    }

    // Called only from the capture thread; data = YUY2 (packed) or NV12 (Y plane then interleaved UV)
    public void Draw(IntPtr data, int pitch, int fw, int fh, bool yuy2)
    {
        Textures(fw, fh, yuy2);
        Resize();
        ctx.UpdateSubresource(yTex!, 0u, null, data, (uint)pitch, 0u);
        if (!yuy2) ctx.UpdateSubresource(uvTex!, 0u, null, data + pitch * fh, (uint)pitch, 0u);

        // letterbox, keep aspect
        float s = Math.Min((float)bw / fw, (float)bh / fh);
        int dw = (int)(fw * s), dh = (int)(fh * s);
        ctx.OMSetRenderTargets(rtv!);
        if (dw != bw || dh != bh) ctx.ClearRenderTargetView(rtv!, new Vortice.Mathematics.Color4(0f, 0f, 0f, 1f));
        ctx.RSSetViewport((bw - dw) / 2, (bh - dh) / 2, dw, dh);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.VSSetShader(vs);
        ctx.PSSetShader(yuy2 ? Scaler switch { 1 => psYuyPoint, 2 => psYuyLinear, _ => psYuyCubic } : psNv12);
        ctx.PSSetShaderResources(0, srvs!);
        samplers[0] = Scaler == 1 ? point : linear;
        ctx.PSSetSamplers(0, samplers);
        ctx.Draw(3u, 0u);
        sc.Present(Vsync ? 1u : 0u, Vsync ? PresentFlags.None : PresentFlags.AllowTearing);
    }

    public void Dispose() { sc.Dispose(); ctx.Dispose(); dev.Dispose(); }

    // ---------------- frame generation ----------------
    // Real frames are converted to RGB at native size (two textures: previous and current). The model gets both at half size
    // (960x544, 1080 rows padded to 1088) and returns flow + blend mask at that size; the in-between frame is warped and blended
    // here from the full-size frames, so only the motion estimate is half resolution, never the picture.
    public const int HalfW = 960, HalfH = 544;
    const string FgHlsl = @"
Texture2D T0 : register(t0); Texture2D T1 : register(t1); Texture2D Fl : register(t2); Texture2D Mk : register(t3);
SamplerState SL : register(s0);
cbuffer C : register(b0) { float2 src; float steady; float tol; };
struct V { float4 p : SV_Position; float2 t : TEXCOORD0; };
float4 PSHalf(V i) : SV_Target { return T0.SampleLevel(SL, float2(i.t.x, i.t.y * 1088.0 / 1080.0), 0); } // 2x2 average, bottom rows clamp
float Mx(float3 v) { return max(max(v.x, v.y), v.z); }
float4 PSMid(V i) : SV_Target {
    float2 px = i.p.xy;                                   // pixel centre, full size
    float2 fuv = float2(px.x / src.x, px.y / 1088.0 * (src.y / 1080.0));
    float4 f = Fl.SampleLevel(SL, fuv, 0) * 2.0;         // half-size pixels -> full-size pixels
    float m = Mk.SampleLevel(SL, fuv, 0).r;
    float3 a = T0.SampleLevel(SL, (px + f.xy) / src, 0).rgb;
    float3 b = T1.SampleLevel(SL, (px + f.zw) / src, 0).rgb;
    float3 o = a * m + b * (1.0 - m);
    if (steady > 0.5) {
        // Pixels that did not change between the two real frames (HUD, minimap, subtitles) are kept, not warped along
        // with the scene behind them. 3x3 neighbourhood so moving edges never count as still. Only where the warp would
        // put something clearly different there: in soft, low-contrast moving areas A and B also look alike, and there
        // the warp is right (measured: copying them cost 3.5 dB on a panning photo).
        int2 q = int2(px); float d = 0;
        [unroll] for (int y = -1; y <= 1; y++) [unroll] for (int x = -1; x <= 1; x++)
            d = max(d, Mx(abs(T0.Load(int3(q + int2(x, y), 0)).rgb - T1.Load(int3(q + int2(x, y), 0)).rgb)));
        float3 still = (T0.Load(int3(q, 0)).rgb + T1.Load(int3(q, 0)).rgb) * 0.5;
        float keep = (1.0 - saturate((d - tol) / tol)) * saturate((Mx(abs(o - still)) - 3.0 * tol) / tol);
        o = lerp(o, still, keep);
    }
    return float4(o, 1);
}
float3 Lr(int2 q) { uint w, h; T0.GetDimensions(w, h); return T0.Load(int3(clamp(q, int2(0, 0), int2(w - 1, h - 1)), 0)).rgb; }
float4 Scale(V i, int mode) {
    uint w, h; T0.GetDimensions(w, h);
    float2 p = i.t * float2(w, h);
    if (mode == 1) return float4(Lr(int2(floor(p))), 1);
    float2 p0 = p - 0.5; int2 q0 = (int2)floor(p0); float2 f = p0 - floor(p0);
    if (mode == 2) return float4(lerp(lerp(Lr(q0), Lr(q0 + int2(1, 0)), f.x), lerp(Lr(q0 + int2(0, 1)), Lr(q0 + int2(1, 1)), f.x), f.y), 1);
    float4 wx = float4(f.x * (-0.5 + f.x * (1.0 - 0.5 * f.x)), 1.0 + f.x * f.x * (-2.5 + 1.5 * f.x), f.x * (0.5 + f.x * (2.0 - 1.5 * f.x)), f.x * f.x * (-0.5 + 0.5 * f.x));
    float4 wy = float4(f.y * (-0.5 + f.y * (1.0 - 0.5 * f.y)), 1.0 + f.y * f.y * (-2.5 + 1.5 * f.y), f.y * (0.5 + f.y * (2.0 - 1.5 * f.y)), f.y * f.y * (-0.5 + 0.5 * f.y));
    float3 c = 0;
    [unroll] for (int j = 0; j < 4; j++) {
        float3 r = 0;
        [unroll] for (int k = 0; k < 4; k++) r += wx[k] * Lr(q0 + int2(k - 1, j - 1));
        c += wy[j] * r;
    }
    return float4(saturate(c), 1);
}
float4 PSSc(V i) : SV_Target { return Scale(i, 0); }
float4 PSSp(V i) : SV_Target { return Scale(i, 1); }
float4 PSSl(V i) : SV_Target { return Scale(i, 2); }";

    ID3D11PixelShader? psHalf, psMid, psScCubic, psScPoint, psScLinear;
    ID3D11Buffer? cb;
    ID3D11Texture2D? rgbPrev, rgbCur, midTex, halfTex, halfStage, flowTex, maskTex;
    ID3D11ShaderResourceView? srvPrev, srvCur, srvMid, srvFlow, srvMask;
    ID3D11RenderTargetView? rtvPrev, rtvCur, rtvMid, rtvHalf;
    int rw, rh;

    // DXGI index of the adapter we render on, for DirectML (it counts adapters in EnumAdapters order)
    public int AdapterIndex()
    {
        using var f = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        using var me = dev.QueryInterface<IDXGIDevice>();
        using var ad = me.GetAdapter();
        var luid = ad.Description.Luid;
        for (uint i = 0; f.EnumAdapters1(i, out var a).Success; i++)
            using (a) if (a.Description1.Luid.Equals(luid)) return (int)i;
        return 0;
    }

    void FgResources(int fw, int fh)
    {
        if (psMid == null)
        {
            psHalf = dev.CreatePixelShader(Compiler.Compile(FgHlsl, "PSHalf", "fg", "ps_5_0").Span);
            psMid = dev.CreatePixelShader(Compiler.Compile(FgHlsl, "PSMid", "fg", "ps_5_0").Span);
            psScCubic = dev.CreatePixelShader(Compiler.Compile(FgHlsl, "PSSc", "fg", "ps_5_0").Span);
            psScPoint = dev.CreatePixelShader(Compiler.Compile(FgHlsl, "PSSp", "fg", "ps_5_0").Span);
            psScLinear = dev.CreatePixelShader(Compiler.Compile(FgHlsl, "PSSl", "fg", "ps_5_0").Span);
            cb = dev.CreateBuffer(new BufferDescription(16, BindFlags.ConstantBuffer));
            halfTex = dev.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UNorm, HalfW, HalfH, 1, 1, BindFlags.RenderTarget));
            rtvHalf = dev.CreateRenderTargetView(halfTex);
            halfStage = dev.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UNorm, HalfW, HalfH, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
            flowTex = dev.CreateTexture2D(new Texture2DDescription(Format.R16G16B16A16_Float, HalfW, HalfH, 1, 1, BindFlags.ShaderResource));
            srvFlow = dev.CreateShaderResourceView(flowTex);
            maskTex = dev.CreateTexture2D(new Texture2DDescription(Format.R16_Float, HalfW, HalfH, 1, 1, BindFlags.ShaderResource));
            srvMask = dev.CreateShaderResourceView(maskTex);
        }
        if (rgbCur != null && rw == fw && rh == fh) return;
        foreach (var d in new IDisposable?[] { srvPrev, srvCur, srvMid, rtvPrev, rtvCur, rtvMid, rgbPrev, rgbCur, midTex }) d?.Dispose();
        var desc = new Texture2DDescription(Format.R8G8B8A8_UNorm, (uint)fw, (uint)fh, 1, 1, BindFlags.ShaderResource | BindFlags.RenderTarget);
        rgbPrev = dev.CreateTexture2D(desc); rgbCur = dev.CreateTexture2D(desc); midTex = dev.CreateTexture2D(desc);
        srvPrev = dev.CreateShaderResourceView(rgbPrev); srvCur = dev.CreateShaderResourceView(rgbCur); srvMid = dev.CreateShaderResourceView(midTex);
        rtvPrev = dev.CreateRenderTargetView(rgbPrev); rtvCur = dev.CreateRenderTargetView(rgbCur); rtvMid = dev.CreateRenderTargetView(midTex);
        rw = fw; rh = fh;
    }

    void Pass(ID3D11RenderTargetView target, int x, int y, int w, int h, ID3D11PixelShader ps, params ID3D11ShaderResourceView?[] inputs)
    {
        ctx.OMSetRenderTargets(target);
        ctx.RSSetViewport(x, y, w, h);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.VSSetShader(vs);
        ctx.PSSetShader(ps);
        ctx.PSSetShaderResources(0, inputs);
        ctx.PSSetSamplers(0, [linear, linear]);
        ctx.PSSetConstantBuffer(0, cb);
        ctx.Draw(3u, 0u);
        ctx.PSSetShaderResources(0, new ID3D11ShaderResourceView?[4]); // unbind so the next pass may render into them
    }

    // A new real frame: becomes "current" (the old current becomes "previous"), and its half-size RGBA copy lands in halfOut
    public unsafe void NewFrame(IntPtr data, int pitch, int fw, int fh, bool yuy2, byte[] halfOut)
    {
        Textures(fw, fh, yuy2);
        FgResources(fw, fh);
        ctx.UpdateSubresource(yTex!, 0u, null, data, (uint)pitch, 0u);
        if (!yuy2) ctx.UpdateSubresource(uvTex!, 0u, null, data + pitch * fh, (uint)pitch, 0u);
        (rgbPrev, rgbCur) = (rgbCur, rgbPrev); (srvPrev, srvCur) = (srvCur, srvPrev); (rtvPrev, rtvCur) = (rtvCur, rtvPrev);
        samplers[0] = linear;
        ctx.PSSetSamplers(0, samplers);
        // Catmull-Rom at 1:1 is the identity for luma and interpolates the 4:2:2 chroma
        ctx.OMSetRenderTargets(rtvCur!); ctx.RSSetViewport(0, 0, fw, fh);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList); ctx.VSSetShader(vs);
        ctx.PSSetShader(yuy2 ? psYuyCubic : psNv12); ctx.PSSetShaderResources(0, srvs!); ctx.Draw(3u, 0u);
        ctx.PSSetShaderResources(0, new ID3D11ShaderResourceView?[2]);
        Pass(rtvHalf!, 0, 0, HalfW, HalfH, psHalf!, srvCur);
        ctx.CopyResource(halfStage!, halfTex!);
        var map = ctx.Map(halfStage!, 0, MapMode.Read);
        try
        {
            fixed (byte* dst = halfOut)
                for (int y = 0; y < HalfH; y++)
                    Buffer.MemoryCopy((byte*)map.DataPointer + (long)y * map.RowPitch, dst + y * HalfW * 4, HalfW * 4, HalfW * 4);
        }
        finally { ctx.Unmap(halfStage!, 0); }
    }

    // Build the in-between frame from the model's flow (fp16 x4) and mask (fp16) into midTex
    public unsafe void Interpolate(ReadOnlySpan<byte> flow, ReadOnlySpan<byte> mask, bool steady)
    {
        fixed (byte* f = flow) ctx.UpdateSubresource(flowTex!, 0u, null, (IntPtr)f, HalfW * 8u, 0u);
        fixed (byte* m = mask) ctx.UpdateSubresource(maskTex!, 0u, null, (IntPtr)m, HalfW * 2u, 0u);
        float* c = stackalloc float[4] { rw, rh, steady ? 1f : 0f, 8f / 255f };
        ctx.UpdateSubresource(cb!, 0u, null, (IntPtr)c, 0u, 0u);
        Pass(rtvMid!, 0, 0, rw, rh, psMid!, srvPrev, srvCur, srvFlow, srvMask);
    }

    // Self-test only: wait until the GPU has finished everything submitted so far
    public void Sync()
    {
        using var q = dev.CreateQuery(new QueryDescription(QueryType.Event));
        ctx.End(q);
        while (ctx.GetData(q, IntPtr.Zero, 0, AsyncGetDataFlags.None).Code != 0) Thread.SpinWait(20); // S_FALSE until done
    }

    // Self-test only (--fgtest): copy a full-size RGB texture (0 = previous, 1 = current, 2 = in-between) to dst
    public unsafe void ReadRgb(int which, byte[] dst)
    {
        var src = which switch { 0 => rgbPrev, 1 => rgbCur, _ => midTex };
        using var st = dev.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UNorm, (uint)rw, (uint)rh, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
        ctx.CopyResource(st, src!);
        var map = ctx.Map(st, 0, MapMode.Read);
        try
        {
            fixed (byte* d = dst)
                for (int y = 0; y < rh; y++) Buffer.MemoryCopy((byte*)map.DataPointer + (long)y * map.RowPitch, d + (long)y * rw * 4, rw * 4, rw * 4);
        }
        finally { ctx.Unmap(st, 0); }
    }

    // Show the in-between frame (mid = true) or the current real frame, scaled to the window like the direct path
    public void PresentRgb(bool mid)
    {
        Resize();
        float s = Math.Min((float)bw / rw, (float)bh / rh);
        int dw = (int)(rw * s), dh = (int)(rh * s);
        if (dw != bw || dh != bh) ctx.ClearRenderTargetView(rtv!, new Vortice.Mathematics.Color4(0f, 0f, 0f, 1f));
        Pass(rtv!, (bw - dw) / 2, (bh - dh) / 2, dw, dh, Scaler switch { 1 => psScPoint!, 2 => psScLinear!, _ => psScCubic! }, mid ? srvMid : srvCur);
        sc.Present(Vsync ? 1u : 0u, Vsync ? PresentFlags.None : PresentFlags.AllowTearing);
    }
}
