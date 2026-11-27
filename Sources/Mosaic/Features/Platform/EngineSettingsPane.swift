import AppKit
import CEFBridge
import MosaicCore
import SwiftUI

/// Settings › Engine: the two engines, the default engine for new tabs, the private Gecko
/// runtime (install, update, remove) and per-site engine rules.
struct EngineSettingsPane: View {
    @EnvironmentObject private var model: BrowserModel

    var body: some View {
        Form {
            Section {
                SettingsNote("""
                    Mosaic runs both engines side by side, and each tab uses one of them. Switching a tab \
                    (⌘⇧E, or the engine switch in the toolbar) needs no restart: the page stays on screen \
                    until the other engine has loaded it, and stays where it was if the other engine can’t.
                    """)
            }

            Section("Engines") {
                ChromiumCard()
                GeckoCard(installer: model.platform.runtime)
            }

            Section("Default Engine") {
                Picker("New tabs open in", selection: Binding(
                    get: { model.defaultEngine },
                    set: { model.setDefaultEngine($0) }
                )) {
                    ForEach(EngineKind.allCases) { kind in
                        Text("\(kind.displayName) (\(kind.engineName))").tag(kind)
                            .disabled(kind == .chromium && !model.chromiumAvailable)
                    }
                }
                .pickerStyle(.radioGroup)
                SettingsNote("""
                    Mosaic starts with this engine. While you browse, a new tab opens in the engine of the tab \
                    you’re on, and switching a tab’s engine makes that engine the default. Site rules below \
                    take precedence.
                    """)
            }

            Section("Mozilla Engine Runtime") {
                GeckoRuntimeSection(installer: model.platform.runtime)
            }

            Section("Site Rules") {
                EngineRulesSection(rules: model.platform.rules)
            }
        }
        .formStyle(.grouped)
    }
}

// MARK: - Engine cards

private struct EngineCardLayout<Status: View, Capabilities: View>: View {
    let kind: EngineKind
    let title: String
    let detail: String
    @ViewBuilder let status: Status
    @ViewBuilder let capabilities: Capabilities

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(alignment: .top, spacing: 12) {
                Image(systemName: kind.settingsSymbol)
                    .font(.system(size: 20, weight: .medium))
                    .foregroundStyle(Chrome.tint(for: kind))
                    .frame(width: 32, height: 32)
                    .background(Circle().fill(Chrome.tint(for: kind).opacity(0.12)))
                    .accessibilityHidden(true)
                VStack(alignment: .leading, spacing: 2) {
                    Text(title).font(.headline)
                    Text(detail).font(.callout).foregroundStyle(.secondary).textSelection(.enabled)
                }
                Spacer()
                status
            }
            VStack(alignment: .leading, spacing: 5) { capabilities }
                .padding(.leading, 44)
        }
        .padding(.vertical, 4)
        .accessibilityElement(children: .contain)
    }
}

private struct ChromiumCard: View {
    @EnvironmentObject private var model: BrowserModel

    var body: some View {
        let version = MosaicCEF.chromiumVersion
        let major = version.split(separator: ".").first.map(String.init) ?? ""
        EngineCardLayout(
            kind: .chromium, title: "Chromium (Blink)",
            detail: model.chromiumAvailable
                ? "Chromium \(version) · Chromium Embedded Framework \(major)"
                : "Not bundled in this build"
        ) {
            if model.chromiumAvailable {
                StatusBadge(text: "Running", tone: .positive)
            } else {
                StatusBadge(text: "Unavailable", tone: .negative)
            }
        } capabilities: {
            CapabilityRow(kind: .yes, text: "Runs inside Mosaic’s window")
            CapabilityRow(kind: .yes, text: "Built-in ad and tracker blocker (network filtering)")
            CapabilityRow(kind: .no, text: "Extensions don’t run in Chromium tabs: the embedded Chromium has no extension support")
            CapabilityRow(kind: .info, text: "No Google account, sync or Safe Browsing services")
        }
    }
}

