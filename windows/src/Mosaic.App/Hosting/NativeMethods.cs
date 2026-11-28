using System.Runtime.InteropServices;

namespace Mosaic.App.Hosting;

/// <summary>
/// The Win32 surface used by the hosting code, and nothing else. Kept deliberately small:
/// every function here is called from <see cref="GeckoWindowHost"/>, <see cref="WindowMessages"/>,
/// <see cref="ContentArea"/> or <see cref="ChildProcessJob"/>, and every call site checks the result.
/// </summary>
/// <remarks>
/// Mosaic ships for x64 and ARM64 only, so the pointer-sized <c>GetWindowLongPtrW</c> /
/// <c>SetWindowLongPtrW</c> exports are used directly (they do not exist in 32-bit user32).
/// Functions declared with <c>SetLastError = true</c> have the thread's last error cleared
/// before the call by the generated marshalling code, so
/// <see cref="Marshal.GetLastPInvokeError"/> is meaningful even for functions (such as
/// <c>SetWindowLongPtrW</c>) whose return value alone cannot tell failure from "was zero".
/// </remarks>
internal static unsafe partial class NativeMethods
{
    // ---- Window styles -------------------------------------------------------------------

    internal const long WS_POPUP = 0x80000000;
    internal const long WS_CHILD = 0x40000000;
    internal const long WS_MINIMIZE = 0x20000000;
    internal const long WS_VISIBLE = 0x10000000;
    internal const long WS_CLIPSIBLINGS = 0x04000000;
    internal const long WS_CLIPCHILDREN = 0x02000000;
    internal const long WS_MAXIMIZE = 0x01000000;
    internal const long WS_CAPTION = 0x00C00000;
    internal const long WS_SYSMENU = 0x00080000;
    internal const long WS_THICKFRAME = 0x00040000;
    internal const long WS_MINIMIZEBOX = 0x00020000;
    internal const long WS_MAXIMIZEBOX = 0x00010000;

    internal const long WS_EX_DLGMODALFRAME = 0x00000001;
    internal const long WS_EX_NOPARENTNOTIFY = 0x00000004;
    internal const long WS_EX_TOOLWINDOW = 0x00000080;
    internal const long WS_EX_WINDOWEDGE = 0x00000100;
    internal const long WS_EX_CLIENTEDGE = 0x00000200;
    internal const long WS_EX_STATICEDGE = 0x00020000;
    internal const long WS_EX_APPWINDOW = 0x00040000;
    internal const long WS_EX_LAYERED = 0x00080000;
    internal const uint LWA_ALPHA = 0x00000002;

    internal const int GWL_STYLE = -16;
    internal const int GWL_EXSTYLE = -20;
    internal const int GWLP_HWNDPARENT = -8;

    // ---- Messages ------------------------------------------------------------------------

    internal const int WM_NULL = 0x0000;
    internal const int WM_DESTROY = 0x0002;
    internal const int WM_SIZE = 0x0005;
    internal const int WM_ACTIVATE = 0x0006;
    internal const int WM_SETFOCUS = 0x0007;
    internal const int WM_QUERYENDSESSION = 0x0011;
    internal const int WM_ENDSESSION = 0x0016;
    internal const int WM_ACTIVATEAPP = 0x001C;
    internal const int WM_NCDESTROY = 0x0082;
    internal const int WM_GETMINMAXINFO = 0x0024;
    internal const int WM_WINDOWPOSCHANGING = 0x0046;
    internal const int WM_WINDOWPOSCHANGED = 0x0047;
    internal const int WM_LBUTTONDOWN = 0x0201;
    internal const int WM_RBUTTONDOWN = 0x0204;
    internal const int WM_MBUTTONDOWN = 0x0207;
    internal const int WM_XBUTTONDOWN = 0x020B;
    internal const int WM_PARENTNOTIFY = 0x0210;
    internal const int WM_ENTERSIZEMOVE = 0x0231;
    internal const int WM_POINTERDOWN = 0x0246;
    internal const int WM_DPICHANGED = 0x02E0;

    internal const int WA_INACTIVE = 0;
    internal const int WA_ACTIVE = 1;
    internal const int WA_CLICKACTIVE = 2;

    // ---- SetWindowPos / ShowWindow / misc ------------------------------------------------

    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOZORDER = 0x0004;
    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const uint SWP_FRAMECHANGED = 0x0020;
    internal const uint SWP_SHOWWINDOW = 0x0040;
    internal const uint SWP_HIDEWINDOW = 0x0080;
    internal const uint SWP_NOOWNERZORDER = 0x0200;
    internal const uint SWP_ASYNCWINDOWPOS = 0x4000;

    internal const nint HWND_TOP = 0;

    internal const int SW_HIDE = 0;
    internal const int SW_SHOWNOACTIVATE = 4;
    internal const int SW_SHOWNA = 8;

    internal const uint GA_ROOT = 2;
    internal const uint GW_HWNDPREV = 3;
    internal const uint GW_OWNER = 4;

    internal const uint SMTO_ABORTIFHUNG = 0x0002;

    internal const int COLOR_WINDOW = 5;
    internal const nint IDC_ARROW = 32512;

    internal const int ERROR_CLASS_ALREADY_EXISTS = 1410;

    // ---- WinEvents -----------------------------------------------------------------------

