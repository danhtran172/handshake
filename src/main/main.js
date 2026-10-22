// Electron main process: window, hub server, display enumeration, screen-capture source
// selection and touch-to-mouse injection.

const { app, BrowserWindow, ipcMain, screen, session, desktopCapturer, shell } = require("electron");
const path = require("node:path");
const fs = require("node:fs");
const os = require("node:os");
const QRCode = require("qrcode");
const { HandshakeServer } = require("./server.js");
const { InputInjector } = require("./input.js");
const { Engine } = require("./engine.js");
const { validateSetting } = require("../shared/state.js");

const args = process.argv.slice(1);
const argValue = (name) => args.find((a) => a.startsWith(`--${name}=`))?.split("=").slice(1).join("=");
const DEV = args.includes("--dev");
const VERBOSE = DEV || args.includes("--verbose");
const SNAPSHOT = argValue("snapshot");
const PAGE = argValue("page");

// Show real LAN IPs in ICE candidates instead of mDNS ".local" names, so the iPhone
// can connect without resolving Chromium's random mDNS hostnames.
app.commandLine.appendSwitch("disable-features", "WebRtcHideLocalIpsWithMdns");

// A second launch (e.g. the shortcut clicked again) only focuses the running window
const GOT_LOCK = !!SNAPSHOT || app.requestSingleInstanceLock();
if (!GOT_LOCK) app.exit(0);

// Own taskbar identity + icon, so the window shows as Handshake instead of Electron
const ICON = path.join(__dirname, "..", "..", "assets", process.platform === "win32" ? "icon.ico" : "icon.png");
app.setAppUserModelId("Handshake.PC");

let win = null;
let server = null;
let pendingDisplayId = null;
const input = new InputInjector();
const engine = new Engine();
const pointer = { x: 0, y: 0, down: false };

// ------------------------------------------------------------------ network & displays

function lanAddresses() {
  const out = [];
  for (const [name, list] of Object.entries(os.networkInterfaces())) {
    for (const a of list || []) {
      if (a.family !== "IPv4" || a.internal || a.address.startsWith("169.254.")) continue;
      // Rank: home LAN ranges first, VPN/virtual adapters last
      const virtual = /tailscale|vpn|virtual|vethernet|hyper-v|wsl|vmware|virtualbox|loopback/i.test(name);
      const home = /^(192\.168\.|10\.|172\.(1[6-9]|2\d|3[01])\.)/.test(a.address);
      out.push({ address: a.address, score: (home ? 0 : 2) + (virtual ? 4 : 0) });
    }
  }
  return out.sort((a, b) => a.score - b.score).map((a) => a.address);
}

function displayList() {
  const primary = screen.getPrimaryDisplay().id;
  return screen.getAllDisplays().map((d, i) => ({
    id: String(d.id),
    label: d.label || `Màn hình ${i + 1}`,
    bounds: d.bounds,
    scaleFactor: d.scaleFactor,
    pixels: { width: Math.round(d.size.width * d.scaleFactor), height: Math.round(d.size.height * d.scaleFactor) },
    primary: d.id === primary,
    internal: !!d.internal,
    rotation: d.rotation,
  }));
}

function refreshDisplays() {
  server?.setDisplays(displayList());
}

// ------------------------------------------------------------------ touch -> mouse

function targetDisplay() {
  const id = server.state.session.displayId;
  return screen.getAllDisplays().find((d) => String(d.id) === id) || screen.getPrimaryDisplay();
}

function toPhysical(nx, ny) {
  const b = targetDisplay().bounds;
  const dip = {
    x: b.x + Math.min(1, Math.max(0, nx)) * (b.width - 1),
    y: b.y + Math.min(1, Math.max(0, ny)) * (b.height - 1),
  };
  return process.platform === "win32" ? screen.dipToScreenPoint(dip) : dip;
}

