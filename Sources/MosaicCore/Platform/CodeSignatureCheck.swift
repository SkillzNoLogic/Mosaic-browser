import Foundation

/// What `codesign --display --verbose=2` says about a signed bundle.
///
///     Executable=/Volumes/Firefox/Firefox.app/Contents/MacOS/firefox
///     Identifier=org.mozilla.firefox
///     Format=app bundle with Mach-O universal (x86_64 arm64)
///     Authority=Developer ID Application: Mozilla Corporation (43AQ936H96)
///     Authority=Developer ID Certification Authority
///     Authority=Apple Root CA
///     Notarization Ticket=stapled
///     TeamIdentifier=43AQ936H96
public struct CodeSignatureInfo: Equatable, Sendable {
    public var identifier: String?
    public var teamIdentifier: String?
    /// Certificate chain, leaf first.
    public var authorities: [String] = []
    public var format: String?
    public var isNotarized = false

    public init(identifier: String? = nil, teamIdentifier: String? = nil, authorities: [String] = [],
                format: String? = nil, isNotarized: Bool = false) {
        self.identifier = identifier
        self.teamIdentifier = teamIdentifier
        self.authorities = authorities
        self.format = format
        self.isNotarized = isNotarized
    }

    /// Parses codesign's display output (it writes to standard error).
    public static func parse(codesignDisplay output: String) -> CodeSignatureInfo {
        var info = CodeSignatureInfo()
        for line in output.split(whereSeparator: \.isNewline) {
            guard let equals = line.firstIndex(of: "=") else { continue }
            let key = line[..<equals].trimmingCharacters(in: .whitespaces)
            let value = line[line.index(after: equals)...].trimmingCharacters(in: .whitespaces)
            switch key {
            case "Identifier": info.identifier = value
            case "TeamIdentifier": info.teamIdentifier = (value == "not set" || value.isEmpty) ? nil : value
            case "Authority": info.authorities.append(value)
            case "Format": info.format = value
            case "Notarization Ticket": info.isNotarized = value == "stapled"
            default: break
            }
        }
        return info
    }

    /// True if the signed code includes an Apple Silicon slice.
    public var includesARM64: Bool { format?.contains("arm64") ?? false }
}

/// Who must have signed the Gecko runtime Mosaic installs, and the checks it runs.
public struct SignerExpectation: Equatable, Sendable {
    /// Apple Developer Team ID.
    public let teamIdentifier: String
    /// Bundle identifier of the main executable's signature.
    public let identifier: String
    /// Organisation in the leaf certificate's name, for the human-readable message.
    public let organisation: String

    public init(teamIdentifier: String, identifier: String, organisation: String) {
        self.teamIdentifier = teamIdentifier
        self.identifier = identifier
        self.organisation = organisation
    }

    /// Mozilla's Developer ID, as found on Firefox releases downloaded from mozilla.org
    /// ("Developer ID Application: Mozilla Corporation (43AQ936H96)").
    public static let mozillaFirefox = SignerExpectation(
        teamIdentifier: "43AQ936H96", identifier: "org.mozilla.firefox", organisation: "Mozilla Corporation")

    /// A code requirement for `codesign --verify -R`: signed with a Developer ID
    /// certificate issued by Apple to this team, for this identifier. This is Apple's
    /// standard designated requirement for Developer ID software, pinned to the team.
    public var requirement: String {
        "identifier \"\(identifier)\" and anchor apple generic"
            + " and certificate 1[field.1.2.840.113635.100.6.2.6] exists"
            + " and certificate leaf[field.1.2.840.113635.100.6.1.13] exists"
            + " and certificate leaf[subject.OU] = \"\(teamIdentifier)\""
    }

    /// Arguments for the three codesign runs, in order: integrity of every nested
    /// component, the requirement above, and the details used in messages.
    public func codesignArguments(appPath: String) -> [[String]] {
        [
            ["--verify", "--deep", "--strict", appPath],
            ["--verify", "--deep", "--strict", "-R=\(requirement)", appPath],
            ["--display", "--verbose=2", appPath],
        ]
    }

    /// Decides from the three runs' exit statuses and the display output.
    public func evaluate(
        integrityStatus: Int32, integrityOutput: String,
        requirementStatus: Int32, display: CodeSignatureInfo
    ) -> SignatureVerdict {
        guard integrityStatus == 0 else {
            var detail = integrityOutput.split(whereSeparator: \.isNewline).last.map(String.init) ?? ""
            // "/path/to/Firefox.app: a sealed resource is missing or invalid" → the reason only.
            if detail.hasPrefix("/"), let separator = detail.range(of: ": ") {
                detail = String(detail[separator.upperBound...])
            }
            return .invalid(detail)
        }
        guard requirementStatus == 0, display.teamIdentifier == teamIdentifier,
              display.identifier == identifier
        else {
            return .wrongSigner(found: display.authorities.first ?? display.teamIdentifier)
        }
        return .trusted(display)
    }
}

public enum SignatureVerdict: Equatable, Sendable {
    /// Intact and signed by the expected developer.
    case trusted(CodeSignatureInfo)
    /// Unsigned, modified after signing, or damaged. Carries codesign's last line.
    case invalid(String)
    /// Intact, but signed by somebody else (or not with a Developer ID).
    case wrongSigner(found: String?)
}