private struct GeckoCard: View {
    @EnvironmentObject private var model: BrowserModel
    @ObservedObject var installer: GeckoRuntimeInstaller

    var body: some View {
        EngineCardLayout(kind: .gecko, title: "Mozilla (Gecko)", detail: detail) {
            switch GeckoRuntimeStatus(model: model) {
            case .notInstalled: StatusBadge(text: "Not installed", tone: .warning)
            case .running: StatusBadge(text: "Running", tone: .positive)
            case .starting: StatusBadge(text: "Starting…", tone: .neutral)
            case .stopped: StatusBadge(text: "Stopped", tone: .neutral)
            case .failed: StatusBadge(text: "Failed", tone: .negative)
            }
        } capabilities: {
            if case .failed(let reason) = GeckoRuntimeStatus(model: model) {
                CapabilityRow(kind: .no, text: "Last start failed: \(reason)")
            } else if case .stopped = GeckoRuntimeStatus(model: model) {
                CapabilityRow(kind: .info, text: "Starts when a Mozilla tab opens a page")
            }
            CapabilityRow(kind: .yes, text: "Chrome extensions run here, converted for Gecko")
            CapabilityRow(kind: .yes, text: "Built-in ad and tracker blocker (network filtering)")
            CapabilityRow(kind: .info, text: "Runs as a separate Firefox process docked behind Mosaic’s window, so a Firefox icon appears in the Dock")
        }
    }

    private var detail: String {
        guard let runtime = model.gecko.runtime else { return "No Gecko runtime installed" }
        return "\(runtime.name) \(runtime.version) · \(GeckoRuntimeSource(runtime: runtime, installer: installer).label)"
    }
}

/// The Mozilla engine's state, for display.
enum GeckoRuntimeStatus {
    case notInstalled, running, starting, stopped
    case failed(String)

    @MainActor init(model: BrowserModel) {
        if model.geckoRuntimeMissing {
            self = .notInstalled
            return
        }
        switch model.gecko.status {
        case .running: self = .running
        case .starting: self = .starting
        case .stopped: self = .stopped
        case .failed(let reason): self = .failed(reason)
        }
    }
}

/// Where the Gecko runtime in use comes from.
enum GeckoRuntimeSource {
    /// This profile's private copy, installed by Mosaic.
    case privateCopy
    /// A test profile using the private copy of the user's main Mosaic profile, read-only.
    case mainProfileCopy
    /// A Gecko browser already on the Mac (Firefox, LibreWolf, Zen…), run with Mosaic's own profile.
    case borrowed(String)

    @MainActor init(runtime: GeckoRuntime, installer: GeckoRuntimeInstaller) {
        let path = runtime.appURL.standardizedFileURL.path
        if path == installer.managedAppURL.standardizedFileURL.path {
            self = .privateCopy
        } else if path == GeckoRuntime.managedAppURL(applicationSupport: AppPaths.standardSupport).standardizedFileURL.path {
            self = .mainProfileCopy
        } else {
            self = .borrowed(runtime.appURL.deletingLastPathComponent().path)
        }
    }

    var label: String {
        switch self {
        case .privateCopy: "Mosaic’s private copy"
        case .mainProfileCopy: "shared from your main Mosaic profile"
        case .borrowed(let folder): "borrowed from \(folder)"
        }
    }
}

// MARK: - Runtime install / update / remove

struct GeckoRuntimeSection: View {
    @EnvironmentObject private var model: BrowserModel
    @ObservedObject var installer: GeckoRuntimeInstaller
    @State private var confirmRemove = false

