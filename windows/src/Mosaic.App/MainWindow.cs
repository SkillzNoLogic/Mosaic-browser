using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Mosaic.App.Chromium;
using Mosaic.App.Features;
using Mosaic.App.Hosting;
using Mosaic.Core.Browser;
using Windows.Graphics;
using Windows.System;
using Windows.UI;
using Windows.UI.ViewManagement;
using static Mosaic.App.Hosting.NativeMethods;

namespace Mosaic.App;

/// <summary>The window's backdrop material (the shell's materials toggle).</summary>
public enum WindowMaterial
{
    /// <summary>Mica (Windows 11). Falls back to Acrylic, then solid.</summary>
    Mica,
    /// <summary>Mica Alt, the stronger tint used behind tabbed title bars.</summary>
    MicaAlt,
    /// <summary>Desktop Acrylic (Windows 10 and 11).</summary>
    Acrylic,
    /// <summary>No transparency: the theme's solid background.</summary>
    Solid,
}

/// <summary>
/// Mosaic's window: the engines' page area (<see cref="ContentArea"/>), the title bar, the
/// backdrop, size and position, full screen, keyboard shortcuts. Its content is the shell
/// area's composed shell (<see cref="FeatureMounts.Shell"/>) or, until that exists, the
/// minimal built-in <see cref="BasicShell"/>.
/// </summary>
/// <remarks>
/// <para><b>For the shell.</b> Place <see cref="ContentArea"/> where the page goes. The content
/// extends into the title bar: call <see cref="Window.SetTitleBar"/> with the element that
/// should drag the window, <see cref="SetInteractiveTitleBarElements"/> for controls inside it
/// (tab strips), and leave <see cref="CaptionButtonsWidth"/> free on the right for the caption
/// buttons (Snap Layouts appear on the maximise button). <see cref="Material"/> switches the
/// backdrop; <see cref="IsBackdropTransparent"/> says whether to paint a background.</para>
/// </remarks>
public sealed class MainWindow : Window
{
    /// <summary>Starting size in DIPs; shrunk to fit small screens (1024×768, 1366×768).</summary>
    private const double DefaultWidth = 1240, DefaultHeight = 800;
    /// <summary>Smallest size in DIPs. Fits a 1024×768 screen at 100 % with room to spare.</summary>
    private const double MinimumWidth = 720, MinimumHeight = 420;
    private const double ScreenMargin = 24;

    private readonly BrowserModel model;
    private readonly BrowserSettings settings;
    private readonly Grid root = new();
    private readonly WindowMessages messages;
    private readonly UISettings uiSettings = new();
    private readonly List<FrameworkElement> interactiveTitleBarElements = [];
    private readonly OverlappedPresenter overlapped;
    private WindowMaterial material = WindowMaterial.Mica;
    private WindowMaterial effectiveMaterial = WindowMaterial.Solid;
    private RectInt32 restoredBounds;
    private bool isShutDown;

    internal MainWindow(BrowserModel model, WebView2Engine chromium, BrowserSettings settings, GeckoHostMode? geckoHostMode)
    {
        this.model = model;
        this.settings = settings;
        Title = "Mosaic";
        Handle = WinRT.Interop.WindowNative.GetWindowHandle(this);

        messages = new WindowMessages(Handle)
        {
            MinimumSize = () => ((int)(MinimumWidth * Scale), (int)(MinimumHeight * Scale)),
        };
        messages.Add(OnWindowMessage);

        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "Mosaic.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);

        // The title bar is Mosaic's own content; the caption buttons stay the system's.
        ExtendsContentIntoTitleBar = true;
        var titleBar = AppWindow.TitleBar;
        titleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

        overlapped = AppWindow.Presenter as OverlappedPresenter ?? OverlappedPresenter.Create();
        PlaceInitially();

        var gecko = new GeckoWindowHost(Handle, messages, DispatcherQueue);
        if (geckoHostMode is { } mode) gecko.Mode = mode;
        ContentArea = new ContentArea(model, chromium, gecko, Handle);

        // Shortcuts work whatever the shell: this sees key presses before any control, including
        // the accelerator keys WebView2 passes on while a Chromium page has focus.
        root.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(Root_PreviewKeyDown), handledEventsToo: true);
        root.Children.Add(CreateShell());
        Content = root;

        uiSettings.AdvancedEffectsEnabledChanged += (_, _) => DispatcherQueue.TryEnqueue(ApplyMaterial);
        ApplyMaterial();

