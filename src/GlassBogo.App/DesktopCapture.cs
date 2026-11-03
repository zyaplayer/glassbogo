using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace GlassBogo.App;

// One duplication per output, created on the adapter that physically owns it.
// Callers own the thread: no UI work and no image persistence happen here.
internal sealed class DesktopCapture : IDisposable
{
    internal ID3D11Device Device { get; private set; } = null!;
    internal ID3D11DeviceContext Context { get; private set; } = null!;
    internal string AdapterName { get; private set; } = "";
    internal long AdapterLuid
    {
        get; private set;
    }
    internal OutputDescription Output
    {
        get; private set;
    }
    private IDXGIOutputDuplication? duplication;
    private bool held;
    internal OutduplFrameInfo FrameInfo
    {
        get; private set;
    }

    internal DesktopCapture(string displayName, bool duplicate = true)
    {
        var stage = "EnumerateOutput";
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
            {
                using (adapter)
                {
                    for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                    {
                        using (output)
                        {
                            if (output.Description.DeviceName != displayName)
                                continue;
                            Output = output.Description;
                            AdapterName = adapter.Description1.Description;
                            AdapterLuid = adapter.Description1.Luid;
                            stage = "D3D11CreateDevice";
                            D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                                [FeatureLevel.Level_11_0], out ID3D11Device device, out ID3D11DeviceContext context).CheckError();
                            Device = device;
                            Context = context;
                            if (duplicate)
                            {
                                stage = "DuplicateOutput";
                                using var output1 = output.QueryInterface<IDXGIOutput1>();
                                duplication = output1.DuplicateOutput(Device);
                            }
                            return;
                        }
                    }
                }
            }
            throw new InvalidOperationException("找不到主显示器对应的 DXGI 输出。");
        }
        catch (Exception ex) { Dispose(); throw new DesktopCaptureException(stage, AdapterName, ex); }
    }

    internal ID3D11Texture2D? Acquire(uint timeoutMilliseconds = 100)
    {
        Release();
        var result = duplication!.AcquireNextFrame(timeoutMilliseconds, out var info, out var resource);
        if (result.Code == unchecked((int)0x887A0027))
            return null; // WAIT_TIMEOUT
        result.CheckError();
        FrameInfo = info;
        held = true;
        using (resource)
        {
            var texture = resource.QueryInterface<ID3D11Texture2D>();
            if (texture.Description.Format != Format.B8G8R8A8_UNorm)
            {
                texture.Dispose();
                throw new InvalidDataException("桌面采样格式不兼容，保留上一完整材质。");
            }
            return texture;
        }
    }

    internal void Release()
    {
        if (!held)
            return;
        held = false;
        duplication!.ReleaseFrame().CheckError();
    }

    // Bounded readback for diagnostics and the rendered material's idle snapshot.
    // Interactive capture/blur/refraction stays on the GPU; these bytes are never saved.
    internal byte[] ReadPixels(ID3D11Texture2D texture)
    {
        var desc = texture.Description;
        desc.BindFlags = BindFlags.None;
        desc.MiscFlags = ResourceOptionFlags.None;
        desc.Usage = ResourceUsage.Staging;
        desc.CPUAccessFlags = CpuAccessFlags.Read;
        using var staging = Device.CreateTexture2D(desc);
        Context.CopyResource(staging, texture);
        var map = Context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var stride = checked((int)desc.Width * 4);
            var pixels = new byte[checked(stride * (int)desc.Height)];
            for (var y = 0; y < desc.Height; y++)
                Marshal.Copy(map.DataPointer + checked((int)(y * map.RowPitch)), pixels, checked((int)y * stride), stride);
            return pixels;
        }
        finally { Context.Unmap(staging, 0); }
    }

    public void Dispose()
    {
        if (held)
        {
            held = false;
            duplication?.ReleaseFrame();
        }
        duplication?.Dispose();
        duplication = null;
        Context?.ClearState();
        Context?.Dispose();
        Device?.Dispose();
    }
}

internal sealed class DesktopCaptureException : Exception
{
    internal string Stage
    {
        get;
    }
    internal string Adapter
    {
        get;
    }
    internal DesktopCaptureException(string stage, string adapter, Exception inner)
        : base($"{stage} ({adapter}): {inner.Message}", inner)
    {
        Stage = stage;
        Adapter = adapter;
        HResult = inner.HResult;
    }
}
