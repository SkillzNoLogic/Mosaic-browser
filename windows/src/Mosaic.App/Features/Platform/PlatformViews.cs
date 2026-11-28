using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Mosaic.App.Features.System;
using Mosaic.Core;
using Mosaic.Core.Browser;
using Mosaic.Core.Platform;
using static Mosaic.App.Features.Platform.PlatformUi;

namespace Mosaic.App.Features.Platform;

/// <summary>
/// Shown over the content area of a Mozilla tab when no Gecko runtime is installed (the downloaded
/// app on a PC that never had Firefox): offers the in-app install, or the page in Chromium
/// meanwhile. Port of <c>GeckoRuntimeMissingView.swift</c>. There is no Gecko window to cover in
/// that state, so it needs no page cover.
/// </summary>
internal sealed class GeckoMissingOverlay : PlatformPageless
{
    private readonly BrowserModel model;

    public GeckoMissingOverlay(BrowserModel model)
    {
        this.model = model;
        Watch(Render, model, model.Platform.Runtime);
    }

    private void Render(StackPanel panel)
    {
        var tab = model.SelectedTab;
        var installer = model.Platform.Runtime;
        var relevant = tab is { Engine: EngineKind.Gecko } && model.GeckoRuntimeMissing;
        Visibility = relevant || installer.IsActive && tab?.Engine == EngineKind.Gecko ? Visibility.Visible : Visibility.Collapsed;
        if (Visibility == Visibility.Collapsed) return;

        var icon = Icons.Make("flame", 30);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        panel.Children.Add(icon);
        panel.Children.Add(new TextBlock { Text = "The Mozilla engine isn’t installed", Style = Style("SubtitleTextBlockStyle"), HorizontalAlignment = HorizontalAlignment.Center });
        panel.Children.Add(new TextBlock
        {
            Text = "Mozilla tabs need Firefox ESR. Mosaic can download it from Mozilla (about 70 MB), check that it is signed by Mozilla, and keep it in its own folder. A Firefox you already have is never touched.",
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Foreground = Secondary,
            MaxWidth = 460,
        });
        if (installer.IsActive)
        {
            panel.Children.Add(new InstallProgressView(installer) { MaxWidth = 380 });
            return;
        }
        if (installer.Phase is RuntimeInstallPhase.Failed) panel.Children.Add(Message(installer.Phase.Summary, Tone.Negative));
        var buttons = Row(10, Button("Download and install", () => installer.Install(), accent: true));
        if (model.ChromiumAvailable && tab is { IsStartPage: false }) buttons.Children.Add(Button("Open in Chromium instead", () => model.SetEngine(EngineKind.Chromium)));
        buttons.HorizontalAlignment = HorizontalAlignment.Center;
        panel.Children.Add(buttons);
    }
}

/// <summary>
/// One button per enabled extension, for the shell to place in each Mosaic's toolbar (component
/// id "extensions"). A click opens the extension's popup as a Mozilla tab, else its options.
/// </summary>
internal sealed class ExtensionToolbarButtons : PlatformPageless
{
    private readonly BrowserModel model;

    public ExtensionToolbarButtons(BrowserModel model) : base(horizontal: true)
    {
        this.model = model;
        Watch(Render, model.Extensions, (global::System.ComponentModel.INotifyPropertyChanged)model.Extensions.Items);
    }

    private void Render(StackPanel panel)
    {
        panel.Spacing = 2;
        foreach (var item in model.ExtensionToolbarItems)
        {
            UIElement icon = item.IconFile is { } path && File.Exists(path)
                ? new Image { Source = new BitmapImage(new Uri(path)) { DecodePixelWidth = 16 }, Width = 16, Height = 16 }
                : Icons.Make("puzzlepiece.extension.fill", 14);
            var button = new Button
            {
                Content = icon,
                Width = 32,
                Height = 32,
                Padding = new Thickness(0),
                Opacity = item.IsRunning || model.Gecko.Status != Mosaic.Core.Gecko.GeckoStatus.Running ? 1 : 0.5,
                Style = Style("SubtleButtonStyle") ?? Style("DefaultButtonStyle"),
            };
            AutomationProperties.SetName(button, item.Name);
            ToolTipService.SetToolTip(button, item.Help);
            button.Click += (_, _) => model.PerformExtensionToolbarItem(item);
            panel.Children.Add(button);
        }
    }
}

/// <summary>A control that re-renders one panel when models change, only while it is in the window.</summary>
internal abstract class PlatformPageless : UserControl
{
    private readonly StackPanel panel;
    private readonly List<global::System.ComponentModel.INotifyPropertyChanged> sources = [];
    private Action<StackPanel>? render;
    private bool attached;
    private bool queued;

    protected PlatformPageless(bool horizontal = false)
    {
        panel = new StackPanel { Spacing = 12, Orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical };
        Content = panel;
        Loaded += (_, _) =>
        {
            attached = true;
            foreach (var source in sources) source.PropertyChanged += Changed;
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            attached = false;
            foreach (var source in sources) source.PropertyChanged -= Changed;
        };
    }

    protected void Watch(Action<StackPanel> render, params global::System.ComponentModel.INotifyPropertyChanged[] watched)
    {
        this.render = render;
        sources.AddRange(watched);
        Refresh();
    }

    private void Changed(object? sender, global::System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (queued || !attached) return;
        queued = true;
        if (DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Refresh) != true) Refresh();
    }

    private void Refresh()
    {
        queued = false;
        if (render is null) return;
        panel.Children.Clear();
        render(panel);
    }
}

/// <summary>
/// For whoever creates the shared WebView2 environment (the winui area): the web-content language
/// list to hand to it, read from the profile before the browser model exists. Records what was
/// handed out, so Settings › Languages can say when Chromium needs a restart.
/// </summary>
public static class PlatformChromium
{
    /// <summary>
    /// <c>Language</c> for <c>CoreWebView2EnvironmentOptions.Language</c> and an argument to append
    /// to <c>AdditionalBrowserArguments</c>, or null when the list follows Windows (pass nothing).
    /// </summary>
    public static ChromiumLanguageOptions? LanguageOptions()
    {
        WasAsked = true;
        try
        {
            var options = LanguageSettings.ChromiumOptionsFor(AppPaths.DataRoot);
            Applied = options?.Value;
            return options;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The list the environment was created with; null when it follows Windows (or was never asked).</summary>
    public static string? Applied { get; private set; }

    /// <summary>Whether the environment's creator asked at all (diagnostics).</summary>
    public static bool WasAsked { get; private set; }
}
