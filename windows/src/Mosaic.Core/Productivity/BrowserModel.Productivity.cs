using Mosaic.Core.Productivity;

namespace Mosaic.Core.Browser;

/// <summary>The productivity area's part of the model (command palette, reader view, shortcuts, Speed Dial).</summary>
public sealed partial class BrowserModel
{
    private ProductivityServices? productivity;
    private ProductivityOptions? productivityOptions;

    /// <summary>The productivity services, created on first use.</summary>
    public ProductivityServices Productivity => productivity ??= new ProductivityServices(this, productivityOptions ?? new ProductivityOptions());

    /// <summary>Where the productivity area keeps its files (tests). Only before <see cref="Productivity"/> is first used.</summary>
    public void UseProductivityOptions(ProductivityOptions options)
    {
        if (productivity is not null) throw new InvalidOperationException("The productivity services already exist.");
        productivityOptions = options;
    }

    /// <summary>Ctrl+K: shows or hides the command palette.</summary>
    public void ShowCommandPalette() => Productivity.Palette.Toggle();

    /// <summary>F9: reader view for the selected tab, in either engine.</summary>
    public void ToggleReaderMode()
    {
        if (selectedTab is { } tab) Productivity.Reader.Toggle(tab);
    }

    internal TimeProvider ProductivityTime => time;

    internal void RequestWindowActivationForProductivity() => RequestWindowActivation();

    /// <summary>
    /// Opens an address chosen in the palette or Speed Dial: in the current tab when it is a
    /// new-tab page, otherwise in a new tab next to it.
    /// </summary>
    public void OpenFromProductivity(string url, bool inNewTab = false)
    {
        if (!inNewTab && selectedTab is { IsStartPage: true } tab)
        {
            Load(url, tab);
            FocusContent();
        }
        else
        {
            NewTab(url);
        }
    }
}
