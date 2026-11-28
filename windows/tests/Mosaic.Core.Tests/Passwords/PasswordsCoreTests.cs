using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.ClearScript.V8;
using Mosaic.Core.Passwords;

namespace Mosaic.Core.Tests.Passwords;

// Passwords (area: passwords): ports of Tests/MosaicCoreTests/PasswordsTests.swift, plus the
// Windows store. Nothing here touches a real profile.

internal static class P
{
    public static SiteOrigin Origin(string text) => SiteOrigin.FromUrl(text) ?? throw new ArgumentException(text);

    /// <summary>Deterministic generator (SplitMix64) for reproducible tests.</summary>
    public static Func<int, int> Seeded(ulong seed)
    {
        var state = seed;
        return max =>
        {
            state += 0x9E3779B97F4A7C15;
            var z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EB;
            z ^= z >> 31;
            return (int)(z % (ulong)max);
        };
    }

    public static string NewFolder() =>
        Path.Combine(AppContext.BaseDirectory, "test-data", "passwords-" + Guid.NewGuid().ToString("N"));
}

public sealed class SiteOriginTests
{
    [Fact]
    public void ParsesAndNormalises()
    {
        var o = P.Origin("HTTPS://Example.COM:443/login?x=1#y");
        Assert.Equal("https://example.com", o.String);
        Assert.Null(o.Port);
        Assert.Equal("http://127.0.0.1:8766", P.Origin("http://127.0.0.1:8766/").String);
        Assert.Equal("http://example.com", P.Origin("http://example.com:80").String);
        Assert.Equal("https://[::1]:8443", P.Origin("https://[::1]:8443/").String);
        Assert.Equal("https://example.com", P.Origin("https://example.com./").String);
        // Hosts in ASCII, as the engines report origins.
        Assert.Equal("https://xn--bcher-kva.example", P.Origin("https://bücher.example/").String);
    }

    [Fact]
    public void ExactMatchOnly()
    {
        var saved = P.Origin("https://example.com");
        Assert.Equal(saved, P.Origin("https://example.com/other/page"));
        Assert.NotEqual(saved, P.Origin("http://example.com"));
        Assert.NotEqual(saved, P.Origin("https://www.example.com"));
        Assert.NotEqual(saved, P.Origin("https://example.com:8443"));
        Assert.NotEqual(saved, P.Origin("https://sub.example.com"));
        Assert.NotEqual(saved, P.Origin("https://example.com.evil.test"));
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("about:blank")]
    [InlineData("javascript:alert(1)")]
    [InlineData("android://hash@com.example/")]
    [InlineData("mosaic://history")]
    [InlineData("https://")]
    [InlineData("http://example.com:99999")]
    [InlineData("localhost:8080")]
    [InlineData("")]
    public void RejectsNonWebAddresses(string url) => Assert.Null(SiteOrigin.FromUrl(url));

    [Fact]
    public void UserInputDefaultsToHttps()
    {
        Assert.Equal("https://example.com", SiteOrigin.FromUserInput("example.com")?.String);
        Assert.Equal("https://example.com", SiteOrigin.FromUserInput(" example.com/login ")?.String);
        Assert.Equal("https://localhost:8080", SiteOrigin.FromUserInput("localhost:8080")?.String);
        Assert.Equal("http://intranet.test", SiteOrigin.FromUserInput("http://intranet.test")?.String);
        Assert.Null(SiteOrigin.FromUserInput(""));
    }

    [Fact]
    public void DisplayNameFlagsPlainHttp()
    {
        Assert.Equal("example.com", P.Origin("https://example.com").DisplayName);
        Assert.Equal("example.com:8443", P.Origin("https://example.com:8443").DisplayName);
        Assert.Equal("http://example.com", P.Origin("http://example.com").DisplayName);
    }

    [Fact]
    public void JsonAsString()
    {
        var json = JsonSerializer.Serialize(new[] { P.Origin("https://a.test:444") });
        Assert.Equal("""["https://a.test:444"]""", json);
        // macOS writes "/" escaped; both read the same.
        Assert.Equal([P.Origin("https://a.test:444")], JsonSerializer.Deserialize<SiteOrigin[]>("""["https:\/\/a.test:444"]""")!);
    }

    [Fact]
    public void OrdersByHostThenSchemeThenPort()
    {
        var sorted = new[] { P.Origin("https://b.test"), P.Origin("https://a.test:8443"), P.Origin("http://a.test") }.Order().ToList();
        Assert.Equal(["http://a.test", "https://a.test:8443", "https://b.test"], sorted.Select(o => o.String));
    }
}

