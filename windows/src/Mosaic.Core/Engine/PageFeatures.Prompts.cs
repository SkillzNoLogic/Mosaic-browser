using System.Text.Json.Nodes;
using Mosaic.Core.Browser;
using Mosaic.Core.Gecko;
using Mosaic.Core.Permissions;

namespace Mosaic.Core.Engine;

public sealed partial class PageFeatures
{
    private readonly List<PermissionPrompt> prompts = [];
    private readonly List<PopupNotice> popupNotices = [];
    private readonly List<PageDialog> dialogs = [];
    /// <summary>Chromium pop-ups this area blocked before the model saw them: (tab, url).</summary>
    private readonly List<(Guid Tab, string Url)> suppressedPopups = [];

    public IReadOnlyList<PermissionPrompt> PermissionPrompts => prompts;
    public IReadOnlyList<PopupNotice> PopupNotices => popupNotices;
    public IReadOnlyList<PageDialog> Dialogs => dialogs;

    // ---- Permissions ----

    /// <summary>Chromium: a page asked for a permission (WebView2's PermissionRequested, deferred).</summary>
    public void ChromiumPermissionRequested(BrowserTab tab, PermissionRequestInfo request, IPermissionResponder responder) =>
        HandlePermissionRequest(request, tab, responder);

    /// <summary>Chromium: the page withdrew a request (navigated away, closed the frame).</summary>
    public void ChromiumPermissionCancelled(IPermissionResponder responder) => CancelPermissionPrompt(responder);

    private void HandlePermissionRequest(PermissionRequestInfo request, BrowserTab tab, object token)
    {
        if (request.AutomaticDecision(Permissions) is { } decision)
        {
            Respond(token, decision == SitePermissionDecision.Allow ? PermissionResponse.Allow : PermissionResponse.Deny, null, request.Site);
            return;
        }
        // WebView2 asks for the camera and the microphone of one getUserMedia call separately;
        // they are one question to the person.
        var pending = prompts.FirstOrDefault(prompt =>
            prompt.TabId == tab.Id && prompt.Engine == tab.Engine && prompt.Request.Origin == request.Origin
            && prompt.Request.IsRememberable == request.IsRememberable);
        if (pending is not null)
        {
            pending.Request = pending.Request.Merging(request);
            pending.Tokens.Add(token);
            Describe(pending);
            UpdateOverlays();
            return;
        }
        var prompt = new PermissionPrompt(tab.Id, tab.Engine, request, SitePermissionStore.Site(tab.Url));
        prompt.Tokens.Add(token);
        Describe(prompt);
        prompts.Add(prompt);
        UpdateOverlays();
    }

    /// <summary>Fills in what the prompt says besides the question: Windows' switches and engine limits.</summary>
    private void Describe(PermissionPrompt prompt)
    {
        var notes = new List<string>();
        var blocked = new List<WindowsCapability>();
        foreach (var kind in prompt.Request.Kinds)
        {
            if (WindowsPrivacy.CapabilityFor(kind) is not { } capability || blocked.Contains(capability)) continue;
            var access = options.WindowsAccess(capability);
            if (WindowsPrivacy.Explanation(capability, access) is not { } explanation) continue;
            blocked.Add(capability);
            notes.Add(explanation);
        }
        if (!prompt.Request.IsRememberable) notes.Add("This permission is asked for every time.");
        prompt.Notes = notes;
        prompt.BlockedByWindows = blocked;
    }

    /// <summary>
    /// The person's answer: Allow / Block are remembered for the site (when the request can be);
    /// null closes the prompt without deciding.
    /// </summary>
    public void Answer(PermissionPrompt prompt, SitePermissionDecision? decision)
    {
        if (!prompts.Remove(prompt)) return;
        var remembered = new List<SitePermission>();
        if (decision is { } value && prompt.Request.IsRememberable && prompt.Request.Site is { } site)
        {
            foreach (var permission in prompt.Request.Kinds.Select(kind => kind.StoredAs()).OfType<SitePermission>())
            {
                Permissions.Set(value, permission, site);
                remembered.Add(permission);
            }
        }
        var response = decision switch
        {
            SitePermissionDecision.Allow => PermissionResponse.Allow,
            SitePermissionDecision.Block => PermissionResponse.Deny,
            _ => PermissionResponse.Dismiss,
        };
        foreach (var token in prompt.Tokens)
        {
            Respond(token, response, decision is { } chosen ? (chosen, remembered) : null, prompt.Request.Site);
        }
        if (decision == SitePermissionDecision.Allow && prompt.BlockedByWindows.Count > 0)
        {
            // The site may use it as far as Mosaic is concerned; say plainly who still refuses.
            var nouns = string.Join(" and ", prompt.BlockedByWindows.Select(WindowsPrivacy.Noun));
            model.Notice = $"Allowed, but Windows is blocking the {nouns}. See Settings › Privacy & security.";
        }
        UpdateOverlays();
        if (prompt.TabId == SelectedTab?.Id) model.FocusContent();
    }

