"""Analyze an AirPlay mirroring capture (pcap / pcapng, e.g. from `pktmon etl2pcap`).

Stdlib only. Usage:
    python tools/airplay-pcap-analyze.py captures/airserver.pcapng [--receiver 192.168.10.138] [--max-messages 80]

Reports:
  1. Flow table (TCP/UDP, bytes, duration, bitrate)
  2. Control channel on TCP 7000: HTTP/RTSP requests + responses, binary plists decoded
  3. Mirror data stream: 128-byte AirPlay mirror headers, codec config (SPS -> resolution),
     frame count, fps, bitrate
  4. UDP streams: RTP payload types (audio), timing packets
"""

import argparse
import collections
import ipaddress
import plistlib
import struct
import sys

CONTROL_PORTS = {7000, 7100}


# ---------------------------------------------------------------- capture reading

def read_packets(path):
    """Yield (timestamp_seconds, linktype, frame_bytes)."""
    with open(path, "rb") as f:
        data = f.read()
    magic = data[:4]
    if magic == b"\x0a\x0d\x0d\x0a":
        yield from _read_pcapng(data)
    elif magic in (b"\xd4\xc3\xb2\xa1", b"\x4d\x3c\xb2\xa1", b"\xa1\xb2\xc3\xd4", b"\xa1\xb2\x3c\x4d"):
        yield from _read_pcap(data)
    else:
        sys.exit(f"Unknown capture format (magic {magic.hex()})")


def _read_pcap(data):
    le = data[:4] in (b"\xd4\xc3\xb2\xa1", b"\x4d\x3c\xb2\xa1")
    nano = data[:4] in (b"\x4d\x3c\xb2\xa1", b"\xa1\xb2\x3c\x4d")
    e = "<" if le else ">"
    linktype = struct.unpack_from(e + "I", data, 20)[0]
    off = 24
    while off + 16 <= len(data):
        sec, frac, incl, _ = struct.unpack_from(e + "IIII", data, off)
        off += 16
        yield sec + frac / (1e9 if nano else 1e6), linktype, data[off:off + incl]
        off += incl


def _read_pcapng(data):
    off = 0
    e = "<"
    interfaces = []
    while off + 12 <= len(data):
        btype = struct.unpack_from(e + "I", data, off)[0]
        if btype == 0x0A0D0D0A:
            bom = data[off + 8:off + 12]
            e = "<" if bom == b"\x4d\x3c\x2b\x1a" else ">"
            interfaces = []
        blen = struct.unpack_from(e + "I", data, off + 4)[0]
        if blen < 12:
            break
        body = data[off + 8:off + blen - 4]
        if btype == 1:  # Interface Description Block
            linktype = struct.unpack_from(e + "H", body, 0)[0]
            tsres = 1e-6
            opt = 8
            while opt + 4 <= len(body):
                code, olen = struct.unpack_from(e + "HH", body, opt)
                if code == 0:
                    break
                if code == 9 and olen >= 1:
                    v = body[opt + 4]
                    tsres = 2 ** -(v & 0x7F) if v & 0x80 else 10 ** -v
                opt += 4 + ((olen + 3) & ~3)
            interfaces.append((linktype, tsres))
        elif btype == 6:  # Enhanced Packet Block
            ifid, tshi, tslo, caplen = struct.unpack_from(e + "IIII", body, 0)
            linktype, tsres = interfaces[ifid] if ifid < len(interfaces) else (1, 1e-6)
            yield ((tshi << 32) | tslo) * tsres, linktype, body[20:20 + caplen]
        elif btype == 3:  # Simple Packet Block
            linktype, _ = interfaces[0] if interfaces else (1, 1e-6)
            yield 0.0, linktype, body[4:]
        off += blen


def parse_ip(linktype, frame):
    """Return (src, dst, proto, l4_bytes) or None."""
    if linktype == 1:  # Ethernet
        if len(frame) < 14:
            return None
        etype = struct.unpack_from(">H", frame, 12)[0]
        off = 14
        while etype in (0x8100, 0x88A8) and len(frame) >= off + 4:
            etype = struct.unpack_from(">H", frame, off + 2)[0]
            off += 4
        pkt = frame[off:]
    elif linktype in (101, 228, 229):  # raw IP
        pkt = frame
        etype = 0x0800 if pkt and pkt[0] >> 4 == 4 else 0x86DD
    else:
        return None
    if etype == 0x0800 and len(pkt) >= 20:
        ihl = (pkt[0] & 0x0F) * 4
        total = struct.unpack_from(">H", pkt, 2)[0]
        proto = pkt[9]
        src = str(ipaddress.IPv4Address(pkt[12:16]))
        dst = str(ipaddress.IPv4Address(pkt[16:20]))
        return src, dst, proto, pkt[ihl:total if total else None]
    if etype == 0x86DD and len(pkt) >= 40:
        proto = pkt[6]
        plen = struct.unpack_from(">H", pkt, 4)[0]
        src = str(ipaddress.IPv6Address(pkt[8:24]))
        dst = str(ipaddress.IPv6Address(pkt[24:40]))
        return src, dst, proto, pkt[40:40 + plen]
    return None


