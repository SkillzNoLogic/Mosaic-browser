using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mosaic.Core.Passwords;

/// <summary>Encrypts and decrypts secrets. The app uses <see cref="DpapiProtector"/>; tests may use a stand-in.</summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] data);

    /// <exception cref="CryptographicException">The data was not protected by this protector (another user, another PC, damaged).</exception>
    byte[] Unprotect(byte[] data);
}

/// <summary>
/// Windows Data Protection (DPAPI) for the current user: <c>CryptProtectData</c> without UI.
/// Only the same Windows user on the same PC (or a roaming profile of that user) can decrypt;
/// the key is derived from the user's logon credentials, so a copied disk or another account
/// cannot read the data.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiProtector(string purpose) : ISecretProtector
{
    private const int CryptProtectUiForbidden = 0x1;
    private readonly byte[] entropy = Encoding.UTF8.GetBytes("Mosaic\0" + purpose);

    public byte[] Protect(byte[] data) => Run(data, protect: true);

    public byte[] Unprotect(byte[] data) => Run(data, protect: false);

    private byte[] Run(byte[] data, bool protect)
    {
        var input = GCHandle.Alloc(data, GCHandleType.Pinned);
        var salt = GCHandle.Alloc(entropy, GCHandleType.Pinned);
        try
        {
            var dataIn = new DataBlob { Size = data.Length, Data = input.AddrOfPinnedObject() };
            var entropyIn = new DataBlob { Size = entropy.Length, Data = salt.AddrOfPinnedObject() };
            var ok = protect
                ? CryptProtectData(ref dataIn, null, ref entropyIn, 0, 0, CryptProtectUiForbidden, out var output)
                : CryptUnprotectData(ref dataIn, 0, ref entropyIn, 0, 0, CryptProtectUiForbidden, out output);
            if (!ok) throw new CryptographicException(Marshal.GetLastPInvokeError());
            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                // Decrypted secrets do not linger in freed native memory.
                if (!protect) Marshal.Copy(new byte[output.Size], 0, output.Data, output.Size);
                return result;
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
        finally
        {
            input.Free();
            salt.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public nint Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, ref DataBlob entropy,
        nint reserved, nint prompt, int flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, nint description, ref DataBlob entropy,
        nint reserved, nint prompt, int flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}

/// <summary>
/// Saved passwords in one file per Mosaic profile (<c>&lt;data&gt;\Passwords\vault.dat</c>),
/// encrypted with DPAPI for the current Windows user. Nothing is ever written in plain text.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why DPAPI and not Credential Manager.</b> Both rest on the same protection (Credential
/// Manager stores its secrets with DPAPI under the user's key), so neither is stronger against
/// another program running as the same user. A file in the profile was chosen because:
/// it belongs to the profile, so an isolated <c>MOSAIC_DATA_DIR</c> profile (tests, portable
/// copies) can never see or change the real profile's passwords, and removing a profile removes
/// its passwords; Credential Manager is one flat list per Windows user whose target names compare
/// case-insensitively (two usernames differing only in case would collide), whose secrets are
/// limited to 2,560 bytes, and which is slow to enumerate and rewrite for an import of thousands
/// of logins; and Credential Manager writes can fail in sessions without a loaded logon session
/// (<c>ERROR_NO_SUCH_LOGON_SESSION</c>), as on some CI runners and services.
/// </para>
/// <para>
/// <b>Format.</b> The magic <c>MOSAICPW</c>, a version byte (1), then the DPAPI-protected UTF-8
/// JSON <c>{"items":[{"origin","username","created","modified","secret"}]}</c>, where each
/// <c>secret</c> is the password protected <em>again</em>, on its own (base64). Decrypting the file
/// therefore yields the list of sites and usernames but no password: each password is decrypted
/// only when it is asked for (filled, shown, exported), as the Keychain does on macOS.
/// </para>
/// <para>
/// A vault that cannot be decrypted (made by another Windows user or PC, or damaged) is never
/// overwritten: every operation reports the problem until <see cref="SetAsideUnreadable"/>
/// moves it aside.
/// </para>
/// </remarks>
public sealed class DpapiPasswordStore : IPasswordStore
{
    private static readonly byte[] Magic = "MOSAICPW"u8.ToArray();
    private const byte FormatVersion = 1;
    /// <summary>Far beyond any real password; keeps one bad value from bloating the vault.</summary>
    public const int MaxPasswordBytes = 16 * 1024;

    private sealed class Item
    {
        public required SiteOrigin Origin;
        public required string Username;
        public DateTimeOffset Created;
        public DateTimeOffset Modified;
        public required byte[] Secret;
    }

    private readonly ISecretProtector protector;
    private readonly TimeProvider time;
    private readonly Lock gate = new();
    private Dictionary<CredentialID, Item>? items;
    private string? unreadable;
    private int batchDepth;
    private bool dirty;

    public DpapiPasswordStore(string path, ISecretProtector protector, TimeProvider? time = null)
    {
        FilePath = path;
        this.protector = protector;
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>The store for a profile's data folder, protected with DPAPI. Windows only.</summary>
    [SupportedOSPlatform("windows")]
    public static DpapiPasswordStore ForProfile(string dataRoot) =>
        new(Path.Combine(dataRoot, "Passwords", "vault.dat"), new DpapiProtector("passwords"));

    public string FilePath { get; }

    /// <summary>Why the vault cannot be used, or null when it can.</summary>
    public string? UnreadableReason
    {
        get
        {
            lock (gate)
            {
                Load();
                return unreadable;
            }
        }
    }

    public IReadOnlyList<PasswordCredential> Credentials()
    {
        lock (gate)
        {
            return [.. Items().Values.Select(item => new PasswordCredential(item.Origin, item.Username, item.Created, item.Modified))];
        }
    }

    public string Password(CredentialID id)
    {
        byte[] secret;
        lock (gate)
        {
            if (!Items().TryGetValue(id, out var item)) throw new PasswordStoreException(PasswordStoreErrorKind.NotFound);
            secret = item.Secret;
        }
        try
        {
            var bytes = protector.Unprotect(secret);
            try
            {
                return Encoding.UTF8.GetString(bytes);
            }
            finally
            {
                Array.Clear(bytes);
            }
        }
        catch (CryptographicException error)
        {
            throw new PasswordStoreException(PasswordStoreErrorKind.Storage, "This password could not be decrypted.", error);
        }
    }

    public void Add(CredentialID id, string password)
    {
        var secret = Seal(password);
        lock (gate)
        {
            var all = Items();
            if (all.ContainsKey(id)) throw new PasswordStoreException(PasswordStoreErrorKind.Duplicate);
            var now = time.GetUtcNow();
            all[id] = new Item { Origin = id.Origin, Username = id.Username, Created = now, Modified = now, Secret = secret };
            Changed();
        }
    }

    public void Update(CredentialID id, string? username, string? password)
    {
        var secret = password is null ? null : Seal(password);
        lock (gate)
        {
            var all = Items();
            if (!all.TryGetValue(id, out var item)) throw new PasswordStoreException(PasswordStoreErrorKind.NotFound);
            var newId = id with { Username = username ?? id.Username };
            if (newId != id && all.ContainsKey(newId)) throw new PasswordStoreException(PasswordStoreErrorKind.Duplicate);
            if (newId == id && secret is null) return;
            all.Remove(id);
            item.Username = newId.Username;
            if (secret is not null) item.Secret = secret;
            item.Modified = time.GetUtcNow();
            all[newId] = item;
            Changed();
        }
    }

    public void Delete(CredentialID id)
    {
        lock (gate)
        {
            if (!Items().Remove(id)) throw new PasswordStoreException(PasswordStoreErrorKind.NotFound);
            Changed();
        }
    }

    public IDisposable BeginBatch()
    {
        lock (gate) batchDepth++;
        return new Batch(this);
    }

    /// <summary>
    /// Moves an unreadable vault aside (<c>vault.unreadable-&lt;time&gt;.dat</c>, kept in case it
    /// can be opened again as the user who made it) and starts an empty one.
    /// </summary>
    public void SetAsideUnreadable()
    {
        lock (gate)
        {
            Load();
            if (unreadable is null) return;
            var stamp = time.GetUtcNow().ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            var aside = Path.Combine(Path.GetDirectoryName(FilePath)!, $"vault.unreadable-{stamp}.dat");
            File.Move(FilePath, aside, overwrite: true);
            unreadable = null;
            items = [];
        }
    }

    private sealed class Batch(DpapiPasswordStore store) : IDisposable
    {
        private bool done;

        public void Dispose()
        {
            if (done) return;
            done = true;
            lock (store.gate)
            {
                store.batchDepth--;
                if (store.batchDepth == 0 && store.dirty) store.Write();
            }
        }
    }

    // ---- Internals (callers hold the lock) ----

    private byte[] Seal(string password)
    {
        var bytes = Encoding.UTF8.GetBytes(password);
        try
        {
            if (bytes.Length > MaxPasswordBytes) throw new PasswordStoreException(PasswordStoreErrorKind.TooLarge);
            return protector.Protect(bytes);
        }
        catch (CryptographicException error)
        {
            throw new PasswordStoreException(PasswordStoreErrorKind.Storage, $"Windows could not encrypt the password: {error.Message}", error);
        }
        finally
        {
            Array.Clear(bytes);
        }
    }

    private Dictionary<CredentialID, Item> Items()
    {
        Load();
        if (unreadable is not null) throw new PasswordStoreException(PasswordStoreErrorKind.Storage, unreadable);
        return items!;
    }

    private void Changed()
    {
        dirty = true;
        if (batchDepth == 0) Write();
    }

    private void Load()
    {
        if (items is not null || unreadable is not null) return;
        byte[] file;
        try
        {
            file = File.ReadAllBytes(FilePath);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            items = [];
            return;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            unreadable = $"The saved passwords could not be read: {error.Message}";
            return;
        }
        if (file.Length < Magic.Length + 1 || !file.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            unreadable = "The saved passwords file is damaged.";
            return;
        }
        if (file[Magic.Length] != FormatVersion)
        {
            unreadable = "The saved passwords were written by a newer version of Mosaic.";
            return;
        }
        byte[] json;
        try
        {
            json = protector.Unprotect(file[(Magic.Length + 1)..]);
        }
        catch (CryptographicException)
        {
            unreadable = "The saved passwords were encrypted by another Windows user or on another PC, so they cannot be opened here.";
            return;
        }
        try
        {
            items = Parse(json);
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException)
        {
            unreadable = "The saved passwords file is damaged.";
        }
        finally
        {
            Array.Clear(json);
        }
    }

    private static Dictionary<CredentialID, Item> Parse(byte[] json)
    {
        var result = new Dictionary<CredentialID, Item>();
        var root = JsonNode.Parse(json) as JsonObject ?? throw new FormatException("not an object");
        foreach (var node in root["items"] as JsonArray ?? [])
        {
            if (node is not JsonObject entry) continue;
            var origin = SiteOrigin.FromUrl((string?)entry["origin"]);
            var secret = (string?)entry["secret"];
            if (origin is null || secret is null) continue;
            var item = new Item
            {
                Origin = origin,
                Username = (string?)entry["username"] ?? "",
                Created = PasswordDates.Parse((string?)entry["created"]) ?? DateTimeOffset.UnixEpoch,
                Modified = PasswordDates.Parse((string?)entry["modified"]) ?? DateTimeOffset.UnixEpoch,
                Secret = Convert.FromBase64String(secret),
            };
            result[new CredentialID(item.Origin, item.Username)] = item;
        }
        return result;
    }

    private void Write()
    {
        var array = new JsonArray();
        foreach (var item in items!.Values.OrderBy(item => new CredentialID(item.Origin, item.Username)))
        {
            array.Add(new JsonObject
            {
                ["origin"] = item.Origin.String,
                ["username"] = item.Username,
                ["created"] = PasswordDates.Format(item.Created),
                ["modified"] = PasswordDates.Format(item.Modified),
                ["secret"] = Convert.ToBase64String(item.Secret),
            });
        }
        var json = Encoding.UTF8.GetBytes(new JsonObject { ["items"] = array }.ToJsonString());
        byte[] sealedJson;
        try
        {
            sealedJson = protector.Protect(json);
        }
        catch (CryptographicException error)
        {
            throw new PasswordStoreException(PasswordStoreErrorKind.Storage, $"Windows could not encrypt the saved passwords: {error.Message}", error);
        }
        finally
        {
            Array.Clear(json);
        }

        var directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        var temporary = FilePath + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(Magic);
                stream.WriteByte(FormatVersion);
                stream.Write(sealedJson);
                stream.Flush(flushToDisk: true);
            }
            // Replaces the old file in one step, so a crash leaves either the old or the new vault.
            File.Move(temporary, FilePath, overwrite: true);
            dirty = false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new PasswordStoreException(PasswordStoreErrorKind.Storage, $"The saved passwords could not be written: {error.Message}", error);
        }
    }
}

/// <summary>Dates as macOS writes them (<c>JSONEncoder.DateEncodingStrategy.iso8601</c>): whole seconds, UTC.</summary>
public static class PasswordDates
{
    public static string Format(DateTimeOffset date) =>
        date.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    public static DateTimeOffset? Parse(string? text) =>
        DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var date)
            ? date : null;
}
