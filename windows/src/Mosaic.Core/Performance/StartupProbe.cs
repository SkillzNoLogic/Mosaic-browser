using System.Collections.Specialized;
using System.ComponentModel;
using Mosaic.Core.Browser;
using Mosaic.Core.Gecko;

namespace Mosaic.Core.Performance;

/// <summary>
/// Marks the model-level start-up moments (<see cref="StartupTrace.ModelReady"/>, the first
/// navigation, <see cref="StartupTrace.GeckoReady"/>) by watching the browser model's own
/// events, so no other area's code needs a call site for them. Every subscription is dropped
/// as soon as its moment has been marked: nothing keeps running after start-up.
/// </summary>
public static class StartupProbe
{
    /// <summary>Call once, on the UI thread, when the model exists (the perf startup hook does).</summary>
    public static void Attach(BrowserModel model)
    {
        StartupTrace.Mark(StartupTrace.ModelReady);
        WatchGecko(model.Gecko);
        if (!StartupTrace.Has(StartupTrace.FirstNavigationCompleted)) new NavigationWatch(model).Start();
    }

    private static void WatchGecko(IGeckoEngine gecko)
    {
        if (Check()) return;
        gecko.StatusChanged += Changed;

        void Changed()
        {
            if (Check()) gecko.StatusChanged -= Changed;
        }

        bool Check()
        {
            switch (gecko.Status)
            {
                case GeckoStatus.Running:
                    StartupTrace.Mark(StartupTrace.GeckoReady);
                    return true;
                case GeckoStatus.Failed:
                    StartupTrace.Note("gecko", "failed");
                    return true;
                default:
                    return false;
            }
        }
    }

    /// <summary>Follows every tab until one has started and then finished loading.</summary>
    private sealed class NavigationWatch(BrowserModel model)
    {
        private readonly HashSet<BrowserTab> watched = [];
        private BrowserTab? first;
        private bool done;

        public void Start()
        {
            model.Tabs.CollectionChanged += TabsChanged;
            foreach (var tab in model.Tabs) Watch(tab);
        }

        private void TabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems is null) return;
            foreach (BrowserTab tab in e.NewItems) Watch(tab);
        }

        private void Watch(BrowserTab tab)
        {
            if (done || !watched.Add(tab)) return;
            tab.PropertyChanged += TabChanged;
            if (tab.IsLoading) Started(tab);
        }

        private void TabChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (done || e.PropertyName != nameof(BrowserTab.IsLoading) || sender is not BrowserTab tab) return;
            if (tab.IsLoading) Started(tab);
            else if (ReferenceEquals(tab, first)) Completed(tab);
        }

        private void Started(BrowserTab tab)
        {
            if (first is not null) return;
            first = tab;
            StartupTrace.Mark(StartupTrace.FirstNavigationStarted);
            StartupTrace.Note("firstNavigationEngine", tab.Engine == EngineKind.Chromium ? "chromium" : "gecko");
        }

        private void Completed(BrowserTab tab)
        {
            StartupTrace.Mark(StartupTrace.FirstNavigationCompleted);
            done = true;
            model.Tabs.CollectionChanged -= TabsChanged;
            foreach (var each in watched) each.PropertyChanged -= TabChanged;
            watched.Clear();
        }
    }
}
