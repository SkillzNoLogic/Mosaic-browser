using System.Text.Json;
using System.Text.Json.Nodes;
using Mosaic.Core.Browser;

namespace Mosaic.Core.Downloads;

/// <summary>
/// Settings › Downloads, kept in <c>%LOCALAPPDATA%\Mosaic\Downloads\settings.json</c>.
/// </summary>
/// <remarks>
/// Port of <c>DownloadSettings.swift</c>; the JSON keys are the macOS preference keys without
/// their <c>downloads.</c> prefix. Changes are saved on a background thread.
/// </remarks>
public sealed class DownloadSettings : ObservableObject
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private readonly string? filePath;
    private readonly object saveLock = new();
    private int saveGeneration;
    private Task lastSave = Task.CompletedTask;

    private string folderPath;
    private bool askWhereToSave;
    private bool openSafeFiles;
    private bool notifyWhenFinished = true;
    private DownloadOrganizationRules rules = new();
    private FdmMode fdmMode = FdmMode.Off;
    private int fdmMinimumMB;
    private bool fdmPassCookies;
    private bool fdmContextMenu = true;

    /// <summary>
    /// The Windows Downloads known folder (FOLDERID_Downloads, which the user may have moved).
    /// The app sets this to <c>SHGetKnownFolderPath</c>; the fallback is <c>%USERPROFILE%\Downloads</c>.
    /// </summary>
    public static Func<string> DefaultFolderProvider { get; set; } =
        () => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    public static string DefaultFolder
    {
        get
        {
            try
            {
                return DefaultFolderProvider() is { Length: > 0 } folder ? folder : Fallback();
            }
            catch (Exception)
            {
                return Fallback();
            }

            static string Fallback() =>
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }

    /// <param name="filePath">Where the settings live; null keeps them in memory only (tests).</param>
    public DownloadSettings(string? filePath)
    {
        this.filePath = filePath;
        folderPath = DefaultFolder;
        if (filePath is not null) Load(filePath);
    }

    /// <summary>Called when a setting the engines need to know about changed (folder, FDM menu, FDM mode).</summary>
    public event Action? EngineSettingsChanged;

    /// <summary>Where downloads are saved. Default: the Downloads known folder.</summary>
    public string FolderPath
    {
        get => folderPath;
        set
        {
            if (!Set(ref folderPath, string.IsNullOrWhiteSpace(value) ? DefaultFolder : value)) return;
            Changed(engines: true);
        }
    }

    public bool AskWhereToSave { get => askWhereToSave; set { if (Set(ref askWhereToSave, value)) Changed(); } }
    public bool OpenSafeFiles { get => openSafeFiles; set { if (Set(ref openSafeFiles, value)) Changed(); } }
    public bool NotifyWhenFinished { get => notifyWhenFinished; set { if (Set(ref notifyWhenFinished, value)) Changed(); } }

    public DownloadOrganizationRules Rules
    {
        get => rules;
        set { if (Set(ref rules, value ?? new DownloadOrganizationRules())) Changed(); }
    }

    public FdmMode FdmMode { get => fdmMode; set { if (Set(ref fdmMode, value)) Changed(engines: true); } }

    /// <summary>Downloads smaller than this many megabytes stay in Mosaic; 0 sends every size.</summary>
    public int FdmMinimumMB { get => fdmMinimumMB; set { if (Set(ref fdmMinimumMB, Math.Max(0, value))) Changed(); } }

    public bool FdmPassCookies { get => fdmPassCookies; set { if (Set(ref fdmPassCookies, value)) Changed(); } }

    public bool FdmContextMenu { get => fdmContextMenu; set { if (Set(ref fdmContextMenu, value)) Changed(engines: true); } }

    public FdmPolicy FdmPolicy => new(fdmMode, (long)Math.Max(0, fdmMinimumMB) * 1_000_000);

    /// <summary>The download folder for display.</summary>
    public string FolderDisplayPath => folderPath;

    /// <summary>Waits for pending saves (tests, shutdown).</summary>
    public void Flush() => lastSave.Wait();

    private void Changed(bool engines = false)
    {
        Save();
        if (engines) EngineSettingsChanged?.Invoke();
    }

    private void Load(string path)
    {
        JsonObject? json;
        try
        {
            json = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return;
        }
        if (json is null) return;
        if (Text(json, "folder") is { Length: > 0 } folder) folderPath = folder;
        askWhereToSave = Flag(json, "askWhereToSave") ?? false;
        openSafeFiles = Flag(json, "openSafeFiles") ?? false;
        notifyWhenFinished = Flag(json, "notifyWhenFinished") ?? true;
        rules = DownloadOrganizationRules.FromJson(json["organizationRules"]);
        fdmMode = FdmModes.FromId(Text(json, "fdm.mode")) ?? FdmMode.Off;
        fdmMinimumMB = json["fdm.minimumMB"] is JsonValue mb && mb.TryGetValue(out int megabytes) ? Math.Max(0, megabytes) : 0;
        fdmPassCookies = Flag(json, "fdm.passCookies") ?? false;
        fdmContextMenu = Flag(json, "fdm.contextMenu") ?? true;
    }

    internal JsonObject ToJson() => new()
    {
        ["folder"] = folderPath,
        ["askWhereToSave"] = askWhereToSave,
        ["openSafeFiles"] = openSafeFiles,
        ["notifyWhenFinished"] = notifyWhenFinished,
        ["organizationRules"] = rules.ToJson(),
        ["fdm.mode"] = fdmMode.Id(),
        ["fdm.minimumMB"] = fdmMinimumMB,
        ["fdm.passCookies"] = fdmPassCookies,
        ["fdm.contextMenu"] = fdmContextMenu,
    };

    private void Save()
    {
        if (filePath is null) return;
        var text = ToJson().ToJsonString(Indented);
        var generation = Interlocked.Increment(ref saveGeneration);
        var path = filePath;
        lastSave = Task.Run(() =>
        {
            lock (saveLock)
            {
                // A newer change has its own save queued; writing this one would be wasted work.
                if (generation != Volatile.Read(ref saveGeneration)) return;
                try
                {
                    DownloadHistoryStore.Write(path, text);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                }
            }
        });
    }

    private static string? Text(JsonObject json, string key) =>
        json[key] is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static bool? Flag(JsonObject json, string key) =>
        json[key] is JsonValue value && value.TryGetValue(out bool flag) ? flag : null;
}
