using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Handshake.Engine.Codecs;
using Handshake.Engine.Common;
using Handshake.Engine.Gpu;
using Handshake.Engine.Host;
using Handshake.Engine.Net;
using Vortice.DXGI;
using Vortice.Direct3D11;

namespace Handshake.Engine.Client;

public sealed class ClientOptions
{
    public string[] Hosts = { "127.0.0.1" }; // one entry per network path, preferred first (USB, then Wi-Fi)
    public int Port = 7430;
    public double DropPrimaryAt;      // test: pull the first path after N seconds
    public double Seconds;            // 0 = run until closed
    public bool VSync;                // false = present immediately with tearing (lowest latency)
    public bool Input;                // forward mouse to the host
    public ushort MaxWidth = 0, MaxHeight = 0, Fps = 120;
    public bool Window = true;
    public string? Gpu;               // decode on this GPU (nvidia | amd)
    public int ListenPort;            // > 0: mirror mode, wait for an iPhone to push its screen here
}

/// <summary>
/// Windows test client standing in for the iPhone app: receives, decodes (DXVA), presents, and
/// measures per-frame latency from host capture to present, frame pacing and drops.
/// With <see cref="ClientOptions.ListenPort"/> it is the PC viewer for the iPhone's own screen.
/// </summary>
public sealed class TestClient
{
    readonly ClientOptions opt;
    readonly BlockingCollection<(VideoHeader h, byte[] data, int offset, long recvUs)> frames = new(new ConcurrentQueue<(VideoHeader, byte[], int, long)>());
    readonly ManualResetEventSlim configured = new();
    IVideoLink? net;
    ConfigMsg config;

    readonly Stats window = new(), total = new();

    public TestClient(ClientOptions opt) => this.opt = opt;