function handleInput(msg) {
  const button = msg.button === 2 ? 2 : msg.button === 1 ? 1 : 0;
  switch (msg.action) {
    case "down": {
      const p = toPhysical(msg.x, msg.y);
      Object.assign(pointer, p);
      input.move(p.x, p.y);
      input.down(button);
      pointer.down = true;
      break;
    }
    case "move": {
      const p = toPhysical(msg.x, msg.y);
      Object.assign(pointer, p);
      input.move(p.x, p.y);
      break;
    }
    case "up":
      if (Number.isFinite(msg.x)) {
        const p = toPhysical(msg.x, msg.y);
        input.move(p.x, p.y);
      }
      input.up(button);
      pointer.down = false;
      break;
    case "rel": {
      // Trackpad mode: dx/dy are fractions of the target display size
      const d = targetDisplay();
      const scale = d.scaleFactor;
      if (!pointer.init) {
        Object.assign(pointer, screen.dipToScreenPoint(screen.getCursorScreenPoint()), { init: true });
      }
      const b = d.bounds;
      const min = screen.dipToScreenPoint({ x: b.x, y: b.y });
      const max = screen.dipToScreenPoint({ x: b.x + b.width - 1, y: b.y + b.height - 1 });
      pointer.x = Math.min(max.x, Math.max(min.x, pointer.x + msg.dx * b.width * scale));
      pointer.y = Math.min(max.y, Math.max(min.y, pointer.y + msg.dy * b.height * scale));
      input.move(pointer.x, pointer.y);
      break;
    }
    case "tap":
      if (Number.isFinite(msg.x)) {
        const p = toPhysical(msg.x, msg.y);
        Object.assign(pointer, p);
        input.move(p.x, p.y);
      }
      input.down(button);
      input.up(button);
      break;
    case "wheel":
      if (msg.dy) input.wheel(-msg.dy);
      if (msg.dx) input.hwheel(msg.dx);
      break;
    default:
      break;
  }
}

// ------------------------------------------------------------------ window

async function infoPayload() {
  const st = server.state;
  return {
    port: st.pc.port,
    token: st.pc.token,
    url: st.pc.url,
    wsUrl: `ws://127.0.0.1:${st.pc.port}/ws`,
    qr: await QRCode.toDataURL(st.pc.url, { margin: 1, width: 360, color: { dark: "#2a2723", light: "#00000000" } }),
    addresses: st.pc.addresses,
    platform: process.platform,
    page: PAGE || null,
  };
}

function createWindow() {
  win = new BrowserWindow({
    width: 1220,
    height: 820,
    minWidth: 980,
    minHeight: 680,
    title: "Handshake",
    icon: ICON,
    backgroundColor: "#f5efe1",
    autoHideMenuBar: true,
    show: !SNAPSHOT,
    webPreferences: {
      preload: path.join(__dirname, "preload.js"),
      contextIsolation: true,
      nodeIntegration: false,
      backgroundThrottling: false,
    },
  });
  win.loadFile(path.join(__dirname, "..", "renderer", "index.html"));
  if (DEV) win.webContents.openDevTools({ mode: "detach" });
  win.webContents.on("console-message", (e) => {
    if (VERBOSE || e.level === "warning" || e.level === "error") console.log(`[renderer:${e.level}] ${e.message}`);
  });

  if (SNAPSHOT) {
    // --click=<elementId> --delay=<ms>: press a button before capturing (used to snapshot a finished benchmark)
    const click = argValue("click");
    const delay = Number(argValue("delay")) || 2500;
    win.webContents.once("did-finish-load", () => {
      if (click && /^[A-Za-z]+$/.test(click)) {
        setTimeout(() => win.webContents.executeJavaScript(`document.getElementById(${JSON.stringify(click)}).click()`), 1500);
      }
      setTimeout(async () => {
        const image = await win.webContents.capturePage();
        fs.mkdirSync(path.dirname(path.resolve(SNAPSHOT)), { recursive: true });
        fs.writeFileSync(path.resolve(SNAPSHOT), image.toPNG());
        console.log("snapshot saved", path.resolve(SNAPSHOT));
        app.quit();
      }, delay);
    });
  }
}

function openAuxWindow(url, opts) {
  const w = new BrowserWindow({
    icon: ICON,
    backgroundColor: "#f5efe1",
    autoHideMenuBar: true,
    ...opts,
    webPreferences: { contextIsolation: true, nodeIntegration: false, backgroundThrottling: false },
  });
  w.loadURL(url);
  return w;
}

// ------------------------------------------------------------------ app lifecycle

// Pairing token survives restarts so an iPhone added to the Home Screen reconnects on its own
function loadConfig() {
  const file = path.join(app.getPath("userData"), "handshake.json");
  let config = {};
  try {
    config = JSON.parse(fs.readFileSync(file, "utf8"));
  } catch {}
  if (!/^[0-9a-f]{8}$/.test(config.token || "")) {
    config.token = require("node:crypto").randomBytes(4).toString("hex");
    saveConfig(config);
  }
  return config;
}

function saveConfig(config) {
  const file = path.join(app.getPath("userData"), "handshake.json");
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, JSON.stringify(config, null, 2));
}

