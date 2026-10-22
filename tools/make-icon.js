// Renders src/ios/icon.svg to assets/icon.png (256px), assets/icon.ico (16–256px, PNG entries) and the
// native iOS app icon (1024px, square: iOS rounds the corners itself).
// Run with Electron: npx electron tools/make-icon.js

const { app, BrowserWindow, nativeImage } = require("electron");
const fs = require("node:fs");
const path = require("node:path");

const ROOT = path.join(__dirname, "..");
const OUT = path.join(ROOT, "assets");
const SIZES = [16, 24, 32, 48, 64, 128, 256];

app.disableHardwareAcceleration();
app.on("window-all-closed", () => {}); // render() closes each window; quit only when done

function buildIco(pngs) {
  const header = Buffer.alloc(6);
  header.writeUInt16LE(0, 0);
  header.writeUInt16LE(1, 2); // icon
  header.writeUInt16LE(pngs.length, 4);
  const dir = Buffer.alloc(16 * pngs.length);
  let offset = 6 + dir.length;
  pngs.forEach(({ size, data }, i) => {
    const e = i * 16;
    dir.writeUInt8(size >= 256 ? 0 : size, e);
    dir.writeUInt8(size >= 256 ? 0 : size, e + 1);
    dir.writeUInt8(0, e + 2);
    dir.writeUInt8(0, e + 3);
    dir.writeUInt16LE(1, e + 4); // planes
    dir.writeUInt16LE(32, e + 6); // bpp
    dir.writeUInt32LE(data.length, e + 8);
    dir.writeUInt32LE(offset, e + 12);
    offset += data.length;
  });
  return Buffer.concat([header, dir, ...pngs.map((p) => p.data)]);
}

// Rasterises through a canvas, so the size is not limited by the screen like a window would be
async function render(svg, size) {
  const win = new BrowserWindow({ show: false, webPreferences: { offscreen: true } });
  await win.loadURL("data:text/html;charset=utf-8,<html><body></body></html>");
  const dataUrl = await win.webContents.executeJavaScript(`new Promise((resolve, reject) => {
    const img = new Image();
    img.onload = () => {
      const c = document.createElement("canvas");
      c.width = c.height = ${size};
      c.getContext("2d").drawImage(img, 0, 0, ${size}, ${size});
      resolve(c.toDataURL("image/png"));
    };
    img.onerror = () => reject(new Error("icon render failed"));
    img.src = "data:image/svg+xml;charset=utf-8," + encodeURIComponent(${JSON.stringify(svg)});
  })`);
  win.destroy();
  return nativeImage.createFromDataURL(dataUrl);
}

app.whenReady().then(async () => {
  const svg = fs.readFileSync(path.join(ROOT, "src", "ios", "icon.svg"), "utf8");
  const base = await render(svg, 256);
  fs.mkdirSync(OUT, { recursive: true });
  fs.writeFileSync(path.join(OUT, "icon.png"), base.toPNG());
  const pngs = SIZES.map((size) => ({
    size,
    data: size === 256 ? base.toPNG() : base.resize({ width: size, height: size, quality: "best" }).toPNG(),
  }));
  fs.writeFileSync(path.join(OUT, "icon.ico"), buildIco(pngs));
  console.log("icon written to", OUT);

  const ios = await render(svg.replace(/(<rect width="180" height="180") rx="\d+"/, "$1"), 1024);
  const iosOut = path.join(ROOT, "ios-app", "Handshake", "Assets.xcassets", "AppIcon.appiconset", "icon-1024.png");
  fs.writeFileSync(iosOut, ios.toPNG());
  console.log("iOS icon written to", iosOut);
  app.quit();
});
