using System.Runtime.InteropServices;
using Mosaic.Core;
using Mosaic.Core.WindowsIntegration;

namespace Mosaic.App.Features.System;

/// <summary>
/// Mosaic's taskbar jump list (ICustomDestinationList): the tasks in <see cref="JumpListTasks"/>.
/// Works without package identity: each task is a shell link to Mosaic.exe with a switch, which
/// the single-instance channel forwards to the running window. Written on a background STA
/// thread a few seconds after start, and only when the tasks or the executable changed since the
/// last time (a fingerprint in the data folder), so ordinary launches do no shell work.
/// </summary>
internal static class JumpList
{
    private static readonly object gate = new();

    public static string Status { get; private set; } = "not written";

    private static string MarkerPath => Path.Combine(AppPaths.DataRoot, "System", "jumplist.txt");

    /// <summary>Writes the list in the background if it is out of date (or always, with <paramref name="force"/>).</summary>
    public static Task<string> UpdateAsync(bool force = false)
    {
        var done = new TaskCompletionSource<string>();
        var thread = new Thread(() =>
        {
            try
            {
                done.SetResult(Update(force));
            }
            catch (Exception error)
            {
                lock (gate) Status = "failed: " + error.Message;
                done.SetResult(Status);
            }
        })
        {
            IsBackground = true,
            Name = "Mosaic jump list",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    private static string Update(bool force)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("The executable path is unknown.");
        var fingerprint = JumpListTasks.Fingerprint(executable);
        if (!force && File.Exists(MarkerPath) && File.ReadAllText(MarkerPath).Trim() == fingerprint)
        {
            lock (gate) Status = "up to date";
            return Status;
        }

        var result = Write(executable);
        if (result == "written")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
            File.WriteAllText(MarkerPath, fingerprint);
        }
        lock (gate) Status = result;
        return result;
    }

    private static string Write(string executable)
    {
        var list = SystemNative.Create<ICustomDestinationList>(SystemNative.CLSID_DestinationList);
        if (list is null) return "unavailable";
        var hr = list.BeginList(out _, SystemNative.IID_IObjectArray, out var removed);
        if (removed != 0) Marshal.Release(removed);
        if (hr < 0) return $"failed: BeginList 0x{hr:X8}";

        var tasks = SystemNative.Create<IObjectCollection>(SystemNative.CLSID_EnumerableObjectCollection);
        if (tasks is null)
        {
            list.AbortList();
            return "failed: no object collection";
        }

        foreach (var task in JumpListTasks.Tasks)
        {
            var link = SystemNative.Create<IShellLinkW>(SystemNative.CLSID_ShellLink);
            if (link is null) continue;
            link.SetPath(executable);
            link.SetArguments(task.Arguments);
            link.SetDescription(task.Description);
            link.SetIconLocation(executable, task.IconIndex);
            if (link is IPropertyStore properties)
            {
                var title = PropVariant.FromString(task.Title);
                try
                {
                    properties.SetValue(SystemNative.PKEY_Title, title);
                    properties.Commit();
                }
                finally
                {
                    title.Free();
                }
            }
            var pointer = SystemNative.PointerFor(link);
            try { tasks.AddObject(pointer); }
            finally { Marshal.Release(pointer); }
        }

        var array = SystemNative.PointerFor(tasks);
        try
        {
            hr = list.AddUserTasks(array);
        }
        finally
        {
            Marshal.Release(array);
        }
        if (hr < 0)
        {
            list.AbortList();
            return $"failed: AddUserTasks 0x{hr:X8}";
        }
        hr = list.CommitList();
        return hr >= 0 ? "written" : $"failed: CommitList 0x{hr:X8}";
    }
}
