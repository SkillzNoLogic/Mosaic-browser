using System.Text.Json.Nodes;

namespace Mosaic.App.Features.System;

/// <summary>
/// Keeps an eye on virtual desktops (IVirtualDesktopManager): Gecko's window must stay on
/// Mosaic's desktop. In the normal mode Gecko's window is a child of Mosaic's, so it moves with
/// it by construction. In the owned-window fallback mode it is a separate top-level window owned
/// by Mosaic's; Windows keeps owned windows with their owner, and this checks that whenever
/// Mosaic's window is activated (switching back to its desktop activates it) — no polling.
/// </summary>
/// <remarks>
/// IVirtualDesktopManager can only move windows of the calling process, so a Gecko window on the
/// wrong desktop cannot be moved from here; it is reported (<c>dump</c> → <c>system.virtualDesktop</c>)
/// and <see cref="Mismatch"/> is raised so the winui area can hide or re-own it.
/// </remarks>
internal sealed class VirtualDesktops
{
    private IVirtualDesktopManager? manager;
    private bool unavailable;

    public event Action? Mismatch;

    public JsonObject Last { get; private set; } = new() { ["checked"] = false };

    /// <summary>Compares the desktops of Mosaic's window and Gecko's. UI thread.</summary>
    public JsonObject Check(nint mosaic, nint gecko)
    {
        var result = new JsonObject { ["checked"] = true };
        if (!Ensure())
        {
            result["status"] = "unavailable";
            return Last = result;
        }

        result["mosaic"] = Describe(mosaic);
        if (gecko == 0 || !SystemNative.IsWindow(gecko))
        {
            result["gecko"] = null;
            result["status"] = "no gecko window";
            return Last = result;
        }

        var root = SystemNative.GetAncestor(gecko, SystemNative.GA_ROOT);
        var mode = root == mosaic ? "child" : SystemNative.GetWindow(root, SystemNative.GW_OWNER) == mosaic ? "owned" : "separate";
        result["geckoMode"] = mode;
        result["gecko"] = Describe(root);

        var mosaicDesktop = DesktopOf(mosaic);
        var geckoDesktop = DesktopOf(root);
        var same = mode == "child" || (mosaicDesktop is not null && mosaicDesktop == geckoDesktop);
        result["sameDesktop"] = same;
        result["status"] = same ? "ok" : "mismatch";
        Last = result;
        if (!same) Mismatch?.Invoke();
        return result;
    }

    private bool Ensure()
    {
        if (manager is not null) return true;
        if (unavailable) return false;
        manager = SystemNative.Create<IVirtualDesktopManager>(SystemNative.CLSID_VirtualDesktopManager);
        unavailable = manager is null;
        return manager is not null;
    }

    private Guid? DesktopOf(nint window) =>
        manager!.GetWindowDesktopId(window, out var id) >= 0 ? id : null;

    private JsonObject Describe(nint window)
    {
        var json = new JsonObject { ["handle"] = $"0x{window:X}" };
        if (manager!.GetWindowDesktopId(window, out var id) is var hr && hr >= 0) json["desktop"] = id.ToString();
        else json["error"] = $"0x{hr:X8}";
        if (manager.IsWindowOnCurrentVirtualDesktop(window, out var current) >= 0) json["onCurrentDesktop"] = current;
        return json;
    }
}
