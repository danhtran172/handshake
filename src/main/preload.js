const { contextBridge, ipcRenderer } = require("electron");

contextBridge.exposeInMainWorld("hs", {
  info: () => ipcRenderer.invoke("hs:info"),
  selectDisplay: (displayId) => ipcRenderer.invoke("hs:select-display", displayId),
  openMockup: () => ipcRenderer.invoke("hs:open-mockup"),
  openPhonePreview: () => ipcRenderer.invoke("hs:open-phone-preview"),
  saveScreenshot: (dataUrl) => ipcRenderer.invoke("hs:save-screenshot", dataUrl),
  onInfo: (fn) => ipcRenderer.on("hs:info", (_e, info) => fn(info)),
  engineDevices: () => ipcRenderer.invoke("hs:engine-devices"),
  engineBench: (opts) => ipcRenderer.invoke("hs:engine-bench", opts),
  engineHost: (on, opts) => ipcRenderer.invoke("hs:engine-host", on, opts),
  onEngine: (fn) => ipcRenderer.on("hs:engine", (_e, evt) => fn(evt)),
});
