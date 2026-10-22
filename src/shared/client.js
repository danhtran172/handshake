// WebSocket client for the Handshake hub. Used by the PC window and the iPhone web client.
// Keeps the latest shared state, reconnects automatically, exposes a tiny event API.

(function (root) {
  class HandshakeClient {
    constructor(url, hello) {
      this.url = url;
      this.hello = hello;
      this.ws = null;
      this.id = null;
      this.state = null;
      this.handlers = {};
      this.retry = 0;
      this.closedByUser = false;
    }

    on(event, fn) {
      (this.handlers[event] ||= []).push(fn);
      return this;
    }

    emit(event, ...args) {
      for (const fn of this.handlers[event] || []) {
        try {
          fn(...args);
        } catch (err) {
          console.error(err);
        }
      }
    }

    connect() {
      this.closedByUser = false;
      const ws = new WebSocket(this.url);
      ws.binaryType = "arraybuffer";
      this.ws = ws;
      this.emit("connection", "connecting");
      ws.onopen = () => {
        this.retry = 0;
        ws.send(JSON.stringify({ t: "hello", ...this.hello }));
      };
      ws.onmessage = (ev) => {
        if (typeof ev.data !== "string") return this.emit("binary", ev.data);
        let msg;
        try {
          msg = JSON.parse(ev.data);
        } catch {
          return;
        }
        if (msg.t === "welcome") {
          this.id = msg.id;
          const prev = this.state;
          this.state = msg.state;
          this.emit("connection", "open");
          this.emit("state", this.state, prev);
        } else if (msg.t === "state") {
          const prev = this.state;
          this.state = msg.state;
          this.emit("state", this.state, prev);
        } else {
          this.emit(msg.t, msg);
        }
      };
      ws.onclose = (ev) => {
        this.ws = null;
        this.emit("connection", "closed", ev.code);
        if (this.closedByUser || ev.code === 4001 || ev.code === 4000) return;
        const delay = Math.min(5000, 400 * 2 ** this.retry++);
        setTimeout(() => this.connect(), delay);
      };
    }

    close() {
      this.closedByUser = true;
      this.ws?.close();
    }

    get open() {
      return this.ws?.readyState === 1 && !!this.id;
    }

    send(msg) {
      if (this.ws?.readyState === 1) this.ws.send(JSON.stringify(msg));
    }

    sendBinary(buf) {
      if (this.ws?.readyState === 1) this.ws.send(buf);
    }

    get bufferedAmount() {
      return this.ws?.bufferedAmount ?? 0;
    }

    set(path, value) {
      this.send({ t: "set", path, value });
    }

    signal(to, data) {
      this.send({ t: "signal", to, data });
    }
  }

  root.HandshakeClient = HandshakeClient;
})(typeof self !== "undefined" ? self : this);
