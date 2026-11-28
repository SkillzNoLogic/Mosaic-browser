using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Windows.Graphics;
using static Mosaic.App.Hosting.NativeMethods;

namespace Mosaic.App.Hosting;

/// <summary>How the Gecko window is attached to Mosaic's window.</summary>
public enum GeckoHostMode
{
    /// <summary>Gecko's window becomes a true child window (SetParent). Default.</summary>
    Child,
    /// <summary>Gecko's window stays top-level but is owned by Mosaic's window and kept
    /// positioned over the host element. Fallback if child embedding misbehaves.</summary>
    Owned,
}

/// <summary>
/// Shows the Gecko engine's chromeless browser window over a rectangle of Mosaic's window. The
/// <see cref="ContentArea"/> owns one and tells it where the page goes (<see cref="Place"/>);
/// it is the Windows counterpart of the transparent "hole" on macOS.
/// </summary>
/// <remarks>
/// Contract:
/// <list type="bullet">
/// <item>All members are called on the UI thread.</item>
/// <item><see cref="Attach"/> adopts the given Gecko window; <see cref="Detach"/> lets go of it
/// (Gecko stopped or is about to). Both are safe to call repeatedly.</item>
/// <item>While attached, the Gecko window exactly fills the rectangle last given to
/// <see cref="Place"/> and follows it when the Mosaic window moves, resizes, changes DPI,
/// minimises or restores.</item>
/// <item><see cref="Place"/>'s <c>visible</c> decides whether the page shows; no other call is
/// needed. A Win32 child window draws over all WinUI content in the same place, so the page
/// must be hidden whenever Mosaic draws over it.</item>
/// <item>The Gecko process must never keep Mosaic's window from closing, and closing Mosaic
/// must not destroy Gecko's window out from under it: detach first (<see cref="Dispose"/> does).</item>
/// </list>
/// <para><b>How it is built.</b> The constructor creates a plain child window of Mosaic's
/// top-level window, the <i>container</i>, above WinUI's content (which is itself a child
/// window, <c>DesktopChildSiteBridge</c>). <see cref="Place"/> positions, sizes, shows and hides
/// the container in physical pixels, so the container is the single source of truth for where
/// the page belongs. Gecko's window, which belongs to another process, is then bound to the
/// container in one of two ways:</para>
/// <list type="bullet">
/// <item><see cref="GeckoHostMode.Child"/>: it becomes a <c>WS_CHILD</c> of the container
/// (<c>SetParent</c>). Windows then clips, moves, stacks and hides it with the container for
/// free; this class only resizes it to the container's client area.</item>
/// <item><see cref="GeckoHostMode.Owned"/>: it stays a top-level window, owned by Mosaic's
/// window (always above it, no taskbar button, minimised with it), and this class keeps it on
/// the container's screen rectangle.</item>
/// </list>
/// <para><b>What a cross-process window relationship implies.</b> Windows permits a parent/child
/// or owner/owned relationship between windows of different processes, but it implicitly
/// attaches the two UI threads' input queues (Raymond Chen, "Is it legal to have a
/// cross-process parent/child or owner/owned window relationship?", The Old New Thing, 12 April
/// 2013). That applies to <i>both</i> modes and has three consequences this class is written
/// around:</para>
/// <list type="number">
/// <item>Focus, activation and capture become shared state. <c>GetFocus</c> here can return
/// Gecko's window, and either thread can move the focus.</item>
/// <item>Input is serialised across the two threads. If Gecko's UI thread stops pumping
/// messages, Mosaic stops receiving keyboard and mouse input until it resumes, although
/// Mosaic's timers and posted messages keep running. This class cannot prevent that; it can
/// only avoid making it worse (next point). Detecting a hung engine and restarting it is the
/// engine's job.</item>
/// <item>Any call that <i>sends</i> a message to Gecko's window blocks Mosaic's UI thread until
/// Gecko answers. So the steady-state paths only ever post: <c>PostMessage</c>,
/// <c>ShowWindowAsync</c>, and <c>SetWindowPos</c> with <c>SWP_ASYNCWINDOWPOS</c>. The calls
/// that have no asynchronous form (<c>SetParent</c>, <c>SetWindowLongPtr</c>) are confined to
/// attaching and releasing, and are made only right after the window has answered a
/// <c>WM_NULL</c> probe with a timeout. A hang in the few milliseconds between probe and call
/// is the remaining exposure.</item>
/// </list>
/// <para><b>Assumptions about Gecko</b> are spelled out where they are used; they were checked
/// against <c>widget/windows/nsWindow.cpp</c> of Firefox ESR 153 and none of it has been run.</para>
/// <para>Failures are traced (<see cref="Trace"/>, prefix <c>[GeckoWindowHost]</c>) and degrade
/// to "the Gecko page is not shown".</para>
/// <para>Ported from the v0.1 WPF host (an <c>HwndHost</c>); the Win32 side is unchanged, the
/// WPF integration (layout, keyboard focus, the window's message hook) is replaced by
/// <see cref="Place"/>, <see cref="OnXamlGotFocus"/>, <see cref="OnXamlPointerPressed"/> and
/// <see cref="WindowMessages"/>.</para>
/// </remarks>
public sealed class GeckoWindowHost : IDisposable
{
    private const string ContainerClassName = "MosaicGeckoHost";

    /// <summary>How long Gecko's window may take to answer before an attach is refused.</summary>
    private const uint AttachProbeTimeoutMs = 2000;

    /// <summary>How long Gecko's window may take to answer before a release gives up on it.</summary>
    private const uint ReleaseProbeTimeoutMs = 1500;

    /// <summary>Owned mode: how often the same rectangle is re-applied when Gecko moves away from it.</summary>
    private const int MaxOwnedCorrections = 3;

    /// <summary>What Gecko's window looked like before Mosaic adopted it, to put it back.</summary>
    private readonly record struct SavedWindowState(nint Style, nint ExStyle, nint Owner, RECT Bounds);

    private static readonly Dictionary<nint, GeckoWindowHost> s_containers = [];

    private readonly nint _topLevel;
    private readonly DispatcherQueue _dispatcher;
    private readonly WindowMessages? _messages;
    private readonly WinEventProc _winEventProc; // field: must outlive the native hooks
    private readonly List<nint> _winEventHooks = [];

    private nint _container;
    private (nint Window, int ProcessId)? _pending;
    private RectInt32 _placement;
    private bool _placementVisible;
    private bool _placed;
    /// <summary>The page had the keyboard; XAML focus changes while restoring it are ignored until then.</summary>
    private long _restoringUntil;

    private nint _gecko;
    private int _geckoProcessId;
    private GeckoHostMode _attachedMode;
    private SavedWindowState _saved;

