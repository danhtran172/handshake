// Bridge to the native streaming engine (engine/HsEngine, C#/.NET): spawns hs-engine.exe and
// turns its JSON-lines stdout into events for the PC window.

const { spawn } = require("node:child_process");
const fs = require("node:fs");
const path = require("node:path");
const { EventEmitter } = require("node:events");

const ROOT = path.join(__dirname, "..", "..");
const CANDIDATES = [
  path.join(ROOT, "engine", "bin", "hs-engine.exe"), // published build
  path.join(ROOT, "engine", "HsEngine", "bin", "Release", "net10.0-windows", "hs-engine.exe"),
  path.join(ROOT, "engine", "HsEngine", "bin", "Debug", "net10.0-windows", "hs-engine.exe"),
];

class Engine extends EventEmitter {
  constructor() {
    super();
    this.exe = CANDIDATES.find((p) => fs.existsSync(p)) || null;
    this.host = null;
    this.bench = null;
  }

  get available() {
    return !!this.exe;
  }

  run(args, onLine) {
    const proc = spawn(this.exe, args, { windowsHide: true, stdio: ["pipe", "pipe", "pipe"] });
    let buffer = "";
    proc.stdout.setEncoding("utf8");
    proc.stdout.on("data", (chunk) => {
      buffer += chunk;
      let nl;
      while ((nl = buffer.indexOf("\n")) >= 0) {
        const line = buffer.slice(0, nl).trim();
        buffer = buffer.slice(nl + 1);
        if (!line) continue;
        let evt = null;
        try {
          evt = JSON.parse(line);
        } catch {
          evt = { event: "text", text: line };
        }
        onLine(evt, line);
      }
    });
    proc.stderr.on("data", (d) => onLine({ event: "stderr", text: d.toString().trim() }));
    return proc;
  }

  /** Hardware inventory: outputs and hardware encoders. */
  devices() {
    if (!this.exe) return Promise.resolve({ available: false, outputs: [], encoders: [] });
    return new Promise((resolve) => {
      const outputs = [];
      const encoders = [];
      const p = this.run(["devices"], (e) => {
        if (e.event === "output") outputs.push(e);
        if (e.event === "encoder" && !encoders.some((x) => x.codec === e.codec && x.name === e.name)) encoders.push(e);
      });
      p.on("close", () => resolve({ available: true, outputs, encoders }));
    });
  }

  /** Runs `hs-engine bench`; emits "bench-line" while running, resolves with the parsed summary. */
  runBench(opts) {
    if (!this.exe) return Promise.reject(new Error("Chưa build engine (dotnet build engine/HsEngine -c Release)"));
    if (this.bench) return Promise.reject(new Error("Đang chạy đo"));
    const args = ["bench", "--seconds", String(opts.seconds || 8), "--fps", String(opts.fps || 120), "--kbps", String(opts.kbps || 40000), "--codec", opts.codec || "h264", "--source", opts.source || "test"];
    if (opts.size) args.push("--size", opts.size);
    if (opts.source !== "desktop") args.push("--headless");
    if (opts.gpu) args.push("--gpu", opts.gpu);
    if (opts.clientGpu) args.push("--client-gpu", opts.clientGpu);
    if (opts.failover) args.push("--host", "127.0.0.1," + opts.failover, "--drop-primary-at", "3");
    return new Promise((resolve, reject) => {
      const seconds = [];
      const events = [];
      let summary = null;
      this.bench = this.run(args, (_evt, raw) => {
        // bench prints prefixed lines ("client {...}", "host {...}") and a human summary
        const m = /^(client|host|anim)\s+(\{.*\})/.exec(raw || "");
        if (!m) return;
        let e;
        try {
          e = JSON.parse(m[2]);
        } catch {
          return;
        }
        if (e.event === "client-stats") seconds.push(e);
        if (["failover", "path-restored", "simulate-unplug", "stream", "client-render", "fatal", "error"].includes(e.event)) events.push(e);
        if (e.event === "client-summary") summary = e;
        this.emit("bench-line", e);
      });
      this.bench.on("close", (code) => {
        this.bench = null;
        if (summary) resolve({ summary, seconds, events, args });
        else reject(new Error(events.find((e) => e.event === "fatal" || e.event === "error")?.message || `Engine thoát với mã ${code}`));
      });
    });
  }

  /** Long-running host for native clients (future iOS app). */
  startHost(opts) {
    if (!this.exe || this.host) return false;
    const args = ["host", "--port", String(opts.port || 7430), "--source", opts.source || "desktop", "--fps", String(opts.fps || 120), "--kbps", String(opts.kbps || 40000), "--codec", opts.codec || "h264"];
    if (opts.output != null) args.push("--output", String(opts.output));
    this.host = this.run(args, (e) => this.emit("host-event", e));
    this.host.on("close", (code) => {
      this.host = null;
      this.emit("host-event", { event: "host-exit", code });
    });
    return true;
  }

  stopHost() {
    if (!this.host) return;
    this.host.stdin.end(); // the engine exits when its stdin closes
    setTimeout(() => this.host?.kill(), 2000);
  }

  stopAll() {
    this.stopHost();
    this.bench?.kill();
  }
}

module.exports = { Engine };
