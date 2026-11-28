using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace Mosaic.App.AutomationSupport;

/// <summary>
/// Saves a picture of a window as the user sees it, for the automation <c>screenshot</c> command.
/// </summary>
/// <remarks>
/// <para><c>PrintWindow</c> with <c>PW_RENDERFULLCONTENT</c> asks the desktop compositor for the
/// window's real content: WinUI's composition visuals, WebView2, and the Gecko child window
/// (another process's), and works while the window is covered by others. XAML's own
/// <c>RenderTargetBitmap</c> could not: it sees neither Gecko nor WebView2.</para>
/// <para>The PNG is written by hand (one IDAT, zlib from the BCL) so that capturing needs no
/// imaging library.</para>
/// </remarks>
internal static partial class WindowCapture
{
    private const uint PW_RENDERFULLCONTENT = 0x00000002;
    private const uint DIB_RGB_COLORS = 0;

    /// <summary>Captures the window and writes a PNG. Throws <see cref="IOException"/> on failure.</summary>
    public static void SavePng(nint window, string path)
    {
        if (window == 0 || !GetWindowRect(window, out var bounds))
        {
            throw new IOException("Mosaic's window is not available to capture.");
        }
        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        if (width <= 0 || height <= 0) throw new IOException("Mosaic's window has no size to capture.");

        var pixels = new byte[width * height * 4];
        var screen = GetDC(0);
        var memory = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, width, height);
        try
        {
            var previous = SelectObject(memory, bitmap);
            var captured = PrintWindow(window, memory, PW_RENDERFULLCONTENT);
            SelectObject(memory, previous);
            if (!captured) throw new IOException("Windows could not capture Mosaic's window.");

            var header = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height, // top-down
                biPlanes = 1,
                biBitCount = 32,
            };
            if (GetDIBits(memory, bitmap, 0, (uint)height, pixels, ref header, DIB_RGB_COLORS) == 0)
            {
                throw new IOException("The captured picture could not be read.");
            }
        }
        finally
        {
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(0, screen);
        }

        using var file = File.Create(path);
        WritePng(file, width, height, pixels);
    }

    /// <summary>Writes 32-bit BGRX pixels (top-down rows) as an 8-bit RGB PNG.</summary>
    internal static void WritePng(Stream output, int width, int height, byte[] bgrx)
    {
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 2; // colour type: RGB
        WriteChunk(output, "IHDR"u8, ihdr);

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[1 + width * 3]; // filter byte 0 (none), then RGB
            for (int y = 0; y < height; y++)
            {
                int source = y * width * 4;
                for (int x = 0, target = 1; x < width; x++, source += 4, target += 3)
                {
                    row[target] = bgrx[source + 2];
                    row[target + 1] = bgrx[source + 1];
                    row[target + 2] = bgrx[source];
                }
                zlib.Write(row);
            }
        }
        WriteChunk(output, "IDAT"u8, compressed.ToArray());
        WriteChunk(output, "IEND"u8, []);
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        output.Write(number);
        output.Write(type);
        output.Write(data);
        uint crc = Crc32(Crc32(0xFFFFFFFF, type), data) ^ 0xFFFFFFFF;
        BinaryPrimitives.WriteUInt32BigEndian(number, crc);
        output.Write(number);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
        // BITMAPINFO's colour table follows; none for 32-bit BI_RGB.
        public uint bmiColors;
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
    private static partial int GetDIBits(nint deviceContext, nint bitmap, uint start, uint lines,
        [Out] byte[] bits, ref BITMAPINFOHEADER info, uint usage);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint gdiObject);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(nint deviceContext);
}
