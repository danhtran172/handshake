// PC window. Renders everything from the synced state and runs the media side:
//   display mode: capture a PC display and send it to the iPhone (WebRTC, JPEG fallback)
//   mirror mode:  receive the iPhone's stream and show it in the phone frame

const { el, settingsPanel, changedSettings, toast, applyTone, sessionPill, formatStats } = HandshakeUI;
const { QUALITY } = HandshakeState;

const $ = (id) => document.getElementById(id);
const PAGES = {
  home: ["Tổng quan", "Kết nối iPhone và chọn cách dùng"],
  display: ["Màn hình phụ", "Gửi một màn hình của PC sang iPhone, chạm trên iPhone để điều khiển"],
  mirror: ["Nhận từ iPhone", "Xem màn hình iPhone trên PC"],
  settings: ["Cài đặt", "Đồng bộ với iPhone theo thời gian thực"],
  perf: ["Hiệu năng", "Engine native: 120 fps, đo độ trễ thật, chuyển đường không gián đoạn"],
};

let info = null;
let client = null;
let state = null;
let page = "home";
let selectedDevice = null; // device id chosen on the Display/Mirror pages
let settingsUi = null;

// ------------------------------------------------------------------ navigation

function go(name) {
  page = name;
  for (const s of document.querySelectorAll(".page")) s.hidden = s.dataset.page !== name;
  for (const b of document.querySelectorAll(".nav-item")) {
    if (b.dataset.go === name) b.setAttribute("aria-current", "page");
    else b.removeAttribute("aria-current");
  }
  $("pageTitle").innerHTML = `<span class="underline">${PAGES[name][0]}</span>`;
  $("pageSub").textContent = PAGES[name][1];
}

for (const b of document.querySelectorAll(".nav-item")) b.addEventListener("click", () => go(b.dataset.go));

// ------------------------------------------------------------------ rendering

function render(prev) {
  applyTone(state.settings);
  settingsUi.update(state.settings, changedSettings(prev, state));

  const n = state.devices.length;
  const dp = $("devicePill");
  dp.className = "pill " + (n ? "ok" : "");
  dp.innerHTML = `<span class="dot"></span>${n} iPhone`;
  sessionPill($("sessionPill"), state.session);

  if (!state.devices.some((d) => d.id === selectedDevice)) selectedDevice = state.devices[0]?.id ?? null;

  renderDevices();
  renderDisplayPage();
  renderMirrorPage();
  renderPcInfo();

  for (const b of document.querySelectorAll(".mode-card")) b.disabled = !n;
  for (const b of document.querySelectorAll(".nav-item .badge")) b.remove();
  if (state.session.mode) {
    const nav = document.querySelector(`.nav-item[data-go="${state.session.mode}"]`);
    nav.append(el("span", { class: "badge" }));
  }
}

function deviceLabel(d) {
  return d.model ? `${d.model} · ${d.ip}` : d.ip;
}

function renderDevices() {
  const list = $("deviceList");
  list.textContent = "";
  $("deviceCount").textContent = state.devices.length ? `${state.devices.length} thiết bị` : "";
  if (!state.devices.length) {
    list.append(
      el("div", { class: "empty-list" }, [
        svg(`<svg viewBox="0 0 120 60"><rect x="44" y="6" width="30" height="48" rx="6" class="sk"/><path d="M14 30c8-10 16-10 24 0M82 30c8-10 16-10 24 0" class="sk" stroke-dasharray="3 5"/></svg>`),
        document.createTextNode("Chưa có iPhone nào. Quét mã QR bên trái."),
      ])
    );
    return;
  }
  for (const d of state.devices) {
    const live = state.session.deviceId === d.id;
    list.append(
      el("div", { class: "row" }, [
        el("div", { class: "device-icon" + (live ? " live" : "") }),
        el("div", { class: "grow" }, [el("div", { class: "title", text: d.name }), el("div", { class: "sub", text: deviceLabel(d) })]),
        el("div", { class: "row-actions" }, live
          ? [el("button", { class: "btn small danger", text: "Dừng", onclick: stopSession })]
          : [
              el("button", { class: "btn small", text: "Màn hình phụ", onclick: () => startSession("display", d.id) }),
              el("button", { class: "btn small", text: "Nhận màn hình", onclick: () => startSession("mirror", d.id) }),
            ]),
      ])
    );
  }
}

