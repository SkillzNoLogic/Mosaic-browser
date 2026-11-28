using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.Web.WebView2.Core;
using Mosaic.Core.Browser;
using Mosaic.Core.Engine;
using Mosaic.Core.Permissions;
using Windows.Foundation;

namespace Mosaic.App.Features.Engine;

/// <summary>
/// One Chromium tab's page features around its WebView2: the <see cref="IChromiumPage"/> the
/// model's <see cref="PageFeatures"/> drives, and the WebView2 events it hands over
/// (permission requests, script dialogs, new windows, full screen, audio, context menus).
/// </summary>
/// <remarks>
/// Hooks the browser as soon as its CoreWebView2 exists (<c>CoreWebView2Initialized</c>), which
/// is before the winui area's own handlers are added, so a blocked pop-up is marked handled
/// before anything opens a tab for it.
/// </remarks>
internal sealed class ChromiumPage : IChromiumPage
{
    private readonly BrowserTab tab;
    private readonly PageFeatures pages;
    private CoreWebView2? core;
    private bool closed;
    private double zoom = 1;
    private bool muted;
    private string? zoomScriptId;
    private int zoomVersion;

    public ChromiumPage(BrowserTab tab, IChromiumBrowser browser, WebView2 view, PageFeatures pages)
    {
        this.tab = tab;
        Browser = browser;
        View = view;
        this.pages = pages;
        if (view.CoreWebView2 is { } existing) Hook(existing);
        else view.CoreWebView2Initialized += Initialized;
    }

    public IChromiumBrowser Browser { get; }
    public WebView2 View { get; }
    public bool HasDevTools { get; private set; }

    /// <summary>The CoreWebView2 of any live tab, for profile-wide settings.</summary>
    public CoreWebView2? Core => closed ? null : core;

    public void Close()
    {
        closed = true;
        View.CoreWebView2Initialized -= Initialized;
    }

    private void Initialized(WebView2 sender, CoreWebView2InitializedEventArgs args)
    {
        if (args.Exception is null && sender.CoreWebView2 is { } created) Hook(created);
    }

    private void Hook(CoreWebView2 created)
    {
        if (closed || core is not null) return;
        core = created;
        Try(() =>
        {
            var settings = created.Settings;
            // Mosaic draws JavaScript dialogs and zooms pages itself.
            settings.AreDefaultScriptDialogsEnabled = false;
            settings.IsZoomControlEnabled = false;
        });
        created.PermissionRequested += PermissionRequested;
        created.ScriptDialogOpening += ScriptDialogOpening;
        created.NewWindowRequested += NewWindowRequested;
        created.ContainsFullScreenElementChanged += (_, _) => Try(() => pages.ChromiumFullScreenChanged(tab, created.ContainsFullScreenElement));
        created.IsDocumentPlayingAudioChanged += (_, _) => Try(() => pages.ChromiumAudioChanged(tab, created.IsDocumentPlayingAudio));
        created.ContextMenuRequested += ContextMenuRequested;
        if (muted) Try(() => created.IsMuted = true);
        if (!PageZoom.IsDefault(zoom)) _ = ApplyZoomAsync();
    }

    private static void Try(Action action)
    {
        try
        {
            action();
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or NotImplementedException)
        {
            // The browser is gone, or the runtime is older than the feature; nothing to do.
        }
    }

    private bool Ready => !closed && core is not null;

    // ---- IChromiumPage ----

    public async Task<FindStatus?> FindAsync(string text, bool matchCase, bool again, bool backwards)
    {
        var result = await ScriptAsync(ChromiumScripts.Find(again ? "again" : "find", text, matchCase, backwards));
        return result is JsonObject json
            ? new FindStatus(Number(json["current"]), Number(json["total"]), Number(json["limit"]))
            : null;
    }

    public void StopFind() => _ = ScriptAsync(ChromiumScripts.Find("stop", "", false, false));

    public void SetZoom(double factor)
    {
        // Nothing to do at 100 % until a zoom has been applied.
        if (Math.Abs(factor - zoom) < 0.0001 && (zoomScriptId is not null || PageZoom.IsDefault(factor))) return;
        zoom = factor;
        if (Ready) _ = ApplyZoomAsync();
    }

