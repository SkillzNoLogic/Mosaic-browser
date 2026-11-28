using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Mosaic.Core.Browser;
using Mosaic.Core.Productivity;

namespace Mosaic.App.Features.Productivity;

/// <summary>Runs once the window and engines exist (<see cref="FeatureMounts.Startup"/>).</summary>
internal static class ProductivityStartup
{
    public const string FullScreen = "View/Full Screen";

    public static void Start(BrowserModel model, Window window)
    {
        var queue = window.DispatcherQueue;
        // After the first frame: the saved settings are read on the thread pool.
        queue.TryEnqueue(DispatcherQueuePriority.Low, () => _ = model.Productivity.StartAsync());

        // F11: the window full screen (Windows convention).
        ShortcutCommands.Register(
            new ShortcutCommand(FullScreen, "Full Screen", "View", KeyCombo.Plain(KeyCombo.FunctionKey(11))),
            _ => ToggleFullScreen(window));

        var accelerators = new ShortcutAccelerators(model, window);
        accelerators.Attach();

        // Commands other areas register later (from any thread) reach the accelerators and Gecko.
        ShortcutCommands.Changed += () => queue.TryEnqueue(model.Productivity.Shortcuts.Refresh);

        new CommandPaletteHost(model, window).Attach();
        window.Closed += (_, _) => model.Productivity.Shutdown();
    }

    private static void ToggleFullScreen(Window window)
    {
        var appWindow = window.AppWindow;
        appWindow.SetPresenter(appWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen
            ? AppWindowPresenterKind.Default
            : AppWindowPresenterKind.FullScreen);
    }
}
