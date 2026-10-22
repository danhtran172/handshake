// Performance page: drives the native engine (hs-engine.exe) through the main process —
// hardware inventory, one-click benchmark with live per-second chart, and the native host toggle.

(function () {
  const { el, toast } = HandshakeUI;
  const $ = (id) => document.getElementById(id);

  const FORM = [
    { key: "size", label: "Độ phân giải", options: [["1920x1080", "1080p"], ["2556x1179", "iPhone Pro (2556×1179)"]] },
    { key: "fps", label: "Khung hình/giây", options: [[60, "60"], [120, "120"]] },
    { key: "codec", label: "Codec", options: [["h264", "H.264"], ["hevc", "HEVC"]] },
    { key: "source", label: "Nguồn hình", hint: "Hình test: đo đúng pipeline · Màn hình thật: chụp DXGI 144 Hz", options: [["test", "Hình test"], ["desktop", "Màn hình thật"]] },
  ];
  const opts = { size: "2556x1179", fps: 120, codec: "h264", source: "test" };
  let hardware = null;
  let running = false;

  function buildForm() {
    const box = $("perfForm");
    for (const f of FORM) {
      const seg = el("div", { class: "seg", role: "group", "aria-label": f.label });
      for (const [value, text] of f.options) {
        seg.append(el("button", {
          type: "button",
          text,
          "aria-pressed": String(opts[f.key] === value),
          onclick: () => {
            opts[f.key] = value;
            for (const b of seg.children) b.setAttribute("aria-pressed", String(b.textContent === text));
          },
        }));
      }
      box.append(el("div", { class: "field" }, [
        el("label", {}, [document.createTextNode(f.label), f.hint ? el("span", { class: "hint", text: f.hint }) : null]),
        seg,
      ]));
    }
  }

  async function loadHardware() {
    const pill = $("perfEnginePill");
    const box = $("perfHardware");
    hardware = await hs.engineDevices();
    if (!hardware.available) {
      pill.className = "pill bad";
      pill.innerHTML = '<span class="dot"></span>Chưa build engine';
      box.textContent = "Chạy: dotnet build engine/HsEngine -c Release";
      $("btnBench").disabled = true;
      $("engineHostToggle").disabled = true;
      return;
    }
    pill.className = "pill ok";
    pill.innerHTML = '<span class="dot"></span>Engine sẵn sàng';
    box.textContent = "";
    for (const o of hardware.outputs) {
      box.append(el("div", { class: "row" }, [
        el("div", { class: "grow" }, [
          el("div", { class: "title", text: `Màn hình ${o.index + 1} · ${o.width}×${o.height}` }),
          el("div", { class: "sub", text: o.adapter }),
        ]),
      ]));
    }
    const chips = el("div", { style: "margin-top:8px" });
    for (const e of hardware.encoders) {
      const vendor = e.vendor === "VEN_10DE" ? "nv" : e.vendor === "VEN_1002" ? "amd" : "";
      chips.append(el("span", { class: `hw-chip ${vendor}`, text: `${e.codec.toUpperCase()} · ${e.name}` }));
    }
    box.append(el("div", { class: "faint", text: "Bộ mã hoá phần cứng:" }), chips);
    box.append(el("p", { class: "faint", style: "margin-top:8px", text: "Ghi chú: encoder NVIDIA qua Media Foundation không khởi tạo được trên laptop Optimus này; engine dùng AMD. NVENC trực tiếp nằm trong kế hoạch." }));
  }

  // ------------------------------------------------------------------ benchmark

  function resetResult() {
    for (const id of ["pFps", "pP50", "pP99", "pMbps"]) $(id).textContent = "–";
    for (const t of document.querySelectorAll(".perf-tile")) t.className = "perf-tile";
    $("perfChart").textContent = "";
    $("perfSplit").textContent = "";
    $("perfEvents").textContent = "";
  }

  function addSecond(s, failoverSecond) {
    const chart = $("perfChart");
    const target = Number(opts.fps);
    const h = Math.max(2, Math.min(100, (s.fps / target) * 100));
    chart.append(el("div", { class: "perf-bar" + (failoverSecond ? " fail" : ""), title: `${s.fps} fps · p50 ${s.latencyP50Ms} ms · p99 ${s.latencyP99Ms} ms` }, [
      el("small", { text: String(s.latencyP50Ms) }),
      el("i", { style: `height:${h}%` }),
    ]));
  }

  function showSummary(r) {
    const s = r.summary;
    const target = Number(opts.fps);
    const tiles = [
      ["pFps", s.fps, s.fps >= target * 0.95],
      ["pP50", `${s.latencyP50Ms} ms`, s.latencyP50Ms <= 16],
      ["pP99", `${s.latencyP99Ms} ms`, s.latencyP99Ms <= 25],
      ["pMbps", s.mbps, true],
    ];
    for (const [id, text, good] of tiles) {
      $(id).textContent = String(text);
      $(id).parentElement.className = "perf-tile " + (good ? "good" : "warn");
    }
    const total = Math.max(0.01, s.encodeMs + s.networkMs + s.decodeMs);
    const split = $("perfSplit");
    split.textContent = "";
    for (const [cls, label, v] of [["enc", "mã hoá", s.encodeMs], ["net", "mạng", s.networkMs], ["dec", "giải mã", s.decodeMs]]) {
      split.append(el("span", { class: cls, style: `flex:${Math.max(v / total, 0.22)}`, title: `${label} ${v} ms`, text: `${label} ${v} ms` }));
    }
    const ev = $("perfEvents");
    ev.append(el("div", { text: `Khoảng hở lớn nhất ${s.frameGapMaxMs} ms · nhịp p99 ${s.frameGapP99Ms} ms · bỏ khung ${s.skipped} · lỗi giải mã ${s.decodeErrors}` }));
    const stream = r.events.find((e) => e.event === "stream");
    if (stream) $("perfConfig").textContent = `${stream.encoder} · ${stream.width}×${stream.height}@${stream.fps}`;
    for (const e of r.events.filter((x) => ["simulate-unplug", "failover", "path-restored"].includes(x.event))) {
      const text = e.event === "simulate-unplug" ? `Rút cáp: ${e.endpoint}` : e.event === "failover" ? `Chuyển sang ${e.to}` : `Cắm lại, quay về ${e.endpoint}`;
      ev.append(el("div", { text: "↳ " + text }));
    }
  }

  async function runBench() {
    if (running) return;
    running = true;
    resetResult();
    const btn = $("btnBench");
    btn.disabled = true;
    const failover = $("perfFailover").checked;
    const lan = (window.__hsState?.pc.addresses || [])[0];
    let second = 0;
    let failoverAt = null;
    const progress = $("perfProgress");
    progress.textContent = "Đang khởi động engine…";
    const off = onEngine((e) => {
      if (e.kind !== "bench") return;
      if (e.event === "failover") failoverAt = second;
      if (e.event === "client-stats") {
        second++;
        progress.textContent = `Giây ${second}/8`;
        addSecond(e, failoverAt === second - 1);
      }
    });
    const hasNvidia = hardware?.outputs && (await hasGpu("NVIDIA"));
    const r = await hs.engineBench({
      seconds: 8,
      fps: Number(opts.fps),
      size: opts.source === "desktop" ? null : opts.size,
      codec: opts.codec,
      source: opts.source,
      kbps: opts.size === "1920x1080" ? 40000 : 50000,
      gpu: opts.source === "desktop" ? null : "amd",
      clientGpu: hasNvidia ? "nvidia" : null, // client plays the iPhone on the other GPU
      failover: failover && lan ? lan : null,
    });
    off();
    running = false;
    btn.disabled = false;
    progress.textContent = "";
    if (!r.ok) return toast("Lỗi engine: " + r.error);
    showSummary(r);
  }

  async function hasGpu(name) {
    return (hardware?.encoders || []).some((e) => e.name.includes(name));
  }

  // ------------------------------------------------------------------ host toggle

  const listeners = new Set();
  function onEngine(fn) {
    listeners.add(fn);
    return () => listeners.delete(fn);
  }
  hs.onEngine((e) => {
    for (const fn of listeners) fn(e);
    if (e.kind !== "host") return;
    const status = $("engineHostStatus");
    if (e.event === "listening") status.textContent = `Đang mở cổng ${e.port} · ${e.source} · ${e.fps} fps · chờ app iOS`;
    if (e.event === "path-attached") status.textContent = `${e.name || "Thiết bị"} đã kết nối (${e.paths} đường)`;
    if (e.event === "host-stats") status.textContent = `Đang phát ${e.fps} fps · ${(e.kbps / 1000).toFixed(1)} Mbit/s · ${e.paths} đường`;
    if (e.event === "path-lost" && !e.failover) status.textContent = "Mất kết nối, đang chờ thiết bị nối lại…";
    if (e.event === "host-exit") {
      status.textContent = "Tắt · app iOS native sẽ kết nối vào cổng 7430";
      $("engineHostToggle").checked = false;
    }
  });

  $("engineHostToggle").addEventListener("change", async (ev) => {
    const on = ev.target.checked;
    const ok = await hs.engineHost(on, { port: 7430, source: "desktop", fps: 120 });
    if (on && !ok) {
      ev.target.checked = false;
      toast("Không bật được engine host");
    }
  });

  $("btnBench").addEventListener("click", runBench);
  buildForm();
  loadHardware();
})();