public sealed class PasswordGeneratorTests
{
    [Fact]
    public void DefaultIsStrongAndUsesEverySet()
    {
        var random = P.Seeded(1);
        for (var i = 0; i < 200; i++)
        {
            var password = PasswordGenerator.Generate(PasswordGeneratorOptions.Default, random);
            Assert.Equal(20, password.Length);
            Assert.Contains(password, char.IsLower);
            Assert.Contains(password, char.IsUpper);
            Assert.Contains(password, char.IsDigit);
            Assert.Contains(password, c => PasswordGeneratorOptions.SymbolSet.Contains(c));
            Assert.DoesNotContain(password, c => "l1IO0".Contains(c));
            Assert.Equal(PasswordStrength.Strong, PasswordStrengths.Evaluate(password));
        }
        Assert.True(PasswordGeneratorOptions.Default.EntropyBits > 110);
    }

    [Fact]
    public void RespectsOptions()
    {
        var random = P.Seeded(2);
        var pin = PasswordGenerator.Generate(new PasswordGeneratorOptions { Length = 12, Lowercase = false, Uppercase = false, Symbols = false, AvoidAmbiguous = false }, random);
        Assert.Equal(12, pin.Length);
        Assert.All(pin, c => Assert.True(char.IsDigit(c)));
        var none = new PasswordGeneratorOptions { Length = 10, Lowercase = false, Uppercase = false, Digits = false, Symbols = false };
        Assert.All(PasswordGenerator.Generate(none, random), c => Assert.True(char.IsLower(c)));
    }

    [Fact]
    public void ClampsLength()
    {
        var random = P.Seeded(3);
        Assert.Equal(8, PasswordGenerator.Generate(new PasswordGeneratorOptions { Length = 2 }, random).Length);
        Assert.Equal(128, PasswordGenerator.Generate(new PasswordGeneratorOptions { Length = 1000 }, random).Length);
    }

    [Fact]
    public void SystemGeneratorDoesNotRepeat() =>
        Assert.Equal(100, Enumerable.Range(0, 100).Select(_ => PasswordGenerator.Generate()).Distinct().Count());
}

public sealed class PasswordStrengthTests
{
    [Fact]
    public void Classifies()
    {
        Assert.Equal(PasswordStrength.Weak, PasswordStrengths.Evaluate("password"));
        Assert.Equal(PasswordStrength.Weak, PasswordStrengths.Evaluate("Password1!"));
        Assert.Equal(PasswordStrength.Weak, PasswordStrengths.Evaluate("12345678"));
        Assert.Equal(PasswordStrength.Weak, PasswordStrengths.Evaluate("aaaaaaaaaaaa"));
        Assert.Equal(PasswordStrength.Weak, PasswordStrengths.Evaluate("abcdefgh"));
        Assert.Equal(PasswordStrength.Weak, PasswordStrengths.Evaluate("short"));
        Assert.True(PasswordStrengths.Evaluate("Kp7#qz2!Lm") >= PasswordStrength.Fair);
        Assert.Equal(PasswordStrength.Strong, PasswordStrengths.Evaluate("q3+fsU7i-WBTZbM=CF46"));
    }

    [Fact]
    public void AuditFindsWeakAndReused()
    {
        var a = new CredentialID(P.Origin("https://a.test"), "u");
        var b = new CredentialID(P.Origin("https://b.test"), "u");
        var c = new CredentialID(P.Origin("https://c.test"), "u");
        var audit = PasswordAudit.Run(new Dictionary<CredentialID, string> { [a] = "q3+fsU7i-WBTZbM=CF46", [b] = "q3+fsU7i-WBTZbM=CF46", [c] = "letmein" });
        Assert.Equal([a, b], audit.Reused.Order());
        Assert.Equal([c], audit.Weak);
    }
}

public sealed class PasswordCsvTests
{
    [Fact]
    public void ParsesRfc4180()
    {
        var rows = PasswordCsv.Parse("\uFEFFa,b,c\r\n\"x,1\",\"he said \"\"hi\"\"\",\"line\nbreak\"\n\n,,\n");
        Assert.Equal(3, rows.Count);
        Assert.Equal(["a", "b", "c"], rows[0]);
        Assert.Equal(["x,1", "he said \"hi\"", "line\nbreak"], rows[1]);
        Assert.Equal(["", "", ""], rows[2]);
    }

