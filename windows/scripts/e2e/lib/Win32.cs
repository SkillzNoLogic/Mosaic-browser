// Native helpers for Mosaic's end-to-end checks (scripts/e2e): finding Mosaic's windows,
// hang detection, process dumps and window captures that do not need the app's cooperation.
// Compiled by E2E.psm1 with Add-Type; C# 5 (see TestServer.cs) and System.Drawing only.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MosaicE2E
{
    public sealed class WindowInfo
    {
        public long Handle;
        public long Parent;
        public int ProcessId;
        public string ClassName;
        public string Title;
        public bool Visible;
        public int X, Y, Width, Height;
        public override string ToString()
        {
            return string.Format("0x{0:X} parent 0x{1:X} pid {2} {3} '{4}' '{5}' at {6},{7} size {8}x{9}",
                Handle, Parent, ProcessId, Visible ? "visible" : "hidden", ClassName, Title, X, Y, Width, Height);
        }
    }

    public static class Win32
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        private delegate bool EnumProc(IntPtr window, IntPtr data);

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr data);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr data);
        [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr window);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out RECT rect);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsHungAppWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr window, StringBuilder name, int size);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr window, StringBuilder text, int size);
        [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr hdc, uint flags);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] private static extern bool SendMessageTimeoutW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
        [DllImport("dbghelp.dll", SetLastError = true)]
        private static extern bool MiniDumpWriteDump(IntPtr process, uint processId, Microsoft.Win32.SafeHandles.SafeFileHandle file, uint type, IntPtr exception, IntPtr user, IntPtr callback);

        private static WindowInfo Describe(IntPtr window)
        {
            var info = new WindowInfo();
            info.Handle = window.ToInt64();
            info.Parent = GetParent(window).ToInt64();
            uint pid;
            GetWindowThreadProcessId(window, out pid);
            info.ProcessId = (int)pid;
            var name = new StringBuilder(256);
            GetClassNameW(window, name, name.Capacity);
            info.ClassName = name.ToString();
            var title = new StringBuilder(512);
            GetWindowTextW(window, title, title.Capacity);
            info.Title = title.ToString();
            info.Visible = IsWindowVisible(window);
            RECT rect;
            GetWindowRect(window, out rect);
            info.X = rect.Left; info.Y = rect.Top; info.Width = rect.Right - rect.Left; info.Height = rect.Bottom - rect.Top;
            return info;
        }

        /// <summary>Top-level windows of the given processes.</summary>
        public static List<WindowInfo> TopLevelWindows(int[] processIds)
        {
            var wanted = new HashSet<int>(processIds);
            var found = new List<WindowInfo>();
            EnumWindows(delegate (IntPtr window, IntPtr data)
            {
                uint pid;
                GetWindowThreadProcessId(window, out pid);
                if (wanted.Contains((int)pid)) found.Add(Describe(window));
                return true;
            }, IntPtr.Zero);
            return found;
        }

        /// <summary>Every descendant window of a window (any process: Gecko's and WebView2's are children of Mosaic's).</summary>
        public static List<WindowInfo> ChildWindows(long root)
        {
            var found = new List<WindowInfo>();
            EnumChildWindows(new IntPtr(root), delegate (IntPtr window, IntPtr data)
            {
                found.Add(Describe(window));
                return true;
            }, IntPtr.Zero);
            return found;
        }

        public static WindowInfo Window(long handle) { return Describe(new IntPtr(handle)); }
        public static bool Exists(long handle) { return IsWindow(new IntPtr(handle)); }

        /// <summary>
        /// True when Windows considers the window hung (it has not pumped messages for about 5 s),
        /// or when it does not answer WM_NULL within the timeout.
        /// </summary>
        public static bool IsHung(long handle, int timeoutMilliseconds)
        {
            var window = new IntPtr(handle);
            if (!IsWindow(window)) return false;
            if (IsHungAppWindow(window)) return true;
            IntPtr result;
            const uint SMTO_ABORTIFHUNG = 0x0002;
            return !SendMessageTimeoutW(window, 0 /* WM_NULL */, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, (uint)timeoutMilliseconds, out result);
        }

        /// <summary>Writes a dump of a running process (for hangs; crashes are caught by WER LocalDumps).</summary>
        public static string WriteDump(int processId, string path, bool full)
        {
            try
            {
                using (var process = Process.GetProcessById(processId))
                using (var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                {
                    // MiniDumpWithFullMemory = 2; MiniDumpWithThreadInfo | MiniDumpWithHandleData | MiniDumpWithUnloadedModules
                    uint type = full ? 0x2u : (0x1000u | 0x4u | 0x20u);
                    if (!MiniDumpWriteDump(process.Handle, (uint)processId, file.SafeFileHandle, type, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero))
                    {
                        return "MiniDumpWriteDump failed: error " + Marshal.GetLastWin32Error();
                    }
                }
                return null;
            }
            catch (Exception error)
            {
                return error.Message;
            }
        }

        /// <summary>
        /// Captures a window without the app's help: PrintWindow with PW_RENDERFULLCONTENT (works
        /// for DirectComposition content such as WinUI), falling back to the screen pixels.
        /// Returns null on success, or what went wrong.
        /// </summary>
        public static string CaptureWindow(long handle, string path)
        {
            try
            {
                var window = new IntPtr(handle);
                RECT rect;
                if (!GetWindowRect(window, out rect)) return "GetWindowRect failed";
                int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
                if (width <= 0 || height <= 0) return "the window has no size";
                using (var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb))
                {
                    bool printed;
                    using (var graphics = Graphics.FromImage(bitmap))
                    {
                        IntPtr hdc = graphics.GetHdc();
                        try { printed = PrintWindow(window, hdc, 2 /* PW_RENDERFULLCONTENT */); }
                        finally { graphics.ReleaseHdc(hdc); }
                    }
                    if (!printed || IsBlank(bitmap))
                    {
                        using (var graphics = Graphics.FromImage(bitmap))
                        {
                            graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, new Size(width, height));
                        }
                    }
                    bitmap.Save(path, ImageFormat.Png);
                }
                return null;
            }
            catch (Exception error)
            {
                return error.Message;
            }
        }

        /// <summary>The whole primary screen (evidence when Mosaic has no window to capture).</summary>
        public static string CaptureScreen(string path)
        {
            try
            {
                int width = GetSystemMetrics(0 /* SM_CXSCREEN */), height = GetSystemMetrics(1 /* SM_CYSCREEN */);
                if (width <= 0 || height <= 0) return "no screen";
                using (var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb))
                {
                    using (var graphics = Graphics.FromImage(bitmap))
                    {
                        graphics.CopyFromScreen(0, 0, 0, 0, new Size(width, height));
                    }
                    bitmap.Save(path, ImageFormat.Png);
                }
                return null;
            }
            catch (Exception error)
            {
                return error.Message;
            }
        }

        private static bool IsBlank(Bitmap bitmap)
        {
            return DistinctColours(bitmap, 24) <= 1;
        }

        /// <summary>Distinct colours on a grid of samples x samples points: 1 means a blank image.</summary>
        public static int DistinctColours(Bitmap bitmap, int samples)
        {
            var colours = new HashSet<int>();
            for (int i = 0; i < samples; i++)
            {
                for (int j = 0; j < samples; j++)
                {
                    int x = (int)((bitmap.Width - 1) * (i + 0.5) / samples);
                    int y = (int)((bitmap.Height - 1) * (j + 0.5) / samples);
                    colours.Add(bitmap.GetPixel(x, y).ToArgb());
                }
            }
            return colours.Count;
        }

        /// <summary>Width, height and sampled distinct colours of a PNG, or null if it cannot be read.</summary>
        public static int[] ImageStats(string path)
        {
            try
            {
                using (var bitmap = new Bitmap(path))
                {
                    return new int[] { bitmap.Width, bitmap.Height, DistinctColours(bitmap, 32) };
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static bool Activate(long handle) { return SetForegroundWindow(new IntPtr(handle)); }
        public static int ScreenWidth() { return GetSystemMetrics(0); }
        public static int ScreenHeight() { return GetSystemMetrics(1); }
        public static long Foreground() { return GetForegroundWindow().ToInt64(); }

        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);

        /// <summary>The window's DPI (96 = 100 %), or 0 where the API does not exist (before Windows 10 1607).</summary>
        public static int Dpi(long handle)
        {
            try { return (int)GetDpiForWindow(new IntPtr(handle)); }
            catch (EntryPointNotFoundException) { return 0; }
        }
    }
}
