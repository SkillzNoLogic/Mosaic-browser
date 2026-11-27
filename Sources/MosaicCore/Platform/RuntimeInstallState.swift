import Foundation

/// Why installing or updating the Gecko runtime failed, in words a person can act on.
public enum RuntimeInstallFailure: Error, Equatable, Sendable {
    /// No internet connection.
    case offline
    /// Another transport error (DNS, TLS, timeout…); carries the system's description.
    case network(String)
    /// Mozilla's server answered with an HTTP error.
    case server(status: Int)
    /// A redirect tried to leave HTTPS or Mozilla's servers.
    case untrustedSource(String)
    case notEnoughSpace(neededMB: Int)
    /// The download is not a readable disk image.
    case damagedDownload
    case noAppInDiskImage
    /// codesign rejected the app: unsigned, altered, or damaged.
    case signatureInvalid(String)
    /// Intact signature, but not Mozilla's.
    case wrongSigner(found: String?)
    case notAppleSilicon
    /// The app is signed by Mozilla but is not a Gecko browser Mosaic can drive.
    case notGeckoBrowser
    /// The running Mozilla engine did not quit, so its files could not be replaced.
    case engineBusy
    case copyFailed(String)
    case removeFailed(String)

    public var message: String {
        switch self {
        case .offline:
            "You appear to be offline. Connect to the internet and try again."
        case .network(let detail):
            "The download from Mozilla did not complete (\(detail)). Check your connection and try again."
        case .server(let status):
            "Mozilla’s download server returned an error (HTTP \(status)). Try again in a few minutes."
        case .untrustedSource(let host):
            "The download was redirected away from Mozilla’s secure servers (to \(host)), so Mosaic stopped it."
        case .notEnoughSpace(let needed):
            "There is not enough free disk space. The Mozilla engine needs about \(needed) MB; free some space and try again."
        case .damagedDownload:
            "The downloaded file is damaged or is not a disk image. Try again; if it keeps happening, your network may be altering downloads."
        case .noAppInDiskImage:
            "The downloaded disk image does not contain an app. Nothing was installed."
        case .signatureInvalid(let detail):
            "The downloaded Firefox failed its code-signature check, so it was not installed. It may be damaged or tampered with."
                + (detail.isEmpty ? "" : " (\(detail))")
        case .wrongSigner(let found):
            "The downloaded app is not signed by Mozilla, so it was not installed."
                + (found.map { " It is signed by: \($0)." } ?? " It has no developer signature.")
        case .notAppleSilicon:
            "The downloaded Firefox does not run natively on Apple Silicon, so it was not installed."
        case .notGeckoBrowser:
            "The downloaded app is not a Firefox build Mosaic can use. Nothing was installed."
        case .engineBusy:
            "The Mozilla engine did not quit, so it could not be replaced. Close Mozilla tabs and try again."
        case .copyFailed(let detail):
            "The engine could not be copied into place (\(detail)). Your previous engine, if any, was kept."
        case .removeFailed(let detail):
            "The engine could not be removed (\(detail))."
        }
    }

    /// Classifies a URLSession error.
    public static func from(urlErrorCode code: Int, description: String) -> RuntimeInstallFailure {
        // NSURLErrorNotConnectedToInternet, NSURLErrorNetworkConnectionLost, NSURLErrorDataNotAllowed
        if [-1009, -1005, -1020].contains(code) { return .offline }
        return .network(description)
    }
}

/// Where an install/update of the Gecko runtime is. Driven by `RuntimeInstallMachine`.
public enum RuntimeInstallPhase: Equatable, Sendable {
    case idle
    /// `expected` is nil until the server reports a length.
    case downloading(received: Int64, expected: Int64?)
    /// Mounting the disk image and checking the app's code signature.
    case verifying
    /// Quitting the running Mozilla engine so its files can be replaced (updates only).
    case stoppingEngine
    /// Copying the verified app into Mosaic's private folder.
    case installing
    case finished(version: String)
    case failed(RuntimeInstallFailure)
    case cancelled

    /// True while work is under way.
    public var isActive: Bool {
        switch self {
        case .downloading, .verifying, .stoppingEngine, .installing: true
        default: false
        }
    }

    /// Cancelling is allowed until the app starts being copied into place, so a
    /// cancelled install never leaves a half-copied engine behind.
    public var isCancellable: Bool {
        switch self {
        case .downloading, .verifying: true
        default: false
        }
    }

    /// 0…1 while the download length is known; nil otherwise.
    public var fraction: Double? {
        guard case .downloading(let received, let expected?) = self, expected > 0 else { return nil }
        return min(1, max(0, Double(received) / Double(expected)))
    }

    /// One line for the UI.
    public var summary: String {
        switch self {
        case .idle: ""
        case .downloading(let received, let expected):
            if let expected, expected > 0 {
                "Downloading Firefox ESR… \(Self.megabytes(received)) of \(Self.megabytes(expected))"
            } else {
                "Downloading Firefox ESR… \(Self.megabytes(received))"
            }
        case .verifying: "Checking Mozilla’s signature…"
        case .stoppingEngine: "Closing the Mozilla engine…"
        case .installing: "Installing…"
        case .finished(let version): "Firefox ESR \(version) is installed."
        case .failed(let failure): failure.message
        case .cancelled: "Cancelled. Nothing was changed."
        }
    }

    static func megabytes(_ bytes: Int64) -> String {
        "\(Int((Double(bytes) / 1_000_000).rounded())) MB"
    }
}

public enum RuntimeInstallEvent: Equatable, Sendable {
    case start
    case progress(received: Int64, expected: Int64?)
    case downloaded
    /// Signature accepted. `replacing` is true when a running engine must quit first.
    case verified(engineRunning: Bool)
    case engineStopped
    case installed(version: String)
    case fail(RuntimeInstallFailure)
    case cancel
    case reset
}

/// The installer's state machine. Events that make no sense in the current phase are
/// ignored (the phase is returned unchanged), so late callbacks from a cancelled
/// download cannot revive it.
public enum RuntimeInstallMachine {
    public static func next(_ phase: RuntimeInstallPhase, on event: RuntimeInstallEvent) -> RuntimeInstallPhase {
        switch (phase, event) {
        case (_, .start) where !phase.isActive:
            return .downloading(received: 0, expected: nil)
        case (.downloading, .progress(let received, let expected)):
            return .downloading(received: received, expected: expected)
        case (.downloading, .downloaded):
            return .verifying
        case (.verifying, .verified(let engineRunning)):
            return engineRunning ? .stoppingEngine : .installing
        case (.stoppingEngine, .engineStopped):
            return .installing
        case (.installing, .installed(let version)):
            return .finished(version: version)
        case (_, .fail(let failure)) where phase.isActive:
            return .failed(failure)
        case (_, .cancel) where phase.isCancellable:
            return .cancelled
        case (_, .reset) where !phase.isActive:
            return .idle
        default:
            return phase
        }
    }
}

/// Free space the install needs: the ~140 MB disk image plus the ~500 MB app, with room
/// to spare for the copy that replaces an existing engine.
public enum RuntimeInstallRequirements {
    public static let neededBytes: Int64 = 1_000_000_000

    public static func hasEnoughSpace(available: Int64?) -> Bool {
        guard let available else { return true }  // unknown: let the copy itself fail
        return available >= neededBytes
    }
}
