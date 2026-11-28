using System.Text.Json.Nodes;
using Mosaic.Core.Browser;

namespace Mosaic.Core.Passwords;

/// <summary>
/// Scripted checks for passwords, with the macOS command names
/// (<c>Sources/Mosaic/Features/Passwords/PasswordsAutomation.swift</c>); state appears under
/// <c>passwords</c> in <c>dump</c>.
/// <code>
/// passwordsFill [username]            fill the selected tab (as Ctrl+\ does)
/// passwordsPrompt save|never|notnow|use|regenerate
/// passwordsSuggest                    Suggest Strong Password
/// passwordsSettings                   open Settings › Passwords &amp; Autofill
/// passwordsOption offerToSave=on|off, suggestStrongPasswords=on|off
/// passwordsPrivate on|off             treat the selected tab as private
/// passwordsNeverSave &lt;origin&gt;|on|off
/// passwordsVerify &lt;origin&gt;|&lt;user&gt;|&lt;password&gt;  out: "match", "mismatch" or "missing"
/// passwordsAudit                      out: {"weak": n, "reused": n}
/// passwordsSnapshot prompt|button|settings    out: PNG of that view (the app renders it)
/// Isolated profiles only (they write to that profile's own vault):
/// passwordsAdd &lt;origin&gt;|&lt;user&gt;|&lt;password&gt;
/// passwordsDelete &lt;origin&gt;|&lt;user&gt;
/// passwordsDeleteAll
/// passwordsImport &lt;csv path&gt;          out: summary
/// passwordsExport &lt;csv path&gt;
/// </code>
/// Neither the commands nor <c>dump</c> ever write a saved password anywhere except the CSV
/// export a script asked for on an isolated profile.
/// </summary>
public static class PasswordsAutomation
{
    private static int registered;

    /// <summary>Renders a password view to a PNG (set by the app): model, which view, output path.</summary>
    public static Func<BrowserModel, string, string, Task>? Snapshot { get; set; }

    /// <summary>Registers the commands and the <c>dump</c> key. Idempotent.</summary>
    public static void Register()
    {
        if (Interlocked.Exchange(ref registered, 1) == 1) return;
        AutomationRegistry.Register(HandleAsync);
        AutomationRegistry.RegisterState("passwords", model => State(model.Passwords, model));
    }

    /// <summary>Whether this run may use the commands that write to the vault.</summary>
    internal static Func<bool> IsIsolated { get; set; } = () => PasswordProfile.IsIsolated;