    [Fact]
    public void ImportsChromeAndEdge()
    {
        var result = PasswordCsv.ParseExport("name,url,username,password,note\nexample.com,https://example.com/login,alice,s3cret,\nbad,android://x@com.app/,bob,pw,\n");
        Assert.Equal([new PasswordCsv.Entry(P.Origin("https://example.com"), "alice", "s3cret")], result.Entries);
        Assert.Equal([3], result.Skipped.Select(s => s.Line));
    }

    [Fact]
    public void ImportsFirefoxSkippingHttpAuth()
    {
        var text = """
            "url","username","password","httpRealm","formActionOrigin","guid","timeCreated","timeLastUsed","timePasswordChanged"
            "https://example.com","alice","pw1","","https://example.com","{a}","1","1","1"
            "https://example.com","bob","pw2","Admin area","","{b}","1","1","1"
            "https://example.com","carol","","","","{c}","1","1","1"
            """;
        var result = PasswordCsv.ParseExport(text);
        Assert.Equal(["alice"], result.Entries.Select(e => e.Username));
        Assert.Equal(2, result.Skipped.Count);
    }

    [Fact]
    public void ImportsSafari()
    {
        var result = PasswordCsv.ParseExport("Title,URL,Username,Password,Notes,OTPAuth\nExample,https://example.com:8443/,alice,\"a,\"\"b\",,\n");
        Assert.Equal([new PasswordCsv.Entry(P.Origin("https://example.com:8443"), "alice", "a,\"b")], result.Entries);
    }

    [Fact]
    public void RejectsFilesWithoutColumns()
    {
        Assert.Equal(PasswordCsv.ImportErrorKind.MissingColumns, Assert.Throws<PasswordCsv.ImportException>(() => PasswordCsv.ParseExport("a,b\n1,2\n")).Kind);
        Assert.Equal(PasswordCsv.ImportErrorKind.Empty, Assert.Throws<PasswordCsv.ImportException>(() => PasswordCsv.ParseExport("")).Kind);
    }

    [Fact]
    public void SkipsDuplicates()
    {
        var result = PasswordCsv.ParseExport("url,username,password\nhttps://a.test,u,1\nhttps://a.test/x,u,2\n");
        Assert.Single(result.Entries);
        Assert.Contains("duplicate", result.Skipped[0].Reason);
    }

    [Fact]
    public void ExportRoundTripsInMacFormat()
    {
        PasswordCsv.Entry[] entries =
        [
            new(P.Origin("https://b.test"), "bob", "with,comma \"and quotes\"\nnewline"),
            new(P.Origin("http://a.test:8080"), "", " spaced "),
        ];
        var text = PasswordCsv.Export(entries);
        // Exactly what Mosaic for macOS writes: Chrome's header, sorted by site, LF line ends.
        Assert.Equal("name,url,username,password\na.test,http://a.test:8080,,\" spaced \"\nb.test,https://b.test,bob,\"with,comma \"\"and quotes\"\"\nnewline\"\n", text);
        var back = PasswordCsv.ParseExport(text);
        Assert.Equal(entries.Select(e => e.Id).ToHashSet(), back.Entries.Select(e => e.Id).ToHashSet());
        Assert.Equal(entries[0].Password, back.Entries.Single(e => e.Username == "bob").Password);
        Assert.Equal(" spaced ", back.Entries.Single(e => e.Username == "").Password);
    }
}

/// <summary>A reversible stand-in for DPAPI, so the vault's format is tested on any OS.</summary>
internal sealed class FakeProtector : ISecretProtector
{
    private static readonly byte[] Header = "FAKEDPAPI"u8.ToArray();
    public int Calls { get; private set; }

    public byte[] Protect(byte[] data)
    {
        Calls++;
        return [.. Header, .. data.Select(b => (byte)(b ^ 0x5A))];
    }

    public byte[] Unprotect(byte[] data)
    {
        Calls++;
        if (data.Length < Header.Length || !data.AsSpan(0, Header.Length).SequenceEqual(Header)) throw new CryptographicException("not ours");
        return [.. data.Skip(Header.Length).Select(b => (byte)(b ^ 0x5A))];
    }
}

/// <summary>A fact that only runs on Windows (real DPAPI).</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Needs Windows (DPAPI).";
    }
}

