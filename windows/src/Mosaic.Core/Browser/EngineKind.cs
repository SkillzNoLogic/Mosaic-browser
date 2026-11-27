namespace Mosaic.Core.Browser;

/// <summary>The rendering engines a tab can run on.</summary>
public enum EngineKind
{
    Gecko,
    Chromium,
}

public static class EngineKindExtensions
{
    /// <summary>Stable identifier used in the settings file and the automation protocol.</summary>
    public static string Id(this EngineKind kind) => kind == EngineKind.Gecko ? "gecko" : "chromium";

    /// <summary>What the user sees: the engines are presented by vendor, not by engine name.</summary>
    public static string DisplayName(this EngineKind kind) => kind == EngineKind.Gecko ? "Mozilla" : "Chromium";

    public static string EngineName(this EngineKind kind) => kind == EngineKind.Gecko ? "Gecko" : "Blink";

    public static EngineKind Other(this EngineKind kind) =>
        kind == EngineKind.Gecko ? EngineKind.Chromium : EngineKind.Gecko;

    /// <summary>Inverse of <see cref="Id"/>; null for anything else.</summary>
    public static EngineKind? FromId(string? id) => id switch
    {
        "gecko" => EngineKind.Gecko,
        "chromium" => EngineKind.Chromium,
        _ => null,
    };
}