# ---------------------------------------------------------------- flows

class Flow:
    def __init__(self, proto, a, b):
        self.proto, self.a, self.b = proto, a, b  # a/b = (ip, port), a = first sender
        self.packets = 0
        self.bytes = 0
        self.first = None
        self.last = None
        self.segments = {a: [], b: []}  # TCP: (seq, payload) ; UDP: (ts, payload)
        self.isn = {}

    def add(self, ts, sender, payload, seq=None, syn=False):
        self.packets += 1
        self.bytes += len(payload)
        self.first = ts if self.first is None else min(self.first, ts)
        self.last = ts if self.last is None else max(self.last, ts)
        if self.proto == "TCP":
            if syn:
                self.isn[sender] = (seq + 1) & 0xFFFFFFFF
            if payload:
                self.segments[sender].append((seq, ts, payload))
        else:
            self.segments[sender].append((ts, payload))

    def label(self):
        return f"{self.proto} {self.a[0]}:{self.a[1]} <-> {self.b[0]}:{self.b[1]}"

    def stream(self, sender):
        """Reassemble one TCP direction. Returns (bytes, gaps, [(offset, ts)])."""
        segs = self.segments[sender]
        if not segs:
            return b"", 0, []
        base = self.isn.get(sender, min(s for s, _, _ in segs))
        rel = sorted((((s - base) & 0xFFFFFFFF), ts, p) for s, ts, p in segs)
        out = bytearray()
        times = []
        gaps = 0
        for r, ts, p in rel:
            if r > 0x7FFFFFFF:  # retransmission from before ISN window
                continue
            end = r + len(p)
            if end <= len(out):
                continue
            if r > len(out):
                gaps += 1
                out.extend(b"\x00" * (r - len(out)))
            times.append((len(out), ts))
            out.extend(p[len(out) - r:])
        return bytes(out), gaps, times


def collect(path):
    flows = {}
    count = 0
    for ts, linktype, frame in read_packets(path):
        ip = parse_ip(linktype, frame)
        if not ip:
            continue
        src, dst, proto, l4 = ip
        if proto == 6 and len(l4) >= 20:
            sport, dport, seq = struct.unpack_from(">HHI", l4, 0)
            doff = (l4[12] >> 4) * 4
            flags = l4[13]
            payload = l4[doff:]
            key = ("TCP",) + tuple(sorted([(src, sport), (dst, dport)]))
            fl = flows.get(key)
            if fl is None:
                fl = flows[key] = Flow("TCP", (src, sport), (dst, dport))
            fl.add(ts, (src, sport), payload, seq, syn=bool(flags & 0x02))
        elif proto == 17 and len(l4) >= 8:
            sport, dport = struct.unpack_from(">HH", l4, 0)
            key = ("UDP",) + tuple(sorted([(src, sport), (dst, dport)]))
            fl = flows.get(key)
            if fl is None:
                fl = flows[key] = Flow("UDP", (src, sport), (dst, dport))
            fl.add(ts, (src, sport), l4[8:])
        count += 1
    return flows, count


# ---------------------------------------------------------------- control channel (HTTP/RTSP)

def summarize(obj, depth=0, max_bytes=24):
    pad = "  " * depth
    if isinstance(obj, dict):
        lines = []
        for k, v in obj.items():
            if isinstance(v, (dict, list)):
                lines.append(f"{pad}{k}:")
                lines.append(summarize(v, depth + 1, max_bytes))
            else:
                lines.append(f"{pad}{k}: {summarize(v, 0, max_bytes).strip()}")
        return "\n".join(lines)
    if isinstance(obj, list):
        return "\n".join(f"{pad}- " + summarize(v, depth + 1, max_bytes).strip() for v in obj)
    if isinstance(obj, (bytes, bytearray)):
        return f"{pad}<{len(obj)} bytes> {bytes(obj[:max_bytes]).hex()}{'...' if len(obj) > max_bytes else ''}"
    return f"{pad}{obj!r}"


