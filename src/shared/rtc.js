// WebRTC helpers shared by both sides.
// The side that owns the media creates the offer:
//   display mode: PC  -> iPhone   (PC screen shown on the iPhone)
//   mirror mode:  iPhone -> PC    (iPhone screen shown on the PC)

(function (root) {
  const RTC_CONFIG = { iceServers: [], bundlePolicy: "max-bundle" };

  function supported() {
    return typeof RTCPeerConnection === "function";
  }

  // Prefer H.264: hardware decode on iPhone, hardware encode on most PCs
  function preferH264(transceiver) {
    const caps = typeof RTCRtpSender !== "undefined" && RTCRtpSender.getCapabilities?.("video");
    if (!caps || !transceiver.setCodecPreferences) return;
    const h264 = caps.codecs.filter((c) => /h264/i.test(c.mimeType));
    const rest = caps.codecs.filter((c) => !/h264/i.test(c.mimeType));
    if (h264.length) {
      try {
        transceiver.setCodecPreferences([...h264, ...rest]);
      } catch {}
    }
  }

  class Peer {
    // signal(data) sends { sdp } / { candidate } to the remote side
    constructor({ signal, onTrack, onState }) {
      this.pc = new RTCPeerConnection(RTC_CONFIG);
      this.signalOut = signal;
      this.pendingCandidates = [];
      this.pc.onicecandidate = (e) => {
        if (e.candidate) this.signalOut({ candidate: e.candidate.toJSON() });
      };
      this.pc.ontrack = (e) => onTrack?.(e.streams[0] || new MediaStream([e.track]), e.track);
      this.pc.onconnectionstatechange = () => onState?.(this.pc.connectionState);
      this.pc.oniceconnectionstatechange = () => {
        // Safari reports progress more reliably through ICE state
        if (this.pc.iceConnectionState === "failed") onState?.("failed");
      };
      this.lastStats = null;
    }

    async sendStream(stream, { kbps, fps }) {
      for (const track of stream.getVideoTracks()) {
        try {
          track.contentHint = "detail";
        } catch {}
        const tr = this.pc.addTransceiver(track, { direction: "sendonly", streams: [stream] });
        preferH264(tr);
        this.sender = tr.sender;
      }
      await this.setLimits({ kbps, fps });
      const offer = await this.pc.createOffer();
      await this.pc.setLocalDescription(offer);
      this.signalOut({ sdp: this.pc.localDescription.toJSON() });
    }

    async setLimits({ kbps, fps }) {
      if (!this.sender) return;
      const params = this.sender.getParameters();
      if (!params.encodings || !params.encodings.length) params.encodings = [{}];
      params.encodings[0].maxBitrate = kbps * 1000;
      params.encodings[0].maxFramerate = fps;
      params.degradationPreference = "maintain-resolution";
      try {
        await this.sender.setParameters(params);
      } catch {}
    }

    async handleSignal(data) {
      if (data.sdp) {
        await this.pc.setRemoteDescription(data.sdp);
        for (const c of this.pendingCandidates.splice(0)) await this.pc.addIceCandidate(c).catch(() => {});
        if (data.sdp.type === "offer") {
          const answer = await this.pc.createAnswer();
          await this.pc.setLocalDescription(answer);
          this.signalOut({ sdp: this.pc.localDescription.toJSON() });
        }
      } else if (data.candidate) {
        if (!this.pc.remoteDescription) this.pendingCandidates.push(data.candidate);
        else await this.pc.addIceCandidate(data.candidate).catch(() => {});
      }
    }

    // Returns { fps, kbps, rttMs, width, height } for the video stream (in or out)
    async stats() {
      const report = await this.pc.getStats();
      let video = null;
      let rtt = 0;
      report.forEach((s) => {
        if (s.kind === "video" && (s.type === "inbound-rtp" || s.type === "outbound-rtp")) video = s;
        if (s.type === "candidate-pair" && s.nominated && s.currentRoundTripTime) rtt = s.currentRoundTripTime * 1000;
      });
      if (!video) return null;
      const bytes = video.bytesReceived ?? video.bytesSent ?? 0;
      const now = video.timestamp;
      let kbps = 0;
      if (this.lastStats && now > this.lastStats.ts) kbps = ((bytes - this.lastStats.bytes) * 8) / (now - this.lastStats.ts);
      this.lastStats = { bytes, ts: now };
      return {
        fps: video.framesPerSecond || 0,
        kbps,
        rttMs: rtt,
        width: video.frameWidth || 0,
        height: video.frameHeight || 0,
      };
    }

    close() {
      try {
        this.pc.close();
      } catch {}
    }
  }

  root.HandshakeRTC = { Peer, supported, RTC_CONFIG };
})(typeof self !== "undefined" ? self : this);
