// HTTP + WebSocket hub running inside the Electron main process.
//
//  - Serves the iPhone web client (src/ios) and shared assets (src/shared).
//  - Owns the canonical shared state and broadcasts it to every client on change,
//    so the PC window and the iPhone always show the same thing.
//  - Relays WebRTC signaling between the PC window and the iPhone.
//  - Forwards binary JPEG frames (fallback transport) and their ACKs.

const http = require("node:http");
const fs = require("node:fs");
const path = require("node:path");
const crypto = require("node:crypto");
const { EventEmitter } = require("node:events");
const { WebSocketServer } = require("ws");
const { createState, validateSetting } = require("../shared/state.js");

const ROOT = path.join(__dirname, "..");
const MIME = {
  ".html": "text/html; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".css": "text/css; charset=utf-8",
  ".svg": "image/svg+xml",
  ".png": "image/png",
  ".json": "application/json",
  ".webmanifest": "application/manifest+json",
};
const ROUTES = [
  ["/ios/", path.join(ROOT, "ios")],
  ["/shared/", path.join(ROOT, "shared")],
];

class HandshakeServer extends EventEmitter {
  constructor({ port = 7420, name = "PC", token } = {}) {
    super();
    this.preferredPort = port;
    this.state = createState();
    this.state.pc.name = name;
    this.state.pc.token = token || crypto.randomBytes(4).toString("hex");
    this.clients = new Map(); // id -> { ws, role, name, model, ip }
    this.statsTimer = null;
  }

  async start() {
    this.http = http.createServer((req, res) => this.onHttp(req, res));
    this.wss = new WebSocketServer({ noServer: true, maxPayload: 32 * 1024 * 1024 });
    this.http.on("upgrade", (req, socket, head) => {
      if (new URL(req.url, "http://x").pathname !== "/ws") return socket.destroy();
      this.wss.handleUpgrade(req, socket, head, (ws) => this.onConnection(ws, req));
    });
    for (let port = this.preferredPort; port < this.preferredPort + 20; port++) {
      try {
        await new Promise((resolve, reject) => {
          this.http.once("error", reject);
          this.http.listen(port, "0.0.0.0", () => {
            this.http.off("error", reject);
            resolve();
          });
        });
        this.state.pc.port = port;
        return port;
      } catch (err) {
        if (err.code !== "EADDRINUSE") throw err;
      }
    }
    throw new Error("No free port for Handshake server");
  }

  stop() {
    for (const c of this.clients.values()) c.ws.close(1001);
    this.wss?.close();
    this.http?.close();
  }

  // ------------------------------------------------------------------ HTTP

  onHttp(req, res) {
    const url = new URL(req.url, "http://x");
    let pathname = decodeURIComponent(url.pathname);
    if (pathname === "/") {
      res.writeHead(302, { Location: "/ios/" + url.search });
      return res.end();
    }
    if (pathname === "/mockup" || pathname === "/mockup/") {
      res.writeHead(302, { Location: "/ios/mockup.html" });
      return res.end();
    }
    const local = ["127.0.0.1", "::1", "::ffff:127.0.0.1"].includes(req.socket.remoteAddress);
    if (pathname === "/debug/snapshot" && this.debugSnapshot && local) return this.debugSnapshot(url.searchParams, res);
    for (const [prefix, dir] of ROUTES) {
      if (!pathname.startsWith(prefix)) continue;
      let rel = pathname.slice(prefix.length) || "index.html";
      if (rel.endsWith("/")) rel += "index.html";
      const file = path.normalize(path.join(dir, rel));
      if (!file.startsWith(dir)) break;
      return fs.readFile(file, (err, data) => {
        if (err) {
          res.writeHead(404, { "Content-Type": "text/plain; charset=utf-8" });
          return res.end("Không tìm thấy");
        }
        res.writeHead(200, {
          "Content-Type": MIME[path.extname(file)] || "application/octet-stream",
          "Cache-Control": "no-store",
        });
        res.end(data);
      });
    }
    res.writeHead(404, { "Content-Type": "text/plain; charset=utf-8" });
    res.end("Không tìm thấy");
  }

