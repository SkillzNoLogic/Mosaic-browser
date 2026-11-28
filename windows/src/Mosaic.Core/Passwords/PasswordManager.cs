using System.Text.Json;
using System.Text.Json.Nodes;
using Mosaic.Core.Browser;
using Mosaic.Core.Gecko;

namespace Mosaic.Core.Passwords;

/// <summary>
/// The Chromium (WebView2) half of password filling, implemented by the app around each tab's
/// <c>CoreWebView2</c>. Fills only frames whose document is on the given origin.
/// </summary>
public interface IChromiumPasswordBridge
{
    /// <summary>Whether the shared page script was found and is injected into Chromium pages.</summary>
    bool IsScriptLoaded { get; }

    /// <summary>Fills a saved login. Resolves to the number of fields filled (0: nothing to fill, or not on that origin).</summary>
    Task<int> FillAsync(BrowserTab tab, SiteOrigin origin, string username, string password);

    /// <summary>Fills a generated password into the new-password fields.</summary>
    Task<int> FillGeneratedAsync(BrowserTab tab, SiteOrigin origin, string password);
}

/// <summary>
/// Mosaic's password manager: saved passwords in the vault, the save prompt after a sign-in,
/// filling on request, and the generator. One per <see cref="BrowserModel"/>
/// (<c>model.Passwords</c>). Port of <c>Sources/Mosaic/Features/Passwords/PasswordManager.swift</c>.
/// </summary>
/// <remarks>
/// Engines report what pages do through the shared page script (<c>passwords-page.js</c>):
/// Gecko through the agent's password events (<c>passwordsTake</c>), Chromium through the app's
/// WebView2 bridge (<see cref="HandleChromium"/>). Both arrive as <see cref="PasswordPageMessage"/>
/// with the origin established by the engine side. Single-threaded: the UI thread.
/// </remarks>
public sealed class PasswordManager
{
    /// <summary>What the overlay above the page is asking, per tab.</summary>
    public abstract record Prompt
    {
        /// <summary>Save (or update) a password just used to sign in or sign up.</summary>
        public sealed record Save(PasswordSubmission Submission, SaveOffer Offer) : Prompt;

        /// <summary>A new-password field has focus: offer a generated password.</summary>
        public sealed record Generate(SiteOrigin Origin, string Password) : Prompt;

        /// <summary>Several saved logins for the site: which one to fill.</summary>
        public sealed record Choose(SiteOrigin Origin, IReadOnlyList<PasswordCredential> Credentials) : Prompt;
    }

    /// <summary>What Mosaic knows about the page in a tab.</summary>
    internal sealed class PageState
    {
        public string? Document;
        public SiteOrigin? Origin;
        public bool HasLogin;
        public bool HasSignup;
        /// <summary>A submitted form whose page navigated away; judged when the next page reports.</summary>
        public Pending? Pending;
        /// <summary>The username of step one of a two-step sign-in.</summary>
        public (SiteOrigin Origin, string Username, DateTimeOffset Date)? Identifier;
    }

    internal sealed record Pending(PasswordSubmission Submission, string? Document, DateTimeOffset Date);

    private static readonly TimeSpan PendingLifetime = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan IdentifierLifetime = TimeSpan.FromMinutes(10);

    private readonly BrowserModel model;
    private readonly TimeProvider time;
    private readonly Func<PasswordVault> makeVault;
    private PasswordVault? vault;
    private readonly Dictionary<Guid, Prompt> prompts = [];
    private readonly Dictionary<Guid, PageState> pages = [];
    private bool attached;
    private bool geckoStarted;
    private bool takingGeckoEvents;
    private bool moreGeckoEvents;

    /// <param name="model">The browser whose tabs and engines this manager serves.</param>
    /// <param name="vault">Creates the vault on first use (it reads the store from disk).</param>
    /// <param name="unlock">Windows Hello / account check before secrets are shown.</param>
    /// <param name="time">Clock (tests pass a manual one).</param>
    /// <param name="storeDescription">"dpapi" or "memory", for <c>dump</c>.</param>
    public PasswordManager(BrowserModel model, Func<PasswordVault> vault, PasswordUnlock unlock, TimeProvider? time = null, string storeDescription = "memory")
    {
        this.model = model;
        makeVault = vault;
        Unlock = unlock;
        this.time = time ?? TimeProvider.System;
        StoreDescription = storeDescription;
    }