    /// <summary>Opens the Windows Settings page for the first device Windows blocks for this prompt.</summary>
    public void OpenWindowsSettings(PermissionPrompt prompt)
    {
        if (prompt.BlockedByWindows.Count > 0) Host?.OpenSettings(WindowsPrivacy.SettingsUri(prompt.BlockedByWindows[0]));
    }

    private void CancelPermissionPrompt(object token)
    {
        foreach (var prompt in prompts.Where(prompt => prompt.Tokens.Remove(token)).ToList())
        {
            if (prompt.Tokens.Count == 0) prompts.Remove(prompt);
        }
        UpdateOverlays();
    }

    private void Respond(
        object token, PermissionResponse response,
        (SitePermissionDecision Decision, List<SitePermission> Stored)? remembering, string? site)
    {
        switch (token)
        {
            case IPermissionResponder responder:
                responder.Respond(response);
                break;
            case int id:
                _ = AnswerGeckoAsync(id, response, remembering, site);
                break;
        }
    }

    private async Task AnswerGeckoAsync(
        int id, PermissionResponse response,
        (SitePermissionDecision Decision, List<SitePermission> Stored)? remembering, string? site)
    {
        try
        {
            // Gecko must know a remembered decision before the page hears the answer
            // (Notification.permission, repeat requests).
            if (remembering is { } remembered && site is not null)
            {
                var entries = new JsonArray();
                foreach (var permission in remembered.Stored.Where(MirroredInGecko))
                {
                    entries.Add(GeckoPermissionEntry(site, permission, remembered.Decision));
                }
                if (entries.Count > 0) await model.Gecko.CallAsync("setSitePermissions", entries);
            }
            await model.Gecko.CallAsync("answerPermission", id, response == PermissionResponse.Allow);
        }
        catch (Exception error) when (error is InvalidOperationException or RdpException)
        {
            // Gecko went away; the request went with it.
        }
    }

    private static bool MirroredInGecko(SitePermission permission) =>
        permission is SitePermission.Location or SitePermission.Notifications or SitePermission.Popups;

    private static JsonObject GeckoPermissionEntry(string site, SitePermission permission, SitePermissionDecision? decision) => new()
    {
        ["origin"] = site,
        ["permission"] = SitePermissionStore.Name(permission),
        ["decision"] = decision is { } value ? SitePermissionStore.Name(value) : null,
    };

    /// <summary>Keeps the engines' own memory in line with Mosaic's decisions (including edits in Privacy settings).</summary>
    private void PermissionChanged(SitePermissionChange change)
    {
        ChromiumPermissionMirror?.Invoke(change.Permission, change.Decision, change.Site);
        if (MirroredInGecko(change.Permission) && model.Gecko.Status == GeckoStatus.Running)
        {
            model.Gecko.Send("setSitePermissions", new JsonArray(GeckoPermissionEntry(change.Site, change.Permission, change.Decision)));
        }
    }

    // ---- Pop-ups ----

    /// <summary>
    /// Chromium: the page asked for a new window (WebView2's NewWindowRequested). A pop-up the
    /// person did not ask for is blocked (with a notice) unless the site is allowed pop-ups.
    /// </summary>
    public PopupVerdict ChromiumPopupRequested(BrowserTab tab, string url, bool userInitiated)
    {
        if (userInitiated) return PopupVerdict.Open;
        var site = SitePermissionStore.Site(tab.Url);
        switch (site is null ? null : Permissions.Decision(SitePermission.Popups, site))
        {
            case SitePermissionDecision.Allow:
                return PopupVerdict.Open;
            case SitePermissionDecision.Block:
                break; // blocked on purpose: no notice
            default:
                PopupsBlocked(tab, [url], 1, replacing: false);
                break;
        }
        suppressedPopups.Add((tab.Id, url));
        if (suppressedPopups.Count > 32) suppressedPopups.RemoveAt(0);
        return PopupVerdict.Block;
    }

    /// <summary>
    /// The model asks before it opens a tab for a Chromium page's new window: true when
    /// <see cref="ChromiumPopupRequested"/> blocked that very request.
    /// </summary>
    internal bool TakeSuppressedPopup(BrowserTab tab, string url)
    {
        var index = suppressedPopups.FindIndex(entry => entry.Tab == tab.Id && entry.Url == url);
        if (index < 0) return false;
        suppressedPopups.RemoveAt(index);
        return true;
    }

