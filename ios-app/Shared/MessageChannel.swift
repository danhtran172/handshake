import Foundation
import Network

/// Framed Handshake messages over one TCP NWConnection (LAN, USB hotspot, or later usbmux).
/// Video sends are counted until the stack has taken them, so a sender can skip frames when the
/// link falls behind instead of letting latency pile up in buffers.
final class MessageChannel {
    enum State {
        case ready
        case closed(String)
    }

    let connection: NWConnection
    private let queue: DispatchQueue
    private let lock = NSLock()
    private var pending = 0
    private var finished = false

    /// Called on `queue`.
    var onMessage: ((MsgType, Data) -> Void)?
    /// Called on `queue`; `.closed` comes exactly once.
    var onState: ((State) -> Void)?

    init(endpoint: NWEndpoint, queue: DispatchQueue) {
        self.queue = queue
        let tcp = NWProtocolTCP.Options()
        tcp.noDelay = true
        tcp.connectionTimeout = 3
        let params = NWParameters(tls: nil, tcp: tcp)
        params.serviceClass = .interactiveVideo
        connection = NWConnection(to: endpoint, using: params)
    }

    var pendingVideo: Int {
        lock.lock()
        defer { lock.unlock() }
        return pending
    }

    func start() {
        connection.stateUpdateHandler = { [weak self] state in
            guard let self else { return }
            switch state {
            case .ready:
                self.onState?(.ready)
                self.receiveHeader()
            case .waiting(let error), .failed(let error):
                // .waiting means "no route right now": report it so the caller can try another path
                self.close(error.localizedDescription)
            case .cancelled:
                self.close("cancelled")
            default:
                break
            }
        }
        connection.start(queue: queue)
    }

    func sendControl(_ message: Data) {
        connection.send(content: message, completion: .contentProcessed { [weak self] error in
            if let error { self?.close(error.localizedDescription) }
        })
    }

    func sendVideo(_ message: Data) {
        lock.lock()
        pending += 1
        lock.unlock()
        connection.send(content: message, completion: .contentProcessed { [weak self] error in
            guard let self else { return }
            self.lock.lock()
            self.pending -= 1
            self.lock.unlock()
            if let error { self.close(error.localizedDescription) }
        })
    }

    func close(_ reason: String = "closed") {
        lock.lock()
        let first = !finished
        finished = true
        lock.unlock()
        guard first else { return }
        connection.cancel()
        queue.async { self.onState?(.closed(reason)) }
    }

    private func receiveHeader() {
        connection.receive(minimumIncompleteLength: Wire.headerSize, maximumLength: Wire.headerSize) { [weak self] data, _, _, error in
            guard let self else { return }
            guard let data, data.count == Wire.headerSize, error == nil else {
                self.close(error?.localizedDescription ?? "closed by peer")
                return
            }
            var r = ByteReader(data)
            let length = Int(r.u32())
            let type = r.u8()
            guard length >= 1, length <= Wire.maxMessage else {
                self.close("bad length \(length)")
                return
            }
            if length == 1 {
                self.deliver(type, Data())
                self.receiveHeader()
                return
            }
            self.connection.receive(minimumIncompleteLength: length - 1, maximumLength: length - 1) { data, _, _, error in
                guard let data, data.count == length - 1, error == nil else {
                    self.close(error?.localizedDescription ?? "closed by peer")
                    return
                }
                self.deliver(type, data)
                self.receiveHeader()
            }
        }
    }

    private func deliver(_ type: UInt8, _ payload: Data) {
        if let t = MsgType(rawValue: type) { onMessage?(t, payload) }
    }
}