    /// <summary>Whether a tab is private. Set by the privacy area; saving is never offered in a private tab.</summary>
    public static Func<BrowserTab, bool> IsPrivateTab { get; set; } = _ => false;

    /// <summary>The saved passwords and the rules. Created (read from disk) on first use.</summary>
    public PasswordVault Vault => vault ??= makeVault();

    /// <summary>True once the vault has been read.</summary>
    public bool IsVaultLoaded => vault is not null;

    public PasswordUnlock Unlock { get; }

    /// <summary>"dpapi" or "memory", for <c>dump</c>.</summary>
    public string StoreDescription { get; }

    /// <summary>The app's WebView2 side; null until the app sets it (or when Chromium is unavailable).</summary>
    public IChromiumPasswordBridge? Chromium { get; set; }

    /// <summary>Bumped whenever prompts or the saved list change; views refresh on <see cref="Changed"/>.</summary>
    public int Revision { get; private set; }

    /// <summary>Raised (on the UI thread) after any change to prompts, pages or saved passwords.</summary>
    public event Action? Changed;

    /// <summary>Settings › Passwords &amp; Autofill was asked for (the app opens it).</summary>
    public event Action? SettingsRequested;

    /// <summary>For scripted checks: the last fill (never the password).</summary>
    public (string Origin, int Count, DateTimeOffset At)? LastFill { get; private set; }

    /// <summary>For scripted checks: the last submission (never the password).</summary>
    public (string Origin, string Username, string Mode, bool Confirmed)? LastSubmission { get; private set; }

    /// <summary>Tabs a scripted check marks as private (automation only).</summary>
    public HashSet<Guid> SimulatedPrivateTabs { get; } = [];

    /// <summary>Why Gecko password support could not start, if it could not.</summary>
    public string? GeckoError { get; private set; }

    // ---- Engines ----

    /// <summary>Connects to the Gecko engine. Idempotent; cheap (subscribes to events only).</summary>
    public void Attach()
    {
        if (attached) return;
        attached = true;
        model.Gecko.StatusChanged += GeckoStatusChanged;
        model.Gecko.SnapshotReceived += GeckoSnapshot;
        GeckoStatusChanged();
    }

    private void GeckoStatusChanged()
    {
        if (model.Gecko.Status != GeckoStatus.Running)
        {
            geckoStarted = false;
            return;
        }
        if (geckoStarted) return;
        geckoStarted = true;
        _ = StartGeckoAsync();
    }

    private async Task StartGeckoAsync()
    {
        try
        {
            // Password events in a queue of their own: this platform's snapshot reader keeps only event types.
            await model.Gecko.CallAsync("passwordsStart", new JsonObject { ["separateQueue"] = true });
            GeckoError = null;
        }
        catch (Exception error)
        {
            GeckoError = error.Message;
            model.Notice = $"Password saving is unavailable in Mozilla tabs: {error.Message}";
        }
    }

    private void GeckoSnapshot(GeckoSnapshot snapshot)
    {
        if (!snapshot.Events.Contains("passwords")) return;
        if (takingGeckoEvents)
        {
            moreGeckoEvents = true;
            return;
        }
        _ = TakeGeckoEventsAsync();
    }

    private async Task TakeGeckoEventsAsync()
    {
        takingGeckoEvents = true;
        try
        {
            do
            {
                moreGeckoEvents = false;
                JsonNode? result;
                try
                {
                    result = await model.Gecko.CallAsync("passwordsTake");
                }
                catch (Exception)
                {
                    return; // Gecko went away; its pages went with it.
                }
                foreach (var message in DecodeGeckoEvents(result)) HandleGecko(message);
            }
            while (moreGeckoEvents);
        }
        finally
        {
            takingGeckoEvents = false;
        }
    }

