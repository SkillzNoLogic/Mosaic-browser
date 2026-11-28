import AppKit
import MosaicCore

/// Installs Chrome extensions (`.crx`) and keeps them loaded in the Gecko engine.
///
/// A package is unpacked and converted once, on install (`ExtensionStore`). The
/// converted copy is then loaded into Gecko every time the engine starts.
@MainActor
final class ExtensionManager: ObservableObject {
    @Published private(set) var extensions: [InstalledExtension] = []
    @Published private(set) var isBusy = false
    @Published var errorMessage: String?
    /// Gecko add-on IDs currently loaded in the running engine.
    @Published private(set) var loaded: Set<String> = []

    private let store: ExtensionStore?
    private unowned let gecko: GeckoEngine

    init(gecko: GeckoEngine) {
        self.gecko = gecko
        do {
            let store = try ExtensionStore(root: AppPaths.extensions)
            self.store = store
            extensions = store.extensions
        } catch {
            store = nil
            errorMessage = "Extensions are unavailable: \(error.localizedDescription)"
        }
    }

    func icon(for ext: InstalledExtension) -> NSImage? {
        store?.iconURL(for: ext).flatMap { NSImage(contentsOf: $0) }
    }

    func isInstalled(chromeID: String) -> Bool {
        extensions.contains { $0.id == chromeID }
    }

    /// What an extension asked for, read from its converted manifest.
    struct Permissions: Equatable {
        /// API permissions ("storage", "tabs"…).
        var apis: [String] = []
        /// Sites it can read and change, as match patterns; "<all_urls>" for every site.
        var sites: [String] = []

        var readsAllSites: Bool {
            sites.contains { $0 == "<all_urls>" || $0.hasPrefix("*://*/") || $0.hasPrefix("http://*/") || $0.hasPrefix("https://*/") }
        }
    }

    func permissions(for ext: InstalledExtension) -> Permissions {
        guard let store,
              let data = try? Data(contentsOf: store.geckoDirectory(for: ext.id).appendingPathComponent("manifest.json")),
              let manifest = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
        else { return Permissions() }
        var result = Permissions()
        let declared = (manifest["permissions"] as? [String] ?? []) + (manifest["host_permissions"] as? [String] ?? [])
        for entry in declared {
            if entry == "<all_urls>" || entry.contains("://") { result.sites.append(entry) } else { result.apis.append(entry) }
        }
        for script in manifest["content_scripts"] as? [[String: Any]] ?? [] {
            result.sites += script["matches"] as? [String] ?? []
        }
        result.apis = Array(Set(result.apis)).sorted()
        var seen = Set<String>()
        result.sites = result.sites.filter { seen.insert($0).inserted }
        return result
    }

    // MARK: - Installing

    /// Installs from a Chrome Web Store link or a bare extension ID.
    func install(fromWebStore input: String) async {
        guard let id = ChromeWebStore.extensionID(from: input) else {
            errorMessage = "That is not a Chrome Web Store link or extension ID."
            return
        }
        await perform {
            var request = URLRequest(url: ChromeWebStore.downloadURL(forID: id))
            request.timeoutInterval = 60
            let (data, response) = try await URLSession.shared.data(for: request)
            let status = (response as? HTTPURLResponse)?.statusCode ?? 0
            guard status == 200, !data.isEmpty else {
                throw InstallError.notAvailable(status)
            }
            return (data, id)
        }
    }

    /// Installs a `.crx` (or zipped extension) from disk.
    func install(file url: URL) async {
        await perform { (try Data(contentsOf: url), nil) }
    }

    private enum InstallError: LocalizedError {
        case notAvailable(Int)

        var errorDescription: String? {
            switch self {
            case .notAvailable(let status):
                "The Chrome Web Store did not provide this extension (HTTP \(status)). It may be unlisted, paid, or removed."
            }
        }
    }

    private func perform(_ fetch: @escaping () async throws -> (Data, String?)) async {
        guard let store, !isBusy else { return }
        isBusy = true
        errorMessage = nil
        defer { isBusy = false }
        do {
            let (data, knownID) = try await fetch()
            // Unpacking and scanning touch a lot of files; keep that off the main thread.
            let record = try await Task.detached(priority: .userInitiated) {
                try store.install(package: data, knownID: knownID)
            }.value
            extensions = store.extensions
            if gecko.isRunning, record.enabled {
                // Replacing an installed extension: Gecko swaps it in place.
                await load(record)
            }
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    func remove(_ ext: InstalledExtension) async {
        guard let store else { return }
        await unload(ext)
        try? store.remove(id: ext.id)
        extensions = store.extensions
    }

    func setEnabled(_ enabled: Bool, for ext: InstalledExtension) async {
        guard let store else { return }
        try? store.setEnabled(enabled, id: ext.id)
        extensions = store.extensions
        if enabled {
            if gecko.isRunning { await load(ext) }
        } else {
            await unload(ext)
        }
    }

    // MARK: - Gecko

    /// Called whenever the Gecko engine has (re)started.
    func loadAllIntoGecko() async {
        loaded.removeAll()
        for ext in extensions where ext.enabled {
            await load(ext, quiet: true)
        }
    }

    func ensureLoaded(_ ext: InstalledExtension) async {
        if !loaded.contains(ext.geckoID) { await load(ext) }
    }

    /// `quiet` is for re-loading an extension the user already has: whatever
    /// welcome tab it would open on install is suppressed.
    private func load(_ ext: InstalledExtension, quiet: Bool = false) async {
        guard let store else { return }
        do {
            try await gecko.call("installExtension", store.geckoDirectory(for: ext.id).path, quiet)
            loaded.insert(ext.geckoID)
        } catch {
            loaded.remove(ext.geckoID)
            errorMessage = "\(ext.name) could not be loaded into Gecko: \(error.localizedDescription)"
        }
    }

    private func unload(_ ext: InstalledExtension) async {
        loaded.remove(ext.geckoID)
        guard gecko.isRunning else { return }
        _ = try? await gecko.call("uninstallExtension", ext.geckoID)
    }
}
