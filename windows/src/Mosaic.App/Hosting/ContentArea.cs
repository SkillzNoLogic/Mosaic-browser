using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Mosaic.App.Chromium;
using Mosaic.App.Features;
using Mosaic.Core.Browser;
using Mosaic.Core.Gecko;
using Windows.Foundation;
using Windows.Graphics;
using static Mosaic.App.Hosting.NativeMethods;

namespace Mosaic.App.Hosting;

/// <summary>
/// The page area: where the selected tab's page shows, whichever engine renders it. The shell
/// places this one element (<see cref="MainWindow.ContentArea"/>) in its layout; everything
/// else about the engines is handled here.
/// </summary>
/// <remarks>
/// <para>Layers, bottom to top: every Chromium tab's WebView2 (the model keeps all but the
/// selected one hidden), the start page, the engine message, and <see cref="OverlayPage"/>.
/// Gecko's page is not a XAML element: it is a Win32 child window that
/// <see cref="GeckoWindowHost"/> keeps glued to this element's bounds, in physical pixels, on
/// every layout pass, DPI change, move and resize.</para>
/// <para>A Win32 child window draws over all XAML in the same place, so Gecko's page is hidden
/// whenever Mosaic draws over the page: while <see cref="FeatureMounts.IsPageCovered"/>, while
/// an <see cref="OverlayPage"/> (Settings, a <c>mosaic://</c> page) or <see cref="IsPageHidden"/>
/// is set, while this element is not in the window or not visible, and of course while the
/// selected tab is not a live Gecko page (<see cref="BrowserModel.ShowsGeckoPage"/>).</para>
/// <para>Moving this element to another parent (a Mosaic switch) is fine: the WebView2 controls
/// survive being re-parented, and Gecko's window follows on the next layout pass. Remove it from
/// the old parent before adding it to the new one.</para>
/// </remarks>
public sealed class ContentArea : Grid
{
    private const string BridgeClassName = "Microsoft.UI.Content.DesktopChildSiteBridge";

    private readonly BrowserModel model;
    private readonly GeckoWindowHost gecko;
    private readonly nint topLevel;
    private readonly Grid overlayLayer = new();
    private readonly StartPage startPage;
    private readonly EngineMessageView messageView;
    private UIElement? overlayPage;
    private bool isPageHidden;
    private nint attachedGeckoWindow;
    private nint bridge;
    private UIElement? pointerRoot;
    private bool isShutDown;

    internal ContentArea(BrowserModel model, WebView2Engine chromium, GeckoWindowHost gecko, nint topLevel)
    {
        this.model = model;
        this.gecko = gecko;
        this.topLevel = topLevel;
        // Hit-testable everywhere (WinUI only hit-tests elements with a background), and so
        // the area is not see-through to the window's Mica while nothing else covers it.
        Background = Ui.Brush("LayerFillColorDefaultBrush");

        // Chromium: every Chromium tab's WebView2 lives here; filled by WebView2Engine.
        chromium.ViewHost = ChromiumLayer;
        Children.Add(ChromiumLayer);

        // Mosaic's own views. Each is visible only while no page is.
        startPage = new StartPage(model);
        messageView = new EngineMessageView(model);
        Children.Add(startPage);
        Children.Add(messageView);
        Children.Add(overlayLayer);

        gecko.FocusXaml = FocusXaml;
        gecko.PageActivated += Gecko_PageActivated;
        model.PropertyChanged += Model_PropertyChanged;
        model.GeckoFocusRequested += gecko.FocusPage;
        model.Gecko.StatusChanged += SyncGeckoHost;
        model.Gecko.SnapshotReceived += Gecko_SnapshotReceived;
        FeatureMounts.PageCoversChanged += PageCoversChanged;
        FocusManager.GotFocus += FocusManager_GotFocus;

        Loaded += (_, _) =>
        {
            if (XamlRoot is { } root)
            {
                root.Changed += (_, _) => UpdatePlacement();
                // A press anywhere on Mosaic's own UI closes Gecko's open menus.
                pointerRoot = root.Content;
                pointerRoot?.AddHandler(PointerPressedEvent, new PointerEventHandler(Root_PointerPressed), handledEventsToo: true);
            }
            UpdatePlacement();
        };
        Unloaded += (_, _) =>
        {
            pointerRoot?.RemoveHandler(PointerPressedEvent, new PointerEventHandler(Root_PointerPressed));
            pointerRoot = null;
            UpdatePlacement();
        };
        LayoutUpdated += (_, _) => UpdatePlacement();
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => UpdatePlacement());

