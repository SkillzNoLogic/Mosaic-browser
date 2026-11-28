using System.Text.RegularExpressions;
using Mosaic.Core.WindowsIntegration;

namespace Mosaic.Core.Tests.WindowsIntegration;

public class IconGlyphsTests
{
    /// <summary>
    /// Every SF Symbol name the macOS app uses (string literals in Sources/** that are SF Symbol
    /// names, found by intersecting them with macOS's symbol list), as of v0.3.1.
    /// </summary>
    public static readonly string[] MacSymbols =
    [
        "accessibility", "archivebox", "arrow.clockwise", "arrow.clockwise.circle",
        "arrow.counterclockwise", "arrow.down", "arrow.down.app", "arrow.down.circle",
        "arrow.down.circle.fill", "arrow.down.doc", "arrow.left.and.right", "arrow.left.square",
        "arrow.right.square", "arrow.triangle.2.circlepath", "arrow.up.forward.app.fill",
        "arrow.uturn.backward", "arrow.uturn.backward.circle", "arrow.uturn.backward.square",
        "arrow.uturn.forward", "bell", "bell.fill", "book", "briefcase", "camera", "cart", "checkmark",
        "checkmark.circle", "checkmark.circle.fill", "checkmark.seal.fill", "checkmark.shield",
        "chevron.down", "chevron.left", "chevron.right", "chevron.right.2", "chevron.up", "circle",
        "circle.circle", "circle.dashed", "circle.fill", "circle.lefthalf.filled", "clipboard",
        "clock", "command", "doc", "doc.on.clipboard", "doc.on.doc", "doc.plaintext",
        "doc.plaintext.fill", "doc.text", "ellipsis", "ellipsis.circle", "engine.combustion",
        "exclamationmark.arrow.circlepath", "exclamationmark.shield", "exclamationmark.triangle",
        "exclamationmark.triangle.fill", "eye", "eye.slash", "eyedropper", "eyeglasses", "film",
        "flame", "folder", "folder.badge.plus", "folder.fill", "gamecontroller",
        "gauge.with.dots.needle.67percent", "gearshape", "globe", "graduationcap", "hammer",
        "hand.raised", "hand.raised.fill", "hand.tap", "heart", "hourglass", "house", "info.circle",
        "info.circle.fill", "key", "key.fill", "key.viewfinder", "keyboard", "lightbulb", "link",
        "location", "location.fill", "lock.fill", "lock.open",
        "lock.open.trianglebadge.exclamationmark", "macwindow.badge.plus", "macwindow.on.rectangle",
        "magnifyingglass", "magnifyingglass.circle", "menubar.rectangle", "mic", "mic.fill", "minus",
        "minus.circle", "moon.zzz", "music.note", "nosign", "paintbrush", "pause", "pause.circle",
        "person", "person.crop.circle", "photo", "pin", "pin.fill", "pin.slash", "play.circle",
        "play.rectangle", "plus", "plus.circle", "plus.square", "plus.square.on.square",
        "puzzlepiece.extension", "puzzlepiece.extension.fill", "rectangle.and.text.magnifyingglass",
        "rectangle.inset.filled.on.rectangle", "rectangle.stack.badge.plus",
        "rectangle.topthird.inset.filled", "shield", "shield.lefthalf.filled",
        "shield.lefthalf.filled.slash", "shield.slash", "shippingbox", "sidebar.left", "sidebar.right",
        "speaker.slash", "speaker.slash.fill", "speaker.wave.2", "speaker.wave.2.fill",
        "square.and.arrow.down", "square.and.arrow.up", "square.and.arrow.up.on.square",
        "square.grid.2x2", "square.grid.2x2.fill", "square.grid.3x3.square", "square.on.square",
        "square.stack", "star", "star.fill", "star.slash", "tag", "text.magnifyingglass", "textformat",
        "textformat.size.larger", "textformat.size.smaller", "trash", "video", "video.fill",
        "wrench.and.screwdriver", "xmark", "xmark.circle", "xmark.circle.fill", "xmark.octagon",
        "xmark.octagon.fill", "xmark.square",
    ];

