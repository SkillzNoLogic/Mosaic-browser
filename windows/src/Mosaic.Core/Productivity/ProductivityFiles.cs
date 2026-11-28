using System.Text.Encodings.Web;
using System.Text.Json;

namespace Mosaic.Core.Productivity;

/// <summary>
/// The productivity area's files in the profile (<c>%LOCALAPPDATA%\Mosaic\Productivity</c>):
/// shortcuts, reader settings, palette recents, Speed Dial and its icons. Writes go to a
/// background thread, one after another, and replace the file atomically, so the UI thread
/// never waits for the disk and a crash never leaves half a file.
/// </summary>
public sealed class ProductivityFiles
{
    public const string ShortcutsFile = "shortcuts.json";
    public const string ReaderFile = "reader.json";
    public const string PaletteRecentsFile = "palette-recents.json";
    public const string SpeedDialFile = "speed-dial.json";
    public const string FaviconsFolder = "Favicons";

    /// <summary>How the files are written: indented, with "+" and non-ASCII titles kept readable.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object gate = new();
    private Task writes = Task.CompletedTask;

    public ProductivityFiles(string directory)
    {
        Directory = directory;
    }

    /// <summary>Where Mosaic keeps them by default.</summary>
    public static string DefaultDirectory => Path.Combine(AppPaths.DataRoot, "Productivity");

    public string Directory { get; }

    public string PathOf(string name) => Path.Combine(Directory, name);

    /// <summary>The file's text, or null when it does not exist or cannot be read. Call off the UI thread.</summary>
    public string? Read(string name)
    {
        try
        {
            var path = PathOf(name);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Queues writing <paramref name="text"/> (null deletes the file). <paramref name="failed"/> gets the error, on the thread pool.</summary>
    public Task Write(string name, string? text, Action<Exception>? failed = null)
    {
        var path = PathOf(name);
        lock (gate)
        {
            writes = writes.ContinueWith(_ =>
            {
                try
                {
                    if (text is null)
                    {
                        if (File.Exists(path)) File.Delete(path);
                        return;
                    }
                    System.IO.Directory.CreateDirectory(Directory);
                    var temporary = path + ".tmp";
                    File.WriteAllText(temporary, text);
                    File.Move(temporary, path, overwrite: true);
                }
                catch (Exception error)
                {
                    failed?.Invoke(error);
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            return writes;
        }
    }

    /// <summary>Completes once every queued write has finished (tests; shutdown).</summary>
    public Task Flush()
    {
        lock (gate) return writes;
    }
}
