import CoreMedia
import Foundation
import QuartzCore

/// Handshake stream protocol v1, same as engine/HsEngine/Net/Protocol.cs.
/// Every message: [u32 length (type + payload)][u8 type][payload], little-endian.
enum MsgType: UInt8 {
    case hello = 1, config = 2, video = 3, ping = 4, pong = 5, keyRequest = 6, input = 7, stats = 8, bye = 9, use = 10
}

enum Codec: UInt8 {
    case h264 = 1, hevc = 2, av1 = 3
}

enum Wire {
    static let version: UInt16 = 1
    static let headerSize = 5
    static let maxMessage = 8 * 1024 * 1024
    static let hostPort: UInt16 = 7430     // hs-engine host: PC screen to the iPhone
    static let mirrorPort: UInt16 = 7432   // hs-engine mirror: iPhone screen to the PC
    static let hostService = "_handshake._tcp"
    static let mirrorService = "_hs-mirror._tcp"
}

/// Microseconds on the host-time clock (mach_absolute_time). ReplayKit sample timestamps use the
/// same clock, so capture times can be sent as they are.
enum Clock {
    static var nowUs: Int64 { Int64(CACurrentMediaTime() * 1_000_000) }

    static func us(_ time: CMTime) -> Int64 {
        time.isValid ? Int64(CMTimeGetSeconds(time) * 1_000_000) : nowUs
    }
}

struct ByteWriter {
    private(set) var data = Data()

    init(capacity: Int = 64) { data.reserveCapacity(capacity) }

    mutating func u8(_ v: UInt8) { data.append(v) }
    mutating func u16(_ v: UInt16) { put(v) }
    mutating func i16(_ v: Int16) { put(v) }
    mutating func u32(_ v: UInt32) { put(v) }
    mutating func u64(_ v: UInt64) { put(v) }
    mutating func i64(_ v: Int64) { put(v) }
    mutating func bytes(_ d: Data) { data.append(d) }

    private mutating func put<T: FixedWidthInteger>(_ v: T) {
        withUnsafeBytes(of: v.littleEndian) { data.append(contentsOf: $0) }
    }
}

struct ByteReader {
    let data: Data
    private(set) var offset = 0

    init(_ data: Data) { self.data = data }

    var remaining: Int { data.count - offset }

    mutating func u8() -> UInt8 {
        defer { offset += 1 }
        return data[data.startIndex + offset]
    }

    mutating func u16() -> UInt16 { get() }
    mutating func u32() -> UInt32 { get() }
    mutating func u64() -> UInt64 { get() }
    mutating func i64() -> Int64 { get() }

    private mutating func get<T: FixedWidthInteger>() -> T {
        var v: T = 0
        let size = MemoryLayout<T>.size
        for i in 0..<size {
            v |= T(truncatingIfNeeded: data[data.startIndex + offset + i]) << (8 * i)
        }
        offset += size
        return v
    }
}

/// Fixed 32-byte header in front of every encoded frame.
struct VideoHeader {
    static let size = 32
    var frameId: UInt32 = 0
    var captureUs: Int64 = 0   // sender clock: when the frame was captured
    var sendUs: Int64 = 0      // sender clock: when it left the encoder
    var encodeUs: UInt32 = 0
    var flags: UInt8 = 0       // bit0 = keyframe

    var isKey: Bool { flags & 1 != 0 }

    init(frameId: UInt32, captureUs: Int64, sendUs: Int64, encodeUs: UInt32, key: Bool) {
        self.frameId = frameId
        self.captureUs = captureUs
        self.sendUs = sendUs
        self.encodeUs = encodeUs
        flags = key ? 1 : 0
    }

    init(_ payload: Data) {
        var r = ByteReader(payload)
        frameId = r.u32()
        captureUs = r.i64()
        sendUs = r.i64()
        encodeUs = r.u32()
        flags = r.u8()
    }

    func write(_ w: inout ByteWriter) {
        w.u32(frameId)
        w.i64(captureUs)
        w.i64(sendUs)
        w.u32(encodeUs)
        w.u8(flags)
        for _ in 0..<7 { w.u8(0) }
    }
}

struct ConfigMsg {
    var codec: Codec
    var width: UInt16
    var height: UInt16
    var fps: UInt16
    var kbps: UInt32
    var sessionId: UInt64