    internal static async Task<AutomationReply?> HandleAsync(BrowserModel model, string command, string value, string? output)
    {
        if (!command.StartsWith("passwords", StringComparison.Ordinal)) return null;
        var passwords = model.Passwords;
        var parts = value.Split('|');
        var tab = model.SelectedTab;
        var isolated = IsIsolated();

        async Task Write(string text)
        {
            if (output is not null) await File.WriteAllTextAsync(output, text);
        }
        static CredentialID? Id(string origin, string user) =>
            SiteOrigin.FromUserInput(origin) is { } site ? new CredentialID(site, user) : null;
        AutomationReply IsolatedOnly() =>
            AutomationRegistry.Error($"{command} only works on an isolated profile (MOSAIC_DATA_DIR), so a script can never change your real passwords.");

        switch (command)
        {
            case "passwordsFill":
                var credential = value.Length == 0 ? null : passwords.Fillable(tab).FirstOrDefault(c => c.Username == value);
                if (value.Length > 0 && credential is null) return AutomationRegistry.Error($"No saved login \"{value}\" for this page.");
                await passwords.FillAsync(tab, credential);
                return AutomationRegistry.Ok();

            case "passwordsPrompt":
                if (tab is null) return AutomationRegistry.Error("There is no tab.");
                if (passwords.PromptFor(tab) is null) return AutomationRegistry.Error("No password prompt is showing for the selected tab.");
                switch (value)
                {
                    case "save": passwords.AcceptSave(tab); break;
                    case "never": passwords.NeverSave(tab); break;
                    case "notnow": passwords.DismissPrompt(tab); break;
                    case "use": await passwords.AcceptGeneratedAsync(tab); break;
                    case "regenerate": passwords.Regenerate(tab); break;
                    default: return AutomationRegistry.Error("Expected save, never, notnow, use or regenerate.");
                }
                return AutomationRegistry.Ok();

            case "passwordsSuggest":
                passwords.OfferGeneratedPassword(tab);
                return AutomationRegistry.Ok();

            case "passwordsSettings":
                passwords.OpenSettings();
                return AutomationRegistry.Ok();

            case "passwordsOption":
                var pair = value.Split('=');
                if (pair is not [var key, var setting] || key is not ("offerToSave" or "suggestStrongPasswords"))
                {
                    return AutomationRegistry.Error("Expected offerToSave=on|off or suggestStrongPasswords=on|off.");
                }
                var on = setting == "on";
                passwords.Vault.Update(preferences => key == "offerToSave"
                    ? preferences with { OfferToSave = on }
                    : preferences with { SuggestStrongPasswords = on });
                passwords.NotifyChanged();
                return AutomationRegistry.Ok();

            case "passwordsPrivate":
                if (tab is null) return AutomationRegistry.Error("There is no tab.");
                if (value == "on") passwords.SimulatedPrivateTabs.Add(tab.Id);
                else passwords.SimulatedPrivateTabs.Remove(tab.Id);
                return AutomationRegistry.Ok();

            case "passwordsNeverSave":
                if (parts.Length != 2 || SiteOrigin.FromUserInput(parts[0]) is not { } neverOrigin)
                {
                    return AutomationRegistry.Error("Expected <origin>|on or <origin>|off.");
                }
                passwords.Vault.SetNeverSave(parts[1] == "on", neverOrigin);
                passwords.NotifyChanged();
                return AutomationRegistry.Ok();

            case "passwordsVerify":
                if (parts.Length != 3 || Id(parts[0], parts[1]) is not { } verifyId)
                {
                    await Write("invalid");
                    return AutomationRegistry.Ok();
                }
                string? saved;
                try
                {
                    saved = passwords.Vault.Password(verifyId);
                }
                catch (PasswordStoreException)
                {
                    saved = null;
                }
                await Write(saved is null ? "missing" : saved == parts[2] ? "match" : "mismatch");
                return AutomationRegistry.Ok();

            case "passwordsAudit":
                var audit = passwords.Vault.Audit();
                await Write($"{{\"weak\": {audit.Weak.Count}, \"reused\": {audit.Reused.Count}}}");
                return AutomationRegistry.Ok();

            case "passwordsSnapshot":
                if (output is null) return AutomationRegistry.Error("passwordsSnapshot needs \"out\": the PNG to write.");
                if (Snapshot is not { } snapshot) return AutomationRegistry.Error("This build cannot render password views.");
                await snapshot(model, value, output);
                return AutomationRegistry.Ok();

            case "passwordsAdd":
                if (!isolated) return IsolatedOnly();
                if (parts.Length != 3 || Id(parts[0], parts[1]) is not { } addId) return AutomationRegistry.Error("Expected <origin>|<user>|<password>.");
                try
                {
                    passwords.Vault.Save(addId, parts[2]);
                }
                catch (PasswordStoreException error)
                {
                    await Write($"error: {error.Message}");
                    return AutomationRegistry.Error(error.Message);
                }
                passwords.NotifyChanged();
                return AutomationRegistry.Ok();

            case "passwordsDelete":
                if (!isolated) return IsolatedOnly();
                if (parts.Length != 2 || Id(parts[0], parts[1]) is not { } deleteId) return AutomationRegistry.Error("Expected <origin>|<user>.");
                try
                {
                    passwords.Vault.Delete(deleteId);
                }
                catch (PasswordStoreException error)
                {
                    return AutomationRegistry.Error(error.Message);
                }
                passwords.NotifyChanged();
                return AutomationRegistry.Ok();

            case "passwordsDeleteAll":
                if (!isolated) return IsolatedOnly();
                foreach (var saved1 in passwords.Vault.Credentials.ToList())
                {
                    try { passwords.Vault.Delete(saved1.Id); } catch (PasswordStoreException) { }
                }
                passwords.NotifyChanged();
                return AutomationRegistry.Ok();

            case "passwordsImport":
                if (!isolated) return IsolatedOnly();
                try
                {
                    var text = await File.ReadAllTextAsync(value);
                    var parsed = PasswordCsv.ParseExport(text);
                    var summary = await Task.Run(() => passwords.Vault.ImportEntries(parsed));
                    await Write(summary.ToString());
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or PasswordCsv.ImportException or PasswordStoreException)
                {
                    await Write($"error: {error.Message}");
                }
                passwords.NotifyChanged();
                return AutomationRegistry.Ok();

            case "passwordsExport":
                if (!isolated) return IsolatedOnly();
                await PasswordExportFile.WriteAsync(value, PasswordCsv.Export(passwords.Vault.ExportEntries()));
                return AutomationRegistry.Ok();

            default:
                return null;
        }
    }

