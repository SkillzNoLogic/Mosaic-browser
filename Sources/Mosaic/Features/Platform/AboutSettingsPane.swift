import AppKit
import CEFBridge
import MosaicCore
import SwiftUI

/// Settings › About & Updates: versions, update checks, privacy statement and licences.
struct AboutSettingsPane: View {
    @EnvironmentObject private var model: BrowserModel

    var body: some View {
        Form {
            Section {
                HStack(spacing: 14) {
                    Image(nsImage: NSApp.applicationIconImage)
                        .resizable().frame(width: 64, height: 64).accessibilityHidden(true)
                    VStack(alignment: .leading, spacing: 3) {
                        Text("Mosaic").font(.title2.weight(.semibold))
                        Text("Version \(UpdateChecker.currentVersion) (build \(UpdateChecker.currentBuild))")
                            .foregroundStyle(.secondary).textSelection(.enabled)
                        Text("A two-engine browser for Apple Silicon Macs.").font(.callout).foregroundStyle(.secondary)
                    }
                }
                .padding(.vertical, 4)
            }

            Section("Updates") {
                UpdatesSection(updates: model.platform.updates)
            }

            Section("Components") {
                LabeledContent("Chromium") {
                    Text(model.chromiumAvailable ? MosaicCEF.chromiumVersion : "not bundled").textSelection(.enabled)
                }
                LabeledContent("Mozilla engine") {
                    Text(model.gecko.runtime.map { "\($0.name) \($0.version)" } ?? "not installed").textSelection(.enabled)
                }
                LabeledContent("Content blocker") {
                    Text(blockerDescription).textSelection(.enabled)
                }
            }

            Section("Privacy") {
                PrivacySection()
            }

            Section("Licences") {
                LicencesSection()
            }
        }
        .formStyle(.grouped)
    }

    private var blockerDescription: String {
        switch model.blocker.status {
        case .unavailable: return "not bundled"
        default: return "uBlock Origin " + (model.blocker.engineVersion ?? "(loading)") + " filtering engine"
        }
    }
}

// MARK: - Updates

private struct UpdatesSection: View {
    @EnvironmentObject private var model: BrowserModel
    @ObservedObject var updates: UpdateChecker

    var body: some View {
        Picker("Check for updates", selection: $updates.frequency) {
            Text("Only when I ask").tag(UpdateCheckFrequency.manual)
            Text("Once a day").tag(UpdateCheckFrequency.daily)
        }
        SettingsNote("A daily check asks GitHub for Mosaic’s latest release and Mozilla for the current Firefox ESR. Nothing else is sent.")
        HStack {
            Button {
                Task { await updates.checkNow() }
            } label: {
                if updates.isChecking { ProgressView().controlSize(.small) } else { Text("Check Now") }
            }
            .disabled(updates.isChecking)
            if let lastCheck = updates.lastCheck {
                Text("Last checked \(lastCheck.formatted(.relative(presentation: .named)))")
                    .font(.callout).foregroundStyle(.secondary)
            }
        }
        if let outcome = updates.outcome {
            switch outcome {
            case .available(let release):
                HStack {
                    Label(outcome.message, systemImage: "arrow.down.circle.fill").foregroundStyle(.blue)
                    Spacer()
                    if let page = release.pageURL {
                        Button("View Release") { open(page) }
                    }
                }
            case .upToDate:
                Label(outcome.message, systemImage: "checkmark.circle.fill").foregroundStyle(.green)
            case .needsAccess:
                VStack(alignment: .leading, spacing: 6) {
                    Label(outcome.message, systemImage: "lock.fill").foregroundStyle(.orange)
                        .fixedSize(horizontal: false, vertical: true)
                    Button("Open Releases Page") { open(ReleaseCheck.releasesPageURL) }
                        .help("Opens GitHub in a tab; sign in there if you have access")
                }
            case .rateLimited, .failed:
                Label(outcome.message, systemImage: "exclamationmark.triangle.fill").foregroundStyle(.orange)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
    }

    private func open(_ url: URL) {
        model.closeSettings()
        model.newTab(url: url.absoluteString)
    }
}

// MARK: - Privacy

private struct PrivacySection: View {
    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Mosaic has no telemetry, no accounts and no analytics.").font(.headline)
            SettingsNote("Apart from the pages you visit, Mosaic itself connects to these services, and only for these reasons:")
            ForEach(Self.connections, id: \.0) { connection in
                VStack(alignment: .leading, spacing: 1) {
                    Text(connection.0).font(.callout.weight(.medium))
                    Text(connection.1).font(.callout).foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
                .accessibilityElement(children: .combine)
            }
            SettingsNote("""
                The Mozilla engine is an unmodified Firefox ESR with its data reporting and automatic \
                updates switched off; like any browser engine it may still contact Mozilla for security \
                services such as certificate revocation checks.
                """)
        }
        .padding(.vertical, 2)
    }

    static let connections: [(String, String)] = [
        ("Filter lists (uBlock Origin’s lists, the Public Suffix List)",
         "ublockorigin.github.io, malware-filter.gitlab.io, pgl.yoyo.org and publicsuffix.org: refreshed every two days, or when you press Update Lists."),
        ("Mozilla engine install and update checks",
         "download.mozilla.org (and Mozilla’s CDN) when you install or update the engine; product-details.mozilla.org when you check for an engine update."),
        ("Chrome extensions",
         "clients2.google.com (the Chrome Web Store, which hands the download to Google’s servers) when you install an extension from a link or ID."),
        ("Mosaic update checks",
         "api.github.com, when you press Check Now or once a day if you chose that."),
    ]
}

// MARK: - Licences

private struct LicencesSection: View {
    @EnvironmentObject private var model: BrowserModel

