using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Mosaic.App;

/// <summary>
/// Saves a picture of a window as the user sees it, for the automation <c>screenshot</c> command.
/// </summary>
/// <remarks>
/// WPF's own rendering (<c>RenderTargetBitmap</c>) cannot be used: web content lives in child
/// windows owned by other processes, which WPF does not draw. <c>PrintWindow</c> with
/// <c>PW_RENDERFULLCONTENT</c> asks the desktop compositor for the window's real content, child
/// windows and GPU-rendered surfaces included, and works while the window is covered by others.
/// </remarks>
internal static partial class WindowCapture
{
    private const uint PW_RENDERFULLCONTENT = 0x00000002;

    public static void SavePng(Window window, string path)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0 || !GetWindowRect(handle, out var bounds))
        {
            throw new IOException("Mosaic's window is not available to capture.");
        }
        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        if (width <= 0 || height <= 0) throw new IOException("Mosaic's window has no size to capture.");

        var screen = GetDC(0);
        var memory = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, width, height);
        try
        {
            var previous = SelectObject(memory, bitmap);
            var captured = PrintWindow(handle, memory, PW_RENDERFULLCONTENT);
            SelectObject(memory, previous);
            if (!captured) throw new IOException("Windows could not capture Mosaic's window.");

            var image = Imaging.CreateBitmapSourceFromHBitmap(
                bitmap, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var file = File.Create(path);
            encoder.Save(file);
        }
        finally
        {
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(0, screen);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint window, out RECT bounds);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PrintWindow(nint window, nint deviceContext, uint flags);

    [LibraryImport("user32.dll")]
    private static partial nint GetDC(nint window);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint window, nint deviceContext);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleDC(nint deviceContext);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleBitmap(nint deviceContext, int width, int height);

    [LibraryImport("gdi32.dll")]
    private static partial nint SelectObject(nint deviceContext, nint gdiObject);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint gdiObject);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(nint deviceContext);
}