public sealed class PasswordStoreTests : IDisposable
{
    private readonly string folder = P.NewFolder();

    public void Dispose()
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }

    private static void Crud(IPasswordStore store)
    {
        var id = new CredentialID(P.Origin("https://a.test"), "u");
        store.Add(id, "p1");
        Assert.Equal(PasswordStoreErrorKind.Duplicate, Assert.Throws<PasswordStoreException>(() => store.Add(id, "p2")).Kind);
        Assert.Equal("p1", store.Password(id));
        store.Update(id, "v", "p2");
        var renamed = id with { Username = "v" };
        Assert.Equal(PasswordStoreErrorKind.NotFound, Assert.Throws<PasswordStoreException>(() => store.Password(id)).Kind);
        Assert.Equal("p2", store.Password(renamed));
        Assert.Equal([renamed], store.Credentials().Select(c => c.Id));
        // Usernames that differ only in case are different logins (Credential Manager could not keep both).
        var upper = id with { Username = "V" };
        store.Add(upper, "p3");
        Assert.Equal("p2", store.Password(renamed));
        Assert.Equal("p3", store.Password(upper));
        store.Delete(upper);
        store.Delete(renamed);
        Assert.Empty(store.Credentials());
        Assert.Equal(PasswordStoreErrorKind.NotFound, Assert.Throws<PasswordStoreException>(() => store.Delete(renamed)).Kind);
    }

    [Fact]
    public void InMemoryStoreCrud() => Crud(new InMemoryPasswordStore());

    [Fact]
    public void VaultFileStoreCrudAndPersistsEncrypted()
    {
        var path = Path.Combine(folder, "vault.dat");
        var protector = new FakeProtector();
        Crud(new DpapiPasswordStore(path, protector));

        var store = new DpapiPasswordStore(path, protector);
        var id = new CredentialID(P.Origin("https://shop.test:8443"), "alice@example.com");
        store.Add(id, "TopSecret-ünïcödé-123");
        var bytes = File.ReadAllBytes(path);
        Assert.Equal("MOSAICPW"u8.ToArray(), bytes[..8]);
        Assert.Equal(1, bytes[8]);
        var raw = Encoding.UTF8.GetString(bytes) + Encoding.Latin1.GetString(bytes);
        Assert.DoesNotContain("TopSecret", raw);
        Assert.DoesNotContain("alice", raw);

        // A second launch reads it back; passwords are decrypted only on demand.
        var reopened = new DpapiPasswordStore(path, protector);
        Assert.Equal([id], reopened.Credentials().Select(c => c.Id));
        Assert.Equal("TopSecret-ünïcödé-123", reopened.Password(id));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void InnerSecretsStayEncryptedAfterTheFileIsOpened()
    {
        var path = Path.Combine(folder, "vault.dat");
        var protector = new FakeProtector();
        var store = new DpapiPasswordStore(path, protector);
        store.Add(new CredentialID(P.Origin("https://a.test"), "u"), "hunter22");
        // Decrypt the outer layer by hand: sites and usernames, but no password.
        var json = Encoding.UTF8.GetString(protector.Unprotect(File.ReadAllBytes(path)[9..]));
        Assert.Contains("https://a.test", json);
        Assert.DoesNotContain("hunter22", json);
    }

    [Fact]
    public void BatchWritesOnce()
    {
        var path = Path.Combine(folder, "vault.dat");
        var store = new DpapiPasswordStore(path, new FakeProtector());
        using (store.BeginBatch())
        {
            for (var i = 0; i < 50; i++) store.Add(new CredentialID(P.Origin($"https://site{i}.test"), "u"), $"pw{i}");
            Assert.False(File.Exists(path));
        }
        Assert.Equal(50, new DpapiPasswordStore(path, new FakeProtector()).Credentials().Count);
    }

    [Fact]
    public void UnreadableVaultIsNeverOverwritten()
    {
        var path = Path.Combine(folder, "vault.dat");
        Directory.CreateDirectory(folder);
        byte[] foreign = [.. "MOSAICPW"u8, 1, .. "made by someone else"u8];
        File.WriteAllBytes(path, foreign);
        var store = new DpapiPasswordStore(path, new FakeProtector());
        Assert.Contains("another Windows user", store.UnreadableReason);
        Assert.Equal(PasswordStoreErrorKind.Storage, Assert.Throws<PasswordStoreException>(() => store.Add(new CredentialID(P.Origin("https://a.test"), "u"), "x")).Kind);
        Assert.Equal(foreign, File.ReadAllBytes(path));

        store.SetAsideUnreadable();
        Assert.Null(store.UnreadableReason);
        Assert.Single(Directory.GetFiles(folder, "vault.unreadable-*.dat"));
        store.Add(new CredentialID(P.Origin("https://a.test"), "u"), "x");
        Assert.Equal("x", new DpapiPasswordStore(path, new FakeProtector()).Password(new CredentialID(P.Origin("https://a.test"), "u")));
    }

    [Fact]
    public void RefusesAbsurdlyLongPasswords()
    {
        var store = new DpapiPasswordStore(Path.Combine(folder, "vault.dat"), new FakeProtector());
        var error = Assert.Throws<PasswordStoreException>(() => store.Add(new CredentialID(P.Origin("https://a.test"), "u"), new string('x', DpapiPasswordStore.MaxPasswordBytes + 1)));
        Assert.Equal(PasswordStoreErrorKind.TooLarge, error.Kind);
    }

    [WindowsFact]
    public void RealDpapiRoundTripsAndOnlyThisUserCanRead()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(folder, "vault.dat");
        var store = new DpapiPasswordStore(path, new DpapiProtector("passwords"));
        var id = new CredentialID(P.Origin("https://dpapi.test"), "alice");
        store.Add(id, "Real-DPAPI-Secret-42");
        Assert.DoesNotContain("Real-DPAPI-Secret-42", Encoding.Latin1.GetString(File.ReadAllBytes(path)));
        Assert.Equal("Real-DPAPI-Secret-42", new DpapiPasswordStore(path, new DpapiProtector("passwords")).Password(id));
        // Another purpose (entropy) cannot open it, as another user could not.
        Assert.NotNull(new DpapiPasswordStore(path, new DpapiProtector("something else")).UnreadableReason);
    }

    [Fact]
    public void ProfilesHaveSeparateVaults()
    {
        // The vault is a file in the profile's own data folder: an isolated MOSAIC_DATA_DIR
        // profile reads <its folder>\Passwords\vault.dat and nothing else.
        var real = Path.Combine(folder, "real");
        var test = Path.Combine(folder, "isolated");
        var a = new DpapiPasswordStore(Path.Combine(PasswordProfile.Folder(real), "vault.dat"), new FakeProtector());
        var b = new DpapiPasswordStore(Path.Combine(PasswordProfile.Folder(test), "vault.dat"), new FakeProtector());
        a.Add(new CredentialID(P.Origin("https://bank.test"), "me"), "real secret");
        Assert.Empty(b.Credentials());
        Assert.NotEqual(a.FilePath, b.FilePath);
    }
}