function renderTargets(container, name) {
  container.textContent = "";
  if (!state.devices.length) {
    container.append(el("p", { class: "faint", text: "Chưa có iPhone nào kết nối." }));
    return;
  }
  for (const d of state.devices) {
    const input = el("input", { type: "radio", name, value: d.id });
    input.checked = d.id === selectedDevice;
    input.addEventListener("change", () => {
      selectedDevice = d.id;
      render(state);
    });
    container.append(
      el("label", { class: "target-option" }, [
        input,
        el("div", { class: "grow" }, [el("div", { class: "serif", text: d.name }), el("div", { class: "faint", text: deviceLabel(d) })]),
      ])
    );
  }
}

function renderDisplayPage() {
  const map = $("displayMap");
  map.textContent = "";
  const ds = state.displays;
  if (ds.length) {
    const minX = Math.min(...ds.map((d) => d.bounds.x));
    const minY = Math.min(...ds.map((d) => d.bounds.y));
    const maxX = Math.max(...ds.map((d) => d.bounds.x + d.bounds.width));
    const maxY = Math.max(...ds.map((d) => d.bounds.y + d.bounds.height));
    const W = map.clientWidth || 340;
    const H = map.clientHeight || 190;
    const scale = Math.min((W - 30) / (maxX - minX), (H - 30) / (maxY - minY));
    const offX = (W - (maxX - minX) * scale) / 2;
    const offY = (H - (maxY - minY) * scale) / 2;
    ds.forEach((d, i) => {
      const box = el("button", {
        class: "display-box",
        "aria-pressed": String(d.id === state.session.displayId),
        title: d.label,
        onclick: () => client.send({ t: "display.select", displayId: d.id }),
      }, [
        el("span", { class: "num", text: String(i + 1) }),
        document.createTextNode(d.primary ? "Chính" : `Màn ${i + 1}`),
        el("small", { text: `${d.pixels.width}×${d.pixels.height}` }),
      ]);
      Object.assign(box.style, {
        left: `${offX + (d.bounds.x - minX) * scale}px`,
        top: `${offY + (d.bounds.y - minY) * scale}px`,
        width: `${d.bounds.width * scale - 4}px`,
        height: `${d.bounds.height * scale - 4}px`,
      });
      map.append(box);
    });
  }
  const chosen = ds.find((d) => d.id === state.session.displayId);
  $("displayHint").textContent = chosen
    ? `${chosen.label} · ${chosen.pixels.width}×${chosen.pixels.height} · tỉ lệ ${Math.round(chosen.scaleFactor * 100)}%`
    : "";
  $("vddTip").hidden = ds.length > 1;

  renderTargets($("displayTargets"), "displayTarget");
  const active = state.session.mode === "display";
  $("btnDisplayStart").hidden = active;
  $("btnDisplayStop").hidden = !active;
  $("btnDisplayStart").disabled = !selectedDevice || !!state.session.mode;
  $("displayStats").textContent = active ? formatStats(state.stats, state.session.transport) : "";
}

function renderMirrorPage() {
  renderTargets($("mirrorTargets"), "mirrorTarget");
  const active = state.session.mode === "mirror";
  $("btnMirrorStart").disabled = !selectedDevice || !!state.session.mode;
  $("btnMirrorStop").hidden = !active;
  $("mirrorStats").textContent = active ? formatStats(state.stats, state.session.transport) : "";
  const device = state.devices.find((d) => d.id === (active ? state.session.deviceId : selectedDevice));
  $("mirrorEmpty").querySelector(".faint").textContent =
    device && !device.caps?.captureStream
      ? "Safari của iPhone này chưa hỗ trợ phát demo"
      : "Trên iPhone chọn “Phát lên PC”, hoặc bấm “Yêu cầu iPhone phát”";
}

function renderPcInfo() {
  $("pcName").textContent = state.pc.name;
  $("pcAddrs").textContent = state.pc.addresses.map((a) => `${a}:${state.pc.port}`).join(", ") || "Không thấy mạng LAN";
  $("pcDisplays").textContent = state.displays.map((d) => `${d.label} (${d.pixels.width}×${d.pixels.height})`).join(", ");
}

function svg(markup) {
  const t = document.createElement("template");
  t.innerHTML = markup.trim();
  return t.content.firstChild;
}

// ------------------------------------------------------------------ session control

