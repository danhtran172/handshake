import SwiftUI

/// The "paper" design tokens from src/shared/paper.css, cream tone.
enum Paper {
    static let paper = Color(hex: 0xF5EFE1)
    static let paper2 = Color(hex: 0xECE3CF)
    static let edge = Color(hex: 0xD9CCB0)
    static let ink = Color(hex: 0x2A2723)
    static let ink2 = Color(hex: 0x5B554C)
    static let ink3 = Color(hex: 0x8C8476)
    static let accent = Color(hex: 0x2F5FB3)
    static let mark = Color(hex: 0xFFE36E)
    static let ok = Color(hex: 0x2F8A4C)

    /// Headings in New York, the iOS book serif (Sitka on the PC).
    static func title(_ size: CGFloat) -> Font { .system(size: size, weight: .semibold, design: .serif) }
}

extension Color {
    init(hex: UInt32) {
        self.init(red: Double((hex >> 16) & 0xFF) / 255, green: Double((hex >> 8) & 0xFF) / 255, blue: Double(hex & 0xFF) / 255)
    }
}

/// Card with a pen-drawn edge and a flat offset shadow, like `.card` in paper.css.
struct PaperCard: ViewModifier {
    func body(content: Content) -> some View {
        content
            .padding(16)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(
                RoundedRectangle(cornerRadius: 16, style: .continuous)
                    .fill(Paper.paper)
                    .shadow(color: Paper.ink.opacity(0.13), radius: 0, x: 2, y: 3)
            )
            .overlay(
                RoundedRectangle(cornerRadius: 16, style: .continuous)
                    .stroke(Paper.ink.opacity(0.25), lineWidth: 1)
            )
    }
}

struct PaperButton: ButtonStyle {
    var primary = false

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.body.weight(.semibold))
            .padding(.horizontal, 16)
            .padding(.vertical, 10)
            .foregroundColor(primary ? .white : Paper.ink)
            .background(
                RoundedRectangle(cornerRadius: 12, style: .continuous)
                    .fill(primary ? Paper.accent : Paper.paper2)
            )
            .overlay(
                RoundedRectangle(cornerRadius: 12, style: .continuous)
                    .stroke(Paper.ink.opacity(primary ? 0 : 0.3), lineWidth: 1)
            )
            .offset(y: configuration.isPressed ? 1 : 0)
    }
}

extension View {
    func paperCard() -> some View { modifier(PaperCard()) }
}