def describe_body(headers, body):
    if not body:
        return ""
    ctype = headers.get("content-type", "")
    if body.startswith(b"bplist00"):
        try:
            return summarize(plistlib.loads(body))
        except Exception as exc:  # noqa: BLE001
            return f"<bplist parse error {exc}> {len(body)} bytes"
    if ctype.startswith("text/") or ctype in ("application/sdp",) or all(32 <= c < 127 or c in (9, 10, 13) for c in body[:200]):
        text = body.decode("latin1")
        return text if len(text) < 1500 else text[:1500] + "..."
    return f"<{len(body)} bytes {ctype}> {body[:32].hex()}{'...' if len(body) > 32 else ''}"


def parse_messages(stream):
    """Split an HTTP/RTSP byte stream into (start_line, headers, body, offset)."""
    msgs = []
    off = 0
    while off < len(stream):
        end = stream.find(b"\r\n\r\n", off)
        if end < 0:
            break
        head = stream[off:end].decode("latin1", "replace").split("\r\n")
        start = head[0]
        if not (start.startswith(("HTTP/", "RTSP/")) or " RTSP/" in start or " HTTP/" in start):
            # Lost sync (e.g. reverse-HTTP event channel or encrypted stream)
            msgs.append(("<non-HTTP data, stopping>", {}, stream[off:off + 64], off))
            break
        headers = {}
        for line in head[1:]:
            if ":" in line:
                k, v = line.split(":", 1)
                headers[k.strip().lower()] = v.strip()
        length = int(headers.get("content-length", "0") or 0)
        body = stream[end + 4:end + 4 + length]
        msgs.append((start, headers, body, off))
        off = end + 4 + length
    return msgs


def report_control(flows, receiver, max_messages):
    print("\n== 2. Control channel (TCP 7000/7100) ==")
    ctrl = [f for f in flows.values() if f.proto == "TCP" and ({f.a[1], f.b[1]} & CONTROL_PORTS)]
    if not ctrl:
        print("  (none found)")
        return
    for fl in sorted(ctrl, key=lambda f: f.first or 0):
        server = fl.a if fl.a[1] in CONTROL_PORTS and (receiver is None or fl.a[0] == receiver) else fl.b
        client = fl.b if server == fl.a else fl.a
        print(f"\n-- {fl.label()}  ({fl.packets} pkts, {fl.bytes} B)")
        req, req_gaps, req_t = fl.stream(client)
        rsp, rsp_gaps, rsp_t = fl.stream(server)
        if req_gaps or rsp_gaps:
            print(f"   ! TCP gaps: client {req_gaps}, server {rsp_gaps} (capture lost packets)")
        reqs = parse_messages(req)
        rsps = parse_messages(rsp)

        def ts_at(times, offset):
            t = None
            for o, ts in times:
                if o > offset:
                    break
                t = ts
            return t

        base = fl.first or 0
        shown = 0
        for i, (start, headers, body, off) in enumerate(reqs):
            if shown >= max_messages:
                print(f"   ... {len(reqs) - shown} more requests")
                break
            shown += 1
            t = ts_at(req_t, off)
            interesting = {k: v for k, v in headers.items() if k in (
                "content-type", "user-agent", "x-apple-device-id", "x-apple-protocol-version",
                "cseq", "x-apple-hkp", "x-apple-client-name", "transport", "session")}
            print(f"\n   [{(t or base) - base:7.3f}s] >> {start}")
            for k, v in interesting.items():
                print(f"        {k}: {v}")
            desc = describe_body(headers, body)
            if desc:
                print("        body:\n" + "\n".join("          " + l for l in desc.splitlines()))
            if i < len(rsps):
                rstart, rheaders, rbody, roff = rsps[i]
                print(f"            << {rstart}  ({rheaders.get('content-type', '-')}, {len(rbody)} B)")
                rdesc = describe_body(rheaders, rbody)
                if rdesc:
                    print("\n".join("              " + l for l in rdesc.splitlines()[:60]))


# ---------------------------------------------------------------- mirror data stream

def read_ue(bits, pos):
    zeros = 0
    while pos < len(bits) and bits[pos] == 0:
        zeros += 1
        pos += 1
    pos += 1
    val = 0
    for _ in range(zeros):
        val = (val << 1) | bits[pos]
        pos += 1
    return (1 << zeros) - 1 + val, pos


