// iPhone web client (Safari). Screens are driven by the synced session state:
//   home    - choose a mode, synced settings
//   viewer  - PC display on the iPhone (display mode), touch -> mouse input
//   mirror  - demo sender (mirror mode); the native app will use ReplayKit instead

const { settingsPanel, changedSettings, toast, applyTone, sessionPill, formatStats } = HandshakeUI;
const { QUALITY } = HandshakeState;
const $ = (id) => document.getElementById(id);

const params = new URLSearchParams(location.search);
const token = params.get("k") || sessionStorage.getItem("hs.k") || "";
if (token) sessionStorage.setItem("hs.k", token);

function defaultName() {
  const ua = navigator.userAgent;
  if (/iPhone/.test(ua)) return "iPhone";
  if (/iPad/.test(ua)) return "iPad";
  if (/Android/.test(ua)) return "Điện thoại Android";
  return "Trình duyệt";
}

function modelString() {
  const ua = navigator.userAgent;
  const ios = ua.match(/OS (\d+)_(\d+)/);
  if (/iPhone|iPad/.test(ua) && ios) return `${/iPad/.test(ua) ? "iPad" : "iPhone"} · iOS ${ios[1]}.${ios[2]}`;
  if (/Edg\//.test(ua)) return "Edge";
  if (/Chrome\//.test(ua)) return "Chrome";
  if (/Safari\//.test(ua)) return "Safari";
  return "";
}

let deviceName = params.get("name") || localStorage.getItem("hs.name") || defaultName();
let state = null;
let screenName = "home";
let settingsUi = null;

const client = new HandshakeClient(`${location.protocol === "https:" ? "wss" : "ws"}://${location.host}/ws`, {
  role: "ios",
  token,
  name: deviceName,
  model: modelString(),
  caps: {
    webrtc: HandshakeRTC.supported(),
    captureStream: typeof HTMLCanvasElement !== "undefined" && "captureStream" in HTMLCanvasElement.prototype,
    screen: { w: screen.width, h: screen.height, dpr: devicePixelRatio || 1 },
  },
});

// ------------------------------------------------------------------ screens

function show(name) {
  if (screenName === name) return;
  const prev = screenName;
  screenName = name;
  for (const id of ["home", "viewer", "mirror"]) $(id).hidden = id !== name;
  if (prev === "viewer") viewer.stop();
  if (prev === "mirror") sender.stop();
  if (name === "viewer") viewer.start();
  if (name === "mirror") sender.start();
}

function route() {
  const s = state.session;
  const mine = s.deviceId === client.id;
  if (mine && s.mode === "display") show("viewer");
  else if (mine && s.mode === "mirror") show("mirror");
  else show("home");
}

function render(prev) {
  applyTone(state.settings);
  settingsUi.update(state.settings, changedSettings(prev, state));
  $("pcName").textContent = state.pc.name || "PC";
  const s = state.session;
  const mine = s.deviceId === client.id;
  const busy = s.mode && !mine;
  $("pcSub").textContent = busy ? "PC đang bận với thiết bị khác" : `${state.pc.addresses[0] || location.hostname} · ${state.displays.length} màn hình`;
  sessionPill($("sessionPill"), mine ? s : { ...s, mode: null, status: "idle" });
  for (const b of document.querySelectorAll(".mode-card")) b.disabled = !client.open || busy;
  for (const b of $("touchSeg").children) b.setAttribute("aria-pressed", String(b.dataset.value === state.settings.touchMode));
  $("viewerStats").textContent = mine && s.mode === "display" ? formatStats(state.stats, s.transport) : "";
  $("mirrorStats").textContent = mine && s.mode === "mirror" ? formatStats(state.stats, s.transport) : "";
  sessionPill($("mirrorPill"), s);
  if (mine && s.status === "error" && s.error) toast(s.error);
}

// ------------------------------------------------------------------ viewer: PC display -> iPhone

const viewer = {
  peer: null,
  transport: null,
  wakeLock: null,
  pending: [],

  start() {
    this.transport = null;
    $("viewerWait").hidden = false;
    $("viewerWaitSub").textContent = "Chờ PC bắt đầu phát";
    $("remoteVideo").hidden = false;
    $("jpegCanvas").hidden = true;
    showToolbar();
    navigator.wakeLock?.request("screen").then((l) => (this.wakeLock = l)).catch(() => {});
  },

  stop() {
    this.peer?.close();
    this.peer = null;
    this.pending = [];
    $("remoteVideo").srcObject = null;
    this.wakeLock?.release().catch(() => {});
    this.wakeLock = null;
  },

  async onSignal(data) {
    if (data.transport) {
      this.transport = data.transport;
      if (data.transport === "jpeg") {
        this.peer?.close();
        this.peer = null;
        $("remoteVideo").hidden = true;
        $("jpegCanvas").hidden = false;
        $("viewerWaitSub").textContent = "Đang nhận hình (JPEG)…";
      } else {
        $("viewerWaitSub").textContent = "Đang kết nối WebRTC…";
      }
      return;
    }
    if (!this.peer) {
      if (!data.sdp) return this.pending.push(data);
      const video = $("remoteVideo");
      this.peer = new HandshakeRTC.Peer({
        signal: (d) => client.signal("pc", d),
        onTrack: (stream) => {
          video.srcObject = stream;
          video.play().catch(() => {});
        },
      });
    }
    await this.peer.handleSignal(data);
    for (const d of this.pending.splice(0)) await this.peer.handleSignal(d);
  },

  async onJpeg(buf) {
    if (screenName !== "viewer") return;
    const canvas = $("jpegCanvas");
    const blob = new Blob([buf], { type: "image/jpeg" });
    try {
      let img;
      if (typeof createImageBitmap === "function") img = await createImageBitmap(blob);
      else {
        img = new Image();
        img.src = URL.createObjectURL(blob);
        await img.decode();
        URL.revokeObjectURL(img.src);
      }
      if (canvas.width !== img.width || canvas.height !== img.height) {
        canvas.width = img.width;
        canvas.height = img.height;
      }
      canvas.getContext("2d").drawImage(img, 0, 0);
      img.close?.();
      $("viewerWait").hidden = true;
    } finally {
      client.send({ t: "ack" });
    }
  },
};

$("remoteVideo").addEventListener("playing", () => ($("viewerWait").hidden = true));
$("btnExit").addEventListener("click", () => client.send({ t: "session.stop" }));
for (const b of $("touchSeg").children) b.addEventListener("click", () => client.set("settings.touchMode", b.dataset.value));

let toolbarTimer = null;
function showToolbar() {
  $("toolbar").classList.add("show");
  clearTimeout(toolbarTimer);
  toolbarTimer = setTimeout(() => $("toolbar").classList.remove("show"), 3500);
}
$("toolbarHandle").addEventListener("click", showToolbar);

// ------------------------------------------------------------------ touch -> PC mouse

// Rectangle where the picture is actually drawn inside the viewer (object-fit: contain)
function contentRect() {
  const layer = $("touchLayer").getBoundingClientRect();
  const video = $("remoteVideo");
  const canvas = $("jpegCanvas");
  const w = !video.hidden && video.videoWidth ? video.videoWidth : canvas.width;
  const h = !video.hidden && video.videoHeight ? video.videoHeight : canvas.height;
  if (!w || !h) return layer;
  const scale = Math.min(layer.width / w, layer.height / h);
  const cw = w * scale;
  const ch = h * scale;
  return { left: layer.left + (layer.width - cw) / 2, top: layer.top + (layer.height - ch) / 2, width: cw, height: ch };
}

function norm(t) {
  const r = contentRect();
  return { x: (t.clientX - r.left) / r.width, y: (t.clientY - r.top) / r.height };
}

function sendInput(msg) {
  client.send({ t: "input", kind: "pointer", ...msg });
}

const gesture = { kind: "none", start: null, last: null, startTime: 0, timer: null, moved: 0, mid: null };
let moveQueued = null;

function flushMove() {
  if (moveQueued) sendInput(moveQueued);
  moveQueued = null;
}

function queueMove(msg) {
  if (msg.action === "rel" && moveQueued?.action === "rel") {
    moveQueued.dx += msg.dx;
    moveQueued.dy += msg.dy;
    return;
  }
  if (!moveQueued) requestAnimationFrame(flushMove);
  moveQueued = msg;
}

const layer = $("touchLayer");

layer.addEventListener("touchstart", (e) => {
  e.preventDefault();
  const trackpad = state?.settings.touchMode === "trackpad";
  if (e.touches.length === 1) {
    const t = e.touches[0];
    Object.assign(gesture, { kind: "pending", start: t, last: { x: t.clientX, y: t.clientY }, startTime: Date.now(), moved: 0 });
    clearTimeout(gesture.timer);
    if (!trackpad) {
      // Holding still starts a drag (select text, move windows)
      gesture.timer = setTimeout(() => {
        if (gesture.kind !== "pending") return;
        gesture.kind = "drag";
        sendInput({ action: "down", ...norm(gesture.start) });
        navigator.vibrate?.(10);
      }, 450);
    }
  } else if (e.touches.length === 2) {
    clearTimeout(gesture.timer);
    if (gesture.kind === "drag") sendInput({ action: "up" });
    const [a, b] = e.touches;
    Object.assign(gesture, {
      kind: "two",
      startTime: Date.now(),
      moved: 0,
      mid: { x: (a.clientX + b.clientX) / 2, y: (a.clientY + b.clientY) / 2 },
      start: a,
    });
  }
}, { passive: false });

layer.addEventListener("touchmove", (e) => {
  e.preventDefault();
  const trackpad = state?.settings.touchMode === "trackpad";
  if (gesture.kind === "two" && e.touches.length >= 2) {
    const [a, b] = e.touches;
    const mid = { x: (a.clientX + b.clientX) / 2, y: (a.clientY + b.clientY) / 2 };
    const dx = mid.x - gesture.mid.x;
    const dy = mid.y - gesture.mid.y;
    gesture.moved += Math.abs(dx) + Math.abs(dy);
    gesture.mid = mid;
    // Natural scrolling: fingers down = content down = wheel up
    sendInput({ action: "wheel", dy: -dy * 3, dx: -dx * 3 });
    return;
  }
  const t = e.touches[0];
  if (!t || gesture.kind === "none") return;
  const dx = t.clientX - gesture.last.x;
  const dy = t.clientY - gesture.last.y;
  gesture.last = { x: t.clientX, y: t.clientY };
  gesture.moved += Math.abs(dx) + Math.abs(dy);
  if (trackpad) {
    const r = contentRect();
    const accel = 1.2 + Math.min(1.6, Math.hypot(dx, dy) / 12);
    queueMove({ action: "rel", dx: (dx * accel) / r.width, dy: (dy * accel) / r.height });
    return;
  }
  if (gesture.kind === "pending" && gesture.moved > 8) {
    clearTimeout(gesture.timer);
    gesture.kind = "drag";
    sendInput({ action: "down", ...norm(gesture.start) });
  }
  if (gesture.kind === "drag") queueMove({ action: "move", ...norm(t) });
}, { passive: false });

layer.addEventListener("touchend", (e) => {
  e.preventDefault();
  clearTimeout(gesture.timer);
  const trackpad = state?.settings.touchMode === "trackpad";
  const quick = Date.now() - gesture.startTime < 300;
  if (gesture.kind === "two") {
    if (e.touches.length === 0) {
      if (quick && gesture.moved < 12) sendInput(trackpad ? { action: "tap", button: 2 } : { action: "tap", button: 2, ...norm(gesture.start) });
      gesture.kind = "none";
    }
    return;
  }
  const t = e.changedTouches[0];
  if (gesture.kind === "pending") {
    if (trackpad) {
      if (quick && gesture.moved < 10) sendInput({ action: "tap" });
    } else {
      sendInput({ action: "tap", ...norm(t) });
    }
  } else if (gesture.kind === "drag") {
    flushMove();
    sendInput({ action: "up", ...norm(t) });
  }
  if (e.touches.length === 0) gesture.kind = "none";
}, { passive: false });

layer.addEventListener("touchcancel", () => {
  if (gesture.kind === "drag") sendInput({ action: "up" });
  gesture.kind = "none";
});

// Mouse support, for testing the client in a desktop browser
let mouseDown = false;
layer.addEventListener("mousedown", (e) => {
  mouseDown = true;
  sendInput({ action: "down", button: e.button, ...norm(e) });
});
layer.addEventListener("mousemove", (e) => mouseDown && queueMove({ action: "move", ...norm(e) }));
layer.addEventListener("mouseup", (e) => {
  mouseDown = false;
  sendInput({ action: "up", button: e.button, ...norm(e) });
});
layer.addEventListener("wheel", (e) => sendInput({ action: "wheel", dy: -e.deltaY, dx: e.deltaX }), { passive: true });
layer.addEventListener("contextmenu", (e) => e.preventDefault());

// ------------------------------------------------------------------ mirror: demo sender (iPhone -> PC)

const sender = {
  peer: null,
  stream: null,
  raf: 0,
  frame: 0,

  async start() {
    const canvas = $("demoCanvas");
    if (!("captureStream" in canvas) || !HandshakeRTC.supported()) {
      client.send({ t: "session.status", status: "error", error: "Safari trên máy này chưa hỗ trợ phát demo (cần iOS 15 trở lên)" });
      return;
    }
    this.draw();
    this.stream = canvas.captureStream(state.settings.fps);
    const q = QUALITY[state.settings.quality];
    this.peer = new HandshakeRTC.Peer({
      signal: (d) => client.signal("pc", d),
      onState: (st) => st === "failed" && client.send({ t: "session.status", status: "error", error: "PC không nhận được hình" }),
    });
    try {
      await this.peer.sendStream(this.stream, { kbps: q.kbps, fps: state.settings.fps });
    } catch (err) {
      client.send({ t: "session.status", status: "error", error: err.message });
    }
  },

  stop() {
    clearTimeout(this.raf);
    this.peer?.close();
    this.peer = null;
    this.stream?.getTracks().forEach((t) => t.stop());
    this.stream = null;
  },

  // Paper-style demo "home screen" so the PC has something moving to show
  draw() {
    const c = $("demoCanvas");
    const g = c.getContext("2d");
    const css = getComputedStyle(document.documentElement);
    const paper = css.getPropertyValue("--paper").trim() || "#f5efe1";
    const ink = css.getPropertyValue("--ink").trim() || "#2a2723";
    const accent = css.getPropertyValue("--accent").trim() || "#2f5fb3";
    const mark = "#ffe36e";
    const serif = css.getPropertyValue("--serif").trim();
    this.frame++;
    const t = this.frame / 60;
    g.fillStyle = paper;
    g.fillRect(0, 0, c.width, c.height);
    g.strokeStyle = "#9fb6d455";
    g.lineWidth = 1;
    for (let y = 120; y < c.height; y += 28) {
      g.beginPath();
      g.moveTo(0, y);
      g.lineTo(c.width, y);
      g.stroke();
    }
    g.fillStyle = ink;
    g.font = `600 17px ${css.getPropertyValue("--body")}`;
    const now = new Date();
    g.fillText(now.toLocaleTimeString("vi-VN", { hour: "2-digit", minute: "2-digit" }), 28, 40);
    g.font = `44px ${serif}`;
    g.fillText(now.toLocaleTimeString("vi-VN"), 40, 150);
    g.font = `20px ${serif}`;
    g.fillStyle = "#5b554c";
    g.fillText(`Demo từ ${deviceName}`, 40, 190);
    // App icons grid
    const icons = ["Ảnh", "Ghi chú", "Lịch", "Bản đồ", "Nhạc", "Thư", "Camera", "Cài đặt"];
    icons.forEach((label, i) => {
      const x = 40 + (i % 4) * 82;
      const y = 250 + Math.floor(i / 4) * 110;
      const wob = Math.sin(t * 2 + i) * 2;
      g.save();
      g.translate(x + 30, y + 30 + wob);
      g.rotate(Math.sin(t + i) * 0.03);
      g.fillStyle = i === Math.floor(t) % icons.length ? mark : paper;
      g.strokeStyle = ink;
      g.lineWidth = 2;
      roundRect(g, -30, -30, 60, 60, 16);
      g.fill();
      g.stroke();
      g.restore();
      g.fillStyle = ink;
      g.font = `14px ${serif}`;
      g.textAlign = "center";
      g.fillText(label, x + 30, y + 82);
      g.textAlign = "left";
    });
    // Pen squiggle
    g.strokeStyle = accent;
    g.lineWidth = 3;
    g.lineCap = "round";
    g.beginPath();
    for (let x = 30; x <= c.width - 30; x += 4) {
      const y = 560 + Math.sin(x / 26 + t * 3) * 18 + Math.sin(x / 9 - t * 5) * 4;
      if (x === 30) g.moveTo(x, y);
      else g.lineTo(x, y);
    }
    g.stroke();
    // Dock
    g.strokeStyle = ink;
    g.lineWidth = 2;
    g.fillStyle = "#ece3cf";
    roundRect(g, 20, 700, c.width - 40, 96, 26);
    g.fill();
    g.stroke();
    g.fillStyle = ink;
    g.font = `16px ${serif}`;
    g.fillText(`khung #${this.frame}`, 40, 755);
    // Timer instead of requestAnimationFrame: keeps frames flowing while the tab is not painted
    this.raf = setTimeout(() => this.draw(), 1000 / (state?.settings.fps || 30));
  },
};

function roundRect(g, x, y, w, h, r) {
  g.beginPath();
  g.moveTo(x + r, y);
  g.arcTo(x + w, y, x + w, y + h, r);
  g.arcTo(x + w, y + h, x, y + h, r);
  g.arcTo(x, y + h, x, y, r);
  g.arcTo(x, y, x + w, y, r);
  g.closePath();
}

$("btnMirrorStop").addEventListener("click", () => client.send({ t: "session.stop" }));

// ------------------------------------------------------------------ boot

settingsUi = settingsPanel($("settingsPanel"), (path, value) => client.set(path, value));
$("deviceName").value = deviceName;
$("deviceName").addEventListener("change", (e) => {
  deviceName = e.target.value.trim() || defaultName();
  localStorage.setItem("hs.name", deviceName);
  client.hello.name = deviceName;
  client.send({ t: "rename", name: deviceName });
});
for (const b of document.querySelectorAll(".mode-card")) {
  b.addEventListener("click", () => client.send({ t: "session.start", mode: b.dataset.mode }));
}

client.on("connection", (st, code) => {
  $("connDot").className = "status-dot " + (st === "open" ? "on" : "wait");
  if (st === "closed" && code === 4001) {
    $("pcName").textContent = "Mã kết nối không đúng";
    $("pcSub").textContent = "Quét lại mã QR trên PC";
  } else if (st !== "open") {
    $("pcSub").textContent = "Đang kết nối lại…";
  }
  if (state) render(state);
});
client.on("state", (next, prev) => {
  state = next;
  render(prev);
  route();
});
client.on("signal", (msg) => {
  if (msg.from !== "pc") return;
  if (screenName === "viewer") viewer.onSignal(msg.data).catch((e) => toast(e.message));
  else if (screenName === "mirror") sender.peer?.handleSignal(msg.data).catch((e) => toast(e.message));
});
client.on("binary", (buf) => viewer.onJpeg(buf));
client.on("error", (msg) => toast(msg.message));

if (!token) {
  $("pcName").textContent = "Thiếu mã kết nối";
  $("pcSub").textContent = "Mở lại bằng cách quét mã QR trên PC";
} else {
  client.connect();
}
