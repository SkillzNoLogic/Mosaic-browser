// Signs Mosaic release files with Mosaic's Ed25519 release key, so the in-app updater can
// tell a genuine release from anything else (see UpdatePackage.swift).
//
//   swift Scripts/sign-update.swift <file>...      writes <file>.sig (base64 signature)
//   swift Scripts/sign-update.swift --generate     creates the key pair once, stores the
//                                                  private key in the login Keychain and
//                                                  prints the public key for UpdatePackage
//   swift Scripts/sign-update.swift --public-key   prints the public key of the stored key
//   swift Scripts/sign-update.swift --export       prints the private key (base64), to move
//                                                  it to another release machine; keep it secret
//
// The private key is read from the login Keychain (service "dev.mosaic.release-signing",
// account "ed25519"), or from the file named by MOSAIC_UPDATE_KEY_FILE (base64 raw key),
// for a machine that does not hold it in its Keychain.
import CryptoKit
import Foundation
import Security

let service = "dev.mosaic.release-signing"
let account = "ed25519"

func fail(_ message: String) -> Never {
    FileHandle.standardError.write(Data("sign-update: \(message)\n".utf8))
    exit(1)
}

func storedKey() -> Curve25519.Signing.PrivateKey? {
    var raw: Data?
    if let path = ProcessInfo.processInfo.environment["MOSAIC_UPDATE_KEY_FILE"] {
        guard let text = try? String(contentsOfFile: path, encoding: .utf8) else { fail("cannot read \(path)") }
        raw = Data(base64Encoded: text.trimmingCharacters(in: .whitespacesAndNewlines))
    } else {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service,
            kSecAttrAccount as String: account, kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne,
        ]
        var result: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        if status == errSecItemNotFound { return nil }
        guard status == errSecSuccess, let data = result as? Data else { fail("Keychain error \(status)") }
        raw = Data(base64Encoded: String(decoding: data, as: UTF8.self))
    }
    guard let raw, let key = try? Curve25519.Signing.PrivateKey(rawRepresentation: raw) else {
        fail("the stored release key is not a valid Ed25519 key")
    }
    return key
}

let arguments = Array(CommandLine.arguments.dropFirst())
guard let first = arguments.first else {
    fail("usage: sign-update.swift <file>... | --generate | --public-key | --export")
}

switch first {
case "--generate":
    if storedKey() != nil { fail("a release key already exists; refusing to replace it") }
    let key = Curve25519.Signing.PrivateKey()
    let attributes: [String: Any] = [
        kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service,
        kSecAttrAccount as String: account, kSecAttrLabel as String: "Mosaic release signing key",
        kSecAttrComment as String: "Ed25519 key that signs Mosaic update downloads. Losing it means existing installs cannot verify new releases.",
        kSecValueData as String: Data(key.rawRepresentation.base64EncodedString().utf8),
    ]
    let status = SecItemAdd(attributes as CFDictionary, nil)
    guard status == errSecSuccess else { fail("could not store the key in the Keychain (\(status))") }
    print(key.publicKey.rawRepresentation.base64EncodedString())
case "--public-key":
    guard let key = storedKey() else { fail("no release key; run with --generate first") }
    print(key.publicKey.rawRepresentation.base64EncodedString())
case "--export":
    guard let key = storedKey() else { fail("no release key") }
    print(key.rawRepresentation.base64EncodedString())
default:
    guard let key = storedKey() else { fail("no release key in the Keychain; run with --generate, or set MOSAIC_UPDATE_KEY_FILE") }
    for path in arguments {
        guard let data = try? Data(contentsOf: URL(fileURLWithPath: path), options: .mappedIfSafe) else { fail("cannot read \(path)") }
        let signature = try key.signature(for: data)
        guard key.publicKey.isValidSignature(signature, for: data) else { fail("signature check failed for \(path)") }
        try Data((signature.base64EncodedString() + "\n").utf8).write(to: URL(fileURLWithPath: path + ".sig"))
        print("signed \((path as NSString).lastPathComponent)")
    }
}