        UpdateViews();
    }

    /// <summary>The Chromium tabs' WebView2 controls.</summary>
    public Grid ChromiumLayer { get; } = new();

    /// <summary>The Gecko window host (for diagnostics and focus).</summary>
    public GeckoWindowHost Gecko => gecko;

    /// <summary>
    /// One of Mosaic's own pages shown in place of the page (Settings, a <c>mosaic://</c> page);
    /// null shows the page. The page stays loaded underneath.
    /// </summary>
    public UIElement? OverlayPage
    {
        get => overlayPage;
        set
        {
            if (ReferenceEquals(value, overlayPage)) return;
            overlayPage = value;
            overlayLayer.Children.Clear();
            if (value is not null) overlayLayer.Children.Add(value);
            UpdateViews();
        }
    }

    /// <summary>Hides the page (both engines) without an overlay, for a shell surface that needs the whole area.</summary>
    public bool IsPageHidden
    {
        get => isPageHidden;
        set
        {
            if (value == isPageHidden) return;
            isPageHidden = value;
            UpdateViews();
        }
    }

    /// <summary>True while Gecko's page window is showing in this area.</summary>
    public bool IsGeckoShown => gecko.Placement.Visible;

    /// <summary>
    /// The area's bounds in physical pixels relative to the top-level window's client area, as
    /// last given to the Gecko host.
    /// </summary>
    public RectInt32 PixelBounds => gecko.Placement.Bounds;

    /// <summary>Raised after the Gecko page was shown or hidden.</summary>
    public event Action? GeckoShownChanged;

    // ---- Lifetime ----

    /// <summary>Lets go of Gecko's window. Runs once, while Mosaic's window still exists.</summary>
    internal void ShutDown()
    {
        if (isShutDown) return;
        isShutDown = true;
        model.PropertyChanged -= Model_PropertyChanged;
        model.GeckoFocusRequested -= gecko.FocusPage;
        model.Gecko.StatusChanged -= SyncGeckoHost;
        model.Gecko.SnapshotReceived -= Gecko_SnapshotReceived;
        FeatureMounts.PageCoversChanged -= PageCoversChanged;
        FocusManager.GotFocus -= FocusManager_GotFocus;
        gecko.PageActivated -= Gecko_PageActivated;
        DetachGecko();
        gecko.Dispose();
    }

    // ---- What shows ----

    private void Model_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(BrowserModel.ShowsGeckoPage):
            case nameof(BrowserModel.ShowsStartPage):
            case nameof(BrowserModel.ShowsMessage):
            case nameof(BrowserModel.Content):
            case null:
            case "":
                UpdateViews();
                break;
        }
    }

    private void PageCoversChanged()
    {
        if (DispatcherQueue.HasThreadAccess) UpdatePlacement();
        else DispatcherQueue.TryEnqueue(UpdatePlacement);
    }

    private void UpdateViews()
    {
        bool own = overlayPage is not null || isPageHidden;
        startPage.Visibility = Ui.Visible(!own && model.ShowsStartPage);
        messageView.Visibility = Ui.Visible(!own && model.ShowsMessage);
        // WebView2 is drawn by XAML, so an overlay would cover it; collapsing it also stops it
        // rendering underneath.
        ChromiumLayer.Visibility = Ui.Visible(!own);
        overlayLayer.Visibility = Ui.Visible(overlayPage is not null);
        UpdatePlacement();
    }

    /// <summary>Whether Gecko's page should be on screen right now.</summary>
    private bool ShouldShowGecko =>
        !isShutDown && IsLoaded && XamlRoot is not null && Visibility == Visibility.Visible
        && model.ShowsGeckoPage && overlayPage is null && !isPageHidden && !FeatureMounts.IsPageCovered;

    /// <summary>Puts Gecko's window over this element. Cheap when nothing changed.</summary>
    private void UpdatePlacement()
    {
        if (isShutDown) return;
        bool wasShown = gecko.Placement.Visible;
        if (XamlRoot is not { } root || !IsLoaded || ActualWidth <= 0 || ActualHeight <= 0)
        {
            gecko.Place(gecko.Placement.Bounds, false);
        }
        else
        {
            double scale = root.RasterizationScale;
            Rect bounds;
            try
            {
                bounds = TransformToVisual(null).TransformBounds(new Rect(0, 0, ActualWidth, ActualHeight));
            }
            catch (ArgumentException)
            {
                // Not in the visual tree after all (being moved between parents).
                gecko.Place(gecko.Placement.Bounds, false);
                return;
            }
            var origin = BridgeOrigin();
            int left = (int)Math.Round(bounds.X * scale) + origin.X;
            int top = (int)Math.Round(bounds.Y * scale) + origin.Y;
            int right = (int)Math.Round((bounds.X + bounds.Width) * scale) + origin.X;
            int bottom = (int)Math.Round((bounds.Y + bounds.Height) * scale) + origin.Y;
            gecko.Place(new RectInt32(left, top, right - left, bottom - top), ShouldShowGecko);
        }
        if (wasShown != gecko.Placement.Visible) GeckoShownChanged?.Invoke();
    }

    /// <summary>
    /// Where WinUI's content window sits in the top-level window's client area: XAML
    /// coordinates are relative to it. (0, 0) in practice; measured to be sure.
    /// </summary>
    private POINT BridgeOrigin()
    {
        if (bridge == 0 || !IsWindow(bridge)) bridge = FindWindowExW(topLevel, 0, BridgeClassName, null);
        var origin = new POINT();
        if (bridge != 0 && GetWindowRect(bridge, out RECT rect))
        {
            origin.X = rect.Left;
            origin.Y = rect.Top;
            ScreenToClient(topLevel, ref origin);
        }
        return origin;
    }

    // ---- Gecko's window ----

    /// <summary>
    /// Keeps the host in step with the engine: attached to Gecko's window while Gecko runs,
    /// detached otherwise. Whether the page shows is decided by <see cref="UpdatePlacement"/>.
    /// </summary>
    private void SyncGeckoHost()
    {
        var engine = model.Gecko;
        if (engine.Status != GeckoStatus.Running)
        {
            DetachGecko();
            return;
        }

        var window = engine.WindowHandle;
        if (window == 0 || engine.ProcessId is not { } processId) return;
        // A restarted Gecko has a new window even if the status never visibly left "running".
        if (gecko.IsAttached && window == attachedGeckoWindow) return;

        attachedGeckoWindow = gecko.Attach(window, processId) ? window : 0;
        model.IsGeckoAttached = gecko.IsAttached;
        Trace.TraceInformation($"[ContentArea] Gecko window 0x{window:X} attach: {gecko.IsAttached}, placement {gecko.Placement}");
    }

    private void DetachGecko()
    {
        gecko.Detach();
        attachedGeckoWindow = 0;
        model.IsGeckoAttached = false;
    }

    private void Gecko_SnapshotReceived(GeckoSnapshot snapshot)
    {
        // Normally a no-op. It retries an attach that could not happen when Gecko reported
        // itself running (no window handle yet, or the window was not ready to be adopted), and
        // reports an attach that completed late (deferred until the first placement).
        if (!gecko.IsAttached) SyncGeckoHost();
        else if (!model.IsGeckoAttached) model.IsGeckoAttached = true;
    }

    private void Gecko_PageActivated(object? sender, EventArgs e)
    {
        // The user is in the page now; whatever was half-typed in the address field is over, and
        // the blocker's popover goes too.
        model.EndAddressEditing();
        model.ShowBlocker = false;
    }

    // ---- Focus ----

    private void FocusManager_GotFocus(object? sender, FocusManagerGotFocusEventArgs e) => gecko.OnXamlGotFocus();

    private void Root_PointerPressed(object sender, PointerRoutedEventArgs e) => gecko.OnXamlPointerPressed();

    private void FocusXaml()
    {
        if (XamlRoot?.Content is Control control) control.Focus(FocusState.Programmatic);
        else if (FocusManager.FindFirstFocusableElement(XamlRoot?.Content) is Control first) first.Focus(FocusState.Programmatic);
    }
}
