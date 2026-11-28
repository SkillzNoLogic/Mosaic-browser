// Native helpers for Mosaic's performance harness (windows/scripts/perf), compiled by
// PerfHarness.psm1 with Add-Type. Windows PowerShell 5.1 compiles this with the .NET Framework's
// C# 5 compiler, so it sticks to C# 5: no string interpolation, no expression-bodied members,
// no out variables.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace MosaicPerf
{
    public sealed class ProcessSample
    {
        public int Id;
        public long CreationFileTime;
        public long CpuTicks;               // kernel + user, 100 ns units
        public long WorkingSetBytes;
        public long PrivateWorkingSetBytes; // -1 when Windows cannot tell (before Windows 10 1809)
        public long CommitBytes;            // private bytes (PrivateUsage)
        public string Error;
    }

    public static class Native
    {
        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const uint ProcessVmRead = 0x0010;
        private const int DwmwaCloaked = 14;

        [DllImport("kernel32.dll")]
        private static extern void GetSystemTimePreciseAsFileTime(out long fileTime);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryCountersEx2
        {
            public uint cb;
            public uint PageFaultCount;
            public UIntPtr PeakWorkingSetSize;
            public UIntPtr WorkingSetSize;
            public UIntPtr QuotaPeakPagedPoolUsage;
            public UIntPtr QuotaPagedPoolUsage;
            public UIntPtr QuotaPeakNonPagedPoolUsage;
            public UIntPtr QuotaNonPagedPoolUsage;
            public UIntPtr PagefileUsage;
            public UIntPtr PeakPagefileUsage;
            public UIntPtr PrivateUsage;
            public UIntPtr PrivateWorkingSetSize;
            public ulong SharedCommitUsage;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryCountersEx
        {
            public uint cb;
            public uint PageFaultCount;
            public UIntPtr PeakWorkingSetSize;
            public UIntPtr WorkingSetSize;
            public UIntPtr QuotaPeakPagedPoolUsage;
            public UIntPtr QuotaPagedPoolUsage;
            public UIntPtr QuotaPeakNonPagedPoolUsage;
            public UIntPtr QuotaNonPagedPoolUsage;
            public UIntPtr PagefileUsage;
            public UIntPtr PeakPagefileUsage;
            public UIntPtr PrivateUsage;
        }

        [DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo", SetLastError = true)]
        private static extern bool GetProcessMemoryInfoEx2(IntPtr process, ref MemoryCountersEx2 counters, uint size);

        [DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo", SetLastError = true)]
        private static extern bool GetProcessMemoryInfoEx(IntPtr process, ref MemoryCountersEx counters, uint size);

        private delegate bool EnumWindowsProc(IntPtr window, IntPtr data);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr data);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr window);

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr window, out Rect rect);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr window, uint command);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);

        [DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint milliseconds);

        [DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint milliseconds);

        // ---- clocks ---------------------------------------------------------------------------

        /// <summary>Now, as a FILETIME (100 ns since 1601, UTC) with sub-microsecond precision.</summary>
        public static long NowFileTime()
        {
            long now;
            GetSystemTimePreciseAsFileTime(out now);
            return now;
        }

        /// <summary>When Windows created the process, as a FILETIME, or 0 if it cannot be read.</summary>
        public static long CreationFileTime(int processId)
        {
            ProcessSample sample = Sample(processId, false);
            return sample == null ? 0 : sample.CreationFileTime;
        }

        // ---- per-process figures --------------------------------------------------------------

        /// <summary>CPU time, creation time and memory of one process; null if it is gone.</summary>
        public static ProcessSample Sample(int processId, bool memory)
        {
            IntPtr handle = OpenProcess(ProcessQueryLimitedInformation | (memory ? ProcessVmRead : 0), false, processId);
            if (handle == IntPtr.Zero && memory)
            {
                handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            }
            if (handle == IntPtr.Zero) return null;
            try
            {
                ProcessSample sample = new ProcessSample();
                sample.Id = processId;
                sample.PrivateWorkingSetBytes = -1;
                long creation, exit, kernel, user;
                if (GetProcessTimes(handle, out creation, out exit, out kernel, out user))
                {
                    if (exit != 0) return null;   // a process that has exited but whose handle is still held
                    sample.CreationFileTime = creation;
                    sample.CpuTicks = kernel + user;
                }
                if (!memory) return sample;

                MemoryCountersEx2 ex2 = new MemoryCountersEx2();
                ex2.cb = (uint)Marshal.SizeOf(typeof(MemoryCountersEx2));
                if (GetProcessMemoryInfoEx2(handle, ref ex2, ex2.cb))
                {
                    sample.WorkingSetBytes = (long)ex2.WorkingSetSize.ToUInt64();
                    sample.CommitBytes = (long)ex2.PrivateUsage.ToUInt64();
                    sample.PrivateWorkingSetBytes = (long)ex2.PrivateWorkingSetSize.ToUInt64();
                    return sample;
                }
                MemoryCountersEx ex = new MemoryCountersEx();
                ex.cb = (uint)Marshal.SizeOf(typeof(MemoryCountersEx));
                if (GetProcessMemoryInfoEx(handle, ref ex, ex.cb))
                {
                    sample.WorkingSetBytes = (long)ex.WorkingSetSize.ToUInt64();
                    sample.CommitBytes = (long)ex.PrivateUsage.ToUInt64();
                }
                else
                {
                    sample.Error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                }
                return sample;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        // ---- windows --------------------------------------------------------------------------

        /// <summary>
        /// A top-level window of the process that is visible, not cloaked by DWM (WinUI windows
        /// stay cloaked until their first frame is composed) and not empty; IntPtr.Zero if none.
        /// </summary>
        public static IntPtr ShownWindow(int processId)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows(delegate (IntPtr window, IntPtr data)
            {
                int owner;
                GetWindowThreadProcessId(window, out owner);
                if (owner != processId || !IsWindowVisible(window)) return true;
                if (GetWindow(window, 4 /* GW_OWNER */) != IntPtr.Zero) return true;
                int cloaked;
                if (DwmGetWindowAttribute(window, DwmwaCloaked, out cloaked, 4) == 0 && cloaked != 0) return true;
                Rect rect;
                if (!GetWindowRect(window, out rect) || rect.Right - rect.Left <= 0 || rect.Bottom - rect.Top <= 0) return true;
                found = window;
                return false;
            }, IntPtr.Zero);
            return found;
        }

        /// <summary>
        /// Polls every millisecond (with the timer resolution raised for this process only) until
        /// the process shows a window; returns the FILETIME at which it was seen, 0 on timeout,
        /// -1 if the process exited first.
        /// </summary>
        public static long WaitForShownWindow(System.Diagnostics.Process process, int timeoutMilliseconds)
        {
            timeBeginPeriod(1);
            try
            {
                long deadline = NowFileTime() + timeoutMilliseconds * 10000L;
                while (true)
                {
                    if (ShownWindow(process.Id) != IntPtr.Zero) return NowFileTime();
                    if (process.HasExited) return -1;
                    if (NowFileTime() > deadline) return 0;
                    Thread.Sleep(1);
                }
            }
            finally
            {
                timeEndPeriod(1);
            }
        }

        // ---- the file cache -------------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        private struct Luid { public uint Low; public int High; }

        [StructLayout(LayoutKind.Sequential)]
        private struct TokenPrivileges { public uint Count; public Luid Luid; public uint Attributes; }

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool LookupPrivilegeValue(string system, string name, out Luid luid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges state, uint length, IntPtr previous, IntPtr returned);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("ntdll.dll")]
        private static extern int NtSetSystemInformation(int infoClass, ref int info, int length);

        private static string EnablePrivilege(string name)
        {
            IntPtr token;
            if (!OpenProcessToken(GetCurrentProcess(), 0x0020 /* ADJUST_PRIVILEGES */ | 0x0008 /* QUERY */, out token))
            {
                return "OpenProcessToken: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;
            }
            try
            {
                TokenPrivileges privileges = new TokenPrivileges();
                privileges.Count = 1;
                privileges.Attributes = 0x2; // SE_PRIVILEGE_ENABLED
                if (!LookupPrivilegeValue(null, name, out privileges.Luid))
                {
                    return "LookupPrivilegeValue: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;
                }
                AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero);
                int error = Marshal.GetLastWin32Error();
                if (error != 0) return name + " is not held (run elevated): " + new Win32Exception(error).Message;
                return null;
            }
            finally
            {
                CloseHandle(token);
            }
        }

        /// <summary>
        /// Writes modified pages out and empties the standby list, so the next launch reads
        /// every file from disk as after a reboot. Needs an elevated process (CI runners are).
        /// Returns null on success, otherwise why it could not be done.
        /// </summary>
        public static string PurgeFileCache()
        {
            string problem = EnablePrivilege("SeProfileSingleProcessPrivilege");
            if (problem != null) return problem;
            const int SystemMemoryListInformation = 80;
            int flushModified = 3, purgeStandby = 4;
            int status = NtSetSystemInformation(SystemMemoryListInformation, ref flushModified, 4);
            if (status != 0) return "Flushing the modified list failed: NTSTATUS 0x" + status.ToString("X8");
            status = NtSetSystemInformation(SystemMemoryListInformation, ref purgeStandby, 4);
            if (status != 0) return "Purging the standby list failed: NTSTATUS 0x" + status.ToString("X8");
            return null;
        }
    }
}
