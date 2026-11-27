import CryptoKit
import Foundation

/// The parts of Mosaic's self-update that need no app: which release file to download, how
/// its signature is checked, whether this copy of Mosaic can replace itself, and the script
/// that swaps the app bundle after Mosaic has quit.
///
/// Trust: Mosaic is not signed with an Apple Developer ID yet, so macOS cannot say who made
/// a downloaded build. Every release zip is therefore signed with Mosaic's own Ed25519
/// release key (`Scripts/sign-update.swift`, run by `Scripts/release.sh`), and the app
/// installs only a zip whose signature verifies against the public key compiled in here.
/// A compromised GitHub account or download server cannot produce that signature.
public enum UpdatePackage {
    /// Raw 32-byte Ed25519 public key, base64. The private half never leaves the release
    /// machine's Keychain (service `dev.mosaic.release-signing`).
    public static let signingPublicKey = "Z61pZlb7FV58rHTravn/GMwYPCiFZA5/QSvpFehHi/c="

    /// Release asset names, as `Scripts/release.sh` writes them.
    public static func zipName(for version: SoftwareVersion) -> String {
        "Mosaic-\(version.numberString)-macos-arm64.zip"
    }

    public static func signatureName(for version: SoftwareVersion) -> String {
        zipName(for: version) + ".sig"
    }

    // MARK: Signature

    /// True if `signature` (base64, as in the `.sig` file; surrounding whitespace allowed)
    /// is a valid Ed25519 signature of `data` by `publicKey` (base64 raw key).
    public static func verify(_ data: Data, signature: String, publicKey: String = signingPublicKey) -> Bool {
        guard let keyData = Data(base64Encoded: publicKey),
              let key = try? Curve25519.Signing.PublicKey(rawRepresentation: keyData),
              let signatureData = Data(base64Encoded: signature.trimmingCharacters(in: .whitespacesAndNewlines))
        else { return false }
        return key.isValidSignature(signatureData, for: data)
    }

    // MARK: Where Mosaic is running from

    public enum LocationProblem: Equatable, Sendable {
        /// macOS runs a quarantined app from a randomised read-only copy until it is moved.
        case translocated
        /// A disk image or another read-only volume.
        case readOnlyVolume
        /// The folder holding Mosaic.app cannot be written by this user.
        case folderNotWritable(String)

        public var message: String {
            switch self {
            case .translocated:
                "macOS is running Mosaic from a temporary copy. Move Mosaic to the Applications folder and open it again to update automatically."
            case .readOnlyVolume:
                "Mosaic is running from a disk image or another read-only disk. Drag it to the Applications folder to update automatically."
            case .folderNotWritable(let folder):
                "Mosaic cannot replace itself because your account cannot write to \(folder)."
            }
        }
    }

    /// Whether the app bundle at `appPath` can be replaced in place.
    public static func locationProblem(appPath: String, volumeIsReadOnly: Bool, parentIsWritable: Bool) -> LocationProblem? {
        if appPath.contains("/AppTranslocation/") { return .translocated }
        if volumeIsReadOnly { return .readOnlyVolume }
        if !parentIsWritable { return .folderNotWritable((appPath as NSString).deletingLastPathComponent) }
        return nil
    }

    // MARK: Installer script

    /// A shell script, run detached as Mosaic quits, that waits for process `$1` to exit,
    /// moves the new bundle into place with the old one kept aside until the move succeeds,
    /// and (when `$4` is "relaunch") starts the new Mosaic. Arguments:
    /// `$1` PID, `$2` installed app path, `$3` staged new app path, `$4` relaunch|stay,
    /// `$5` "direct" to start the executable itself (keeps test profiles' environment) or
    /// "open" to go through Launch Services; remaining arguments go to a direct launch.
    public static let installerScript = """
        #!/bin/bash
        # Written by Mosaic to finish an update after it has quit.
        set -u
        pid="$1"; target="$2"; staged="$3"; relaunch="$4"; how="$5"; shift 5
        for _ in $(seq 1 600); do kill -0 "$pid" 2>/dev/null || break; sleep 0.1; done
        if kill -0 "$pid" 2>/dev/null; then echo "Mosaic did not quit; update not installed"; exit 1; fi
        backup="${target%.app}.previous-$$.app"
        if ! mv "$target" "$backup"; then echo "could not move the old app aside"; exit 1; fi
        if ! mv "$staged" "$target" 2>/dev/null && ! ditto "$staged" "$target"; then
            rm -rf "$target"; mv "$backup" "$target"; echo "could not install the new app; old app restored"; exit 1
        fi
        rm -rf "$backup" "$staged"
        xattr -dr com.apple.quarantine "$target" 2>/dev/null
        echo "installed $target"
        if [ "$relaunch" = relaunch ]; then
            if [ "$how" = direct ]; then
                nohup "$target/Contents/MacOS/Mosaic" "$@" >/dev/null 2>&1 &
            else
                open "$target"
            fi
        fi
        exit 0
        """
}

extension ReleaseCheck {
    /// A file attached to a GitHub release.
    public struct Asset: Equatable, Sendable {
        public let name: String
        public let size: Int
        /// The public download link (works only for public repositories).
        public let browserURL: URL?
        /// The API link; with `Accept: application/octet-stream` and a token it downloads
        /// the file from a private repository too.
        public let apiURL: URL?
    }

    /// The release's assets, from the same JSON `parseRelease` reads.
    public static func parseAssets(_ data: Data) -> [Asset] {
        guard let object = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any],
              let assets = object["assets"] as? [[String: Any]]
        else { return [] }
        return assets.compactMap { asset in
            guard let name = asset["name"] as? String else { return nil }
            return Asset(
                name: name, size: asset["size"] as? Int ?? 0,
                browserURL: (asset["browser_download_url"] as? String).flatMap(URL.init(string:)),
                apiURL: (asset["url"] as? String).flatMap(URL.init(string:)))
        }
    }

    /// The zip and its signature for `version`, or nil if the release lacks either (for
    /// example a release made before updates were signed).
    public static func updateAssets(in assets: [Asset], version: SoftwareVersion) -> (zip: Asset, signature: Asset)? {
        guard let zip = assets.first(where: { $0.name == UpdatePackage.zipName(for: version) }),
              let signature = assets.first(where: { $0.name == UpdatePackage.signatureName(for: version) })
        else { return nil }
        return (zip, signature)
    }

    /// An update feed other than GitHub, for testing: only HTTPS, or HTTP to this Mac.
    public static func isAcceptableFeedOverride(_ url: URL) -> Bool {
        switch url.scheme?.lowercased() {
        case "https": return true
        case "http": return ["127.0.0.1", "localhost", "::1"].contains(url.host?.lowercased() ?? "")
        default: return false
        }
    }
}
