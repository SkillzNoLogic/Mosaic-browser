import Foundation

/// JavaScript snippets run with the page script (`passwords-page.js`) in scope, in Chromium
/// frames (see `MosaicCEF.passwordLoadScript` and `runPasswordScript`). `__mosaicSend` is
/// supplied by the bridge. Gecko does the same through its window actor instead.
public enum PasswordPageScripts {
    /// Run once per document after it loads: starts watching forms.
    public static let load = """
        if (!window.__mosaicPasswordsAttached) {
          try { Object.defineProperty(window, "__mosaicPasswordsAttached", { value: true }); } catch (e) {}
          createMosaicPasswordPage(window, __mosaicSend).attach();
        }
        """

    /// Fills a saved credential and reports how many fields it filled.
    public static func fill(username: String, password: String) -> String {
        "__mosaicSend({ kind: \"filled\", count: createMosaicPasswordPage(window, __mosaicSend).fill(\(literal(username)), \(literal(password))) });"
    }

    /// Fills a generated password into the new-password fields.
    public static func fillGenerated(password: String) -> String {
        "__mosaicSend({ kind: \"filled\", count: createMosaicPasswordPage(window, __mosaicSend).fillGenerated(\(literal(password))) });"
    }

    /// A JavaScript string literal for any text. JSON strings are valid JavaScript; `<`, U+2028
    /// and U+2029 are escaped as well so the literal is safe in any context.
    public static func literal(_ text: String) -> String {
        guard let data = try? JSONSerialization.data(withJSONObject: [text]),
              let json = String(data: data, encoding: .utf8)
        else { return "\"\"" }
        return String(json.dropFirst().dropLast())
            .replacingOccurrences(of: "<", with: "\\u003c")
            .replacingOccurrences(of: "\u{2028}", with: "\\u2028")
            .replacingOccurrences(of: "\u{2029}", with: "\\u2029")
    }
}
