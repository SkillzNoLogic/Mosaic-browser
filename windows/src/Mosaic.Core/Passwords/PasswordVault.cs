using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mosaic.Core.Passwords;

/// <summary>
/// Non-secret password settings and metadata, kept in <c>Passwords\settings.json</c> in Mosaic's
/// data folder. Never holds a password. Same file as macOS (same keys, ISO 8601 dates).
/// </summary>
public sealed record PasswordPreferences
{
    /// <summary>Offer to save passwords when a sign-in or sign-up form is submitted.</summary>
    public bool OfferToSave { get; init; } = true;

    /// <summary>Suggest a generated password when a new-password field is focused.</summary>
    public bool SuggestStrongPasswords { get; init; } = true;

    /// <summary>Sites the user chose "Never for This Site" on, sorted.</summary>
    public IReadOnlyList<SiteOrigin> NeverSave { get; init; } = [];

    /// <summary>When each credential was last filled or confirmed by a sign-in, by <see cref="CredentialID.Key"/>.</summary>
    public IReadOnlyDictionary<string, DateTimeOffset> LastUsed { get; init; } = new Dictionary<string, DateTimeOffset>();

    public PasswordGeneratorOptions Generator { get; init; } = PasswordGeneratorOptions.Default;

    public bool Equals(PasswordPreferences? other) =>
        other is not null && OfferToSave == other.OfferToSave && SuggestStrongPasswords == other.SuggestStrongPasswords
        && NeverSave.SequenceEqual(other.NeverSave) && Generator == other.Generator
        && LastUsed.Count == other.LastUsed.Count
        && LastUsed.All(pair => other.LastUsed.TryGetValue(pair.Key, out var date) && date == pair.Value);

    public override int GetHashCode() => HashCode.Combine(OfferToSave, SuggestStrongPasswords, NeverSave.Count, LastUsed.Count, Generator);

    /// <summary>Tolerant: a missing or unreadable key keeps its default instead of losing the file.</summary>
    public static PasswordPreferences FromJson(JsonObject root)
    {
        var defaults = new PasswordPreferences();
        bool Flag(string key, bool fallback) =>
            root[key] is JsonValue value && value.TryGetValue(out bool flag) ? flag : fallback;

        var neverSave = new List<SiteOrigin>();
        if (root["neverSave"] is JsonArray never)
        {
            foreach (var node in never)
            {
                // macOS drops the whole list if one entry is bad; keeping the good ones is kinder.
                if (node is JsonValue value && value.TryGetValue(out string? text) && SiteOrigin.FromUrl(text) is { } origin)
                {
                    neverSave.Add(origin);
                }
            }
        }
        var lastUsed = new Dictionary<string, DateTimeOffset>();
        if (root["lastUsed"] is JsonObject used)
        {
            foreach (var (key, node) in used)
            {
                if (node is JsonValue value && value.TryGetValue(out string? text) && PasswordDates.Parse(text) is { } date)
                {
                    lastUsed[key] = date;
                }
            }
        }
        PasswordGeneratorOptions generator = defaults.Generator;
        if (root["generator"] is JsonObject options)
        {
            try
            {
                generator = options.Deserialize<PasswordGeneratorOptions>() ?? defaults.Generator;
            }
            catch (JsonException)
            {
            }
        }
        return new PasswordPreferences
        {
            OfferToSave = Flag("offerToSave", true),
            SuggestStrongPasswords = Flag("suggestStrongPasswords", true),
            NeverSave = [.. neverSave.Distinct().Order()],
            LastUsed = lastUsed,
            Generator = generator,
        };
    }

    /// <summary>Keys sorted, as macOS writes them.</summary>
    public JsonObject ToJson()
    {
        var used = new JsonObject();
        foreach (var (key, date) in LastUsed.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            used[key] = PasswordDates.Format(date);
        }
        return new JsonObject
        {
            ["generator"] = new JsonObject
            {
                ["avoidAmbiguous"] = Generator.AvoidAmbiguous,
                ["digits"] = Generator.Digits,
                ["length"] = Generator.Length,
                ["lowercase"] = Generator.Lowercase,
                ["symbols"] = Generator.Symbols,
                ["uppercase"] = Generator.Uppercase,
            },
            ["lastUsed"] = used,
            ["neverSave"] = new JsonArray([.. NeverSave.Select(origin => JsonValue.Create(origin.String))]),
            ["offerToSave"] = OfferToSave,
            ["suggestStrongPasswords"] = SuggestStrongPasswords,
        };
    }
}

