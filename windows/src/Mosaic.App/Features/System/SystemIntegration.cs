using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Mosaic.Core.Browser;
using Mosaic.Core.WindowsIntegration;

namespace Mosaic.App.Features.System;

/// <summary>
/// The system area's wiring in the running app. <see cref="Start"/> runs once after the window
/// exists (a <see cref="FeatureMounts.Startup"/> hook) and does only cheap work: attach drag and
/// drop, subscribe to taskbar progress, listen for window activation. Everything else is created
/// on first use (notifications, appearance listener, taskbar COM object, virtual desktop
/// manager) or deferred off the startup path (the jump list, written a few seconds later on a
/// background thread and only when out of date). Registration as the default browser happens
/// only when the user (or a script) asks.
/// </summary>
public static class SystemIntegration
{
    private static Window? window;
    private static BrowserModel? model;
    private static nint handle;
    private static Taskbar? taskbar;
    private static readonly VirtualDesktops desktops = new();
    private static Flyout? gallery;

    internal static DispatcherQueue? Dispatcher { get; private set; }

    /// <summary>Mosaic's main window (null before start).</summary>
    internal static Window? MainWindow => window;

    /// <summary>Mosaic's window handle (0 before start).</summary>
    public static nint WindowHandle => handle;

    /// <summary>Raised when Gecko's window is found on a different virtual desktop than Mosaic's (winui may re-own or hide it).</summary>
    public static event Action? GeckoDesktopMismatch
    {
        add => desktops.Mismatch += value;
        remove => desktops.Mismatch -= value;
    }