    /// <summary>The <c>passwords</c> section of <c>dump</c>. Never contains a password.</summary>
    public static JsonObject State(PasswordManager passwords, BrowserModel model)
    {
        var vault = passwords.Vault;
        var tabs = new JsonArray();
        for (var index = 0; index < model.Tabs.Count; index++)
        {
            var tab = model.Tabs[index];
            var page = passwords.PageFor(tab);
            var entry = new JsonObject
            {
                ["index"] = index,
                ["hasLogin"] = page?.HasLogin ?? false,
                ["hasSignup"] = page?.HasSignup ?? false,
                ["pending"] = page?.Pending is not null,
                ["fillable"] = passwords.Fillable(tab).Count,
                ["offersFill"] = passwords.OffersFill(tab),
                ["private"] = passwords.IsPrivate(tab),
            };
            switch (passwords.PromptFor(tab))
            {
                case PasswordManager.Prompt.Save save:
                    entry["prompt"] = save.Offer is SaveOffer.Update ? "update" : "save";
                    entry["promptOrigin"] = save.Submission.Origin.String;
                    entry["promptUsername"] = save.Submission.Username;
                    break;
                case PasswordManager.Prompt.Generate generate:
                    entry["prompt"] = "generate";
                    entry["promptOrigin"] = generate.Origin.String;
                    // Its length only: the suggestion is not saved anywhere yet, but stays out of files.
                    entry["promptPasswordLength"] = generate.Password.Length;
                    break;
                case PasswordManager.Prompt.Choose choose:
                    entry["prompt"] = "choose";
                    entry["promptOrigin"] = choose.Origin.String;
                    entry["promptChoices"] = new JsonArray([.. choose.Credentials.Select(c => JsonValue.Create(c.Username))]);
                    break;
                default:
                    entry["prompt"] = "";
                    break;
            }
            tabs.Add(entry);
        }

        var state = new JsonObject
        {
            // macOS reports its Keychain scope here; on Windows the vault is the profile's own file.
            ["keychainScope"] = passwords.StoreDescription == "dpapi"
                ? (PasswordProfile.IsIsolated ? "dpapi:isolated-profile" : "dpapi:profile")
                : "memory",
            ["store"] = passwords.StoreDescription,
            ["vault"] = vault.Store is DpapiPasswordStore dpapi ? dpapi.FilePath : "",
            ["count"] = vault.Credentials.Count,
            ["credentials"] = new JsonArray([.. vault.Credentials.Select(credential => (JsonNode)new JsonObject
            {
                ["origin"] = credential.Origin.String,
                ["username"] = credential.Username,
                ["lastUsed"] = vault.LastUsed(credential.Id) is { } used ? PasswordDates.Format(used) : "",
            })]),
            ["neverSave"] = new JsonArray([.. vault.Preferences.NeverSave.Select(origin => JsonValue.Create(origin.String))]),
            ["offerToSave"] = vault.Preferences.OfferToSave,
            ["suggestStrongPasswords"] = vault.Preferences.SuggestStrongPasswords,
            ["tabs"] = tabs,
            ["error"] = vault.LastError ?? "",
            ["geckoError"] = passwords.GeckoError ?? "",
            ["chromiumScriptLoaded"] = passwords.Chromium?.IsScriptLoaded ?? false,
            ["unlocked"] = passwords.Unlock.IsUnlocked,
            ["automationBypassesUnlock"] = passwords.Unlock.BypassesForAutomation,
        };
        if (passwords.LastFill is { } fill)
        {
            state["lastFill"] = new JsonObject { ["origin"] = fill.Origin, ["count"] = fill.Count, ["at"] = PasswordDates.Format(fill.At) };
        }
        if (passwords.LastSubmission is { } submission)
        {
            state["lastSubmission"] = new JsonObject
            {
                ["origin"] = submission.Origin,
                ["username"] = submission.Username,
                ["mode"] = submission.Mode,
                ["confirmed"] = submission.Confirmed,
            };
        }
        return state;
    }
}

/// <summary>Writes a CSV export: the only place a password leaves the vault in plain text, on the user's (or a test script's) request.</summary>
public static class PasswordExportFile
{
    public static async Task WriteAsync(string path, string csv)
    {
        // Created fresh so a stale file's permissions (or a link planted in its place) are not reused.
        if (File.Exists(path)) File.Delete(path);
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await writer.WriteAsync(csv);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