    var body: some View {
        let phase = installer.phase
        VStack(alignment: .leading, spacing: 10) {
            if phase.isActive {
                InstallProgressView(installer: installer)
            } else if let installed = installer.installedRuntime {
                installedView(installed)
            } else {
                notInstalledView
            }
            if !phase.isActive, case .failed = phase {
                Label(phase.summary, systemImage: "exclamationmark.triangle.fill")
                    .foregroundStyle(.red).font(.callout).textSelection(.enabled)
            } else if !phase.isActive, phase != .idle {
                Label(phase.summary, systemImage: phase == .cancelled ? "xmark.circle" : "checkmark.circle.fill")
                    .foregroundStyle(phase == .cancelled ? Color.secondary : Color.green).font(.callout)
            }
            if let message = installer.message, !phase.isActive {
                SettingsNote(message).textSelection(.enabled)
            }
        }
        .padding(.vertical, 2)
        .confirmationDialog("Remove the Mozilla engine?", isPresented: $confirmRemove) {
            Button("Remove", role: .destructive) { Task { await installer.remove() } }
        } message: {
            Text("Mozilla tabs will stop working until you install it again. Your Mozilla browsing data and extensions are kept.")
        }
    }

    private func installedView(_ installed: GeckoRuntime) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                VStack(alignment: .leading, spacing: 2) {
                    Text("Firefox ESR \(installed.version)").font(.headline)
                    Text(installer.managedAppURL.path)
                        .font(.caption).foregroundStyle(.secondary).textSelection(.enabled).lineLimit(1)
                        .truncationMode(.middle)
                }
                Spacer()
                if installer.isUpdateAvailable, let latest = installer.latestVersion {
                    StatusBadge(text: "\(latest.numberString) available", tone: .warning)
                } else if installer.latestVersion != nil {
                    StatusBadge(text: "Up to date", tone: .positive)
                }
            }
            HStack(spacing: 8) {
                if installer.isUpdateAvailable {
                    Button("Update to \(installer.latestVersion?.numberString ?? "latest")") { installer.install() }
                        .buttonStyle(.borderedProminent)
                }
                Button {
                    Task { await installer.checkForUpdate() }
                } label: {
                    if installer.isChecking { ProgressView().controlSize(.small) } else { Text("Check for Updates") }
                }
                .disabled(installer.isChecking)
                Menu("More") {
                    Button("Reinstall Firefox ESR") { installer.install() }
                    Button("Show in Finder") { NSWorkspace.shared.activateFileViewerSelecting([installer.managedAppURL]) }
                    Divider()
                    Button("Remove…", role: .destructive) { confirmRemove = true }
                }
                .fixedSize()
                .accessibilityLabel("More runtime actions")
            }
            SettingsNote(lastCheckText + " Updating closes Mozilla tabs’ pages for a moment and reopens them; your Mozilla profile is kept.")
        }
    }

    private var notInstalledView: some View {
        VStack(alignment: .leading, spacing: 8) {
            if let runtime = model.gecko.runtime {
                Text("Using \(runtime.name) \(runtime.version), \(GeckoRuntimeSource(runtime: runtime, installer: installer).label).")
                    .font(.headline)
                SettingsNote("""
                    Mosaic can install its own private Firefox ESR, which it is tested with and which never \
                    touches another browser’s profile. It is downloaded from Mozilla (about 150 MB) and \
                    checked against Mozilla’s signature before use.
                    """)
                Button("Install Firefox ESR") { installer.install() }
            } else {
                Text("The Mozilla engine is not installed.").font(.headline)
                SettingsNote("""
                    Mozilla tabs need Firefox ESR. Mosaic downloads it from Mozilla (about 150 MB), checks that \
                    it is signed by Mozilla, and keeps it in Mosaic’s own folder. A Firefox you already have is \
                    never touched. Chromium tabs work meanwhile.
                    """)
                Button("Download and Install Firefox ESR") { installer.install() }
                    .buttonStyle(.borderedProminent)
            }
        }
    }

    private var lastCheckText: String {
        guard let date = installer.lastCheck else { return "Not checked for updates yet." }
        return "Last checked \(date.formatted(.relative(presentation: .named)))."
    }
}