public sealed class PasswordVaultTests : IDisposable
{
    private readonly string folder = P.NewFolder();
    private readonly SiteOrigin site = P.Origin("https://shop.test");

    public void Dispose()
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }

    private string SettingsPath => Path.Combine(folder, "settings.json");

    private PasswordVault MakeVault() => new(new InMemoryPasswordStore(), new PasswordPreferencesStore(SettingsPath));

    [Fact]
    public void OffersSaveThenNothingThenUpdate()
    {
        var vault = MakeVault();
        var login = new PasswordSubmission(site, "alice", "pw1");
        Assert.IsType<SaveOffer.Save>(vault.Offer(login, isPrivate: false));
        Assert.Equal(SaveOutcome.Added, vault.Accept(new SaveOffer.Save(), login));
        Assert.Null(vault.Offer(login, isPrivate: false));
        var changed = login with { Password = "pw2" };
        var id = new CredentialID(site, "alice");
        Assert.Equal(new SaveOffer.Update(id), vault.Offer(changed, isPrivate: false));
        Assert.Equal(SaveOutcome.Updated, vault.Accept(new SaveOffer.Update(id), changed));
        Assert.Equal("pw2", vault.Password(id));
        Assert.Single(vault.Credentials);
    }

    [Fact]
    public void QuietInPrivateTabsNeverSaveAndWhenOff()
    {
        var vault = MakeVault();
        var login = new PasswordSubmission(site, "alice", "pw1");
        Assert.Null(vault.Offer(login, isPrivate: true));
        vault.SetNeverSave(true, site);
        Assert.Null(vault.Offer(login, isPrivate: false));
        vault.SetNeverSave(false, site);
        vault.Update(p => p with { OfferToSave = false });
        Assert.Null(vault.Offer(login, isPrivate: false));
        vault.Update(p => p with { OfferToSave = true });
        Assert.Null(vault.Offer(new PasswordSubmission(site, "a", ""), isPrivate: false));
    }

    [Fact]
    public void ChangeFormWithoutUsernameUpdatesTheOnlyLogin()
    {
        var vault = MakeVault();
        vault.Save(new CredentialID(site, "alice"), "old");
        Assert.Equal(new SaveOffer.Update(new CredentialID(site, "alice")), vault.Offer(new PasswordSubmission(site, "", "new", SubmissionMode.Change), false));
        Assert.Null(vault.Offer(new PasswordSubmission(site, "", "old"), false));
    }

    [Fact]
    public void MatchesAreExactOriginMostRecentFirst()
    {
        var vault = MakeVault();
        vault.Save(new CredentialID(site, "a"), "1");
        vault.Save(new CredentialID(site, "b"), "2");
        vault.Save(new CredentialID(P.Origin("http://shop.test"), "c"), "3");
        vault.Save(new CredentialID(P.Origin("https://www.shop.test"), "d"), "4");
        vault.MarkUsed(new CredentialID(site, "a"), DateTimeOffset.UtcNow.AddSeconds(60));
        Assert.Equal(["a", "b"], vault.Matches(site).Select(c => c.Username));
        Assert.Equal(["d"], vault.Search("WWW").Select(c => c.Username));
    }

    [Fact]
    public void PreferencesPersistWithoutSecretsInTheMacFormat()
    {
        var vault = MakeVault();
        vault.Save(new CredentialID(site, "alice"), "TopSecret-123");
        vault.SetNeverSave(true, P.Origin("https://news.test"));
        vault.Update(p => p with { Generator = p.Generator with { Length = 32 } });
        var text = File.ReadAllText(SettingsPath);
        Assert.DoesNotContain("TopSecret", text);
        var root = JsonDocument.Parse(text).RootElement;
        Assert.Equal(["generator", "lastUsed", "neverSave", "offerToSave", "suggestStrongPasswords"], root.EnumerateObject().Select(p => p.Name));
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$", root.GetProperty("lastUsed").GetProperty("https://shop.test alice").GetString());
        var reloaded = new PasswordPreferencesStore(SettingsPath).Load();
        Assert.Equal([P.Origin("https://news.test")], reloaded.NeverSave);
        Assert.Equal(32, reloaded.Generator.Length);
        Assert.True(reloaded.LastUsed.ContainsKey(new CredentialID(site, "alice").Key));
    }

    [Fact]
    public void ReadsAMacSettingsFile()
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(SettingsPath, """
            {
              "generator" : { "avoidAmbiguous" : false, "digits" : true, "length" : 24, "lowercase" : true, "symbols" : false, "uppercase" : true },
              "lastUsed" : { "https:\/\/shop.test alice" : "2026-11-01T09:30:00Z" },
              "neverSave" : [ "https:\/\/news.test" ],
              "offerToSave" : false,
              "suggestStrongPasswords" : true
            }
            """);
        var preferences = new PasswordPreferencesStore(SettingsPath).Load();
        Assert.False(preferences.OfferToSave);
        Assert.Equal(24, preferences.Generator.Length);
        Assert.False(preferences.Generator.Symbols);
        Assert.Equal([P.Origin("https://news.test")], preferences.NeverSave);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 9, 30, 0, TimeSpan.Zero), preferences.LastUsed["https://shop.test alice"]);
    }

    [Fact]
    public void ImportCountsAndDoesNotMarkUsed()
    {
        var vault = MakeVault();
        vault.Save(new CredentialID(site, "alice"), "same");
        vault.Save(new CredentialID(site, "bob"), "old");
        var summary = vault.ImportEntries(PasswordCsv.ParseExport("url,username,password\nhttps://shop.test,alice,same\nhttps://shop.test,bob,new\nhttps://shop.test,carol,pw\nnot a url,x,y\n"));
        Assert.Equal((1, 1, 1, 1), (summary.Added, summary.Updated, summary.Unchanged, summary.Skipped.Count));
        Assert.Equal("added 1 updated 1 unchanged 1 failed 0 skipped 1", summary.ToString());
        Assert.Null(vault.LastUsed(new CredentialID(site, "carol")));
        Assert.Equal(3, vault.ExportEntries().Count);
    }
}