    internal static void Start(BrowserModel browser, Window mainWindow)
    {
        model = browser;
        window = mainWindow;
        Dispatcher = mainWindow.DispatcherQueue;
        handle = WinRT.Interop.WindowNative.GetWindowHandle(mainWindow);

        DragDropSupport.Attach(browser, mainWindow);

        taskbar = new Taskbar(handle);
        TaskbarProgress.Changed += () => OnUi(() => taskbar.Apply());
        taskbar.Apply();

        mainWindow.Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated) return;
            DragDropSupport.Attach(browser, mainWindow);  // the shell may have replaced the content
            CheckDesktops();
        };
        mainWindow.Closed += (_, _) =>
        {
            AppNotificationPresenter.Shutdown();
            TaskbarProgress.Clear("automation");
        };

        // Started by a click on a notification: register now so the click is delivered.
        if (Environment.GetCommandLineArgs().Any(a => a.StartsWith(ActivationRequest.NotificationActivationPrefix, StringComparison.Ordinal)))
            UserNotifications.Start();

        _ = Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(_ => JumpList.UpdateAsync(), TaskScheduler.Default);
    }

    /// <summary>Brings Mosaic's window forward (after a notification click), unless a script is driving it.</summary>
    internal static void BringToFront()
    {
        if (window is null || model is { AllowsFocusChanges: false }) return;
        window.Activate();
    }

    /// <summary>The in-window fallback for notifications when Windows cannot show them.</summary>
    internal static void ShowNotice(string text)
    {
        if (model is not null) model.Notice = text;
    }

    private static JsonObject CheckDesktops() => desktops.Check(handle, model?.Gecko.WindowHandle ?? 0);

    private static void OnUi(Action action)
    {
        if (Dispatcher is null || Dispatcher.HasThreadAccess) action();
        else Dispatcher.TryEnqueue(() => action());
    }

    private static Task<T> OnUiAsync<T>(Func<T> function)
    {
        if (Dispatcher is null || Dispatcher.HasThreadAccess) return Task.FromResult(function());
        var done = new TaskCompletionSource<T>();
        Dispatcher.TryEnqueue(() =>
        {
            try { done.SetResult(function()); }
            catch (Exception error) { done.SetException(error); }
        });
        return done.Task;
    }

    // ---- Automation ----

    /// <summary>
    /// App-side commands (besides <see cref="SystemAutomation"/>'s):
    /// <c>jumpList</c> (rewrite now; reply status), <c>virtualDesktop</c> (check now),
    /// <c>simulateDrop</c> (JSON <c>{"files":[…],"link":"…","text":"…"}</c>: the drop pipeline without OLE),
    /// <c>contextMenuTest</c> (show a sample menu on the window; "close" closes it),
    /// <c>iconGallery</c> (show every icon in a flyout for a screenshot; "close" closes it).
    /// </summary>
    internal static async Task<AutomationReply?> HandleAutomationAsync(BrowserModel browser, string command, string value, string? output)
    {
        JsonObject? reply = command switch
        {
            "jumpList" => new JsonObject { ["ok"] = true, ["status"] = await JumpList.UpdateAsync(force: true) },
            "virtualDesktop" => new JsonObject { ["ok"] = true, ["virtualDesktop"] = await OnUiAsync(CheckDesktops) },
            "simulateDrop" => await OnUiAsync(() => SimulateDrop(browser, value)),
            "contextMenuTest" => await OnUiAsync(() => ContextMenuTest(value)),
            "iconGallery" => await OnUiAsync(() => IconGallery(value)),
            _ => null,
        };
        if (reply is null) return await SystemAutomation.HandleAsync(DefaultBrowser.Shared, browser, command, value, output);
        if (output is not null) await File.WriteAllTextAsync(output, reply.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return new AutomationReply(reply.ToJsonString());
    }

    private static JsonObject SimulateDrop(BrowserModel browser, string value)
    {
        if (JsonNode.Parse(value) is not JsonObject json) return new JsonObject { ["ok"] = false, ["error"] = "Expected a JSON object." };
        var files = json["files"] is JsonArray array ? array.Select(n => n?.GetValue<string>() ?? "").ToList() : null;
        var targets = DragDropSupport.Open(browser, files, json["link"]?.GetValue<string>(), json["text"]?.GetValue<string>());
        return new JsonObject { ["ok"] = true, ["targets"] = new JsonArray([.. targets.Select(t => (JsonNode)JsonValue.Create(t))]) };
    }

    private static JsonObject ContextMenuTest(string value)
    {
        if (value == "close")
        {
            ContextMenus.CloseOpen();
            return new JsonObject { ["ok"] = true, ["open"] = ContextMenus.IsOpen };
        }
        if (window?.Content is not FrameworkElement root) return new JsonObject { ["ok"] = false, ["error"] = "The window has no content yet." };
        ContextMenus.Show(root, new Windows.Foundation.Point(80, 80), new MenuItemSpec[]
        {
            new MenuItemSpec("&Back", () => { }) { Icon = "back", Shortcut = "Alt+Left" },
            new MenuItemSpec("&Reload", () => { }) { Icon = "reload", Shortcut = "Ctrl+R" },
            MenuItemSpec.Separator,
            new MenuItemSpec("Open in new &tab", () => { }) { Icon = "newtab" },
            new MenuItemSpec("Pin tab", () => { }) { Icon = "pin", Checked = true },
            new MenuItemSpec("Engine") { Icon = "engine", Children = new MenuItemSpec[] { new("Mozilla", () => { }) { Checked = true }, new("Chromium", () => { }) { Checked = false } } },
            MenuItemSpec.Separator,
            new MenuItemSpec("Close tab", () => { }) { Icon = "xmark", Shortcut = "Ctrl+W", Destructive = true },
        });
        return new JsonObject { ["ok"] = true, ["open"] = ContextMenus.IsOpen, ["pageCovered"] = FeatureMounts.IsPageCovered };
    }

    private static JsonObject IconGallery(string value)
    {
        if (value == "close")
        {
            gallery?.Hide();
            gallery = null;
            return new JsonObject { ["ok"] = true };
        }
        if (window?.Content is not FrameworkElement root) return new JsonObject { ["ok"] = false, ["error"] = "The window has no content yet." };
        var names = IconGlyphs.Names.Order(StringComparer.Ordinal).ToList();
        var grid = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, MaximumRowsOrColumns = 24, ItemWidth = 30, ItemHeight = 30 };
        foreach (var name in names)
        {
            var icon = Icons.Make(name, 18);
            ToolTipService.SetToolTip(icon, name);
            grid.Children.Add(icon);
        }
        gallery = new Flyout
        {
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = $"{names.Count} icons · {Icons.FontName}" },
                    grid,
                },
            },
            ShouldConstrainToRootBounds = false,
        };
        gallery.Opening += (_, _) => FeatureMounts.CoverPage("system.iconGallery", true);
        gallery.Closed += (_, _) => FeatureMounts.CoverPage("system.iconGallery", false);
        gallery.ShowAt(root, new FlyoutShowOptions { Position = new Windows.Foundation.Point(20, 20), ShowMode = FlyoutShowMode.Transient });
        return new JsonObject { ["ok"] = true, ["icons"] = names.Count, ["font"] = Icons.FontName };
    }

    /// <summary>The <c>system</c> key of <c>dump</c>.</summary>
    internal static JsonNode State(BrowserModel browser)
    {
        var state = SystemAutomation.CoreState();
        state["started"] = window is not null;
        state["iconFont"] = new JsonObject { ["name"] = Icons.FontName, ["fluent"] = Icons.UsesFluent, ["names"] = IconGlyphs.Names.Count() };
        try { state["defaultBrowser"] = DefaultBrowser.Shared.Status().ToJson(); }
        catch (Exception error) { state["defaultBrowser"] = new JsonObject { ["error"] = error.Message }; }
        state["notificationsRegistered"] = AppNotificationPresenter.Registered;
        state["jumpList"] = JumpList.Status;
        if (taskbar is not null) state["taskbar"] = taskbar.State();
        state["virtualDesktop"] = desktops.Last.DeepClone();
        state["dragDrop"] = new JsonObject { ["attached"] = DragDropSupport.IsAttached, ["lastDrop"] = DragDropSupport.LastDrop?.DeepClone() };
        state["contextMenu"] = new JsonObject { ["open"] = ContextMenus.IsOpen, ["shown"] = ContextMenus.ShownCount };
        state["appearanceListener"] = AppearanceListener.Status;
        return state;
    }
}
