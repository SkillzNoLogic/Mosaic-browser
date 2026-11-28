using System.Text.Json.Nodes;
using Mosaic.Core.Browser;
using Mosaic.Core.Gecko;
using Mosaic.Core.Permissions;

namespace Mosaic.Core.Engine;

public sealed partial class PageFeatures
{
    /// <summary>Snapshot event types whose details are fetched with the agent's <c>takePageEvents()</c>.</summary>
    private static readonly HashSet<string> DetailedEvents = ["permissionRequest", "permissionCancel", "popupBlocked", "pageFullscreen"];

    private bool takingEvents;
    private bool takeAgain;
    private int activityGeneration;

    /// <summary>
    /// Gecko is running: route its permission requests, blocked pop-ups and full screen to
    /// Mosaic (the agent's <c>configurePageFeatures</c>), tell it Mosaic's site decisions, and
    /// start comparing its zoom and mute with Mosaic's.
    /// </summary>
    private void GeckoStatusChanged()
    {
        var generation = ++activityGeneration;
        if (model.Gecko.Status != GeckoStatus.Running) return;
        _ = ConfigureGeckoAsync();
        _ = PollGeckoActivityAsync(generation);
    }

    private async Task ConfigureGeckoAsync()
    {
        try
        {
            // keepEvents: the details of page events are also kept for takePageEvents(), because
            // the Windows engine passes only each event's type on in its snapshots.
            await model.Gecko.CallAsync("configurePageFeatures", new JsonObject
            {
                ["fakeMedia"] = options.FakeMedia,
                ["keepEvents"] = true,
            });
            var entries = new JsonArray();
            foreach (var entry in Permissions.Entries().Where(entry => MirroredInGecko(entry.Permission)))
            {
                entries.Add(GeckoPermissionEntry(entry.Site, entry.Permission, entry.Decision));
            }
            if (entries.Count > 0) await model.Gecko.CallAsync("setSitePermissions", entries);
        }
        catch (Exception error) when (error is InvalidOperationException or RdpException)
        {
            // Gecko stopped again; the next start configures it.
        }
    }

    private void GeckoSnapshot(GeckoSnapshot snapshot)
    {
        var detailed = false;
        foreach (var type in snapshot.Events)
        {
            switch (type)
            {
                // Shortcuts pressed inside the Gecko window that open Mosaic's find bar.
                case "find":
                    ShowFindBar();
                    break;
                case "findNext" or "findPrevious":
                    FindNext(backwards: type == "findPrevious");
                    break;
                default:
                    detailed |= DetailedEvents.Contains(type);
                    break;
            }
        }
        if (detailed) _ = TakeGeckoEventsAsync();
    }

    /// <summary>Fetches the details of the page events Gecko reported, one fetch at a time.</summary>
    private async Task TakeGeckoEventsAsync()
    {
        if (takingEvents)
        {
            takeAgain = true;
            return;
        }
        takingEvents = true;
        try
        {
            do
            {
                takeAgain = false;
                JsonNode? events;
                try
                {
                    events = await model.Gecko.CallAsync("takePageEvents");
                }
                catch (Exception error) when (error is InvalidOperationException or RdpException)
                {
                    return;
                }
                if (events is not JsonArray list) return;
                foreach (var node in list)
                {
                    if (node is not JsonObject entry || entry["type"] is not JsonValue type || !type.TryGetValue(out string? name)) continue;
                    int? tab = entry["tab"] is JsonValue value && value.TryGetValue(out double number) ? (int)number : null;
                    var detail = entry["detail"] switch
                    {
                        JsonValue text when text.TryGetValue(out string? json) => Parse(json),
                        JsonObject inline => inline,
                        _ => null,
                    } ?? [];
                    HandleGeckoEvent(name, tab, detail);
                }
            }
            while (takeAgain);
        }
        finally
        {
            takingEvents = false;
        }
    }

