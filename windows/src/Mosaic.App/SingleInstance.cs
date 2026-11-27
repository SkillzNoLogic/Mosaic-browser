using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Mosaic.App;

/// <summary>
/// Makes sure only one Mosaic runs on a given data folder: both engines keep a profile there
/// that cannot be shared between processes.
/// </summary>
/// <remarks>
/// The guard is a named mutex derived from the data folder, so a Mosaic pointed at a different
/// folder (<c>MOSAIC_DATA_DIR</c>, as tests do) is not affected. Create and dispose it on the
/// same thread: a mutex belongs to the thread that acquired it.
/// </remarks>
internal sealed partial class SingleInstance : IDisposable
{
    private const int SW_RESTORE = 9;

    private readonly Mutex mutex;

    public SingleInstance(string dataRoot)
    {
        // Windows paths are case-insensitive; two spellings of one folder must collide.
        var folder = Path.GetFullPath(dataRoot).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(folder)), 0, 8);
        mutex = new Mutex(initiallyOwned: true, $@"Local\Mosaic.Instance.{digest}", out var createdNew);
        IsFirst = createdNew;
    }

    /// <summary>True when no other Mosaic is running on the data folder.</summary>
    public bool IsFirst { get; }

    /// <summary>Brings the window of the Mosaic that is already running to the front.</summary>
    public static void ActivateRunningInstance()
    {
        using var current = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcessesByName(current.ProcessName))
        {
            using (process)
            {
                if (process.Id == current.Id || process.MainWindowHandle == 0) continue;
                if (IsIconic(process.MainWindowHandle)) ShowWindow(process.MainWindowHandle, SW_RESTORE);
                SetForegroundWindow(process.MainWindowHandle);
            }
        }
    }

    public void Dispose()
    {
        if (IsFirst) mutex.ReleaseMutex();
        mutex.Dispose();
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint window, int command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint window);
}
