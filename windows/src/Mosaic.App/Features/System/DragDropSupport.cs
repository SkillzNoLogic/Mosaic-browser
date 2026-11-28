using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Mosaic.Core.Browser;
using Mosaic.Core.WindowsIntegration;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Mosaic.App.Features.System;

/// <summary>
/// Dropping links, text or files on Mosaic's own UI (tab strip, toolbar, start page, empty
/// areas) opens them in new tabs. Drops on a page are the engine's business (Gecko's window and
/// WebView2 take them themselves). Attached to the window's content as the last handler in the
/// bubbling order, so areas that handle drags themselves (tab reordering) mark the event handled
/// and win.
/// </summary>
internal static class DragDropSupport
{
    private static UIElement? attached;

    public static JsonObject? LastDrop { get; private set; }

    public static bool IsAttached => attached is not null;

    public static void Attach(BrowserModel model, Window window)
    {
        if (window.Content is not UIElement root || ReferenceEquals(root, attached)) return;
        attached = root;
        root.AllowDrop = true;
        root.DragOver += (_, e) =>
        {
            if (e.Handled || !CanOpen(e.DataView)) return;
            e.AcceptedOperation = DataPackageOperation.Link | DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Open in Mosaic";
            e.Handled = true;
        };
        root.Drop += async (_, e) =>
        {
            if (e.Handled || !CanOpen(e.DataView)) return;
            e.Handled = true;
            var deferral = e.GetDeferral();
            try
            {
                var view = e.DataView;
                IEnumerable<string>? files = null;
                string? link = null, text = null;
                if (view.Contains(StandardDataFormats.StorageItems))
                    files = (await view.GetStorageItemsAsync()).Select(item => item.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
                if (view.Contains(StandardDataFormats.WebLink)) link = (await view.GetWebLinkAsync())?.AbsoluteUri;
                if (view.Contains(StandardDataFormats.Text)) text = await view.GetTextAsync();
                Open(model, files, link, text);
            }
            finally
            {
                deferral.Complete();
            }
        };
    }

    private static bool CanOpen(DataPackageView view) =>
        view.Contains(StandardDataFormats.StorageItems) || view.Contains(StandardDataFormats.WebLink) || view.Contains(StandardDataFormats.Text);

    /// <summary>Opens what was dropped (also the automation command <c>simulateDrop</c>). UI thread.</summary>
    public static IReadOnlyList<string> Open(BrowserModel model, IEnumerable<string>? files, string? link, string? text)
    {
        var targets = DropTargets.From(files, link, text);
        LastDrop = new JsonObject
        {
            ["targets"] = new JsonArray([.. targets.Select(t => (JsonNode)JsonValue.Create(t))]),
            ["at"] = DateTimeOffset.UtcNow.ToString("O"),
        };
        // Dropped items always open in new tabs, even on a fresh window.
        foreach (var target in targets)
        {
            model.NewTab(select: true);
            if (target.Contains("://", StringComparison.Ordinal) || target.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
                model.Load(target, model.SelectedTab!);
            else
                model.Navigate(target);
        }
        return targets;
    }
}