/// <summary>Loads and saves <see cref="PasswordPreferences"/>.</summary>
public sealed class PasswordPreferencesStore(string path)
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public string FilePath { get; } = path;

    public PasswordPreferences Load()
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(FilePath)) is JsonObject root ? PasswordPreferences.FromJson(root) : new();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    public void Save(PasswordPreferences preferences)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, preferences.ToJson().ToJsonString(Indented));
        File.Move(temporary, FilePath, overwrite: true);
    }
}

/// <summary>How a form was used.</summary>
public enum SubmissionMode
{
    /// <summary>Signing in: username and current password.</summary>
    Login,
    /// <summary>Creating an account: the new password (typed twice, or marked <c>new-password</c>).</summary>
    Signup,
    /// <summary>Changing a password: the new one replaces the saved one.</summary>
    Change,
}

public static class SubmissionModes
{
    public static string Id(this SubmissionMode mode) => mode switch
    {
        SubmissionMode.Signup => "signup",
        SubmissionMode.Change => "change",
        _ => "login",
    };

    public static SubmissionMode? FromId(string? id) => id switch
    {
        "login" => SubmissionMode.Login,
        "signup" => SubmissionMode.Signup,
        "change" => SubmissionMode.Change,
        _ => null,
    };
}

/// <summary>A sign-in or sign-up the page reported, with the origin established by the engine (never by the page).</summary>
public sealed record PasswordSubmission(SiteOrigin Origin, string Username, string Password, SubmissionMode Mode = SubmissionMode.Login);

/// <summary>What to ask after a submission: save (nothing saved for this username yet) or update an existing one.</summary>
public abstract record SaveOffer
{
    public sealed record Save : SaveOffer;
    public sealed record Update(CredentialID Id) : SaveOffer;
}

public enum SaveOutcome { Added, Updated, Unchanged }

/// <summary>
/// The password manager's rules, over an <see cref="IPasswordStore"/> and the preferences.
/// Engine- and UI-independent. Port of <c>Sources/MosaicCore/Passwords/PasswordVault.swift</c>.
/// </summary>
/// <remarks>Called from the UI thread; an import may run on a worker while the UI waits for it.</remarks>
public sealed class PasswordVault
{
    private readonly PasswordPreferencesStore? preferencesStore;
    private readonly TimeProvider time;
    private readonly Lock gate = new();

    public PasswordVault(IPasswordStore store, PasswordPreferencesStore? preferencesStore, TimeProvider? time = null)
    {
        Store = store;
        this.preferencesStore = preferencesStore;
        this.time = time ?? TimeProvider.System;
        Preferences = preferencesStore?.Load() ?? new PasswordPreferences();
        Reload();
    }

    public IPasswordStore Store { get; }
    public PasswordPreferences Preferences { get; private set; }

    /// <summary>Cached list of saved credentials (metadata only), sorted, refreshed after every change.</summary>
    public IReadOnlyList<PasswordCredential> Credentials { get; private set; } = [];

    public string? LastError { get; private set; }

    // ---- Reading ----

    public void Reload()
    {
        try
        {
            Credentials = [.. Store.Credentials().OrderBy(credential => credential.Id)];
            LastError = null;
        }
        catch (PasswordStoreException error)
        {
            Credentials = [];
            LastError = error.Message;
        }
    }

    /// <summary>Credentials saved for exactly <paramref name="origin"/>, most recently used first.</summary>
    public IReadOnlyList<PasswordCredential> Matches(SiteOrigin origin) =>
        [.. Credentials.Where(credential => credential.Origin == origin)
            .OrderByDescending(credential => LastUsed(credential.Id) ?? DateTimeOffset.MinValue)
            .ThenBy(credential => credential.Id)];

