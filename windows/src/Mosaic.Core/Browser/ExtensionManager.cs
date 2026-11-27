using System.Collections.ObjectModel;
using System.Net;
using Mosaic.Core.Extensions;
using Mosaic.Core.Gecko;

namespace Mosaic.Core.Browser;

/// <summary>
/// Installs Chrome extensions (<c>.crx</c>) and keeps them loaded in the Gecko engine.
/// </summary>
/// <remarks>
/// <para>A package is unpacked and converted once, on install (<see cref="ExtensionStore"/>). The
/// converted copy is then loaded into Gecko every time the engine starts.</para>
/// <para>On Windows the unconverted copy is also offered to the Chromium engine. That part is
/// experimental: its failures are recorded per extension (<see cref="ExtensionItem.ChromiumError"/>)
/// and never reach <see cref="ErrorMessage"/> or the Gecko path.</para>
/// <para>No public method throws; problems are reported through <see cref="ErrorMessage"/>.</para>
/// </remarks>
public sealed class ExtensionManager : ObservableObject
{
    private readonly IGeckoEngine gecko;
    private readonly IExtensionLibrary? library;
    private readonly BrowserSettings settings;
    private readonly IChromiumExtensionHost? chromium;
    private readonly BrowserRules rules;
    private readonly HttpClient http;

    /// <summary>The library is not thread-safe and installs run off the UI thread, so every use takes this.</summary>
    private readonly SemaphoreSlim libraryGate = new(1, 1);
    /// <summary>Keeps a profile sync and an install's own Chromium load from interleaving.</summary>
    private readonly SemaphoreSlim chromiumGate = new(1, 1);

    /// <summary>Gecko add-on IDs currently loaded in the running engine.</summary>
    private readonly HashSet<string> loaded = new(StringComparer.Ordinal);
    /// <summary>Chrome ID → Chromium's ID, for extensions present in the Chromium profile right now.</summary>
    private readonly Dictionary<string, string> inChromium = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> chromiumErrors = new(StringComparer.Ordinal);

    private IReadOnlyList<InstalledExtension> extensions = [];
    private bool isBusy;
    private string? errorMessage;

    /// <param name="gecko">The engine extensions are loaded into.</param>
    /// <param name="openLibrary">Opens the library; if it throws, extensions are unavailable for this run.</param>
    /// <param name="settings">Remembers which profile extensions are Mosaic's (see <see cref="BrowserSettings.ChromiumExtensionIds"/>).</param>
    /// <param name="chromium">Null when the Chromium engine cannot take extensions at all.</param>
    /// <param name="httpHandler">Transport for Chrome Web Store downloads; tests substitute their own.</param>
    /// <param name="rules">Chrome Web Store addressing; the real rules when null.</param>
    public ExtensionManager(
        IGeckoEngine gecko,
        Func<IExtensionLibrary> openLibrary,
        BrowserSettings settings,
        IChromiumExtensionHost? chromium = null,
        HttpMessageHandler? httpHandler = null,
        BrowserRules? rules = null)
    {
        this.gecko = gecko;
        this.settings = settings;
        this.chromium = chromium;
        this.rules = rules ?? BrowserRules.Default;
        http = httpHandler is null ? new HttpClient() : new HttpClient(httpHandler);
        http.Timeout = TimeSpan.FromSeconds(60);

        try
        {
            library = openLibrary();
            extensions = library.Extensions;
        }
        catch (Exception error)
        {
            // The browser is fully usable without extensions; say why they are missing.
            errorMessage = $"Extensions are unavailable: {error.Message}";
        }

        SyncItems();
        // Nothing is running in an engine that has gone away.
        gecko.Stopped += () =>
        {
            loaded.Clear();
            SyncItems();
        };
        if (chromium is not null) chromium.OnReady = SyncChromiumAsync;
    }

    public IReadOnlyList<InstalledExtension> Extensions
    {
        get => extensions;
        private set
        {
            extensions = value;
            Notify();
            Notify(nameof(HasExtensions));
        }
    }

    /// <summary>The rows of the Extensions dialog, in the same order as <see cref="Extensions"/>.</summary>
    public ObservableCollection<ExtensionItem> Items { get; } = [];

    public bool HasExtensions => extensions.Count > 0;