    init(codec: Codec, width: UInt16, height: UInt16, fps: UInt16, kbps: UInt32, sessionId: UInt64 = 0) {
        self.codec = codec
        self.width = width
        self.height = height
        self.fps = fps
        self.kbps = kbps
        self.sessionId = sessionId
    }

    init?(_ payload: Data) {
        guard payload.count >= 19 else { return nil }
        var r = ByteReader(payload)
        guard let c = Codec(rawValue: r.u8()) else { return nil }
        codec = c
        width = r.u16()
        height = r.u16()
        fps = r.u16()
        kbps = r.u32()
        sessionId = r.u64()
    }
}

/// Builders for the messages the iOS side sends.
enum Message {
    static func build(_ type: MsgType, _ payload: Data = Data()) -> Data {
        var w = ByteWriter(capacity: Wire.headerSize + payload.count)
        w.u32(UInt32(payload.count + 1))
        w.u8(type.rawValue)
        w.bytes(payload)
        return w.data
    }

    static func hello(maxWidth: Int, maxHeight: Int, fps: Int, name: String, sessionId: UInt64 = 0) -> Data {
        var w = ByteWriter()
        w.u16(Wire.version)
        w.u16(UInt16(clamping: maxWidth))
        w.u16(UInt16(clamping: maxHeight))
        w.u16(UInt16(clamping: fps))
        w.u32(0b11) // H.264 + HEVC
        w.u64(sessionId)
        w.bytes(Data(name.utf8))
        return build(.hello, w.data)
    }

    static func config(_ c: ConfigMsg) -> Data {
        var w = ByteWriter()
        w.u8(c.codec.rawValue)
        w.u16(c.width)
        w.u16(c.height)
        w.u16(c.fps)
        w.u32(c.kbps)
        w.u64(c.sessionId)
        return build(.config, w.data)
    }

    static func ping() -> Data {
        var w = ByteWriter()
        w.i64(Clock.nowUs)
        return build(.ping, w.data)
    }

    /// Answer to the peer's ping: echo its timestamp, add ours.
    static func pong(echo ping: Data) -> Data {
        var r = ByteReader(ping)
        var w = ByteWriter()
        w.i64(r.i64())
        w.i64(Clock.nowUs)
        return build(.pong, w.data)
    }

    /// kind: 1 down, 2 move, 3 up, 4 wheel. x, y: 0...1 over the streamed picture.
    static func input(kind: UInt8, button: UInt8 = 0, x: Double, y: Double, wheel: Int16 = 0) -> Data {
        var w = ByteWriter()
        w.u8(kind)
        w.u8(button)
        w.u16(UInt16(max(0, min(1, x)) * 65535))
        w.u16(UInt16(max(0, min(1, y)) * 65535))
        w.i16(wheel)
        w.u32(UInt32(truncatingIfNeeded: Clock.nowUs / 1000))
        return build(.input, w.data)
    }

    static func video(_ header: VideoHeader, accessUnit: Data) -> Data {
        var w = ByteWriter(capacity: Wire.headerSize + VideoHeader.size + accessUnit.count)
        w.u32(UInt32(1 + VideoHeader.size + accessUnit.count))
        w.u8(MsgType.video.rawValue)
        header.write(&w)
        w.bytes(accessUnit)
        return w.data
    }
}

/// Estimates the peer's clock offset from ping/pong pairs, keeping the least-queued sample
/// (same method as PathManager on the PC).
struct ClockSync {
    private var samples: [(rtt: Int64, offset: Int64)] = []
    private(set) var offsetUs: Int64 = 0   // peer clock minus ours
    private(set) var rttUs: Int64 = 0

    mutating func add(pong payload: Data) {
        guard payload.count >= 16 else { return }
        let now = Clock.nowUs
        var r = ByteReader(payload)
        let t0 = r.i64()
        let peer = r.i64()
        samples.append((now - t0, peer - (t0 + now) / 2))
        if samples.count > 20 { samples.removeFirst() }
        if let best = samples.min(by: { $0.rtt < $1.rtt }) { offsetUs = best.offset }
        rttUs = samples.map { $0.rtt }.reduce(0, +) / Int64(samples.count)
    }

    mutating func reset() {
        samples.removeAll()
        offsetUs = 0
        rttUs = 0
    }
}