def sps_resolution(sps):
    """Parse H.264 SPS -> (profile_idc, level_idc, width, height)."""
    rbsp = bytearray()
    zeros = 0
    for b in sps[1:]:
        if zeros >= 2 and b == 3:
            zeros = 0
            continue
        rbsp.append(b)
        zeros = zeros + 1 if b == 0 else 0
    bits = [(byte >> (7 - i)) & 1 for byte in rbsp for i in range(8)]
    profile, level = rbsp[0], rbsp[2]
    pos = 24
    _, pos = read_ue(bits, pos)  # sps id
    chroma = 1
    if profile in (100, 110, 122, 244, 44, 83, 86, 118, 128, 138, 139, 134, 135):
        chroma, pos = read_ue(bits, pos)
        if chroma == 3:
            pos += 1
        _, pos = read_ue(bits, pos)
        _, pos = read_ue(bits, pos)
        pos += 1
        if bits[pos]:
            return profile, level, None, None  # scaling matrices: not parsed
        pos += 1
    _, pos = read_ue(bits, pos)  # log2_max_frame_num
    poc_type, pos = read_ue(bits, pos)
    if poc_type == 0:
        _, pos = read_ue(bits, pos)
    elif poc_type == 1:
        pos += 1
        for _ in range(2):
            _, pos = read_ue(bits, pos)  # signed, value unused
        n, pos = read_ue(bits, pos)
        for _ in range(n):
            _, pos = read_ue(bits, pos)
    _, pos = read_ue(bits, pos)  # max_num_ref_frames
    pos += 1
    w_mbs, pos = read_ue(bits, pos)
    h_map, pos = read_ue(bits, pos)
    frame_mbs_only = bits[pos]
    pos += 1
    if not frame_mbs_only:
        pos += 1
    pos += 1
    width = (w_mbs + 1) * 16
    height = (2 - frame_mbs_only) * (h_map + 1) * 16
    if bits[pos]:
        pos += 1
        cl, pos = read_ue(bits, pos)
        cr, pos = read_ue(bits, pos)
        ct, pos = read_ue(bits, pos)
        cb, pos = read_ue(bits, pos)
        sx = 1 if chroma == 0 or chroma == 3 else 2
        sy = (1 if chroma in (0, 2, 3) else 2) * (2 - frame_mbs_only)
        width -= (cl + cr) * sx
        height -= (ct + cb) * sy
    return profile, level, width, height


def describe_codec_payload(p):
    if len(p) >= 7 and p[0] == 1 and (p[4] & 0xFC) == 0xFC and (p[5] & 0xE0) == 0xE0:  # avcC
        profile, compat, level = p[1], p[2], p[3]
        nal_len = (p[4] & 3) + 1
        n_sps = p[5] & 0x1F
        off = 6
        out = [f"avcC (H.264) profile={profile} compat=0x{compat:02x} level={level} nal_length_size={nal_len} sps_count={n_sps}"]
        for _ in range(n_sps):
            ln = struct.unpack_from(">H", p, off)[0]
            sps = p[off + 2:off + 2 + ln]
            off += 2 + ln
            try:
                prof, lev, w, h = sps_resolution(sps)
                out.append(f"  SPS {ln} B: profile_idc={prof} level_idc={lev} -> {w}x{h}")
            except Exception as exc:  # noqa: BLE001
                out.append(f"  SPS {ln} B: parse error {exc}")
        if off < len(p):
            out.append(f"  pps_count={p[off]}")
        return "\n".join(out)
    if len(p) >= 23 and p[0] == 1 and (p[1] & 0x1F) in (1, 2):
        return f"possible hvcC (HEVC) profile_idc={p[1] & 0x1F} ({len(p)} B)"
    return f"unknown codec config {len(p)} B: {p[:32].hex()}"


def looks_like_mirror_stream(stream):
    if len(stream) < 128:
        return False
    size, ptype = struct.unpack_from("<IH", stream, 0)
    return ptype in (0, 1, 2, 5) and size < 8_000_000


