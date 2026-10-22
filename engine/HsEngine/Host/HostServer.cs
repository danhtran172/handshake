using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Handshake.Engine.Capture;
using Handshake.Engine.Codecs;
using Handshake.Engine.Common;
using Handshake.Engine.Gpu;
using Handshake.Engine.Net;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Handshake.Engine.Host;

public sealed class HostOptions
{
    public int Port = 7430;
    public string Source = "test";   // test | desktop
    public int Output = 0;           // desktop output index
    public uint Width, Height;       // stream size (0 = source size)
    public uint Fps = 120;
    public uint Kbps = 40_000;
    public Codec Codec = Codec.H264;
    public uint MinFps = 10;         // desktop: repeat the last frame at least this often
    public int MaxPendingFrames = 2; // congestion threshold
    public string? Gpu;              // test source: render + encode on this GPU (nvidia | amd)
}

/// <summary>
/// Streaming host. One session at a time; a session can have several network paths (e.g. USB
/// and Wi-Fi). Video goes over the active path only; if it dies, the next path takes over with
/// an immediate keyframe, and the encoder keeps running so a reconnect resumes within a frame.
/// Pipeline: capture (GPU) -> VideoProcessor BGRA->NV12 (+scale) -> hardware encoder -> active path.
/// </summary>
public sealed class HostServer
{
    readonly HostOptions opt;
    readonly object gate = new();
    Session? current;

    public const string ServiceType = "_handshake._tcp";

    public HostServer(HostOptions opt) => this.opt = opt;

    public void Run(CancellationToken token)
    {
        var listener = new TcpListener(IPAddress.Any, opt.Port);
        listener.Start();
        using var advert = Bonjour.Advertise(ServiceType, opt.Port); // the iOS app finds the PC by this
        Log.Event("listening", new { port = opt.Port, source = opt.Source, fps = opt.Fps, kbps = opt.Kbps, codec = opt.Codec.ToString(), bonjour = advert != null });
        token.Register(() => listener.Stop());
        while (!token.IsCancellationRequested)
        {
            Socket socket;
            try { socket = listener.AcceptSocket(); }
            catch (SocketException) { break; }
            var conn = new MessageConnection(socket);
            new Thread(() => Handshake(conn)) { IsBackground = true, Name = "hs-handshake" }.Start();
        }
        lock (gate) current?.Stop("shutdown");
    }

    /// <summary>Waits for Hello, then either attaches the connection to the running session or starts a new one.</summary>
    void Handshake(MessageConnection conn)
    {
        HelloMsg hello = default;
        using var got = new ManualResetEventSlim();
        void OnHello(MsgType t, ReadOnlyMemory<byte> p)
        {
            if (t != MsgType.Hello || got.IsSet) return;
            hello = HelloMsg.Parse(p.Span);
            got.Set();
        }
        conn.Message += OnHello;
        conn.Start();
        if (!got.Wait(5000))
        {
            conn.Close("no hello");
            return;
        }
        conn.Message -= OnHello;
        lock (gate)
        {
            if (hello.SessionId != 0 && current is { Stopped: false } s && s.Id == hello.SessionId)
            {
                s.Attach(conn, hello);
                return;
            }
            current?.Stop("replaced");
            current = new Session(opt, (ulong)Random.Shared.NextInt64(1, long.MaxValue), hello);
            current.Attach(conn, hello);
            var session = current;
            new Thread(() => session.Run()) { IsBackground = true, Name = "hs-session" }.Start();
        }
    }

    sealed class Session
    {
        readonly HostOptions opt;
        readonly HelloMsg hello;
        readonly List<MessageConnection> paths = new();
        volatile MessageConnection? active;
        volatile bool stopped;
        byte[]? configMsg;
        MfEncoder? encoder;
        long noPathSinceUs;

        // statistics (reset every second)
        int sent, busyDrops, congestionDrops, noPathDrops, keyRequests;
        long encodeUsSum, bytesSent;

        public ulong Id { get; }
        public bool Stopped => stopped;

        public Session(HostOptions opt, ulong id, HelloMsg hello)
        {
            this.opt = opt;
            this.hello = hello;
            Id = id;
        }

        public void Attach(MessageConnection conn, HelloMsg h)
        {
            conn.Message += (t, p) => OnMessage(conn, t, p);
            conn.Closed += reason => Detach(conn, reason);
            lock (paths)
            {
                paths.Add(conn);
                active ??= conn;
            }
            Log.Event("path-attached", new { remote = conn.Remote, name = h.Name, paths = paths.Count, active = active == conn });
            if (configMsg != null)
            {
                conn.SendControl(configMsg);
                if (active == conn) encoder?.RequestKeyFrame();
            }
        }