function startSession(mode, deviceId = selectedDevice) {
  if (!deviceId) return toast("Chưa có iPhone nào kết nối");
  client.send({ t: "session.start", mode, deviceId, displayId: state.session.displayId });
  go(mode);
}

function stopSession() {
  client.send({ t: "session.stop" });
}

$("btnDisplayStart").addEventListener("click", () => startSession("display"));
$("btnDisplayStop").addEventListener("click", stopSession);
$("btnMirrorStart").addEventListener("click", () => startSession("mirror"));
$("btnMirrorStop").addEventListener("click", stopSession);
for (const b of document.querySelectorAll(".mode-card")) {
  b.addEventListener("click", () => (state.devices.length > 1 ? go(b.dataset.mode) : startSession(b.dataset.mode)));
}

// ------------------------------------------------------------------ media engine

const media = {
  key: null, // `${mode}:${deviceId}:${displayId}:${transport}` of the running pipeline
  stream: null,
  peer: null,
  jpeg: null,
  statsTimer: null,
  fallbackTimer: null,
  pendingSignals: [],
  recorder: null,
};

function stopMedia() {
  clearInterval(media.statsTimer);
  clearTimeout(media.fallbackTimer);
  media.jpeg?.stop();
  media.peer?.close();
  media.stream?.getTracks().forEach((t) => t.stop());
  if (media.recorder?.state === "recording") media.recorder.stop();
  Object.assign(media, { key: null, stream: null, peer: null, jpeg: null, statsTimer: null, fallbackTimer: null, pendingSignals: [] });
  for (const id of ["displayPreview", "mirrorVideo"]) $(id).srcObject = null;
  $("displayPreview").closest(".phone-frame").classList.remove("has-video");
  $("mirrorFrame").classList.remove("has-video");
}

function syncMedia() {
  const s = state.session;
  if (!s.mode) {
    if (media.key) stopMedia();
    return;
  }
  if (s.mode === "display") {
    const key = `display:${s.deviceId}:${s.displayId}:${state.settings.transport}`;
    if (media.key !== key) {
      stopMedia();
      media.key = key;
      startDisplay().catch((err) => fail(err));
    } else if (media.peer) {
      const q = QUALITY[state.settings.quality];
      media.peer.setLimits({ kbps: q.kbps, fps: state.settings.fps });
    }
  } else if (s.mode === "mirror") {
    const key = `mirror:${s.deviceId}`;
    if (media.key !== key) {
      stopMedia();
      media.key = key;
    }
  }
}

function fail(err) {
  console.error(err);
  client.send({ t: "session.status", status: "error", error: err?.message || String(err) });
  toast("Lỗi: " + (err?.message || err));
}

// ---- display mode: PC -> iPhone

async function startDisplay() {
  const s = state.session;
  const key = media.key;
  await hs.selectDisplay(s.displayId);
  const stream = await navigator.mediaDevices.getDisplayMedia({
    video: { frameRate: { ideal: state.settings.fps, max: 60 }, cursor: state.settings.showCursor ? "always" : "never" },
    audio: false,
  });
  if (media.key !== key) return stream.getTracks().forEach((t) => t.stop());
  media.stream = stream;
  const preview = $("displayPreview");
  preview.srcObject = stream;
  preview.closest(".phone-frame").classList.add("has-video");

  const device = state.devices.find((d) => d.id === s.deviceId);
  const transport = state.settings.transport;
  const canRtc = device?.caps?.webrtc && HandshakeRTC.supported();
  if (transport === "jpeg" || (transport === "auto" && !canRtc)) return startJpeg(stream, s.deviceId);
  if (!canRtc) throw new Error("iPhone này không hỗ trợ WebRTC, hãy chọn đường truyền JPEG");

  const q = QUALITY[state.settings.quality];
  const peer = new HandshakeRTC.Peer({
    signal: (data) => client.signal(s.deviceId, data),
    onState: (st) => {
      if (st === "connected") {
        clearTimeout(media.fallbackTimer);
        client.send({ t: "session.status", status: "live", transport: "webrtc" });
      }
      if (st === "failed" && state.settings.transport === "auto") switchToJpeg();
    },
  });
  media.peer = peer;
  client.signal(s.deviceId, { transport: "webrtc" });
  await peer.sendStream(stream, { kbps: q.kbps, fps: state.settings.fps });
  media.statsTimer = setInterval(async () => {
    const st = await peer.stats();
    if (st) client.send({ t: "stats", ...st });
  }, 1000);
  if (transport === "auto") media.fallbackTimer = setTimeout(switchToJpeg, 8000);
}

