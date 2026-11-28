using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Mosaic.Core.Browser;
using Mosaic.Core.Shell;

namespace Mosaic.App.Features.Shell;

/// <summary>
/// Plugs the shell in: the composed window (<c>FeatureMounts.Shell</c>), its three settings
/// pages, and its automation commands and <c>dump</c> fields. Only stores delegates: nothing
/// runs until the window asks for its content.
/// </summary>
internal static class ShellMounts
{
    /// <summary>The window's shell (one window).</summary>
    internal static ShellRoot? Current { get; private set; }

    [ModuleInitializer]
    internal static void Register()
    {
        FeatureMounts.Shell = (model, window) => Current = new ShellRoot(model, window);
        FeatureMounts.SettingsPages[SettingsCategory.Mosaics] = _ => new MosaicsSettingsPage(Require());
        FeatureMounts.SettingsPages[SettingsCategory.General] = _ => new GeneralSettingsPage(Require());
        FeatureMounts.SettingsPages[SettingsCategory.Accessibility] = _ => new AccessibilitySettingsPage(Require());
        ShellAutomation.Register();
        ShellAutomation.UiState = UiState;
        AutomationRegistry.Register(HandleAsync);
    }

    private static ShellRoot Require() => Current ?? throw new InvalidOperationException("The shell is not showing.");

    /// <summary>What only the WinUI shell knows, added to <c>dump</c>'s <c>shell</c> object.</summary>
    private static JsonObject? UiState(BrowserModel model)
    {
        if (Current is not { } root) return new JsonObject { ["rendered"] = false };
        return OnUiThread(root, () => new JsonObject
        {
            ["rendered"] = root.IsLoaded,
            ["rebuilds"] = root.RebuildCount,
            ["theme"] = root.IsDark ? "dark" : "light",
            ["backdrop"] = root.BackdropKind,
            ["translucent"] = root.Theme.Translucent,
            ["highContrast"] = root.Theme.HighContrast,
            ["reduceMotion"] = root.Theme.ReduceMotion,
            ["reduceTransparency"] = root.Theme.ReduceTransparency,
            ["micaSupported"] = ShellTheme.MicaSupported,
            ["accentColor"] = $"#{root.Theme.AccentColor.R:X2}{root.Theme.AccentColor.G:X2}{root.Theme.AccentColor.B:X2}",
            ["captionHeight"] = root.TitleBarCaptionHeight,
            ["titleBarPassthrough"] = root.TitleBarPassthroughCount,
            ["placeholders"] = new JsonArray([.. root.Placeholders.Order(StringComparer.Ordinal).Select(p => (JsonNode)JsonValue.Create(p))]),
            ["pagePlaceholder"] = PageArea.IsPlaceholder,
            ["pageCovered"] = FeatureMounts.IsPageCovered,
            ["pageCovers"] = new JsonArray([.. FeatureMounts.PageCovers.Order(StringComparer.Ordinal).Select(p => (JsonNode)JsonValue.Create(p))]),
            ["unnamedControls"] = new JsonArray([.. root.UnnamedControls().Select(p => (JsonNode)JsonValue.Create(p))]),
            ["pageRect"] = Rect(root, root.PageHost),
        }) ?? new JsonObject { ["rendered"] = false };
    }

    private static JsonObject Rect(ShellRoot root, FrameworkElement element)
    {
        try
        {
            var r = root.Bounds(element);
            return new JsonObject { ["x"] = Math.Round(r.X, 1), ["y"] = Math.Round(r.Y, 1), ["width"] = Math.Round(r.Width, 1), ["height"] = Math.Round(r.Height, 1) };
        }
        catch (ArgumentException)
        {
            return new JsonObject();
        }
    }

    private static T? OnUiThread<T>(FrameworkElement element, Func<T> work) where T : class
    {
        if (element.DispatcherQueue.HasThreadAccess) return work();
        var done = new TaskCompletionSource<T?>();
        if (!element.DispatcherQueue.TryEnqueue(() =>
        {
            try { done.SetResult(work()); }
            catch (Exception error) { done.SetException(error); }
        }))
        {
            return null;
        }
        return done.Task.Wait(TimeSpan.FromSeconds(5)) ? done.Task.Result : null;
    }

    private static async Task<AutomationReply?> HandleAsync(BrowserModel model, string command, string value, string? output)
    {
        switch (command)
        {
            case "shellScreenshot":
                if (Current is not { } root) return AutomationRegistry.Error("The shell is not showing.");
                if (value.Length == 0) return AutomationRegistry.Error("shellScreenshot needs the path to write to.");
                await OnUi(root, () => ShellCapture.CaptureAsync(root, value));
                return AutomationRegistry.Ok();
            case "shellGallery":
                if (Current is not { } galleryRoot) return AutomationRegistry.Error("The shell is not showing.");
                if (value.Length == 0) return AutomationRegistry.Error("shellGallery needs a folder to write to.");
                JsonArray? files = null;
                await OnUi(galleryRoot, async () => files = await ShellCapture.GalleryAsync(galleryRoot, value));
                return new AutomationReply(new JsonObject { ["ok"] = true, ["files"] = files }.ToJsonString());
            default:
                return null;
        }
    }

    private static Task OnUi(FrameworkElement element, Func<Task> work)
    {
        if (element.DispatcherQueue.HasThreadAccess) return work();
        var done = new TaskCompletionSource();
        element.DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                await work();
                done.SetResult();
            }
            catch (Exception error)
            {
                done.SetException(error);
            }
        });
        return done.Task;
    }
}