    /// <summary>Child mode: Gecko has been told its window is the active one.</summary>
    private bool _pageActive;
    /// <summary>Child mode: the page had the keyboard when Mosaic's window was deactivated.</summary>
    private bool _restorePageOnActivate;
    /// <summary>Child mode: last client size given to Gecko.</summary>
    private (int Width, int Height) _childSize;
    /// <summary>Owned mode: last screen rectangle and visibility requested of Gecko.</summary>
    private RECT? _ownedTarget;
    private bool _ownedShown;
    private int _ownedCorrections;

    private bool _activationNotificationQueued;
    private bool _dpiNoteTraced;

    /// <param name="topLevel">Mosaic's top-level window (the WinUI window's HWND).</param>
    /// <param name="messages">That window's messages, for activation and (owned mode) moves.</param>
    /// <param name="dispatcher">The UI thread's queue.</param>
    public GeckoWindowHost(nint topLevel, WindowMessages? messages, DispatcherQueue dispatcher)
    {
        _topLevel = topLevel;
        _dispatcher = dispatcher;
        _messages = messages;
        _winEventProc = OnWinEvent;
        try
        {
            _container = CreateContainer(topLevel);
            lock (s_containers) s_containers[_container] = this;
        }
        catch (Win32Exception error)
        {
            // Mosaic still runs; the Gecko page cannot be shown.
            Error(error.Message);
        }
        _messages?.Add(OnTopLevelMessage);
    }

    /// <summary>The container window (for diagnostics); 0 if it could not be created.</summary>
    public nint ContainerWindow => _container;

    /// <summary>Gecko's window while attached, else 0.</summary>
    public nint GeckoWindow => _gecko;

    /// <summary>Where the page is placed (client pixels of the top-level window) and whether it shows.</summary>
    public (RectInt32 Bounds, bool Visible) Placement => (_placement, _placementVisible);

    /// <summary>The mode of the current attachment (which may differ from <see cref="Mode"/> after a fallback).</summary>
    public GeckoHostMode AttachedMode => _attachedMode;

    /// <summary>
    /// Asks the host to give the keyboard back to Mosaic's own (XAML) UI, after Gecko's window
    /// went away while it had the focus. Set by <see cref="ContentArea"/>.
    /// </summary>
    public Action? FocusXaml { get; set; }

    /// <summary>Set before the first <see cref="Attach"/>. Default <see cref="GeckoHostMode.Child"/>;
    /// the command-line switch <c>--gecko-host=owned</c> selects the fallback.</summary>
    /// <remarks>The switch is read here, for the initial value, so it works whether or not the
    /// app also assigns the property. A change takes effect at the next <see cref="Attach"/>.
    /// Child mode falls back to owned by itself for one attachment when it cannot be used
    /// safely (see <see cref="Attach"/>).</remarks>
    public GeckoHostMode Mode { get; set; } = ModeFromCommandLine();

    public bool IsAttached => _gecko != 0;

    /// <summary>Adopts Gecko's browser window. Returns false if the handle is not a live window.</summary>
    /// <remarks>
    /// Also returns false, leaving Gecko's window hidden, if the window does not belong to
    /// <paramref name="geckoProcessId"/>, does not answer within two seconds, or cannot be bound
    /// to Mosaic's window in either mode. If the page area has not been placed yet, the
    /// attachment is remembered and completed at the first <see cref="Place"/>; the call returns
    /// true and <see cref="IsAttached"/> turns true at that point. Never activates Mosaic or Gecko.
    /// </remarks>
    public bool Attach(nint geckoWindow, int geckoProcessId)
    {
        VerifyAccess();
        if (!TryIdentify(geckoWindow, geckoProcessId, out int processId))
        {
            return false;
        }
        if (_gecko == geckoWindow && _geckoProcessId == processId)
        {
            return true;
        }
        if (_pending is { } pending && pending.Window == geckoWindow && pending.ProcessId == processId)
        {
            return true;
        }

        Detach(); // a different window than before: let go of the old one first
        if (_container == 0)
        {
            Warn($"Attach of 0x{geckoWindow:X} refused: there is no container window.");
            ShowWindowAsync(geckoWindow, SW_HIDE);
            return false;
        }
        if (!_placed)
        {
            // Not placed yet: the content area has not been laid out. Adopted on the first Place.
            _pending = (geckoWindow, processId);
            Info($"Attach of 0x{geckoWindow:X} deferred until the page area has been laid out.");
            return true;
        }
        return Adopt(geckoWindow, processId);
    }

    /// <summary>
    /// Lets go of Gecko's window, leaving it a hidden, ordinary top-level window again.
    /// </summary>
    /// <remarks>
    /// Putting the window back needs calls that wait for Gecko. If Gecko's window does not answer
    /// within 1.5 seconds, this method does not wait further: it terminates the Gecko process
    /// (the one given to <see cref="Attach"/>). That is the only way to guarantee that a hung
    /// engine cannot keep Mosaic's window from closing, because destroying a window that still
    /// has a live window of a hung thread as child or owned window waits for that thread.
    /// </remarks>
    public void Detach()
    {
        VerifyAccess();
        _pending = null;
        if (_gecko != 0)
        {
            Release();
        }
    }

    /// <summary>Gives keyboard focus to the page.</summary>
    /// <remarks>
    /// Does nothing while not attached or while this element is not visible. It never brings
    /// Mosaic to the foreground: if another application is in front, child mode only records the
    /// focus inside Mosaic's window and owned mode does nothing. Call the agent's
    /// <c>focusContent()</c> as well if a particular element inside Gecko should get the focus;
    /// this method is the Win32 half.
    /// </remarks>
    public void FocusPage()
    {
        VerifyAccess();
        if (_gecko == 0 || _container == 0 || !IsWindowVisible(_container))
        {
            return;
        }
        if (_attachedMode == GeckoHostMode.Child)
        {
            ActivatePage();
            return;
        }

        // Owned mode: Gecko's window is a top-level window, so focusing it means making it the
        // foreground window. Only do that when Mosaic is what the user is working in already.
        nint foreground = GetForegroundWindow();
        if (foreground == _gecko)
        {
            return;
        }
        if (foreground != 0 && foreground == TopLevelWindow)
        {
            if (!SetForegroundWindow(_gecko))
            {
                Warn("SetForegroundWindow on the Gecko window was refused.");
            }
        }
    }

    /// <summary>Raised when the user clicks into / otherwise activates the Gecko page.</summary>
    /// <remarks>
    /// Raised asynchronously on the UI thread, at most once per burst. Child mode: every mouse
    /// button or pointer press on the page, and tabbing into it. Owned mode: when Gecko's window
    /// becomes the foreground window (clicks inside an already active page are not observable).
    /// Not raised for <see cref="FocusPage"/>.
    /// </remarks>
    public event EventHandler? PageActivated;

    // =====================================================================================
    // Placement and lifetime
    // =====================================================================================

