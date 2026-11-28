namespace Mosaic.Core.Tabs;

/// <summary>How hard memory is to come by (on Windows: see <see cref="MemoryPressureMonitor"/>).</summary>
public enum MemoryPressure { Normal, Warning, Critical }

public static class MemoryPressureNames
{
    /// <summary>The automation and saved name: "normal", "warning", "critical" (as on macOS).</summary>
    public static string Id(this MemoryPressure pressure) => pressure.ToString().ToLowerInvariant();

    public static MemoryPressure? FromId(string? id) => id switch
    {
        "normal" => MemoryPressure.Normal,
        "warning" => MemoryPressure.Warning,
        "critical" => MemoryPressure.Critical,
        _ => null,
    };

    /// <summary>What the Performance page shows.</summary>
    public static string DisplayName(this MemoryPressure pressure) => pressure switch
    {
        MemoryPressure.Warning => "High",
        MemoryPressure.Critical => "Critical",
        _ => "Normal",
    };
}

/// <summary>What the sleeping-tabs policy needs to know about one tab.</summary>
/// <remarks><c>HasLoadedContent</c>: False for tabs that hold nothing in an engine (start page, Mosaic's own pages, tabs whose page was never loaded).</remarks>
/// <remarks><c>LastActive</c>: When the tab was last the selected tab.</remarks>
public readonly record struct SleepCandidate(
    Guid Id, DateTimeOffset LastActive, bool IsSelected = false, bool IsAsleep = false, bool HasLoadedContent = true,
    bool IsPinned = false, bool IsPlayingAudio = false, bool HasActiveDownload = false);

public enum SleepExemption { Selected, AlreadyAsleep, NothingLoaded, Pinned, PlayingAudio, Downloading }

/// <summary>
/// Which background tabs to unload. Port of <c>Sources/MosaicCore/Tabs/SleepPolicy.swift</c>.
/// </summary>
/// <remarks>
/// normal: tabs inactive for the configured minutes (if unloading is on); warning: tabs inactive
/// for a quarter of that (at least one minute); critical: every eligible background tab. The two
/// pressure levels apply only with <see cref="SleepSettings.RespondToMemoryPressure"/>, and work
/// even when timed unloading is off. Exempt in every case: the selected tab, tabs playing audio,
/// tabs with an active download, pinned tabs if so configured, and tabs that hold nothing.
/// </remarks>
public static class SleepPolicy
{
    public static SleepExemption? Exemption(SleepCandidate tab, SleepSettings settings)
    {
        if (tab.IsSelected) return SleepExemption.Selected;
        if (tab.IsAsleep) return SleepExemption.AlreadyAsleep;
        if (!tab.HasLoadedContent) return SleepExemption.NothingLoaded;
        if (tab.IsPlayingAudio) return SleepExemption.PlayingAudio;
        if (tab.HasActiveDownload) return SleepExemption.Downloading;
        if (tab.IsPinned && settings.NeverUnloadPinned) return SleepExemption.Pinned;
        return null;
    }

    /// <summary>Inactivity after which a tab is unloaded, or null for never.</summary>
    public static TimeSpan? Threshold(SleepSettings settings, MemoryPressure pressure)
    {
        var configured = TimeSpan.FromMinutes(settings.InactiveMinutes);
        TimeSpan? normal = settings.UnloadInactiveTabs ? configured : null;
        if (!settings.RespondToMemoryPressure) return normal;
        return pressure switch
        {
            MemoryPressure.Warning => TimeSpan.FromSeconds(Math.Max(60, configured.TotalSeconds / 4)),
            MemoryPressure.Critical => TimeSpan.Zero,
            _ => normal,
        };
    }

    /// <summary>The tabs to unload now, least recently used first.</summary>
    public static List<Guid> TabsToUnload(
        IEnumerable<SleepCandidate> tabs, SleepSettings settings, MemoryPressure pressure, DateTimeOffset now)
    {
        if (Threshold(settings, pressure) is not { } threshold) return [];
        return tabs
            .Where(tab => Exemption(tab, settings) is null && now - tab.LastActive >= threshold)
            .OrderBy(tab => tab.LastActive)
            .Select(tab => tab.Id)
            .ToList();
    }
}