def report_mirror(flows, receiver):
    print("\n== 3. Mirror data stream (TCP, 128-byte headers) ==")
    found = False
    for fl in sorted(flows.values(), key=lambda f: -f.bytes):
        if fl.proto != "TCP" or ({fl.a[1], fl.b[1]} & CONTROL_PORTS) or fl.bytes < 50_000:
            continue
        for sender in (fl.a, fl.b):
            stream, gaps, times = fl.stream(sender)
            if not looks_like_mirror_stream(stream):
                continue
            found = True
            print(f"\n-- {fl.label()}  sender={sender[0]}:{sender[1]}  {len(stream)} B, gaps={gaps}")
            off = 0
            counts = collections.Counter()
            video_sizes = []
            first_video = None
            ntp_times = []
            unknown_flags = collections.Counter()
            while off + 128 <= len(stream):
                size, ptype, flags = struct.unpack_from("<IHH", stream, off)
                ntp = struct.unpack_from("<Q", stream, off + 8)[0]
                if size > 8_000_000 or ptype > 64:
                    print(f"   ! lost sync at offset {off} (size={size} type={ptype})")
                    break
                payload = stream[off + 128:off + 128 + size]
                counts[ptype] += 1
                unknown_flags[(ptype, flags)] += 1
                if ptype == 0:
                    if first_video is None:
                        first_video = payload[:16]
                    video_sizes.append(size)
                    ntp_times.append(ntp)
                elif ptype == 1:
                    w1, h1 = struct.unpack_from("<ff", stream, off + 40)
                    w2, h2 = struct.unpack_from("<ff", stream, off + 56)
                    print(f"   codec packet @ {off}: header floats src={w1:.0f}x{h1:.0f} out={w2:.0f}x{h2:.0f}")
                    print("   " + describe_codec_payload(payload).replace("\n", "\n   "))
                off += 128 + size
            names = {0: "video", 1: "codec-config", 2: "heartbeat", 5: "report"}
            print("   packet types: " + ", ".join(f"{names.get(t, t)}={c}" for t, c in sorted(counts.items())))
            print("   (type, flags) seen: " + ", ".join(f"{t}/0x{fl_:04x} x{c}" for (t, fl_), c in sorted(unknown_flags.items())))
            if video_sizes:
                dur = (times[-1][1] - times[0][1]) if len(times) > 1 else 0
                avg = sum(video_sizes) / len(video_sizes)
                print(f"   video frames={len(video_sizes)} avg={avg:.0f} B max={max(video_sizes)} B")
                if dur > 0:
                    print(f"   wall-clock {dur:.2f}s -> {len(video_sizes) / dur:.1f} fps, {len(stream) * 8 / dur / 1e6:.2f} Mbit/s")
                if len(ntp_times) > 1:
                    secs = [(t >> 32) + (t & 0xFFFFFFFF) / 2**32 for t in ntp_times]
                    span = secs[-1] - secs[0]
                    if span > 0:
                        print(f"   NTP timestamps span {span:.2f}s -> {len(secs) / span:.1f} fps (sender clock)")
                print(f"   first video payload bytes: {first_video.hex()}  (random-looking => encrypted, 00000001/length-prefixed NAL => clear)")
    if not found:
        print("  (no TCP stream with AirPlay mirror headers found)")


# ---------------------------------------------------------------- UDP

def report_udp(flows):
    print("\n== 4. UDP streams ==")
    udp = [f for f in flows.values() if f.proto == "UDP" and 5353 not in (f.a[1], f.b[1]) and 1900 not in (f.a[1], f.b[1])]
    if not udp:
        print("  (none)")
        return
    for fl in sorted(udp, key=lambda f: -f.bytes)[:15]:
        dur = (fl.last - fl.first) if fl.last and fl.first else 0
        print(f"\n-- {fl.label()}  {fl.packets} pkts {fl.bytes} B" + (f" {fl.bytes * 8 / dur / 1000:.0f} kbit/s" if dur > 0 else ""))
        for sender in (fl.a, fl.b):
            pkts = fl.segments[sender]
            if not pkts:
                continue
            rtp = collections.Counter()
            sizes = collections.Counter()
            for _, p in pkts:
                sizes[len(p)] += 1
                if len(p) >= 12 and p[0] >> 6 == 2:
                    rtp[p[1] & 0x7F] += 1
            common = ", ".join(f"{s}B x{c}" for s, c in sizes.most_common(4))
            print(f"   from {sender[0]}:{sender[1]}: {len(pkts)} pkts, sizes {common}")
            if rtp:
                print("   RTP payload types: " + ", ".join(f"pt={pt} x{c}" for pt, c in rtp.most_common(6)))


# ---------------------------------------------------------------- main

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("capture")
    ap.add_argument("--receiver", help="IP of the AirPlay receiver (PC)")
    ap.add_argument("--max-messages", type=int, default=80)
    args = ap.parse_args()

    flows, count = collect(args.capture)
    print(f"== 1. Flows ({count} IP packets, {len(flows)} flows) ==")
    for fl in sorted(flows.values(), key=lambda f: -f.bytes)[:25]:
        dur = (fl.last - fl.first) if fl.last and fl.first else 0
        rate = f"{fl.bytes * 8 / dur / 1e6:6.2f} Mbit/s" if dur > 0 else "        -    "
        print(f"  {fl.label():70s} {fl.packets:7d} pkts {fl.bytes:11d} B {dur:7.2f}s {rate}")

    report_control(flows, args.receiver, args.max_messages)
    report_mirror(flows, args.receiver)
    report_udp(flows)


if __name__ == "__main__":
    main()
