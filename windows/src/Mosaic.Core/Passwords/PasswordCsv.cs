using System.Text;

namespace Mosaic.Core.Passwords;

/// <summary>
/// Password CSV files, as exported by Chrome (and Edge, Brave, Opera), Firefox, Safari and most
/// password managers, and as Mosaic exports them on both platforms.
/// Port of <c>Sources/MosaicCore/Passwords/PasswordCSV.swift</c>.
/// </summary>
/// <remarks>
/// Columns are found by their header, so the column order and extra columns do not matter:
/// Chrome/Edge <c>name,url,username,password,note</c>; Firefox
/// <c>url,username,password,httpRealm,formActionOrigin,guid,timeCreated,…</c>; Safari
/// <c>Title,URL,Username,Password,Notes,OTPAuth</c>.
/// </remarks>
public static class PasswordCsv
{
    public sealed record Entry(SiteOrigin Origin, string Username, string Password)
    {
        public CredentialID Id => new(Origin, Username);
    }

    /// <param name="Line">1-based line of the record in the file (the header is line 1).</param>
    /// <param name="Reason">Why the row was not imported.</param>
    public sealed record Skipped(int Line, string Reason);

    public sealed class ImportResult
    {
        public List<Entry> Entries { get; } = [];
        public List<Skipped> Skipped { get; } = [];
    }

    public enum ImportErrorKind { Empty, MissingColumns }

    public sealed class ImportException(ImportErrorKind kind) : Exception(kind == ImportErrorKind.Empty
        ? "The file is empty."
        : "This does not look like a password export: it needs columns named url, username and password.")
    {
        public ImportErrorKind Kind { get; } = kind;
    }

    internal static readonly string[] UrlColumns = ["url", "website", "origin", "login_uri", "login url", "web site", "hostname"];
    internal static readonly string[] UsernameColumns = ["username", "login", "user name", "login_username", "user", "email", "account"];
    internal static readonly string[] PasswordColumns = ["password", "login_password", "pass"];
    internal static readonly string[] RealmColumns = ["httprealm"];

    /// <summary>
    /// Reads an export. Rows that cannot be used (no password, not a web address, an HTTP
    /// authentication login, a duplicate) are skipped and reported, never guessed at.
    /// </summary>
    /// <exception cref="ImportException">Empty file, or no url and password columns.</exception>
    public static ImportResult ParseExport(string text)
    {
        var rows = Parse(text);
        if (rows.Count == 0) throw new ImportException(ImportErrorKind.Empty);
        var names = rows[0].Select(name => name.Trim(' ', '\t').ToLowerInvariant()).ToList();
        int? Column(string[] candidates)
        {
            foreach (var candidate in candidates)
            {
                var index = names.IndexOf(candidate);
                if (index >= 0) return index;
            }
            return null;
        }
        if (Column(UrlColumns) is not { } urlColumn || Column(PasswordColumns) is not { } passwordColumn)
        {
            throw new ImportException(ImportErrorKind.MissingColumns);
        }
        var usernameColumn = Column(UsernameColumns);
        var realmColumn = Column(RealmColumns);

        var result = new ImportResult();
        var seen = new HashSet<CredentialID>();
        for (var offset = 1; offset < rows.Count; offset++)
        {
            var row = rows[offset];
            var line = offset + 1;
            string Field(int? index) => index is { } i && i < row.Count ? row[i] : "";

            var password = Field(passwordColumn);
            var address = Field(urlColumn).Trim(' ', '\t');
            if (password.Length == 0)
            {
                result.Skipped.Add(new Skipped(line, "no password"));
                continue;
            }
            if (Field(realmColumn).Length > 0)
            {
                result.Skipped.Add(new Skipped(line, "HTTP authentication login (not supported)"));
                continue;
            }
            var origin = SiteOrigin.FromUrl(address)
                ?? (address.Contains("://", StringComparison.Ordinal) ? null : SiteOrigin.FromUserInput(address));
            if (origin is null || !(origin.Host.Contains('.') || origin.Host == "localhost" || origin.Host.Contains(':')))
            {
                result.Skipped.Add(new Skipped(line, $"not a web address ({(address.Length > 40 ? address[..40] : address)})"));
                continue;
            }
            var entry = new Entry(origin, Field(usernameColumn), password);
            if (!seen.Add(entry.Id))
            {
                result.Skipped.Add(new Skipped(line, "duplicate of an earlier row"));
                continue;
            }
            result.Entries.Add(entry);
        }
        return result;
    }

    /// <summary>
    /// Writes an export in Chrome's layout (<c>name,url,username,password</c>), which Chrome,
    /// Edge, Firefox, Safari and Mosaic all import. LF line ends, as Mosaic for macOS writes.
    /// </summary>
    public static string Export(IEnumerable<Entry> entries)
    {
        var text = new StringBuilder("name,url,username,password\n");
        foreach (var entry in entries.OrderBy(entry => entry.Id))
        {
            text.Append(string.Join(",", new[] { entry.Origin.Host, entry.Origin.String, entry.Username, entry.Password }.Select(Quote)));
            text.Append('\n');
        }
        return text.ToString();
    }

    internal static string Quote(string field)
    {
        var needsQuotes = field.AsSpan().IndexOfAny(",\"\n\r") >= 0 || field.StartsWith(' ') || field.EndsWith(' ');
        return needsQuotes ? "\"" + field.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : field;
    }

    /// <summary>
    /// RFC 4180 records: quoted fields, doubled quotes inside them, CRLF or LF line ends, a
    /// leading byte-order mark. Blank lines are dropped.
    /// </summary>
    public static List<List<string>> Parse(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var index = text.StartsWith('\uFEFF') ? 1 : 0;

        void EndRow()
        {
            row.Add(field.ToString());
            if (!(row.Count == 1 && row[0].Length == 0)) rows.Add(row);
            row = [];
            field.Clear();
        }

        while (index < text.Length)
        {
            var c = text[index++];
            if (quoted)
            {
                if (c == '"')
                {
                    if (index < text.Length && text[index] == '"')
                    {
                        field.Append('"');
                        index++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(c);
                }
                continue;
            }
            switch (c)
            {
                case '"' when field.Length == 0:
                    quoted = true;
                    break;
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    if (index < text.Length && text[index] == '\n') index++;
                    EndRow();
                    break;
                case '\n':
                    EndRow();
                    break;
                default:
                    field.Append(c);
                    break;
            }
        }
        if (field.Length > 0 || row.Count > 0) EndRow();
        return rows;
    }
}
