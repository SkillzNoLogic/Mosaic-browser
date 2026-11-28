using System.Text.Json.Nodes;

namespace Mosaic.Core.WindowsIntegration;

/// <summary>
/// The current user's registry (<c>HKEY_CURRENT_USER</c>), as far as the default-browser
/// registration needs it. Key paths are relative to HKCU; a null value name is the key's default
/// value. The app implements it with <c>Microsoft.Win32.Registry</c>; tests use <see cref="InMemoryRegistry"/>.
/// </summary>
public interface IRegistryStore
{
    string? GetString(string key, string? name);
    void SetString(string key, string? name, string value);
    void DeleteValue(string key, string name);
    void DeleteTree(string key);
    bool KeyExists(string key);
}

/// <summary>
/// Registers Mosaic as a web browser for the current user, the way Windows expects browsers to
/// (learn.microsoft.com: "Registering Programs with Client Types", "Default Programs"):
/// <list type="bullet">
/// <item><c>Software\Clients\StartMenuInternet\Mosaic</c> with <c>Capabilities</c> (URL and file associations),</item>
/// <item><c>Software\RegisteredApplications\Mosaic</c> pointing at those capabilities,</item>
/// <item>ProgIDs <c>MosaicHTML</c> (files) and <c>MosaicURL</c> (http/https) whose open command is
/// <c>"…\Mosaic.exe" --single-argument %1</c>,</item>
/// <item><c>OpenWithProgids</c> on each file type so Mosaic appears under "Open with".</item>
/// </list>
/// Everything is per user (HKCU): no elevation, and nothing is written until the user asks for
/// it. Since Windows 8 only the user can make an app the default, so after registering, Mosaic
/// opens Settings › Default apps at its own page (<see cref="SettingsUri"/>).
/// </summary>
public sealed class DefaultBrowserRegistration
{
    public const string DefaultAppName = "Mosaic";

    /// <summary>URL schemes Mosaic registers for. (FTP is gone from both engines; mailto is a mail client's.)</summary>
    public static readonly IReadOnlyList<string> UrlSchemes = ["http", "https"];

    /// <summary>File types Mosaic registers for: web pages, PDF, SVG and WebP, which both engines display.</summary>
    public static readonly IReadOnlyList<string> FileExtensions = [".htm", ".html", ".shtml", ".xht", ".xhtml", ".pdf", ".svg", ".webp"];

    public const string RegisteredApplicationsKey = @"Software\RegisteredApplications";
    private const string Classes = @"Software\Classes";

    public DefaultBrowserRegistration(string executablePath, string appName = DefaultAppName)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) throw new ArgumentException("The executable path is required.", nameof(executablePath));
        if (string.IsNullOrWhiteSpace(appName) || appName.IndexOfAny(['\\', '/', '"']) >= 0)
            throw new ArgumentException("The registration name must be a plain name.", nameof(appName));
        ExecutablePath = executablePath;
        AppName = appName;
    }

    public string ExecutablePath { get; }
    public string AppName { get; }

    public string HtmlProgId => AppName + "HTML";
    public string UrlProgId => AppName + "URL";
    public string ClientKey => @"Software\Clients\StartMenuInternet\" + AppName;
    public string CapabilitiesKey => ClientKey + @"\Capabilities";

    /// <summary>The shell's open command. See <see cref="ActivationRequest.SingleArgumentSwitch"/>.</summary>
    public string OpenCommand => $"\"{ExecutablePath}\" {ActivationRequest.SingleArgumentSwitch} %1";

    public string Icon => ExecutablePath + ",0";

    /// <summary>Settings › Apps › Default apps › Mosaic (Windows 11; Windows 10 opens Default apps).</summary>
    public string SettingsUri => "ms-settings:defaultapps?registeredAppUser=" + Uri.EscapeDataString(AppName);

    /// <summary>A registry value to write.</summary>
    public readonly record struct Value(string Key, string? Name, string Data);

    /// <summary>Everything <see cref="Register"/> writes, in order.</summary>
    public IReadOnlyList<Value> Values()
    {
        var values = new List<Value>
        {
            new(ClientKey, null, AppName),
            new(CapabilitiesKey, "ApplicationName", AppName),
            new(CapabilitiesKey, "ApplicationDescription", "Mosaic web browser, with the Mozilla and Chromium engines."),
            new(CapabilitiesKey, "ApplicationIcon", Icon),
            new(CapabilitiesKey + @"\StartMenu", "StartMenuInternet", AppName),
            new(ClientKey + @"\DefaultIcon", null, Icon),
            new(ClientKey + @"\shell\open\command", null, $"\"{ExecutablePath}\""),
        };
        foreach (var extension in FileExtensions) values.Add(new(CapabilitiesKey + @"\FileAssociations", extension, HtmlProgId));
        foreach (var scheme in UrlSchemes) values.Add(new(CapabilitiesKey + @"\URLAssociations", scheme, UrlProgId));
        values.Add(new(RegisteredApplicationsKey, AppName, CapabilitiesKey));

        foreach (var (progId, description) in new[] { (HtmlProgId, "Mosaic HTML Document"), (UrlProgId, "Mosaic URL") })
        {
            var key = $@"{Classes}\{progId}";
            values.Add(new(key, null, description));
            if (progId == UrlProgId) values.Add(new(key, "URL Protocol", ""));
            values.Add(new(key + @"\Application", "ApplicationName", AppName));
            values.Add(new(key + @"\Application", "ApplicationIcon", Icon));
            values.Add(new(key + @"\Application", "ApplicationCompany", "Mosaic"));
            values.Add(new(key + @"\DefaultIcon", null, Icon));
            values.Add(new(key + @"\shell\open\command", null, OpenCommand));
        }
        foreach (var extension in FileExtensions) values.Add(new($@"{Classes}\{extension}\OpenWithProgids", HtmlProgId, ""));
        return values;
    }

    /// <summary>Writes the registration (idempotent; also repairs a registration pointing at another copy).</summary>
    public void Register(IRegistryStore registry)
    {
        foreach (var value in Values()) registry.SetString(value.Key, value.Name, value.Data);
    }

    /// <summary>Removes everything <see cref="Register"/> wrote, and nothing else.</summary>
    public void Unregister(IRegistryStore registry)
    {
        registry.DeleteTree(ClientKey);
        registry.DeleteValue(RegisteredApplicationsKey, AppName);
        registry.DeleteTree($@"{Classes}\{HtmlProgId}");
        registry.DeleteTree($@"{Classes}\{UrlProgId}");
        foreach (var extension in FileExtensions) registry.DeleteValue($@"{Classes}\{extension}\OpenWithProgids", HtmlProgId);
    }

    /// <summary>Where Windows keeps the user's choice for a URL scheme or file extension.</summary>
    public static string UserChoiceKey(string association) => association.StartsWith('.')
        ? $@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{association}"
        : $@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\{association}";

    /// <summary>
    /// The ProgID the user chose for a scheme or extension, from the registry. Windows 11 (2025
    /// updates) also writes <c>UserChoiceLatest</c>; it wins when present.
    /// </summary>
    public static string? UserChoice(IRegistryStore registry, string association)
    {
        var key = UserChoiceKey(association);
        return registry.GetString(key + @"\UserChoiceLatest", "ProgId")
            ?? registry.GetString(key + @"\UserChoiceLatest\ProgId", "ProgId")
            ?? registry.GetString(key + @"\UserChoice", "ProgId");
    }

    public DefaultBrowserStatus Status(IRegistryStore registry)
    {
        var command = registry.GetString($@"{Classes}\{UrlProgId}\shell\open\command", null);
        var registered = registry.KeyExists(CapabilitiesKey) && registry.GetString(RegisteredApplicationsKey, AppName) is not null;
        var choices = new Dictionary<string, string?>();
        foreach (var scheme in UrlSchemes) choices[scheme] = UserChoice(registry, scheme);
        foreach (var extension in FileExtensions) choices[extension] = UserChoice(registry, extension);
        return new DefaultBrowserStatus(
            AppName,
            registered,
            registered && string.Equals(command, OpenCommand, StringComparison.OrdinalIgnoreCase),
            command,
            choices,
            UrlSchemes.All(scheme => string.Equals(choices[scheme], UrlProgId, StringComparison.OrdinalIgnoreCase)),
            FileExtensions.Where(e => string.Equals(choices[e], HtmlProgId, StringComparison.OrdinalIgnoreCase)).ToList());
    }
}