public sealed class PasswordPageMessageTests
{
    [Fact]
    public void DecodesAndNeverThrows()
    {
        var message = PasswordPageMessage.Decode("""{"kind":"submit","doc":"d1","username":"  alice ","password":"pw","mode":"signup","confirmed":true}""");
        Assert.Equal(PasswordPageMessage.MessageKind.Submit, message?.Kind);
        Assert.Equal(SubmissionMode.Signup, message?.Mode);
        var odd = PasswordPageMessage.Decode("""{"kind":7,"password":["x"],"origin":"javascript:x"}""")!;
        Assert.Null(odd.Kind);
        Assert.Null(odd.Password);
        Assert.Null(odd.Origin);
        Assert.Null(PasswordPageMessage.Decode("[1,2]"));
        Assert.Null(PasswordPageMessage.Decode("not json"));
    }

    [Fact]
    public void SubmissionNeedsTrustedOriginAndFrame()
    {
        var message = PasswordPageMessage.Decode("""{"kind":"submit","username":" alice ","password":"pw"}""")!;
        Assert.Null(message.Submission);
        message.Origin = P.Origin("https://a.test");
        message.Frame = PasswordPageMessage.FrameKind.CrossOrigin;
        Assert.Null(message.Submission);
        message.Frame = PasswordPageMessage.FrameKind.Main;
        Assert.Equal(new PasswordSubmission(P.Origin("https://a.test"), "alice", "pw"), message.Submission);
    }

