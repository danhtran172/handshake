import Foundation
import Network

/// Finds PCs running `hs-engine host` (Bonjour `_handshake._tcp`, advertised by Windows' own
/// mDNS responder). The first browse also triggers the Local Network permission prompt.
final class Discovery: ObservableObject {
    struct PC: Identifiable, Hashable {
        let name: String
        let endpoint: NWEndpoint
        var id: String { name }
    }

    @Published private(set) var pcs: [PC] = []
    private var browser: NWBrowser?

    func start() {
        guard browser == nil else { return }
        let b = NWBrowser(for: .bonjour(type: Wire.hostService, domain: nil), using: NWParameters())
        b.browseResultsChangedHandler = { [weak self] results, _ in
            var seen = Set<String>()
            var list: [PC] = []
            for r in results {
                guard case let .service(name, _, _, _) = r.endpoint, seen.insert(name).inserted else { continue }
                list.append(PC(name: name, endpoint: r.endpoint))
            }
            self?.pcs = list.sorted { $0.name < $1.name }
        }
        b.start(queue: .main)
        browser = b
    }

    func stop() {
        browser?.cancel()
        browser = nil
    }
}
