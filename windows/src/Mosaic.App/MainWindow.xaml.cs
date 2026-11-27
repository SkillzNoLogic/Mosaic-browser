using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Mosaic.App.Chromium;
using Mosaic.App.Hosting;
using Mosaic.App.Views;
using Mosaic.Core.Browser;
using Mosaic.Core.Gecko;

namespace Mosaic.App;

/// <summary>
/// Mosaic's window. It shows <see cref="BrowserModel"/> and forwards what the user does to it;
/// the only state of its own is which native windows are attached where.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>Distance between the notice and the bottom of the content area.</summary>
    private const double NoticeBottomMargin = 14;

    private readonly BrowserModel model;
    private ExtensionsWindow? extensionsWindow;
    private nint attachedGeckoWindow;
    private bool isShutDown;

    /// <summary>
    /// The default size (set in XAML) is bigger than small screens: on a 1366×768 laptop, or a
    /// 1024×768 virtual machine, a centred 1240×800 window would hang off the top and sides.
    /// Shrinks the starting size to the primary work area, keeping a small margin.
    /// </summary>
    private void FitToWorkArea()
    {
        const double margin = 24;
        var area = SystemParameters.WorkArea;
        Width = Math.Max(MinWidth, Math.Min(Width, area.Width - 2 * margin));
        Height = Math.Max(MinHeight, Math.Min(Height, area.Height - 2 * margin));
    }

    internal MainWindow(BrowserModel model, WebView2Engine chromium)
    {
        this.model = model;
        InitializeComponent();
        FitToWorkArea();
        DataContext = model;

        chromium.ViewHost = ChromiumLayer;
        NoticePopup.CustomPopupPlacementCallback = PlaceNotice;

        model.PropertyChanged += Model_PropertyChanged;
        model.AddressFocusRequested += FocusAddressBox;
        model.WindowActivationRequested += BringToFront;
        model.GeckoFocusRequested += GeckoHost.FocusPage;

        model.Gecko.StatusChanged += SyncGeckoHost;
        model.Gecko.SnapshotReceived += Gecko_SnapshotReceived;
        GeckoHost.PageActivated += GeckoHost_PageActivated;

        Loaded += (_, _) => FocusAddressBox();
        LocationChanged += (_, _) => RepositionNotice();
        SizeChanged += (_, _) => RepositionNotice();
    }

    /// <summary>How Gecko's window is attached; set before Gecko first runs.</summary>
    internal GeckoHostMode GeckoHostMode
    {
        get => GeckoHost.Mode;
        set => GeckoHost.Mode = value;
    }

    // ---- Lifetime ----

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!e.Cancel) ShutDownBrowser();
    }

    /// <summary>
    /// Lets go of both engines. Runs once, while the window still exists: Gecko's window has to be
    /// detached before Mosaic's is destroyed, or it would be destroyed along with it.
    /// </summary>
    internal void ShutDownBrowser()
    {
        if (isShutDown) return;
        isShutDown = true;

        extensionsWindow?.Close();
        model.Gecko.StatusChanged -= SyncGeckoHost;
        model.Gecko.SnapshotReceived -= Gecko_SnapshotReceived;
        GeckoHost.PageActivated -= GeckoHost_PageActivated;
        DetachGecko();
        model.Shutdown();
    }

    // ---- Gecko's window ----

    /// <summary>
    /// Keeps the host element in step with the engine: attached to Gecko's window while Gecko
    /// runs, detached otherwise. Whether the page shows is not decided here; that is the host's
    /// Visibility, bound to <see cref="BrowserModel.ShowsGeckoPage"/>.
    /// </summary>
    private void SyncGeckoHost()
    {
        var gecko = model.Gecko;
        if (gecko.Status != GeckoStatus.Running)
        {
            DetachGecko();
            return;
        }

        var window = gecko.WindowHandle;
        if (window == 0 || gecko.ProcessId is not { } processId) return;
        // A restarted Gecko has a new window even if the status never visibly left "running".
        if (GeckoHost.IsAttached && window == attachedGeckoWindow) return;

        attachedGeckoWindow = GeckoHost.Attach(window, processId) ? window : 0;
        model.IsGeckoAttached = GeckoHost.IsAttached;
    }

    private void DetachGecko()
    {
        GeckoHost.Detach();
        attachedGeckoWindow = 0;
        model.IsGeckoAttached = false;
    }

    private void Gecko_SnapshotReceived(GeckoSnapshot snapshot)
    {
        // Normally a no-op. It retries an attach that could not happen when Gecko reported
        // itself running (no window handle yet, or the window was not ready to be adopted).
        if (!GeckoHost.IsAttached) SyncGeckoHost();
    }

    private void GeckoHost_PageActivated(object? sender, EventArgs e)
    {
        // The user is in the page now; whatever was half-typed in the address field is over.
        model.EndAddressEditing();
    }

    // ---- Model requests ----

    private void Model_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(BrowserModel.ShowExtensions):
                SyncExtensionsWindow();
                break;
            case nameof(BrowserModel.SelectedTab):
                // After layout, so a tab that was only just added has its container.
                Dispatcher.InvokeAsync(BringSelectedTabIntoView, DispatcherPriority.Loaded);
                break;
        }
    }

    private void FocusAddressBox()
    {
        // In a scripted run Mosaic's window is normally in the background, and taking focus there
        // is the first step towards taking it from the person using the PC. If they brought the
        // window forward themselves, it behaves as usual.
        if (!model.AllowsFocusChanges && !IsActive) return;
        AddressBox.Focus();
        AddressBox.SelectAll();
    }

    private void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void BringSelectedTabIntoView()
    {
        if (model.SelectedTab is { } tab && TabList.ItemContainerGenerator.ContainerFromItem(tab) is FrameworkElement chip)
        {
            chip.BringIntoView();
        }
    }

    private void SyncExtensionsWindow()
    {
        if (!model.ShowExtensions)
        {
            extensionsWindow?.Close();
            return;
        }
        if (extensionsWindow is not null)
        {
            if (model.AllowsFocusChanges) extensionsWindow.Activate();
            return;
        }

        // An owned window rather than a modal dialog: it floats above the page (as it must, being
        // more than a child window can draw over) and Mosaic stays usable behind it.
        extensionsWindow = new ExtensionsWindow(model) { Owner = this, ShowActivated = model.AllowsFocusChanges };
        extensionsWindow.Closed += (_, _) =>
        {
            extensionsWindow = null;
            model.ShowExtensions = false;
        };
        extensionsWindow.Show();
    }

    // ---- Notice ----

    private CustomPopupPlacement[] PlaceNotice(Size popupSize, Size targetSize, Point offset) =>
    [
        new CustomPopupPlacement(
            new Point((targetSize.Width - popupSize.Width) / 2, targetSize.Height - popupSize.Height - NoticeBottomMargin),
            PopupPrimaryAxis.Horizontal),
    ];

    /// <summary>A popup is positioned once, when it opens; nudge it so it follows the window.</summary>
    private void RepositionNotice()
    {
        if (!NoticePopup.IsOpen) return;
        NoticePopup.HorizontalOffset += 1;
        NoticePopup.HorizontalOffset -= 1;
    }

    // ---- Keyboard ----

    /// <summary>
    /// Mosaic's shortcuts. This one handler sees key presses from both places they come from:
    /// WPF's own input while one of Mosaic's controls has focus, and, while a Chromium page has
    /// focus, the accelerator keys WebView2 forwards as this same event before acting on them
    /// itself. (Gecko reports its own through the agent.)
    /// </summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.IsRepeat) return;
        // With Alt held, WPF reports the key as a "system" key.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var pressed = Keyboard.Modifiers;
        if (pressed.HasFlag(ModifierKeys.Windows)) return;

        var modifiers = ShortcutModifiers.None;
        if (pressed.HasFlag(ModifierKeys.Control)) modifiers |= ShortcutModifiers.Control;
        if (pressed.HasFlag(ModifierKeys.Shift)) modifiers |= ShortcutModifiers.Shift;
        if (pressed.HasFlag(ModifierKeys.Alt)) modifiers |= ShortcutModifiers.Alt;

        if (Shortcuts.Match(KeyInterop.VirtualKeyFromKey(key), modifiers) is not { } command) return;

        // Handled: neither a text box nor Chromium gets to act on the key as well.
        e.Handled = true;
        // Not from inside the handler: WebView2 blocks its browser process while a forwarded key
        // is being handled, and its APIs may fail if called back during that time.
        Dispatcher.InvokeAsync(() => model.Execute(command));
    }

    // ---- Tab strip ----

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (TabOf(sender) is { } tab) model.Select(tab);
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (TabOf(sender) is { } tab) model.Close(tab);
    }

    private void NewTab_Click(object sender, RoutedEventArgs e) => model.NewTab();

    private void TabScroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // The strip only scrolls sideways, so that is what the wheel does over it.
        TabScroller.ScrollToHorizontalOffset(TabScroller.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    private static BrowserTab? TabOf(object sender) => (sender as FrameworkElement)?.DataContext as BrowserTab;

    // ---- Toolbar ----

    private void Back_Click(object sender, RoutedEventArgs e) => model.GoBack();

    private void Forward_Click(object sender, RoutedEventArgs e) => model.GoForward();

    private void ReloadOrStop_Click(object sender, RoutedEventArgs e) => model.ReloadOrStop();

    private void UseGecko_Click(object sender, RoutedEventArgs e) => model.SetEngine(EngineKind.Gecko);

    private void UseChromium_Click(object sender, RoutedEventArgs e) => model.SetEngine(EngineKind.Chromium);

    private void Extensions_Click(object sender, RoutedEventArgs e) => model.ShowExtensions = true;

    private async void AddExtension_Click(object sender, RoutedEventArgs e) => await model.AddWebStoreExtensionAsync();

    // ---- Address field ----

    private void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                var input = AddressBox.Text;
                // Leave the field first (which puts the current address back), then navigate:
                // the page, not the field, is where the user is headed.
                Keyboard.ClearFocus();
                model.Navigate(input);
                break;
            case Key.Escape:
                e.Handled = true;
                Keyboard.ClearFocus();
                model.FocusContent();
                break;
        }
    }

    private void AddressBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // The first click selects the whole address, ready to be replaced; later clicks place the caret.
        if (AddressBox.IsKeyboardFocusWithin) return;
        e.Handled = true;
        FocusAddressBox();
    }

    private void AddressBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => model.BeginAddressEditing();

    private void AddressBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Switching to another app and back is not leaving the field: WPF restores the focus,
        // and what was typed should still be there.
        if (IsActive) model.EndAddressEditing();
    }
}
