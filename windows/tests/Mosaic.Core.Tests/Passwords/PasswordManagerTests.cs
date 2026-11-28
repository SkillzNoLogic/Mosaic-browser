using System.Text.Json.Nodes;
using Mosaic.Core.Browser;
using Mosaic.Core.Passwords;
using Mosaic.Core.Tests.Browser;

namespace Mosaic.Core.Tests.Passwords;

internal sealed class FakeChromiumPasswords : IChromiumPasswordBridge
{
    public bool IsScriptLoaded => true;
    public List<(BrowserTab Tab, string Origin, string? Username, string Password)> Fills { get; } = [];
    public int Result { get; set; } = 2;

    public Task<int> FillAsync(BrowserTab tab, SiteOrigin origin, string username, string password)
    {
        Fills.Add((tab, origin.String, username, password));
        return Task.FromResult(Result);
    }

    public Task<int> FillGeneratedAsync(BrowserTab tab, SiteOrigin origin, string password)
    {
        Fills.Add((tab, origin.String, null, password));
        return Task.FromResult(Result);
    }
}

/// <summary>The password manager against the fake engines: what the page script reports, what Mosaic asks and fills.</summary>
public sealed class PasswordManagerTests : IDisposable
{
    private readonly BrowserFixture fixture = new();
    private readonly PasswordManager passwords;
    private readonly FakeChromiumPasswords chromium = new();
    private readonly Queue<JsonObject> geckoEvents = new();

    public PasswordManagerTests()
    {
        var preferences = new PasswordPreferencesStore(fixture.PathFor("passwords-settings.json"));
        passwords = new PasswordManager(fixture.Model,
            () => new PasswordVault(new InMemoryPasswordStore(fixture.Time), preferences, fixture.Time),
            new PasswordUnlock(fixture.Time), fixture.Time) { Chromium = chromium };
        fixture.Gecko.Handlers["passwordsTake"] = _ =>
        {
            var events = new JsonArray();
            while (geckoEvents.TryDequeue(out var next)) events.Add(next);
            return JsonValue.Create(events.ToJsonString());
        };
        fixture.Gecko.Handlers["passwordsFill"] = _ => JsonValue.Create(2);
        fixture.Gecko.Handlers["passwordsFillGenerated"] = _ => JsonValue.Create(2);
        fixture.Model.UsePasswords(passwords);
    }

    public void Dispose() => fixture.Dispose();

    private const string Shop = "https://shop.test";

    /// <summary>Gecko reports page events for <paramref name="tab"/> (as the agent's actor does: origin, frame, tab, private).</summary>
    private void Gecko(BrowserTab tab, string json, string origin = Shop, string frame = "main", bool isPrivate = false)
    {
        var message = (JsonObject)JsonNode.Parse(json)!;
        message["origin"] = origin;
        message["frame"] = frame;
        message["tab"] = tab.GeckoId;
        message["private"] = isPrivate;
        geckoEvents.Enqueue(message);
        fixture.Gecko.ReportEvents([.. fixture.Model.Tabs.Where(t => t.GeckoId is not null).Select(t => Snapshots.Tab(t.GeckoId!.Value, t.Url, selected: t.IsSelected))], "passwords");
    }

    /// <summary>A sign-in: the login page, a submit, then the next page (the sign-in worked).</summary>
    private void SignIn(BrowserTab tab, string username, string password, string origin = Shop, bool isPrivate = false)
    {
        Gecko(tab, """{"kind":"page","doc":"login","login":true}""", origin, isPrivate: isPrivate);
        Gecko(tab, $$"""{"kind":"submit","doc":"login","username":"{{username}}","password":"{{password}}","mode":"login"}""", origin, isPrivate: isPrivate);
        Gecko(tab, """{"kind":"page","doc":"home","login":false}""", origin, isPrivate: isPrivate);
    }