    private static JsonObject? Parse(string? json)
    {
        try
        {
            return json is null ? null : JsonNode.Parse(json) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>One page event from the agent (see <c>postPageEvent</c> in agent.sys.mjs).</summary>
    internal void HandleGeckoEvent(string type, int? geckoTab, JsonObject detail)
    {
        switch (type)
        {
            case "permissionRequest":
                if (TabForGeckoId(geckoTab) is not { } tab || detail["id"] is not JsonValue idValue || !idValue.TryGetValue(out double id))
                {
                    return;
                }
                var request = new PermissionRequestInfo(
                    Text(detail["origin"]) ?? "",
                    [.. Strings(detail["kinds"]).Select(PermissionKinds.Parse).OfType<PermissionKind>()],
                    [.. Strings(detail["other"])]);
                HandlePermissionRequest(request, tab, (int)id);
                break;
            case "permissionCancel":
                if (detail["id"] is JsonValue cancelled && cancelled.TryGetValue(out double cancelledId)) CancelPermissionPrompt((int)cancelledId);
                break;
            case "popupBlocked":
                if (TabForGeckoId(geckoTab) is not { } opener) return;
                var count = Int(detail["count"]);
                PopupsBlocked(opener, [.. Strings(detail["urls"])], count > 0 ? count : 1, replacing: true);
                break;
            case "pageFullscreen":
                var entering = detail["value"] is JsonValue flag && flag.TryGetValue(out bool on) && on;
                var page = TabForGeckoId(geckoTab) ?? SelectedTab;
                if (entering) FullScreenTabId = page?.Id;
                else if (page is null || fullScreenTabId == page.Id) FullScreenTabId = null;
                break;
        }
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static IEnumerable<string> Strings(JsonNode? node) =>
        node is JsonArray array ? array.Select(Text).OfType<string>() : [];

    /// <summary>
    /// While Gecko runs and has tabs: its zoom and mute, every couple of seconds. Mosaic decides
    /// the mute; a zoom changed inside the Gecko window (its own Ctrl+wheel) is remembered too.
    /// </summary>
    private async Task PollGeckoActivityAsync(int generation)
    {
        while (generation == activityGeneration && model.Gecko.Status == GeckoStatus.Running)
        {
            await model.Delay(options.GeckoActivityInterval);
            if (generation != activityGeneration || model.Gecko.Status != GeckoStatus.Running) return;
            if (!model.Tabs.Any(tab => tab is { Engine: EngineKind.Gecko, GeckoId: not null })) continue;
            JsonNode? activity;
            try
            {
                activity = await model.Gecko.CallAsync("pageActivity");
            }
            catch (Exception error) when (error is InvalidOperationException or RdpException)
            {
                continue;
            }
            if (activity is JsonArray list) ApplyGeckoActivity(list);
        }
    }

    internal void ApplyGeckoActivity(JsonArray list)
    {
        foreach (var node in list)
        {
            if (node is not JsonObject entry || TabForGeckoId(Int(entry["id"])) is not { } tab) continue;
            var state = StateOf(tab);
            if (entry["muted"] is JsonValue mutedValue && mutedValue.TryGetValue(out bool muted) && muted != state.Muted)
            {
                // Mosaic decides; put Gecko back in line if something else changed it.
                ApplyMuteToEngine(tab, state.Muted);
            }
            if (entry["zoom"] is JsonValue zoomValue && zoomValue.TryGetValue(out double zoom)
                && Math.Abs(zoom - state.Zoom) > 0.005 && !tab.IsLoading
                && model.Now - state.ZoomAppliedAt > ZoomReportGrace)
            {
                // Zoomed inside the Gecko window (Ctrl+wheel, its own Ctrl+ / Ctrl-): remember that too.
                state.Zoom = zoom;
                ZoomStore.SetFactor(zoom, tab.Url);
                if (ReferenceEquals(tab, SelectedTab)) NotifyZoom();
            }
        }
    }
}