function switchToJpeg() {
  if (!media.stream || media.jpeg) return;
  clearInterval(media.statsTimer);
  media.peer?.close();
  media.peer = null;
  toast("WebRTC không kết nối được, chuyển sang JPEG");
  startJpeg(media.stream, state.session.deviceId);
}

// JPEG over the hub WebSocket, one frame in flight (same idea as spacedesk's ACK flow control)
function startJpeg(stream, deviceId) {
  client.signal(deviceId, { transport: "jpeg" });
  const video = document.createElement("video");
  video.muted = true;
  video.srcObject = stream;
  video.play();
  const canvas = document.createElement("canvas");
  const ctx = canvas.getContext("2d", { alpha: false });
  let running = true;
  let waitingAck = false;
  let ackTimer = null;
  let frames = 0;
  let bytes = 0;
  let lastReport = performance.now();

  const onAck = () => {
    waitingAck = false;
    clearTimeout(ackTimer);
  };
  client.on("ack", onAck);

  async function tick() {
    if (!running) return;
    const started = performance.now();
    if (!waitingAck && video.videoWidth && client.bufferedAmount < 2_000_000) {
      const q = QUALITY[state.settings.quality];
      const scale = Math.min(q.scale, 1920 / video.videoWidth);
      canvas.width = Math.round(video.videoWidth * scale);
      canvas.height = Math.round(video.videoHeight * scale);
      ctx.drawImage(video, 0, 0, canvas.width, canvas.height);
      const blob = await new Promise((r) => canvas.toBlob(r, "image/jpeg", q.jpeg));
      if (blob && running) {
        const buf = await blob.arrayBuffer();
        client.sendBinary(buf);
        waitingAck = true;
        ackTimer = setTimeout(() => (waitingAck = false), 1000);
        frames++;
        bytes += buf.byteLength;
        if (frames === 1) client.send({ t: "session.status", status: "live", transport: "jpeg" });
      }
    }
    const now = performance.now();
    if (now - lastReport >= 1000) {
      const sec = (now - lastReport) / 1000;
      client.send({ t: "stats", fps: frames / sec, kbps: (bytes * 8) / 1000 / sec, width: canvas.width, height: canvas.height, rttMs: 0 });
      frames = 0;
      bytes = 0;
      lastReport = now;
    }
    const interval = 1000 / state.settings.fps;
    setTimeout(tick, Math.max(0, interval - (performance.now() - started)));
  }
  tick();

  media.jpeg = {
    stop() {
      running = false;
      clearTimeout(ackTimer);
      const list = client.handlers.ack || [];
      const i = list.indexOf(onAck);
      if (i >= 0) list.splice(i, 1);
    },
  };
}

// ---- mirror mode: iPhone -> PC (iPhone sends the offer)

async function onSignal(msg) {
  const s = state?.session;
  if (!s?.mode || msg.from !== s.deviceId) return;
  if (s.mode === "display") {
    return media.peer?.handleSignal(msg.data);
  }
  if (s.mode !== "mirror") return;
  if (!media.peer) {
    if (!msg.data.sdp) {
      media.pendingSignals.push(msg.data);
      return;
    }
    const video = $("mirrorVideo");
    media.peer = new HandshakeRTC.Peer({
      signal: (data) => client.signal(s.deviceId, data),
      onTrack: (stream) => {
        media.stream = stream;
        video.srcObject = stream;
        $("mirrorFrame").classList.add("has-video");
      },
      onState: (st) => {
        if (st === "connected") client.send({ t: "session.status", status: "live", transport: "webrtc" });
        if (st === "failed") fail(new Error("Không nhận được hình từ iPhone"));
      },
    });
    media.statsTimer = setInterval(async () => {
      const st = await media.peer?.stats();
      if (st) client.send({ t: "stats", ...st });
    }, 1000);
  }
  await media.peer.handleSignal(msg.data);
  for (const d of media.pendingSignals.splice(0)) await media.peer.handleSignal(d);
}

