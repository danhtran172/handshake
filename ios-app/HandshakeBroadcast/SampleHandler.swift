import CoreMedia
import Network
import ReplayKit

/// iPhone screen to the PC. Finds `hs-engine mirror` through Bonjour (`_hs-mirror._tcp`), then
/// plays the host side of the Handshake protocol: Hello, Config, H.264 frames; it answers pings
/// (the PC measures latency with them) and honours KeyRequest. When the link falls behind it
/// skips frames instead of queueing them.
final class SampleHandler: RPBroadcastSampleHandler {
    private let fps = 60          // ReplayKit delivers at most about 60 frames per second
    private let kbps = 20_000

    private let queue = DispatchQueue(label: "handshake.broadcast", qos: .userInteractive)
    // Everything below is only touched on `queue`
    private var running = false
    private var browser: NWBrowser?
    private var channel: MessageChannel?
    private var ready = false
    private var encoder: VideoEncoder?
    private var sentConfig = false
    private var frameId: UInt32 = 0

    override func broadcastStarted(withSetupInfo setupInfo: [String: NSObject]?) {
        queue.async {
            self.running = true
            self.browse()
        }
    }

    override func broadcastFinished() {
        queue.sync {
            running = false
            browser?.cancel()
            channel?.close("broadcast finished")
            encoder?.invalidate()
            encoder = nil
        }
    }

    override func processSampleBuffer(_ sampleBuffer: CMSampleBuffer, with sampleBufferType: RPSampleBufferType) {
        guard sampleBufferType == .video else { return }
        queue.sync { encode(sampleBuffer) }
    }

    private func browse() {
        browser?.cancel()
        let b = NWBrowser(for: .bonjour(type: Wire.mirrorService, domain: nil), using: NWParameters())
        b.browseResultsChangedHandler = { [weak self] results, _ in
            guard let self, self.running, self.channel == nil, let first = results.first else { return }
            self.connect(first.endpoint)
        }
        b.start(queue: queue)
        browser = b
        // Stop with a clear message rather than broadcasting into the void
        queue.asyncAfter(deadline: .now() + 20) { [weak self] in
            guard let self, self.running, !self.ready else { return }
            self.finishBroadcastWithError(NSError(domain: "Handshake", code: 1, userInfo: [
                NSLocalizedDescriptionKey: "Không tìm thấy PC. Trên máy tính hãy chạy: hs-engine mirror",
            ]))
        }
    }

    private func connect(_ endpoint: NWEndpoint) {
        let ch = MessageChannel(endpoint: endpoint, queue: queue)
        channel = ch
        ch.onMessage = { [weak self] type, payload in self?.handle(type, payload) }
        ch.onState = { [weak self, weak ch] state in
            guard let self, let ch, ch === self.channel else { return }
            switch state {
            case .ready:
                self.browser?.cancel()
                self.browser = nil
                ch.sendControl(Message.hello(maxWidth: 0, maxHeight: 0, fps: self.fps, name: "iPhone"))
                self.ready = true
                self.sentConfig = false // Config goes out with the first frame, once the size is known
            case .closed:
                self.channel = nil
                self.ready = false
                guard self.running else { return }
                self.queue.asyncAfter(deadline: .now() + 0.5) { self.browse() }
            }
        }
        ch.start()
    }

    private func handle(_ type: MsgType, _ payload: Data) {
        switch type {
        case .ping:
            channel?.sendControl(Message.pong(echo: payload))
        case .keyRequest:
            encoder?.requestKeyFrame()
        default:
            break
        }
    }

    private func encode(_ sample: CMSampleBuffer) {
        guard ready, let channel, let pixels = CMSampleBufferGetImageBuffer(sample) else { return }
        if channel.pendingVideo > 2 { return } // link is behind: skip this frame
        let w = CVPixelBufferGetWidth(pixels), h = CVPixelBufferGetHeight(pixels)
        if encoder?.width != w || encoder?.height != h {
            encoder?.invalidate()
            encoder = VideoEncoder(width: w, height: h, fps: fps, kbps: kbps) { [weak self] out in
                self?.queue.async { self?.send(out) }
            }
            sentConfig = false
        }
        guard let encoder else { return }
        if !sentConfig {
            channel.sendControl(Message.config(ConfigMsg(codec: .h264, width: UInt16(w), height: UInt16(h),
                                                          fps: UInt16(fps), kbps: UInt32(kbps))))
            sentConfig = true
            encoder.requestKeyFrame()
        }
        let pts = CMSampleBufferGetPresentationTimeStamp(sample)
        encoder.encode(pixels, pts: pts, captureUs: Clock.us(pts))
    }

    private func send(_ out: VideoEncoder.Output) {
        guard ready, let channel else { return }
        let header = VideoHeader(frameId: frameId, captureUs: out.captureUs, sendUs: Clock.nowUs,
                                 encodeUs: UInt32(clamping: out.encodeUs), key: out.key)
        frameId &+= 1
        channel.sendVideo(Message.video(header, accessUnit: out.accessUnit))
    }
}