        void Detach(MessageConnection conn, string reason)
        {
            MessageConnection? next;
            lock (paths)
            {
                paths.Remove(conn);
                if (active != conn) next = active;
                else
                {
                    // Fail over right away to the next path, without waiting for the client
                    next = paths.FirstOrDefault();
                    active = next;
                    if (next != null) encoder?.RequestKeyFrame();
                }
            }
            if (next == null) noPathSinceUs = Clock.NowUs;
            Log.Event("path-lost", new { remote = conn.Remote, reason, paths = paths.Count, failover = next?.Remote });
        }

        public void Stop(string reason)
        {
            if (stopped) return;
            stopped = true;
            Log.Event("session-end", new { reason });
            lock (paths) foreach (var p in paths.ToArray()) p.Close(reason);
        }

        void OnMessage(MessageConnection conn, MsgType type, ReadOnlyMemory<byte> payload)
        {
            switch (type)
            {
                case MsgType.Ping:
                    var pong = new byte[16];
                    payload.Span[..8].CopyTo(pong);
                    BinaryPrimitives.WriteInt64LittleEndian(pong.AsSpan(8), Clock.NowUs);
                    conn.SendControl(Protocol.Build(MsgType.Pong, pong)); // answer on the path it came from
                    break;
                case MsgType.Use:
                    if (active != conn)
                    {
                        active = conn;
                        encoder?.RequestKeyFrame();
                        Log.Event("path-active", new { remote = conn.Remote });
                    }
                    break;
                case MsgType.KeyRequest:
                    Interlocked.Increment(ref keyRequests);
                    encoder?.RequestKeyFrame();
                    break;
                case MsgType.Stats:
                    Console.Out.WriteLine(System.Text.Encoding.UTF8.GetString(payload.Span));
                    break;
                case MsgType.Input:
                    InputInjector.Apply(InputEvent.Parse(payload.Span), opt.Output);
                    break;
            }
        }

        public void Run()
        {
            try { Stream(); }
            catch (Exception ex)
            {
                Log.Event("error", new { message = ex.Message, where = ex.TargetSite?.Name, stack = ex.StackTrace?.Split((char)10).Take(6) });
                Stop("error");
            }
        }

        void Stream()
        {
            // Desktop capture must run on the GPU that owns the output; the test source can use any GPU
            var (adapter, output) = GpuDevice.FindOutput(opt.Output);
            if (opt.Source != "desktop" && opt.Gpu is { } want && want != "auto")
            {
                adapter.Dispose();
                adapter = GpuDevice.FindAdapter(want);
            }
            using var gpu = new GpuDevice(adapter);
            using ICaptureSource source = opt.Source == "desktop"
                ? new DesktopDuplicationSource(gpu, output ?? throw new InvalidOperationException("Không thấy màn hình"))
                : new TestPatternSource(gpu, opt.Width == 0 ? 1920 : opt.Width, opt.Height == 0 ? 1080 : opt.Height);
            if (opt.Source != "desktop") output?.Dispose();

            uint fps = Math.Min(opt.Fps, hello.Fps == 0 ? opt.Fps : hello.Fps);
            uint w = opt.Width == 0 ? source.Width : opt.Width;
            uint h = opt.Height == 0 ? source.Height : opt.Height;
            if (hello.MaxWidth > 0 && (w > hello.MaxWidth || h > hello.MaxHeight))
            {
                double s = Math.Min((double)hello.MaxWidth / w, (double)hello.MaxHeight / h);
                w = (uint)(w * s);
                h = (uint)(h * s);
            }
            w &= ~1u; // encoders need even sizes
            h &= ~1u;

            using var converter = new VideoConverter(gpu, source.Width, source.Height, w, h, fps, inputIsYuv: false);
            encoder = new MfEncoder(gpu, opt.Codec, w, h, fps, opt.Kbps);
            encoder.FrameEncoded += OnEncoded;
            var nv12 = Enumerable.Range(0, 4)
                .Select(_ => gpu.CreateTexture(w, h, Format.NV12, BindFlags.RenderTarget | BindFlags.ShaderResource))
                .ToArray();

            Log.Event("stream", new { gpu = gpu.Name, encoder = encoder.Name, source = source.Description, width = w, height = h, fps, kbps = opt.Kbps, settings = string.Join(", ", encoder.Notes) });
            configMsg = new ConfigMsg { Codec = opt.Codec, Width = (ushort)w, Height = (ushort)h, Fps = (ushort)fps, Kbps = opt.Kbps, SessionId = Id }.ToBytes();
            lock (paths) foreach (var p in paths) p.SendControl(configMsg);

            using var pacer = new FramePacer(fps);
            uint frameId = 0;
            long lastEncodeUs = 0;
            long minIntervalUs = 1_000_000 / Math.Max(1, opt.MinFps);
            long statsAt = Clock.NowUs + 1_000_000;
            bool desktop = opt.Source == "desktop";
            bool haveFrame = false;
            bool pending = false;     // desktop: newest capture converted, waiting for its encode slot
            long pendingUs = 0;
            long minGapUs = pacer.IntervalUs * 9 / 10;

            while (!stopped)
            {
                long captureUs;
                var target = nv12[frameId % nv12.Length];
                if (desktop)
                {
                    // Wait for a desktop update, but never past the moment the pending frame is due
                    long untilDue = pending ? Math.Max(0, minGapUs - (Clock.NowUs - lastEncodeUs)) : pacer.IntervalUs;
                    var frame = source.Next(frameId, (int)(untilDue / 1000), out var updateUs);
                    if (frame != null)
                    {
                        // Newest picture wins: overwrite the slot, keep its timestamp
                        converter.Convert(frame, 0, target);
                        pending = true;
                        pendingUs = updateUs;
                        haveFrame = true;
                    }
                    long now = Clock.NowUs;
                    if (pending && now - lastEncodeUs >= minGapUs)
                    {
                        captureUs = pendingUs;
                    }
                    else if (!pending && haveFrame && now - lastEncodeUs >= minIntervalUs)
                    {
                        // Nothing changed for a while: re-send the last picture (keeps the link warm)
                        gpu.Context.CopyResource(target, nv12[(frameId + nv12.Length - 1) % nv12.Length]);
                        captureUs = now;
                    }
                    else goto stats;
                }
                else
                {
                    pacer.Wait();
                    var frame = source.Next(frameId, 0, out captureUs)!;
                    converter.Convert(frame, 0, target);
                }

                var path = active;
                if (path == null)
                {
                    // Every path is down: keep the encoder alive for a reconnect, give up after 10 s
                    noPathDrops++;
                    if (Clock.NowUs - noPathSinceUs > 10_000_000) Stop("no path");
                    goto stats;
                }
                if (path.PendingVideo >= opt.MaxPendingFrames)
                {
                    congestionDrops++; // link is behind: skip this capture instead of queueing latency
                    goto stats;
                }

                if (encoder.Encode(target, frameId, captureUs))
                {
                    lastEncodeUs = Clock.NowUs;
                    frameId++;
                    pending = false;
                }
                else busyDrops++;

            stats:
                if (Clock.NowUs >= statsAt)
                {
                    ReportStats();
                    statsAt += 1_000_000;
                }
            }
            encoder.Dispose();
            foreach (var t in nv12) t.Dispose();
        }

