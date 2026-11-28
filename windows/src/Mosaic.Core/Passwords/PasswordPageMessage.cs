using System.Globalization;
using System.Text.Json;

namespace Mosaic.Core.Passwords;

/// <summary>
/// A message from Mosaic's password page script (<c>Resources/gecko/passwords-page.js</c>), in
/// either engine. Port of <c>Sources/MosaicCore/Passwords/PasswordPageMessage.swift</c>.
/// </summary>
/// <remarks>
/// The page script supplies <see cref="Kind"/> and the page fields. The <em>engine side</em>
/// supplies <see cref="Origin"/>, <see cref="Frame"/>, <see cref="Tab"/> and
/// <see cref="IsPrivate"/> from what it knows about the frame (Gecko: the parent-process actor
/// reads the document principal; Chromium/WebView2: the sender's address that WebView2 reports
/// with the message, and which event delivered it). A page can therefore never make Mosaic file a
/// password under another site's origin. Decoding never fails: unknown or malformed fields are
/// left null.
/// </remarks>
public sealed class PasswordPageMessage
{
    public enum MessageKind
    {
        /// <summary>The document's login state: sent when the page loads and when a login form appears.</summary>
        Page,
        /// <summary>A form with a password was submitted (or a script-driven sign-in finished).</summary>
        Submit,
        /// <summary>A new-password field got focus: offer a generated password.</summary>
        Generate,
        /// <summary>The user pressed the fill shortcut inside the page (Gecko, which has the keyboard).</summary>
        FillShortcut,
        /// <summary>Result of a fill request: <see cref="Count"/> fields filled.</summary>
        Filled,
        /// <summary>A form with only a username was submitted (step one of a two-step sign-in).</summary>
        Identifier,
    }

    public enum FrameKind
    {
        /// <summary>The tab's top-level document.</summary>
        Main,
        /// <summary>A subframe with the same origin as the top-level document.</summary>
        SameOrigin,
        /// <summary>A subframe from another origin. Never filled; never offered for saving.</summary>
        CrossOrigin,
    }

    internal const int MaxUsername = 512;
    internal const int MaxPassword = 1024;

    public MessageKind? Kind { get; set; }
    /// <summary>Random per-document identifier from the page script; tells a new document from the same one.</summary>
    public string? Document { get; set; }
    public bool? HasLogin { get; set; }
    public bool? HasNewPassword { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public SubmissionMode? Mode { get; set; }
    /// <summary>For <c>submit</c>: the page script saw the sign-in finish (form gone, address changed).</summary>
    public bool? Confirmed { get; set; }
    public int? Count { get; set; }

    // Set by the engine side.
    public SiteOrigin? Origin { get; set; }
    public FrameKind? Frame { get; set; }
    /// <summary>Gecko: the agent's tab ID.</summary>
    public int? Tab { get; set; }
    /// <summary>Gecko: the browsing context is private.</summary>
    public bool? IsPrivate { get; set; }

    /// <summary>Decodes a JSON object (wire names as macOS: doc, login, signup, private…). Null if it is not a JSON object.</summary>
    public static PasswordPageMessage? Decode(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return Decode(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static PasswordPageMessage? Decode(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        string? Text(string key) => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        bool? Flag(string key) => root.TryGetProperty(key, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
        int? Number(string key) => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

        var username = Text("username");
        var password = Text("password");
        return new PasswordPageMessage
        {
            Kind = Text("kind") switch
            {
                "page" => MessageKind.Page,
                "submit" => MessageKind.Submit,
                "generate" => MessageKind.Generate,
                "fillShortcut" => MessageKind.FillShortcut,
                "filled" => MessageKind.Filled,
                "identifier" => MessageKind.Identifier,
                _ => null,
            },
            Document = Text("doc"),
            HasLogin = Flag("login"),
            HasNewPassword = Flag("signup"),
            Username = username is null ? null : Prefix(username, MaxUsername),
            // An implausibly long "password" is dropped rather than truncated into a wrong one.
            Password = password is not null && new StringInfo(password).LengthInTextElements <= MaxPassword ? password : null,
            Mode = SubmissionModes.FromId(Text("mode")),
            Confirmed = Flag("confirmed"),
            Count = Number("count"),
            Origin = SiteOrigin.FromUrl(Text("origin")),
            Frame = Text("frame") switch
            {
                "main" => FrameKind.Main,
                "same" => FrameKind.SameOrigin,
                "cross" => FrameKind.CrossOrigin,
                _ => null,
            },
            Tab = Number("tab"),
            IsPrivate = Flag("private"),
        };
    }

    private static string Prefix(string text, int elements)
    {
        var info = new StringInfo(text);
        return info.LengthInTextElements <= elements ? text : info.SubstringByTextElements(0, elements);
    }

    /// <summary>
    /// The submission this message describes, if it is a usable one: a <c>submit</c> with a
    /// password, from the top-level document or a same-origin frame, with a known origin.
    /// </summary>
    public PasswordSubmission? Submission =>
        Kind == MessageKind.Submit && Origin is { } origin && Password is { Length: > 0 } password
        && Frame is FrameKind.Main or FrameKind.SameOrigin
            ? new PasswordSubmission(origin, (Username ?? "").Trim(), password, Mode ?? SubmissionMode.Login)
            : null;
}
