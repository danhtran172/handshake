// UI pieces shared by the PC window and the iPhone client, so both render the same controls
// from the same synced state.

(function (root) {
  const SETTING_UI = [
    {
      key: "quality",
      label: "Chất lượng hình",
      hint: "Mượt = ít băng thông, Nét = chữ rõ",
      options: [
        ["smooth", "Mượt"],
        ["balanced", "Cân bằng"],
        ["sharp", "Nét"],
      ],
    },
    {
      key: "fps",
      label: "Khung hình/giây",
      options: [
        [15, "15"],
        [30, "30"],
        [60, "60"],
      ],
    },
    {
      key: "touchMode",
      label: "Chạm trên iPhone",
      hint: "Chạm = bấm đúng chỗ, Bàn di = điều khiển con trỏ",
      options: [
        ["touch", "Chạm"],
        ["trackpad", "Bàn di"],
      ],
    },
    {
      key: "transport",
      label: "Đường truyền",
      hint: "WebRTC độ trễ thấp, JPEG tương thích mọi máy",
      options: [
        ["auto", "Tự động"],
        ["webrtc", "WebRTC"],
        ["jpeg", "JPEG"],
      ],
    },
    {
      key: "paperTone",
      label: "Màu giấy",
      options: [
        ["cream", "Kem"],
        ["white", "Trắng"],
        ["kraft", "Kraft"],
      ],
    },
  ];

  function el(tag, attrs = {}, children = []) {
    const n = document.createElement(tag);
    for (const [k, v] of Object.entries(attrs)) {
      if (k === "class") n.className = v;
      else if (k === "text") n.textContent = v;
      else if (k.startsWith("on")) n.addEventListener(k.slice(2), v);
      else n.setAttribute(k, v);
    }
    for (const c of [].concat(children)) if (c) n.append(c);
    return n;
  }

  // Builds the settings list once; call update(settings, changedKeys) on each state change.
  function settingsPanel(container, onSet) {
    container.textContent = "";
    const segs = {};
    for (const s of SETTING_UI) {
      const seg = el("div", { class: "seg", role: "group", "aria-label": s.label });
      for (const [value, text] of s.options) {
        seg.append(
          el("button", {
            type: "button",
            text,
            "data-value": String(value),
            "aria-pressed": "false",
            onclick: () => onSet(`settings.${s.key}`, value),
          })
        );
      }
      segs[s.key] = seg;
      container.append(
        el("div", { class: "field" }, [
          el("label", {}, [document.createTextNode(s.label), s.hint ? el("span", { class: "hint", text: s.hint }) : null]),
          seg,
        ])
      );
    }
    return {
      update(settings, changed = []) {
        for (const s of SETTING_UI) {
          for (const b of segs[s.key].children) {
            b.setAttribute("aria-pressed", String(b.dataset.value === String(settings[s.key])));
          }
          if (changed.includes(s.key)) flash(segs[s.key]);
        }
      },
    };
  }

  function changedSettings(prev, next) {
    if (!prev) return [];
    return Object.keys(next.settings).filter((k) => prev.settings[k] !== next.settings[k]);
  }

  function flash(node) {
    node.classList.remove("synced");
    void node.offsetWidth;
    node.classList.add("synced");
  }

  let toastTimer = null;
  function toast(text) {
    let t = document.querySelector(".toast");
    if (!t) {
      t = el("div", { class: "toast", role: "status" });
      document.body.append(t);
    }
    t.textContent = text;
    t.classList.add("show");
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => t.classList.remove("show"), 2600);
  }

  function applyTone(settings) {
    document.documentElement.dataset.tone = settings.paperTone;
  }

  const STATUS_TEXT = {
    idle: "Đang chờ",
    connecting: "Đang kết nối…",
    live: "Đang phát",
    error: "Lỗi",
  };

  function sessionPill(pill, session) {
    const cls = { idle: "", connecting: "warn", live: "live", error: "bad" }[session.status] || "";
    pill.className = "pill " + cls;
    const what = session.mode === "display" ? "Màn hình phụ · " : session.mode === "mirror" ? "iPhone → PC · " : "";
    pill.innerHTML = '<span class="dot"></span>';
    pill.append(document.createTextNode(what + (STATUS_TEXT[session.status] || session.status)));
  }

  function formatStats(stats, transport) {
    const parts = [];
    if (stats.width) parts.push(`${stats.width}×${stats.height}`);
    parts.push(`${stats.fps || 0} fps`);
    parts.push(stats.kbps >= 1000 ? `${(stats.kbps / 1000).toFixed(1)} Mbit/s` : `${stats.kbps || 0} kbit/s`);
    if (stats.rttMs) parts.push(`${stats.rttMs} ms`);
    if (transport) parts.push(transport === "webrtc" ? "WebRTC" : "JPEG");
    return parts.join(" · ");
  }

  root.HandshakeUI = { SETTING_UI, el, settingsPanel, changedSettings, flash, toast, applyTone, sessionPill, formatStats, STATUS_TEXT };
})(typeof self !== "undefined" ? self : this);
