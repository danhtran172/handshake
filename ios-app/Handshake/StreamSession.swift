import AVFoundation
import Network
import UIKit

/// PC screen on the iPhone: connects to `hs-engine host`, decodes H.264/HEVC with the hardware
/// decoder behind AVSampleBufferDisplayLayer and sends touches back as pointer input.
/// Reconnects on its own and re-attaches to the same session, so a dropped link only costs a keyframe.
final class StreamSession: ObservableObject {
    enum Status: Equatable {
        case idle
        case connecting(String)
        case streaming
    }

    @Published private(set) var status: Status = .idle
    @Published private(set) var statsText = ""
    @Published private(set) var videoSize = CGSize(width: 16, height: 9)

    let displayLayer = AVSampleBufferDisplayLayer()
    private let queue = DispatchQueue(label: "handshake.stream", qos: .userInteractive)

    // Everything below is only touched on `queue`
    private var endpoint: NWEndpoint?
    private var channel: MessageChannel?
    private var running = false
    private var screen = (width: 0, height: 0, fps: 60)
    private var deviceName = "iPhone"
    private var config: ConfigMsg?
    private var format: CMVideoFormatDescription?
    private var waitingKey = true
    private var lastKeyRequestUs: Int64 = 0
    private var lastPongUs: Int64 = 0
    private var clock = ClockSync()
    private var pingTimer: DispatchSourceTimer?
    private var window = StatsWindow()

    init() {
        displayLayer.videoGravity = .resizeAspect
        displayLayer.backgroundColor = UIColor.black.cgColor
    }

    /// Main thread.
    func connect(to endpoint: NWEndpoint, name: String) {
        let s = UIScreen.main
        let w = Int(s.nativeBounds.width), h = Int(s.nativeBounds.height)
        // The PC screen is wide: the phone is turned sideways to show it
        let size = (width: max(w, h), height: min(w, h), fps: s.maximumFramesPerSecond)
        let deviceName = UIDevice.current.name
        status = .connecting("Đang kết nối tới \(name)…")
        UIApplication.shared.isIdleTimerDisabled = true
        queue.async {
            self.screen = size
            self.deviceName = deviceName
            self.endpoint = endpoint
            self.config = nil
            self.running = true
            self.open()
        }
    }

    /// Main thread.
    func disconnect() {
        status = .idle
        statsText = ""
        UIApplication.shared.isIdleTimerDisabled = false
        queue.async {
            self.running = false
            self.pingTimer?.cancel()
            self.pingTimer = nil
            self.channel?.close("user")
            self.channel = nil
            self.displayLayer.flushAndRemoveImage()
        }
    }

    /// kind: 1 down, 2 move, 3 up. x, y: 0...1 over the picture.
    func sendInput(kind: UInt8, x: Double, y: Double) {
        queue.async { self.channel?.sendControl(Message.input(kind: kind, x: x, y: y)) }
    }

    private func open() {
        guard running, let endpoint else { return }
        let ch = MessageChannel(endpoint: endpoint, queue: queue)
        channel = ch
        ch.onMessage = { [weak self] type, payload in self?.handle(type, payload) }
        ch.onState = { [weak self, weak ch] state in
            guard let self, let ch, ch === self.channel else { return }
            switch state {
            case .ready:
                self.clock.reset()
                self.lastPongUs = Clock.nowUs
                // A non-zero session id re-attaches to the running session (the PC keeps it 10 s)
                ch.sendControl(Message.hello(maxWidth: self.screen.width, maxHeight: self.screen.height, fps: self.screen.fps,
                                             name: self.deviceName, sessionId: self.config?.sessionId ?? 0))
                self.startPing()
            case .closed(let reason):
                self.channel = nil
                self.pingTimer?.cancel() // restarted once the next connection is ready
                self.pingTimer = nil
                guard self.running else { return }
                self.publish(.connecting("Mất kết nối (\(reason)), đang nối lại…"))
                self.queue.asyncAfter(deadline: .now() + 0.5) { self.open() }
            }
        }
        ch.start()
    }