    [Fact]
    public void EveryMacSymbolIsMapped()
    {
        var missing = MacSymbols.Where(name => !IconGlyphs.Contains(name)).ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void EverySemanticNameIsMapped()
    {
        Assert.DoesNotContain(IconGlyphs.SemanticNames, name => !IconGlyphs.Contains(name));
        Assert.Equal(IconGlyphs.SemanticNames.Count, IconGlyphs.SemanticNames.Distinct().Count());
    }

    [Fact]
    public void NamesTheIntegratorShippedStillWork()
    {
        foreach (var name in new[] { "back", "forward", "reload", "stop", "add", "settings", "search", "home",
                     "download", "star", "star.fill", "shield", "lock", "sidebar.left", "puzzlepiece", "more", "ellipsis" })
            Assert.True(IconGlyphs.Contains(name), name);
    }

    [Fact]
    public void CodePointsAreInThePrivateUseArea()
    {
        foreach (var name in IconGlyphs.Names)
        {
            Assert.True(IconGlyphs.TryGet(name, out var entry));
            Assert.InRange(entry.Fluent, 0xE000, 0xF8FF);
            Assert.InRange(entry.Mdl2, 0xE000, 0xF8FF);
        }
    }

    [Fact]
    public void FluentOnlyGlyphsHaveAnMdl2Substitute()
    {
        foreach (var name in IconGlyphs.Names)
        {
            IconGlyphs.TryGet(name, out var entry);
            Assert.False(IconGlyphs.FluentOnly.Contains(entry.Mdl2), $"{name}: MDL2 code point {entry.Mdl2:X} is Fluent-only");
            if (IconGlyphs.FluentOnly.Contains(entry.Fluent))
                Assert.NotEqual(entry.Fluent, entry.Mdl2);
        }
    }

    [Fact]
    public void LookupIsCaseInsensitiveAndUnknownNamesGetThePlaceholder()
    {
        Assert.Equal(IconGlyphs.CodePoint("back", true), IconGlyphs.CodePoint("BACK", true));
        Assert.Equal(IconGlyphs.Placeholder, IconGlyphs.CodePoint("no.such.symbol", true));
        Assert.Equal(IconGlyphs.Placeholder, IconGlyphs.CodePoint("no.such.symbol", false));
    }

    [Fact]
    public void GlyphPicksTheFontsCodePoint()
    {
        Assert.Equal("", IconGlyphs.Glyph("back", fluent: true));
        Assert.Equal("", IconGlyphs.Glyph("back", fluent: false));
        Assert.Equal("", IconGlyphs.Glyph("eye.slash", fluent: true));
        Assert.Equal("", IconGlyphs.Glyph("eye.slash", fluent: false));
    }

    [Fact]
    public void SetAddsAndReplaces()
    {
        IconGlyphs.Set("test.only.icon", new IconGlyphs.Entry(0xE700));
        Assert.Equal(0xE700, IconGlyphs.CodePoint("test.only.icon", false));
    }

    [Theory]
    [InlineData(true, null, true)]
    [InlineData(false, null, false)]
    [InlineData(true, "mdl2", false)]
    [InlineData(true, " Windows10 ", false)]
    [InlineData(false, "fluent", true)]
    [InlineData(true, "something-else", true)]
    public void FontChoice(bool installed, string? overrideValue, bool expected) =>
        Assert.Equal(expected, IconGlyphs.ChooseFluent(installed, overrideValue));

    /// <summary>
    /// When the macOS sources are next to the tests (any checkout of this repository), every
    /// literal symbol name passed directly to SwiftUI/AppKit is mapped, so new macOS icons
    /// fail here until the Windows table catches up.
    /// </summary>
    [Fact]
    public void LiteralSymbolsInTheMacSourcesAreMapped()
    {
        var sources = FindMacSources();
        if (sources is null) return;  // tests copied somewhere without the repository
        var pattern = new Regex(@"(?:systemName|systemImage|systemSymbolName|symbol)\s*:\s*""([a-z0-9.]+)""");
        var missing = new SortedSet<string>();
        foreach (var file in Directory.EnumerateFiles(sources, "*.swift", SearchOption.AllDirectories))
            foreach (Match match in pattern.Matches(File.ReadAllText(file)))
                if (!IconGlyphs.Contains(match.Groups[1].Value)) missing.Add(match.Groups[1].Value);
        Assert.Empty(missing);
    }

    private static string? FindMacSources()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "Sources", "Mosaic");
            if (Directory.Exists(candidate)) return Path.GetDirectoryName(candidate);
        }
        return null;
    }
}
