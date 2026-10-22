// Injects mouse input into Windows from touch events sent by the iPhone.
// Uses one long-lived PowerShell process hosting a tiny C# class (SetCursorPos / mouse_event),
// so no native Node module has to be compiled. Commands are written line by line to stdin.

const { spawn } = require("node:child_process");

const CSHARP = `
using System;
using System.Runtime.InteropServices;
public static class HsInput {
  [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, int data, UIntPtr extra);
  [DllImport("user32.dll")] static extern IntPtr SetProcessDpiAwarenessContext(IntPtr value);
  public static void Init() { try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch {} }
  public static void Move(int x, int y) { SetCursorPos(x, y); }
  public static void Down(int b) { mouse_event(b == 2 ? 0x0008u : b == 1 ? 0x0020u : 0x0002u, 0, 0, 0, UIntPtr.Zero); }
  public static void Up(int b) { mouse_event(b == 2 ? 0x0010u : b == 1 ? 0x0040u : 0x0004u, 0, 0, 0, UIntPtr.Zero); }
  public static void Wheel(int d) { mouse_event(0x0800u, 0, 0, d, UIntPtr.Zero); }
  public static void HWheel(int d) { mouse_event(0x1000u, 0, 0, d, UIntPtr.Zero); }
}`;

const LOOP = `
$ProgressPreference = 'SilentlyContinue'
Add-Type -TypeDefinition @'
${CSHARP}
'@
[HsInput]::Init()
[Console]::Out.WriteLine('ready')
while ($true) {
  $l = [Console]::In.ReadLine()
  if ($l -eq $null) { break }
  $p = $l.Split(' ')
  switch ($p[0]) {
    'm' { [HsInput]::Move([int]$p[1], [int]$p[2]) }
    'd' { [HsInput]::Down([int]$p[1]) }
    'u' { [HsInput]::Up([int]$p[1]) }
    'w' { [HsInput]::Wheel([int]$p[1]) }
    'h' { [HsInput]::HWheel([int]$p[1]) }
  }
}`;

class InputInjector {
  constructor() {
    this.proc = null;
    this.ready = false;
    this.queue = [];
  }

  start() {
    if (process.platform !== "win32" || this.proc) return;
    const encoded = Buffer.from(LOOP, "utf16le").toString("base64");
    this.proc = spawn("powershell.exe", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded], {
      windowsHide: true,
      stdio: ["pipe", "pipe", "pipe"],
    });
    this.proc.stdout.on("data", (d) => {
      if (!this.ready && d.toString().includes("ready")) {
        this.ready = true;
        for (const line of this.queue.splice(0)) this.proc.stdin.write(line);
      }
    });
    this.proc.stderr.on("data", (d) => {
      const text = d.toString().trim();
      if (text && !text.startsWith("#< CLIXML")) console.error("[input]", text);
    });
    this.proc.on("exit", () => {
      this.proc = null;
      this.ready = false;
    });
  }

  write(line) {
    if (!this.proc) this.start();
    if (!this.proc) return;
    if (this.ready) this.proc.stdin.write(line + "\n");
    else this.queue.push(line + "\n");
  }

  move(x, y) {
    this.write(`m ${Math.round(x)} ${Math.round(y)}`);
  }

  down(button = 0) {
    this.write(`d ${button}`);
  }

  up(button = 0) {
    this.write(`u ${button}`);
  }

  wheel(delta) {
    this.write(`w ${Math.round(delta)}`);
  }

  hwheel(delta) {
    this.write(`h ${Math.round(delta)}`);
  }

  stop() {
    this.proc?.stdin.end();
    this.proc?.kill();
    this.proc = null;
  }
}

module.exports = { InputInjector };