    var body: some View {
        LicenceRow(
            title: "Mosaic",
            summary: "Mosaic is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License, version 3 or (at your option) any later version. It comes with no warranty. Source: github.com/SkillzNoLogic/Mosaic-browser",
            text: Licences.mosaic ?? "The licence file is missing from this build (LICENSE). The GPL v3 is at https://www.gnu.org/licenses/gpl-3.0.txt")

        LicenceRow(
            title: "Chromium Embedded Framework",
            summary: "BSD 3-Clause licence. © Marshall A. Greenblatt; portions © Google Inc.",
            text: Licences.cef)

        LicenceRow(
            title: "Chromium",
            summary: "BSD licence, plus the licences of Chromium’s many third-party components.") {
            Button("Show Chromium’s Credits") {
                model.closeSettings()
                model.newTab(url: "chrome://credits/", engine: .chromium)
            }
            .disabled(!model.chromiumAvailable)
        }

        LicenceRow(
            title: "Firefox ESR (Mozilla engine)",
            summary: "Mozilla Public License 2.0; downloaded unmodified from Mozilla. Firefox is a trademark of the Mozilla Foundation; Mosaic is not affiliated with Mozilla.") {
            Button("Show Firefox’s Licences") {
                model.closeSettings()
                model.newTab(url: "about:license", engine: .gecko)
            }
            .disabled(model.geckoRuntimeMissing)
        }

        LicenceRow(
            title: "uBlock Origin \(model.blocker.engineVersion ?? "")".trimmingCharacters(in: .whitespaces),
            summary: "Mosaic’s content blocker contains uBlock Origin’s network filtering engine, © Raymond Hill and contributors, under the GNU General Public License v3 or later.",
            text: Licences.uBlockOrigin ?? "The licence file is missing from this build (blocker/LICENSE-uBlock-Origin.txt). The GPL v3 is at https://www.gnu.org/licenses/gpl-3.0.txt")

        LicenceRow(title: "Filter lists", summary: "Each list has its own licence:") {
            VStack(alignment: .leading, spacing: 4) {
                ForEach(Licences.filterLists, id: \.0) { list in
                    Text("\(list.0): \(list.1)").font(.callout).foregroundStyle(.secondary)
                        .textSelection(.enabled)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
        }
    }
}

private struct LicenceRow<Extra: View>: View {
    let title: String
    let summary: String
    var text: String?
    @ViewBuilder var extra: Extra
    @State private var expanded = false

    init(title: String, summary: String, text: String? = nil, @ViewBuilder extra: () -> Extra = { EmptyView() }) {
        self.title = title
        self.summary = summary
        self.text = text
        self.extra = extra()
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(title).font(.body.weight(.medium))
            SettingsNote(summary)
            extra
            if let text {
                DisclosureGroup(expanded ? "Hide Licence Text" : "Show Licence Text", isExpanded: $expanded) {
                    LongTextView(text: text)
                }
            }
        }
        .padding(.vertical, 2)
    }
}

enum Licences {
    /// Mosaic's own licence (GPL v3 or later), copied into the app by the build.
    static var mosaic: String? {
        Bundle.main.resourceURL
            .map { $0.appendingPathComponent("LICENSE") }
            .flatMap { try? String(contentsOf: $0, encoding: .utf8) }
    }

    /// uBlock Origin's licence (GPL v3), copied into the app by the build.
    static var uBlockOrigin: String? {
        Bundle.main.resourceURL
            .map { $0.appendingPathComponent("blocker/LICENSE-uBlock-Origin.txt") }
            .flatMap { try? String(contentsOf: $0, encoding: .utf8) }
    }

    static let filterLists: [(String, String)] = [
        ("uBlock filters (Ads, Badware risks, Privacy, Quick fixes, Unbreak)", "GPL v3, github.com/uBlockOrigin/uAssets"),
        ("EasyList, EasyPrivacy", "GPL v3 or CC BY-SA 3.0, at your choice; easylist.to/pages/licence.html"),
        ("Online Malicious URL Blocklist", "see gitlab.com/malware-filter/urlhaus-filter#license"),
        ("Peter Lowe’s Ad and tracking server list", "Free to use under the list’s policy; pgl.yoyo.org/adservers/policy.php"),
        ("Public Suffix List", "Mozilla Public License 2.0; publicsuffix.org"),
    ]

    /// Chromium Embedded Framework's LICENSE.txt.
    static let cef = """
        Copyright (c) 2008-2020 Marshall A. Greenblatt. Portions Copyright (c)
        2006-2009 Google Inc. All rights reserved.

        Redistribution and use in source and binary forms, with or without
        modification, are permitted provided that the following conditions are
        met:

           * Redistributions of source code must retain the above copyright
        notice, this list of conditions and the following disclaimer.
           * Redistributions in binary form must reproduce the above
        copyright notice, this list of conditions and the following disclaimer
        in the documentation and/or other materials provided with the
        distribution.
           * Neither the name of Google Inc. nor the name Chromium Embedded
        Framework nor the names of its contributors may be used to endorse
        or promote products derived from this software without specific prior
        written permission.

        THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
        "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
        LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
        A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
        OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
        SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
        LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
        DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
        THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
        (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
        OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
        """
}
