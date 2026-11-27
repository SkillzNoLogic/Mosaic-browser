import CryptoKit
import Foundation

/// A Chrome extension package (`.crx`), reduced to the parts Mosaic needs:
/// the ZIP payload and the extension's ID.
///
/// Layouts handled:
///  - CRX3: `Cr24` | version=3 | headerLength | protobuf header | zip
///  - CRX2: `Cr24` | version=2 | keyLength | sigLength | key | signature | zip
///  - a bare ZIP (an unpacked extension that was zipped by hand)
public struct CRXArchive: Sendable {
    public enum Format: Sendable, Equatable { case crx2, crx3, zip }

    public let format: Format
    public let zipData: Data
    /// 32 characters in `a`–`p`, or nil when the package does not carry one (bare ZIP).
    public let extensionID: String?

    public enum ParseError: Error, LocalizedError, Equatable {
        case notAnExtensionPackage
        case unsupportedVersion(UInt32)
        case truncated

        public var errorDescription: String? {
            switch self {
            case .notAnExtensionPackage: "This file is not a Chrome extension package (.crx or .zip)."
            case .unsupportedVersion(let v): "Unsupported CRX version \(v)."
            case .truncated: "The extension package is incomplete or corrupt."
            }
        }
    }

    public static func parse(_ data: Data) throws -> CRXArchive {
        let bytes = [UInt8](data)
        guard bytes.count >= 4 else { throw ParseError.notAnExtensionPackage }

        if bytes.starts(with: [0x50, 0x4B, 0x03, 0x04]) {
            return CRXArchive(format: .zip, zipData: data, extensionID: nil)
        }
        guard bytes.starts(with: Array("Cr24".utf8)) else { throw ParseError.notAnExtensionPackage }
        guard bytes.count >= 12 else { throw ParseError.truncated }

        let version = readUInt32(bytes, at: 4)
        switch version {
        case 3:
            let headerLength = Int(readUInt32(bytes, at: 8))
            let zipStart = 12 + headerLength
            guard zipStart <= bytes.count else { throw ParseError.truncated }
            let header = Array(bytes[12..<zipStart])
            return CRXArchive(
                format: .crx3,
                zipData: Data(bytes[zipStart...]),
                extensionID: crx3ID(fromHeader: header))
        case 2:
            guard bytes.count >= 16 else { throw ParseError.truncated }
            let keyLength = Int(readUInt32(bytes, at: 8))
            let signatureLength = Int(readUInt32(bytes, at: 12))
            let zipStart = 16 + keyLength + signatureLength
            guard zipStart <= bytes.count else { throw ParseError.truncated }
            let key = Data(bytes[16..<(16 + keyLength)])
            return CRXArchive(
                format: .crx2,
                zipData: Data(bytes[zipStart...]),
                extensionID: extensionID(fromPublicKey: key))
        default:
            throw ParseError.unsupportedVersion(version)
        }
    }

    /// Chrome derives an extension's ID from its public key: the first 16 bytes of
    /// SHA-256(key), written in base 16 using the letters a–p instead of 0–f.
    public static func extensionID(fromPublicKey key: Data) -> String {
        encodeID(Array(SHA256.hash(data: key).prefix(16)))
    }

    /// The same derivation from the base64 `key` field of a manifest.
    public static func extensionID(fromManifestKey base64: String) -> String? {
        let cleaned = base64.filter { !$0.isWhitespace }
        guard let key = Data(base64Encoded: cleaned) else { return nil }
        return extensionID(fromPublicKey: key)
    }

    public static func isValidID(_ id: String) -> Bool {
        id.utf8.count == 32 && id.utf8.allSatisfy { $0 >= UInt8(ascii: "a") && $0 <= UInt8(ascii: "p") }
    }

    static func encodeID(_ bytes: [UInt8]) -> String {
        var out = ""
        out.reserveCapacity(bytes.count * 2)
        let base = UInt8(ascii: "a")
        for byte in bytes {
            out.unicodeScalars.append(UnicodeScalar(base + (byte >> 4)))
            out.unicodeScalars.append(UnicodeScalar(base + (byte & 0x0F)))
        }
        return out
    }