        void OnEncoded(EncodedFrame f)
        {
            var msg = new byte[Protocol.HeaderSize + VideoHeader.Size + f.Data.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(msg, (uint)(1 + VideoHeader.Size + f.Data.Length));
            msg[4] = (byte)MsgType.Video;
            new VideoHeader
            {
                FrameId = f.FrameId,
                CaptureUs = f.CaptureUs,
                SendUs = Clock.NowUs,
                EncodeUs = (uint)Math.Max(0, f.EncodedUs - f.CaptureUs),
                Flags = (byte)(f.Key ? 1 : 0),
            }.Write(msg.AsSpan(Protocol.HeaderSize));
            f.Data.CopyTo(msg, Protocol.HeaderSize + VideoHeader.Size);
            var path = active;
            if (path == null) return;
            path.SendVideo(msg);
            Interlocked.Increment(ref sent);
            Interlocked.Add(ref encodeUsSum, f.EncodedUs - f.CaptureUs);
            Interlocked.Add(ref bytesSent, msg.Length);
        }

        void ReportStats()
        {
            int n = Interlocked.Exchange(ref sent, 0);
            long enc = Interlocked.Exchange(ref encodeUsSum, 0);
            long bytes = Interlocked.Exchange(ref bytesSent, 0);
            Log.Event("host-stats", new
            {
                fps = n,
                kbps = bytes * 8 / 1000,
                captureToEncodedMs = n > 0 ? Math.Round(enc / (double)n / 1000, 2) : 0,
                busyDrops = Interlocked.Exchange(ref busyDrops, 0),
                congestionDrops = Interlocked.Exchange(ref congestionDrops, 0),
                noPathDrops = Interlocked.Exchange(ref noPathDrops, 0),
                keyRequests = Interlocked.Exchange(ref keyRequests, 0),
                pending = active?.PendingVideo ?? -1,
                paths = paths.Count,
            });
        }
    }
}

/// <summary>JSON-lines event log on stdout (the Electron app parses these).</summary>
public static class Log
{
    static readonly object Gate = new();
    static readonly JsonSerializerOptions Json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static void Event(string evt, object data)
    {
        var json = JsonSerializer.Serialize(data, Json);
        lock (Gate) Console.Out.WriteLine($"{{\"event\":\"{evt}\",\"t\":{Clock.NowUs / 1000},{json[1..]}");
    }
}