    [Fact]
    public void StartsGeckoSupportWithItsOwnEventQueue()
    {
        fixture.OpenGeckoTab(Shop + "/login");
        var start = Assert.Single(fixture.Gecko.CallsTo("passwordsStart"));
        Assert.True(((JsonObject)start[0]!)["separateQueue"]!.GetValue<bool>());
    }

    [Fact]
    public void SaveThenQuietThenUpdate()
    {
        var tab = fixture.OpenGeckoTab(Shop + "/login");
        SignIn(tab, "alice", "pw1");
        var prompt = Assert.IsType<PasswordManager.Prompt.Save>(passwords.PromptFor(tab));
        Assert.IsType<SaveOffer.Save>(prompt.Offer);
        Assert.Equal("alice", prompt.Submission.Username);
        passwords.AcceptSave(tab);
        Assert.Equal("Password saved", fixture.Model.Notice);
        Assert.Equal("pw1", passwords.Vault.Password(new CredentialID(P.Origin(Shop), "alice")));
        Assert.Null(passwords.PromptFor(tab));

        // The same password again: a sign-in with a saved login, nothing to ask.
        Gecko(tab, """{"kind":"submit","doc":"home2","username":"alice","password":"pw1","confirmed":true}""");
        Assert.Null(passwords.PromptFor(tab));

        Gecko(tab, """{"kind":"submit","doc":"home3","username":"alice","password":"pw2","confirmed":true}""");
        Assert.IsType<SaveOffer.Update>(Assert.IsType<PasswordManager.Prompt.Save>(passwords.PromptFor(tab)).Offer);
        passwords.AcceptSave(tab);
        Assert.Equal("Password updated", fixture.Model.Notice);
        Assert.Equal("pw2", passwords.Vault.Password(new CredentialID(P.Origin(Shop), "alice")));
    }

    [Fact]
    public void AFailedSignInIsNotOffered()
    {
        var tab = fixture.OpenGeckoTab(Shop + "/login");
        Gecko(tab, """{"kind":"page","doc":"login","login":true}""");
        Gecko(tab, """{"kind":"submit","doc":"login","username":"alice","password":"wrong"}""");
        Assert.Null(passwords.PromptFor(tab));
        Assert.True(passwords.PageFor(tab)!.Pending is not null);
        // Back on the sign-in form of the same site.
        Gecko(tab, """{"kind":"page","doc":"login-again","login":true}""");
        Assert.Null(passwords.PromptFor(tab));
    }

    [Fact]
    public void CrossOriginFramesAreRefused()
    {
        var tab = fixture.OpenGeckoTab(Shop + "/login");
        Gecko(tab, """{"kind":"submit","doc":"frame","username":"alice","password":"pw","confirmed":true}""", "https://ads.test", frame: "cross");
        Assert.Null(passwords.PromptFor(tab));
        Assert.Null(passwords.LastSubmission);
        // A same-origin frame is fine.
        Gecko(tab, """{"kind":"submit","doc":"frame2","username":"alice","password":"pw","confirmed":true}""", frame: "same");
        Assert.IsType<PasswordManager.Prompt.Save>(passwords.PromptFor(tab));
    }

    [Fact]
    public void NeverOfferedInPrivateTabs()
    {
        var tab = fixture.OpenGeckoTab(Shop + "/login");
        // Gecko says the browsing context is private.
        SignIn(tab, "alice", "pw", isPrivate: true);
        Assert.Null(passwords.PromptFor(tab));

        // The privacy area's flag (or the automation stand-in) has the same effect.
        passwords.SimulatedPrivateTabs.Add(tab.Id);
        Gecko(tab, """{"kind":"submit","doc":"x","username":"alice","password":"pw","confirmed":true}""");
        Assert.Null(passwords.PromptFor(tab));
        Gecko(tab, """{"kind":"generate","doc":"x"}""");
        Assert.Null(passwords.PromptFor(tab));
    }

