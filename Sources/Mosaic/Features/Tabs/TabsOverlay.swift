import SwiftUI

/// Mount point for the tabs area's overlays. The shell places it once over the whole
/// window, in every Mosaic:
///
///     .overlay { TabsOverlay() }        // needs .environmentObject(model)
///
/// It shows the tab search panel (⌘⇧A), the "already open in another tab" notice and the
/// crash-recovery banner. Where it draws nothing it takes no clicks, so the Gecko page
/// behind the window keeps receiving them.
struct TabsOverlay: View {
    @EnvironmentObject private var model: BrowserModel

    var body: some View {
        TabsOverlayContent(model: model, feature: model.tabsFeature)
    }
}

private struct TabsOverlayContent: View {
    @ObservedObject var model: BrowserModel
    @ObservedObject var feature: TabsFeature
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        ZStack {
            if feature.isTabSearchVisible {
                Color.black.opacity(0.08)
                    .contentShape(Rectangle())
                    .onTapGesture { model.hideTabSearch() }
                    .accessibilityLabel("Close tab search")
                    .accessibilityAddTraits(.isButton)
                VStack {
                    TabSearchPanel(model: model)
                        .padding(.top, 88)
                    Spacer(minLength: 0)
                }
                .transition(reduceMotion ? .opacity : .opacity.combined(with: .scale(scale: 0.98, anchor: .top)))
            }
            VStack(spacing: 0) {
                if feature.offersCrashRecovery {
                    CrashRecoveryBanner(model: model, feature: feature)
                        .padding(.top, 92)
                }
                Spacer(minLength: 0)
                if let offer = feature.duplicateOffer {
                    DuplicateTabNotice(model: model, offer: offer)
                        .padding(.bottom, 64)
                        .transition(.opacity)
                }
            }
        }
        .animation(reduceMotion ? nil : .easeOut(duration: 0.15), value: feature.isTabSearchVisible)
        .animation(reduceMotion ? nil : .easeOut(duration: 0.15), value: feature.duplicateOffer)
    }
}

/// "<page> is already open in another tab. [Switch to Tab]" (non-blocking; goes away
/// after ten seconds).
struct DuplicateTabNotice: View {
    @ObservedObject var model: BrowserModel
    let offer: TabsFeature.DuplicateOffer

    var body: some View {
        HStack(spacing: 10) {
            Image(systemName: "square.on.square").foregroundStyle(.secondary).accessibilityHidden(true)
            Text(message)
                .font(.callout)
                .lineLimit(2)
            Button("Switch to Tab") { model.acceptDuplicateOffer() }
                .buttonStyle(.borderedProminent)
                .controlSize(.small)
                .help(offer.closesNewTab ? "Go to the open tab and close this one" : "Go to the open tab")
            Button {
                model.dismissDuplicateOffer()
            } label: {
                Image(systemName: "xmark").font(.system(size: 10, weight: .bold)).frame(width: 20, height: 20)
            }
            .buttonStyle(.plain)
            .help("Dismiss")
            .accessibilityLabel("Dismiss")
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 9)
        .frame(maxWidth: 560)
        .tabsCard(cornerRadius: 12)
        .accessibilityElement(children: .contain)
        .task(id: offer.id) {
            try? await Task.sleep(nanoseconds: 10_000_000_000)
            if model.tabsFeature.duplicateOffer?.id == offer.id { model.dismissDuplicateOffer() }
        }
    }

    private var message: String {
        let place = offer.workspaceName.map { "in \($0)" } ?? "in another tab"
        return "“\(offer.title)” is already open \(place)."
    }
}

/// Shown after a crash when the startup setting is "Start page".
struct CrashRecoveryBanner: View {
    @ObservedObject var model: BrowserModel
    @ObservedObject var feature: TabsFeature

    var body: some View {
        let count = model.previousSessionPageCount
        HStack(spacing: 12) {
            Image(systemName: "exclamationmark.arrow.circlepath")
                .font(.title2)
                .foregroundStyle(.orange)
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 2) {
                Text("Mosaic quit unexpectedly").font(.headline)
                Text("Restore the \(count) \(count == 1 ? "tab" : "tabs") from your last session?")
                    .font(.callout)
                    .foregroundStyle(.secondary)
            }
            Spacer(minLength: 8)
            Button("Not Now") { model.dismissCrashRecovery() }
                .controlSize(.regular)
            Button("Restore Tabs") { model.restorePreviousSession() }
                .buttonStyle(.borderedProminent)
                .controlSize(.regular)
        }
        .padding(14)
        .frame(maxWidth: 560)
        .tabsCard()
        .accessibilityElement(children: .contain)
        .accessibilityLabel("Mosaic quit unexpectedly. Restore tabs from your last session?")
    }
}
