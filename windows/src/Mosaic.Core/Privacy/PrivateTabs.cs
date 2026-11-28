using System.Runtime.CompilerServices;
using Mosaic.Core.Browser;

namespace Mosaic.Core.Privacy;

/// <summary>
/// A Chromium engine that can open private browsers: WebView2 controllers with InPrivate mode on,
/// which share one in-memory profile among themselves. Nothing they store reaches the disk, and
/// the profile is discarded when the last of them closes. The app's WebView2 engine implements it.
/// </summary>
public interface IPrivateChromiumEngine
{
    /// <summary>Creates a private browser and starts loading <paramref name="url"/> in it.</summary>
    IChromiumBrowser CreatePrivateBrowser(string url);
}

/// <summary>
/// <c>BrowserTab.IsPrivate</c>: whether a tab is private. Other areas read it to keep private tabs
/// out of history, sessions, recently closed, password saving and the like.
/// </summary>
/// <remarks>
/// An extension property, so the privacy area does not have to edit <see cref="BrowserTab"/>; it
/// reads like a member (<c>tab.IsPrivate</c>) with <c>using Mosaic.Core.Privacy;</c>. Only the
/// privacy area sets it, when it creates the tab (<see cref="BrowserModel.NewPrivateTab"/>), and a
/// tab never stops being private.
/// </remarks>
public static class PrivateTabs
{
    private static readonly ConditionalWeakTable<BrowserTab, object> Private = [];
    private static readonly object Marker = new();

    extension(BrowserTab tab)
    {
        /// <summary>Whether the tab is private (Chromium InPrivate profile, never recorded).</summary>
        public bool IsPrivate => Private.TryGetValue(tab, out _);
    }

    internal static void MarkPrivate(BrowserTab tab) => Private.AddOrUpdate(tab, Marker);
}