// Shape the phone frame after the video it shows (orientation + exact aspect ratio, 12px bezel)
function fitFrame(video, frame) {
  if (!video.videoWidth) return;
  const bezel = 24; // 12px padding on each side
  const r = video.videoWidth / video.videoHeight;
  frame.classList.toggle("landscape", r > 1);
  frame.style.removeProperty("--ratio");
  const box = frame.getBoundingClientRect();
  // Portrait frames have a fixed height, landscape frames a fixed width
  const ratio = r > 1 ? box.width / ((box.width - bezel) / r + bezel) : ((box.height - bezel) * r + bezel) / box.height;
  frame.style.setProperty("--ratio", String(ratio));
}
for (const id of ["mirrorVideo", "displayPreview"]) {
  $(id).addEventListener("resize", (e) => fitFrame(e.target, e.target.closest(".phone-frame")));
}

$("btnRotate").addEventListener("click", () => {
  const frame = $("mirrorFrame");
  frame.style.removeProperty("--ratio");
  frame.classList.toggle("landscape");
});
$("btnFull").addEventListener("click", () => $("mirrorFrame").requestFullscreen?.());
$("btnShot").addEventListener("click", async () => {
  const v = $("mirrorVideo");
  if (!v.videoWidth) return toast("Chưa có hình để chụp");
  const c = document.createElement("canvas");
  c.width = v.videoWidth;
  c.height = v.videoHeight;
  c.getContext("2d").drawImage(v, 0, 0);
  const file = await hs.saveScreenshot(c.toDataURL("image/png"));
  toast("Đã lưu " + file.split(/[\\/]/).pop());
});
$("btnRecord").addEventListener("click", () => {
  const btn = $("btnRecord");
  if (media.recorder?.state === "recording") {
    media.recorder.stop();
    return;
  }
  if (!media.stream) return toast("Chưa có hình để quay");
  const chunks = [];
  const rec = new MediaRecorder(media.stream, { mimeType: "video/webm;codecs=vp9" });
  rec.ondataavailable = (e) => e.data.size && chunks.push(e.data);
  rec.onstop = () => {
    btn.textContent = "Quay video";
    const url = URL.createObjectURL(new Blob(chunks, { type: "video/webm" }));
    const a = el("a", { href: url, download: `iphone-${Date.now()}.webm` });
    a.click();
    setTimeout(() => URL.revokeObjectURL(url), 10000);
    toast("Đã lưu video");
  };
  rec.start(1000);
  media.recorder = rec;
  btn.innerHTML = '<span class="rec-dot">●</span> Dừng quay';
});

// ------------------------------------------------------------------ boot

function applyInfo(i) {
  info = i;
  $("qr").src = i.qr;
  $("url").textContent = i.url;
  $("token").textContent = i.token;
}

async function boot() {
  applyInfo(await hs.info());
  hs.onInfo(applyInfo);
  settingsUi = settingsPanel($("settingsPanel"), (path, value) => client.set(path, value));

  client = new HandshakeClient(info.wsUrl, { role: "pc" });
  client.on("connection", (st) => {
    $("serverDot").classList.toggle("on", st === "open");
    $("serverText").textContent = st === "open" ? `Đang mở cổng ${info.port}` : "Mất kết nối máy chủ";
  });
  client.on("state", (next, prev) => {
    state = next;
    window.__hsState = next; // read by perf.js
    // Follow a session started from the iPhone
    if (next.session.mode && next.session.mode !== prev?.session.mode && page !== next.session.mode) go(next.session.mode);
    if (prev && next.devices.length > prev.devices.length) {
      const d = next.devices.find((x) => !prev.devices.some((p) => p.id === x.id));
      if (d) toast(`${d.name} đã kết nối`);
    }
    if (next.session.error && next.session.error !== prev?.session.error) toast(next.session.error);
    render(prev);
    syncMedia();
  });
  client.on("signal", (msg) => onSignal(msg).catch(fail));
  client.on("error", (msg) => toast(msg.message));
  client.connect();

  $("btnCopy").addEventListener("click", () => {
    navigator.clipboard.writeText(info.url);
    toast("Đã chép địa chỉ");
  });
  $("btnMockup").addEventListener("click", () => hs.openMockup());
  $("linkMockup").addEventListener("click", (e) => {
    e.preventDefault();
    hs.openMockup();
  });
  $("btnPreview").addEventListener("click", () => hs.openPhonePreview());
  window.addEventListener("resize", () => state && renderDisplayPage());
  if (info.page && PAGES[info.page]) go(info.page);
}

boot();