    [Fact]
    public void DropsOversizedPasswords() =>
        Assert.Null(PasswordPageMessage.Decode($$"""{"kind":"submit","password":"{{new string('x', 2000)}}"}""")?.Password);

    [Fact]
    public void GeckoEventFieldNames()
    {
        var message = PasswordPageMessage.Decode("""{"kind":"page","login":true,"signup":false,"origin":"http://127.0.0.1:8766","frame":"same","tab":3,"private":true}""")!;
        Assert.True(message.HasLogin);
        Assert.False(message.HasNewPassword);
        Assert.Equal(P.Origin("http://127.0.0.1:8766"), message.Origin);
        Assert.Equal(PasswordPageMessage.FrameKind.SameOrigin, message.Frame);
        Assert.Equal(3, message.Tab);
        Assert.True(message.IsPrivate);
    }

    [Fact]
    public void WebMessagesIgnoreWhatThePageSaysAboutItself()
    {
        var json = JsonSerializer.Serialize(PasswordPageScripts.MessagePrefix + """{"kind":"submit","password":"pw","origin":"https://bank.test","frame":"main","tab":1,"private":false}""");
        var message = PasswordPageScripts.FromWebMessage(json)!;
        Assert.Equal(PasswordPageMessage.MessageKind.Submit, message.Kind);
        Assert.Null(message.Origin);
        Assert.Null(message.Frame);
        Assert.Null(message.Tab);
        Assert.Null(message.IsPrivate);
        // Other areas' messages, and anything not a string, are not passwords'.
        Assert.Null(PasswordPageScripts.FromWebMessage(JsonSerializer.Serialize("""{"kind":"submit"}""")));
        Assert.Null(PasswordPageScripts.FromWebMessage("""{"kind":"submit"}"""));
        Assert.Null(PasswordPageScripts.FromWebMessage("42"));
    }
}

/// <summary>The shared page script and Mosaic's wrappers, run in V8 (ClearScript), as macOS runs them in JavaScriptCore.</summary>
public sealed class PasswordPageScriptTests : IDisposable
{
    private readonly V8ScriptEngine engine = new();

    public PasswordPageScriptTests() => engine.Execute(PageScript);

    public void Dispose() => engine.Dispose();

    internal static string PageScript
    {
        get
        {
            for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
            {
                var candidate = Path.Combine(folder.FullName, "Resources", "gecko", "passwords-page.js");
                if (File.Exists(candidate)) return File.ReadAllText(candidate);
            }
            throw new FileNotFoundException("Resources/gecko/passwords-page.js was not found above the test folder.");
        }
    }

    private JsonElement? Classify(string fields)
    {
        var text = (string)engine.Evaluate($"JSON.stringify(MosaicPasswordHeuristics.classify({fields}))");
        return text == "null" ? null : JsonDocument.Parse(text).RootElement;
    }

    private string Kind(string field) => (string)engine.Evaluate($"MosaicPasswordHeuristics.fieldKind({field})");