app.whenReady().then(async () => {
  if (!GOT_LOCK) return;
  const config = loadConfig();
  server = new HandshakeServer({ port: 7420, name: os.hostname(), token: config.token });
  // Restore synced settings and persist them whenever either side changes one
  for (const [key, value] of Object.entries(config.settings || {})) {
    if (validateSetting(key, value)) server.state.settings[key] = value;
  }
  let saveTimer = null;
  server.on("state", (st) => {
    if (JSON.stringify(st.settings) === JSON.stringify(config.settings)) return;
    config.settings = { ...st.settings };
    clearTimeout(saveTimer);
    saveTimer = setTimeout(() => saveConfig(config), 500);
  });
  await server.start();
  server.setAddresses(lanAddresses());
  refreshDisplays();
  server.on("input", handleInput);
  if (VERBOSE) {
    // GET /debug/snapshot?page=mirror -> PNG of the PC window (local only), for automated checks
    server.debugSnapshot = async (params, res) => {
      const page = params.get("page");
      if (page && /^[a-z]+$/.test(page)) await win.webContents.executeJavaScript(`go(${JSON.stringify(page)})`);
      const click = params.get("click");
      if (click && /^[A-Za-z]+$/.test(click)) await win.webContents.executeJavaScript(`document.getElementById(${JSON.stringify(click)}).click()`);
      await new Promise((r) => setTimeout(r, Math.min(30000, Number(params.get("wait")) || 700)));
      const image = await win.webContents.capturePage();
      res.writeHead(200, { "Content-Type": "image/png" });
      res.end(image.toPNG());
    };
  }

  // getDisplayMedia() in the renderer captures the display chosen via hs:select-display
  session.defaultSession.setDisplayMediaRequestHandler(async (_request, callback) => {
    try {
      const sources = await desktopCapturer.getSources({ types: ["screen"], thumbnailSize: { width: 0, height: 0 } });
      const source = sources.find((s) => s.display_id === pendingDisplayId) || sources[0];
      if (VERBOSE) console.log("[capture]", pendingDisplayId, "->", source?.id, source?.name);
      callback(source ? { video: source } : {});
    } catch (err) {
      console.error("[capture] failed", err);
      callback({});
    }
  });

  ipcMain.handle("hs:info", () => infoPayload());

  // Native engine (120 fps pipeline)
  const toWindow = (evt) => win?.webContents.send("hs:engine", evt);
  engine.on("bench-line", (e) => toWindow({ kind: "bench", ...e }));
  engine.on("host-event", (e) => toWindow({ kind: "host", ...e }));
  ipcMain.handle("hs:engine-devices", () => engine.devices());
  ipcMain.handle("hs:engine-bench", async (_e, opts) => {
    try {
      return { ok: true, ...(await engine.runBench(opts)) };
    } catch (err) {
      return { ok: false, error: err.message };
    }
  });
  ipcMain.handle("hs:engine-host", (_e, on, opts) => (on ? engine.startHost(opts || {}) : (engine.stopHost(), true)));
  ipcMain.handle("hs:select-display", (_e, displayId) => {
    pendingDisplayId = String(displayId);
    return true;
  });
  ipcMain.handle("hs:open-mockup", () => {
    openAuxWindow(`http://127.0.0.1:${server.state.pc.port}/mockup`, { width: 1400, height: 900, title: "Handshake – Mockup iOS" });
  });
  ipcMain.handle("hs:open-phone-preview", () => {
    openAuxWindow(`http://127.0.0.1:${server.state.pc.port}/?k=${server.state.pc.token}&name=Xem%20th%E1%BB%AD`, {
      width: 410,
      height: 860,
      title: "Xem thử phía iPhone",
    });
  });
  ipcMain.handle("hs:save-screenshot", (_e, dataUrl) => {
    const dir = path.join(app.getPath("pictures"), "Handshake");
    fs.mkdirSync(dir, { recursive: true });
    const file = path.join(dir, `iphone-${new Date().toISOString().replace(/[:.]/g, "-")}.png`);
    fs.writeFileSync(file, Buffer.from(dataUrl.split(",")[1], "base64"));
    shell.showItemInFolder(file);
    return file;
  });

  for (const ev of ["display-added", "display-removed", "display-metrics-changed"]) screen.on(ev, refreshDisplays);

  // LAN address may change (Wi-Fi switch); refresh periodically
  setInterval(async () => {
    const addrs = lanAddresses();
    if (addrs.join() !== server.state.pc.addresses.join()) {
      server.setAddresses(addrs);
      win?.webContents.send("hs:info", await infoPayload());
    }
  }, 5000);

  createWindow();
  console.log(`Handshake listening on ${server.state.pc.url}`);
});

app.on("second-instance", () => {
  if (win) {
    if (win.isMinimized()) win.restore();
    win.focus();
  }
});

app.on("window-all-closed", () => app.quit());
app.on("before-quit", () => {
  input.stop();
  engine.stopAll();
  server?.stop();
});
