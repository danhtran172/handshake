using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Sockets;

namespace Handshake.Engine.Net;

/// <summary>
/// Framed message stream over a TCP socket (LAN, or USB via usbmux later).
/// Two send queues: control messages always go first, so a Pong never waits behind a keyframe.
/// <see cref="PendingVideo"/> lets the host see congestion and skip capturing instead of
/// letting latency build up in buffers.
/// </summary>
public sealed class MessageConnection : IDisposable
{
    readonly Socket socket;
    readonly ConcurrentQueue<byte[]> control = new();
    readonly ConcurrentQueue<byte[]> video = new();
    readonly SemaphoreSlim signal = new(0);
    readonly CancellationTokenSource cts = new();
    int pendingVideo;
    volatile bool closed;

    public event Action<MsgType, ReadOnlyMemory<byte>>? Message;
    public event Action<string>? Closed;

    public string Remote { get; }
    public long BytesSent;
    public long BytesReceived;
    public int PendingVideo => Volatile.Read(ref pendingVideo);

    public MessageConnection(Socket socket)
    {
        this.socket = socket;
        socket.NoDelay = true;
        // Small kernel buffer: congestion shows up in our queue, where we can react to it
        socket.SendBufferSize = 256 * 1024;
        socket.ReceiveBufferSize = 4 * 1024 * 1024;
        Remote = socket.RemoteEndPoint?.ToString() ?? "?";
    }

    public void Start()
    {
        new Thread(SendLoop) { IsBackground = true, Name = "hs-send", Priority = ThreadPriority.AboveNormal }.Start();
        new Thread(ReceiveLoop) { IsBackground = true, Name = "hs-recv", Priority = ThreadPriority.AboveNormal }.Start();
    }

    public void SendControl(byte[] message)
    {
        if (closed) return;
        control.Enqueue(message);
        signal.Release();
    }

    public void SendVideo(byte[] message)
    {
        if (closed) return;
        Interlocked.Increment(ref pendingVideo);
        video.Enqueue(message);
        signal.Release();
    }

    void SendLoop()
    {
        try
        {
            while (!closed)
            {
                signal.Wait(cts.Token);
                if (control.TryDequeue(out var c)) Send(c);
                else if (video.TryDequeue(out var v))
                {
                    Send(v);
                    Interlocked.Decrement(ref pendingVideo);
                }
            }
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException)
        {
            Close(ex.Message);
        }
    }

    void Send(byte[] data)
    {
        int off = 0;
        while (off < data.Length) off += socket.Send(data, off, data.Length - off, SocketFlags.None);
        Interlocked.Add(ref BytesSent, data.Length);
    }

    void ReceiveLoop()
    {
        var header = new byte[Protocol.HeaderSize];
        try
        {
            while (!closed)
            {
                ReadExact(header);
                uint len = BinaryPrimitives.ReadUInt32LittleEndian(header);
                if (len < 1 || len > Protocol.MaxMessage) throw new InvalidDataException($"bad length {len}");
                var payload = new byte[len - 1];
                ReadExact(payload);
                Interlocked.Add(ref BytesReceived, len + 4);
                Message?.Invoke((MsgType)header[4], payload);
            }
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or EndOfStreamException or InvalidDataException)
        {
            Close(ex.Message);
        }
    }

    void ReadExact(byte[] buf)
    {
        int off = 0;
        while (off < buf.Length)
        {
            int n = socket.Receive(buf, off, buf.Length - off, SocketFlags.None);
            if (n == 0) throw new EndOfStreamException("closed by peer");
            off += n;
        }
    }

    public void Close(string reason)
    {
        if (closed) return;
        closed = true;
        cts.Cancel();
        try { socket.Shutdown(SocketShutdown.Both); } catch { }
        socket.Dispose();
        Closed?.Invoke(reason);
    }

    public void Dispose() => Close("disposed");
}
