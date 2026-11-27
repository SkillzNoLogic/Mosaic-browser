import CEFBridge
import MosaicCore
import SwiftUI

/// Settings › Privacy & Security: the content blocker (lists, My filters, sites it
/// is off for), tracking protection, HTTPS-only mode, DNS over HTTPS, cookies and
/// site data, clearing browsing data, private tabs and site permissions.
struct PrivacySettingsPane: View {
    @EnvironmentObject private var model: BrowserModel

    var body: some View {
        PrivacySettingsForm(model: model, privacy: model.privacy, blocker: model.blocker)
    }
}

private struct PrivacySettingsForm: View {
    let model: BrowserModel
    @ObservedObject var privacy: PrivacyController
    @ObservedObject var blocker: ContentBlocker

    var body: some View {
        Form {
            BlockerSection(model: model, blocker: blocker)
            FilterListsSection(blocker: blocker)
            MyFiltersSection(blocker: blocker)
            AllowlistSection(blocker: blocker)
            TrackingSection(privacy: privacy)
            HTTPSSection(privacy: privacy)
            DNSSection(privacy: privacy)
            SiteDataSection(model: model, privacy: privacy)
            ClearDataSection(privacy: privacy)
            PrivateBrowsingSection(model: model)
            SitePermissionsSection(store: privacy.sitePermissions)
        }
        .formStyle(.grouped)
        .sheet(isPresented: $privacy.showClearDataSheet) {
            ClearBrowsingDataSheet(privacy: privacy)
        }
    }
}

// MARK: - Content blocker

private struct BlockerSection: View {
    let model: BrowserModel
    @ObservedObject var blocker: ContentBlocker

    var body: some View {
        Section {
            if blocker.status == .unavailable {
                Label("The content blocker is not included in this build. Run Scripts/bootstrap.sh and rebuild.",
                      systemImage: "exclamationmark.triangle")
                    .foregroundStyle(.orange)
            } else {
                Toggle(isOn: Binding(get: { blocker.isEnabled }, set: { model.setBlockerEnabled($0) })) {
                    Text("Block ads and trackers")
                    Text("uBlock Origin’s filtering engine and filter lists, built into Mosaic. Works in both engines.")
                }
                Toggle(isOn: Binding(get: { blocker.cosmeticEnabled }, set: { blocker.setCosmeticEnabled($0) })) {
                    Text("Hide leftover ad elements")
                    Text("Applies the lists’ element-hiding (cosmetic) filters to pages as they load. Scriptlets and procedural filters (such as :has-text) are not supported.")
                }
                .disabled(!blocker.isEnabled)
                LabeledContent("Status", value: statusText)
            }
        } header: {
            Text("Content Blocker")
        }
    }

    private var statusText: String {
        switch blocker.status {
        case .unavailable: return "Unavailable"
        case .loading: return "Loading filters…"
        case .failed(let message): return message
        case .ready(let filters):
            let number = { (value: Int) in NumberFormatter.localizedString(from: NSNumber(value: value), number: .decimal) }
            let cosmetic = blocker.cosmeticStats.generic + blocker.cosmeticStats.specific
            let version = blocker.engineVersion.map { " · uBO \($0)" } ?? ""
            return "\(number(filters)) network filters · \(number(cosmetic)) element-hiding rules\(version)"
        }
    }
}

private struct FilterListsSection: View {
    @ObservedObject var blocker: ContentBlocker

    private static let groupTitles: [(id: String, title: String)] = [
        ("default", "uBlock Origin"), ("ads", "Ads"), ("privacy", "Privacy"), ("malware", "Malware and phishing"),
        ("annoyances", "Cookie notices and annoyances"), ("multipurpose", "Multipurpose"),
    ]

    var body: some View {
        Section {
            ForEach(Self.groupTitles, id: \.id) { group in
                let lists = FilterListCatalog.all.filter { $0.group == group.id }
                if !lists.isEmpty {
                    DisclosureGroup {
                        ForEach(lists) { list in row(list) }
                    } label: {
                        let count = lists.filter { blocker.enabledListIDs.contains($0.id) }.count
                        LabeledContent(group.title, value: "\(count) of \(lists.count) on")
                    }
                }
            }
            HStack {
                VStack(alignment: .leading, spacing: 2) {
                    Text(updatedText).font(.callout)
                    if let message = blocker.lastUpdateMessage {
                        Text(message).font(.caption).foregroundStyle(.secondary)
                    }
                }
                Spacer()
                if blocker.isUpdating {
                    ProgressView().controlSize(.small)
                }
                Button("Update Now") { Task { await blocker.updateLists() } }
                    .disabled(blocker.isUpdating || blocker.status == .unavailable)
            }
        } header: {
            Text("Filter Lists")
        } footer: {
            Text("Lists marked as uBlock Origin’s defaults ship with Mosaic. Others are downloaded when you turn them on, and refreshed every two days.")
                .frame(maxWidth: .infinity, alignment: .leading)
        }
        .disabled(blocker.status == .unavailable)
    }

