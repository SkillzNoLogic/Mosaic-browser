using System.Runtime.InteropServices;

namespace Mosaic.Core.Tabs;

/// <summary>Reads how hard memory is to come by right now.</summary>
public interface IMemoryPressureSource
{
    MemoryPressure Read();
}

/// <summary>
/// Windows memory pressure, polled by the sleeping-tabs check (cheap calls, no thread of its own):
/// <list type="bullet">
/// <item><b>critical</b> when the system's low-memory resource notification is signalled
/// (<c>CreateMemoryResourceNotification(LowMemoryResourceNotification)</c>) or physical memory is
/// at least 95 % in use;</item>
/// <item><b>warning</b> at 85 % or more (<c>GlobalMemoryStatusEx</c>'s memory load), or when less
/// than 10 % of the commit limit is left;</item>
/// <item>otherwise <b>normal</b>.</item>
/// </list>
/// Elsewhere (unit tests on macOS/Linux) it always reads normal.
/// </summary>
public sealed class MemoryPressureMonitor : IMemoryPressureSource, IDisposable
{
    private const int LowMemoryResourceNotification = 0;

    private nint lowMemoryHandle;
    private bool triedHandle;

    public MemoryPressure Read()
    {
        if (!OperatingSystem.IsWindows()) return MemoryPressure.Normal;
        try
        {
            if (!triedHandle)
            {
                triedHandle = true;
                lowMemoryHandle = CreateMemoryResourceNotification(LowMemoryResourceNotification);
            }
            if (lowMemoryHandle != 0 && QueryMemoryResourceNotification(lowMemoryHandle, out var low) && low)
            {
                return MemoryPressure.Critical;
            }
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (!GlobalMemoryStatusEx(ref status)) return MemoryPressure.Normal;
            return Classify(status.MemoryLoad, status.TotalPageFile, status.AvailablePageFile);
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            return MemoryPressure.Normal;
        }
    }

    /// <summary>The thresholds above, for tests.</summary>
    internal static MemoryPressure Classify(uint memoryLoadPercent, ulong commitLimit, ulong commitAvailable)
    {
        if (memoryLoadPercent >= 95) return MemoryPressure.Critical;
        if (memoryLoadPercent >= 85) return MemoryPressure.Warning;
        if (commitLimit > 0 && commitAvailable < commitLimit / 10) return MemoryPressure.Warning;
        return MemoryPressure.Normal;
    }

    public void Dispose()
    {
        if (lowMemoryHandle != 0 && OperatingSystem.IsWindows()) CloseHandle(lowMemoryHandle);
        lowMemoryHandle = 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateMemoryResourceNotification(int notificationType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryMemoryResourceNotification(nint handle, [MarshalAs(UnmanagedType.Bool)] out bool state);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
