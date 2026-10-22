using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Handshake.Engine.Common;
using Handshake.Engine.Host;
using Handshake.Engine.Net;

namespace Handshake.Engine.Client;

/// <summary>
/// Receiving side of "phát màn hình iPhone lên PC". The iOS broadcast extension finds us through
/// Bonjour (_hs-mirror._tcp), connects out and sends Hello + Config + Video, i.e. it plays the host.
/// We ping it every 100 ms to estimate its clock offset. When it reconnects (broadcast restarted,
/// Wi-Fi changed) the new connection simply replaces the old one.
/// </summary>
public sealed class MirrorLink : IVideoLink
{
    public const string ServiceType = "_hs-mirror._tcp";

    readonly TcpListener listener;
    readonly IDisposable? advert;
    readonly List<(long rtt, long offset)> samples = new();
    volatile MessageConnection? conn;
    volatile bool disposed;
    long offsetUs, rttUs;
    int reconnects;

    public event Action<MsgType, ReadOnlyMemory<byte>>? Message;

    public MirrorLink(int port)
    {
        listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        advert = Bonjour.Advertise(ServiceType, port);
        Log.Event("mirror-listening", new { port, bonjour = advert != null });
        new Thread(AcceptLoop) { IsBackground = true, Name = "hs-mirror-accept" }.Start();
        new Thread(PingLoop) { IsBackground = true, Name = "hs-ping", Priority = ThreadPriority.AboveNormal }.Start();
    }

    public bool ActiveIsLocal => false;
    public long ClockOffsetUs => Interlocked.Read(ref offsetUs);
    public long RttUs => Interlocked.Read(ref rttUs);
    public int Failovers => reconnects;

    void AcceptLoop()
    {
        while (!disposed)
        {
            Socket socket;
            try { socket = listener.AcceptSocket(); }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { break; }
            var c = new MessageConnection(socket);
            c.Message += (t, p) => OnMessage(c, t, p);
            c.Closed += reason => Log.Event("mirror-disconnected", new { remote = c.Remote, reason });
            var old = conn;
            lock (samples) samples.Clear();
            conn = c;
            if (old != null)
            {
                reconnects++;
                old.Close("replaced by a new connection");
            }
            c.Start();
            Log.Event("mirror-connected", new { remote = c.Remote });
        }
    }

    void OnMessage(MessageConnection c, MsgType type, ReadOnlyMemory<byte> payload)
    {
        if (c != conn) return;
        switch (type)
        {
            case MsgType.Pong:
            {
                long now = Clock.NowUs;
                long t0 = BinaryPrimitives.ReadInt64LittleEndian(payload.Span);
                long phoneUs = BinaryPrimitives.ReadInt64LittleEndian(payload.Span[8..]);
                lock (samples)
                {
                    samples.Add((now - t0, phoneUs - (t0 + now) / 2));
                    if (samples.Count > 20) samples.RemoveAt(0);
                    Interlocked.Exchange(ref offsetUs, samples.MinBy(s => s.rtt).offset); // least-queued sample
                    Interlocked.Exchange(ref rttUs, (long)samples.Average(s => s.rtt));
                }
                return;
            }
            case MsgType.Ping:
            {
                var pong = new byte[16];
                payload.Span[..8].CopyTo(pong);
                BinaryPrimitives.WriteInt64LittleEndian(pong.AsSpan(8), Clock.NowUs);
                c.SendControl(Protocol.Build(MsgType.Pong, pong));
                return;
            }
            case MsgType.Hello:
            {
                var h = HelloMsg.Parse(payload.Span);
                Log.Event("mirror-hello", new { name = h.Name, width = h.MaxWidth, height = h.MaxHeight, fps = h.Fps });
                return;
            }
        }
        Message?.Invoke(type, payload);
    }

    void PingLoop()
    {
        var buf = new byte[8];
        while (!disposed)
        {
            BinaryPrimitives.WriteInt64LittleEndian(buf, Clock.NowUs);
            conn?.SendControl(Protocol.Build(MsgType.Ping, buf));
            Thread.Sleep(100);
        }
    }

    public void SendActive(byte[] message) => conn?.SendControl(message);

    public void Dispose()
    {
        disposed = true;
        advert?.Dispose();
        listener.Stop();
        conn?.Close("receiver done");
    }
}
