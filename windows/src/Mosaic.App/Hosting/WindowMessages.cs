using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static Mosaic.App.Hosting.NativeMethods;

namespace Mosaic.App.Hosting;

/// <summary>
/// The Win32 messages of Mosaic's top-level window, which WinUI does not surface: activation,
/// moves and resizes, DPI changes, minimum size, the end of the Windows session.
/// </summary>
/// <remarks>
/// Implemented as a comctl32 window subclass (<c>SetWindowSubclass</c>), which chains cleanly
/// with WinUI's own window procedure. Listeners run on the UI thread, after WinUI has handled
/// the message, except <see cref="MinimumSize"/>, which fills in <c>WM_GETMINMAXINFO</c>.
/// Nothing may throw back into native code, so every listener call is guarded.
/// </remarks>
public sealed class WindowMessages : IDisposable
{
    /// <summary>A message of the top-level window; called after the window has processed it.</summary>
    public delegate void Listener(int message, nint wParam, nint lParam);

    private static readonly Dictionary<nint, WindowMessages> instances = [];
    private readonly nint window;
    private readonly List<Listener> listeners = [];
    private bool attached;

    public WindowMessages(nint window)
    {
        this.window = window;
        unsafe
        {
            delegate* unmanaged[Stdcall]<nint, int, nint, nint, nuint, nuint, nint> procedure = &SubclassProc;
            attached = SetWindowSubclass(window, (nint)procedure, SubclassId, 0);
        }
        if (attached)
        {
            lock (instances) instances[window] = this;
        }
        else
        {
            Trace.TraceError($"[WindowMessages] SetWindowSubclass failed for 0x{window:X}; window messages will not be observed.");
        }
    }

    /// <summary>The window's minimum size in physical pixels, applied through <c>WM_GETMINMAXINFO</c>; null for none.</summary>
    public Func<(int Width, int Height)?>? MinimumSize { get; set; }

    public void Add(Listener listener) => listeners.Add(listener);

    public void Remove(Listener listener) => listeners.Remove(listener);

    public void Dispose()
    {
        if (!attached) return;
        attached = false;
        lock (instances) instances.Remove(window);
        unsafe
        {
            delegate* unmanaged[Stdcall]<nint, int, nint, nint, nuint, nuint, nint> procedure = &SubclassProc;
            RemoveWindowSubclass(window, (nint)procedure, SubclassId);
        }
    }

    private const nuint SubclassId = 0x4D6F73; // "Mos"

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint SubclassProc(nint hwnd, int message, nint wParam, nint lParam, nuint id, nuint data)
    {
        WindowMessages? self;
        lock (instances) instances.TryGetValue(hwnd, out self);
        nint result = DefSubclassProc(hwnd, message, wParam, lParam);
        if (self is null) return result;
        try
        {
            if (message == WM_GETMINMAXINFO && self.MinimumSize?.Invoke() is { } minimum && lParam != 0)
            {
                unsafe
                {
                    var info = (MINMAXINFO*)lParam;
                    info->MinTrackSize.X = Math.Max(info->MinTrackSize.X, minimum.Width);
                    info->MinTrackSize.Y = Math.Max(info->MinTrackSize.Y, minimum.Height);
                }
            }
            // The documented place to remove a subclass is the window's last message.
            if (message == WM_NCDESTROY) self.Dispose();
            // A copy: a listener may add or remove listeners.
            foreach (var listener in self.listeners.ToArray())
            {
                listener(message, wParam, lParam);
            }
        }
        catch (Exception error)
        {
            Trace.TraceError($"[WindowMessages] Handling message 0x{message:X4} failed: {error}");
        }
        return result;
    }
}
