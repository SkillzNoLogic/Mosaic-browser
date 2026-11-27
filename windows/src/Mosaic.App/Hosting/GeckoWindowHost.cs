using System.Runtime.InteropServices;
using System.Windows.Interop;

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
/// A WPF element that shows the Gecko engine's chromeless browser window in its own bounds.
/// Place one in the content area; it is the Windows counterpart of the transparent "hole" on macOS.
/// </summary>
/// <remarks>
/// Contract (the app shell codes against this; the hosting work implements it):
/// <list type="bullet">
/// <item>All members are called on the UI thread.</item>
/// <item><see cref="Attach"/> adopts the given Gecko window; <see cref="Detach"/> lets go of it
/// (Gecko stopped or is about to). Both are safe to call repeatedly.</item>
/// <item>While attached, the Gecko window exactly fills this element and follows it when the
/// Mosaic window moves, resizes, changes DPI, minimises or restores.</item>
/// <item>WPF <c>Visibility</c> controls whether the page shows: when this element is not
/// <c>Visible</c> the Gecko window is hidden; no other call is needed.</item>
/// <item>The Gecko process must never keep Mosaic's window from closing, and closing Mosaic
/// must not destroy Gecko's window out from under it: detach first.</item>
/// </list>
/// </remarks>
public sealed class GeckoWindowHost : HwndHost
{
    /// <summary>Set before the first <see cref="Attach"/>. Default <see cref="GeckoHostMode.Child"/>;
    /// the command-line switch <c>--gecko-host=owned</c> selects the fallback.</summary>
    public GeckoHostMode Mode { get; set; } = GeckoHostMode.Child;

    public bool IsAttached => false;

    /// <summary>Adopts Gecko's browser window. Returns false if the handle is not a live window.</summary>
    public bool Attach(nint geckoWindow, int geckoProcessId) => false;

    public void Detach() { }

    /// <summary>Gives keyboard focus to the page.</summary>
    public void FocusPage() { }

    /// <summary>Raised when the user clicks into / otherwise activates the Gecko page.</summary>
    public event EventHandler? PageActivated;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent) =>
        throw new NotImplementedException("GeckoWindowHost is part of the contract but has not been implemented yet.");

    protected override void DestroyWindowCore(HandleRef hwnd) { }

    private void Touch() => PageActivated?.Invoke(this, EventArgs.Empty);
}
