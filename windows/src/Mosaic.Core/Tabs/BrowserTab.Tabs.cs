namespace Mosaic.Core.Browser;

// Tabs area: organisation and sleeping-tab state of a tab. Behaviour is in the BrowserModel
// partials next to this file (Features/Tabs on macOS); only BrowserModel changes these.
public sealed partial class BrowserTab
{
    /// <summary>Opacity the shell applies to a sleeping tab's title and icon.</summary>
    public const double AsleepOpacity = 0.55;

    private bool isPinned;
    private bool isMuted;
    private Guid? workspaceId;
    private Guid? groupId;
    private bool isAsleep;
    private bool isPlayingAudio;

    /// <summary>Pinned tabs sit at the front of their workspace and are never in a group.</summary>
    public bool IsPinned { get => isPinned; internal set => Set(ref isPinned, value); }

    /// <summary>Applied to the tab's engine by the tabs area (Gecko: the agent; Chromium: <see cref="Tabs.IChromiumAudio"/>).</summary>
    public bool IsMuted
    {
        get => isMuted;
        internal set
        {
            if (Set(ref isMuted, value)) Notify(nameof(ShowsAudioIndicator));
        }
    }

    /// <summary>The workspace the tab belongs to (set as soon as the tab is in the list).</summary>
    public Guid? WorkspaceId { get => workspaceId; internal set => Set(ref workspaceId, value); }

    /// <summary>The tab group it belongs to, if any.</summary>
    public Guid? GroupId { get => groupId; internal set => Set(ref groupId, value); }

    /// <summary>
    /// Unloaded to save memory: its engine holds nothing (Chromium) or a discarded browser
    /// (Gecko). URL and title are kept and the page comes back when the tab is selected. The
    /// shell draws such tabs dimmed (<see cref="AsleepOpacity"/>).
    /// </summary>
    public bool IsAsleep
    {
        get => isAsleep;
        internal set
        {
            if (Set(ref isAsleep, value)) Notify(nameof(TabOpacity));
        }
    }

    /// <summary>Set by engines that can tell. Tabs playing audio are never put to sleep.</summary>
    public bool IsPlayingAudio
    {
        get => isPlayingAudio;
        internal set
        {
            if (Set(ref isPlayingAudio, value)) Notify(nameof(ShowsAudioIndicator));
        }
    }

    /// <summary>The speaker icon on a tab: muted, or playing sound.</summary>
    public bool ShowsAudioIndicator => isMuted || isPlayingAudio;

    /// <summary>1, or <see cref="AsleepOpacity"/> for a sleeping tab.</summary>
    public double TabOpacity => isAsleep ? AsleepOpacity : 1;

    /// <summary>When this tab last was, or stopped being, the selected tab.</summary>
    internal DateTimeOffset LastActivated { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Mosaic's own pages (<c>mosaic://…</c>) are drawn by the app; no engine holds them.</summary>
    public bool IsInternalPage => Url.StartsWith("mosaic:", StringComparison.OrdinalIgnoreCase);
}
