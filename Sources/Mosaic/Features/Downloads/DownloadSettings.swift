import Foundation
import MosaicCore

/// Settings › Downloads, kept in this profile's preferences (`AppPaths.defaults`).
@MainActor
final class DownloadSettings: ObservableObject {
    private enum Keys {
        static let folder = "downloads.folder"
        static let askWhereToSave = "downloads.askWhereToSave"
        static let openSafeFiles = "downloads.openSafeFiles"
        static let notify = "downloads.notifyWhenFinished"
        static let rules = "downloads.organizationRules"
        static let fdmMode = "downloads.fdm.mode"
        static let fdmMinimumMB = "downloads.fdm.minimumMB"
        static let fdmCookies = "downloads.fdm.passCookies"
        static let fdmContextMenu = "downloads.fdm.contextMenu"
    }

    private let defaults = AppPaths.defaults

    /// Where downloads are saved. Default: ~/Downloads.
    @Published var folderPath: String { didSet { save(folderPath, Keys.folder); changed() } }
    @Published var askWhereToSave: Bool { didSet { save(askWhereToSave, Keys.askWhereToSave) } }
    @Published var openSafeFiles: Bool { didSet { save(openSafeFiles, Keys.openSafeFiles) } }
    @Published var notifyWhenFinished: Bool { didSet { save(notifyWhenFinished, Keys.notify) } }
    @Published var rules: DownloadOrganizationRules {
        didSet { defaults.set(try? JSONEncoder().encode(rules), forKey: Keys.rules) }
    }
    @Published var fdmMode: FDMMode { didSet { save(fdmMode.rawValue, Keys.fdmMode); changed() } }
    /// Downloads smaller than this many megabytes stay in Mosaic; 0 sends every size.
    @Published var fdmMinimumMB: Int { didSet { save(fdmMinimumMB, Keys.fdmMinimumMB) } }
    @Published var fdmPassCookies: Bool { didSet { save(fdmPassCookies, Keys.fdmCookies) } }
    @Published var fdmContextMenu: Bool { didSet { save(fdmContextMenu, Keys.fdmContextMenu); changed() } }

    /// Called when a setting the engines need to know about changed.
    var onEngineSettingsChange: (() -> Void)?

    static var defaultFolder: URL {
        FileManager.default.urls(for: .downloadsDirectory, in: .userDomainMask).first
            ?? FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Downloads", isDirectory: true)
    }

    init() {
        let defaults = AppPaths.defaults
        folderPath = defaults.string(forKey: Keys.folder) ?? Self.defaultFolder.path
        askWhereToSave = defaults.bool(forKey: Keys.askWhereToSave)
        openSafeFiles = defaults.bool(forKey: Keys.openSafeFiles)
        notifyWhenFinished = defaults.object(forKey: Keys.notify) as? Bool ?? true
        rules = defaults.data(forKey: Keys.rules).flatMap { try? JSONDecoder().decode(DownloadOrganizationRules.self, from: $0) }
            ?? DownloadOrganizationRules()
        fdmMode = defaults.string(forKey: Keys.fdmMode).flatMap(FDMMode.init(rawValue:)) ?? .off
        fdmMinimumMB = defaults.integer(forKey: Keys.fdmMinimumMB)
        fdmPassCookies = defaults.bool(forKey: Keys.fdmCookies)
        fdmContextMenu = defaults.object(forKey: Keys.fdmContextMenu) as? Bool ?? true
    }

    var folder: URL { URL(fileURLWithPath: folderPath, isDirectory: true) }

    var fdmPolicy: FDMPolicy { FDMPolicy(mode: fdmMode, minimumBytes: Int64(max(0, fdmMinimumMB)) * 1_000_000) }

    /// The download folder with the home folder abbreviated, for display.
    var folderDisplayPath: String { (folderPath as NSString).abbreviatingWithTildeInPath }

    private func save(_ value: Any, _ key: String) { defaults.set(value, forKey: key) }
    private func changed() { onEngineSettingsChange?() }
}
