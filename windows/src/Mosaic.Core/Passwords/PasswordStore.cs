namespace Mosaic.Core.Passwords;

/// <summary>Identifies a saved password: one per origin and username.</summary>
public sealed record CredentialID(SiteOrigin Origin, string Username) : IComparable<CredentialID>
{
    /// <summary>Stable text form, used as a key for non-secret metadata (last used). Same as macOS.</summary>
    public string Key => $"{Origin.String} {Username}";

    public override string ToString() => Key;

    public int CompareTo(CredentialID? other)
    {
        if (other is null) return 1;
        var byOrigin = Origin.CompareTo(other.Origin);
        return byOrigin != 0 ? byOrigin
            : string.CompareOrdinal(Username.ToLowerInvariant(), other.Username.ToLowerInvariant());
    }
}

/// <summary>A saved password's non-secret attributes. The password itself is only read on demand.</summary>
public sealed record PasswordCredential(SiteOrigin Origin, string Username, DateTimeOffset? Created = null, DateTimeOffset? Modified = null)
{
    public CredentialID Id => new(Origin, Username);
}

public enum PasswordStoreErrorKind
{
    Duplicate,
    NotFound,
    Cancelled,
    /// <summary>The secret is too large for the store.</summary>
    TooLarge,
    /// <summary>The store itself failed (unreadable vault, disk error).</summary>
    Storage,
}

public sealed class PasswordStoreException : Exception
{
    public PasswordStoreException(PasswordStoreErrorKind kind, string? detail = null, Exception? inner = null)
        : base(MessageFor(kind, detail), inner)
    {
        Kind = kind;
    }

    public PasswordStoreErrorKind Kind { get; }

    private static string MessageFor(PasswordStoreErrorKind kind, string? detail) => kind switch
    {
        PasswordStoreErrorKind.Duplicate => "A password for this username is already saved for this site.",
        PasswordStoreErrorKind.NotFound => "The password is no longer saved.",
        PasswordStoreErrorKind.Cancelled => "Access to the password was not allowed.",
        PasswordStoreErrorKind.TooLarge => "The password is too long to store.",
        _ => detail ?? "The password store failed.",
    };
}

/// <summary>
/// Where passwords live. The app uses <see cref="DpapiPasswordStore"/>; tests use
/// <see cref="InMemoryPasswordStore"/> (or the DPAPI store with a stand-in protector).
/// </summary>
public interface IPasswordStore
{
    /// <summary>Every saved credential (no secrets), in no particular order.</summary>
    IReadOnlyList<PasswordCredential> Credentials();

    string Password(CredentialID id);

    /// <summary>Throws <see cref="PasswordStoreErrorKind.Duplicate"/> if the origin and username are already saved.</summary>
    void Add(CredentialID id, string password);

    /// <summary>Changes the username and/or the password of a saved credential.</summary>
    void Update(CredentialID id, string? username, string? password);

    void Delete(CredentialID id);

    /// <summary>
    /// Groups many changes (an import) so the store writes once at the end, not per change.
    /// Dispose the result to finish the batch.
    /// </summary>
    IDisposable BeginBatch();
}

/// <summary>A store that keeps everything in memory. For tests and previews.</summary>
public sealed class InMemoryPasswordStore(TimeProvider? time = null) : IPasswordStore
{
    private sealed record Item(string Password, DateTimeOffset Created, DateTimeOffset Modified);

    private readonly Dictionary<CredentialID, Item> items = [];
    private readonly Lock gate = new();
    private readonly TimeProvider time = time ?? TimeProvider.System;

    public IReadOnlyList<PasswordCredential> Credentials()
    {
        lock (gate) return [.. items.Select(pair => new PasswordCredential(pair.Key.Origin, pair.Key.Username, pair.Value.Created, pair.Value.Modified))];
    }

    public string Password(CredentialID id)
    {
        lock (gate)
        {
            return items.TryGetValue(id, out var item) ? item.Password : throw new PasswordStoreException(PasswordStoreErrorKind.NotFound);
        }
    }

    public void Add(CredentialID id, string password)
    {
        lock (gate)
        {
            if (items.ContainsKey(id)) throw new PasswordStoreException(PasswordStoreErrorKind.Duplicate);
            var now = time.GetUtcNow();
            items[id] = new Item(password, now, now);
        }
    }

    public void Update(CredentialID id, string? username, string? password)
    {
        lock (gate)
        {
            if (!items.TryGetValue(id, out var item)) throw new PasswordStoreException(PasswordStoreErrorKind.NotFound);
            var newId = id with { Username = username ?? id.Username };
            if (newId != id && items.ContainsKey(newId)) throw new PasswordStoreException(PasswordStoreErrorKind.Duplicate);
            items.Remove(id);
            items[newId] = item with { Password = password ?? item.Password, Modified = time.GetUtcNow() };
        }
    }

    public void Delete(CredentialID id)
    {
        lock (gate)
        {
            if (!items.Remove(id)) throw new PasswordStoreException(PasswordStoreErrorKind.NotFound);
        }
    }

    public IDisposable BeginBatch() => NoBatch.Instance;

    private sealed class NoBatch : IDisposable
    {
        public static readonly NoBatch Instance = new();
        public void Dispose() { }
    }
}
