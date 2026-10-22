import Network
import ReplayKit
import SwiftUI

@main
struct HandshakeApp: App {
    var body: some Scene {
        WindowGroup {
            ContentView()
                .preferredColorScheme(.light)
        }
    }
}

struct ContentView: View {
    @StateObject private var discovery = Discovery()
    @StateObject private var session = StreamSession()
    @AppStorage("manualHost") private var manualHost = ""

    private var viewerShown: Binding<Bool> {
        Binding(get: { session.status != .idle }, set: { if !$0 { session.disconnect() } })
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 20) {
                VStack(alignment: .leading, spacing: 4) {
                    Text("Handshake").font(Paper.title(34))
                    Text("Máy tính và iPhone, chung một bàn làm việc.")
                        .foregroundColor(Paper.ink2)
                }
                displayCard
                mirrorCard
                Text("Cắm cáp: bật Điểm truy cập cá nhân trên iPhone rồi cắm vào PC, PC sẽ hiện ở danh sách trên như khi dùng Wi‑Fi.")
                    .font(.footnote)
                    .foregroundColor(Paper.ink3)
            }
            .padding(20)
        }
        .foregroundColor(Paper.ink)
        .background(Paper.paper.ignoresSafeArea())
        .onAppear { discovery.start() }
        .fullScreenCover(isPresented: viewerShown) { ViewerView(session: session) }
    }

    private var displayCard: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Màn hình phụ").font(Paper.title(22))
            Text("iPhone hiện màn hình PC, chạm để điều khiển. Trên PC bật engine host ở trang Hiệu năng.")
                .font(.subheadline)
                .foregroundColor(Paper.ink2)
            if discovery.pcs.isEmpty {
                HStack(spacing: 8) {
                    ProgressView()
                    Text("Đang tìm PC trong mạng…").foregroundColor(Paper.ink3)
                }
            }
            ForEach(discovery.pcs) { pc in
                Button {
                    session.connect(to: pc.endpoint, name: pc.name)
                } label: {
                    HStack {
                        Image(systemName: "desktopcomputer")
                        Text(pc.name)
                        Spacer()
                        Image(systemName: "chevron.right").foregroundColor(Paper.ink3)
                    }
                }
                .buttonStyle(PaperButton())
            }
            HStack {
                TextField("Địa chỉ PC, ví dụ 192.168.1.10", text: $manualHost)
                    .keyboardType(.numbersAndPunctuation)
                    .textInputAutocapitalization(.never)
                    .autocorrectionDisabled()
                    .padding(10)
                    .background(RoundedRectangle(cornerRadius: 10).stroke(Paper.ink.opacity(0.3)))
                Button("Kết nối") { connectManual() }
                    .buttonStyle(PaperButton(primary: true))
                    .disabled(manualHost.trimmingCharacters(in: .whitespaces).isEmpty)
            }
        }
        .paperCard()
    }

    private var mirrorCard: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Phát màn hình lên PC").font(Paper.title(22))
            Text("Trên PC chạy hs-engine mirror, rồi bấm nút dưới và chọn Bắt đầu phát. iPhone tự tìm PC.")
                .font(.subheadline)
                .foregroundColor(Paper.ink2)
            HStack(spacing: 12) {
                BroadcastPicker()
                    .frame(width: 52, height: 52)
                    .background(Circle().fill(Paper.mark))
                Text("Bắt đầu / dừng phát").font(.body.weight(.semibold))
            }
        }
        .paperCard()
    }

    private func connectManual() {
        let text = manualHost.trimmingCharacters(in: .whitespaces)
        let parts = text.split(separator: ":")
        let port = parts.count > 1 ? UInt16(parts[1]) ?? Wire.hostPort : Wire.hostPort
        guard let host = parts.first, let nwPort = NWEndpoint.Port(rawValue: port) else { return }
        session.connect(to: .hostPort(host: NWEndpoint.Host(String(host)), port: nwPort), name: String(host))
    }
}

struct ViewerView: View {
    @ObservedObject var session: StreamSession

    private var connectingText: String? {
        if case .connecting(let text) = session.status { return text }
        return nil
    }

    var body: some View {
        ZStack(alignment: .topTrailing) {
            VideoView(session: session)
                .ignoresSafeArea()
            if let text = connectingText {
                VStack(spacing: 12) {
                    ProgressView().tint(.white)
                    Text(text).foregroundColor(.white)
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            }
            HStack(spacing: 8) {
                if !session.statsText.isEmpty {
                    Text(session.statsText)
                        .font(.system(.caption2, design: .monospaced))
                        .padding(.horizontal, 8)
                        .padding(.vertical, 4)
                        .background(Capsule().fill(Color.black.opacity(0.45)))
                        .foregroundColor(.white)
                }
                Button {
                    session.disconnect()
                } label: {
                    Image(systemName: "xmark.circle.fill")
                        .font(.title2)
                        .foregroundColor(.white.opacity(0.8))
                }
            }
            .padding(10)
        }
        .background(Color.black)
        .statusBarHidden()
        .persistentSystemOverlays(.hidden)
    }
}

/// System button that starts/stops the ReplayKit broadcast with our extension preselected.
struct BroadcastPicker: UIViewRepresentable {
    func makeUIView(context: Context) -> RPSystemBroadcastPickerView {
        let picker = RPSystemBroadcastPickerView(frame: CGRect(x: 0, y: 0, width: 52, height: 52))
        picker.preferredExtension = Self.extensionId
        picker.showsMicrophoneButton = false
        return picker
    }

    func updateUIView(_ view: RPSystemBroadcastPickerView, context: Context) {}

    /// Read from the embedded .appex: sideloading tools may rewrite bundle ids.
    static var extensionId: String? {
        guard let dir = Bundle.main.builtInPlugInsURL,
              let items = try? FileManager.default.contentsOfDirectory(at: dir, includingPropertiesForKeys: nil),
              let appex = items.first(where: { $0.pathExtension == "appex" }) else { return nil }
        return Bundle(url: appex)?.bundleIdentifier
    }
}
