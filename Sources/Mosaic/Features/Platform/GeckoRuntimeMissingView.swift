import MosaicCore
import SwiftUI

/// Shown in the content area of a Mozilla tab when no Gecko runtime is installed (the
/// downloadable app on a Mac that never had Firefox). Offers the in-app install, or the
/// page in Chromium meanwhile. The shell places it in `ContentArea` for Gecko tabs when
/// `model.geckoRuntimeMissing` is true.
struct GeckoRuntimeMissingView: View {
    @EnvironmentObject private var model: BrowserModel
    let tab: BrowserTab

    var body: some View {
        RuntimeMissingContent(installer: model.platform.runtime, tab: tab)
    }
}

private struct RuntimeMissingContent: View {
    @EnvironmentObject private var model: BrowserModel
    @ObservedObject var installer: GeckoRuntimeInstaller
    let tab: BrowserTab

    var body: some View {
        VStack(spacing: 14) {
            Image(systemName: EngineKind.gecko.settingsSymbol)
                .font(.system(size: 34, weight: .medium))
                .foregroundStyle(ShellColors.tint(for: .gecko))
                .accessibilityHidden(true)
            Text("The Mozilla engine isn’t installed").font(.title3.weight(.medium))
            Text("""
                Mozilla tabs need Firefox ESR. Mosaic can download it from Mozilla (about 150 MB), check \
                that it is signed by Mozilla, and keep it in its own folder. A Firefox you already have is \
                never touched.
                """)
                .font(.callout)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
                .frame(maxWidth: 460)

            if installer.phase.isActive {
                InstallProgressView(installer: installer).frame(maxWidth: 380)
            } else {
                if case .failed = installer.phase {
                    Label(installer.phase.summary, systemImage: "exclamationmark.triangle.fill")
                        .font(.callout).foregroundStyle(.red).multilineTextAlignment(.center).frame(maxWidth: 460)
                }
                HStack(spacing: 10) {
                    Button("Download and Install") { installer.install() }
                        .keyboardShortcut(.defaultAction)
                    if model.chromiumAvailable, !tab.isStartPage {
                        Button("Open in Chromium Instead") { model.setEngine(.chromium) }
                    }
                }
                Button("Engine Settings…") { model.openSettings(.engine) }
                    .buttonStyle(.link)
            }
        }
        .padding(24)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Color(nsColor: .textBackgroundColor))
    }
}
