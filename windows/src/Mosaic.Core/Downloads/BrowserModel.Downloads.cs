using Mosaic.Core.Downloads;

namespace Mosaic.Core.Browser;

/// <summary>The downloads area's part of the browser model (owner: <b>downloads</b>).</summary>
public sealed partial class BrowserModel
{
    /// <summary>Address of the Downloads page.</summary>
    public const string DownloadsPageUrl = "mosaic://downloads";

    private DownloadManager? downloads;

    /// <summary>
    /// The download manager, created on first use (the app's downloads startup hook, after the
    /// window is shown). Its history is read on a background thread.
    /// </summary>
    public DownloadManager Downloads => downloads ??= CreateDownloads();

    /// <summary>Whether the download manager exists yet (nothing to save or show before it does).</summary>
    public bool HasDownloadManager => downloads is not null;

    private DownloadManager CreateDownloads()
    {
        var folder = Path.Combine(AppPaths.DataRoot, "Downloads");
        var manager = new DownloadManager(
            Gecko, Path.Combine(folder, "history.json"), new DownloadSettings(Path.Combine(folder, "settings.json")), time)
        {
            ShowNotice = text => Notice = text,
            OpenPage = ShowDownloadsPage,
            IsPageShown = () => SelectedTab?.Url == DownloadsPageUrl,
            CurrentEngine = () => Engine,
        };
        _ = manager.LoadAsync();
        return manager;
    }

    /// <summary>Selects the tab showing the Downloads page, or opens one (Ctrl+J).</summary>
    public void ShowDownloadsPage()
    {
        if (Tabs.FirstOrDefault(tab => tab.Url == DownloadsPageUrl) is { } existing) Select(existing);
        else NewTab(DownloadsPageUrl);
    }
}
