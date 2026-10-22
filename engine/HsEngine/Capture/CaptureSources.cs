using Handshake.Engine.Common;
using Handshake.Engine.Gpu;
using Vortice;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Handshake.Engine.Capture;

public interface ICaptureSource : IDisposable
{
    uint Width { get; }
    uint Height { get; }
    string Description { get; }

    /// <summary>
    /// Produces the next BGRA frame. Returns null when nothing changed within the timeout.
    /// captureUs is the moment the picture was created (QPC, microseconds).
    /// </summary>
    ID3D11Texture2D? Next(uint frameId, int timeoutMs, out long captureUs);
}

/// <summary>
/// Synthetic 120+ fps source rendered on the GPU: moving bars, a flickering parity square and a
/// changing tile field (so the encoder sees real motion). Measures the pipeline independently
/// of whether the desktop happens to be repainting.
/// </summary>
public sealed class TestPatternSource : ICaptureSource
{
    readonly GpuDevice gpu;
    readonly ID3D11Texture2D texture;
    readonly ID3D11RenderTargetView rtv;
    readonly ID3D11DeviceContext1 ctx1;

    public uint Width { get; }
    public uint Height { get; }
    public string Description => $"test pattern {Width}x{Height}";

    public TestPatternSource(GpuDevice gpu, uint width, uint height)
    {
        this.gpu = gpu;
        Width = width;
        Height = height;
        texture = gpu.CreateTexture(width, height, Format.B8G8R8A8_UNorm, BindFlags.RenderTarget | BindFlags.ShaderResource);
        rtv = gpu.Device.CreateRenderTargetView(texture);
        ctx1 = gpu.Context.QueryInterface<ID3D11DeviceContext1>();
    }

    static readonly Color4 Paper = new(0.96f, 0.94f, 0.88f, 1f);
    static readonly Color4 Ink = new(0.16f, 0.15f, 0.14f, 1f);
    static readonly Color4 Mark = new(1f, 0.89f, 0.43f, 1f);
    static readonly Color4 Blue = new(0.18f, 0.37f, 0.70f, 1f);

    public ID3D11Texture2D? Next(uint frameId, int timeoutMs, out long captureUs)
    {
        Draw(ctx1, rtv, (int)Width, (int)Height, frameId);
        gpu.Context.Flush();
        captureUs = Clock.NowUs;
        return texture;
    }

    /// <summary>Draws one test-pattern frame into any render target (also used by `hs-engine animate`).</summary>
    public static void Draw(ID3D11DeviceContext1 ctx1, ID3D11RenderTargetView rtv, int w, int h, uint frameId)
    {
        ctx1.ClearView(rtv, Paper, new[] { new RawRect(0, 0, w, h) });

        // Tile field: a diagonal wave of highlighted tiles, different every frame
        const int cols = 24, rows = 12;
        int tw = w / cols, th = h / rows;
        var tiles = new List<RawRect>();
        for (int y = 0; y < rows; y++)
            for (int x = 0; x < cols; x++)
                if (((x + y + (int)frameId / 2) % 7) == 0) tiles.Add(new RawRect(x * tw + 2, y * th + 2, (x + 1) * tw - 2, (y + 1) * th - 2));
        ctx1.ClearView(rtv, Mark, tiles.ToArray());

        // Moving bars: one frame = one step, so skipped frames are visible as jumps
        int barX = (int)(frameId * 12 % (uint)w);
        ctx1.ClearView(rtv, Ink, new[] { new RawRect(barX, 0, Math.Min(w, barX + 24), h) });
        int barY = (int)(frameId * 7 % (uint)h);
        ctx1.ClearView(rtv, Blue, new[] { new RawRect(0, barY, w, Math.Min(h, barY + 16)) });

        // Parity square + 16-bit frame counter as blocks (readable on a high-speed camera)
        ctx1.ClearView(rtv, (frameId & 1) == 0 ? Ink : Paper, new[] { new RawRect(16, 16, 96, 96) });
        var bits = new List<RawRect>();
        for (int b = 0; b < 16; b++)
            if (((frameId >> b) & 1) != 0) bits.Add(new RawRect(112 + b * 26, 40, 112 + b * 26 + 22, 72));
        if (bits.Count > 0) ctx1.ClearView(rtv, Ink, bits.ToArray());
    }

    public void Dispose()
    {
        ctx1.Dispose();
        rtv.Dispose();
        texture.Dispose();
    }
}

/// <summary>
/// Desktop capture with DXGI Desktop Duplication on the adapter that owns the output.
/// The duplicated surface is copied into our own texture before release (it is only valid
/// until ReleaseFrame). Capture time comes from DWM's present timestamp.
/// </summary>
public sealed class DesktopDuplicationSource : ICaptureSource
{
    readonly GpuDevice gpu;
    readonly IDXGIOutput1 output;
    IDXGIOutputDuplication? duplication;
    readonly ID3D11Texture2D copy;
    bool hasFrame;

    public uint Width { get; }
    public uint Height { get; }
    public string Description { get; }

    public DesktopDuplicationSource(GpuDevice gpu, IDXGIOutput1 output)
    {
        this.gpu = gpu;
        this.output = output;
        duplication = output.DuplicateOutput(gpu.Device);
        var mode = duplication.Description.ModeDescription;
        Width = mode.Width;
        Height = mode.Height;
        Description = $"desktop {output.Description.DeviceName} {Width}x{Height}@{(mode.RefreshRate.Denominator == 0 ? 0 : mode.RefreshRate.Numerator / mode.RefreshRate.Denominator)}Hz";
        copy = gpu.CreateTexture(Width, Height, Format.B8G8R8A8_UNorm, BindFlags.RenderTarget | BindFlags.ShaderResource);
    }

    public ID3D11Texture2D? Next(uint frameId, int timeoutMs, out long captureUs)
    {
        captureUs = Clock.NowUs;
        if (duplication == null)
        {
            try { duplication = output.DuplicateOutput(gpu.Device); }
            catch { Thread.Sleep(100); return null; }
        }
        var hr = duplication.AcquireNextFrame((uint)timeoutMs, out var info, out var resource);
        if (hr.Code == unchecked((int)0x887A0027)) return null; // DXGI_ERROR_WAIT_TIMEOUT
        if (hr.Failure)
        {
            // Access lost (mode change, secure desktop, UAC): recreate on the next call
            duplication.Dispose();
            duplication = null;
            return null;
        }
        try
        {
            if (info.LastPresentTime == 0 && hasFrame) return null; // pointer-only update
            using var tex = resource!.QueryInterface<ID3D11Texture2D>();
            gpu.Context.CopyResource(copy, tex);
            hasFrame = true;
            captureUs = info.LastPresentTime != 0 ? Clock.QpcToUs(info.LastPresentTime) : Clock.NowUs;
            return copy;
        }
        finally
        {
            resource?.Dispose();
            duplication.ReleaseFrame();
        }
    }

    public void Dispose()
    {
        duplication?.Dispose();
        copy.Dispose();
        output.Dispose();
    }
}
