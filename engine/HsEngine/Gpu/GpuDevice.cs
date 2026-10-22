using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;
using static Vortice.Direct3D11.D3D11;

namespace Handshake.Engine.Gpu;

/// <summary>
/// A D3D11 device on one adapter plus the Media Foundation device manager, so capture, colour
/// conversion and the hardware encoder/decoder all share textures without copies.
/// </summary>
public sealed class GpuDevice : IDisposable
{
    public IDXGIAdapter1 Adapter { get; }
    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public IMFDXGIDeviceManager Manager { get; }
    public string Name { get; }
    public uint VendorId { get; }

    public GpuDevice(IDXGIAdapter1 adapter)
    {
        Adapter = adapter;
        var desc = adapter.Description1;
        Name = desc.Description;
        VendorId = desc.VendorId;
        D3D11CreateDevice(adapter, DriverType.Unknown,
            DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
            new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 },
            out ID3D11Device device, out ID3D11DeviceContext context).CheckError();
        Device = device;
        Context = context;
        // Media Foundation uses the device from its own threads
        using (var mt = device.QueryInterface<ID3D11Multithread>()) mt.SetMultithreadProtected(true);
        Manager = MediaFactory.MFCreateDXGIDeviceManager();
        Manager.ResetDevice(device).CheckError();
    }

    /// <summary>Adapter that drives the given output (capture must happen there), or the first adapter.</summary>
    public static (IDXGIAdapter1 adapter, IDXGIOutput1? output) FindOutput(int outputIndex)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        int seen = 0;
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
            {
                if (seen++ == outputIndex) return (adapter, output.QueryInterface<IDXGIOutput1>());
                output.Dispose();
            }
            adapter.Dispose();
        }
        factory.EnumAdapters1(0, out var first).CheckError();
        return (first, null);
    }

    /// <summary>First adapter of a vendor: "nvidia", "amd", "intel"; null/"auto" = first adapter.</summary>
    public static IDXGIAdapter1 FindAdapter(string? vendor)
    {
        uint want = vendor?.ToLowerInvariant() switch { "nvidia" => 0x10DE, "amd" => 0x1002, "intel" => 0x8086, _ => 0 };
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            if (want == 0 || adapter.Description1.VendorId == want) return adapter;
            adapter.Dispose();
        }
        throw new InvalidOperationException($"Không thấy GPU {vendor}");
    }

    public static List<(int index, string adapter, string device, Vortice.RawRect rect)> ListOutputs()
    {
        var list = new List<(int, string, string, Vortice.RawRect)>();
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        int seen = 0;
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
            {
                var d = output.Description;
                list.Add((seen++, adapter.Description1.Description, d.DeviceName, d.DesktopCoordinates));
                output.Dispose();
            }
            adapter.Dispose();
        }
        return list;
    }

    public ID3D11Texture2D CreateTexture(uint width, uint height, Format format, BindFlags bind)
    {
        return Device.CreateTexture2D(new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = bind,
        });
    }

    public void Dispose()
    {
        Manager.Dispose();
        Context.Dispose();
        Device.Dispose();
        Adapter.Dispose();
    }
}