    private func startPing() {
        pingTimer?.cancel()
        let t = DispatchSource.makeTimerSource(queue: queue)
        t.schedule(deadline: .now(), repeating: .milliseconds(100))
        t.setEventHandler { [weak self] in
            guard let self, let ch = self.channel else { return }
            // Same rule as the PC client: a path silent for 400 ms is dead
            if Clock.nowUs - self.lastPongUs > 400_000 {
                ch.close("no pong for 400 ms")
                return
            }
            ch.sendControl(Message.ping())
            self.reportStats()
        }
        t.resume()
        pingTimer = t
    }

    private func handle(_ type: MsgType, _ payload: Data) {
        switch type {
        case .config:
            guard let c = ConfigMsg(payload) else { return }
            config = c
            format = nil
            waitingKey = true
            let size = CGSize(width: Int(c.width), height: Int(c.height))
            DispatchQueue.main.async {
                self.videoSize = size
                if self.status != .idle { self.status = .streaming }
            }
        case .video:
            decode(payload)
        case .pong:
            clock.add(pong: payload)
            lastPongUs = Clock.nowUs
        case .ping:
            channel?.sendControl(Message.pong(echo: payload))
        default:
            break
        }
    }

    private func decode(_ payload: Data) {
        guard let config, payload.count > VideoHeader.size else { return }
        let header = VideoHeader(payload)
        let units = AnnexB.nalUnits(payload.dropFirst(VideoHeader.size))
        if header.isKey, let f = AnnexB.formatDescription(codec: config.codec, units: units) {
            if format == nil || !CMFormatDescriptionEqual(format, otherFormatDescription: f) { format = f }
        }
        if waitingKey {
            guard header.isKey, format != nil else {
                window.skipped += 1
                requestKey()
                return
            }
            waitingKey = false
        }
        guard let format, let sample = AnnexB.sampleBuffer(units: units, format: format, codec: config.codec) else { return }
        if displayLayer.status == .failed {
            displayLayer.flush()
            window.errors += 1
            waitingKey = true
            requestKey()
            return
        }
        displayLayer.enqueue(sample)
        // Capture on the PC to "handed to the display layer" here, on the PC's clock
        window.add(latencyUs: Clock.nowUs + clock.offsetUs - header.captureUs, bytes: payload.count)
    }

    private func requestKey() {
        let now = Clock.nowUs
        guard now - lastKeyRequestUs > 200_000 else { return }
        lastKeyRequestUs = now
        channel?.sendControl(Message.build(.keyRequest))
    }

    private func reportStats() {
        let now = Clock.nowUs
        guard now - window.startUs >= 1_000_000 else { return }
        let seconds = Double(now - window.startUs) / 1e6
        let sorted = window.latencies.sorted()
        let p50 = sorted.isEmpty ? 0 : Double(sorted[sorted.count / 2]) / 1000
        let fps = Int((Double(window.frames) / seconds).rounded())
        let mbps = Double(window.bytes) * 8 / 1e6 / seconds
        var text = String(format: "%ld fps · trễ ~%.1f ms · %.1f Mbit/s · RTT %.1f ms", fps, p50, mbps, Double(clock.rttUs) / 1000)
        if window.skipped + window.errors > 0 { text += " · bỏ \(window.skipped + window.errors)" }
        window = StatsWindow()
        DispatchQueue.main.async { self.statsText = text }
    }

    private func publish(_ s: Status) {
        DispatchQueue.main.async {
            if self.status != .idle { self.status = s }
        }
    }
}

private struct StatsWindow {
    var startUs = Clock.nowUs
    var frames = 0
    var bytes = 0
    var skipped = 0
    var errors = 0
    var latencies: [Int64] = []

    mutating func add(latencyUs: Int64, bytes b: Int) {
        frames += 1
        bytes += b
        latencies.append(latencyUs)
    }
}
