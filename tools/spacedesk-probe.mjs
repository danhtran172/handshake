// Probe the spacedesk WebSocket protocol (as documented in docs/spacedesk-protocol.md).
// Requires Node 18+. No dependencies.
//
// Uses a minimal WebSocket client over node:net because spacedesk matches the
// handshake header "Sec-WebSocket-Key" case-sensitively, and Node's built-in
// WebSocket (undici) sends lowercase header names, which the server rejects.
//
// Usage:
//   node tools/spacedesk-probe.mjs [--host 127.0.0.1] [--port 28252] [--major 4] [--minor 8]
//        [--compression 3] [--quality 40] [--width 1280] [--height 720]
//        [--seconds 10] [--save <dir>] [--saveMax 5] [--noAck] [--mouseDx 0] [--mouseDy 0]
//
// --mouseDx/--mouseDy send one relative Mouse Move packet after the first full frame.

import { mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import net from "node:net";
import { createHash, randomBytes } from "node:crypto";

// Minimal RFC 6455 client: binary messages only, client frames masked.
class RawWebSocket {
  static OPEN = 1;

  constructor(host, port) {
    this.readyState = 0;
    this.buffer = Buffer.alloc(0);
    this.handshakeDone = false;
    this.fragments = [];
    this.key = randomBytes(16).toString("base64");
    this.socket = net.connect(port, host, () => {
      this.socket.setNoDelay(true);
      this.socket.write(
        `GET / HTTP/1.1\r\nHost: ${host}:${port}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n` +
          `Sec-WebSocket-Key: ${this.key}\r\nSec-WebSocket-Version: 13\r\n\r\n`
      );
    });
    this.socket.on("data", (d) => this.onData(d));
    this.socket.on("error", (e) => this.onerror?.({ message: e.message }));
    this.socket.on("close", () => {
      const wasOpen = this.readyState === RawWebSocket.OPEN;
      this.readyState = 3;
      this.onclose?.({ code: this.closeCode ?? (wasOpen ? 1006 : 1006), reason: this.closeReason ?? "" });
    });
  }

  onData(chunk) {
    this.buffer = Buffer.concat([this.buffer, chunk]);
    if (!this.handshakeDone) {
      const end = this.buffer.indexOf("\r\n\r\n");
      if (end < 0) return;
      const head = this.buffer.subarray(0, end).toString("latin1");
      this.buffer = this.buffer.subarray(end + 4);
      const expected = createHash("sha1").update(this.key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11").digest("base64");
      if (!head.startsWith("HTTP/1.1 101") || !head.includes(expected)) {
        this.onerror?.({ message: `bad handshake: ${head}` });
        this.socket.destroy();
        return;
      }
      this.handshakeDone = true;
      this.readyState = RawWebSocket.OPEN;
      this.onhandshake?.(head);
      this.onopen?.();
    }
    this.parseFrames();
  }

  parseFrames() {
    for (;;) {
      const b = this.buffer;
      if (b.length < 2) return;
      const fin = (b[0] & 0x80) !== 0;
      const opcode = b[0] & 0x0f;
      const masked = (b[1] & 0x80) !== 0;
      let len = b[1] & 0x7f;
      let off = 2;
      if (len === 126) {
        if (b.length < 4) return;
        len = b.readUInt16BE(2);
        off = 4;
      } else if (len === 127) {
        if (b.length < 10) return;
        len = Number(b.readBigUInt64BE(2));
        off = 10;
      }
      const maskKey = masked ? b.subarray(off, off + 4) : null;
      if (masked) off += 4;
      if (b.length < off + len) return;
      let payload = Buffer.from(b.subarray(off, off + len));
      if (maskKey) for (let i = 0; i < payload.length; i++) payload[i] ^= maskKey[i & 3];
      this.buffer = b.subarray(off + len);

      if (opcode === 0x8) {
        this.closeCode = payload.length >= 2 ? payload.readUInt16BE(0) : 1005;
        this.closeReason = payload.subarray(2).toString("utf8");
        this.socket.end();
        return;
      }
      if (opcode === 0x9) {
        this.sendFrame(0xa, payload);
        continue;
      }
      if (opcode === 0xa) continue;
      this.fragments.push(payload);
      if (!fin) continue;
      const msg = Buffer.concat(this.fragments);
      this.fragments = [];
      const ab = msg.buffer.slice(msg.byteOffset, msg.byteOffset + msg.byteLength);
      this.onmessage?.({ data: opcode === 0x1 ? msg.toString("utf8") : ab });
    }
  }

  sendFrame(opcode, data) {
    const payload = Buffer.from(data.buffer ? new Uint8Array(data.buffer, data.byteOffset, data.byteLength) : data);
    const len = payload.length;
    const head = len < 126 ? Buffer.alloc(2) : len < 65536 ? Buffer.alloc(4) : Buffer.alloc(10);
    head[0] = 0x80 | opcode;
    if (len < 126) head[1] = 0x80 | len;
    else if (len < 65536) {
      head[1] = 0x80 | 126;
      head.writeUInt16BE(len, 2);
    } else {
      head[1] = 0x80 | 127;
      head.writeBigUInt64BE(BigInt(len), 2);
    }
    const mask = randomBytes(4);
    for (let i = 0; i < len; i++) payload[i] ^= mask[i & 3];
    this.socket.write(Buffer.concat([head, mask, payload]));
  }

  send(data) {
    this.sendFrame(0x2, data);
  }

  close(code = 1000) {
    const p = Buffer.alloc(2);
    p.writeUInt16BE(code);
    this.sendFrame(0x8, p);
    setTimeout(() => this.socket.destroy(), 500);
  }
}

const args = Object.fromEntries(
  process.argv.slice(2).reduce((acc, a, i, all) => {
    if (a.startsWith("--")) {
      const next = all[i + 1];
      acc.push([a.slice(2), next === undefined || next.startsWith("--") ? true : next]);
    }
    return acc;
  }, [])
);

const opt = {
  host: args.host ?? "127.0.0.1",
  port: Number(args.port ?? 28252),
  major: Number(args.major ?? 4),
  minor: Number(args.minor ?? 8),
  compression: Number(args.compression ?? 3),
  quality: Number(args.quality ?? 40),
  width: Number(args.width ?? 1280),
  height: Number(args.height ?? 720),
  seconds: Number(args.seconds ?? 10),
  save: args.save,
  saveMax: Number(args.saveMax ?? 5),
  ack: !args.noAck,
  mouseDx: Number(args.mouseDx ?? 0),
  mouseDy: Number(args.mouseDy ?? 0),
};

const HEADER = 128;
const PAYLOAD = 334;
const TYPE = [
  "Identification", "Ping", "FrameBuffer", "Visibility", "CursorPosition", "CursorBitmap",
  "PowerSuspendHibernate", "FlowControlAck", "Disconnect", "Rotation", "Mouse", "Keyboard", "Touch",
];

function identificationPacket() {
  const buf = new Uint8Array(HEADER + PAYLOAD);
  const v = new DataView(buf.buffer);
  v.setInt32(0, 0, true); // Identification
  v.setInt32(4, PAYLOAD, true);
  v.setInt32(8, opt.major, true);
  v.setInt32(12, opt.minor, true);
  v.setInt32(16, 1, true); // ClientType WebBrowser
  v.setInt32(20, 0, true);
  v.setInt32(24, opt.compression, true);
  v.setInt32(32, Math.max(1, opt.quality), true);
  v.setInt16(44, 0, true); // frame rate: unlimited
  v.setInt32(48, 1, true); // no custom resolution
  v.setInt32(52, opt.width, true);
  v.setInt32(88, opt.height, true);
  // Payload: device id "1111122222" (UTF-16LE), device name at +78
  const id = "1111122222";
  for (let i = 0; i < id.length; i++) buf[HEADER + i * 2] = id.charCodeAt(i);
  const name = "Probe on Node";
  for (let i = 0; i < name.length; i++) buf[HEADER + 78 + i * 2] = name.charCodeAt(i);
  return buf;
}

function mouseMovePacket(dx, dy) {
  const buf = new Uint8Array(HEADER);
  const v = new DataView(buf.buffer);
  v.setInt32(0, 10, true); // Mouse
  v.setInt32(8, dx, true);
  v.setInt32(12, dy, true);
  v.setInt32(20, 0x1, true); // Move
  return buf;
}

function ackPacket() {
  const buf = new Uint8Array(HEADER);
  new DataView(buf.buffer).setInt32(0, 7, true);
  return buf;
}

const i32 = (v, o) => v.getInt32(o, true);
const t0 = Date.now();
const ms = () => String(Date.now() - t0).padStart(6);
const stats = { packets: 0, bytes: 0, frames: 0, acks: 0, byType: {} };
let fragments = 0;
let saved = 0;

if (opt.save) mkdirSync(opt.save, { recursive: true });

const url = `ws://${opt.host}:${opt.port}`;
console.log(`[${ms()}] connecting ${url} (protocol ${opt.major}.${opt.minor}, compression ${opt.compression}, ${opt.width}x${opt.height})`);
const ws = new RawWebSocket(opt.host, opt.port);
const WebSocket = RawWebSocket;
ws.onhandshake = (head) => console.log(`[${ms()}] handshake ok: ${head.split("\r\n")[0]}`);

ws.onopen = () => {
  console.log(`[${ms()}] open -> sending Identification (${HEADER + PAYLOAD} bytes)`);
  ws.send(identificationPacket());
};

ws.onmessage = (ev) => {
  const data = ev.data;
  if (!(data instanceof ArrayBuffer)) {
    console.log(`[${ms()}] text message: ${String(data).slice(0, 200)}`);
    return;
  }
  stats.packets++;
  stats.bytes += data.byteLength;
  if (data.byteLength < 8) {
    console.log(`[${ms()}] short packet ${data.byteLength} bytes`);
    return;
  }
  const v = new DataView(data);
  const type = i32(v, 0);
  const len = i32(v, 4);
  const name = TYPE[type] ?? `Unknown(${type})`;
  stats.byType[name] = (stats.byType[name] ?? 0) + 1;

  let detail = "";
  if (type === 2 && data.byteLength >= HEADER) {
    const [w, h, pitch, fmt, x, y, x2, y2, comp, frag] =
      [8, 12, 16, 20, 24, 28, 32, 36, 40, 64].map((o) => i32(v, o));
    const payload = new Uint8Array(data, HEADER);
    const magic = payload.length >= 2 ? payload[0].toString(16) + payload[1].toString(16) : "";
    detail = `screen=${w}x${h} pitch=${pitch} fmt=${fmt} rect=(${x},${y})-(${x2},${y2}) comp=${comp} frag=${frag} payload=${payload.length} magic=${magic}`;

    if (opt.save && saved < opt.saveMax && comp !== 0) {
      writeFileSync(join(opt.save, `frame_${String(stats.packets).padStart(4, "0")}_${x}_${y}.jpg`), payload);
      saved++;
    }

    // ACK once a full frame is drawn: a single packet, or every 4th fragment
    let complete = true;
    if (frag === 1) {
      fragments++;
      complete = fragments % 4 === 0;
      if (complete) fragments = 0;
    } else {
      fragments = 0;
    }
    if (complete) {
      stats.frames++;
      if (stats.frames === 1 && (opt.mouseDx || opt.mouseDy)) {
        ws.send(mouseMovePacket(opt.mouseDx, opt.mouseDy));
        console.log(`[${ms()}] sent Mouse Move dx=${opt.mouseDx} dy=${opt.mouseDy}`);
      }
      if (opt.ack && ws.readyState === WebSocket.OPEN) {
        ws.send(ackPacket());
        stats.acks++;
      }
    }
  } else if (type === 3) {
    detail = `visible=${i32(v, 8)}`;
  } else if (type === 4) {
    detail = `x=${i32(v, 8)} y=${i32(v, 12)} visible=${i32(v, 16)}`;
  } else if (type === 5) {
    detail = `size=${i32(v, 8)}x${i32(v, 12)} pitch=${i32(v, 16)} hot=(${i32(v, 20)},${i32(v, 24)}) flags=${i32(v, 28)}`;
  } else {
    const words = [];
    for (let o = 8; o < Math.min(64, data.byteLength); o += 4) words.push(i32(v, o));
    detail = `words[8..64]=${words.join(",")}`;
  }

  if (stats.packets <= 40 || type !== 2) {
    console.log(`[${ms()}] #${stats.packets} ${name} total=${data.byteLength} len=${len} ${detail}`);
  }
};

ws.onerror = (ev) => console.log(`[${ms()}] error: ${ev.message ?? ev.type}`);

ws.onclose = (ev) => {
  console.log(`[${ms()}] close code=${ev.code} reason="${ev.reason}"`);
  summary();
  process.exit(0);
};

function summary() {
  const sec = (Date.now() - t0) / 1000;
  console.log("---- summary ----");
  console.log(JSON.stringify({ ...stats, seconds: sec, fps: +(stats.frames / sec).toFixed(1), kbps: Math.round((stats.bytes * 8) / 1000 / sec) }, null, 2));
}

setTimeout(() => {
  if (ws.readyState === WebSocket.OPEN) {
    // Disconnect header (28 bytes, type 8) like the official web client
    const d = new Uint8Array(28);
    new DataView(d.buffer).setInt32(0, 8, true);
    ws.send(d);
    ws.close(1000);
  } else {
    summary();
    process.exit(0);
  }
}, opt.seconds * 1000);