    private func row(_ list: FilterList) -> some View {
        let isDefault = FilterListCatalog.defaults.contains(list)
        return Toggle(isOn: Binding(
            get: { blocker.enabledListIDs.contains(list.id) },
            set: { blocker.setList(list, enabled: $0) }
        )) {
            Text(list.title)
            if !list.summary.isEmpty || isDefault {
                Text(isDefault ? "Default" : list.summary)
            }
        }
        .accessibilityHint(list.summary)
    }

    private var updatedText: String {
        guard let date = blocker.listsDate else { return "Some lists have not been downloaded yet" }
        let formatter = RelativeDateTimeFormatter()
        formatter.unitsStyle = .full
        return "Updated \(formatter.localizedString(for: date, relativeTo: Date()))"
    }
}

private struct MyFiltersSection: View {
    @ObservedObject var blocker: ContentBlocker
    @State private var text = ""
    @State private var checks: [FilterCheck] = []
    @State private var loaded = false
    @State private var saveError: String?

    var body: some View {
        Section {
            TextEditor(text: $text)
                .font(.system(.body, design: .monospaced))
                .frame(minHeight: 110, maxHeight: 220)
                .scrollContentBackground(.hidden)
                .accessibilityLabel("My filters")
                .accessibilityHint("One filter per line, in uBlock Origin syntax")
                .onChange(of: text) { _, newValue in checks = blocker.validate(filters: newValue) }

            let problems = checks.filter { !$0.isEffective && $0.kind != .comment }
            let notes = checks.filter { $0.isEffective && !$0.message.isEmpty }
            ForEach(problems + notes, id: \.line) { check in
                Label {
                    Text("Line \(check.line): ").bold() + Text(check.message)
                } icon: {
                    Image(systemName: check.kind == .error ? "xmark.octagon.fill" : "exclamationmark.triangle.fill")
                        .foregroundStyle(check.kind == .error ? .red : .orange)
                }
                .font(.callout)
            }
            HStack {
                Text(summary).font(.caption).foregroundStyle(.secondary)
                Spacer()
                Button("Revert") { text = blocker.userFilters }
                    .disabled(text == blocker.userFilters)
                Button("Apply") {
                    do {
                        try blocker.saveUserFilters(text)
                        saveError = nil
                    } catch {
                        saveError = error.localizedDescription
                    }
                }
                .keyboardShortcut("s", modifiers: .command)
                .disabled(text == blocker.userFilters || checks.contains { $0.kind == .error })
            }
            if let saveError {
                Text(saveError).font(.caption).foregroundStyle(.red)
            }
        } header: {
            Text("My Filters")
        } footer: {
            Text("uBlock Origin syntax, one per line: ||ads.example.com^ blocks a server, example.com##.banner hides an element on one site, ##.banner hides it everywhere, @@ and #@# make exceptions. Lines starting with ! are comments.")
                .frame(maxWidth: .infinity, alignment: .leading)
        }
        .disabled(blocker.status == .unavailable)
        .onAppear {
            guard !loaded else { return }
            loaded = true
            text = blocker.userFilters
            checks = blocker.validate(filters: text)
        }
    }

    private var summary: String {
        let network = checks.filter { $0.kind == .network }.count
        let cosmetic = checks.filter { $0.kind == .cosmetic }.count
        if !blocker.canValidate { return "Filters are checked once the blocker has loaded." }
        return "\(network) network, \(cosmetic) element-hiding"
    }
}

private struct AllowlistSection: View {
    @ObservedObject var blocker: ContentBlocker
    @State private var newSite = ""

