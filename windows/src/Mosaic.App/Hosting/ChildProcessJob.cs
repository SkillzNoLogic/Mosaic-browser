using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Mosaic.App.Hosting;

/// <summary>
/// Ties child processes to Mosaic's lifetime with a Windows job object: when Mosaic exits for any
/// reason, including a crash, every process added here (and its descendants) is terminated.
/// </summary>
/// <remarks>
/// Contract: thread-safe; <see cref="Add"/> never throws (failures return false).
/// <para>
/// How it works: the job is created with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>, and this
/// process holds the only handle to it (the handle is not inheritable). Windows closes that
/// handle when Mosaic's process ends, however it ends, and closing the last handle terminates
/// every process still in the job. For the same reason <see cref="Dispose"/> is not a passive
/// clean-up: it closes the handle, so it <b>terminates whatever is still in the job</b>. Stop the
/// engine first and dispose last. There is deliberately no finalizer: a job that is merely no
/// longer referenced must not kill the engine at an arbitrary garbage collection.
/// </para>
/// <para>What this does and does not cover, for whoever debugs a leftover <c>firefox.exe</c>:</para>
/// <list type="bullet">
/// <item>Descendants join the job only if they are <i>created after</i> their ancestor was
/// added. The process Mosaic launches may be Firefox's launcher process, which starts the real
/// browser process as its child (<c>browser/app/winlauncher/LauncherProcessWin.cpp</c>) and can
/// do so before <see cref="Add"/> runs. So add the launched process immediately, and add the
/// browser's own process ID (the one the agent reports) again once it is known; adding a
/// process that is already in the job is a no-op that returns true.</item>
/// <item>A child can leave a job only if it is created with <c>CREATE_BREAKAWAY_FROM_JOB</c> and
/// the job allows breakaway. This job does not allow it. Firefox additionally puts its browser
/// process in a job of its own when started with <c>--wait-for-browser</c>, and its sandbox uses
/// jobs for content processes; on Windows 8 and later those nest inside this one.</item>
/// <item><c>AssignProcessToJobObject</c> fails with access denied if the process already belongs
/// to a job that cannot be nested with this one, if it has already exited, or if it runs at a
/// higher integrity level than Mosaic. Mosaic's own minimum is Windows 10, where nesting is
/// available, so the realistic causes are the last two.</item>
/// </list>
/// </remarks>
public sealed class ChildProcessJob : IDisposable
{
    private readonly object _gate = new();
    private nint _job;
    private bool _disposed;

    public ChildProcessJob()
    {
        try
        {
            _job = CreateKillOnCloseJob();
        }
        catch (Exception error)
        {
            // Never fail Mosaic's start over this: without a job the engine is simply not
            // cleaned up automatically if Mosaic crashes.
            Trace.TraceError($"[ChildProcessJob] Could not create the job object: {error.Message}");
        }
    }

    /// <summary>Adds a running process to the job. Returns false (and traces why) if it could not be added.</summary>
    public bool Add(int processId)
    {
        try
        {
            lock (_gate)
            {
                if (_disposed || _job == 0 || processId <= 0)
                {
                    Trace.TraceWarning($"[ChildProcessJob] Process {processId} not added: " +
                        (_disposed ? "the job has been disposed." : _job == 0 ? "there is no job object." : "not a process ID."));
                    return false;
                }

                // PROCESS_SET_QUOTA and PROCESS_TERMINATE are what AssignProcessToJobObject
                // requires; the query right is for IsProcessInJob.
                nint process = NativeMethods.OpenProcess(
                    NativeMethods.PROCESS_SET_QUOTA | NativeMethods.PROCESS_TERMINATE |
                    NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION,
                    inheritHandle: false, (uint)processId);
                if (process == 0)
                {
                    Trace.TraceWarning($"[ChildProcessJob] Could not open process {processId}: {LastError()}");
                    return false;
                }

                try
                {
                    if (NativeMethods.IsProcessInJob(process, _job, out bool alreadyMember) && alreadyMember)
                    {
                        return true;
                    }
                    if (NativeMethods.AssignProcessToJobObject(_job, process))
                    {
                        return true;
                    }
                    Trace.TraceWarning(
                        $"[ChildProcessJob] Could not add process {processId} to the job: {LastError()} " +
                        "(access denied usually means it has exited, or already belongs to a job that cannot be nested).");
                    return false;
                }
                finally
                {
                    NativeMethods.CloseHandle(process);
                }
            }
        }
        catch (Exception error)
        {
            Trace.TraceError($"[ChildProcessJob] Adding process {processId} failed: {error.Message}");
            return false;
        }
    }

    /// <summary>Closes the job. Any process still in it is terminated by Windows at that moment.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            if (_job != 0)
            {
                NativeMethods.CloseHandle(_job);
                _job = 0;
            }
        }
    }

    private static nint CreateKillOnCloseJob()
    {
        // Unnamed, default security: nothing else can open it, and the handle is not inherited
        // by child processes (an inherited handle would keep the job alive after Mosaic dies).
        nint job = NativeMethods.CreateJobObjectW(0, null);
        if (job == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        var limits = new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        limits.BasicLimitInformation.LimitFlags = NativeMethods.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!NativeMethods.SetInformationJobObject(
                job, NativeMethods.JobObjectExtendedLimitInformation, in limits,
                (uint)Marshal.SizeOf<NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        {
            int error = Marshal.GetLastPInvokeError();
            NativeMethods.CloseHandle(job);
            throw new Win32Exception(error);
        }
        return job;
    }

    private static string LastError()
    {
        int error = Marshal.GetLastPInvokeError();
        return $"{new Win32Exception(error).Message} (Win32 error {error})";
    }
}