    // MARK: - CRX3 header (protobuf)

    /// `CrxFileHeader.signed_header_data` (field 10000) wraps a `SignedData`
    /// message whose field 1 is the 16-byte binary extension ID.
    private static func crx3ID(fromHeader header: [UInt8]) -> String? {
        guard let signedData = protobufBytesField(10000, in: header),
              let id = protobufBytesField(1, in: signedData), id.count == 16
        else { return nil }
        return encodeID(id)
    }

    /// Returns the first length-delimited value of `field` in a protobuf message.
    private static func protobufBytesField(_ field: UInt64, in message: [UInt8]) -> [UInt8]? {
        var offset = 0
        while offset < message.count {
            guard let tag = readVarint(message, &offset) else { return nil }
            let number = tag >> 3
            switch tag & 0x7 {
            case 0:
                guard readVarint(message, &offset) != nil else { return nil }
            case 1:
                offset += 8
            case 2:
                guard let length = readVarint(message, &offset),
                      length <= UInt64(message.count - offset) else { return nil }
                let end = offset + Int(length)
                if number == field { return Array(message[offset..<end]) }
                offset = end
            case 5:
                offset += 4
            default:
                return nil
            }
        }
        return nil
    }

    private static func readVarint(_ bytes: [UInt8], _ offset: inout Int) -> UInt64? {
        var result: UInt64 = 0
        var shift: UInt64 = 0
        while offset < bytes.count, shift < 64 {
            let byte = bytes[offset]
            offset += 1
            result |= UInt64(byte & 0x7F) << shift
            if byte & 0x80 == 0 { return result }
            shift += 7
        }
        return nil
    }

    private static func readUInt32(_ bytes: [UInt8], at offset: Int) -> UInt32 {
        UInt32(bytes[offset]) | UInt32(bytes[offset + 1]) << 8
            | UInt32(bytes[offset + 2]) << 16 | UInt32(bytes[offset + 3]) << 24
    }
}

/// Chrome Web Store addressing.
public enum ChromeWebStore {
    /// Extracts an extension ID from a bare ID or a Web Store listing URL
    /// (`chromewebstore.google.com/detail/<slug>/<id>` and the older
    /// `chrome.google.com/webstore/detail/...` form).
    public static func extensionID(from input: String) -> String? {
        let text = input.trimmingCharacters(in: .whitespacesAndNewlines)
        if CRXArchive.isValidID(text) { return text }
        guard let components = URLComponents(string: text),
              let host = components.host?.lowercased(),
              host == "chromewebstore.google.com" || host == "chrome.google.com"
        else { return nil }
        let parts = components.path.split(separator: "/").map(String.init)
        guard let detail = parts.firstIndex(of: "detail") else { return nil }
        return parts[(detail + 1)...].first(where: CRXArchive.isValidID)
    }

    /// The endpoint Chrome itself uses to download an extension's CRX.
    public static func downloadURL(forID id: String, chromiumVersion: String = "158.0.0.0") -> URL {
        var components = URLComponents(string: "https://clients2.google.com/service/update2/crx")!
        components.queryItems = [
            URLQueryItem(name: "response", value: "redirect"),
            URLQueryItem(name: "os", value: "mac"),
            URLQueryItem(name: "arch", value: "arm64"),
            URLQueryItem(name: "os_arch", value: "arm64"),
            URLQueryItem(name: "prod", value: "chromecrx"),
            URLQueryItem(name: "prodchannel", value: "stable"),
            URLQueryItem(name: "prodversion", value: chromiumVersion),
            URLQueryItem(name: "acceptformat", value: "crx2,crx3"),
            URLQueryItem(name: "x", value: "id=\(id)&installsource=ondemand&uc"),
        ]
        return components.url!
    }

    public static func listingURL(forID id: String) -> URL {
        URL(string: "https://chromewebstore.google.com/detail/\(id)")!
    }
}
