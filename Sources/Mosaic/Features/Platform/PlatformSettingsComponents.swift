import SwiftUI

/// Small status capsule: "Running", "Not installed"…
struct StatusBadge: View {
    enum Tone { case positive, neutral, warning, negative }

    let text: String
    var tone: Tone = .neutral

    var body: some View {
        Text(text)
            .font(.caption.weight(.medium))
            .padding(.horizontal, 7)
            .padding(.vertical, 2)
            .foregroundStyle(color)
            .background(Capsule().fill(color.opacity(0.12)))
            .overlay(Capsule().strokeBorder(color.opacity(0.35)))
            .accessibilityLabel(text)
    }

    private var color: Color {
        switch tone {
        case .positive: .green
        case .neutral: .secondary
        case .warning: .orange
        case .negative: .red
        }
    }
}

/// One line of "what this engine can do": a check, a cross or an information mark.
struct CapabilityRow: View {
    enum Kind { case yes, no, info }

    let kind: Kind
    let text: String

    var body: some View {
        Label {
            Text(text).font(.callout).fixedSize(horizontal: false, vertical: true)
        } icon: {
            Image(systemName: symbol).foregroundStyle(color)
        }
        .accessibilityElement(children: .combine)
        .accessibilityLabel((kind == .no ? "Not supported: " : "") + text)
    }

    private var symbol: String {
        switch kind {
        case .yes: "checkmark.circle.fill"
        case .no: "xmark.circle.fill"
        case .info: "info.circle.fill"
        }
    }

    private var color: Color {
        switch kind {
        case .yes: .green
        case .no: .red
        case .info: .secondary
        }
    }
}


/// A scrollable block of licence or other long text, selectable.
struct LongTextView: View {
    let text: String
    var height: CGFloat = 260

    var body: some View {
        ScrollView {
            Text(text)
                .font(.system(.caption, design: .monospaced))
                .textSelection(.enabled)
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(10)
        }
        .frame(height: height)
        .background(RoundedRectangle(cornerRadius: 8).fill(Color.primary.opacity(0.04)))
        .overlay(RoundedRectangle(cornerRadius: 8).strokeBorder(Color.primary.opacity(0.1)))
    }
}

extension EngineKind {
    /// SF Symbol for the engine in settings.
    var settingsSymbol: String {
        switch self {
        case .gecko: "flame"
        case .chromium: "circle.circle"
        }
    }
}