    [Fact]
    public void ScriptDefinesItsGlobals() => Assert.Equal("function", engine.Evaluate("typeof createMosaicPasswordPage"));

    [Fact]
    public void LiteralsAreSafe()
    {
        var text = "a\"b\\c</script>\u2028'\u2029";
        var literal = PasswordPageScripts.Literal(text);
        Assert.DoesNotContain("<", literal);
        Assert.DoesNotContain("\u2028", literal);
        Assert.Equal(text, engine.Evaluate(literal));
        var fill = PasswordPageScripts.ChromiumFill("", P.Origin("https://a.test"), "u", "p\");alert(1);//");
        Assert.Contains(PasswordPageScripts.Literal("p\");alert(1);//"), fill);
    }

    [Fact]
    public void FieldKinds()
    {
        Assert.Equal("password", Kind("""{type:"password"}"""));
        Assert.Equal("new-password", Kind("""{type:"password",autocomplete:"new-password"}"""));
        Assert.Equal("new-password", Kind("""{type:"password",name:"confirm_password"}"""));
        Assert.Equal("current-password", Kind("""{type:"password",id:"oldPassword"}"""));
        Assert.Equal("username", Kind("""{type:"email"}"""));
        Assert.Equal("username", Kind("""{type:"text",name:"login"}"""));
        Assert.Equal("username", Kind("""{type:"text",autocomplete:"username"}"""));
        Assert.Equal("other", Kind("""{type:"text",name:"q",placeholder:"Search"}"""));
        Assert.Equal("other", Kind("""{type:"text",autocomplete:"one-time-code"}"""));
        Assert.Equal("text", Kind("""{type:"text",name:"field1"}"""));
        Assert.Equal("other", Kind("""{type:"checkbox",name:"remember"}"""));
        Assert.Equal("hidden", Kind("""{type:"password",visible:false}"""));
    }

    [Fact]
    public void LoginSignupAndChangeForms()
    {
        var login = Classify("""[{type:"text",name:"q",placeholder:"search"},{type:"email",name:"email"},{type:"password",name:"pw"},{type:"checkbox"}]""")!.Value;
        Assert.Equal("login", login.GetProperty("mode").GetString());
        Assert.Equal(1, login.GetProperty("username").GetInt32());
        Assert.Equal(2, login.GetProperty("password").GetInt32());

        var signup = Classify("""[{type:"text",name:"first_name"},{type:"email",name:"email"},{type:"password",value:"Abc-123-xyz"},{type:"password",value:"Abc-123-xyz"}]""")!.Value;
        Assert.Equal("signup", signup.GetProperty("mode").GetString());
        Assert.Equal([2, 3], signup.GetProperty("newFields").EnumerateArray().Select(e => e.GetInt32()));

        var change = Classify("""[{type:"text",autocomplete:"username",visible:false,value:"alice"},{type:"password",value:"old"},{type:"password",value:"new-1"},{type:"password",value:"new-1"}]""")!.Value;
        Assert.Equal("change", change.GetProperty("mode").GetString());
        Assert.Equal(1, change.GetProperty("current").GetInt32());
        Assert.Equal(0, change.GetProperty("username").GetInt32());

        Assert.Null(Classify("""[{type:"text",name:"user"},{type:"password",visible:false}]"""));
    }

    [Fact]
    public void IdentifierFirstStep()
    {
        Assert.Equal(0, engine.Evaluate("""MosaicPasswordHeuristics.identifierField([{type:"email",name:"identifier"},{type:"hidden"}])"""));
        Assert.Equal(-1, engine.Evaluate("""MosaicPasswordHeuristics.identifierField([{type:"text",name:"name"},{type:"email"}])"""));
    }

    [Fact]
    public void ChromiumScriptsParseAndRefuseOtherOrigins()
    {
        using var page = new V8ScriptEngine();
        // A bare stand-in for a page on https://bank.test: enough for the wrappers to run.
        page.Execute("""var location = { origin: "https://bank.test", protocol: "https:" }; var window = {}; window.top = window;""");
        var fill = PasswordPageScripts.ChromiumFill(PageScript, P.Origin("https://evil.test"), "u", "p");
        Assert.Equal(0, page.Evaluate(fill));
        // The document script never attaches without WebView2's bridge, and is valid JavaScript.
        page.Evaluate(PasswordPageScripts.ChromiumDocumentScript(PageScript));
        Assert.Equal(false, page.Evaluate("!!window.__mosaicPasswordsAttached"));
    }
}
