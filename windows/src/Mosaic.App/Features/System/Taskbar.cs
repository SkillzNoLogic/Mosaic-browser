using System.Text.Json.Nodes;
using Mosaic.Core.WindowsIntegration;

namespace Mosaic.App.Features.System;

/// <summary>
/// Shows <see cref="TaskbarProgress"/> on Mosaic's taskbar button (ITaskbarList3). Created on
/// the first progress change, on the UI thread; nothing happens at startup.
/// </summary>
internal sealed class Taskbar
{
    private readonly nint window;
    private ITaskbarList3? list;
    private bool failed;

    public Taskbar(nint window) => this.window = window;

    public string Status { get; private set; } = "idle";
    public string Applied { get; private set; } = "none";

    /// <summary>Applies the combined progress. UI thread only (the taskbar object is apartment-threaded).</summary>
    public void Apply()
    {
        var (state, value) = TaskbarProgress.Current;
        if (list is null && state == TaskbarProgressState.None) return;  // never created, nothing to clear
        if (!EnsureList()) return;

        var flags = state switch
        {
            TaskbarProgressState.Indeterminate => TaskbarFlags.Indeterminate,
            TaskbarProgressState.Normal => TaskbarFlags.Normal,
            TaskbarProgressState.Error => TaskbarFlags.Error,
            TaskbarProgressState.Paused => TaskbarFlags.Paused,
            _ => TaskbarFlags.NoProgress,
        };
        var hr = list!.SetProgressState(window, flags);
        if (hr >= 0 && state is TaskbarProgressState.Normal or TaskbarProgressState.Error or TaskbarProgressState.Paused)
            hr = list.SetProgressValue(window, (ulong)Math.Round(value * 1000), 1000);
        Status = hr >= 0 ? "ok" : $"failed: 0x{hr:X8}";
        Applied = hr >= 0 ? $"{state.ToString().ToLowerInvariant()}:{value:0.###}" : Applied;
    }

    private bool EnsureList()
    {
        if (list is not null) return true;
        if (failed) return false;
        list = SystemNative.Create<ITaskbarList3>(SystemNative.CLSID_TaskbarList);
        if (list is null || list.HrInit() < 0)
        {
            failed = true;
            list = null;
            Status = "unavailable";
            return false;
        }
        return true;
    }

    public JsonObject State()
    {
        var state = TaskbarProgress.State();
        state["applied"] = Applied;
        state["status"] = Status;
        return state;
    }
}
