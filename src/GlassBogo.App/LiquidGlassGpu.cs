using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Direct3D9;
using Vortice.DXGI;
using Format = Vortice.DXGI.Format;

namespace GlassBogo.App;

// Independent GPU material for V2 desktop surfaces.
// Production reads completed BGRA frames. The optional legacy interop probe must
// lock D3DImage before Render and detach its back buffer before Dispose.
internal sealed class LiquidGlassGpu : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Parameters
    {
        public Vector4 SourceRect, Shape, Pointer, Tint, Mapping;
    }
    private readonly ID3D11DeviceContext context;
    private IDirect3D9Ex? d3d9;
    private IDirect3DDevice9Ex? device9;
    private IDirect3DTexture9? texture9;
    private IDirect3DSurface9? surface9;
    private ID3D11Texture2D? shared, blur;
    private ID3D11RenderTargetView? outputView, blurView;
    private ID3D11ShaderResourceView? blurInput;
    private ID3D11VertexShader? vertex;
    private ID3D11PixelShader? horizontal, glass;
    private ID3D11SamplerState? sampler;
    private ID3D11Buffer? constants;
    private ID3D11Query? complete;
    private ID3D11DeviceContext3? context3;
    private AutoResetEvent? completionEvent;
    private readonly int width, height;
    private readonly bool ownsDriver;
    private readonly bool sharedPresentation;
    private ID3D11Texture2D? readback;
    private byte[]? readbackPixels;
    private bool disposed;
    internal nint Surface => surface9!.NativePointer;
    internal ID3D11Texture2D Texture => shared!;

    internal LiquidGlassGpu(ID3D11Device device, ID3D11DeviceContext context, long adapterLuid, nint hwnd, int width, int height, LiquidGlassGpu? resources = null, bool sharedPresentation = true)
    {
        this.context = context;
        this.width = width;
        this.height = height;
        ownsDriver = resources is null;
        this.sharedPresentation = resources?.sharedPresentation ?? sharedPresentation;
        try
        {
            if (resources is not null)
            {
                device9 = resources.device9;
                vertex = resources.vertex;
                horizontal = resources.horizontal;
                glass = resources.glass;
                sampler = resources.sampler;
                constants = resources.constants;
                complete = resources.complete;
                context3 = resources.context3;
                completionEvent = resources.completionEvent;
            }
            else
            {
                if (this.sharedPresentation)
                {
                    d3d9 = D3D9.Direct3DCreate9Ex();
                    uint index = 0;
                    for (; index < d3d9.AdapterCount; index++)
                    {
                        var luid = d3d9.GetAdapterLuid(index);
                        if (((long)luid.HighPart << 32 | luid.LowPart) == adapterLuid)
                            break;
                    }
                    if (index == d3d9.AdapterCount)
                        throw new InvalidOperationException("D3D9Ex 与主屏 D3D11 适配器不匹配。");
                    device9 = d3d9.CreateDeviceEx(index, DeviceType.Hardware, hwnd,
                        CreateFlags.HardwareVertexProcessing | CreateFlags.Multithreaded | CreateFlags.FpuPreserve,
                        new Vortice.Direct3D9.PresentParameters { Windowed = true, SwapEffect = Vortice.Direct3D9.SwapEffect.Discard, DeviceWindowHandle = hwnd, BackBufferWidth = 1, BackBufferHeight = 1 });
                }
                vertex = device.CreateVertexShader(Compiler.Compile(Shader, "Vertex", "LiquidGlass", "vs_5_0").Span);
                horizontal = device.CreatePixelShader(Compiler.Compile(Shader, "Horizontal", "LiquidGlass", "ps_5_0").Span);
                glass = device.CreatePixelShader(Compiler.Compile(Shader, "Glass", "LiquidGlass", "ps_5_0").Span);
                sampler = device.CreateSamplerState(new SamplerDescription(Vortice.Direct3D11.Filter.MinMagMipLinear, TextureAddressMode.Clamp));
                constants = device.CreateBuffer(80, BindFlags.ConstantBuffer);
                complete = device.CreateQuery(new QueryDescription(Vortice.Direct3D11.QueryType.Event));
                try
                {
                    context3 = context.QueryInterface<ID3D11DeviceContext3>();
                    completionEvent = new(false);
                }
                catch (SharpGen.Runtime.SharpGenException) { /* Older compatible devices use the bounded query path. */ }
            }
            if (this.sharedPresentation)
            {
                if (device9 is null)
                    throw new InvalidOperationException("共享图形设备已释放。");
                nint handle = 0;
                texture9 = device9.CreateTexture((uint)width, (uint)height, 1, Vortice.Direct3D9.Usage.RenderTarget, Vortice.Direct3D9.Format.A8R8G8B8, Pool.Default, ref handle);
                surface9 = texture9.GetSurfaceLevel(0);
                shared = device.OpenSharedResource<ID3D11Texture2D>(handle);
            }
            else
                shared = device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1, BindFlags.RenderTarget | BindFlags.ShaderResource));
            outputView = device.CreateRenderTargetView(shared);
            blur = device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1, BindFlags.RenderTarget | BindFlags.ShaderResource));
            blurView = device.CreateRenderTargetView(blur);
            blurInput = device.CreateShaderResourceView(blur);
        }
        catch { Dispose(); throw; }
    }

    // Reuse staging storage on the serialized worker. Callers create an owned,
    // immutable bitmap before this array is used by the next frame.
    internal byte[] ReadFrame(ID3D11Device device)
    {
        readback ??= device.CreateTexture2D(shared!.Description with
        {
            BindFlags = BindFlags.None,
            MiscFlags = ResourceOptionFlags.None,
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read
        });
        readbackPixels ??= new byte[checked(width * height * 4)];
        context.CopyResource(readback, shared!);
        var mapped = context.Map(readback, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            for (var y = 0; y < height; y++)
                Marshal.Copy(mapped.DataPointer + checked((int)(y * mapped.RowPitch)), readbackPixels, y * width * 4, width * 4);
        }
        finally { context.Unmap(readback, 0); }
        return readbackPixels;
    }

    internal void Render(ID3D11Device device, ID3D11Texture2D frame, Vector4 sourceRect, Vector2 pointer, bool dark, bool reduced, float radius = 24, int rotation = 0)
    {
        var p = new Parameters
        {
            SourceRect = sourceRect,
            Shape = new(width, height, radius, reduced ? 0 : 1),
            Mapping = new(rotation, 0, 0, 0),
            Pointer = new(pointer.X, pointer.Y, 0, 0),
            Tint = dark ? new(.07f, .10f, .16f, .16f) : new(.96f, .98f, 1, .10f)
        };
        context.UpdateSubresource(in p, constants!);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.VSSetShader(vertex);
        context.PSSetConstantBuffer(0, constants);
        context.PSSetSampler(0, sampler);
        context.RSSetViewport(0, 0, width, height);
        using var input = device.CreateShaderResourceView(frame);
        context.OMSetRenderTargets(blurView!);
        context.PSSetShader(horizontal);
        context.PSSetShaderResource(0, input);
        context.Draw(3, 0);
        context.PSSetShaderResource(0, null!);
        context.OMSetRenderTargets(outputView!);
        context.PSSetShader(glass);
        context.PSSetShaderResource(0, blurInput!);
        context.PSSetShaderResource(1, input);
        context.Draw(3, 0);
        context.PSSetShaderResource(0, null!);
        context.PSSetShaderResource(1, null!);
        context.OMSetRenderTargets(0, [], null);
        context.End(complete!);
        if (context3 is not null && completionEvent is not null)
        {
            completionEvent.Reset();
            context3.Flush1(ContextType.All, completionEvent.SafeWaitHandle.DangerousGetHandle());
            if (!completionEvent.WaitOne(1000))
                throw new TimeoutException("玻璃 GPU 完成事件超时。");
        }
        else
            context.Flush();
        // D3DImage consumes the D3D9Ex shared surface on a different device.
        // A bounded fence prevents presenting unfinished D3D11 work.
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!context.GetData(complete!, out int ready) || ready == 0)
        {
            if (watch.ElapsedMilliseconds > 1000)
                throw new TimeoutException("玻璃 GPU 帧未在一秒内完成。");
            Thread.Sleep(1);
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        blurInput?.Dispose();
        blurView?.Dispose();
        outputView?.Dispose();
        readback?.Dispose();
        if (readbackPixels is not null)
            Array.Clear(readbackPixels);
        blur?.Dispose();
        shared?.Dispose();
        surface9?.Dispose();
        texture9?.Dispose();
        if (ownsDriver)
        {
            complete?.Dispose();
            constants?.Dispose();
            sampler?.Dispose();
            context3?.Dispose();
            completionEvent?.Dispose();
            glass?.Dispose();
            horizontal?.Dispose();
            vertex?.Dispose();
            device9?.Dispose();
            d3d9?.Dispose();
        }
    }

    // Original shader: spatial Gaussian sampling, rounded SDF normals for local
    // refraction, a narrow rim and pointer-local specular light. No copied assets.
    private const string Shader = """
        Texture2D background : register(t0);
        Texture2D clearBackground : register(t1);
        SamplerState linearClamp : register(s0);
        cbuffer Settings : register(b0) {
            float4 sourceRect;
            float4 shape;
            float4 pointer;
            float4 tint;
            float4 mapping;
        };
        struct Fragment { float4 position : SV_POSITION; float2 uv : TEXCOORD0; };
        Fragment Vertex(uint id : SV_VertexID) {
            Fragment o;
            o.uv = float2((id << 1) & 2, id & 2);
            o.position = float4(o.uv * float2(2,-2) + float2(-1,1), 0, 1);
            return o;
        }
        float2 BackgroundUv(float2 uv) {
            if (mapping.x == 90) return float2(uv.y, 1-uv.x);
            if (mapping.x == 180) return 1-uv;
            if (mapping.x == 270) return float2(1-uv.y, uv.x);
            return uv;
        }
        float4 Horizontal(Fragment f) : SV_TARGET {
            float2 uv = sourceRect.xy + f.uv * sourceRect.zw;
            float2 step = float2(sourceRect.z / shape.x * 1.2 * shape.z / 24.0, 0);
            float4 color = 0;
            float total = 0;
            [unroll] for (int i = -6; i <= 6; i++) {
                float weight = exp(-float(i*i) / 12.5);
                color += background.Sample(linearClamp, BackgroundUv(uv + step*i)) * weight;
                total += weight;
            }
            return color / total;
        }
        float distanceToEdge(float2 p) {
            float2 q = abs(p - shape.xy * .5) - (shape.xy * .5 - shape.z);
            return length(max(q, 0)) + min(max(q.x,q.y),0) - shape.z;
        }
        float4 Glass(Fragment f) : SV_TARGET {
            float2 p = f.uv * shape.xy;
            float d = distanceToEdge(p);
            float alpha = saturate(.5 - d);
            float2 n = normalize(float2(distanceToEdge(p+float2(1,0))-distanceToEdge(p-float2(1,0)),
                                       distanceToEdge(p+float2(0,1))-distanceToEdge(p-float2(0,1))) + .00001);
            float dpi = shape.z / 24.0;
            float edge = exp(-abs(d)/(10.0*dpi));
            float2 uv = f.uv - n * edge * 9.0 * dpi * shape.w / shape.xy;
            float3 color = 0;
            float total = 0;
            [unroll] for (int i = -5; i <= 5; i++) {
                float weight = exp(-float(i*i)/9.0);
                color += background.Sample(linearClamp, uv+float2(0,i*1.2*dpi/shape.y)).rgb * weight;
                total += weight;
            }
            // Preserve recognizable background detail through the flat body;
            // edge displacement and a softer blur suggest a rounded lens.
            float3 clear = clearBackground.Sample(linearClamp, BackgroundUv(sourceRect.xy + uv * sourceRect.zw)).rgb;
            color = lerp(clear, color/total, .26 + edge*.34);
            float luminance = dot(color,float3(.2126,.7152,.0722));
            color = lerp(luminance.xxx,color,1.08);
            color = lerp(color, tint.rgb, tint.a);
            float rim = exp(-abs(d/dpi+1.1)*1.1);
            float light = .35 + .45*saturate(dot(n,normalize(float2(-.4,-1))));
            float glow = exp(-dot(p-pointer.xy,p-pointer.xy)/9000.0);
            color += rim*light*.28 + edge*glow*.055*shape.w;
            return float4(saturate(color)*alpha,alpha);
        }
        """;
}