    [Fact]
    public void NeverForThisSite()
    {
        var tab = fixture.OpenGeckoTab(Shop + "/login");
        SignIn(tab, "alice", "pw");
        passwords.NeverSave(tab);
        Assert.True(passwords.Vault.IsNeverSave(P.Origin(Shop)));
        SignIn(tab, "bob", "pw2");
        Assert.Null(passwords.PromptFor(tab));
        Assert.Empty(passwords.Vault.Credentials);
    }

    [Fact]
    public void UsernameEditedInThePromptIsSaved()
    {
        var tab = fixture.OpenGeckoTab(Shop + "/login");
        SignIn(tab, "", "pw");
        passwords.AcceptSave(tab, "carol");
        Assert.Equal(["carol"], passwords.Vault.Credentials.Select(c => c.Username));
    }

    [Fact]
    public void TwoStepSignInRemembersTheUsername()
    {
        var tab = fixture.OpenGeckoTab(Shop + "/login");
        Gecko(tab, """{"kind":"identifier","doc":"step1","username":"dave@shop.test"}""");
        Gecko(tab, """{"kind":"submit","doc":"step2","username":"","password":"pw","confirmed":true}""");
        Assert.Equal("dave@shop.test", Assert.IsType<PasswordManager.Prompt.Save>(passwords.PromptFor(tab)).Submission.Username);
    }

    [Fact]
    public async Task FillsTheExactOriginInGecko()
    {
        var tab = fixture.OpenGeckoTab(Shop + "/login");
        passwords.Vault.Save(new CredentialID(P.Origin(Shop), "alice"), "pw1");
        passwords.Vault.Save(new CredentialID(P.Origin("https://www.shop.test"), "mallory"), "pw2");
        Gecko(tab, """{"kind":"page","doc":"login","login":true}""");
        Assert.True(passwords.OffersFill(tab));
        Assert.Single(passwords.Fillable(tab));

        await passwords.FillAsync(tab);
        var call = Assert.Single(fixture.Gecko.CallsTo("passwordsFill"));
        Assert.Equal([tab.GeckoId, Shop, "alice", "pw1"], call);
        Assert.Equal((Shop, 2), (passwords.LastFill!.Value.Origin, passwords.LastFill.Value.Count));
        Assert.NotNull(passwords.Vault.LastUsed(new CredentialID(P.Origin(Shop), "alice")));
    }

    [Fact]
    public async Task SeveralLoginsAskWhichOne()
    {
        var tab = fixture.OpenGeckoTab(Shop + "/login");
        passwords.Vault.Save(new CredentialID(P.Origin(Shop), "alice"), "a");
        passwords.Vault.Save(new CredentialID(P.Origin(Shop), "bob"), "b");
        await passwords.FillAsync(tab);
        var choose = Assert.IsType<PasswordManager.Prompt.Choose>(passwords.PromptFor(tab));
        Assert.Equal(2, choose.Credentials.Count);
        await passwords.FillAsync(tab, choose.Credentials.Single(c => c.Username == "bob"));
        Assert.Null(passwords.PromptFor(tab));
        Assert.Equal("b", fixture.Gecko.CallsTo("passwordsFill").Single()[3]);
    }

    [Fact]
    public async Task NothingToFillSaysSo()
    {
        var tab = fixture.OpenGeckoTab(Shop + "/login");
        await passwords.FillAsync(tab);
        Assert.Equal("No saved passwords for shop.test", fixture.Model.Notice);
        Assert.Empty(fixture.Gecko.CallsTo("passwordsFill"));
    }

    [Fact]
    public void FillShortcutFromGeckoFillsTheSelectedTab()
    {
        var tab = fixture.OpenGeckoTab(Shop + "/login");
        passwords.Vault.Save(new CredentialID(P.Origin(Shop), "alice"), "pw1");
        Gecko(tab, """{"kind":"fillShortcut","doc":"login"}""");
        Assert.Single(fixture.Gecko.CallsTo("passwordsFill"));
    }

