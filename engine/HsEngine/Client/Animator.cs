using Handshake.Engine.Capture;
using Handshake.Engine.Common;
using Handshake.Engine.Gpu;
using Handshake.Engine.Host;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Handshake.Engine.Client;

/// <summary>
/// `hs-engine animate`: a window that redraws the test pattern on every vsync of its monitor.
/// It gives desktop capture a steady stream of real screen updates (144 Hz on this laptop)
/// that does not depend on the streaming loop itself.
/// </summary>
public static class Animator
{
    public static int Run(double seconds, int width, int height)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory2>();
        factory.EnumAdapters1(0, out var adapter).CheckError();
        using var gpu = new GpuDevice(adapter);
        using var win = new Win32Window("Handshake animate", width, height);
        var (w, h) = win.ClientSize;
        using var swap = factory.CreateSwapChainForHwnd(gpu.Device, win.Handle, new SwapChainDescription1
        {
            Width = (uint)w,
            Height = (uint)h,
            Format = Format.B8G8R8A8_UNorm,
            BufferCount = 2,
            BufferUsage = Usage.RenderTargetOutput,
            SampleDescription = new SampleDescription(1, 0),
            SwapEffect = SwapEffect.FlipDiscard,
        });
        using var ctx1 = gpu.Context.QueryInterface<ID3D11DeviceContext1>();
        long start = Clock.NowUs, statsAt = start + 1_000_000;
        uint frame = 0, perSecond = 0;
        while (!win.Closed && (seconds <= 0 || Clock.NowUs - start < seconds * 1_000_000))
        {
            win.Pump(0);
            using (var back = swap.GetBuffer<ID3D11Texture2D>(0))
            using (var rtv = gpu.Device.CreateRenderTargetView(back))
            {
                TestPatternSource.Draw(ctx1, rtv, w, h, frame++);
            }
            swap.Present(1, PresentFlags.None); // one new picture per refresh
            perSecond++;
            if (Clock.NowUs >= statsAt)
            {
                Log.Event("animate", new { fps = perSecond });
                perSecond = 0;
                statsAt += 1_000_000;
            }
        }
        return 0;
    }
}