/// <summary>Mosaic's standing as the user's browser.</summary>
/// <param name="Registered">Mosaic's capabilities are registered for this user.</param>
/// <param name="RegisteredForThisCopy">…and the open command points at this Mosaic.exe.</param>
/// <param name="Choices">The user's ProgID for each scheme and extension Mosaic registers (null: none chosen).</param>
/// <param name="IsDefault">Mosaic is the user's choice for both http and https.</param>
/// <param name="DefaultFor">File extensions Mosaic is the user's choice for.</param>
public sealed record DefaultBrowserStatus(
    string AppName,
    bool Registered,
    bool RegisteredForThisCopy,
    string? OpenCommand,
    IReadOnlyDictionary<string, string?> Choices,
    bool IsDefault,
    IReadOnlyList<string> DefaultFor)
{
    public JsonObject ToJson()
    {
        var choices = new JsonObject();
        foreach (var (association, progId) in Choices) choices[association] = progId;
        return new JsonObject
        {
            ["appName"] = AppName,
            ["registered"] = Registered,
            ["registeredForThisCopy"] = RegisteredForThisCopy,
            ["openCommand"] = OpenCommand,
            ["isDefault"] = IsDefault,
            ["defaultFor"] = new JsonArray([.. DefaultFor.Select(e => (JsonNode)JsonValue.Create(e))]),
            ["choices"] = choices,
        };
    }
}

/// <summary>An in-memory <see cref="IRegistryStore"/> (tests, and dry runs).</summary>
public sealed class InMemoryRegistry : IRegistryStore
{
    private readonly Dictionary<string, Dictionary<string, string>> keys = new(StringComparer.OrdinalIgnoreCase);

    private static string Normalize(string key) => key.Trim('\\');

    public IReadOnlyCollection<string> Keys => keys.Keys;

    public string? GetString(string key, string? name) =>
        keys.TryGetValue(Normalize(key), out var values) && values.TryGetValue(name ?? "", out var data) ? data : null;

    public void SetString(string key, string? name, string value)
    {
        key = Normalize(key);
        // Creating a key creates its parents, as the real registry does.
        var parts = key.Split('\\');
        for (var i = 1; i <= parts.Length; i++)
        {
            var path = string.Join('\\', parts.Take(i));
            if (!keys.ContainsKey(path)) keys[path] = new(StringComparer.OrdinalIgnoreCase);
        }
        keys[key][name ?? ""] = value;
    }

    public void DeleteValue(string key, string name)
    {
        if (keys.TryGetValue(Normalize(key), out var values)) values.Remove(name);
    }

    public void DeleteTree(string key)
    {
        key = Normalize(key);
        foreach (var path in keys.Keys.Where(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)
                     || k.StartsWith(key + "\\", StringComparison.OrdinalIgnoreCase)).ToList())
            keys.Remove(path);
    }

    public bool KeyExists(string key) => keys.ContainsKey(Normalize(key));

    /// <summary>Number of values stored under all keys.</summary>
    public int ValueCount => keys.Values.Sum(v => v.Count);
}