    /// <summary>Pop-ups were stopped on <paramref name="tab"/>'s page: a notice, unless the site has a decision.</summary>
    /// <param name="tab">The page that tried.</param>
    /// <param name="urls">Where they would have gone (may be empty).</param>
    /// <param name="count">How many (Gecko counts some it has no address for).</param>
    /// <param name="replacing">The engine reported the page's complete list (Gecko), not one more (Chromium).</param>
    private void PopupsBlocked(BrowserTab tab, IReadOnlyList<string> urls, int count, bool replacing)
    {
        var site = SitePermissionStore.Site(tab.Url);
        switch (site is null ? null : Permissions.Decision(SitePermission.Popups, site))
        {
            case SitePermissionDecision.Allow:
                OpenPopups(urls, tab);
                return;
            case SitePermissionDecision.Block:
                return;
        }
        var notice = popupNotices.FirstOrDefault(item => item.TabId == tab.Id);
        if (notice is null)
        {
            notice = new PopupNotice(tab.Id, tab.Engine, site);
            popupNotices.Add(notice);
        }
        else if (replacing)
        {
            notice.Urls.Clear();
            notice.Count = 0;
        }
        notice.Urls.AddRange(urls.Where(url => !notice.Urls.Contains(url)));
        notice.Count += count;
        UpdateOverlays();
    }

    /// <summary>"Open": opens the blocked pop-ups as tabs; <paramref name="alwaysAllow"/> also remembers the site.</summary>
    public void OpenPopups(PopupNotice notice, bool alwaysAllow)
    {
        popupNotices.Remove(notice);
        if (alwaysAllow && notice.Site is { } site) Permissions.Set(SitePermissionDecision.Allow, SitePermission.Popups, site);
        if (Tab(notice.TabId) is { } tab) OpenPopups(notice.Urls, tab);
        UpdateOverlays();
    }

    public void DismissPopups(PopupNotice notice)
    {
        popupNotices.Remove(notice);
        UpdateOverlays();
    }

    private void OpenPopups(IReadOnlyList<string> urls, BrowserTab tab)
    {
        if (tab.Engine == EngineKind.Chromium)
        {
            foreach (var url in urls.ToList()) model.NewTab(url, EngineKind.Chromium);
        }
        else if (tab.GeckoId is { } id)
        {
            // Gecko re-opens them itself (as tabs Mosaic then adopts), with their opener intact.
            model.Gecko.Send("openBlockedPopups", id);
        }
    }

    // ---- JavaScript dialogs (Chromium; Gecko draws its own inside the page area) ----

    /// <summary>Chromium: a page opened alert/confirm/prompt or asks before leaving (WebView2's ScriptDialogOpening, deferred).</summary>
    public void ChromiumDialogOpening(BrowserTab tab, PageDialogKind kind, string message, string defaultText, string pageUrl, IDialogResponder responder)
    {
        var site = SitePermissionStore.Site(pageUrl);
        var origin = site is null || site == "file://" ? "" : SitePermissionStore.DisplayName(site);
        dialogs.Add(new PageDialog(tab.Id, kind, message, defaultText, origin, responder));
        UpdateOverlays();
    }

    /// <summary>The person closed a dialog. <paramref name="text"/> is a prompt's answer.</summary>
    public void Finish(PageDialog dialog, bool accepted, string? text = null)
    {
        if (!dialogs.Remove(dialog)) return;
        dialog.Responder.Finish(accepted, dialog.Kind == PageDialogKind.Prompt && accepted ? text ?? "" : null);
        UpdateOverlays();
        if (dialog.TabId == SelectedTab?.Id) model.FocusContent();
    }

    // ---- Clean-up ----

    /// <summary>
    /// The page that asked has gone: its prompts are dismissed, its notices dropped. After a
    /// navigation only what belongs to other sites than <paramref name="currentSite"/> goes (a
    /// request the new page made at once must survive).
    /// </summary>
    private void DropPageState(Guid id, DropReason reason, string? currentSite = null)
    {
        bool Stale(Guid tab, string? site) => tab == id && (reason != DropReason.Navigated || site != currentSite);
        foreach (var prompt in prompts.Where(prompt => Stale(prompt.TabId, prompt.PageSite)).ToList())
        {
            prompts.Remove(prompt);
            foreach (var token in prompt.Tokens) Respond(token, PermissionResponse.Dismiss, null, null);
        }
        popupNotices.RemoveAll(notice => Stale(notice.TabId, notice.Site));
        suppressedPopups.RemoveAll(entry => entry.Tab == id && reason != DropReason.Navigated);
        if (reason != DropReason.Navigated)
        {
            // A navigation ends Chromium's dialogs itself.
            foreach (var dialog in dialogs.Where(dialog => dialog.TabId == id).ToList())
            {
                dialogs.Remove(dialog);
                dialog.Responder.Finish(false, null);
            }
        }
        UpdateOverlays();
    }

    /// <summary>Chromium: the page's dialogs were dismissed by the engine (a navigation).</summary>
    public void ChromiumDialogsReset(BrowserTab tab)
    {
        dialogs.RemoveAll(dialog => dialog.TabId == tab.Id);
        UpdateOverlays();
    }
}