    internal const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    internal const uint EVENT_OBJECT_DESTROY = 0x8001;
    internal const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    internal const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    internal const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    internal const int OBJID_WINDOW = 0;
    internal const int CHILDID_SELF = 0;

    // ---- Job objects ---------------------------------------------------------------------

    internal const uint PROCESS_TERMINATE = 0x0001;
    internal const uint PROCESS_SET_QUOTA = 0x0100;
    internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    internal const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    internal const int JobObjectExtendedLimitInformation = 9;

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT : IEquatable<RECT>
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;

        public readonly bool Equals(RECT other) =>
            Left == other.Left && Top == other.Top && Right == other.Right && Bottom == other.Bottom;

        public override readonly bool Equals(object? obj) => obj is RECT other && Equals(other);
        public override readonly int GetHashCode() => HashCode.Combine(Left, Top, Right, Bottom);
        public override readonly string ToString() => $"({Left},{Top}) {Width}x{Height}";
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MINMAXINFO
    {
        public POINT Reserved;
        public POINT MaxSize;
        public POINT MaxPosition;
        public POINT MinTrackSize;
        public POINT MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public char* lpszMenuName;
        public char* lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    /// <summary>WINEVENTPROC. Out-of-context hooks call it on the thread that installed them,
    /// from that thread's message loop.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void WinEventProc(
        nint hook, uint eventId, nint hwnd, int idObject, int idChild, uint eventThread, uint eventTime);

    // ---- user32: inspection (none of these send messages to the target window) -----------

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindowVisible(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsIconic(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsZoomed(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsChild(nint hWndParent, nint hWnd);

    [LibraryImport("user32.dll")]
    internal static partial uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetWindowRect(nint hWnd, out RECT rect);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetClientRect(nint hWnd, out RECT rect);

    [LibraryImport("user32.dll")]
    internal static partial nint GetAncestor(nint hWnd, uint flags);

    [LibraryImport("user32.dll")]
    internal static partial nint GetWindow(nint hWnd, uint command);

    [LibraryImport("user32.dll")]
    internal static partial nint GetFocus();

    [LibraryImport("user32.dll")]
    internal static partial nint GetActiveWindow();

    [LibraryImport("user32.dll")]
    internal static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nint GetWindowLongPtrW(nint hWnd, int index);

    [LibraryImport("user32.dll")]
    internal static partial nint GetWindowDpiAwarenessContext(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AreDpiAwarenessContextsEqual(nint contextA, nint contextB);

    // ---- user32: changes. SetParent, SetWindowLongPtrW, ShowWindow and (when the two threads
    // share an input queue) SetWindowPos are synchronous: they wait for the target window's
    // thread. GeckoWindowHost only calls them on a foreign window right after probing that the
    // window answers; everything on a hot path is posted instead. ---------------------------

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nint SetParent(nint hWndChild, nint hWndNewParent);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nint SetWindowLongPtrW(nint hWnd, int index, nint value);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindow(nint hWnd, int command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindowAsync(nint hWnd, int command);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostMessageW(nint hWnd, int message, nint wParam, nint lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nint SendMessageTimeoutW(
        nint hWnd, int message, nint wParam, nint lParam, uint flags, uint timeoutMilliseconds, out nint result);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(nint hWnd);

    // ---- user32: the container window ----------------------------------------------------

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial ushort RegisterClassExW(in WNDCLASSEXW windowClass);

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint CreateWindowExW(
        uint exStyle, string className, string? windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    internal static partial nint GetSysColorBrush(int index);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetLayeredWindowAttributes(nint hWnd, uint colorKey, byte alpha, uint flags);

    [LibraryImport("user32.dll")]
    internal static partial nint LoadCursorW(nint instance, nint cursorName);

    // ---- user32: WinEvents ---------------------------------------------------------------

    [LibraryImport("user32.dll")]
    internal static partial nint SetWinEventHook(
        uint eventMin, uint eventMax, nint module, nint callback, uint processId, uint threadId, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnhookWinEvent(nint hook);

    // ---- user32: Mosaic's own windows --------------------------------------------------------

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nint SetFocus(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ScreenToClient(nint hWnd, ref POINT point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ClientToScreen(nint hWnd, ref POINT point);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint FindWindowExW(nint parent, nint childAfter, string? className, string? windowName);

    [LibraryImport("user32.dll")]
    internal static partial uint GetDpiForWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    internal static partial nint DefWindowProcW(nint hWnd, int message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AllowSetForegroundWindow(uint processId);

    internal const uint ASFW_ANY = unchecked((uint)-1);

    // ---- comctl32: subclassing Mosaic's top-level window (app.manifest asks for v6) ----------

    [LibraryImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowSubclass(nint hWnd, nint subclassProc, nuint subclassId, nuint refData);

    [LibraryImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RemoveWindowSubclass(nint hWnd, nint subclassProc, nuint subclassId);

    [LibraryImport("comctl32.dll")]
    internal static partial nint DefSubclassProc(nint hWnd, int message, nint wParam, nint lParam);

    // ---- kernel32 ------------------------------------------------------------------------

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint GetModuleHandleW(string? moduleName);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint CreateJobObjectW(nint securityAttributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetInformationJobObject(
        nint job, int informationClass, in JOBOBJECT_EXTENDED_LIMIT_INFORMATION information, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsProcessInJob(nint process, nint job, [MarshalAs(UnmanagedType.Bool)] out bool result);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);
}