/// Progress bar, step and Cancel button while installing.
struct InstallProgressView: View {
    @ObservedObject var installer: GeckoRuntimeInstaller

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            if let fraction = installer.phase.fraction {
                ProgressView(value: fraction)
            } else {
                ProgressView().progressViewStyle(.linear)
            }
            HStack {
                Text(installer.phase.summary).font(.callout).foregroundStyle(.secondary)
                Spacer()
                Button("Cancel") { installer.cancel() }
                    .disabled(!installer.phase.isCancellable)
                    .help(installer.phase.isCancellable ? "Stop and discard the download" : "Finishing; this step can’t be interrupted")
            }
        }
        .accessibilityElement(children: .combine)
        .accessibilityLabel(installer.phase.summary)
    }
}

// MARK: - Site rules

private struct EngineRulesSection: View {
    @EnvironmentObject private var model: BrowserModel
    @ObservedObject var rules: EngineRuleStore
    @State private var newDomain = ""
    @State private var newEngine: EngineKind = .chromium
    @State private var error: String?

    var body: some View {
        if let domain = model.currentSiteDomain {
            LabeledContent("Always open \(domain) in") {
                HStack(spacing: 6) {
                    ForEach(EngineKind.allCases) { kind in
                        Button(kind.displayName) { model.addEngineRuleForCurrentSite(kind) }
                            .disabled(!model.isEngineUsable(kind))
                    }
                }
            }
        }

        ForEach(rules.rules) { rule in
            RuleRow(rule: rule, rules: rules)
        }
        if rules.rules.isEmpty {
            Text("No site rules yet.").foregroundStyle(.secondary)
        }

        HStack(spacing: 8) {
            TextField("Site", text: $newDomain, prompt: Text("example.com"))
                .onSubmit(add)
                .accessibilityLabel("Site for a new rule")
            Picker("Engine", selection: $newEngine) {
                ForEach(EngineKind.allCases) { Text($0.displayName).tag($0) }
            }
            .labelsHidden()
            .fixedSize()
            Button("Add Rule", action: add)
                .disabled(newDomain.trimmingCharacters(in: .whitespaces).isEmpty)
        }
        if let error {
            Text(error).font(.callout).foregroundStyle(.red)
        }
        SettingsNote("""
            A rule picks the engine when you open the site from the address bar, the start page or as a new \
            tab. It covers the site’s subdomains unless you switch that off. Links you follow inside a page \
            stay in the tab’s engine, so a page is never reloaded mid-visit.
            """)
    }

    private func add() {
        if rules.set(domain: newDomain, engine: newEngine) {
            newDomain = ""
            error = nil
        } else {
            error = "“\(newDomain)” isn’t a site address. Type a domain such as example.com."
        }
    }
}

private struct RuleRow: View {
    let rule: EngineRule
    @ObservedObject var rules: EngineRuleStore

    var body: some View {
        HStack(spacing: 10) {
            VStack(alignment: .leading, spacing: 2) {
                Text(rule.domain).font(.body.weight(.medium))
                Toggle("Include subdomains", isOn: Binding(
                    get: { rule.includesSubdomains },
                    set: { value in
                        var updated = rule
                        updated.includesSubdomains = value
                        rules.update(updated)
                    }
                ))
                .toggleStyle(.checkbox)
                .controlSize(.small)
            }
            Spacer()
            Picker("Engine for \(rule.domain)", selection: Binding(
                get: { EngineKind(rawValue: rule.engine) ?? .gecko },
                set: { kind in
                    var updated = rule
                    updated.engine = kind.rawValue
                    rules.update(updated)
                }
            )) {
                ForEach(EngineKind.allCases) { Text($0.displayName).tag($0) }
            }
            .labelsHidden()
            .fixedSize()
            Button {
                rules.remove(rule)
            } label: {
                Image(systemName: "trash")
            }
            .buttonStyle(.borderless)
            .help("Delete rule")
            .accessibilityLabel("Delete rule for \(rule.domain)")
        }
    }
}
