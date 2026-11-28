using System.Text;
using Microsoft.Data.Sqlite;

namespace Mosaic.Core.Library;

/// <summary>A library operation that cannot be done (no such folder, a folder into itself…).</summary>
public sealed class LibraryException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// A small wrapper around one SQLite connection (Microsoft.Data.Sqlite with its bundled
/// e_sqlite3, which ships win-x64 and win-arm64 natives).
/// </summary>
/// <remarks>
/// Port of <c>Sources/MosaicCore/Library/SQLiteDatabase.swift</c>. Not thread-safe by itself:
/// <see cref="LibraryStore"/> serialises every use behind its lock. SQL is written with bare
/// <c>?</c> placeholders (as on macOS), numbered here so Microsoft.Data.Sqlite can bind them;
/// explicit <c>?1</c>…<c>?9</c> are left alone.
/// </remarks>
internal sealed class SqliteDatabase : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly Dictionary<string, SqliteCommand> commands = new(StringComparer.Ordinal);

    /// <summary>Opens (creating if needed) the database at <paramref name="path"/>, or an in-memory one when null.</summary>
    public SqliteDatabase(string? path)
    {
        string source;
        if (path is null)
        {
            source = ":memory:";
        }
        else
        {
            if (Path.GetDirectoryName(path) is { Length: > 0 } directory) Directory.CreateDirectory(directory);
            source = path;
        }
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = source,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // One connection for the life of the store; pooling would keep the file open after Dispose.
            Pooling = false,
        };
        connection = new SqliteConnection(builder.ToString());
        try
        {
            connection.Open();
            Execute("PRAGMA busy_timeout = 2000");
            Execute("PRAGMA foreign_keys = ON");
            if (path is not null)
            {
                // WAL keeps readers and the writer out of each other's way and survives crashes.
                Execute("PRAGMA journal_mode = WAL");
                Execute("PRAGMA synchronous = NORMAL");
            }
        }
        catch (SqliteException error)
        {
            connection.Dispose();
            throw new LibraryException($"Could not open {source}: {error.Message}", error);
        }
    }

    public void Dispose()
    {
        foreach (var command in commands.Values) command.Dispose();
        commands.Clear();
        connection.Dispose();
    }

    public int UserVersion
    {
        get => (int)Scalar("PRAGMA user_version");
        set => Execute($"PRAGMA user_version = {value}");
    }

    /// <summary>Runs one or more statements without parameters.</summary>
    public void Execute(string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>Runs one statement with parameters. Returns the number of rows changed.</summary>
    public int Run(string sql, params object?[] arguments) => Command(sql, arguments).ExecuteNonQuery();

    public List<T> Query<T>(string sql, Func<Row, T> map, params object?[] arguments)
    {
        var rows = new List<T>();
        using var reader = Command(sql, arguments).ExecuteReader();
        var row = new Row(reader);
        while (reader.Read()) rows.Add(map(row));
        return rows;
    }

    public long Scalar(string sql, params object?[] arguments) =>
        Command(sql, arguments).ExecuteScalar() switch
        {
            null or DBNull => 0,
            long value => value,
            var other => Convert.ToInt64(other, System.Globalization.CultureInfo.InvariantCulture),
        };

    /// <summary>Runs <paramref name="body"/> in a transaction, rolling back if it throws.</summary>
    public T Transaction<T>(Func<T> body)
    {
        Execute("BEGIN IMMEDIATE");
        try
        {
            var result = body();
            Execute("COMMIT");
            return result;
        }
        catch
        {
            try { Execute("ROLLBACK"); } catch (SqliteException) { }
            throw;
        }
    }

    public void Transaction(Action body) => Transaction(() => { body(); return 0; });

    /// <summary>
    /// Commands are cached per SQL text: the hot paths (recording a visit, suggestion lookups)
    /// run the same few statements over and over, and a reused command keeps its prepared statement.
    /// </summary>
    private SqliteCommand Command(string sql, object?[] arguments)
    {
        if (!commands.TryGetValue(sql, out var command))
        {
            command = connection.CreateCommand();
            command.CommandText = NumberPlaceholders(sql, out var count);
            for (var i = 1; i <= count; i++) command.Parameters.Add(new SqliteParameter("?" + i, null));
            commands[sql] = command;
        }
        if (arguments.Length != command.Parameters.Count)
        {
            throw new ArgumentException($"Expected {command.Parameters.Count} arguments, got {arguments.Length}.", nameof(arguments));
        }
        for (var i = 0; i < arguments.Length; i++) command.Parameters[i].Value = Value(arguments[i]);
        return command;
    }

    private static object Value(object? argument) => argument switch
    {
        null => DBNull.Value,
        bool flag => flag ? 1L : 0L,
        int number => (long)number,
        DateTimeOffset time => time.ToUnixTimeMilliseconds(),
        _ => argument,
    };

    /// <summary>"a = ? AND b = ?" → "a = ?1 AND b = ?2"; SQL that already numbers its parameters is unchanged.</summary>
    internal static string NumberPlaceholders(string sql, out int count)
    {
        var builder = new StringBuilder(sql.Length + 8);
        var next = 0;
        var highest = 0;
        for (var i = 0; i < sql.Length; i++)
        {
            var c = sql[i];
            if (c == '\'')
            {
                // Copy string literals untouched.
                var end = sql.IndexOf('\'', i + 1);
                if (end < 0) end = sql.Length - 1;
                builder.Append(sql, i, end - i + 1);
                i = end;
                continue;
            }
            if (c != '?')
            {
                builder.Append(c);
                continue;
            }
            var digits = i + 1;
            while (digits < sql.Length && char.IsAsciiDigit(sql[digits])) digits++;
            if (digits > i + 1)
            {
                var number = int.Parse(sql.AsSpan(i + 1, digits - i - 1), System.Globalization.CultureInfo.InvariantCulture);
                highest = Math.Max(highest, number);
                builder.Append(sql, i, digits - i);
                i = digits - 1;
            }
            else
            {
                next++;
                builder.Append('?').Append(next.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        count = Math.Max(next, highest);
        return builder.ToString();
    }

    /// <summary>The current row of a query.</summary>
    public sealed class Row(SqliteDataReader reader)
    {
        public long Int(int column) => reader.IsDBNull(column) ? 0 : reader.GetInt64(column);
        public double Double(int column) => reader.IsDBNull(column) ? 0 : reader.GetDouble(column);
        public bool Bool(int column) => Int(column) != 0;
        public bool IsNull(int column) => reader.IsDBNull(column);
        public string String(int column) => OptionalString(column) ?? "";
        public string? OptionalString(int column) => reader.IsDBNull(column) ? null : reader.GetString(column);
        public DateTimeOffset Time(int column) => FromMillis(Int(column));
    }

    public static DateTimeOffset FromMillis(long millis) => DateTimeOffset.FromUnixTimeMilliseconds(millis);
}