  // ------------------------------------------------------------------ WebSocket

  onConnection(ws, req) {
    const id = crypto.randomBytes(4).toString("hex");
    const ip = (req.socket.remoteAddress || "").replace(/^::ffff:/, "");
    const client = { id, ws, role: null, name: "", model: "", ip };
    this.clients.set(id, client);

    ws.on("message", (data, isBinary) => {
      if (isBinary) return this.onBinary(client, data);
      let msg;
      try {
        msg = JSON.parse(data.toString());
      } catch {
        return;
      }
      if (!client.role && msg.t !== "hello") return;
      this.onMessage(client, msg);
    });
    ws.on("close", () => this.onClose(client));
    ws.on("error", () => {});
  }

  onMessage(client, msg) {
    switch (msg.t) {
      case "hello":
        return this.onHello(client, msg);
      case "set":
        return this.onSet(msg);
      case "session.start":
        return this.onSessionStart(client, msg);
      case "session.stop":
        return this.endSession("Đã dừng");
      case "display.select":
        if (this.state.displays.some((d) => d.id === msg.displayId)) {
          this.state.session.displayId = msg.displayId;
          this.broadcastState();
        }
        return;
      case "session.status":
        return this.onSessionStatus(client, msg);
      case "stats":
        return this.onStats(msg);
      case "signal":
        return this.onSignal(client, msg);
      case "input":
        if (this.state.session.mode === "display" && this.state.session.deviceId === client.id) this.emit("input", msg);
        return;
      case "ack":
        return this.sendTo(this.pcClient(), { t: "ack" });
      case "rename":
        if (client.role === "ios" && typeof msg.name === "string") {
          client.name = msg.name.slice(0, 40);
          this.syncDevices();
        }
        return;
      default:
        return;
    }
  }

  onHello(client, msg) {
    const isLocal = client.ip === "127.0.0.1" || client.ip === "::1";
    if (msg.role === "pc") {
      if (!isLocal) return client.ws.close(4003, "pc role is local only");
      // Only one PC window at a time
      const old = this.pcClient();
      if (old && old !== client) old.ws.close(4000, "replaced");
      client.role = "pc";
    } else if (msg.role === "ios") {
      if (msg.token !== this.state.pc.token) return client.ws.close(4001, "bad token");
      client.role = "ios";
      client.name = String(msg.name || "iPhone").slice(0, 40);
      client.model = String(msg.model || "").slice(0, 60);
      client.caps = {
        webrtc: !!msg.caps?.webrtc,
        captureStream: !!msg.caps?.captureStream,
        screen: { w: Number(msg.caps?.screen?.w) || 0, h: Number(msg.caps?.screen?.h) || 0, dpr: Number(msg.caps?.screen?.dpr) || 1 },
      };
    } else {
      return client.ws.close(4002, "unknown role");
    }
    this.sendTo(client, { t: "welcome", id: client.id, state: this.state });
    this.syncDevices();
    this.emit("client", client);
  }

  onSet(msg) {
    const [scope, key] = String(msg.path || "").split(".");
    if (scope !== "settings" || !validateSetting(key, msg.value)) return;
    this.state.settings[key] = msg.value;
    this.broadcastState();
  }

  onSessionStart(client, msg) {
    const mode = msg.mode === "mirror" ? "mirror" : msg.mode === "display" ? "display" : null;
    if (!mode) return;
    const deviceId = client.role === "ios" ? client.id : msg.deviceId;
    const device = this.clients.get(deviceId);
    if (!device || device.role !== "ios") return this.sendTo(client, { t: "error", message: "Chưa có iPhone nào kết nối" });
    if (!this.pcClient()) return this.sendTo(client, { t: "error", message: "Cửa sổ PC chưa sẵn sàng" });
    const displayId = msg.displayId ?? this.state.session.displayId ?? this.state.displays[0]?.id ?? null;
    this.state.session = {
      mode,
      deviceId,
      displayId,
      status: "connecting",
      transport: null,
      startedAt: Date.now(),
      error: null,
    };
    this.state.stats = { fps: 0, kbps: 0, rttMs: 0, width: 0, height: 0 };
    this.broadcastState();
  }

