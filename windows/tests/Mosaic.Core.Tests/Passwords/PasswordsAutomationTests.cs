using System.Text.Json.Nodes;
using Mosaic.Core.Browser;
using Mosaic.Core.Passwords;
using Mosaic.Core.Tests.Browser;

namespace Mosaic.Core.Tests.Passwords;

/// <summary>
/// The macOS command names and the <c>dump</c> section. Called directly rather than registered
/// with <see cref="AutomationRegistry"/>, which is process-wide: other tests check the exact
/// built-in <c>dump</c> keys.
/// </summary>
public sealed class PasswordsAutomationTests : IDisposable
{
    private readonly BrowserFixture fixture = new();
    private readonly PasswordManager passwords;

    public PasswordsAutomationTests()
    {
        var preferences = new PasswordPreferencesStore(fixture.PathFor("passwords-settings.json"));
        passwords = new PasswordManager(fixture.Model,
            () => new PasswordVault(new InMemoryPasswordStore(fixture.Time), preferences, fixture.Time),
            new PasswordUnlock(fixture.Time), fixture.Time);
        fixture.Model.UsePasswords(passwords);
        PasswordsAutomation.IsIsolated = () => true;
    }

    public void Dispose()
    {
        PasswordsAutomation.IsIsolated = () => PasswordProfile.IsIsolated;
        fixture.Dispose();
    }

    private async Task<JsonObject> Run(string command, string value = "", string? output = null)
    {
        if (command == "dump")
        {
            return new JsonObject { ["ok"] = true, ["state"] = new JsonObject { ["passwords"] = PasswordsAutomation.State(passwords, fixture.Model) } };
        }
        var reply = await PasswordsAutomation.HandleAsync(fixture.Model, command, value, output)
            ?? AutomationRegistry.Error($"Unknown command \"{command}\".");
        return (JsonObject)JsonNode.Parse(reply.Json)!;
    }

    [Fact]
    public async Task AddVerifyDeleteAndDumpWithoutSecrets()
    {
        Assert.True((await Run("passwordsAdd", "https://shop.test|alice|Sup3r-Secret!"))["ok"]!.GetValue<bool>());
        var verdict = fixture.PathFor("verify.txt");
        await Run("passwordsVerify", "shop.test|alice|Sup3r-Secret!", verdict);
        Assert.Equal("match", File.ReadAllText(verdict));
        await Run("passwordsVerify", "shop.test|alice|nope", verdict);
        Assert.Equal("mismatch", File.ReadAllText(verdict));
        await Run("passwordsVerify", "shop.test|bob|x", verdict);
        Assert.Equal("missing", File.ReadAllText(verdict));

        var dump = await Run("dump");
        var text = dump.ToJsonString();
        Assert.DoesNotContain("Sup3r-Secret", text);
        var state = dump["state"]!["passwords"]!;
        Assert.Equal(1, state["count"]!.GetValue<int>());
        Assert.Equal("alice", state["credentials"]![0]!["username"]!.GetValue<string>());
        Assert.Equal("memory", state["store"]!.GetValue<string>());

        await Run("passwordsDelete", "https://shop.test|alice");
        Assert.Empty(passwords.Vault.Credentials);
    }

    [Fact]
    public async Task WritingCommandsNeedAnIsolatedProfile()
    {
        PasswordsAutomation.IsIsolated = () => false;
        var reply = await Run("passwordsAdd", "https://shop.test|alice|pw");
        Assert.False(reply["ok"]!.GetValue<bool>());
        Assert.Contains("isolated profile", reply["error"]!.GetValue<string>());
        Assert.Empty(passwords.Vault.Credentials);
        Assert.False((await Run("passwordsExport", fixture.PathFor("x.csv")))["ok"]!.GetValue<bool>());
        Assert.False(File.Exists(fixture.PathFor("x.csv")));
    }

    [Fact]
    public async Task ImportExportAndAudit()
    {
        var csv = fixture.PathFor("in.csv");
        File.WriteAllText(csv, "name,url,username,password\na,https://a.test,u,letmein\nb,https://b.test,u,q3+fsU7i-WBTZbM=CF46\nc,https://c.test,u,q3+fsU7i-WBTZbM=CF46\n");
        var summary = fixture.PathFor("summary.txt");
        await Run("passwordsImport", csv, summary);
        Assert.Equal("added 3 updated 0 unchanged 0 failed 0 skipped 0", File.ReadAllText(summary));

        var audit = fixture.PathFor("audit.json");
        await Run("passwordsAudit", "", audit);
        Assert.Equal("{\"weak\": 1, \"reused\": 2}", File.ReadAllText(audit));

        var exported = fixture.PathFor("out.csv");
        await Run("passwordsExport", exported);
        Assert.Equal(PasswordCsv.Export(PasswordCsv.ParseExport(File.ReadAllText(csv)).Entries), File.ReadAllText(exported));
    }

    [Fact]
    public async Task OptionsNeverSaveAndPrivate()
    {
        await Run("passwordsOption", "offerToSave=off");
        Assert.False(passwords.Vault.Preferences.OfferToSave);
        await Run("passwordsNeverSave", "news.test|on");
        Assert.True(passwords.Vault.IsNeverSave(P.Origin("https://news.test")));
        var tab = fixture.OpenGeckoTab("https://shop.test/");
        await Run("passwordsPrivate", "on");
        Assert.True(passwords.IsPrivate(tab));
        var state = (await Run("dump"))["state"]!["passwords"]!;
        Assert.False(state["offerToSave"]!.GetValue<bool>());
        Assert.Equal("https://news.test", state["neverSave"]![0]!.GetValue<string>());
        Assert.True(state["tabs"]!.AsArray().Last()!["private"]!.GetValue<bool>());
        Assert.False((await Run("passwordsOption", "bogus=on"))["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task SuggestPromptsAndDumpShowsOnlyTheLength()
    {
        fixture.OpenGeckoTab("https://shop.test/signup");
        await Run("passwordsSuggest");
        var prompt = Assert.IsType<PasswordManager.Prompt.Generate>(passwords.PromptFor(fixture.Model.SelectedTab));
        var dump = await Run("dump");
        Assert.DoesNotContain(prompt.Password, dump.ToJsonString());
        var tabState = dump["state"]!["passwords"]!["tabs"]!.AsArray().Last()!;
        Assert.Equal("generate", tabState["prompt"]!.GetValue<string>());
        Assert.Equal(20, tabState["promptPasswordLength"]!.GetValue<int>());
        await Run("passwordsPrompt", "notnow");
        Assert.Null(passwords.PromptFor(fixture.Model.SelectedTab));
        Assert.False((await Run("passwordsPrompt", "save"))["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task OtherAreasCommandsAreNotOurs() =>
        Assert.Contains("Unknown command", (await Run("passwordz"))["error"]!.GetValue<string>());
}