    /// <summary>Case-insensitive search over site and username.</summary>
    public IReadOnlyList<PasswordCredential> Search(string text)
    {
        var query = text.Trim();
        if (query.Length == 0) return Credentials;
        return [.. Credentials.Where(credential =>
            credential.Origin.String.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || credential.Username.Contains(query, StringComparison.CurrentCultureIgnoreCase))];
    }

    public string Password(CredentialID id) => Store.Password(id);

    public DateTimeOffset? LastUsed(CredentialID id) => Preferences.LastUsed.TryGetValue(id.Key, out var date) ? date : null;

    public bool Contains(CredentialID id) => Credentials.Any(credential => credential.Id == id);

    private string? TryPassword(CredentialID id)
    {
        try
        {
            return Store.Password(id);
        }
        catch (PasswordStoreException)
        {
            return null;
        }
    }

    // ---- Changing ----

    /// <summary>
    /// Adds a credential, or replaces the password of the one with the same origin and username.
    /// <paramref name="markUsed"/> records it as just used (a sign-in); imports pass false.
    /// </summary>
    public SaveOutcome Save(CredentialID id, string password, bool markUsed = true)
    {
        lock (gate)
        {
            SaveOutcome outcome;
            if (Contains(id))
            {
                if (TryPassword(id) == password)
                {
                    outcome = SaveOutcome.Unchanged;
                }
                else
                {
                    Store.Update(id, null, password);
                    outcome = SaveOutcome.Updated;
                }
            }
            else
            {
                Store.Add(id, password);
                outcome = SaveOutcome.Added;
            }
            if (markUsed) MarkUsed(id);
            Reload();
            return outcome;
        }
    }

    /// <summary>Edits a credential. The origin cannot change (that would be a different credential).</summary>
    public void Edit(CredentialID id, string username, string? password)
    {
        lock (gate)
        {
            Store.Update(id, username, password);
            var newId = id with { Username = username };
            if (newId != id && Preferences.LastUsed.TryGetValue(id.Key, out var used))
            {
                var lastUsed = new Dictionary<string, DateTimeOffset>(Preferences.LastUsed);
                lastUsed.Remove(id.Key);
                lastUsed[newId.Key] = used;
                Preferences = Preferences with { LastUsed = lastUsed };
                Persist();
            }
            Reload();
        }
    }

    public void Delete(CredentialID id)
    {
        lock (gate)
        {
            Store.Delete(id);
            if (Preferences.LastUsed.ContainsKey(id.Key))
            {
                var lastUsed = new Dictionary<string, DateTimeOffset>(Preferences.LastUsed);
                lastUsed.Remove(id.Key);
                Preferences = Preferences with { LastUsed = lastUsed };
                Persist();
            }
            Reload();
        }
    }

    public void MarkUsed(CredentialID id, DateTimeOffset? date = null)
    {
        var lastUsed = new Dictionary<string, DateTimeOffset>(Preferences.LastUsed) { [id.Key] = date ?? time.GetUtcNow() };
        Preferences = Preferences with { LastUsed = lastUsed };
        Persist();
    }

    // ---- Saving after a sign-in ----

    /// <summary>
    /// Whether to offer saving <paramref name="submission"/>; null means stay quiet: the tab is
    /// private, offers are off, the site is on the never-save list, the password is empty, or the
    /// same password is already saved for this username (which counts as a use of it).
    /// </summary>
    public SaveOffer? Offer(PasswordSubmission submission, bool isPrivate)
    {
        if (isPrivate || !Preferences.OfferToSave || submission.Password.Length == 0 || IsNeverSave(submission.Origin)) return null;
        var saved = Matches(submission.Origin);
        var id = new CredentialID(submission.Origin, submission.Username);
        if (saved.Any(credential => credential.Id == id))
        {
            if (TryPassword(id) == submission.Password)
            {
                MarkUsed(id);
                return null;
            }
            return new SaveOffer.Update(id);
        }
        if (submission.Username.Length == 0)
        {
            // A form without a username field: a change-password page for the only saved login
            // updates it; a password that is already saved is a sign-in with it.
            if (saved.FirstOrDefault(credential => TryPassword(credential.Id) == submission.Password) is { } match)
            {
                MarkUsed(match.Id);
                return null;
            }
            if (submission.Mode == SubmissionMode.Change && saved.Count == 1) return new SaveOffer.Update(saved[0].Id);
        }
        return new SaveOffer.Save();
    }

