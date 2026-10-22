using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Handshake.Engine.Common;
using Handshake.Engine.Host;
using Handshake.Engine.Net;

namespace Handshake.Engine.Client;

/// <summary>
/// Keeps one connection per network path to the host (e.g. USB first, Wi-Fi second).
/// The first path that connects carries video; the others are hot standbys attached to the same
/// session. Every path is pinged every 100 ms; a path that is closed or silent for 400 ms is
/// dropped, the next one becomes active (Use + KeyRequest) and the dead one keeps reconnecting.
/// </summary>
public sealed class PathManager : IVideoLink
{
    public sealed class Path
    {
        public required string Endpoint;
        public required IPEndPoint Address;
        public MessageConnection? Conn;
        public volatile bool Alive;
        public bool Local;
        public long LastPongUs;
        public long RttUs;
        public long OffsetUs;
        public readonly List<(long rtt, long offset)> Samples = new();
    }

    const long PingIntervalUs = 100_000;
    const long DeadAfterUs = 400_000;

    readonly List<Path> paths;
    readonly Func<ulong, byte[]> hello;
    readonly HashSet<IPAddress> localAddresses;
    volatile Path? active;
    volatile bool disposed;
    public ulong SessionId;

    public event Action<MsgType, ReadOnlyMemory<byte>>? Message;
    public int Failovers { get; private set; }
    public long LastFailoverUs;

    public PathManager(IEnumerable<string> endpoints, int defaultPort, Func<ulong, byte[]> hello)
    {
        this.hello = hello;
        localAddresses = NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(u => u.Address)).ToHashSet();
        paths = endpoints.Select(e =>
        {
            var parts = e.Split(':');
            var ip = Dns.GetHostAddresses(parts[0]).First(a => a.AddressFamily == AddressFamily.InterNetwork);
            var ep = new IPEndPoint(ip, parts.Length > 1 ? int.Parse(parts[1]) : defaultPort);
            return new Path { Endpoint = ep.ToString(), Address = ep, Local = IPAddress.IsLoopback(ip) || localAddresses.Contains(ip) };
        }).ToList();
    }

    public Path? Active => active;
    public IReadOnlyList<Path> Paths => paths;
    public bool ActiveIsLocal => active?.Local ?? true;
    public long ClockOffsetUs => active?.OffsetUs ?? 0;
    public long RttUs => active?.RttUs ?? 0;

    /// <summary>Connects the first reachable path and starts a new session on it.</summary>
    public void ConnectFirst()
    {
        foreach (var p in paths)
        {
            if (TryConnect(p, 0))
            {
                active = p;
                break;
            }
        }
        if (active == null) throw new IOException("Không kết nối được tới host qua đường nào");
        new Thread(PingLoop) { IsBackground = true, Name = "hs-ping", Priority = ThreadPriority.AboveNormal }.Start();
    }

    /// <summary>After the session id is known: attach every other path as a standby.</summary>
    public void AttachStandbys(ulong sessionId)
    {
        SessionId = sessionId;
        foreach (var p in paths.Where(p => !p.Alive)) StartReconnect(p);
    }

    bool TryConnect(Path p, ulong sessionId)
    {
        try
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            if (!socket.ConnectAsync(p.Address).Wait(800))
            {
                socket.Dispose();
                return false;
            }
            var conn = new MessageConnection(socket);
            conn.Message += (t, payload) => OnMessage(p, t, payload);
            conn.Closed += reason => OnClosed(p, reason);
            p.Conn = conn;
            p.LastPongUs = Clock.NowUs;
            p.Alive = true;
            conn.Start();
            conn.SendControl(hello(sessionId));
            Log.Event("path-up", new { endpoint = p.Endpoint, standby = sessionId != 0 });
            return true;
        }
        catch (Exception ex) when (ex is SocketException or AggregateException)
        {
            return false;
        }
    }

    void OnMessage(Path p, MsgType type, ReadOnlyMemory<byte> payload)
    {
        if (type == MsgType.Pong)
        {
            long now = Clock.NowUs;
            long t0 = BinaryPrimitives.ReadInt64LittleEndian(payload.Span);
            long hostUs = BinaryPrimitives.ReadInt64LittleEndian(payload.Span[8..]);
            lock (p.Samples)
            {
                p.Samples.Add((now - t0, hostUs - (t0 + now) / 2));
                if (p.Samples.Count > 20) p.Samples.RemoveAt(0);
                p.OffsetUs = p.Samples.MinBy(s => s.rtt).offset; // least-queued sample
                p.RttUs = (long)p.Samples.Average(s => s.rtt);
            }
            p.LastPongUs = now;
            return;
        }
        // Video/config can only come from the active path; accept from any path anyway
        // (during a switch the host may already be sending on the new one)
        Message?.Invoke(type, payload);
    }

    void OnClosed(Path p, string reason)
    {
        if (!p.Alive) return;
        p.Alive = false;
        Log.Event("path-down", new { endpoint = p.Endpoint, reason });
        if (active == p) Failover(p);
        if (!disposed) StartReconnect(p);
    }

    void Failover(Path from)
    {
        var next = paths.FirstOrDefault(x => x != from && x.Alive);
        active = next;
        if (next == null) return;
        Failovers++;
        LastFailoverUs = Clock.NowUs;
        next.Conn!.SendControl(Protocol.Build(MsgType.Use, ReadOnlySpan<byte>.Empty));
        next.Conn.SendControl(Protocol.Build(MsgType.KeyRequest, ReadOnlySpan<byte>.Empty));
        Log.Event("failover", new { from = from.Endpoint, to = next.Endpoint });
    }

    void StartReconnect(Path p)
    {
        new Thread(() =>
        {
            while (!disposed && !p.Alive)
            {
                if (SessionId != 0 && TryConnect(p, SessionId))
                {
                    // Prefer paths in the order given (USB before Wi-Fi): take the video back
                    if (active == null || paths.IndexOf(p) < paths.IndexOf(active))
                    {
                        active = p;
                        p.Conn!.SendControl(Protocol.Build(MsgType.Use, ReadOnlySpan<byte>.Empty));
                        p.Conn.SendControl(Protocol.Build(MsgType.KeyRequest, ReadOnlySpan<byte>.Empty));
                        Log.Event("path-restored", new { endpoint = p.Endpoint });
                    }
                    return;
                }
                Thread.Sleep(500);
            }
        }) { IsBackground = true, Name = "hs-reconnect" }.Start();
    }

    void PingLoop()
    {
        var buf = new byte[8];
        while (!disposed)
        {
            long now = Clock.NowUs;
            foreach (var p in paths)
            {
                if (!p.Alive) continue;
                if (now - p.LastPongUs > DeadAfterUs)
                {
                    p.Conn?.Close("no pong for 400 ms");
                    continue;
                }
                BinaryPrimitives.WriteInt64LittleEndian(buf, now);
                p.Conn?.SendControl(Protocol.Build(MsgType.Ping, buf));
            }
            Thread.Sleep((int)(PingIntervalUs / 1000));
        }
    }

    public void SendActive(byte[] message) => active?.Conn?.SendControl(message);

    /// <summary>Test hook: abruptly closes a path, like pulling the cable.</summary>
    public void Drop(int index, string reason)
    {
        if (index < paths.Count) paths[index].Conn?.Close(reason);
    }

    public void Dispose()
    {
        disposed = true;
        foreach (var p in paths) p.Conn?.Close("client done");
    }
}