        model.WindowActivationRequested += BringToFront;
        AppWindow.Changed += AppWindow_Changed;
        Closed += (_, _) => ShutDownBrowser();
    }

    /// <summary>The top-level window handle.</summary>
    public nint Handle { get; }

    /// <summary>The page area (both engines). The shell places it in its layout.</summary>
    public ContentArea ContentArea { get; }

    /// <summary>
    /// The window's content, which never changes after start-up (the shell is its child). The
    /// place for window-wide <c>KeyboardAccelerators</c> and input handlers (start-up hooks).
    /// </summary>
    public Grid Root => root;

    /// <summary>
    /// The window's one entry for keyboard shortcuts (productivity's shortcut manager): called
    /// for every key press, from Mosaic's own controls and, forwarded by WebView2, from Chromium
    /// pages, before any control acts on it. Return true to consume the key. While it is set the
    /// built-in v0.1 table (<see cref="Shortcuts"/>) is not consulted. Called on the UI thread
    /// from inside the key event: do slow or re-entrant work later (WebView2 waits meanwhile).
    /// </summary>
    public Func<VirtualKey, ShortcutModifiers, bool>? KeyHandler { get; set; }

    /// <summary>The window's messages, for areas that need one WinUI does not surface.</summary>
    public WindowMessages Messages => messages;

    /// <summary>Physical pixels per DIP on the window's current monitor.</summary>
    public double Scale => GetDpiForWindow(Handle) is var dpi and > 0 ? dpi / 96.0 : 1;

    // ---- Showing ----

    /// <summary>Shows the window; with <paramref name="activate"/> false it does not take the focus (automation).</summary>
    internal void Show(bool activate)
    {
        if (activate)
        {
            Activate();
        }
        else
        {
            AppWindow.Show(activateWindow: false);
        }
        if (settings.WindowPlacement is { Maximized: true }) overlapped.Maximize();
    }

    internal void BringToFront()
    {
        if (overlapped.State == OverlappedPresenterState.Minimized) overlapped.Restore();
        Activate();
    }

    private UIElement CreateShell()
    {
        if (FeatureMounts.Shell is { } shell)
        {
            try
            {
                return shell(model, this);
            }
            catch (Exception error)
            {
                // The built-in UI keeps Mosaic usable if the shell cannot be built.
                Trace.TraceError($"[MainWindow] The shell could not be created: {error}");
            }
        }
        return new BasicShell(model, this);
    }

    // ---- Lifetime ----

    /// <summary>
    /// Lets go of both engines and saves the window's place. Runs once, while the window still
    /// exists: Gecko's window has to be detached before Mosaic's is destroyed.
    /// </summary>
    internal void ShutDownBrowser()
    {
        if (isShutDown) return;
        isShutDown = true;
        model.WindowActivationRequested -= BringToFront;
        SavePlacement();
        ContentArea.ShutDown();
        model.Shutdown();
    }

    private void OnWindowMessage(int message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case WM_ENDSESSION when wParam != 0:
                // Signing out or shutting down may end the process without the window closing.
                ShutDownBrowser();
                break;
            case WM_DPICHANGED:
                UpdateInteractiveRegions();
                break;
        }
    }

    // ---- Size and position ----

    /// <summary>The saved place if it is still on a screen, otherwise a size that fits, centred.</summary>
    private void PlaceInitially()
    {
        // The minimum size is applied through WM_GETMINMAXINFO (WindowMessages), at the DPI of
        // whichever monitor the window is on.
        if (settings.WindowPlacement is { } saved)
        {
            var bounds = new RectInt32(saved.X, saved.Y, saved.Width, saved.Height);
            if (DisplayArea.GetFromRect(bounds, DisplayAreaFallback.None) is { } display)
            {
                var work = display.WorkArea;
                bounds.Width = Math.Min(bounds.Width, work.Width);
                bounds.Height = Math.Min(bounds.Height, work.Height);
                // Keep the title bar reachable.
                bounds.X = Math.Clamp(bounds.X, work.X - bounds.Width + 100, work.X + work.Width - 100);
                bounds.Y = Math.Clamp(bounds.Y, work.Y, work.Y + work.Height - 50);
                AppWindow.MoveAndResize(bounds);
                restoredBounds = bounds;
                return;
            }
        }

        var area = (DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary) ?? DisplayArea.Primary).WorkArea;
        double scale = Scale, margin = ScreenMargin * scale;
        int width = (int)Math.Max(MinimumWidth * scale, Math.Min(DefaultWidth * scale, area.Width - 2 * margin));
        int height = (int)Math.Max(MinimumHeight * scale, Math.Min(DefaultHeight * scale, area.Height - 2 * margin));
        restoredBounds = new RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height);
        AppWindow.MoveAndResize(restoredBounds);
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if ((args.DidPositionChange || args.DidSizeChange) && AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored })
        {
            restoredBounds = new RectInt32(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
        }
        if (args.DidSizeChange) UpdateInteractiveRegions();
        if (args.DidPresenterChange) PageFullScreenChanged?.Invoke();
    }

    private void SavePlacement()
    {
        if (restoredBounds.Width <= 0 || restoredBounds.Height <= 0) return;
        settings.WindowPlacement = new WindowPlacement(restoredBounds.X, restoredBounds.Y, restoredBounds.Width, restoredBounds.Height,
            overlapped.State == OverlappedPresenterState.Maximized);
        settings.Save();
    }

    /// <summary>Maximises, minimises or restores the window (automation and the shell's menus).</summary>
    public void SetState(OverlappedPresenterState state)
    {
        if (AppWindow.Presenter.Kind != AppWindowPresenterKind.Overlapped) AppWindow.SetPresenter(overlapped);
        switch (state)
        {
            case OverlappedPresenterState.Maximized: overlapped.Maximize(); break;
            case OverlappedPresenterState.Minimized: overlapped.Minimize(activateWindow: false); break;
            default: overlapped.Restore(activateWindow: false); break;
        }
    }

    public OverlappedPresenterState State => overlapped.State;

    /// <summary>Resizes the restored window to <paramref name="width"/>×<paramref name="height"/> DIPs.</summary>
    public void ResizeTo(double width, double height)
    {
        SetState(OverlappedPresenterState.Restored);
        AppWindow.Resize(new SizeInt32((int)(width * Scale), (int)(height * Scale)));
    }

    // ---- Page full screen (for the engine area) ----

    /// <summary>True while the window is full screen for a page (video, presentation).</summary>
    public bool IsPageFullScreen => AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;

    /// <summary>
    /// Puts the window into or out of full screen, for a page that asked for it
    /// (<c>requestFullscreen</c>). The shell hides its chrome while <see cref="IsPageFullScreen"/>.
    /// </summary>
    public void SetPageFullScreen(bool fullScreen)
    {
        if (fullScreen == IsPageFullScreen) return;
        // The overlapped presenter is kept, so leaving full screen restores a maximised window
        // as maximised.
        if (fullScreen) AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        else AppWindow.SetPresenter(overlapped);
    }

    /// <summary>Raised when the window enters or leaves full screen.</summary>
    public event Action? PageFullScreenChanged;

    // ---- Title bar ----

    /// <summary>Width (DIPs) the system caption buttons take on the right; keep it clear.</summary>
    public double CaptionButtonsWidth => AppWindow.TitleBar.RightInset / Scale;

    /// <summary>Width (DIPs) reserved on the left (right-to-left layouts put the buttons there).</summary>
    public double CaptionLeftInset => AppWindow.TitleBar.LeftInset / Scale;

    /// <summary>Height (DIPs) of the title bar area the caption buttons occupy.</summary>
    public double TitleBarHeight => AppWindow.TitleBar.Height / Scale;

    /// <summary>
    /// Controls inside the title bar (set with <see cref="Window.SetTitleBar"/>) that take
    /// clicks rather than dragging the window: tab strips, buttons, a search box. Their
    /// rectangles are kept up to date on layout and DPI changes.
    /// </summary>
    public void SetInteractiveTitleBarElements(params FrameworkElement[] elements)
    {
        foreach (var element in interactiveTitleBarElements)
        {
            element.SizeChanged -= InteractiveElement_LayoutChanged;
        }
        interactiveTitleBarElements.Clear();
        interactiveTitleBarElements.AddRange(elements);
        foreach (var element in elements)
        {
            element.SizeChanged += InteractiveElement_LayoutChanged;
        }
        UpdateInteractiveRegions();
    }

    private void InteractiveElement_LayoutChanged(object sender, SizeChangedEventArgs e) => UpdateInteractiveRegions();

    /// <summary>Re-applies the interactive regions (call after moving the title bar's controls).</summary>
    public void UpdateInteractiveRegions()
    {
        if (isShutDown) return;
        var rects = new List<RectInt32>();
        foreach (var element in interactiveTitleBarElements)
        {
            if (element.XamlRoot is not { } xamlRoot || element.Visibility != Visibility.Visible || element.ActualWidth <= 0) continue;
            try
            {
                var bounds = element.TransformToVisual(null).TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
                double scale = xamlRoot.RasterizationScale;
                rects.Add(new RectInt32((int)Math.Round(bounds.X * scale), (int)Math.Round(bounds.Y * scale),
                    (int)Math.Round(bounds.Width * scale), (int)Math.Round(bounds.Height * scale)));
            }
            catch (ArgumentException)
            {
                // Not in the tree right now.
            }
        }
        try
        {
            InputNonClientPointerSource.GetForWindowId(AppWindow.Id).SetRegionRects(NonClientRegionKind.Passthrough, [.. rects]);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Trace.TraceWarning($"[MainWindow] Could not set the title bar's interactive regions: {error.Message}");
        }
    }

    // ---- Backdrop ----

    /// <summary>The material the shell asked for. The window falls back from it when needed (<see cref="EffectiveMaterial"/>).</summary>
    public WindowMaterial Material
    {
        get => material;
        set
        {
            if (value == material) return;
            material = value;
            ApplyMaterial();
        }
    }

    /// <summary>What is actually shown: solid when transparency effects are off or the material is unsupported.</summary>
    public WindowMaterial EffectiveMaterial => effectiveMaterial;

    /// <summary>True when the backdrop shows through (the shell should leave its root background transparent).</summary>
    public bool IsBackdropTransparent => effectiveMaterial != WindowMaterial.Solid;

    /// <summary>Raised after <see cref="EffectiveMaterial"/> changed.</summary>
    public event Action? MaterialChanged;

    private void ApplyMaterial()
    {
        var effective = material;
        if (!uiSettings.AdvancedEffectsEnabled)
        {
            // Settings › Personalisation › Colours › Transparency effects is off.
            effective = WindowMaterial.Solid;
        }
        if (effective is WindowMaterial.Mica or WindowMaterial.MicaAlt && !MicaController.IsSupported())
        {
            effective = WindowMaterial.Acrylic; // Windows 10
        }
        if (effective == WindowMaterial.Acrylic && !DesktopAcrylicController.IsSupported())
        {
            effective = WindowMaterial.Solid;
        }

        // Battery saver is handled by the system: Mica and Acrylic turn solid by themselves.
        SystemBackdrop = effective switch
        {
            WindowMaterial.Mica => new MicaBackdrop { Kind = MicaKind.Base },
            WindowMaterial.MicaAlt => new MicaBackdrop { Kind = MicaKind.BaseAlt },
            WindowMaterial.Acrylic => new DesktopAcrylicBackdrop(),
            _ => null,
        };
        root.Background = effective == WindowMaterial.Solid
            ? Ui.Brush("SolidBackgroundFillColorBaseBrush")
            : new SolidColorBrush(Colors.Transparent);
        bool changed = effective != effectiveMaterial;
        effectiveMaterial = effective;
        if (changed) MaterialChanged?.Invoke();
    }

    // ---- Keyboard ----

    /// <summary>
    /// Mosaic's shortcuts, from Mosaic's own controls and (forwarded by WebView2) from Chromium
    /// pages. Gecko reports its own through the agent.
    /// </summary>
    private void Root_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.KeyStatus.WasKeyDown) return; // auto-repeat
        static bool Down(VirtualKey key) =>
            InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows)) return;

        var modifiers = ShortcutModifiers.None;
        if (Down(VirtualKey.Control)) modifiers |= ShortcutModifiers.Control;
        if (Down(VirtualKey.Shift)) modifiers |= ShortcutModifiers.Shift;
        if (Down(VirtualKey.Menu)) modifiers |= ShortcutModifiers.Alt;

        if (KeyHandler is { } handler)
        {
            if (handler(e.Key, modifiers)) e.Handled = true;
            return;
        }
        if (Shortcuts.Match((int)e.Key, modifiers) is not { } command) return;
        // Handled: neither a text box nor Chromium acts on the key as well.
        e.Handled = true;
        // Not from inside the handler: WebView2 waits while a forwarded key is being handled.
        DispatcherQueue.TryEnqueue(() => model.Execute(command));
    }
}