    public int Run()
    {
        bool mirror = opt.ListenPort > 0;
        using IVideoLink link = mirror ? new MirrorLink(opt.ListenPort) : new PathManager(opt.Hosts, opt.Port, sid => new HelloMsg
        {
            Version = Protocol.Version, MaxWidth = opt.MaxWidth, MaxHeight = opt.MaxHeight, Fps = opt.Fps,
            Codecs = 0b11, SessionId = sid, Name = Environment.MachineName + " test client",
        }.ToBytes());
        var paths = link as PathManager;
        net = link;
        link.Message += OnMessage;
        paths?.ConnectFirst();
        // Mirror mode waits for as long as it takes the user to start the broadcast on the iPhone
        if (!configured.Wait(mirror ? Timeout.Infinite : 10_000)) throw new TimeoutException("Host không gửi cấu hình");
        paths?.AttachStandbys(config.SessionId);
        Log.Event("client-config", new { codec = config.Codec.ToString(), width = config.Width, height = config.Height, fps = config.Fps, kbps = config.Kbps, session = config.SessionId, paths = mirror ? new[] { $"listen:{opt.ListenPort}" } : opt.Hosts });
        bool dropped = false;

        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory2>();
        using var gpu = new GpuDevice(GpuDevice.FindAdapter(opt.Gpu));
        using var decoder = new MfDecoder(gpu, config.Codec, config.Width, config.Height, config.Fps);
        // Headless mode decodes into an offscreen texture: used when the streamed display is the
        // one this window would sit on (otherwise capture films its own output).
        using var win = opt.Window ? new Win32Window(mirror ? $"Handshake – màn hình iPhone {config.Width}x{config.Height}" : $"Handshake test client – {config.Width}x{config.Height}@{config.Fps}", config.Width, config.Height) : null;
        using var offscreen = opt.Window ? null : gpu.CreateTexture(config.Width, config.Height, Format.B8G8R8A8_UNorm, BindFlags.RenderTarget);
        using var gpuDone = gpu.Device.CreateQuery(new QueryDescription(QueryType.Event));

        bool tearing = win != null && !opt.VSync && SupportsTearing(factory);
        var (cw, ch) = win?.ClientSize ?? ((int)config.Width, (int)config.Height);
        var swapDesc = new SwapChainDescription1
        {
            Width = (uint)cw,
            Height = (uint)ch,
            Format = Format.B8G8R8A8_UNorm,
            BufferCount = 2,
            BufferUsage = Usage.RenderTargetOutput,
            SampleDescription = new SampleDescription(1, 0),
            SwapEffect = SwapEffect.FlipDiscard,
            Scaling = Scaling.Stretch,
            AlphaMode = AlphaMode.Ignore,
            Flags = tearing ? SwapChainFlags.AllowTearing : SwapChainFlags.None,
        };
        using var swap = win == null ? null : factory.CreateSwapChainForHwnd(gpu.Device, win.Handle, swapDesc);
        if (win != null) factory.MakeWindowAssociation(win.Handle, WindowAssociationFlags.IgnoreAltEnter);
        Log.Event("client-render", new { gpu = gpu.Name, decoder = decoder.Name, tearing, vsync = opt.VSync, headless = win == null });

        VideoConverter? converter = null;
        bool resize = swap != null;
        Vortice.RawRect dest = default;
        bool waitingKey = false;
        long started = Clock.NowUs, statsAt = started + 1_000_000;
        long sampleTime = 0;

        if (win != null)
        {
            win.Message += (msg, w, l) =>
            {
                if (msg == Win32Window.WM_SIZE) resize = true;
                if (opt.Input && !mirror) ForwardInput(msg, w, l, dest);
            };
        }

        // Decode + present loop on this thread; window messages are pumped between frames
        while (!(win?.Closed ?? false))
        {
            if (opt.Seconds > 0 && Clock.NowUs - started > opt.Seconds * 1_000_000) break;
            if (paths != null && !dropped && opt.DropPrimaryAt > 0 && Clock.NowUs - started > opt.DropPrimaryAt * 1_000_000)
            {
                dropped = true;
                Log.Event("simulate-unplug", new { endpoint = paths.Paths[0].Endpoint });
                paths.Drop(0, "simulated cable unplug");
            }
            win?.Pump(0);
            if (!frames.TryTake(out var item, 2)) goto stats;

            // Too far behind (decoder/GPU stall): skip to the next keyframe instead of lagging
            if (frames.Count > 3 && !waitingKey)
            {
                waitingKey = true;
                link.SendActive(Protocol.Build(MsgType.KeyRequest, ReadOnlySpan<byte>.Empty));
                window.SkippedBehind += frames.Count;
            }
            if (waitingKey)
            {
                if (!item.h.IsKey) { window.Skipped++; goto stats; }
                waitingKey = false;
            }

            if (resize)
            {
                (cw, ch) = win!.ClientSize;
                converter?.Dispose();
                converter = null;
                swap!.ResizeBuffers(2, (uint)cw, (uint)ch, Format.B8G8R8A8_UNorm, swapDesc.Flags).CheckError();
                resize = false;
            }

            long decodeStart = Clock.NowUs;
            List<DecodedFrame> pics;
            try { pics = decoder.Decode(item.data.AsSpan(item.offset), sampleTime++); }
            catch (Exception ex)
            {
                window.DecodeErrors++;
                waitingKey = true;
                link.SendActive(Protocol.Build(MsgType.KeyRequest, ReadOnlySpan<byte>.Empty));
                Log.Event("decode-error", new { message = ex.Message });
                goto stats;
            }
            long decodeUs = Clock.NowUs - decodeStart;

            foreach (var pic in pics)
            {
                using var back = swap?.GetBuffer<ID3D11Texture2D>(0);
                var target = back ?? offscreen!;
                if (converter == null)
                {
                    converter = new VideoConverter(gpu, config.Width, config.Height, (uint)cw, (uint)ch, config.Fps, inputIsYuv: true);
                    double s = Math.Min((double)cw / config.Width, (double)ch / config.Height);
                    int dw = (int)(config.Width * s), dh = (int)(config.Height * s);
                    dest = new Vortice.RawRect((cw - dw) / 2, (ch - dh) / 2, (cw - dw) / 2 + dw, (ch - dh) / 2 + dh);
                    converter.SetDestination(dest);
                }
                converter.Convert(pic.Texture, pic.ArraySlice, target);
                gpu.Context.End(gpuDone);
                var hr = swap?.Present(opt.VSync ? 1u : 0u, tearing ? PresentFlags.AllowTearing : PresentFlags.None) ?? default;
                gpu.Context.Flush();
                // Latency ends when the GPU has really finished the picture, not when Present returns
                while (!gpu.Context.IsDataAvailable(gpuDone)) Thread.Yield();
                long presentUs = Clock.NowUs;
                pic.Texture.Dispose();
                pic.Sample.Dispose();
                converter.ResetViews(); // decoder may hand out a different surface next time

                long offset = link.ActiveIsLocal ? 0 : link.ClockOffsetUs; // same machine: QPC is shared
                long latency = presentUs + offset - item.h.CaptureUs;
                long network = item.recvUs + offset - item.h.SendUs;
                window.Add(latency, decodeUs, network, item.h.EncodeUs, presentUs, item.data.Length - item.offset, hr.Code == 0x087A0001);
                total.Add(latency, decodeUs, network, item.h.EncodeUs, presentUs, item.data.Length - item.offset, false);
            }

        stats:
            if (Clock.NowUs >= statsAt)
            {
                var s = window.Snapshot(link.RttUs, 1, link.Failovers);
                Log.Event("client-stats", s);
                win?.SetTitle($"Handshake test – {s.fps} fps · trễ p50 {s.latencyP50Ms} ms · p95 {s.latencyP95Ms} ms · {s.mbps} Mbit/s");
                window.Reset();
                statsAt += 1_000_000;
            }
        }

        converter?.Dispose();
        var summary = total.Snapshot(link.RttUs, (Clock.NowUs - started) / 1e6, link.Failovers);
        Log.Event("client-summary", summary);
        return 0;
    }