    [Fact]
    public async Task GeneratorPromptFillsTheNewPasswordFields()
    {
        var tab = fixture.OpenGeckoTab(Shop + "/signup");
        Gecko(tab, """{"kind":"generate","doc":"signup"}""");
        var generate = Assert.IsType<PasswordManager.Prompt.Generate>(passwords.PromptFor(tab));
        Assert.Equal(20, generate.Password.Length);
        passwords.Regenerate(tab);
        var regenerated = Assert.IsType<PasswordManager.Prompt.Generate>(passwords.PromptFor(tab));
        Assert.NotEqual(generate.Password, regenerated.Password);
        await passwords.AcceptGeneratedAsync(tab);
        Assert.Equal([tab.GeckoId, Shop, regenerated.Password], fixture.Gecko.CallsTo("passwordsFillGenerated").Single());

        passwords.Vault.Update(p => p with { SuggestStrongPasswords = false });
        Gecko(tab, """{"kind":"generate","doc":"signup"}""");
        Assert.Null(passwords.PromptFor(tab));
    }

    [Fact]
    public async Task ChromiumPagesGoThroughTheBridge()
    {
        var (tab, _) = fixture.OpenChromiumTab(Shop + "/login");
        PasswordPageMessage Message(string json, string frame = "main")
        {
            var message = PasswordPageMessage.Decode(json)!;
            message.Origin = P.Origin(Shop);
            message.Frame = frame == "main" ? PasswordPageMessage.FrameKind.Main : PasswordPageMessage.FrameKind.CrossOrigin;
            return message;
        }

        passwords.HandleChromium(tab, Message("""{"kind":"submit","doc":"d","username":"alice","password":"pw","confirmed":true}""", "cross"));
        Assert.Null(passwords.PromptFor(tab));
        passwords.HandleChromium(tab, Message("""{"kind":"submit","doc":"d","username":"alice","password":"pw","confirmed":true}"""));
        passwords.AcceptSave(tab);
        Assert.Single(passwords.Vault.Credentials);

        // A page could post a fake shortcut; it never fills anything.
        passwords.HandleChromium(tab, Message("""{"kind":"fillShortcut"}"""));
        Assert.Empty(chromium.Fills);

        await passwords.FillSelectedAsync();
        Assert.Equal((Shop, "alice", "pw"), (chromium.Fills.Single().Origin, chromium.Fills.Single().Username, chromium.Fills.Single().Password));
    }

    [Fact]
    public async Task InPrivateChromiumPagesAreNeverOfferedButCanBeFilled()
    {
        var (tab, _) = fixture.OpenChromiumTab(Shop + "/login");
        var submit = PasswordPageMessage.Decode("""{"kind":"submit","doc":"d","username":"alice","password":"pw","confirmed":true}""")!;
        submit.Origin = P.Origin(Shop);
        submit.Frame = PasswordPageMessage.FrameKind.Main;
        passwords.HandleChromium(tab, submit, isPrivate: true);
        Assert.Null(passwords.PromptFor(tab));
        var generate = PasswordPageMessage.Decode("""{"kind":"generate","doc":"d"}""")!;
        generate.Origin = P.Origin(Shop);
        generate.Frame = PasswordPageMessage.FrameKind.Main;
        passwords.HandleChromium(tab, generate, isPrivate: true);
        Assert.Null(passwords.PromptFor(tab));

        passwords.Vault.Save(new CredentialID(P.Origin(Shop), "alice"), "pw");
        await passwords.FillAsync(tab);
        Assert.Single(chromium.Fills);
    }

    [Fact]
    public void ClosedTabsAreForgotten()
    {
        var tab = fixture.OpenGeckoTab(Shop + "/login");
        SignIn(tab, "alice", "pw");
        Assert.NotNull(passwords.PromptFor(tab));
        var other = fixture.OpenGeckoTab("https://other.test/");
        fixture.Model.Close(tab);
        Gecko(other, """{"kind":"page","doc":"o","login":false}""", "https://other.test");
        Assert.Null(passwords.PromptFor(tab));
        Assert.Null(passwords.PageFor(tab));
    }
}
