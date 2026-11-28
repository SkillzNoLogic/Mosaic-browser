using Mosaic.Core.Engine;
using Mosaic.Core.Permissions;

namespace Mosaic.Core.Browser;

/// <summary>The engine area's part of the model: page features and remembered site permissions.</summary>
public sealed partial class BrowserModel
{
    private PageFeatures? pages;
    private PageFeaturesOptions? pageOptions;

    /// <summary>
    /// Find in page, zoom, printing, screenshots, permission prompts, pop-ups, dialogs and full
    /// screen, in both engines. Created on first use (the app creates it at start-up so engine
    /// requests are answered from the first page on).
    /// </summary>
    public PageFeatures Pages => pages ??= new PageFeatures(this, pageOptions ?? new PageFeaturesOptions());

    /// <summary>
    /// Remembered per-site permissions (camera, microphone, location, notifications, clipboard,
    /// pop-ups), shared by both engines and edited by the privacy area's site-permissions page.
    /// </summary>
    public SitePermissionStore SitePermissions => Pages.Permissions;

    /// <summary>
    /// Mutes or unmutes <paramref name="tab"/>'s page in its engine (and keeps it muted if it
    /// moves to the other engine). For the tabs area, which owns the mute state.
    /// </summary>
    public void ApplyMute(BrowserTab tab, bool muted) => Pages.ApplyMute(tab, muted);

    /// <summary>Where the page features keep their files; tests call this before first use of <see cref="Pages"/>.</summary>
    internal void ConfigurePages(PageFeaturesOptions options)
    {
        if (pages is not null) throw new InvalidOperationException("The page features already exist.");
        pageOptions = options;
    }

    /// <summary>
    /// The engine area's say before a Chromium page's new window becomes a tab: true when it
    /// blocked that pop-up (see <see cref="PageFeatures.ChromiumPopupRequested"/>).
    /// </summary>
    internal bool SuppressesPopup(BrowserTab tab, string url) => pages?.TakeSuppressedPopup(tab, url) ?? false;

    internal DateTimeOffset Now => time.GetUtcNow();

    internal Task Delay(TimeSpan delay) => Task.Delay(delay, time);
}
