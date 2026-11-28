namespace Mosaic.Core.Permissions;

/// <summary>What a page can ask for, in engine-neutral terms. JSON names are the lower-case member names.</summary>
public enum PermissionKind
{
    Camera,
    Microphone,
    Location,
    Notifications,
    Clipboard,
    /// <summary>Screen or tab sharing.</summary>
    Screen,
    /// <summary>Something else, named in <see cref="PermissionRequestInfo.OtherNames"/>.</summary>
    Other,
}

public static class PermissionKinds
{
    public static string Name(this PermissionKind kind) => kind.ToString().ToLowerInvariant();

    public static PermissionKind? Parse(string? name) =>
        Enum.TryParse<PermissionKind>(name, ignoreCase: true, out var kind) && kind.Name() == name ? kind : null;

    /// <summary>The remembered permission this kind is stored as; null for kinds that are asked every time.</summary>
    public static SitePermission? StoredAs(this PermissionKind kind) => kind switch
    {
        PermissionKind.Camera => SitePermission.Camera,
        PermissionKind.Microphone => SitePermission.Microphone,
        PermissionKind.Location => SitePermission.Location,
        PermissionKind.Notifications => SitePermission.Notifications,
        PermissionKind.Clipboard => SitePermission.Clipboard,
        _ => null,
    };
}

/// <summary>
/// What a page asked for, and how Mosaic words and decides it. Port of
/// <c>Sources/MosaicCore/Permissions/PermissionPrompting.swift</c>.
/// </summary>
/// <param name="Origin">The requesting origin ("https://meet.example"); may be empty or "null" for
/// pages without one (data: URLs).</param>
/// <param name="Kinds">What is asked for, in the order the engine reported it.</param>
/// <param name="OtherNames">Engine names of <see cref="PermissionKind.Other"/> permissions
/// ("midi-sysex", "persistent-storage", …).</param>
public sealed record PermissionRequestInfo(string Origin, IReadOnlyList<PermissionKind> Kinds, IReadOnlyList<string> OtherNames)
{
    public PermissionRequestInfo(string origin, params PermissionKind[] kinds) : this(origin, kinds, []) { }

    /// <summary>
    /// The site decisions are remembered for, or null if the page has none. Engines always give an
    /// origin with a scheme; anything else ("null" for opaque origins) has no site.
    /// </summary>
    public string? Site => Origin.Contains("://", StringComparison.Ordinal) ? SitePermissionStore.Site(Origin) : null;

    /// <summary>True when every requested kind can be remembered for the site.</summary>
    public bool IsRememberable => Site is not null && Kinds.Count > 0 && Kinds.All(kind => kind.StoredAs() is not null);

    /// <summary>What to do without asking: allow if every kind is allowed for the site, block if any is blocked, null to ask.</summary>
    public SitePermissionDecision? AutomaticDecision(SitePermissionStore store)
    {
        if (!IsRememberable || Site is not { } site) return null;
        var decisions = Kinds.Select(kind => store.Decision(kind.StoredAs()!.Value, site)).ToList();
        if (decisions.Contains(SitePermissionDecision.Block)) return SitePermissionDecision.Block;
        return decisions.All(decision => decision == SitePermissionDecision.Allow) ? SitePermissionDecision.Allow : null;
    }

    /// <summary>This request and <paramref name="other"/> as one ("camera" + "microphone" → both).</summary>
    public PermissionRequestInfo Merging(PermissionRequestInfo other) => this with
    {
        Kinds = [.. Kinds.Concat(other.Kinds).Distinct()],
        OtherNames = [.. OtherNames.Concat(other.OtherNames).Distinct()],
    };

    /// <summary>"example.com wants to use your camera and microphone".</summary>
    public string Sentence
    {
        get
        {
            var who = Site is { } site ? SitePermissionStore.DisplayName(site) : "This page";
            return $"{who} wants to {Phrase(Kinds, OtherNames)}";
        }
    }

    internal static string Phrase(IReadOnlyList<PermissionKind> kinds, IReadOnlyList<string> otherNames)
    {
        var parts = new List<string>();
        var set = kinds.ToHashSet();
        if (set.Contains(PermissionKind.Camera) && set.Contains(PermissionKind.Microphone)) parts.Add("use your camera and microphone");
        else if (set.Contains(PermissionKind.Camera)) parts.Add("use your camera");
        else if (set.Contains(PermissionKind.Microphone)) parts.Add("use your microphone");
        if (set.Contains(PermissionKind.Screen)) parts.Add("share your screen");
        if (set.Contains(PermissionKind.Location)) parts.Add("know your location");
        if (set.Contains(PermissionKind.Notifications)) parts.Add("show notifications");
        if (set.Contains(PermissionKind.Clipboard)) parts.Add("see text and images you copy");
        if (set.Contains(PermissionKind.Other))
        {
            parts.AddRange(otherNames.Count == 0 ? [OtherPhrase("")] : otherNames.Select(OtherPhrase));
        }
        var unique = parts.Distinct().ToList();
        return unique.Count switch
        {
            0 => OtherPhrase(""),
            1 => unique[0],
            _ => string.Join(", ", unique.Take(unique.Count - 1)) + " and " + unique[^1],
        };
    }

    internal static string OtherPhrase(string name) => name switch
    {
        "midi" or "midi-sysex" => "control your MIDI devices",
        "storage-access" => "use its cookies while embedded in other sites",
        "persistent-storage" => "keep its data on this PC permanently",
        "multiple-downloads" => "download multiple files",
        "xr" => "use virtual-reality devices",
        "local-network" or "loopback-network" => "connect to devices on your local network",
        "window-management" => "place windows on all your displays",
        "local-fonts" => "use the fonts installed on this PC",
        "idle-detection" => "know when you are using this PC",
        "keyboard-lock" => "take over keyboard shortcuts",
        "pointer-lock" => "hide and lock your pointer",
        "file-system" => "edit files on this PC",
        "sensors" => "use motion sensors",
        "serial" => "connect to serial devices",
        "autoplay" => "play media with sound automatically",
        _ => "use a feature that needs your permission",
    };
}
