using System.Text.Json;
using System.Text.Json.Nodes;
using Mosaic.Core.Browser;

namespace Mosaic.Core.WindowsIntegration;

/// <summary>
/// What a launch of <c>Mosaic.exe</c> asks for: pages to open (from the default-browser
/// registration, a file association, "Open with", a shortcut or a terminal), jump-list tasks
/// (new tab, new private tab) and toast activations.
/// </summary>
/// <remarks>
/// <para><b>For the winui area (single instance).</b> In <c>Main</c>, before anything else:</para>
/// <code>
/// var request = ActivationRequest.Parse(args, Environment.CommandLine, Environment.CurrentDirectory, File.Exists);
/// // second instance:  send request.ToPayload() over the single-instance channel, then exit.
/// // first instance:   after the window and model exist (and the session is restored),
/// //                   request.ApplyTo(model);
/// // on a forwarded payload: ActivationRequest.FromPayload(text)?.ApplyTo(model) on the UI thread,
/// //                   then bring the window to the front.
/// </code>
/// <para>File paths are resolved against the launching process's working directory before
/// forwarding, so the running instance never sees relative paths. The registration's open
/// command is <c>"Mosaic.exe" --single-argument %1</c> (as Chrome and Edge register): everything
/// after <see cref="SingleArgumentSwitch"/> on the raw command line is one URL or path, so a
/// crafted URL cannot smuggle in switches such as <c>--mosaic-automation</c>.</para>
/// </remarks>
public sealed record ActivationRequest
{
    /// <summary>Precedes the one URL/path the shell passes (see the registration's open command).</summary>
    public const string SingleArgumentSwitch = "--single-argument";
    public const string NewTabSwitch = "--new-tab";
    public const string NewPrivateTabSwitch = "--new-private-tab";
    public const string NewWindowSwitch = "--new-window";

    /// <summary>The argument Windows App SDK passes when a toast is clicked in an unpackaged app.</summary>
    public const string NotificationActivationPrefix = "----AppNotificationActivated:";