    internal static IEnumerable<PasswordPageMessage> DecodeGeckoEvents(JsonNode? result)
    {
        var json = result switch
        {
            JsonArray array => array.ToJsonString(),
            JsonValue value when value.TryGetValue(out string? text) => text,
            _ => null,
        };
        if (json is null) yield break;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            yield break;
        }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array) yield break;
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (PasswordPageMessage.Decode(element) is { } message) yield return message;
            }
        }
    }

    /// <summary>A message from a Gecko page (origin, frame, tab and privacy set by the agent).</summary>
    public void HandleGecko(PasswordPageMessage message)
    {
        if (message.Tab is not { } id) return;
        var tab = model.Tabs.FirstOrDefault(candidate => candidate.Engine == EngineKind.Gecko && candidate.GeckoId == id);
        if (tab is not null) Handle(message, tab);
    }

    /// <summary>
    /// A message from a Chromium page. The bridge sets <see cref="PasswordPageMessage.Origin"/> and
    /// <see cref="PasswordPageMessage.Frame"/> from WebView2's report of the sender.
    /// </summary>
    public void HandleChromium(BrowserTab tab, PasswordPageMessage message)
    {
        // A Chromium page can post anything to the bridge, so it could fake the shortcut and get
        // a fill without the user asking. Ctrl+\ reaches Mosaic's own key handling in Chromium tabs.
        if (message.Kind == PasswordPageMessage.MessageKind.FillShortcut) return;
        message.Tab = null;
        message.IsPrivate = null;
        if (!model.Tabs.Contains(tab) || tab.Engine != EngineKind.Chromium) return;
        Handle(message, tab);
    }

    public bool IsPrivate(BrowserTab tab) => IsPrivateTab(tab) || SimulatedPrivateTabs.Contains(tab.Id);

    private void Handle(PasswordPageMessage message, BrowserTab tab)
    {
        if (message.Origin is not { } origin
            || message.Frame is not (PasswordPageMessage.FrameKind.Main or PasswordPageMessage.FrameKind.SameOrigin))
        {
            return;
        }
        Prune();
        if (!pages.TryGetValue(tab.Id, out var page)) pages[tab.Id] = page = new PageState();

        switch (message.Kind)
        {
            case PasswordPageMessage.MessageKind.Page:
                if (message.Frame == PasswordPageMessage.FrameKind.Main)
                {
                    if (message.Document != page.Document)
                    {
                        // A new top-level document.
                        page.Document = message.Document;
                        page.Origin = origin;
                        page.HasLogin = false;
                        page.HasSignup = false;
                        if (prompts.GetValueOrDefault(tab.Id) is Prompt.Generate or Prompt.Choose) prompts.Remove(tab.Id);
                        if (page.Pending is { } pending && pending.Document != message.Document)
                        {
                            page.Pending = null;
                            // Back on a sign-in form of the same site: the sign-in failed.
                            var failed = origin == pending.Submission.Origin && message.HasLogin == true;
                            if (!failed) OfferToSave(pending.Submission, tab, message.IsPrivate == true);
                        }
                    }
                    page.HasLogin = message.HasLogin ?? false;
                    page.HasSignup = message.HasNewPassword ?? false;
                }
                else if (origin == page.Origin)
                {
                    page.HasLogin = page.HasLogin || message.HasLogin == true;
                    page.HasSignup = page.HasSignup || message.HasNewPassword == true;
                }
                break;

            case PasswordPageMessage.MessageKind.Submit:
                if (message.Submission is not { } submission) return;
                if (submission.Username.Length == 0 && page.Identifier is { } step && step.Origin == submission.Origin
                    && time.GetUtcNow() - step.Date < IdentifierLifetime)
                {
                    submission = submission with { Username = step.Username };
                }
                LastSubmission = (submission.Origin.String, submission.Username, submission.Mode.Id(), message.Confirmed == true);
                if (message.Confirmed == true)
                {
                    page.Pending = null;
                    OfferToSave(submission, tab, message.IsPrivate == true);
                }
                else
                {
                    var pending = new Pending(submission, message.Document, time.GetUtcNow());
                    page.Pending = pending;
                    // If no page follows (a download, a non-web page), forget it.
                    _ = ForgetPendingAsync(tab.Id, pending);
                }
                break;

            case PasswordPageMessage.MessageKind.Identifier:
                if (message.Username is { Length: > 0 } username) page.Identifier = (origin, username, time.GetUtcNow());
                break;

            case PasswordPageMessage.MessageKind.Generate:
                if (!Vault.Preferences.SuggestStrongPasswords || IsPrivate(tab) || message.IsPrivate == true) return;
                if (prompts.GetValueOrDefault(tab.Id) is Prompt.Save or Prompt.Generate) return;
                prompts[tab.Id] = new Prompt.Generate(origin, PasswordGenerator.Generate(Vault.Preferences.Generator));
                break;

            case PasswordPageMessage.MessageKind.FillShortcut:
                if (ReferenceEquals(tab, model.SelectedTab)) _ = FillAsync(tab);
                break;

            case PasswordPageMessage.MessageKind.Filled:
                LastFill = (origin.String, message.Count ?? 0, time.GetUtcNow());
                break;
        }
        Bump();
    }

    private async Task ForgetPendingAsync(Guid tabId, Pending pending)
    {
        await Task.Delay(PendingLifetime, time);
        if (pages.TryGetValue(tabId, out var state) && ReferenceEquals(state.Pending, pending))
        {
            state.Pending = null;
            Bump();
        }
    }

    private void OfferToSave(PasswordSubmission submission, BrowserTab tab, bool isPrivatePage)
    {
        SaveOffer? offer;
        try
        {
            offer = Vault.Offer(submission, IsPrivate(tab) || isPrivatePage);
        }
        catch (PasswordStoreException)
        {
            offer = null;
        }
        if (offer is not null) prompts[tab.Id] = new Prompt.Save(submission, offer);
    }

    /// <summary>Forgets tabs that have closed.</summary>
    private void Prune()
    {
        var open = model.Tabs.Select(tab => tab.Id).ToHashSet();
        foreach (var id in pages.Keys.Where(id => !open.Contains(id)).ToList()) pages.Remove(id);
        foreach (var id in prompts.Keys.Where(id => !open.Contains(id)).ToList()) prompts.Remove(id);
        SimulatedPrivateTabs.IntersectWith(open);
    }

    private void Bump()
    {
        Revision++;
        Changed?.Invoke();
    }

    // ---- Prompt answers ----

    public Prompt? PromptFor(BrowserTab? tab) => tab is not null && prompts.TryGetValue(tab.Id, out var prompt) ? prompt : null;

    internal PageState? PageFor(BrowserTab tab) => pages.GetValueOrDefault(tab.Id);

    public void DismissPrompt(BrowserTab tab)
    {
        if (prompts.Remove(tab.Id)) Bump();
    }

    /// <summary>Save / Update. <paramref name="username"/> is what the user typed in the prompt (save only).</summary>
    public void AcceptSave(BrowserTab tab, string? username = null)
    {
        if (prompts.GetValueOrDefault(tab.Id) is not Prompt.Save save) return;
        prompts.Remove(tab.Id);
        var (submission, offer) = (save.Submission, save.Offer);
        if (username is not null && username != submission.Username && offer is SaveOffer.Save)
        {
            submission = submission with { Username = username };
            var id = new CredentialID(submission.Origin, username);
            if (Vault.Contains(id)) offer = new SaveOffer.Update(id);
        }
        try
        {
            var outcome = Vault.Accept(offer, submission);
            model.Notice = outcome == SaveOutcome.Updated ? "Password updated" : "Password saved";
        }
        catch (PasswordStoreException error)
        {
            model.Notice = $"Couldn’t save the password: {error.Message}";
        }
        Bump();
    }

    /// <summary>Never for This Site.</summary>
    public void NeverSave(BrowserTab tab)
    {
        if (prompts.GetValueOrDefault(tab.Id) is not Prompt.Save save) return;
        prompts.Remove(tab.Id);
        Vault.SetNeverSave(true, save.Submission.Origin);
        Bump();
    }

    /// <summary>Use Password (generator prompt).</summary>
    public Task AcceptGeneratedAsync(BrowserTab tab)
    {
        if (prompts.GetValueOrDefault(tab.Id) is not Prompt.Generate generate) return Task.CompletedTask;
        prompts.Remove(tab.Id);
        Bump();
        return RunAsync(new Filling(null, generate.Password, Generated: true), tab, generate.Origin);
    }

    public void Regenerate(BrowserTab tab)
    {
        if (prompts.GetValueOrDefault(tab.Id) is not Prompt.Generate generate) return;
        prompts[tab.Id] = generate with { Password = PasswordGenerator.Generate(Vault.Preferences.Generator) };
        Bump();
    }

    // ---- Filling ----

    /// <summary>The tab's site, if it is a web page.</summary>
    public static SiteOrigin? OriginOf(BrowserTab? tab) => tab is null || tab.IsStartPage ? null : SiteOrigin.FromUrl(tab.Url);

    /// <summary>Saved logins that may be filled into <paramref name="tab"/> (its exact origin).</summary>
    public IReadOnlyList<PasswordCredential> Fillable(BrowserTab? tab) =>
        OriginOf(tab) is { } origin ? Vault.Matches(origin) : [];

    /// <summary>True when the tab's page shows a sign-in form and there is something to fill.</summary>
    public bool OffersFill(BrowserTab? tab) =>
        tab is not null && pages.TryGetValue(tab.Id, out var page) && page.HasLogin && page.Origin == OriginOf(tab)
        && Fillable(tab).Count > 0;

    /// <summary>
    /// Fills <paramref name="credential"/>, or the only saved login for the site, or asks which
    /// one. Only ever on the user's request (key button, Ctrl+\, menu, automation).
    /// </summary>
    public async Task FillAsync(BrowserTab? tab, PasswordCredential? credential = null)
    {
        if (tab is null || OriginOf(tab) is not { } origin) return;
        var matches = Vault.Matches(origin);
        var chosen = credential ?? (matches.Count == 1 ? matches[0] : null);
        if (chosen is null)
        {
            if (matches.Count == 0)
            {
                model.Notice = $"No saved passwords for {origin.DisplayName}";
            }
            else
            {
                prompts[tab.Id] = new Prompt.Choose(origin, matches);
                Bump();
            }
            return;
        }
        if (chosen.Origin != origin) return;
        if (prompts.GetValueOrDefault(tab.Id) is Prompt.Choose) prompts.Remove(tab.Id);
        string password;
        try
        {
            password = Vault.Password(chosen.Id);
        }
        catch (PasswordStoreException error)
        {
            model.Notice = $"Couldn’t read the password: {error.Message}";
            return;
        }
        Vault.MarkUsed(chosen.Id);
        Bump();
        await RunAsync(new Filling(chosen.Username, password, Generated: false), tab, origin);
    }

    /// <summary>The fill shortcut (Ctrl+\) or Edit › Fill Password for the selected tab.</summary>
    public Task FillSelectedAsync() => FillAsync(model.SelectedTab);

    private sealed record Filling(string? Username, string Password, bool Generated);

    private async Task RunAsync(Filling filling, BrowserTab tab, SiteOrigin origin)
    {
        int count;
        try
        {
            switch (tab.Engine)
            {
                case EngineKind.Chromium:
                    if (Chromium is not { } bridge || tab.Chromium is null) return;
                    count = filling.Generated
                        ? await bridge.FillGeneratedAsync(tab, origin, filling.Password)
                        : await bridge.FillAsync(tab, origin, filling.Username ?? "", filling.Password);
                    break;
                default:
                    if (tab.GeckoId is not { } id) return;
                    var result = filling.Generated
                        ? await model.Gecko.CallAsync("passwordsFillGenerated", id, origin.String, filling.Password)
                        : await model.Gecko.CallAsync("passwordsFill", id, origin.String, filling.Username ?? "", filling.Password);
                    count = result is JsonValue value && value.TryGetValue(out int number) ? number : 0;
                    break;
            }
        }
        catch (Exception)
        {
            count = 0;
        }
        LastFill = (origin.String, count, time.GetUtcNow());
        if (count == 0) model.Notice = "No sign-in form to fill on this page";
        Bump();
    }

    /// <summary>Edit › Suggest Strong Password: offers a generated password for the page's new-password field.</summary>
    public void OfferGeneratedPassword(BrowserTab? tab)
    {
        if (tab is null || OriginOf(tab) is not { } origin) return;
        prompts[tab.Id] = new Prompt.Generate(origin, PasswordGenerator.Generate(Vault.Preferences.Generator));
        Bump();
    }

    // ---- Manager actions (settings page) ----

    /// <summary>Re-reads the saved list and tells the views.</summary>
    public void NotifyChanged()
    {
        Vault.Reload();
        Bump();
    }

    public void OpenSettings() => SettingsRequested?.Invoke();

    public void ShowNotice(string text) => model.Notice = text;
}
