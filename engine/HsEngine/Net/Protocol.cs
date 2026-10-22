using System.Buffers.Binary;

namespace Handshake.Engine.Net;

/// <summary>
/// Handshake stream protocol v1. Every message: [u32 length (type + payload)][u8 type][payload].
/// Little-endian. The same messages travel over TCP (USB / usbmux, LAN) and, fragmented, over UDP.
/// </summary>
public enum MsgType : byte
{
    Hello = 1,       // client -> host: u16 version, u16 maxW, u16 maxH, u16 fps, u32 codecs, u64 sessionId (0 = new), utf8 name
    Config = 2,      // host -> client: u8 codec, u16 w, u16 h, u16 fps, u32 kbps, u64 sessionId
    Video = 3,       // host -> client: VideoHeader + Annex-B access unit
    Ping = 4,        // client -> host: i64 clientUs
    Pong = 5,        // host -> client: i64 clientUs, i64 hostUs
    KeyRequest = 6,  // client -> host: (empty) decoder needs an IDR
    Input = 7,       // client -> host: InputEvent
    Stats = 8,       // client -> host: ClientStats (utf8 json)
    Bye = 9,
    Use = 10,        // client -> host: make the path this arrives on the active video path
}

public enum Codec : byte { H264 = 1, Hevc = 2, Av1 = 3 }

public static class Protocol
{
    public const ushort Version = 1;
    public const int HeaderSize = 5;
    public const int MaxMessage = 8 * 1024 * 1024;

    public static byte[] Build(MsgType type, ReadOnlySpan<byte> payload)
    {
        var buf = new byte[HeaderSize + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, (uint)(payload.Length + 1));
        buf[4] = (byte)type;
        payload.CopyTo(buf.AsSpan(HeaderSize));
        return buf;
    }
}

/// <summary>Fixed 32-byte header in front of every encoded frame.</summary>
public struct VideoHeader
{
    public const int Size = 32;
    public uint FrameId;
    public long CaptureUs;   // host QPC time the frame was captured / rendered
    public long SendUs;      // host QPC time the frame left the encoder
    public uint EncodeUs;    // encoder latency for this frame
    public byte Flags;       // bit0 = keyframe

    public readonly bool IsKey => (Flags & 1) != 0;

    public readonly void Write(Span<byte> s)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(s, FrameId);
        BinaryPrimitives.WriteInt64LittleEndian(s[4..], CaptureUs);
        BinaryPrimitives.WriteInt64LittleEndian(s[12..], SendUs);
        BinaryPrimitives.WriteUInt32LittleEndian(s[20..], EncodeUs);
        s[24] = Flags;
    }

    public static VideoHeader Read(ReadOnlySpan<byte> s) => new()
    {
        FrameId = BinaryPrimitives.ReadUInt32LittleEndian(s),
        CaptureUs = BinaryPrimitives.ReadInt64LittleEndian(s[4..]),
        SendUs = BinaryPrimitives.ReadInt64LittleEndian(s[12..]),
        EncodeUs = BinaryPrimitives.ReadUInt32LittleEndian(s[20..]),
        Flags = s[24],
    };
}

public struct HelloMsg
{
    public ushort Version, MaxWidth, MaxHeight, Fps;
    public uint Codecs;
    public ulong SessionId; // non-zero: attach this connection to a running session (extra path / reconnect)
    public string Name;

    public readonly byte[] ToBytes()
    {
        var name = System.Text.Encoding.UTF8.GetBytes(Name ?? "");
        var p = new byte[20 + name.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(p, Version);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(2), MaxWidth);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(4), MaxHeight);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(6), Fps);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(8), Codecs);
        BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(12), SessionId);
        name.CopyTo(p, 20);
        return Protocol.Build(MsgType.Hello, p);
    }

    public static HelloMsg Parse(ReadOnlySpan<byte> p) => new()
    {
        Version = BinaryPrimitives.ReadUInt16LittleEndian(p),
        MaxWidth = BinaryPrimitives.ReadUInt16LittleEndian(p[2..]),
        MaxHeight = BinaryPrimitives.ReadUInt16LittleEndian(p[4..]),
        Fps = BinaryPrimitives.ReadUInt16LittleEndian(p[6..]),
        Codecs = BinaryPrimitives.ReadUInt32LittleEndian(p[8..]),
        SessionId = BinaryPrimitives.ReadUInt64LittleEndian(p[12..]),
        Name = System.Text.Encoding.UTF8.GetString(p[20..]),
    };
}

public struct ConfigMsg
{
    public Codec Codec;
    public ushort Width, Height, Fps;
    public uint Kbps;
    public ulong SessionId;

    public readonly byte[] ToBytes()
    {
        var p = new byte[19];
        p[0] = (byte)Codec;
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(1), Width);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(3), Height);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(5), Fps);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(7), Kbps);
        BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(11), SessionId);
        return Protocol.Build(MsgType.Config, p);
    }

    public static ConfigMsg Parse(ReadOnlySpan<byte> p) => new()
    {
        Codec = (Codec)p[0],
        Width = BinaryPrimitives.ReadUInt16LittleEndian(p[1..]),
        Height = BinaryPrimitives.ReadUInt16LittleEndian(p[3..]),
        Fps = BinaryPrimitives.ReadUInt16LittleEndian(p[5..]),
        Kbps = BinaryPrimitives.ReadUInt32LittleEndian(p[7..]),
        SessionId = BinaryPrimitives.ReadUInt64LittleEndian(p[11..]),
    };
}

/// <summary>Pointer / touch event. Coordinates are normalised 0..65535 over the streamed display.</summary>
public struct InputEvent
{
    public const int Size = 12;
    public byte Kind;     // 1 = pointer down, 2 = move, 3 = up, 4 = wheel
    public byte Button;   // 0 left, 1 middle, 2 right
    public ushort X, Y;
    public short WheelDelta;
    public uint TimeMs;

    public readonly byte[] ToBytes()
    {
        var p = new byte[Size];
        p[0] = Kind;
        p[1] = Button;
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(2), X);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(4), Y);
        BinaryPrimitives.WriteInt16LittleEndian(p.AsSpan(6), WheelDelta);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(8), TimeMs);
        return Protocol.Build(MsgType.Input, p);
    }

    public static InputEvent Parse(ReadOnlySpan<byte> p) => new()
    {
        Kind = p[0],
        Button = p[1],
        X = BinaryPrimitives.ReadUInt16LittleEndian(p[2..]),
        Y = BinaryPrimitives.ReadUInt16LittleEndian(p[4..]),
        WheelDelta = BinaryPrimitives.ReadInt16LittleEndian(p[6..]),
        TimeMs = BinaryPrimitives.ReadUInt32LittleEndian(p[8..]),
    };
}
