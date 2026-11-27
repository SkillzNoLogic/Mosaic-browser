import Foundation

/// How downloads are shared with Free Download Manager.
public enum FDMMode: String, Codable, CaseIterable, Sendable, Identifiable {
    /// Mosaic downloads everything itself.
    case off
    /// Mosaic asks, for each download, whether FDM should take it.
    case ask
    /// Downloads go to FDM (subject to the size threshold).
    case always

    public var id: String { rawValue }

    public var title: String {
        switch self {
        case .off: "Off"
        case .ask: "Ask for each download"
        case .always: "Always use Free Download Manager"
        }
    }
}

/// Decides whether a download goes to Free Download Manager.
public struct FDMPolicy: Hashable, Codable, Sendable {
    public enum Decision: Equatable, Sendable {
        case useEngine
        case askUser
        case sendToFDM
    }

    public var mode: FDMMode
    /// Downloads known to be smaller than this stay in Mosaic; 0 sends every size.
    public var minimumBytes: Int64

    public init(mode: FDMMode = .off, minimumBytes: Int64 = 0) {
        self.mode = mode
        self.minimumBytes = minimumBytes
    }

    /// Schemes FDM can fetch by itself. Anything else (blob:, data:, file:, pages generated
    /// by the browser) exists only inside the engine and must stay there.
    public static let supportedSchemes: Set<String> = ["http", "https", "ftp"]

    /// `totalBytes` is nil (or negative) when the server did not give a size; such
    /// downloads are treated as large enough.
    public func decide(url: String, totalBytes: Int64?, fdmInstalled: Bool) -> Decision {
        guard fdmInstalled, mode != .off,
              let scheme = URL(string: url)?.scheme?.lowercased(), Self.supportedSchemes.contains(scheme)
        else { return .useEngine }
        if minimumBytes > 0, let size = totalBytes, size >= 0, size < minimumBytes { return .useEngine }
        return mode == .ask ? .askUser : .sendToFDM
    }
}

/// One download as Free Download Manager's browser integration describes it.
public struct FDMDownloadRequest: Hashable, Sendable {
    public var url: String
    public var originalURL: String?
    public var referrer: String?
    public var userAgent: String?
    /// "name=value; name2=value2", only when the user allowed passing cookies.
    public var cookies: String?

    public init(url: String, originalURL: String? = nil, referrer: String? = nil, userAgent: String? = nil, cookies: String? = nil) {
        self.url = url
        self.originalURL = originalURL
        self.referrer = referrer
        self.userAgent = userAgent
        self.cookies = cookies
    }
}

/// The messages FDM's browser-integration helper (`wenativehost`, a WebExtension native
/// messaging host) understands. It reads and writes messages framed the native-messaging
/// way: a 32-bit length in native (little-endian) byte order, then that many bytes of JSON.
///
/// Learned from FDM's own browser extension, which talks to the same helper: a
/// `handshake` first, then `create_downloads`. FDM answers `create_downloads` once the
/// user has confirmed or cancelled its "New download" dialog (`result` "0" = cancelled),
/// or at once with `error` set when it cannot fetch the URL.
public enum FDMNativeMessage {
    public static func frame(_ message: [String: Any]) throws -> Data {
        let json = try JSONSerialization.data(withJSONObject: message, options: [.sortedKeys])
        var length = UInt32(json.count).littleEndian
        var data = Data(bytes: &length, count: 4)
        data.append(json)
        return data
    }

    /// Removes and returns every complete message at the start of `buffer`.
    public static func takeMessages(from buffer: inout Data) -> [[String: Any]] {
        var messages: [[String: Any]] = []
        while buffer.count >= 4 {
            let bytes = [UInt8](buffer.prefix(4))
            let length = Int(UInt32(bytes[0]) | UInt32(bytes[1]) << 8 | UInt32(bytes[2]) << 16 | UInt32(bytes[3]) << 24)
            guard buffer.count >= 4 + length else { break }
            let body = buffer.subdata(in: buffer.startIndex + 4 ..< buffer.startIndex + 4 + length)
            buffer.removeSubrange(buffer.startIndex ..< buffer.startIndex + 4 + length)
            if let object = (try? JSONSerialization.jsonObject(with: body)) as? [String: Any] {
                messages.append(object)
            }
        }
        return messages
    }

    public static func handshake(id: String) -> [String: Any] {
        ["id": id, "type": "handshake", "handshake": ["api_version": "2", "browser": "Mosaic"]]
    }

    public static func createDownloads(id: String, _ requests: [FDMDownloadRequest]) -> [String: Any] {
        let downloads: [[String: String]] = requests.map { request in
            var entry: [String: String] = [
                "url": request.url,
                "originalUrl": request.originalURL ?? request.url,
                "httpReferer": request.referrer ?? "",
                "documentUrl": request.referrer ?? "",
                "httpPostData": "",
            ]
            if let agent = request.userAgent, !agent.isEmpty { entry["userAgent"] = agent }
            if let cookies = request.cookies, !cookies.isEmpty { entry["httpCookies"] = cookies }
            return entry
        }
        return [
            "id": id, "type": "create_downloads",
            "create_downloads": ["catchedDownloads": "1", "waitResponse": "1", "downloads": downloads] as [String: Any],
        ]
    }

    /// How FDM answered a `create_downloads` message.
    public enum Outcome: Equatable, Sendable {
        case accepted
        case cancelledByUser
        case failed(String)
    }

    public static func outcome(of response: [String: Any]) -> Outcome {
        if let error = response["error"] as? String, !error.isEmpty { return .failed(error) }
        if (response["result"] as? String) == "0" { return .cancelledByUser }
        return .accepted
    }
}