    /// <summary>
    /// Puts the page at <paramref name="bounds"/> (physical pixels, relative to the top-level
    /// window's client area) and shows or hides it. Cheap when nothing changed.
    /// </summary>
    public void Place(RectInt32 bounds, bool visible)
    {
        VerifyAccess();
        if (_container == 0) return;
        visible = visible && bounds.Width > 0 && bounds.Height > 0;
        bool firstPlacement = !_placed;
        // WinUI creates its content window after the container, and may re-create or re-order
        // it later; a sibling above the container would hide the page.
        bool buried = visible && GetWindow(_container, GW_HWNDPREV) != 0;
        if (_placed && bounds.Equals(_placement) && visible == _placementVisible && !buried) return;
        if (buried && _placed) Info("The container was below another window of Mosaic's; raising it.");
        _placed = true;
        _placement = bounds;
        _placementVisible = visible;

        // Our own window: synchronous, and it answers immediately. HWND_TOP keeps the container
        // above WinUI's content window, which is a sibling.
        uint flags = SWP_NOACTIVATE | SWP_NOOWNERZORDER | (visible ? SWP_SHOWWINDOW : SWP_HIDEWINDOW);
        if (!SetWindowPos(_container, HWND_TOP, bounds.X, bounds.Y, Math.Max(bounds.Width, 0), Math.Max(bounds.Height, 0), flags))
        {
            Warn($"Placing the container failed: {LastError()}");
        }
        // WM_WINDOWPOSCHANGED on the container has run SyncGecko already; this covers a call
        // that changed nothing Windows reports (and the first placement's pending attach).
        if (firstPlacement && _pending is not null)
        {
            _dispatcher.TryEnqueue(DispatcherQueuePriority.Normal, CompletePendingAttach);
        }
        SyncGecko();
    }

    /// <summary>
    /// Lets go of Gecko's window and destroys the container. Call while Mosaic's window still
    /// exists (as it closes); the container's WM_DESTROY covers the case where it does not.
    /// </summary>
    public void Dispose()
    {
        _messages?.Remove(OnTopLevelMessage);
        _pending = null;
        if (_gecko != 0)
        {
            try
            {
                Release();
            }
            catch (Exception error)
            {
                Error($"Releasing the Gecko window failed: {error}");
            }
        }
        nint container = _container;
        _container = 0;
        if (container != 0)
        {
            lock (s_containers) s_containers.Remove(container);
            if (IsWindow(container) && !DestroyWindow(container))
            {
                Warn($"DestroyWindow on the container failed: {LastError()}");
            }
        }
    }