    /// <summary>Applies the zoom now and to every document the tab loads from here on.</summary>
    private async Task ApplyZoomAsync()
    {
        if (core is not { } browser) return;
        var version = ++zoomVersion;
        var script = ChromiumScripts.Zoom(zoom);
        try
        {
            if (zoomScriptId is { } old)
            {
                zoomScriptId = null;
                browser.RemoveScriptToExecuteOnDocumentCreated(old);
            }
            await browser.ExecuteScriptAsync(script);
            var id = await browser.AddScriptToExecuteOnDocumentCreatedAsync(script);
            if (version == zoomVersion) zoomScriptId = id;
            else browser.RemoveScriptToExecuteOnDocumentCreated(id);
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or ArgumentException)
        {
            // Closed meanwhile.
        }
    }

    public void SetMuted(bool value)
    {
        muted = value;
        if (core is { } browser) Try(() => browser.IsMuted = value);
    }

    public void Print()
    {
        if (core is { } browser) Try(() => browser.ShowPrintUI(CoreWebView2PrintDialogKind.Browser));
    }

    public async Task<bool> PrintToPdfAsync(string path)
    {
        if (core is not { } browser) return false;
        try
        {
            return await browser.PrintToPdfAsync(path, null);
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void OpenDevTools()
    {
        if (core is not { } browser) return;
        Try(() =>
        {
            browser.OpenDevToolsWindow();
            HasDevTools = true;
        });
    }

    public async Task<PageCapture?> CaptureAsync(bool fullPage)
    {
        var parameters = new JsonObject { ["format"] = "png" };
        var cropped = false;
        if (fullPage && await DevToolsAsync("Page.getLayoutMetrics", new JsonObject()) is JsonObject metrics
            && metrics["cssContentSize"] is JsonObject size)
        {
            var width = Real(size["width"]);
            var height = Real(size["height"]);
            if (width > 0 && height > 0)
            {
                var shown = Math.Min(height, PageFeatures.MaxScreenshotHeight);
                cropped = height > shown;
                parameters["captureBeyondViewport"] = true;
                parameters["clip"] = new JsonObject { ["x"] = 0, ["y"] = 0, ["width"] = width, ["height"] = shown, ["scale"] = 1 };
            }
        }
        if (await DevToolsAsync("Page.captureScreenshot", parameters) is not JsonObject shot
            || shot["data"] is not JsonValue data || !data.TryGetValue(out string? base64))
        {
            return null;
        }
        byte[] png;
        try
        {
            png = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return null;
        }
        var (pixelsWide, pixelsHigh) = PngSize(png);
        return new PageCapture(png, pixelsWide, pixelsHigh, cropped);
    }

    /// <summary>Width and height from a PNG's IHDR chunk.</summary>
    internal static (int Width, int Height) PngSize(byte[] png) =>
        png.Length < 24 ? (0, 0)
            : ((png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19], (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23]);

    public async Task<string> TogglePictureInPictureAsync()
    {
        var result = await EvaluateAsync(ChromiumScripts.PictureInPicture, userGesture: true);
        return result is JsonValue value && value.TryGetValue(out string? text) ? text : "unsupported";
    }

    public void ExitFullScreen() => _ = ScriptAsync(ChromiumScripts.ExitFullScreen);

    public async Task<JsonNode?> EvaluateAsync(string script, bool userGesture)
    {
        var reply = await DevToolsAsync("Runtime.evaluate", new JsonObject
        {
            ["expression"] = script,
            ["userGesture"] = userGesture,
            ["awaitPromise"] = true,
            ["returnByValue"] = true,
        });
        return reply?["result"]?["value"]?.DeepClone();
    }

    public void Click(double x, double y, int button)
    {
        var name = button == 2 ? "right" : "left";
        foreach (var type in new[] { "mousePressed", "mouseReleased" })
        {
            _ = DevToolsAsync("Input.dispatchMouseEvent", new JsonObject
            {
                ["type"] = type, ["x"] = x, ["y"] = y, ["button"] = name, ["clickCount"] = 1,
            });
        }
    }

    private async Task<JsonNode?> ScriptAsync(string script)
    {
        if (core is not { } browser || closed) return null;
        try
        {
            var json = await browser.ExecuteScriptAsync(script);
            return string.IsNullOrEmpty(json) ? null : JsonNode.Parse(json);
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or JsonException)
        {
            return null;
        }
    }

    private async Task<JsonNode?> DevToolsAsync(string method, JsonObject parameters)
    {
        if (core is not { } browser || closed) return null;
        try
        {
            return JsonNode.Parse(await browser.CallDevToolsProtocolMethodAsync(method, parameters.ToJsonString()));
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    private static int Number(JsonNode? node) => node is JsonValue value && value.TryGetValue(out double number) ? (int)number : 0;
    private static double Real(JsonNode? node) => node is JsonValue value && value.TryGetValue(out double number) ? number : 0;

    // ---- WebView2 events ----

    private sealed class PermissionAnswer(CoreWebView2PermissionRequestedEventArgs request, Deferral deferral) : IPermissionResponder
    {
        private bool done;

        public void Respond(PermissionResponse response)
        {
            if (done) return;
            done = true;
            Try(() =>
            {
                // "Decide later" denies this once: Default would bring up WebView2's own prompt.
                request.State = response == PermissionResponse.Allow ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
                deferral.Complete();
            });
        }
    }

    private void PermissionRequested(CoreWebView2 sender, CoreWebView2PermissionRequestedEventArgs request)
    {
        if (Describe(request.PermissionKind) is not var (kind, other)) return; // left to WebView2
        Try(() => request.SavesInProfile = false); // Mosaic remembers, per site, for both engines
        var deferral = request.GetDeferral();
        var origin = SitePermissionStore.Site(request.Uri) ?? request.Uri;
        pages.ChromiumPermissionRequested(tab, new PermissionRequestInfo(origin, [kind], other is null ? [] : [other]), new PermissionAnswer(request, deferral));
    }

    /// <summary>WebView2's permission kinds in Mosaic's terms; null for ones WebView2 decides alone.</summary>
    internal static (PermissionKind Kind, string? Other)? Describe(CoreWebView2PermissionKind kind) => kind switch
    {
        CoreWebView2PermissionKind.Camera => (PermissionKind.Camera, null),
        CoreWebView2PermissionKind.Microphone => (PermissionKind.Microphone, null),
        CoreWebView2PermissionKind.Geolocation => (PermissionKind.Location, null),
        CoreWebView2PermissionKind.Notifications => (PermissionKind.Notifications, null),
        CoreWebView2PermissionKind.ClipboardRead => (PermissionKind.Clipboard, null),
        CoreWebView2PermissionKind.OtherSensors => (PermissionKind.Other, "sensors"),
        CoreWebView2PermissionKind.MultipleAutomaticDownloads => (PermissionKind.Other, "multiple-downloads"),
        CoreWebView2PermissionKind.FileReadWrite => (PermissionKind.Other, "file-system"),
        CoreWebView2PermissionKind.LocalFonts => (PermissionKind.Other, "local-fonts"),
        CoreWebView2PermissionKind.MidiSystemExclusiveMessages => (PermissionKind.Other, "midi-sysex"),
        CoreWebView2PermissionKind.WindowManagement => (PermissionKind.Other, "window-management"),
        _ => null,
    };

    private sealed class DialogAnswer(CoreWebView2ScriptDialogOpeningEventArgs dialog, Deferral deferral) : IDialogResponder
    {
        private bool done;

        public void Finish(bool accepted, string? text)
        {
            if (done) return;
            done = true;
            Try(() =>
            {
                if (accepted)
                {
                    if (text is not null) dialog.ResultText = text;
                    dialog.Accept();
                }
                deferral.Complete();
            });
        }
    }

    private void ScriptDialogOpening(CoreWebView2 sender, CoreWebView2ScriptDialogOpeningEventArgs dialog)
    {
        var kind = dialog.Kind switch
        {
            CoreWebView2ScriptDialogKind.Confirm => PageDialogKind.Confirm,
            CoreWebView2ScriptDialogKind.Prompt => PageDialogKind.Prompt,
            CoreWebView2ScriptDialogKind.Beforeunload => PageDialogKind.BeforeUnload,
            _ => PageDialogKind.Alert,
        };
        var deferral = dialog.GetDeferral();
        pages.ChromiumDialogOpening(tab, kind, dialog.Message ?? "", dialog.DefaultText ?? "", dialog.Uri ?? "", new DialogAnswer(dialog, deferral));
    }

    private void NewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs request)
    {
        if (pages.ChromiumPopupRequested(tab, request.Uri, request.IsUserInitiated) == PopupVerdict.Block) request.Handled = true;
    }

    // ---- Context menu ----

    private void ContextMenuRequested(CoreWebView2 sender, CoreWebView2ContextMenuRequestedEventArgs request)
    {
        IReadOnlyList<PageMenuEntry> entries;
        PageContextTarget target;
        try
        {
            target = Target(request.ContextMenuTarget);
            entries = pages.ContextMenu(tab, target, [.. request.MenuItems.Select(Native)]);
        }
        catch (Exception error) when (error is COMException or InvalidOperationException)
        {
            return; // WebView2 shows its own menu
        }
        if (entries.Count == 0) return;

        var deferral = request.GetDeferral();
        request.Handled = true;
        var finished = false;
        void Finish(PageMenuEntry? chosen)
        {
            if (finished) return;
            finished = true;
            Try(() =>
            {
                if (chosen is { Command: PageMenuCommand.Native } native) request.SelectedCommandId = native.NativeId;
                deferral.Complete();
            });
            if (chosen is { Command: not PageMenuCommand.Native and not PageMenuCommand.Separator } mosaic) pages.RunMenuCommand(tab, mosaic.Command, target);
        }

        var flyout = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        foreach (var item in EngineMenus.Build(entries, chosen =>
        {
            Finish(chosen);
            flyout.Hide();
        }))
        {
            flyout.Items.Add(item);
        }
        flyout.Closed += (_, _) => Finish(null);
        pages.RecordContextMenu(entries, chosen =>
        {
            Finish(chosen);
            flyout.Hide();
        });
        flyout.ShowAt(View, new FlyoutShowOptions { Position = new Point(request.Location.X, request.Location.Y) });
    }

    private static NativeMenuItem Native(CoreWebView2ContextMenuItem item) => new(
        item.Name ?? "",
        (item.Label ?? "").Replace("&", "", StringComparison.Ordinal),
        item.CommandId,
        item.Kind switch
        {
            CoreWebView2ContextMenuItemKind.Separator => NativeMenuItemKind.Separator,
            CoreWebView2ContextMenuItemKind.Submenu => NativeMenuItemKind.Submenu,
            CoreWebView2ContextMenuItemKind.CheckBox => NativeMenuItemKind.CheckBox,
            CoreWebView2ContextMenuItemKind.Radio => NativeMenuItemKind.Radio,
            _ => NativeMenuItemKind.Command,
        },
        item.IsEnabled,
        item.IsChecked,
        item.ShortcutKeyDescription,
        item.Kind == CoreWebView2ContextMenuItemKind.Submenu ? [.. item.Children.Select(Native)] : null);

    private static PageContextTarget Target(CoreWebView2ContextMenuTarget target) => new(
        target.Kind switch
        {
            CoreWebView2ContextMenuTargetKind.Image => PageContextKind.Image,
            CoreWebView2ContextMenuTargetKind.SelectedText => PageContextKind.SelectedText,
            CoreWebView2ContextMenuTargetKind.Audio => PageContextKind.Audio,
            CoreWebView2ContextMenuTargetKind.Video => PageContextKind.Video,
            _ => PageContextKind.Page,
        },
        target.PageUri ?? "",
        target.HasLinkUri ? target.LinkUri : null,
        target.HasLinkText ? target.LinkText : null,
        target.HasSourceUri ? target.SourceUri : null,
        target.HasSelection ? target.SelectionText : null,
        target.IsEditable);
}
