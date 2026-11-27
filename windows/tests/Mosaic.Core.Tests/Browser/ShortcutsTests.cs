using Mosaic.Core.Browser;
using Mosaic.Core.Search;

namespace Mosaic.Core.Tests.Browser;

public class ShortcutsTests
{
    private const ShortcutModifiers Ctrl = ShortcutModifiers.Control;
    private const ShortcutModifiers CtrlShift = ShortcutModifiers.Control | ShortcutModifiers.Shift;
    private const ShortcutModifiers Alt = ShortcutModifiers.Alt;

    private const int VkTab = 0x09;
    private const int VkLeft = 0x25;
    private const int VkRight = 0x27;
    private const int VkF5 = 0x74;

    [Theory]
    [InlineData((int)'T', Ctrl, BrowserCommand.NewTab)]
    [InlineData((int)'N', Ctrl, BrowserCommand.NewTab)]
    [InlineData((int)'W', Ctrl, BrowserCommand.CloseTab)]
    [InlineData((int)'L', Ctrl, BrowserCommand.FocusAddress)]
    [InlineData((int)'R', Ctrl, BrowserCommand.Reload)]
    [InlineData(VkF5, ShortcutModifiers.None, BrowserCommand.Reload)]
    [InlineData(VkLeft, Alt, BrowserCommand.Back)]
    [InlineData(VkRight, Alt, BrowserCommand.Forward)]
    [InlineData(VkTab, Ctrl, BrowserCommand.NextTab)]
    [InlineData(VkTab, CtrlShift, BrowserCommand.PreviousTab)]
    [InlineData((int)'E', CtrlShift, BrowserCommand.ToggleEngine)]
    [InlineData((int)'S', CtrlShift, BrowserCommand.NextSearchEngine)]
    [InlineData((int)'X', CtrlShift, BrowserCommand.ShowExtensions)]
    public void ShortcutMapsToItsCommand(int virtualKey, ShortcutModifiers modifiers, BrowserCommand expected) =>
        Assert.Equal(expected, Shortcuts.Match(virtualKey, modifiers));

    [Theory]
    [InlineData((int)'T', ShortcutModifiers.None)]
    [InlineData((int)'T', CtrlShift)]
    // AltGr arrives as Ctrl+Alt and types characters on many layouts.
    [InlineData((int)'E', Ctrl | Alt)]
    [InlineData((int)'L', Ctrl | Alt)]
    [InlineData((int)'E', Ctrl)]
    [InlineData((int)'S', Ctrl)]
    [InlineData((int)'X', Ctrl)]
    [InlineData(VkLeft, ShortcutModifiers.None)]
    [InlineData(VkLeft, Ctrl)]
    [InlineData(VkTab, ShortcutModifiers.None)]
    [InlineData(VkTab, Alt)]
    [InlineData(VkF5, Ctrl)]
    [InlineData((int)'Q', Ctrl)]
    public void OtherKeyPressesAreLeftAlone(int virtualKey, ShortcutModifiers modifiers) =>
        Assert.Null(Shortcuts.Match(virtualKey, modifiers));

    [Fact]
    public void EveryCommandDoesItsJob() => TestLoop.Run(() =>
    {
        using var fixture = new BrowserFixture();
        var model = fixture.Model;
        var (first, browser) = fixture.OpenChromiumTab("https://example.com/");

        model.Execute(BrowserCommand.Back);
        model.Execute(BrowserCommand.Forward);
        model.Execute(BrowserCommand.Reload);
        Assert.Equal(["back", "forward", "reload"], browser.Actions);

        model.Execute(BrowserCommand.NewTab);
        Assert.Equal(3, model.Tabs.Count);
        var blank = model.SelectedTab;
        Assert.Equal(1, fixture.AddressFocusRequests);

        model.Execute(BrowserCommand.PreviousTab);
        Assert.Same(first, model.SelectedTab);
        model.Execute(BrowserCommand.NextTab);
        Assert.Same(blank, model.SelectedTab);

        model.Execute(BrowserCommand.CloseTab);
        Assert.Equal(2, model.Tabs.Count);

        model.Execute(BrowserCommand.FocusAddress);
        Assert.Equal(2, fixture.AddressFocusRequests);
        // Pressed in Mosaic's own window, which is already in front.
        Assert.Equal(0, fixture.WindowActivationRequests);

        model.Execute(BrowserCommand.ToggleEngine);
        Assert.Equal(EngineKind.Gecko, first.Engine);

        model.Execute(BrowserCommand.NextSearchEngine);
        Assert.Equal(SearchEngine.DuckDuckGo, model.SearchEngine);

        model.Execute(BrowserCommand.ShowExtensions);
        Assert.True(model.ShowExtensions);
    });
}