    /// <summary>Carries out an accepted offer.</summary>
    public SaveOutcome Accept(SaveOffer offer, PasswordSubmission submission)
    {
        switch (offer)
        {
            case SaveOffer.Update update:
                lock (gate)
                {
                    Store.Update(update.Id, null, submission.Password);
                    MarkUsed(update.Id);
                    Reload();
                }
                return SaveOutcome.Updated;
            default:
                return Save(new CredentialID(submission.Origin, submission.Username), submission.Password);
        }
    }

    // ---- Never-save list ----

    public bool IsNeverSave(SiteOrigin origin) => Preferences.NeverSave.Contains(origin);

    public void SetNeverSave(bool never, SiteOrigin origin)
    {
        var list = new HashSet<SiteOrigin>(Preferences.NeverSave);
        if (never) list.Add(origin);
        else list.Remove(origin);
        Update(preferences => preferences with { NeverSave = [.. list.Order()] });
    }

    // ---- Preferences ----

    public void Update(Func<PasswordPreferences, PasswordPreferences> change)
    {
        var copy = change(Preferences);
        if (copy.Equals(Preferences)) return;
        Preferences = copy;
        Persist();
    }

    private void Persist()
    {
        try
        {
            preferencesStore?.Save(Preferences);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            LastError = $"Could not save password settings: {error.Message}";
        }
    }

    // ---- Import, export, audit ----

    public sealed class ImportSummary
    {
        public int Added { get; set; }
        public int Updated { get; set; }
        public int Unchanged { get; set; }
        public int Failed { get; set; }
        public IReadOnlyList<PasswordCsv.Skipped> Skipped { get; init; } = [];

        /// <summary>The line automation writes (same as macOS).</summary>
        public override string ToString() =>
            $"added {Added} updated {Updated} unchanged {Unchanged} failed {Failed} skipped {Skipped.Count}";

        /// <summary>The message shown after an import (same wording as macOS, with "PC").</summary>
        public string Describe()
        {
            var parts = new List<string> { $"{Added} added" };
            if (Updated > 0) parts.Add($"{Updated} updated");
            if (Unchanged > 0) parts.Add($"{Unchanged} already saved");
            if (Failed > 0) parts.Add($"{Failed} could not be saved");
            if (Skipped.Count > 0) parts.Add($"{Skipped.Count} skipped (no password, or not a web address)");
            return "Import finished: " + string.Join(", ", parts) + ".\n\nThe CSV file holds your passwords in plain text: delete it now.";
        }
    }

    /// <summary>Saves every entry, writing the store once at the end. Safe to run on a worker thread.</summary>
    public ImportSummary ImportEntries(PasswordCsv.ImportResult result)
    {
        var summary = new ImportSummary { Skipped = result.Skipped };
        lock (gate)
        {
            using (Store.BeginBatch())
            {
                foreach (var entry in result.Entries)
                {
                    try
                    {
                        switch (Save(entry.Id, entry.Password, markUsed: false))
                        {
                            case SaveOutcome.Added: summary.Added++; break;
                            case SaveOutcome.Updated: summary.Updated++; break;
                            default: summary.Unchanged++; break;
                        }
                    }
                    catch (PasswordStoreException)
                    {
                        summary.Failed++;
                    }
                }
            }
            Reload();
        }
        return summary;
    }

    /// <summary>Every saved credential with its password, for export. Reads each secret once.</summary>
    public IReadOnlyList<PasswordCsv.Entry> ExportEntries() =>
        [.. Credentials.Select(credential => new PasswordCsv.Entry(credential.Origin, credential.Username, Store.Password(credential.Id)))];

    /// <summary>Weak and reused passwords. Reads every secret; the caller authenticates first.</summary>
    public PasswordAudit Audit()
    {
        var passwords = new Dictionary<CredentialID, string>();
        foreach (var credential in Credentials)
        {
            if (TryPassword(credential.Id) is { } password) passwords[credential.Id] = password;
        }
        return PasswordAudit.Run(passwords);
    }
}
