using System.Text.Json.Nodes;
using Microsoft.UI.Windowing;
using Mosaic.App.Features;
using Mosaic.Core.Browser;
using Windows.Graphics;

namespace Mosaic.App.AutomationSupport;

/// <summary>
/// The window's automation commands and <c>dump</c> key, so scripts can check the window on a
/// Windows runner (Windows-only; macOS has no counterpart).
/// </summary>
/// <remarks>
/// <code>
/// {"do": "window", "value": "maximize" | "minimize" | "restore" | "fullscreen" | "windowed" | "1024x768"}
/// {"do": "material", "value": "mica" | "micaAlt" | "acrylic" | "solid"}
/// {"do": "coverPage", "value": "on" | "off"}     // as an overlay would: Gecko's page must hide
/// {"do": "screenCapture", "value": "C:\\out.png"} // the window as the screen shows it
/// </code>
/// <c>dump</c> gains <c>winui</c>: the window's state, bounds and scale, the materials, the page
/// area's bounds and whether Gecko's page is showing, the active page covers and the shell in use.
/// </remarks>
internal static class WindowAutomation
{
    private const string CoverId = "automation";
    private static MainWindow? window;

    public static void Register(MainWindow mainWindow)
    {
        bool first = window is null;
        window = mainWindow;
        if (!first) return;
        AutomationRegistry.Register(HandleAsync);
        AutomationRegistry.RegisterState("winui", _ => State());
    }

    private static Task<AutomationReply?> HandleAsync(BrowserModel model, string command, string value, string? output)
    {
        if (window is not { } w) return Task.FromResult<AutomationReply?>(null);
        switch (command)
        {
            case "window":
                switch (value)
                {
                    case "maximize": w.SetState(OverlappedPresenterState.Maximized); break;
                    case "minimize": w.SetState(OverlappedPresenterState.Minimized); break;
                    case "restore": w.SetState(OverlappedPresenterState.Restored); break;
                    case "fullscreen": w.SetPageFullScreen(true); break;
                    case "windowed": w.SetPageFullScreen(false); break;
                    default:
                        var size = value.Split('x');
                        if (size.Length != 2 || !double.TryParse(size[0], out var width) || !double.TryParse(size[1], out var height))
                        {
                            return Reply(AutomationRegistry.Error("Expected maximize, minimize, restore, fullscreen, windowed or <width>x<height>."));
                        }
                        w.ResizeTo(width, height);
                        break;
                }
                return Reply(AutomationRegistry.Ok());
            case "material":
                if (!Enum.TryParse<WindowMaterial>(value, ignoreCase: true, out var material))
                {
                    return Reply(AutomationRegistry.Error("Expected mica, micaAlt, acrylic or solid."));
                }
                w.Material = material;
                return Reply(AutomationRegistry.Ok());
            case "coverPage":
                FeatureMounts.CoverPage(CoverId, value != "off");
                return Reply(AutomationRegistry.Ok());
            case "screenCapture":
                // The window's rectangle as the screen shows it (see WindowCapture.SaveScreenPng).
                if (value.Length == 0) return Reply(AutomationRegistry.Error("screenCapture needs the path to write to."));
                var handle = w.Handle;
                return Task.Run<AutomationReply?>(() =>
                {
                    WindowCapture.SaveScreenPng(handle, value);
                    return AutomationRegistry.Ok();
                });
            default:
                return Task.FromResult<AutomationReply?>(null);
        }
    }

    private static Task<AutomationReply?> Reply(AutomationReply reply) => Task.FromResult<AutomationReply?>(reply);

    private static JsonObject? State()
    {
        if (window is not { } w) return null;
        var area = w.ContentArea;
        var position = w.AppWindow.Position;
        var size = w.AppWindow.Size;
        var covers = new JsonArray();
        foreach (var cover in FeatureMounts.PageCovers) covers.Add(cover);
        return new JsonObject
        {
            ["state"] = w.IsPageFullScreen ? "fullscreen" : w.State.ToString().ToLowerInvariant(),
            ["bounds"] = Rect(new RectInt32(position.X, position.Y, size.Width, size.Height)),
            ["scale"] = w.Scale,
            ["material"] = w.Material.ToString(),
            ["effectiveMaterial"] = w.EffectiveMaterial.ToString(),
            ["captionButtonsWidth"] = w.CaptionButtonsWidth,
            ["titleBarHeight"] = w.TitleBarHeight,
            ["contentArea"] = Rect(area.PixelBounds),
            ["geckoShown"] = area.IsGeckoShown,
            ["geckoHostMode"] = area.Gecko.IsAttached ? area.Gecko.AttachedMode.ToString().ToLowerInvariant() : "",
            ["pageCovers"] = covers,
            ["shell"] = FeatureMounts.Shell is null ? "basic" : "feature",
            // Diagnostics: how the windows Gecko's page depends on are made (WS_EX_* bits).
            ["topLevelExStyle"] = $"0x{(long)Hosting.NativeMethods.GetWindowLongPtrW(w.Handle, Hosting.NativeMethods.GWL_EXSTYLE):X}",
            ["containerExStyle"] = $"0x{(long)Hosting.NativeMethods.GetWindowLongPtrW(area.Gecko.ContainerWindow, Hosting.NativeMethods.GWL_EXSTYLE):X}",
        };
    }

    private static JsonObject Rect(RectInt32 rect) => new()
    {
        ["x"] = rect.X,
        ["y"] = rect.Y,
        ["width"] = rect.Width,
        ["height"] = rect.Height,
    };
}