    var body: some View {
        Section {
            let sites = blocker.settings.allowlist.sorted()
            if sites.isEmpty {
                Text("The blocker is on for every site.").foregroundStyle(.secondary)
            }
            ForEach(sites, id: \.self) { site in
                HStack {
                    Text(site)
                    Spacer()
                    Button("Turn Blocker Back On") { blocker.setAllowlisted(false, site: site) }
                        .buttonStyle(.borderless)
                        .accessibilityLabel("Turn the blocker back on for \(site)")
                }
            }
            HStack {
                TextField("Add a site", text: $newSite, prompt: Text("example.com"))
                    .onSubmit(add)
                Button("Add", action: add).disabled(BlockerSettings.normalize(newSite) == nil)
            }
        } header: {
            Text("Sites Where the Blocker Is Off")
        } footer: {
            Text("A site covers its subdomains. You can also switch the blocker off for the current site from the shield in the toolbar.")
                .frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    private func add() {
        guard BlockerSettings.normalize(newSite) != nil else { return }
        blocker.setAllowlisted(true, site: newSite)
        newSite = ""
    }
}

// MARK: - Tracking, HTTPS, DNS

private struct TrackingSection: View {
    @ObservedObject var privacy: PrivacyController

    var body: some View {
        Section("Tracking") {
            Toggle(isOn: binding(\.stripTrackingParameters)) {
                Text("Remove tracking parameters from links")
                Text("Strips fbclid, gclid, utm_source and similar from addresses before they load (Firefox’s query-stripping list plus campaign parameters).")
            }
            Toggle(isOn: binding(\.blockThirdPartyCookies)) {
                Text("Block third-party cookies")
                Text("Embedded content from other sites cannot set or read cookies. A few sign-in and payment flows may need an exception: turn this off to allow them.")
            }
        }
    }

    private func binding(_ key: WritableKeyPath<PrivacySettings, Bool>) -> Binding<Bool> {
        Binding(get: { privacy.settings[keyPath: key] }, set: { value in privacy.update { $0[keyPath: key] = value } })
    }
}

private struct HTTPSSection: View {
    @ObservedObject var privacy: PrivacyController
    @State private var newSite = ""

    var body: some View {
        Section {
            Toggle(isOn: Binding(get: { privacy.settings.httpsOnly },
                                 set: { value in privacy.update { $0.httpsOnly = value } })) {
                Text("HTTPS-Only Mode")
                Text("Opens every site over a secure connection. If a site has no secure version, Mosaic asks before opening it over HTTP. Local network addresses are left alone.")
            }
            if privacy.settings.httpsOnly {
                ForEach(privacy.settings.httpsExceptions, id: \.self) { site in
                    HStack {
                        Text(site)
                        Spacer()
                        Button("Remove") { privacy.update { $0.setHTTPException(false, site: site) } }
                            .buttonStyle(.borderless)
                            .accessibilityLabel("Remove the HTTP exception for \(site)")
                    }
                }
                ForEach(privacy.sessionHTTPExceptions.sorted(), id: \.self) { site in
                    LabeledContent(site, value: "Allowed until Mosaic quits")
                }
                HStack {
                    TextField("Allow a site over HTTP", text: $newSite, prompt: Text("example.com"))
                        .onSubmit(add)
                    Button("Add", action: add).disabled(BlockerSettings.normalize(newSite) == nil)
                }
            }
        } header: {
            Text("Secure Connections")
        }
    }

    private func add() {
        guard BlockerSettings.normalize(newSite) != nil else { return }
        let site = newSite
        privacy.update { $0.setHTTPException(true, site: site) }
        newSite = ""
    }
}

private struct DNSSection: View {
    @ObservedObject var privacy: PrivacyController
    @State private var customTemplate = ""

    var body: some View {
        let setting = privacy.settings.dnsOverHTTPS
        Section {
            Picker("DNS over HTTPS", selection: Binding(
                get: { setting.mode },
                set: { mode in privacy.update { $0.dnsOverHTTPS.mode = mode } }
            )) {
                Text("Off (use the network’s DNS)").tag(DNSOverHTTPS.Mode.off)
                Text("On, fall back to the network’s DNS").tag(DNSOverHTTPS.Mode.automatic)
                Text("On, never fall back (strict)").tag(DNSOverHTTPS.Mode.strict)
            }
            if setting.mode != .off {
                Picker("Provider", selection: Binding(
                    get: { setting.providerID },
                    set: { id in privacy.update { $0.dnsOverHTTPS.providerID = id } }
                )) {
                    ForEach(DNSOverHTTPS.providers) { provider in
                        Text(provider.name).tag(provider.id)
                    }
                    Text("Custom…").tag("custom")
                }
                if setting.providerID == "custom" {
                    HStack {
                        TextField("Resolver URL", text: $customTemplate, prompt: Text("https://dns.example/dns-query"))
                            .onSubmit(saveCustom)
                        Button("Use", action: saveCustom)
                            .disabled(!DNSOverHTTPS.isValidTemplate(customTemplate))
                    }
                    if !customTemplate.isEmpty, !DNSOverHTTPS.isValidTemplate(customTemplate) {
                        Text("Enter an https:// address.").font(.caption).foregroundStyle(.red)
                    }
                } else if let provider = DNSOverHTTPS.providers.first(where: { $0.id == setting.providerID }) {
                    Text(provider.note).font(.caption).foregroundStyle(.secondary)
                }
            }
            let dnsErrors = privacy.chromiumPreferenceErrors.filter { $0.key.hasPrefix("dns_over_https") }
            if !dnsErrors.isEmpty {
                Label("Chromium could not apply this setting (\(dnsErrors.values.first ?? "")); it applies to Mozilla tabs only. Restart Mosaic to try again.",
                      systemImage: "exclamationmark.triangle")
                    .font(.caption)
                    .foregroundStyle(.orange)
            }
        } header: {
            Text("DNS over HTTPS")
        } footer: {
            Text("Encrypts the lookups that turn site names into addresses, so the network cannot see or alter them. Strict mode fails to open sites when the resolver is unreachable.")
                .frame(maxWidth: .infinity, alignment: .leading)
        }
        .onAppear { customTemplate = setting.customTemplate }
    }

    private func saveCustom() {
        guard DNSOverHTTPS.isValidTemplate(customTemplate) else { return }
        let template = customTemplate
        privacy.update { $0.dnsOverHTTPS.customTemplate = template }
    }
}

// MARK: - Site data

private struct SiteDataSection: View {
    let model: BrowserModel
    @ObservedObject var privacy: PrivacyController
    @State private var showList = false
    @State private var query = ""
    @State private var confirmClearAll = false

    var body: some View {
        Section {
            Toggle(isOn: Binding(get: { privacy.settings.clearCookiesOnExit },
                                 set: { value in privacy.update { $0.clearCookiesOnExit = value } })) {
                Text("Delete cookies and site data when Mosaic quits")
                Text("You will be signed out of sites each time. History and passwords are kept.")
            }
            DisclosureGroup(isExpanded: $showList) {
                siteList
            } label: {
                LabeledContent("Stored site data", value: privacy.siteData.isEmpty ? "" : "\(privacy.siteData.count) sites")
            }
            .onChange(of: showList) { _, open in if open { Task { await privacy.loadSiteData() } } }
        } header: {
            Text("Cookies and Site Data")
        }
        .confirmationDialog("Delete all cookies and site data?", isPresented: $confirmClearAll) {
            Button("Delete All", role: .destructive) { Task { await privacy.clearAllSiteData() } }
        } message: {
            Text("You will be signed out of all sites, in both engines.")
        }
    }

    @ViewBuilder private var siteList: some View {
        HStack {
            TextField("Search sites", text: $query)
                .textFieldStyle(.roundedBorder)
            if privacy.isLoadingSiteData { ProgressView().controlSize(.small) }
            Button("Refresh") { Task { await privacy.loadSiteData() } }
            Button("Delete All…", role: .destructive) { confirmClearAll = true }
                .disabled(privacy.siteData.isEmpty)
        }
        if !privacy.siteDataIncludesGecko {
            Text("Mozilla’s data is listed once a Mozilla tab has been opened in this session.")
                .font(.caption).foregroundStyle(.secondary)
        }
        let shown = privacy.siteData.filter { query.isEmpty || $0.site.localizedCaseInsensitiveContains(query) }
        if shown.isEmpty, !privacy.isLoadingSiteData {
            Text(query.isEmpty ? "No site has stored any data." : "No matching sites.").foregroundStyle(.secondary)
        }
        ForEach(shown) { entry in
            HStack {
                VStack(alignment: .leading, spacing: 1) {
                    Text(entry.site)
                    Text(describe(entry)).font(.caption).foregroundStyle(.secondary)
                }
                Spacer()
                Button("Delete") { Task { await privacy.clearSiteData(entry) } }
                    .buttonStyle(.borderless)
                    .accessibilityLabel("Delete data stored by \(entry.site)")
            }
        }
    }

    private func describe(_ entry: SiteDataEntry) -> String {
        var parts: [String] = []
        if entry.chromiumCookies > 0 { parts.append("Chromium: \(entry.chromiumCookies) cookie\(entry.chromiumCookies == 1 ? "" : "s")") }
        if entry.geckoCookies > 0 || entry.geckoStorageBytes > 0 {
            var gecko = "Mozilla: \(entry.geckoCookies) cookie\(entry.geckoCookies == 1 ? "" : "s")"
            if entry.geckoStorageBytes > 0 {
                gecko += ", " + ByteCountFormatter.string(fromByteCount: entry.geckoStorageBytes, countStyle: .file)
            }
            parts.append(gecko)
        }
        return parts.joined(separator: " · ")
    }
}

private struct ClearDataSection: View {
    @ObservedObject var privacy: PrivacyController

    var body: some View {
        Section {
            HStack {
                VStack(alignment: .leading, spacing: 2) {
                    Text("Clear browsing data")
                    Text(privacy.lastClearSummary ?? "History, cookies, cache, download history and form entries, by time range.")
                        .font(.caption).foregroundStyle(.secondary)
                }
                Spacer()
                Button("Clear Browsing Data…") { privacy.showClearDataSheet = true }
                    .help("Clear Browsing Data (⇧⌘⌫)")
            }
        }
    }
}

private struct PrivateBrowsingSection: View {
    let model: BrowserModel

    var body: some View {
        Section {
            HStack(alignment: .top) {
                PrivateBrowsingBadge()
                VStack(alignment: .leading, spacing: 4) {
                    Text("Private tabs keep cookies, storage and cache in memory only, never record history, and forget everything when the last private tab closes.")
                    Text("Private tabs run on the Chromium engine. Mozilla private windows are not supported in Mosaic yet, so a private tab cannot be switched to Mozilla.")
                        .font(.caption).foregroundStyle(.secondary)
                }
                Spacer()
                Button("New Private Tab") {
                    model.closeSettings()
                    model.newPrivateTab()
                }
                .help("New Private Tab (⇧⌘N)")
                .disabled(!model.chromiumAvailable)
            }
        } header: {
            Text("Private Browsing")
        }
    }
}

// MARK: - Site permissions

private struct SitePermissionsSection: View {
    let store: SitePermissionStore
    @State private var entries: [SitePermissionStore.Entry] = []

    var body: some View {
        Section {
            if entries.isEmpty {
                Text("No decisions remembered yet. When a site asks for the camera, microphone, location, notifications or pop-ups, your answer is listed here.")
                    .foregroundStyle(.secondary)
            }
            ForEach(entries, id: \.self) { entry in
                HStack {
                    Image(systemName: symbol(entry.permission)).frame(width: 20).accessibilityHidden(true)
                    VStack(alignment: .leading, spacing: 1) {
                        Text(entry.site)
                        Text(title(entry.permission)).font(.caption).foregroundStyle(.secondary)
                    }
                    Spacer()
                    Picker(selection: Binding(
                        get: { entry.decision },
                        set: { decision in
                            store.set(decision, for: entry.permission, site: entry.site)
                            reload()
                        }
                    )) {
                        Text("Allow").tag(SitePermissionStore.Decision.allow)
                        Text("Block").tag(SitePermissionStore.Decision.block)
                    } label: {
                        Text("\(title(entry.permission)) for \(entry.site)")
                    }
                    .labelsHidden()
                    .fixedSize()
                    Button {
                        store.set(nil, for: entry.permission, site: entry.site)
                        reload()
                    } label: {
                        Image(systemName: "minus.circle")
                    }
                    .buttonStyle(.borderless)
                    .help("Forget (the site will ask again)")
                    .accessibilityLabel("Forget \(title(entry.permission)) decision for \(entry.site)")
                }
            }
        } header: {
            Text("Site Permissions")
        }
        .onAppear(perform: reload)
    }

    private func reload() { entries = store.entries() }

    private func title(_ permission: SitePermissionStore.Permission) -> String {
        switch permission {
        case .camera: "Camera"
        case .microphone: "Microphone"
        case .location: "Location"
        case .notifications: "Notifications"
        case .popups: "Pop-ups"
        case .autoplay: "Autoplay"
        case .clipboard: "Clipboard"
        }
    }

    private func symbol(_ permission: SitePermissionStore.Permission) -> String {
        switch permission {
        case .camera: "camera"
        case .microphone: "mic"
        case .location: "location"
        case .notifications: "bell"
        case .popups: "macwindow.on.rectangle"
        case .autoplay: "play.rectangle"
        case .clipboard: "doc.on.clipboard"
        }
    }
}