    /// <summary>Messages of the container window.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint ContainerProc(nint hwnd, int msg, nint wParam, nint lParam)
    {
        GeckoWindowHost? host;
        lock (s_containers) s_containers.TryGetValue(hwnd, out host);
        // Nothing may escape into the native window procedure.
        try
        {
            host?.ContainerMessage(msg, wParam, lParam);
        }
        catch (Exception error)
        {
            Error($"Handling container message 0x{msg:X4} failed: {error}");
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void ContainerMessage(int msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_WINDOWPOSCHANGED:
                // Moved, resized, shown or hidden by Place. (Not WM_SHOWWINDOW: that arrives
                // before the visibility actually changes.)
                SyncGecko();
                break;

            case WM_SETFOCUS:
                OnContainerGotFocus();
                break;

            case WM_PARENTNOTIFY:
                OnParentNotify(wParam, lParam);
                break;

            case WM_DESTROY:
                // The container is being destroyed by something other than Dispose (Mosaic's
                // window is going away). WM_DESTROY reaches a window before its children are
                // destroyed, so Gecko's window can still be taken out here.
                _pending = null;
                if (_gecko != 0)
                {
                    Release();
                }
                lock (s_containers) s_containers.Remove(_container);
                _container = 0;
                break;
        }
    }

    /// <summary>Tabbing from Mosaic's controls into the page.</summary>
    /// <remarks>
    /// The way back is not symmetric: once the page has the focus, Tab is Gecko's, and Gecko has
    /// no notion of a host to tab out to. Ctrl+L (forwarded by the agent as <c>focusAddress</c>)
    /// or a click returns the focus to Mosaic's own controls.
    /// </remarks>
    public bool TabInto()
    {
        if (_gecko == 0 || _container == 0 || !IsWindowVisible(_container))
        {
            return false;
        }
        FocusPage();
        QueuePageActivated();
        return true;
    }

    /// <summary>True while the page has the keyboard focus.</summary>
    public bool HasFocusWithin =>
        _gecko != 0 && (GetFocus() is var focus && (focus == _gecko || (_container != 0 && IsChild(_container, focus))));

    /// <summary>
    /// Mosaic's window moved to a monitor with another DPI. Sizes stay right (the content area
    /// places the container in physical pixels and Gecko's window follows); the page's own scale
    /// may not, see the note below.
    /// </summary>
    private void OnDpiChanged(int newDpi)
    {
        if (_gecko != 0 && _attachedMode == GeckoHostMode.Child && !_dpiNoteTraced)
        {
            // Gecko computes its scale from the DPI of the root window (WinUtils::LogToPhysFactor,
            // which is Mosaic's window here) but only re-reads it on WM_DPICHANGED, and Windows
            // sends that message to top-level windows only; child windows get
            // WM_DPICHANGED_AFTERPARENT, which Gecko does not handle. Nothing can be posted to
            // make up for it (WM_DPICHANGED carries a pointer). Remedies outside this class: the
            // agent setting layout.css.devPixelsPerPx, or owned mode, where Gecko's window is
            // top-level and gets the message itself.
            _dpiNoteTraced = true;
            Warn($"DPI changed to {newDpi} while the Gecko window is a child window; the page may keep rendering at its previous scale.");
        }
        SyncGecko();
    }

    // =====================================================================================
    // Attaching
    // =====================================================================================

    private void CompletePendingAttach()
    {
        if (_pending is not { } pending || _container == 0)
        {
            return;
        }
        _pending = null;
        if (TryIdentify(pending.Window, pending.ProcessId, out int processId))
        {
            Adopt(pending.Window, processId);
        }
    }

    /// <summary>Checks that the handle is a live window of the stated process, and not ours.</summary>
    private static bool TryIdentify(nint window, int statedProcessId, out int processId)
    {
        processId = 0;
        if (window == 0 || !IsWindow(window))
        {
            Warn($"Attach refused: 0x{window:X} is not a live window.");
            return false;
        }
        if (GetWindowThreadProcessId(window, out uint owner) == 0)
        {
            Warn($"Attach refused: 0x{window:X} has no owning thread (it is being destroyed).");
            return false;
        }
        processId = (int)owner;
        if (processId == Environment.ProcessId)
        {
            Warn($"Attach refused: 0x{window:X} is one of Mosaic's own windows.");
            return false;
        }
        // Window handles are reused. The process ID the agent reports alongside the handle is
        // what makes a stale handle detectable.
        if (statedProcessId > 0 && statedProcessId != processId)
        {
            Warn($"Attach refused: 0x{window:X} belongs to process {processId}, not to Gecko's process {statedProcessId}.");
            return false;
        }
        return true;
    }

    private bool Adopt(nint window, int processId)
    {
        if (!Answers(window, AttachProbeTimeoutMs))
        {
            Warn($"Attach refused: Gecko's window 0x{window:X} did not answer within {AttachProbeTimeoutMs} ms.");
            ShowWindowAsync(window, SW_HIDE); // posted; "not shown" rather than a stray window
            return false;
        }

        GeckoHostMode mode = Mode;
        if (mode == GeckoHostMode.Child && !SharesDpiAwarenessContext(window))
        {
            // SetParent documentation: with a cross-process parent and child in different DPI
            // awareness modes Windows forcibly resets the DPI awareness of the child's whole
            // process. Firefox selects Per-Monitor V2 at start-up (mozglue/misc/
            // WindowsDpiInitialization.cpp), the same as Mosaic's manifest, so this is not
            // expected; if it ever differs (a compatibility override on firefox.exe, a fork),
            // do not find out what a forced reset does to a running browser.
            Warn("Gecko's window does not use the same DPI awareness context as Mosaic; using owned mode for this attachment.");
            mode = GeckoHostMode.Owned;
        }

        var saved = new SavedWindowState(
            GetWindowLongPtrW(window, GWL_STYLE),
            GetWindowLongPtrW(window, GWL_EXSTYLE),
            GetWindow(window, GW_OWNER),
            GetWindowRect(window, out RECT bounds) ? bounds : default);

        bool adopted;
        if (mode == GeckoHostMode.Child)
        {
            adopted = AdoptAsChild(window, saved);
            if (!adopted)
            {
                Warn("Child embedding failed; trying owned mode for this attachment.");
                mode = GeckoHostMode.Owned;
                adopted = AdoptAsOwned(window);
            }
        }
        else
        {
            adopted = AdoptAsOwned(window);
        }

        if (!adopted)
        {
            // "Not shown" rather than a stray browser window somewhere on the desktop.
            ShowWindowAsync(window, SW_HIDE);
            Error($"Could not attach Gecko's window 0x{window:X}; the Gecko page will not be shown.");
            return false;
        }

        _gecko = window;
        _geckoProcessId = processId;
        _attachedMode = mode;
        _saved = saved;
        _pageActive = false;
        _ownedTarget = null;
        _ownedShown = false;
        _ownedCorrections = 0;
        InstallWinEventHooks();
        Info($"Attached Gecko window 0x{window:X} (process {processId}) in {mode} mode.");
        SyncGecko();
        return true;
    }

    /// <summary>Makes Gecko's top-level window a child window of the container.</summary>
    private bool AdoptAsChild(nint window, SavedWindowState saved)
    {
        // A maximised or minimised window keeps that state as a child, with odd results.
        // SW_SHOWNOACTIVATE restores it without activating it.
        if (IsIconic(window) || IsZoomed(window))
        {
            ShowWindow(window, SW_SHOWNOACTIVATE);
        }

        // While it is still a top-level window, put it where it is going to be. Besides avoiding
        // a visible jump, this is what gets the page's scale right on a multi-monitor desktop:
        // if Gecko started on a monitor with a different DPI than Mosaic's, this move makes
        // Windows send it WM_DPICHANGED for Mosaic's monitor, the one message that makes Gecko
        // re-read its scale. Once it is a child it will not get that message again (see
        // OnDpiChanged). Synchronous on purpose, so the message precedes SetParent.
        // If this element is not showing right now (another tab is selected), hide Gecko's
        // window for the move so that it does not flash over what is showing.
        if (!IsWindowVisible(_container))
        {
            ShowWindow(window, SW_HIDE);
        }
        if (GetWindowRect(_container, out RECT target))
        {
            SetWindowPos(window, 0, target.Left, target.Top, Math.Max(target.Width, 1), Math.Max(target.Height, 1),
                SWP_NOZORDER | SWP_NOACTIVATE);
        }

        // SetParent documentation: SetParent does not touch WS_CHILD / WS_POPUP; when giving a
        // top-level window a parent, clear WS_POPUP and set WS_CHILD *before* calling SetParent.
        // The frame styles go too: a caption or sizing border on a child window would be drawn
        // and would take mouse input. (The agent's "hidechrome" has normally removed them
        // already. As a child, WS_MINIMIZEBOX/WS_MAXIMIZEBOX would mean WS_GROUP/WS_TABSTOP.)
        nint style = GetWindowLongPtrW(window, GWL_STYLE);
        nint childStyle = (nint)(((long)style
            & ~(WS_POPUP | WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX)) | WS_CHILD);
        if (!TrySetWindowLong(window, GWL_STYLE, childStyle, "the window style"))
        {
            return false;
        }

        // Extended styles: no taskbar button semantics, no edges, and WS_EX_NOPARENTNOTIFY must
        // be off because WM_PARENTNOTIFY is how the container learns about clicks on the page.
        // A failure here is cosmetic, so it does not abort the attach.
        nint exStyle = GetWindowLongPtrW(window, GWL_EXSTYLE);
        TrySetWindowLong(window, GWL_EXSTYLE, (nint)((long)exStyle
            & ~(WS_EX_APPWINDOW | WS_EX_TOOLWINDOW | WS_EX_NOPARENTNOTIFY
                | WS_EX_WINDOWEDGE | WS_EX_CLIENTEDGE | WS_EX_STATICEDGE | WS_EX_DLGMODALFRAME)),
            "the extended window style");

        // The return value (previous parent) is not a dependable success signal for a window
        // that was top-level, so the outcome is checked directly.
        SetParent(window, _container);
        int parentError = Marshal.GetLastPInvokeError();
        if (!IsChild(_container, window))
        {
            Warn($"SetParent(0x{window:X}) failed: {Describe(parentError)}");
            TrySetWindowLong(window, GWL_STYLE, saved.Style, "the window style (restoring)");
            TrySetWindowLong(window, GWL_EXSTYLE, saved.ExStyle, "the extended window style (restoring)");
            SetWindowPos(window, 0, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            return false;
        }

        // Fill the container. SWP_FRAMECHANGED makes the style changes take effect (SetWindowPos
        // documentation); SWP_SHOWWINDOW because inside the container Gecko's window is always
        // "visible": whether the page actually shows is decided by the container's visibility,
        // which Place drives. A child of a hidden window is hidden.
        //
        // The SetParent documentation also asks to synchronise the UISTATE of both windows.
        // That state only governs focus rectangles and accelerator underlines drawn by standard
        // controls; Gecko draws its own, so it is left alone.
        GetClientRect(_container, out RECT client);
        _childSize = (client.Width, client.Height);
        if (!SetWindowPos(window, 0, 0, 0, client.Width, client.Height,
                SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_SHOWWINDOW))
        {
            Warn($"Sizing the Gecko window after SetParent failed: {LastError()}");
        }
        return true;
    }

    /// <summary>Leaves Gecko's window top-level, borderless, and owned by Mosaic's window.</summary>
    private bool AdoptAsOwned(nint window)
    {
        if (IsIconic(window) || IsZoomed(window))
        {
            ShowWindow(window, SW_SHOWNOACTIVATE);
        }

        // The taskbar only re-evaluates whether a window gets a button when the window is shown,
        // so the owner and WS_EX_APPWINDOW are changed while it is hidden. SyncOwned shows it
        // again in the right place.
        ShowWindow(window, SW_HIDE);

        // Borderless pop-up: without a caption or sizing border Windows 11 neither rounds the
        // corners nor draws a shadow around what should look like part of Mosaic's window.
        nint style = GetWindowLongPtrW(window, GWL_STYLE);
        if (!TrySetWindowLong(window, GWL_STYLE, (nint)((((long)style
                & ~(WS_CHILD | WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX)) | WS_POPUP)
                & ~WS_VISIBLE), "the window style"))
        {
            return false;
        }
        nint exStyle = GetWindowLongPtrW(window, GWL_EXSTYLE);
        TrySetWindowLong(window, GWL_EXSTYLE, (nint)((long)exStyle
            & ~(WS_EX_APPWINDOW | WS_EX_WINDOWEDGE | WS_EX_CLIENTEDGE | WS_EX_STATICEDGE | WS_EX_DLGMODALFRAME)),
            "the extended window style");

        // An owned window stays above its owner, is hidden when the owner is minimised and has
        // no taskbar button. If the owner cannot be set the page still shows and follows, but
        // may fall behind Mosaic's window; that is traced by TrySetWindowLong.
        ApplyOwner(window, TopLevelWindow);

        SetWindowPos(window, 0, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        return true;
    }

    /// <summary>GWLP_HWNDPARENT on a top-level window sets its owner (SetWindowLongPtr documentation).</summary>
    private static void ApplyOwner(nint window, nint owner) =>
        TrySetWindowLong(window, GWLP_HWNDPARENT, owner, "the owner window");

    // =====================================================================================
    // Releasing
    // =====================================================================================

    /// <summary>Puts Gecko's window back as a hidden top-level window and forgets it.</summary>
    private void Release()
    {
        nint window = _gecko;
        int processId = _geckoProcessId;
        GeckoHostMode mode = _attachedMode;
        SavedWindowState saved = _saved;
        bool hadFocus = _pageActive || GetFocus() == window;
        ClearAttachment();

        if (!IsLive(window, processId))
        {
            Info($"Gecko window 0x{window:X} was already gone when released.");
        }
        else if (!Answers(window, ReleaseProbeTimeoutMs))
        {
            // Everything needed to put the window back waits for Gecko's UI thread, and so (to
            // the best of our knowledge; this is not documented) does destroying a window that
            // still has a child or owned window of a hung thread, which is what happens next
            // when Mosaic closes. Waiting is not an option, and leaving the window in place is
            // not safe, so the engine process goes. Its windows go with it without its help.
            Warn($"Gecko's window 0x{window:X} did not answer within {ReleaseProbeTimeoutMs} ms while being released; " +
                 $"terminating Gecko process {processId} so that it cannot block Mosaic.");
            TerminateGecko(processId);
        }
        else
        {
            // Hidden first, so that it never shows up as a stray window on the desktop.
            ShowWindow(window, SW_HIDE);
            bool free = true;
            if (mode == GeckoHostMode.Child)
            {
                SetParent(window, 0);
                int parentError = Marshal.GetLastPInvokeError();
                if (_container != 0 && IsChild(_container, window))
                {
                    // Still a child: leave its styles consistent with that. It stays hidden, and
                    // goes when the container goes.
                    free = false;
                    Warn($"Could not take Gecko's window out of the container: {Describe(parentError)}");
                }
            }
            else
            {
                ApplyOwner(window, saved.Owner);
            }

            if (free)
            {
                // SetParent documentation: after SetParent(NULL), clear WS_CHILD and set
                // WS_POPUP. The saved style is exactly the top-level style the window had before
                // (pop-up or overlapped, as Gecko made it); it goes back as it was, minus
                // "visible", and the window goes back to where Gecko had put it.
                TrySetWindowLong(window, GWL_STYLE,
                    (nint)((long)saved.Style & ~(WS_CHILD | WS_VISIBLE | WS_MINIMIZE | WS_MAXIMIZE)),
                    "the window style (restoring)");
                TrySetWindowLong(window, GWL_EXSTYLE, saved.ExStyle, "the extended window style (restoring)");
                bool hasBounds = saved.Bounds.Width > 0 && saved.Bounds.Height > 0;
                SetWindowPos(window, 0, saved.Bounds.Left, saved.Bounds.Top, saved.Bounds.Width, saved.Bounds.Height,
                    SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED | (hasBounds ? 0 : SWP_NOMOVE | SWP_NOSIZE));
                Info($"Released Gecko window 0x{window:X}.");
            }
        }

        if (hadFocus)
        {
            ReturnFocusToXaml();
        }
    }

    /// <summary>Gecko's window is gone or going, by its own doing: only forget it.</summary>
    private void Forget(string reason)
    {
        if (_gecko == 0)
        {
            return;
        }
        Info($"Gecko window 0x{_gecko:X} {reason}; detaching.");
        bool hadFocus = _pageActive;
        ClearAttachment();
        if (hadFocus)
        {
            // The focus died with the window. Without this the keyboard is dead in Mosaic's
            // window until the user clicks something.
            _dispatcher.TryEnqueue(DispatcherQueuePriority.High, ReturnFocusToXaml);
        }
    }

    private void ClearAttachment()
    {
        RemoveWinEventHooks();
        _gecko = 0;
        _geckoProcessId = 0;
        _saved = default;
        _pageActive = false;
        _childSize = default;
        _ownedTarget = null;
        _ownedShown = false;
        _ownedCorrections = 0;
    }

    private static void TerminateGecko(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            process.Kill();
            if (!process.WaitForExit(3000))
            {
                Warn($"Gecko process {processId} had not exited 3 s after being terminated.");
            }
        }
        catch (ArgumentException)
        {
            // No such process: it exited by itself in the meantime.
        }
        catch (Exception error)
        {
            Error($"Could not terminate Gecko process {processId}: {error.Message}");
        }
    }

    // =====================================================================================
    // Keeping Gecko's window in place
    // =====================================================================================

    /// <summary>Brings Gecko's window in line with the container. Idempotent and cheap.</summary>
    private void SyncGecko()
    {
        if (_gecko == 0 || _container == 0)
        {
            return;
        }
        if (_attachedMode == GeckoHostMode.Child)
        {
            SyncChild();
        }
        else
        {
            SyncOwned();
        }
    }

    private void SyncChild()
    {
        // Position, clipping, z-order and visibility come with being a child. Only the size
        // needs following.
        if (GetClientRect(_container, out RECT client) && _childSize != (client.Width, client.Height))
        {
            _childSize = (client.Width, client.Height);
            MoveGecko(0, 0, client.Width, client.Height);
        }
        if (_pageActive && !IsWindowVisible(_container))
        {
            // The page was hidden (another tab, another engine) while it had the focus.
            DeactivatePage();
        }
    }

    private void SyncOwned()
    {
        nint topLevel = TopLevelWindow;
        if (topLevel != 0 && IsIconic(topLevel))
        {
            // Windows hides owned windows when their owner is minimised and shows them again on
            // restore. The container's rectangle is meaningless meanwhile; touch nothing.
            return;
        }

        RECT target = default;
        bool show = topLevel != 0 && IsWindowVisible(_container)
            && GetWindowRect(_container, out target) && target.Width > 0 && target.Height > 0;
        if (!show)
        {
            if (_ownedShown)
            {
                _ownedShown = false;
                ShowWindowAsync(_gecko, SW_HIDE);
            }
            return;
        }

        if (_ownedTarget is not { } current || !current.Equals(target))
        {
            _ownedTarget = target;
            _ownedCorrections = 0;
            MoveGecko(target.Left, target.Top, target.Width, target.Height);
        }
        if (!_ownedShown && _gecko != 0)
        {
            // The requested state is tracked here rather than read back with IsWindowVisible:
            // the requests are asynchronous and an earlier hide may not have been applied yet.
            // SW_SHOWNA: show without activating.
            _ownedShown = true;
            ShowWindowAsync(_gecko, SW_SHOWNA);
        }
    }

    /// <summary>
    /// Owned mode: Gecko's window changed its own rectangle. The expected cause is a DPI change:
    /// as a top-level window it receives WM_DPICHANGED when it crosses to a monitor with another
    /// scale, and resizes itself to the rectangle Windows suggests, which is not ours.
    /// </summary>
    private void CorrectOwnedPosition()
    {
        if (_attachedMode != GeckoHostMode.Owned || _ownedTarget is not { } target || !_ownedShown)
        {
            return;
        }
        nint topLevel = TopLevelWindow;
        if (topLevel == 0 || IsIconic(topLevel) || !GetWindowRect(_gecko, out RECT actual))
        {
            return;
        }
        if (actual.Equals(target))
        {
            _ownedCorrections = 0;
            return;
        }
        // Our own moves are asynchronous, so notifications for rectangles we have since replaced
        // also arrive here; re-applying the current one is harmless. The limit is for a window
        // that refuses the rectangle (a minimum size, say): do not fight it forever.
        if (_ownedCorrections >= MaxOwnedCorrections)
        {
            return;
        }
        _ownedCorrections++;
        MoveGecko(target.Left, target.Top, target.Width, target.Height);
    }

    private void MoveGecko(int x, int y, int width, int height)
    {
        // SWP_ASYNCWINDOWPOS: "if the calling thread and the thread that owns the window are
        // attached to different input queues, the system posts the request" (SetWindowPos
        // documentation). Because the cross-process relationship attaches the queues, Windows
        // may well treat this as an ordinary synchronous call; the documentation does not say.
        // The flag is passed for the cases where it helps. Either way a resize waits at most for
        // Gecko to acknowledge the new size, not for it to repaint.
        if (SetWindowPos(_gecko, 0, x, y, Math.Max(width, 0), Math.Max(height, 0),
                SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS))
        {
            return;
        }
        int error = Marshal.GetLastPInvokeError();
        if (IsLive(_gecko, _geckoProcessId))
        {
            Warn($"Moving the Gecko window failed: {Describe(error)}");
        }
        else
        {
            Forget("no longer exists");
        }
    }

    // =====================================================================================
    // Focus and activation (child mode)
    //
    // Windows only activates top-level windows. A child window never receives WM_ACTIVATE, and
    // Gecko only treats its window as active (focus ring, caret, keyboard events delivered to
    // the page) after WM_ACTIVATE followed by WM_SETFOCUS: nsWindow sets sJustGotActivate on
    // WM_ACTIVATE and raises the window in its focus manager on the next WM_SETFOCUS. So when
    // the page should have the keyboard, Gecko is told so with posted messages:
    //
    //   WM_ACTIVATE(WA_ACTIVE)  Gecko notes the activation, then passes the message to
    //                           DefWindowProc, which sets the keyboard focus to the window
    //                           (documented default processing of WM_ACTIVATE). That SetFocus
    //                           runs on Gecko's own thread, so Mosaic never waits for it, and it
    //                           produces the real WM_SETFOCUS that completes the activation.
    //   WM_SETFOCUS             Covers the case where Gecko's window already had the Win32
    //                           focus (SetFocus is then a no-op and sends nothing). If the
    //                           activation was already completed, Gecko ignores this one.
    //
    // and when the focus goes back to Mosaic's own controls:
    //
    //   WM_ACTIVATE(WA_INACTIVE)  Gecko has by then received the real WM_KILLFOCUS, which makes
    //                             it deactivate at once on this message (the same order it
    //                             handles when a window is minimised).
    //
    // WinUI helps on the way back: clicking a XAML control (the address bar) makes WinUI's
    // content window take the Win32 focus, so that needs nothing from this class beyond telling
    // Gecko (OnXamlGotFocus). When Mosaic's window is reactivated WinUI restores the focus to its
    // own content, not to the page; OnTopLevelActivate hands it back to the page if it had it.
    //
    // One side effect to know about: Gecko closes its open pop-ups (menus, dropdowns) on any
    // WM_ACTIVATE. That is why activation is only posted when the page is not already active.
    // =====================================================================================

    private void ActivatePage()
    {
        if (Post(WM_ACTIVATE, WA_ACTIVE) && Post(WM_SETFOCUS, 0))
        {
            _pageActive = true;
        }
    }

    private void DeactivatePage()
    {
        _pageActive = false;
        Post(WM_ACTIVATE, WA_INACTIVE);
    }

    /// <summary>
    /// Closes Gecko's open pop-ups without changing who has the focus. Gecko normally does this
    /// when its top-level window is clicked outside the pop-up or starts moving; as a child it
    /// sees neither when the click or drag is on Mosaic's own UI. A posted WM_ACTIVATEAPP is the
    /// message with the least side effects that makes nsWindow::DealWithPopups roll them up (its
    /// only other effect is telling Gecko's GPU process that the app is in the foreground).
    /// </summary>
    private void DismissPagePopups()
    {
        if (_gecko != 0 && _attachedMode == GeckoHostMode.Child && _pageActive)
        {
            Post(WM_ACTIVATEAPP, 1);
        }
    }

    private bool Post(int message, nint wParam)
    {
        if (PostMessageW(_gecko, message, wParam, 0))
        {
            return true;
        }
        int error = Marshal.GetLastPInvokeError();
        if (IsLive(_gecko, _geckoProcessId))
        {
            Warn($"Posting message 0x{message:X4} to the Gecko window failed: {Describe(error)}");
        }
        else
        {
            Forget("no longer exists");
        }
        return false;
    }

    private void OnContainerGotFocus()
    {
        // The container itself never wants the focus. It gets it through SetFocus or Tab
        // handling by other code, or when a click lands on it while Gecko is not there.
        if (_gecko != 0 && _attachedMode == GeckoHostMode.Child && IsWindowVisible(_container))
        {
            ActivatePage();
        }
        else
        {
            // Nothing here can use the keyboard; do not leave the focus stranded on an empty
            // window. Deferred: changing focus from inside WM_SETFOCUS is asking for trouble.
            _dispatcher.TryEnqueue(DispatcherQueuePriority.High, () =>
            {
                if (_container != 0 && GetFocus() == _container)
                {
                    ReturnFocusToXaml();
                }
            });
        }
    }

    /// <summary>
    /// WM_PARENTNOTIFY: sent to the ancestors of a child window when it is clicked, created or
    /// destroyed. It comes from Gecko's thread while that thread is handling the click, so this
    /// must return quickly and must not call back into Gecko synchronously.
    /// </summary>
    private void OnParentNotify(nint wParam, nint lParam)
    {
        if (_gecko == 0 || _attachedMode != GeckoHostMode.Child)
        {
            return;
        }
        switch ((int)(wParam & 0xFFFF))
        {
            case WM_LBUTTONDOWN:
            case WM_RBUTTONDOWN:
            case WM_MBUTTONDOWN:
            case WM_XBUTTONDOWN:
            case WM_POINTERDOWN:
                // The user clicked into the page. Gecko does not take the Win32 focus for a
                // child window by itself in a way that also activates it (see above). The
                // GetFocus test repairs the case where the focus left Gecko without this class
                // noticing.
                if (!_pageActive || GetFocus() != _gecko)
                {
                    ActivatePage();
                }
                QueuePageActivated();
                break;

            case WM_DESTROY when lParam == _gecko:
                // Sent "before any processing to destroy the window takes place".
                Forget("is being destroyed by Gecko");
                break;
        }
    }

    /// <summary>
    /// A XAML element took the keyboard (address bar clicked, Ctrl+L forwarded by the agent, a
    /// menu opened). Called by <see cref="ContentArea"/> from <c>FocusManager.GotFocus</c>.
    /// </summary>
    public void OnXamlGotFocus()
    {
        // WinUI has moved the Win32 focus to its content window by now; tell Gecko it is inactive.
        // While Mosaic's window is being reactivated WinUI restores its own focus first; the page
        // gets the keyboard back right after (OnTopLevelActivate), so that is not a hand-over.
        if (_pageActive && _gecko != 0 && Environment.TickCount64 >= _restoringUntil && !HasFocusWithin)
        {
            DeactivatePage();
        }
    }

    /// <summary>A pointer went down on Mosaic's own UI: Gecko's open pop-ups go.</summary>
    public void OnXamlPointerPressed() => DismissPagePopups();

    /// <summary>Gives the keyboard to Mosaic's own UI, if Mosaic's window is the active one.</summary>
    private void ReturnFocusToXaml()
    {
        if (_topLevel == 0 || GetActiveWindow() != _topLevel)
        {
            return;
        }
        // The top-level window hands a focus it receives to WinUI's content window, which then
        // puts it on the XAML element that last had it.
        SetFocus(_topLevel);
        FocusXaml?.Invoke();
    }

    private void QueuePageActivated()
    {
        if (_activationNotificationQueued)
        {
            return;
        }
        _activationNotificationQueued = true;
        _dispatcher.TryEnqueue(DispatcherQueuePriority.High, () =>
        {
            _activationNotificationQueued = false;
            PageActivated?.Invoke(this, EventArgs.Empty);
        });
    }

    // =====================================================================================
    // Mosaic's own window
    // =====================================================================================

    private nint TopLevelWindow => _topLevel;

    /// <summary>Messages of Mosaic's top-level window (<see cref="WindowMessages"/>).</summary>
    private void OnTopLevelMessage(int msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_WINDOWPOSCHANGED:
                // Moved, resized, minimised, restored, shown, hidden. Child mode needs none of
                // this (the container's own messages cover it); owned mode follows.
                if (_attachedMode == GeckoHostMode.Owned)
                {
                    SyncGecko();
                }
                break;

            case WM_ACTIVATE:
                OnTopLevelActivate((int)(wParam & 0xFFFF));
                break;

            case WM_ENTERSIZEMOVE:
                DismissPagePopups();
                break;

            case WM_DPICHANGED:
                OnDpiChanged((int)(wParam & 0xFFFF));
                break;
        }
    }

    private void OnTopLevelActivate(int activation)
    {
        if (_gecko == 0 || _attachedMode != GeckoHostMode.Child)
        {
            return;
        }
        if (activation == WA_INACTIVE)
        {
            // Another window is taking over. Gecko's child window only gets WM_KILLFOCUS out of
            // that, which is not enough for it to consider itself inactive.
            if (_pageActive)
            {
                DeactivatePage();
                _restorePageOnActivate = true;
            }
            return;
        }

        bool restore = _restorePageOnActivate;
        _restorePageOnActivate = false;
        if (!restore || activation == WA_CLICKACTIVE)
        {
            // Activated by a click: whatever was clicked decides where the focus goes (a click
            // on the page arrives as WM_PARENTNOTIFY).
            return;
        }
        // The page had the keyboard when Mosaic was deactivated, so it gets it back. WinUI puts
        // the focus on its own content first (the XAML element that last had it); this runs
        // after that and moves it to the page. XAML focus changes in the meantime are not a
        // reason to deactivate the page (OnXamlGotFocus).
        _restoringUntil = Environment.TickCount64 + 250;
        _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (_gecko != 0 && _attachedMode == GeckoHostMode.Child && !_pageActive
                && _container != 0 && IsWindowVisible(_container) && GetActiveWindow() == TopLevelWindow)
            {
                ActivatePage();
            }
        });
    }

    // =====================================================================================
    // Notifications from Gecko's process
    // =====================================================================================

    /// <summary>
    /// Out-of-context WinEvent hooks limited to Gecko's process: Windows queues the events and
    /// delivers them on this thread through its message loop, so nothing is injected into Gecko
    /// and nothing here can block it. They are the only way to hear about a foreign window that
    /// works the same in both modes.
    /// </summary>
    private void InstallWinEventHooks()
    {
        // The window was destroyed (Gecko quit or crashed, or closed its window).
        AddWinEventHook(EVENT_OBJECT_DESTROY);
        if (_attachedMode == GeckoHostMode.Owned)
        {
            // Became the foreground window: the user clicked into the page.
            AddWinEventHook(EVENT_SYSTEM_FOREGROUND);
            // Moved or resized itself.
            AddWinEventHook(EVENT_OBJECT_LOCATIONCHANGE);
        }
    }

    private void AddWinEventHook(uint eventId)
    {
        nint hook = SetWinEventHook(eventId, eventId, 0, Marshal.GetFunctionPointerForDelegate(_winEventProc),
            (uint)_geckoProcessId, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        if (hook == 0)
        {
            Warn($"SetWinEventHook(0x{eventId:X4}) for process {_geckoProcessId} failed; some changes to the Gecko window will go unnoticed.");
            return;
        }
        _winEventHooks.Add(hook);
    }

    private void RemoveWinEventHooks()
    {
        foreach (nint hook in _winEventHooks)
        {
            UnhookWinEvent(hook);
        }
        _winEventHooks.Clear();
    }

    private void OnWinEvent(nint hook, uint eventId, nint hwnd, int idObject, int idChild, uint eventThread, uint eventTime)
    {
        // Called from native code: nothing may escape.
        try
        {
            if (hwnd == 0 || hwnd != _gecko)
            {
                return;
            }
            switch (eventId)
            {
                case EVENT_OBJECT_DESTROY when idObject == OBJID_WINDOW && idChild == CHILDID_SELF:
                    // Events are delivered late; make sure it is not a new window that happens
                    // to have the old handle.
                    if (!IsLive(_gecko, _geckoProcessId))
                    {
                        Forget("was destroyed");
                    }
                    break;

                case EVENT_SYSTEM_FOREGROUND:
                    QueuePageActivated();
                    break;

                case EVENT_OBJECT_LOCATIONCHANGE when idObject == OBJID_WINDOW && idChild == CHILDID_SELF:
                    CorrectOwnedPosition();
                    break;
            }
        }
        catch (Exception error)
        {
            Error($"Handling WinEvent 0x{eventId:X4} failed: {error}");
        }
    }

    // =====================================================================================
    // Helpers
    // =====================================================================================

    private void VerifyAccess()
    {
        if (!_dispatcher.HasThreadAccess)
        {
            throw new InvalidOperationException("GeckoWindowHost is used from a thread other than the UI thread.");
        }
    }

    private static GeckoHostMode ModeFromCommandLine() =>
        Environment.GetCommandLineArgs().Any(argument =>
            string.Equals(argument, "--gecko-host=owned", StringComparison.OrdinalIgnoreCase))
            ? GeckoHostMode.Owned
            : GeckoHostMode.Child;

    /// <summary>
    /// The container: a child window of Mosaic's top-level window with the system window colour
    /// as background (what shows while Gecko is starting or absent), and
    /// <see cref="ContainerProc"/> as its window procedure.
    /// </summary>
    private static unsafe nint CreateContainer(nint parent)
    {
        nint instance = GetModuleHandleW(null);
        delegate* unmanaged[Stdcall]<nint, int, nint, nint, nint> procedure = &ContainerProc;
        fixed (char* className = ContainerClassName)
        {
            var windowClass = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = (nint)procedure,
                hInstance = instance,
                hCursor = LoadCursorW(0, IDC_ARROW),
                hbrBackground = GetSysColorBrush(COLOR_WINDOW),
                lpszClassName = className,
            };
            if (RegisterClassExW(in windowClass) == 0)
            {
                int error = Marshal.GetLastPInvokeError();
                if (error != ERROR_CLASS_ALREADY_EXISTS) // a second host, or a second window
                {
                    throw new Win32Exception(error, $"Could not register the Gecko host window class: {Describe(error)}");
                }
            }
        }

        // WS_CLIPCHILDREN: never paint the background over Gecko's window. WS_CLIPSIBLINGS: it
        // shares its parent with WinUI's content window. Created hidden; Place shows it.
        nint container = CreateWindowExW(0, ContainerClassName, null,
            (uint)(WS_CHILD | WS_CLIPCHILDREN | WS_CLIPSIBLINGS), 0, 0, 0, 0, parent, 0, instance, 0);
        if (container == 0)
        {
            int error = Marshal.GetLastPInvokeError();
            throw new Win32Exception(error, $"Could not create the Gecko host window: {Describe(error)}");
        }
        return container;
    }

    private static bool IsLive(nint window, int processId) =>
        window != 0 && IsWindow(window)
        && GetWindowThreadProcessId(window, out uint owner) != 0 && owner == (uint)processId;

    /// <summary>
    /// True if the window's thread processes a message within the timeout. WM_NULL does nothing;
    /// SMTO_ABORTIFHUNG returns at once if Windows already considers the thread hung.
    /// </summary>
    private static bool Answers(nint window, uint timeoutMilliseconds) =>
        SendMessageTimeoutW(window, WM_NULL, 0, 0, SMTO_ABORTIFHUNG, timeoutMilliseconds, out _) != 0;

    private bool SharesDpiAwarenessContext(nint window)
    {
        nint theirs = GetWindowDpiAwarenessContext(window);
        nint ours = GetWindowDpiAwarenessContext(_container);
        return theirs != 0 && ours != 0 && AreDpiAwarenessContextsEqual(theirs, ours);
    }

    /// <summary>SetWindowLongPtr with the error check its documentation prescribes (a zero
    /// return is only a failure if the last error is set).</summary>
    private static bool TrySetWindowLong(nint window, int index, nint value, string what)
    {
        nint previous = SetWindowLongPtrW(window, index, value);
        int error = Marshal.GetLastPInvokeError();
        if (previous == 0 && error != 0)
        {
            Warn($"Setting {what} of window 0x{window:X} failed: {Describe(error)}");
            return false;
        }
        return true;
    }

    private static string LastError() => Describe(Marshal.GetLastPInvokeError());

    private static string Describe(int error) => $"{new Win32Exception(error).Message} (Win32 error {error})";

    private static void Info(string message) => Trace.TraceInformation("[GeckoWindowHost] " + message);

    private static void Warn(string message) => Trace.TraceWarning("[GeckoWindowHost] " + message);

    private static void Error(string message) => Trace.TraceError("[GeckoWindowHost] " + message);
}