  onSessionStatus(client, msg) {
    const s = this.state.session;
    if (!s.mode) return;
    if (msg.status === "live" || msg.status === "connecting") s.status = msg.status;
    if (msg.status === "error") {
      s.status = "error";
      s.error = String(msg.error || "Lỗi kết nối").slice(0, 200);
    }
    if (msg.transport === "webrtc" || msg.transport === "jpeg") s.transport = msg.transport;
    this.broadcastState();
  }

  onStats(msg) {
    const st = this.state.stats;
    for (const k of ["fps", "kbps", "rttMs", "width", "height"]) {
      if (Number.isFinite(msg[k])) st[k] = Math.round(msg[k]);
    }
    // Stats change often; coalesce broadcasts to at most 2 per second
    if (!this.statsTimer) {
      this.statsTimer = setTimeout(() => {
        this.statsTimer = null;
        this.broadcastState();
      }, 500);
    }
  }

  onSignal(client, msg) {
    const target = msg.to === "pc" ? this.pcClient() : this.clients.get(msg.to);
    if (!target) return;
    this.sendTo(target, { t: "signal", from: client.role === "pc" ? "pc" : client.id, data: msg.data });
  }

  onBinary(client, data) {
    // JPEG fallback: PC window -> iPhone of the current session
    if (client.role !== "pc") return;
    const device = this.clients.get(this.state.session.deviceId);
    if (device && device.ws.readyState === 1) device.ws.send(data, { binary: true });
  }

  onClose(client) {
    this.clients.delete(client.id);
    if (client.role === "ios" && this.state.session.deviceId === client.id) this.endSession(null);
    else if (client.role === "pc" && this.state.session.mode) this.endSession(null);
    this.syncDevices();
    this.emit("client-left", client);
  }

  endSession(reason) {
    this.state.session = {
      mode: null,
      deviceId: null,
      displayId: this.state.session.displayId,
      status: "idle",
      transport: null,
      startedAt: null,
      error: reason && reason !== "Đã dừng" ? reason : null,
    };
    this.state.stats = { fps: 0, kbps: 0, rttMs: 0, width: 0, height: 0 };
    this.broadcastState();
  }

  // ------------------------------------------------------------------ helpers

  pcClient() {
    for (const c of this.clients.values()) if (c.role === "pc") return c;
    return null;
  }

  syncDevices() {
    this.state.devices = [...this.clients.values()]
      .filter((c) => c.role === "ios")
      .map((c) => ({ id: c.id, name: c.name, model: c.model, ip: c.ip, caps: c.caps }));
    this.broadcastState();
  }

  setDisplays(displays) {
    this.state.displays = displays;
    if (!displays.some((d) => d.id === this.state.session.displayId)) {
      this.state.session.displayId = displays[0]?.id ?? null;
    }
    this.broadcastState();
  }

  setAddresses(addresses) {
    this.state.pc.addresses = addresses;
    const ip = addresses[0] || "127.0.0.1";
    this.state.pc.url = `http://${ip}:${this.state.pc.port}/?k=${this.state.pc.token}`;
    this.broadcastState();
  }

  sendTo(client, msg) {
    if (client && client.ws.readyState === 1) client.ws.send(JSON.stringify(msg));
  }

  broadcastState() {
    this.state.updatedAt = Date.now();
    const payload = JSON.stringify({ t: "state", state: this.state });
    for (const c of this.clients.values()) if (c.role && c.ws.readyState === 1) c.ws.send(payload);
    this.emit("state", this.state);
  }
}

module.exports = { HandshakeServer };