    /// <summary>Schemes a launch may open. Anything else (javascript:, data:, vbscript:, …) is dropped.</summary>
    public static readonly IReadOnlySet<string> AllowedSchemes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "http", "https", "ftp", "file", "mosaic", "about" };

    /// <summary>
    /// What to open, in order: absolute URLs (<c>https://…</c>, <c>file:///…</c>, <c>mosaic://…</c>)
    /// or address text to resolve like the address bar (<c>example.com</c>, search words).
    /// </summary>
    public IReadOnlyList<string> Targets { get; init; } = [];

    /// <summary>Open an empty new tab (jump list "New tab", or <c>--new-tab</c> without targets).</summary>
    public bool NewTab { get; init; }

    /// <summary>Open the targets (or an empty tab) as private tabs (jump list "New private tab").</summary>
    public bool Private { get; init; }

    /// <summary>The user asked for a new window (<c>--new-window</c>); tabs open in the window until Mosaic has several.</summary>
    public bool NewWindow { get; init; }

    /// <summary>Started by clicking a notification (Windows App SDK handles the arguments).</summary>
    public bool FromNotification { get; init; }

    /// <summary>Arguments that were ignored because they cannot be opened safely (for logs and tests).</summary>
    public IReadOnlyList<string> Rejected { get; init; } = [];

    /// <summary>Nothing to do beyond showing the window.</summary>
    public bool IsPlainLaunch => Targets.Count == 0 && !NewTab && !Private;

    /// <summary>Parses a command line.</summary>
    /// <param name="args">The process's arguments (<c>Main(string[] args)</c>).</param>
    /// <param name="rawCommandLine"><c>Environment.CommandLine</c>, used after <see cref="SingleArgumentSwitch"/>; null to use <paramref name="args"/>.</param>
    /// <param name="workingDirectory">Where relative paths are resolved.</param>
    /// <param name="fileExists">File check (<c>File.Exists</c>), injectable for tests.</param>
    public static ActivationRequest Parse(
        IReadOnlyList<string> args, string? rawCommandLine, string workingDirectory, Func<string, bool> fileExists)
    {
        var targets = new List<string>();
        var rejected = new List<string>();
        bool newTab = false, isPrivate = false, newWindow = false, notification = false;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg.Equals(SingleArgumentSwitch, StringComparison.OrdinalIgnoreCase))
            {
                var rest = SingleArgument(rawCommandLine) ?? string.Join(' ', args.Skip(i + 1));
                Add(rest);
                break;
            }
            // The rest belongs to Windows App SDK, which reads it through AppInstance.
            if (arg.StartsWith(NotificationActivationPrefix, StringComparison.Ordinal)) { notification = true; break; }
            if (arg.Equals(NewTabSwitch, StringComparison.OrdinalIgnoreCase)) { newTab = true; continue; }
            if (arg.Equals(NewPrivateTabSwitch, StringComparison.OrdinalIgnoreCase)) { isPrivate = true; continue; }
            if (arg.Equals(NewWindowSwitch, StringComparison.OrdinalIgnoreCase)) { newWindow = true; continue; }
            if (arg.StartsWith('-') && !LooksLikePath(arg)) continue;  // app switches (--mosaic-automation, …)
            Add(arg);
        }

        return new ActivationRequest
        {
            Targets = targets,
            NewTab = newTab,
            Private = isPrivate,
            NewWindow = newWindow,
            FromNotification = notification,
            Rejected = rejected,
        };

        void Add(string candidate)
        {
            switch (Classify(candidate, workingDirectory, fileExists))
            {
                case (true, { } target): targets.Add(target); break;
                case (false, _) when candidate.Trim().Length > 0: rejected.Add(candidate); break;
            }
        }
    }

    /// <summary>
    /// Turns one argument into something to open: a URL with an allowed scheme, a file URI for
    /// an existing file, or address text. Returns (false, null) for what must not be opened.
    /// </summary>
    public static (bool Accepted, string? Target) Classify(string candidate, string workingDirectory, Func<string, bool> fileExists)
    {
        var text = candidate.Trim();
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"') text = text[1..^1].Trim();
        if (text.Length == 0) return (false, null);

        if (LooksLikePath(text) || !HasScheme(text))
        {
            var full = WindowsPath.Combine(workingDirectory, text);
            if (full is not null && fileExists(full)) return (true, WindowsPath.ToFileUri(full));
            if (LooksLikePath(text)) return (false, null);  // a path to nothing: do not search for it
            return (true, text);                            // address text, resolved like the address bar
        }

        var scheme = text[..text.IndexOf(':')];
        if (!AllowedSchemes.Contains(scheme)) return (false, null);
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) ? (true, uri.AbsoluteUri) : (false, null);
    }

    private static bool HasScheme(string text)
    {
        var colon = text.IndexOf(':');
        if (colon < 2) return false;  // "C:" is a drive, not a scheme
        for (var i = 0; i < colon; i++)
        {
            var c = text[i];
            var ok = char.IsAsciiLetter(c) || (i > 0 && (char.IsAsciiDigit(c) || c is '+' or '-' or '.'));
            if (!ok) return false;
        }
        // "example.com:8080" and "localhost:3000" are addresses with ports, not schemes.
        var after = text[(colon + 1)..];
        return !(after.Length > 0 && char.IsAsciiDigit(after[0]) && text[..colon].Contains('.'))
            && !(text[..colon].Equals("localhost", StringComparison.OrdinalIgnoreCase));
    }

    private static bool LooksLikePath(string text) =>
        WindowsPath.IsRooted(text) || text.StartsWith(@".\", StringComparison.Ordinal) || text.StartsWith(@"..\", StringComparison.Ordinal);

    /// <summary>The raw text after <see cref="SingleArgumentSwitch"/>, or null when it is not there.</summary>
    internal static string? SingleArgument(string? rawCommandLine)
    {
        if (rawCommandLine is null) return null;
        var index = rawCommandLine.IndexOf(" " + SingleArgumentSwitch, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return null;
        var rest = rawCommandLine[(index + SingleArgumentSwitch.Length + 1)..];
        if (rest.Length > 0 && rest[0] != ' ') return null;  // e.g. "--single-argumentX"
        return rest.Trim();
    }

    // ---- Forwarding to the running instance ----

    /// <summary>One line of JSON for the single-instance channel.</summary>
    public string ToPayload() => new JsonObject
    {
        ["v"] = 1,
        ["targets"] = new JsonArray([.. Targets.Select(t => (JsonNode)JsonValue.Create(t))]),
        ["newTab"] = NewTab,
        ["private"] = Private,
        ["newWindow"] = NewWindow,
        ["notification"] = FromNotification,
    }.ToJsonString();

    /// <summary>Reads <see cref="ToPayload"/>'s output; null when it is not a payload. Targets are re-checked.</summary>
    public static ActivationRequest? FromPayload(string payload)
    {
        try
        {
            if (JsonNode.Parse(payload) is not JsonObject json || json["v"]?.GetValue<int>() != 1) return null;
            var targets = new List<string>();
            if (json["targets"] is JsonArray array)
            {
                foreach (var item in array)
                {
                    // The sender already resolved files; a payload must not reintroduce unsafe schemes.
                    if (item?.GetValue<string>() is { } t && (!HasScheme(t) || AllowedSchemes.Contains(t[..t.IndexOf(':')])))
                        targets.Add(t);
                }
            }
            return new ActivationRequest
            {
                Targets = targets,
                NewTab = json["newTab"]?.GetValue<bool>() ?? false,
                Private = json["private"]?.GetValue<bool>() ?? false,
                NewWindow = json["newWindow"]?.GetValue<bool>() ?? false,
                FromNotification = json["notification"]?.GetValue<bool>() ?? false,
            };
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    // ---- Applying ----

    /// <summary>
    /// Opens a private tab with the given target ("" for an empty one). Set by the privacy area;
    /// while it is null a private request shows a notice instead of opening a normal tab.
    /// </summary>
    public static Func<BrowserModel, string, bool>? PrivateTabOpener { get; set; }

    /// <summary>The most recent request applied (for <c>dump</c>).</summary>
    public static ActivationRequest? LastApplied { get; private set; }

    /// <summary>
    /// Opens what the request asks for in <paramref name="model"/> (UI thread). The first target
    /// reuses the selected tab when it is an untouched empty tab (a fresh launch).
    /// </summary>
    public void ApplyTo(BrowserModel model)
    {
        LastApplied = this;
        if (Private)
        {
            if (PrivateTabOpener is not { } open)
            {
                model.Notice = "Private tabs are not available yet.";
                return;
            }
            if (Targets.Count == 0) open(model, "");
            foreach (var target in Targets) open(model, target);
            return;
        }

        if (Targets.Count == 0)
        {
            if (NewTab) model.NewTab();
            return;
        }
        foreach (var target in Targets)
        {
            var reuse = model.SelectedTab is { Url.Length: 0, IsLoading: false, GeckoId: null } && model.Tabs.Count == 1;
            if (!reuse) model.NewTab(select: true);
            if (HasScheme(target)) model.Load(target, model.SelectedTab!);
            else model.Navigate(target);
        }
    }
}

/// <summary>Windows path rules, implemented here so they behave the same when tests run on macOS/Linux.</summary>
internal static class WindowsPath
{
    public static bool IsRooted(string path) =>
        (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/')
        || path.StartsWith(@"\\", StringComparison.Ordinal);

    /// <summary>The full path of <paramref name="path"/> relative to <paramref name="directory"/>, normalised; null if it escapes the root.</summary>
    public static string? Combine(string directory, string path)
    {
        string root, rest;
        var full = IsRooted(path) ? path : directory.TrimEnd('\\', '/') + "\\" + path;
        if (full.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var parts = full[2..].Split('\\', '/');
            if (parts.Length < 2) return null;
            root = $@"\\{parts[0]}\{parts[1]}";
            rest = string.Join('\\', parts.Skip(2));
        }
        else if (IsRooted(full))
        {
            root = full[..2].ToUpperInvariant();
            rest = full[3..];
        }
        else
        {
            return null;  // the working directory itself was not a Windows path
        }

        var stack = new List<string>();
        foreach (var part in rest.Split('\\', '/'))
        {
            if (part is "" or ".") continue;
            if (part == "..")
            {
                if (stack.Count == 0) return null;
                stack.RemoveAt(stack.Count - 1);
                continue;
            }
            stack.Add(part);
        }
        return root + "\\" + string.Join('\\', stack);
    }

    /// <summary><c>C:\a b\x.html</c> → <c>file:///C:/a%20b/x.html</c>; UNC → <c>file://server/share/…</c>.</summary>
    public static string ToFileUri(string fullPath)
    {
        var unc = fullPath.StartsWith(@"\\", StringComparison.Ordinal);
        var segments = (unc ? fullPath[2..] : fullPath).Split('\\');
        var encoded = string.Join('/', segments.Select((s, i) => (unc && i == 0) || (!unc && i == 0) ? s : Uri.EscapeDataString(s)));
        return unc ? "file://" + encoded : "file:///" + encoded;
    }
}