    void OnMessage(MsgType type, ReadOnlyMemory<byte> payload)
    {
        switch (type)
        {
            case MsgType.Config:
                config = ConfigMsg.Parse(payload.Span);
                configured.Set();
                break;
            case MsgType.Video:
            {
                long now = Clock.NowUs;
                var arr = payload.ToArray(); // payload is a fresh array per message
                var h = VideoHeader.Read(arr);
                if (!frames.IsAddingCompleted) frames.Add((h, arr, VideoHeader.Size, now));
                break;
            }
        }
    }

    bool mouseDown;

    void ForwardInput(uint msg, IntPtr w, IntPtr l, Vortice.RawRect dest)
    {
        if (net == null || dest.Right <= dest.Left) return;
        int x = (short)(l.ToInt64() & 0xFFFF), y = (short)((l.ToInt64() >> 16) & 0xFFFF);
        ushort nx = (ushort)Math.Clamp((x - dest.Left) * 65535L / Math.Max(1, dest.Right - dest.Left), 0, 65535);
        ushort ny = (ushort)Math.Clamp((y - dest.Top) * 65535L / Math.Max(1, dest.Bottom - dest.Top), 0, 65535);
        InputEvent? e = msg switch
        {
            Win32Window.WM_LBUTTONDOWN => new InputEvent { Kind = 1, X = nx, Y = ny },
            Win32Window.WM_LBUTTONUP => new InputEvent { Kind = 3, X = nx, Y = ny },
            Win32Window.WM_RBUTTONDOWN => new InputEvent { Kind = 1, Button = 2, X = nx, Y = ny },
            Win32Window.WM_RBUTTONUP => new InputEvent { Kind = 3, Button = 2, X = nx, Y = ny },
            Win32Window.WM_MOUSEMOVE when mouseDown => new InputEvent { Kind = 2, X = nx, Y = ny },
            Win32Window.WM_MOUSEWHEEL => new InputEvent { Kind = 4, WheelDelta = (short)((w.ToInt64() >> 16) & 0xFFFF) },
            _ => null,
        };
        if (msg == Win32Window.WM_LBUTTONDOWN) mouseDown = true;
        if (msg == Win32Window.WM_LBUTTONUP) mouseDown = false;
        if (e.HasValue) net.SendActive(e.Value.ToBytes());
    }

    static bool SupportsTearing(IDXGIFactory2 factory)
    {
        try
        {
            using var f5 = factory.QueryInterface<IDXGIFactory5>();
            return f5.PresentAllowTearing;
        }
        catch { return false; }
    }

    sealed class Stats
    {
        readonly List<long> latencies = new();
        long decodeSum, netSum, encSum, bytes, lastPresent;
        readonly List<long> gaps = new();
        public int Skipped, SkippedBehind, DecodeErrors, Occluded;

        public void Add(long latencyUs, long decodeUs, long netUs, long encUs, long presentUs, int size, bool occluded)
        {
            latencies.Add(latencyUs);
            decodeSum += decodeUs;
            netSum += netUs;
            encSum += encUs;
            bytes += size;
            if (lastPresent != 0) gaps.Add(presentUs - lastPresent);
            lastPresent = presentUs;
            if (occluded) Occluded++;
        }

        public void Reset()
        {
            latencies.Clear();
            gaps.Clear();
            decodeSum = netSum = encSum = bytes = 0;
            Skipped = SkippedBehind = DecodeErrors = Occluded = 0;
        }

        static double Ms(double us) => Math.Round(us / 1000, 2);

        static double Pct(List<long> sorted, double p) => sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)(p * sorted.Count))];

        public dynamic Snapshot(long rttUs, double seconds, int failovers)
        {
            var sorted = latencies.OrderBy(x => x).ToList();
            var gapSorted = gaps.OrderBy(x => x).ToList();
            int n = Math.Max(1, latencies.Count);
            return new
            {
                fps = (int)Math.Round(latencies.Count / seconds),
                frames = latencies.Count,
                latencyAvgMs = Ms(latencies.Count == 0 ? 0 : latencies.Average()),
                latencyP50Ms = Ms(Pct(sorted, 0.50)),
                latencyP95Ms = Ms(Pct(sorted, 0.95)),
                latencyP99Ms = Ms(Pct(sorted, 0.99)),
                latencyMaxMs = Ms(sorted.Count == 0 ? 0 : sorted[^1]),
                encodeMs = Ms(encSum / (double)n),
                networkMs = Ms(netSum / (double)n),
                decodeMs = Ms(decodeSum / (double)n),
                frameGapP99Ms = Ms(Pct(gapSorted, 0.99)),
                frameGapMaxMs = Ms(gapSorted.Count == 0 ? 0 : gapSorted[^1]),
                failovers,
                mbps = Math.Round(bytes * 8 / 1e6 / seconds, 1),
                rttMs = Ms(rttUs),
                skipped = Skipped + SkippedBehind,
                decodeErrors = DecodeErrors,
                occluded = Occluded,
            };
        }
    }
}
