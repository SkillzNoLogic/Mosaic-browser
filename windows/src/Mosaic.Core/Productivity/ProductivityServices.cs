using Mosaic.Core.Browser;
using Mosaic.Core.Gecko;

namespace Mosaic.Core.Productivity;

/// <summary>Where the productivity area keeps its files and finds its scripts (tests point these at a scratch folder).</summary>
public sealed class ProductivityOptions
{
    public string Directory { get; init; } = ProductivityFiles.DefaultDirectory;

    public string ReaderResources { get; init; } = ReaderMode.DefaultResourcesDirectory;

    /// <summary>For favicon downloads (tests stub it).</summary>
    public HttpMessageHandler? HttpHandler { get; init; }
}

/// <summary>
/// The productivity area's services: the command palette, reader view, customisable keyboard
/// shortcuts and the new-tab Speed Dial. One instance per model, <c>model.Productivity</c>,
/// created on first use (nothing on the start-up path). Port of
/// <c>Sources/Mosaic/Features/Productivity/Productivity.swift</c>.
/// </summary>
public sealed class ProductivityServices
{
    private readonly BrowserModel model;
    private Task? starting;

    internal ProductivityServices(BrowserModel model, ProductivityOptions options)
    {
        this.model = model;
        Files = new ProductivityFiles(options.Directory);
        Shortcuts = new ShortcutManager(model, Files);
        Palette = new CommandPalette(model, Files);
        Reader = new ReaderMode(model, Files, options.ReaderResources);
        SpeedDial = new SpeedDialModel(model, Files);
        Favicons = new FaviconCache(Files.PathOf(ProductivityFiles.FaviconsFolder), model.ProductivityTime, options.HttpHandler);
    }

    public ProductivityFiles Files { get; }
    public ShortcutManager Shortcuts { get; }
    public CommandPalette Palette { get; }
    public ReaderMode Reader { get; }
    public SpeedDialModel SpeedDial { get; }
    public FaviconCache Favicons { get; }

    public bool IsStarted => starting is { IsCompletedSuccessfully: true };

    /// <summary>
    /// Loads the saved settings (off the UI thread), starts watching tabs and Gecko, and tells
    /// Gecko which shortcuts to forward. Call once the window is up; later calls share the first.
    /// </summary>
    public Task StartAsync() => starting ??= StartCoreAsync();

    private async Task StartCoreAsync()
    {
        lastGeckoStatus = model.Gecko.Status;
        model.Gecko.StatusChanged += GeckoStatusChanged;
        model.Gecko.SnapshotReceived += GeckoSnapshot;
        await Task.WhenAll(Shortcuts.LoadAsync(), Palette.LoadAsync(), Reader.LoadAsync(), SpeedDial.LoadAsync());
        Reader.Start();
        if (model.Gecko.Status == GeckoStatus.Running) Shortcuts.GeckoStarted();
    }

    private GeckoStatus lastGeckoStatus;

    private void GeckoStatusChanged()
    {
        var status = model.Gecko.Status;
        if (status == GeckoStatus.Running && lastGeckoStatus != GeckoStatus.Running) Shortcuts.GeckoStarted();
        lastGeckoStatus = status;
    }

    /// <summary>Events the agent queued for this area (agent.sys.mjs, "productivity").</summary>
    private void GeckoSnapshot(GeckoSnapshot snapshot)
    {
        // Commands that open Mosaic's own UI (the palette, the address bar) ask for the window themselves.
        foreach (var type in snapshot.Events) Shortcuts.HandleGeckoEvent(type);
    }

    /// <summary>Before quitting: releases page-script channels and waits briefly for pending saves.</summary>
    public void Shutdown()
    {
        Reader.Shutdown();
        Files.Flush().Wait(TimeSpan.FromSeconds(2));
    }

    internal static bool IsWebPage(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
}