    /// <summary>True while a package is being downloaded and installed.</summary>
    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (Set(ref isBusy, value)) Notify(nameof(IsIdle));
        }
    }

    public bool IsIdle => !isBusy;

    public string? ErrorMessage
    {
        get => errorMessage;
        set
        {
            if (Set(ref errorMessage, value)) Notify(nameof(HasError));
        }
    }

    public bool HasError => errorMessage is not null;

    /// <summary>Whether the extension is loaded in the running Gecko engine.</summary>
    public bool IsLoaded(InstalledExtension extension) => loaded.Contains(extension.GeckoId);

    public bool IsInstalled(string chromeId) => extensions.Any(extension => extension.Id == chromeId);

    // ---- Installing ----

    /// <summary>Installs from a Chrome Web Store link or a bare extension ID.</summary>
    /// <returns>True when the extension was installed and nothing went wrong along the way.</returns>
    public Task<bool> InstallFromWebStoreAsync(string input)
    {
        if (rules.WebStoreExtensionId(input) is not { } id)
        {
            ErrorMessage = "That is not a Chrome Web Store link or extension ID.";
            return Task.FromResult(false);
        }
        return InstallAsync(async () => (await DownloadAsync(id), id));
    }

    /// <summary>Installs a <c>.crx</c> (or zipped extension) from disk.</summary>
    /// <returns>True when the extension was installed and nothing went wrong along the way.</returns>
    public Task<bool> InstallFileAsync(string path) =>
        InstallAsync(async () => (await File.ReadAllBytesAsync(path), null));

    private async Task<byte[]> DownloadAsync(string id)
    {
        using var response = await http.GetAsync(rules.WebStoreDownloadUrl(id));
        var package = response.StatusCode == HttpStatusCode.OK ? await response.Content.ReadAsByteArrayAsync() : [];
        if (package.Length == 0)
        {
            throw new ExtensionStoreException(
                $"The Chrome Web Store did not provide this extension (HTTP {(int)response.StatusCode}). " +
                "It may be unlisted, paid, or removed.");
        }
        return package;
    }

    private async Task<bool> InstallAsync(Func<Task<(byte[] Package, string? KnownId)>> fetch)
    {
        if (library is null || IsBusy) return false;
        IsBusy = true;
        ErrorMessage = null;
        await libraryGate.WaitAsync();
        try
        {
            var (package, knownId) = await fetch();
            // Unpacking and scanning touch a lot of files; keep that off the UI thread.
            var record = await Task.Run(() => library.Install(package, knownId));
            Extensions = library.Extensions;
            // The files changed, so whatever Chromium had installed from them is stale.
            ForgetChromiumCopy(record.Id);
            SyncItems();
            if (record.Enabled)
            {
                // Replacing an installed extension: Gecko swaps it in place.
                if (IsGeckoRunning) await LoadIntoGeckoAsync(record);
                await LoadIntoChromiumAsync(record);
            }
            return ErrorMessage is null;
        }
        catch (Exception error)
        {
            // Network, package and disk problems all end the same way: nothing was installed,
            // and the message says why.
            ErrorMessage = error.Message;
            return false;
        }
        finally
        {
            libraryGate.Release();
            IsBusy = false;
        }
    }

    public async Task RemoveAsync(InstalledExtension extension)
    {
        if (library is null) return;
        await libraryGate.WaitAsync();
        try
        {
            await UnloadFromGeckoAsync(extension);
            await UnloadFromChromiumAsync(extension);
            library.Remove(extension.Id);
        }
        catch (Exception error)
        {
            ErrorMessage = $"{extension.Name} could not be removed: {error.Message}";
        }
        finally
        {
            Extensions = library.Extensions;
            SyncItems();
            libraryGate.Release();
        }
    }

    public async Task SetEnabledAsync(bool enabled, InstalledExtension extension)
    {
        if (library is null) return;
        await libraryGate.WaitAsync();
        try
        {
            library.SetEnabled(enabled, extension.Id);
            Extensions = library.Extensions;
            SyncItems();
            if (enabled)
            {
                if (IsGeckoRunning) await LoadIntoGeckoAsync(extension);
                await LoadIntoChromiumAsync(extension);
            }
            else
            {
                await UnloadFromGeckoAsync(extension);
                await UnloadFromChromiumAsync(extension);
            }
        }
        catch (Exception error)
        {
            ErrorMessage = $"{extension.Name} could not be {(enabled ? "enabled" : "disabled")}: {error.Message}";
        }
        finally
        {
            // Also when it failed: the row's toggle has to fall back to what is true.
            SyncItems();
            libraryGate.Release();
        }
    }

    // ---- Gecko ----

    private bool IsGeckoRunning => gecko.Status == GeckoStatus.Running;

    /// <summary>Called whenever the Gecko engine has (re)started.</summary>
    public async Task LoadAllIntoGeckoAsync()
    {
        loaded.Clear();
        SyncItems();
        foreach (var extension in extensions.Where(extension => extension.Enabled))
        {
            await LoadIntoGeckoAsync(extension, quiet: true);
        }
    }

    public async Task EnsureLoadedAsync(InstalledExtension extension)
    {
        if (!loaded.Contains(extension.GeckoId)) await LoadIntoGeckoAsync(extension);
    }

    /// <summary>
    /// Loads the converted copy into the running Gecko. <paramref name="quiet"/> is for re-loading
    /// an extension the user already has: whatever welcome tab it would open on install is suppressed.
    /// </summary>
    private async Task LoadIntoGeckoAsync(InstalledExtension extension, bool quiet = false)
    {
        if (library is null) return;
        try
        {
            await gecko.CallAsync("installExtension", library.GeckoDirectoryFor(extension.Id), quiet);
            loaded.Add(extension.GeckoId);
        }
        catch (Exception error)
        {
            loaded.Remove(extension.GeckoId);
            ErrorMessage = $"{extension.Name} could not be loaded into Gecko: {error.Message}";
        }
        SyncItems();
    }

    private async Task UnloadFromGeckoAsync(InstalledExtension extension)
    {
        loaded.Remove(extension.GeckoId);
        SyncItems();
        if (!IsGeckoRunning) return;
        try
        {
            await gecko.CallAsync("uninstallExtension", extension.GeckoId);
        }
        catch (Exception)
        {
            // Gecko stopping underneath the call unloads the extension just as well.
        }
    }

    // ---- Chromium (experimental) ----

    /// <summary>
    /// Brings the Chromium profile in line with the library: enabled extensions are present,
    /// everything else is removed. An extension the profile kept from the last run is left as it
    /// is, because installing it again would discard the data it has stored.
    /// </summary>
    internal async Task SyncChromiumAsync()
    {
        if (chromium is null || library is null) return;
        await chromiumGate.WaitAsync();
        try
        {
            var installed = await chromium.ListAsync();
            inChromium.Clear();
            chromiumErrors.Clear();

            var wanted = new HashSet<string>(StringComparer.Ordinal);
            foreach (var extension in extensions.Where(extension => extension.Enabled))
            {
                if (settings.ChromiumExtensionIds.TryGetValue(extension.Id, out var known) && installed.Contains(known))
                {
                    inChromium[extension.Id] = known;
                    wanted.Add(known);
                }
                else if (await AddToChromiumAsync(extension) is { } added)
                {
                    wanted.Add(added);
                }
            }

            foreach (var stale in installed.Where(id => !wanted.Contains(id)))
            {
                await chromium.UnloadAsync(stale);
            }
            foreach (var chromeId in settings.ChromiumExtensionIds.Keys.Where(id => !inChromium.ContainsKey(id)).ToList())
            {
                settings.SetChromiumExtensionId(chromeId, null);
            }
            settings.Save();
        }
        catch (Exception)
        {
            // Experimental path: the profile could not be listed or tidied. Extensions that did
            // load are recorded; the rest stay Gecko-only.
        }
        finally
        {
            chromiumGate.Release();
            SyncItems();
        }
    }

    private async Task LoadIntoChromiumAsync(InstalledExtension extension)
    {
        if (chromium is not { IsReady: true }) return;
        await chromiumGate.WaitAsync();
        try
        {
            await AddToChromiumAsync(extension);
            settings.Save();
        }
        finally
        {
            chromiumGate.Release();
            SyncItems();
        }
    }

    /// <summary>Installs one extension into the profile; returns Chromium's ID, or null if it was refused.</summary>
    private async Task<string?> AddToChromiumAsync(InstalledExtension extension)
    {
        try
        {
            var chromiumId = await chromium!.LoadAsync(library!.ChromiumDirectoryFor(extension.Id));
            inChromium[extension.Id] = chromiumId;
            chromiumErrors.Remove(extension.Id);
            settings.SetChromiumExtensionId(extension.Id, chromiumId);
            return chromiumId;
        }
        catch (Exception error)
        {
            inChromium.Remove(extension.Id);
            chromiumErrors[extension.Id] = error.Message;
            return null;
        }
    }

    private async Task UnloadFromChromiumAsync(InstalledExtension extension)
    {
        chromiumErrors.Remove(extension.Id);
        if (chromium is not { IsReady: true })
        {
            // Left in the profile for now; the next sync removes it.
            inChromium.Remove(extension.Id);
            return;
        }
        await chromiumGate.WaitAsync();
        try
        {
            if (inChromium.Remove(extension.Id, out var chromiumId))
            {
                await chromium.UnloadAsync(chromiumId);
                settings.SetChromiumExtensionId(extension.Id, null);
                settings.Save();
            }
        }
        catch (Exception)
        {
            // Experimental path: if Chromium would not let go of it now, the next sync retries.
        }
        finally
        {
            chromiumGate.Release();
            SyncItems();
        }
    }

    private void ForgetChromiumCopy(string chromeId)
    {
        inChromium.Remove(chromeId);
        chromiumErrors.Remove(chromeId);
        if (!settings.ChromiumExtensionIds.ContainsKey(chromeId)) return;
        settings.SetChromiumExtensionId(chromeId, null);
        settings.Save();
    }

    // ---- Rows ----

    /// <summary>Brings <see cref="Items"/> in line with the library and the engines' current state.</summary>
    private void SyncItems()
    {
        for (var index = 0; index < extensions.Count; index++)
        {
            var extension = extensions[index];
            var existing = index;
            while (existing < Items.Count && Items[existing].Extension.Id != extension.Id) existing++;

            if (existing == Items.Count) Items.Insert(index, new ExtensionItem(extension));
            else if (existing != index) Items.Move(existing, index);

            Items[index].Update(
                extension,
                isRunning: loaded.Contains(extension.GeckoId),
                iconFile: library?.IconFileFor(extension),
                isInChromium: inChromium.ContainsKey(extension.Id),
                chromiumError: chromiumErrors.GetValueOrDefault(extension.Id));
        }
        while (Items.Count > extensions.Count) Items.RemoveAt(Items.Count - 1);
    }
}
