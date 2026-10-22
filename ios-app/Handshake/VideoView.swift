import AVFoundation
import SwiftUI
import UIKit

struct VideoView: UIViewRepresentable {
    let session: StreamSession

    func makeUIView(context: Context) -> VideoLayerView { VideoLayerView(session: session) }

    func updateUIView(_ view: VideoLayerView, context: Context) { view.setNeedsLayout() }
}

/// Hosts the display layer and turns one-finger touches into pointer events over the picture
/// (down = click / start of a drag, like the web client's touch mode).
final class VideoLayerView: UIView {
    private let session: StreamSession

    init(session: StreamSession) {
        self.session = session
        super.init(frame: .zero)
        backgroundColor = .black
        isMultipleTouchEnabled = false
        layer.addSublayer(session.displayLayer)
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) is not supported") }

    override func layoutSubviews() {
        super.layoutSubviews()
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        session.displayLayer.frame = bounds
        CATransaction.commit()
    }

    /// Where the picture sits inside the view (aspect fit, letterboxed).
    private var pictureRect: CGRect { AVMakeRect(aspectRatio: session.videoSize, insideRect: bounds) }

    private func send(_ kind: UInt8, _ touches: Set<UITouch>) {
        guard let touch = touches.first else { return }
        let p = touch.location(in: self)
        let r = pictureRect
        guard r.width > 0, r.height > 0 else { return }
        session.sendInput(kind: kind, x: Double((p.x - r.minX) / r.width), y: Double((p.y - r.minY) / r.height))
    }

    override func touchesBegan(_ touches: Set<UITouch>, with event: UIEvent?) { send(1, touches) }
    override func touchesMoved(_ touches: Set<UITouch>, with event: UIEvent?) { send(2, touches) }
    override func touchesEnded(_ touches: Set<UITouch>, with event: UIEvent?) { send(3, touches) }
    override func touchesCancelled(_ touches: Set<UITouch>, with event: UIEvent?) { send(3, touches) }
}
