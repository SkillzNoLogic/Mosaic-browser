using System.Text.Encodings.Web;
using System.Text.Json;

namespace Mosaic.Core.Passwords;

/// <summary>
/// The JavaScript Mosaic runs in Chromium (WebView2) pages around the shared page script
/// (<c>Resources/gecko/passwords-page.js</c>). Gecko runs the same page script through its
/// window actor instead (<c>MosaicPasswordsChild.sys.mjs</c>).
/// Counterpart of <c>Sources/MosaicCore/Passwords/PasswordPageScripts.swift</c> and
/// <c>CEFPasswords.mm</c> on macOS.
/// </summary>
/// <remarks>
/// <para>
/// <b>Channel.</b> The document script posts <c>"\u2063mosaic-pw:" + JSON</c> with
/// <c>window.chrome.webview.postMessage</c>. WebView2 tells the host which document sent it
/// (<c>WebMessageReceivedEventArgs.Source</c>) and through which object (the WebView for the
/// top-level document, a <c>CoreWebView2Frame</c> for an iframe), so the origin and frame come
/// from the engine, never from the page; the bridge drops messages from cross-origin frames.
/// A page can post the same marker itself, but only about itself; and the fill shortcut is never
/// taken from a Chromium page (a page could fake it), Mosaic's own keyboard handling does that.
/// </para>
/// <para>
/// <b>Frames.</b> WebView2 runs document scripts in every frame; the script attaches only in the
/// top-level document and in frames with the top-level document's origin (a cross-origin frame
/// cannot read <c>top.location.origin</c>). Fill scripts check <c>location.origin</c> first and
/// do nothing in a document of any other origin.
/// </para>
/// </remarks>
public static class PasswordPageScripts
{
    /// <summary>U+2063 INVISIBLE SEPARATOR, then a name: no page posts this by accident.</summary>
    public const string MessagePrefix = "\u2063mosaic-pw:";

    /// <summary>
    /// For <c>AddScriptToExecuteOnDocumentCreatedAsync</c> (and, guarded against running twice,
    /// for documents that were already open): starts watching forms in this document.
    /// </summary>
    public static string ChromiumDocumentScript(string pageScript) => $$"""
        (function () {
          "use strict";
          if (window.__mosaicPasswordsAttached) return;
          if (location.protocol !== "http:" && location.protocol !== "https:") return;
          try {
            if (window.top !== window && window.top.location.origin !== location.origin) return;
          } catch (e) {
            return;
          }
          var bridge = window.chrome && window.chrome.webview;
          if (!bridge || typeof bridge.postMessage !== "function") return;
          var post = bridge.postMessage.bind(bridge);
          var stringify = JSON.stringify;
          try { Object.defineProperty(window, "__mosaicPasswordsAttached", { value: true }); } catch (e) {}
          function __mosaicSend(message) {
            try { post({{Literal(MessagePrefix)}} + stringify(message)); } catch (e) {}
          }
        {{pageScript}}
          createMosaicPasswordPage(window, __mosaicSend).attach();
        })();
        """;

    /// <summary>Fills a saved credential if the document is on <paramref name="origin"/>; evaluates to the number of fields filled.</summary>
    public static string ChromiumFill(string pageScript, SiteOrigin origin, string username, string password) =>
        Wrap(pageScript, origin, $"createMosaicPasswordPage(window, function () {{}}).fill({Literal(username)}, {Literal(password)})");

    /// <summary>Fills a generated password into the new-password fields if the document is on <paramref name="origin"/>.</summary>
    public static string ChromiumFillGenerated(string pageScript, SiteOrigin origin, string password) =>
        Wrap(pageScript, origin, $"createMosaicPasswordPage(window, function () {{}}).fillGenerated({Literal(password)})");

    private static string Wrap(string pageScript, SiteOrigin origin, string expression) => $$"""
        (function () {
          "use strict";
          if (location.origin !== {{Literal(origin.String)}}) return 0;
        {{pageScript}}
          return {{expression}};
        })();
        """;

    /// <summary>
    /// Reads a message from WebView2's <c>WebMessageAsJson</c>: the page script's JSON object if
    /// the message is a string with <see cref="MessagePrefix"/>, otherwise null (another area's message).
    /// </summary>
    public static PasswordPageMessage? FromWebMessage(string webMessageAsJson)
    {
        if (!webMessageAsJson.StartsWith('"')) return null;
        string? text;
        try
        {
            text = JsonSerializer.Deserialize<string>(webMessageAsJson);
        }
        catch (JsonException)
        {
            return null;
        }
        if (text is null || !text.StartsWith(MessagePrefix, StringComparison.Ordinal)) return null;
        var message = PasswordPageMessage.Decode(text[MessagePrefix.Length..]);
        if (message is null) return null;
        // Whatever the page claims about where it is and what it is, the bridge decides.
        message.Origin = null;
        message.Frame = null;
        message.Tab = null;
        message.IsPrivate = null;
        return message;
    }

    private static readonly JsonSerializerOptions LiteralOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// A JavaScript string literal for any text. JSON strings are valid JavaScript; <c>&lt;</c>,
    /// U+2028 and U+2029 are escaped as well so the literal is safe in any context.
    /// </summary>
    public static string Literal(string text) =>
        JsonSerializer.Serialize(text, LiteralOptions)
            .Replace("<", "\\u003c", StringComparison.Ordinal)
            .Replace("\u2028", "\\u2028", StringComparison.Ordinal)
            .Replace("\u2029", "\\u2029", StringComparison.Ordinal);
}
