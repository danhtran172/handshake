// Shared state model. Loaded by the Node main process (CommonJS) and by browsers (global).
// The server owns the canonical copy; every client receives the full state on change,
// and asks for changes with { t: "set", path, value }.

(function (root, factory) {
  if (typeof module === "object" && module.exports) module.exports = factory();
  else root.HandshakeState = factory();
})(typeof self !== "undefined" ? self : this, function () {
  const SETTINGS_SCHEMA = {
    quality: { values: ["smooth", "balanced", "sharp"], default: "balanced" },
    fps: { values: [15, 30, 60], default: 30 },
    transport: { values: ["auto", "webrtc", "jpeg"], default: "auto" },
    touchMode: { values: ["touch", "trackpad"], default: "touch" },
    showCursor: { values: [true, false], default: true },
    orientation: { values: ["auto", "landscape", "portrait"], default: "auto" },
    paperTone: { values: ["cream", "white", "kraft"], default: "cream" },
  };

  // Target bitrate (kbit/s) and JPEG quality for each quality preset
  const QUALITY = {
    smooth: { kbps: 2500, jpeg: 0.5, scale: 0.75 },
    balanced: { kbps: 6000, jpeg: 0.7, scale: 1 },
    sharp: { kbps: 12000, jpeg: 0.85, scale: 1 },
  };

  function defaultSettings() {
    const s = {};
    for (const [k, v] of Object.entries(SETTINGS_SCHEMA)) s[k] = v.default;
    return s;
  }

  function createState() {
    return {
      pc: { name: "", addresses: [], port: 0, url: "", token: "" },
      devices: [],
      displays: [],
      session: {
        mode: null, // "display" (iPhone làm màn hình phụ) | "mirror" (iPhone phát lên PC)
        deviceId: null,
        displayId: null,
        status: "idle", // idle | connecting | live | error
        transport: null,
        startedAt: null,
        error: null,
      },
      settings: defaultSettings(),
      stats: { fps: 0, kbps: 0, rttMs: 0, width: 0, height: 0 },
      updatedAt: 0,
    };
  }

  function validateSetting(key, value) {
    const schema = SETTINGS_SCHEMA[key];
    return !!schema && schema.values.includes(value);
  }

  return { SETTINGS_SCHEMA, QUALITY, createState, defaultSettings, validateSetting };
});
