import AppKit
import MosaicCore
import UserNotifications

/// The `com.apple.quarantine` attribute: marks a file as downloaded from the internet, so
/// Gatekeeper checks it (and asks the user) the first time it is opened, as with any browser.
enum Quarantine {
    static let attribute = "com.apple.quarantine"

    static func isQuarantined(_ url: URL) -> Bool {
        getxattr(url.path, attribute, nil, 0, 0, XATTR_NOFOLLOW) > 0
    }

    /// The raw attribute value, for diagnostics ("0083;…;Mosaic;…").
    static func value(_ url: URL) -> String? {
        let size = getxattr(url.path, attribute, nil, 0, 0, XATTR_NOFOLLOW)
        guard size > 0 else { return nil }
        var buffer = [UInt8](repeating: 0, count: size)
        guard getxattr(url.path, attribute, &buffer, size, 0, XATTR_NOFOLLOW) == size else { return nil }
        return String(decoding: buffer, as: UTF8.self)
    }

    /// Adds the attribute unless the engine already did, recording where the file came from.
    @discardableResult
    static func ensure(_ url: URL, source: String, referrer: String?) -> Bool {
        if isQuarantined(url) { return true }
        var properties: [String: Any] = [
            kLSQuarantineAgentNameKey as String: "Mosaic",
            kLSQuarantineTypeKey as String: kLSQuarantineTypeWebDownload as String,
        ]
        if let bundleID = Bundle.main.bundleIdentifier {
            properties[kLSQuarantineAgentBundleIdentifierKey as String] = bundleID
        }
        if let source = URL(string: source), source.scheme == "http" || source.scheme == "https" || source.scheme == "ftp" {
            properties[kLSQuarantineDataURLKey as String] = source
        }
        if let referrer, let page = URL(string: referrer), page.scheme == "http" || page.scheme == "https" {
            properties[kLSQuarantineOriginURLKey as String] = page
        }
        var values = URLResourceValues()
        values.quarantineProperties = properties
        var target = url
        try? target.setResourceValues(values)
        if isQuarantined(url) { return true }
        // Fallback: write the attribute directly (flag 0083 = downloaded, not yet approved).
        let raw = String(format: "0083;%08x;Mosaic;%@", Int(Date().timeIntervalSince1970), UUID().uuidString)
        return raw.withCString { setxattr(url.path, attribute, $0, strlen($0), 0, XATTR_NOFOLLOW) } == 0
    }
}

/// "Download finished" notifications. Permission is asked for at the first download, not
/// at launch.
@MainActor
final class DownloadNotifier: NSObject, UNUserNotificationCenterDelegate {
    /// Scripted runs must not put a permission prompt on the screen of whoever is using the
    /// Mac (the bundle ID is shared with the user's own copy), so they only record what
    /// would have been shown.
    let isScripted: Bool
    private(set) var lastMessage: String?
    private(set) var authorization: String = "not asked"
    private var asked = false
    /// Called when a notification is clicked, with the download's ID.
    var onOpen: ((UUID) -> Void)?

    init(isScripted: Bool) {
        self.isScripted = isScripted
        super.init()
    }

    /// Asks for permission once per launch (macOS remembers the answer; asking again after
    /// a decision shows nothing).
    func requestAuthorizationIfNeeded() {
        guard !asked else { return }
        asked = true
        guard !isScripted, Bundle.main.bundleIdentifier != nil else {
            authorization = "skipped (scripted run)"
            return
        }
        let center = UNUserNotificationCenter.current()
        center.delegate = self
        center.requestAuthorization(options: [.alert, .sound]) { granted, _ in
            Task { @MainActor in self.authorization = granted ? "granted" : "denied" }
        }
    }

    func post(title: String, body: String, id: UUID) {
        lastMessage = "\(title): \(body)"
        guard !isScripted, Bundle.main.bundleIdentifier != nil else { return }
        let content = UNMutableNotificationContent()
        content.title = title
        content.body = body
        content.userInfo = ["download": id.uuidString]
        let request = UNNotificationRequest(identifier: "download-\(id.uuidString)", content: content, trigger: nil)
        UNUserNotificationCenter.current().add(request)
    }

    nonisolated func userNotificationCenter(
        _ center: UNUserNotificationCenter, didReceive response: UNNotificationResponse,
        withCompletionHandler completionHandler: @escaping () -> Void
    ) {
        let id = (response.notification.request.content.userInfo["download"] as? String).flatMap(UUID.init(uuidString:))
        Task { @MainActor in
            if let id { self.onOpen?(id) }
            completionHandler()
        }
    }

    nonisolated func userNotificationCenter(
        _ center: UNUserNotificationCenter, willPresent notification: UNNotification,
        withCompletionHandler completionHandler: @escaping (UNNotificationPresentationOptions) -> Void
    ) {
        completionHandler([.banner, .list])
    }
}

/// Text for sizes, speeds and times.
enum DownloadFormat {
    static func bytes(_ count: Int64) -> String {
        ByteCountFormatter.string(fromByteCount: max(0, count), countStyle: .file)
    }

    static func speed(_ bytesPerSecond: Double) -> String {
        "\(bytes(Int64(bytesPerSecond)))/s"
    }

    static func remaining(_ seconds: Double) -> String {
        let formatter = DateComponentsFormatter()
        formatter.unitsStyle = .abbreviated
        formatter.maximumUnitCount = 2
        formatter.allowedUnits = seconds >= 3600 ? [.hour, .minute] : seconds >= 60 ? [.minute, .second] : [.second]
        return (formatter.string(from: max(1, seconds)) ?? "") + " left"
    }

    /// One status line for a download: "12 MB of 40 MB · 1.5 MB/s · 20s left", "Completed · 40 MB"…
    static func status(_ record: DownloadRecord) -> String {
        switch record.state {
        case .inProgress:
            var parts: [String] = []
            if record.totalBytes > 0 {
                parts.append("\(bytes(record.receivedBytes)) of \(bytes(record.totalBytes))")
            } else {
                parts.append(bytes(record.receivedBytes))
            }
            if record.speed > 0 { parts.append(speed(record.speed)) }
            if let left = record.secondsRemaining { parts.append(remaining(left)) }
            return parts.joined(separator: " · ")
        case .paused:
            let size = record.totalBytes > 0 ? "\(bytes(record.receivedBytes)) of \(bytes(record.totalBytes))" : bytes(record.receivedBytes)
            return "Paused · \(size)"
        case .completed:
            let size = record.totalBytes > 0 ? record.totalBytes : record.receivedBytes
            return size > 0 ? bytes(size) : "Completed"
        case .failed:
            return "Failed" + (record.errorMessage.map { " · \($0)" } ?? "")
        case .cancelled:
            return record.errorMessage ?? "Cancelled"
        case .handedOff:
            return record.errorMessage ?? "Sent to Free Download Manager"
        }
    }
}
