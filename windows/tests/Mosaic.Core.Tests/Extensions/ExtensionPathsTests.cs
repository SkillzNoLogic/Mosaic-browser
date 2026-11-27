using Mosaic.Core.Extensions;

namespace Mosaic.Core.Tests.Extensions;

/// <summary>Which names from inside a package may reach the file system.</summary>
public class ExtensionPathsTests
{
    [Theory]
    [InlineData("manifest.json")]
    [InlineData("_locales")]
    [InlineData(".hidden")]
    [InlineData("a b.js")]
    [InlineData("日本語.png")]
    [InlineData("icon@2x.png")]
    [InlineData("console.js")]      // merely starts like a device name
    [InlineData("com10.js")]
    [InlineData("nulled")]
    [InlineData("a.nul")]           // only the part before the first dot names a device
    [InlineData("file.tar.gz")]
    [InlineData("~$%&'()+,;=[]{}^`!#-")]
    public void AcceptsOrdinaryNames(string name) => Assert.True(ExtensionPaths.IsStorableName(name));

    [Theory]
    // Reserved device names, in any case and with any extension.
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("PRN")]
    [InlineData("AUX")]
    [InlineData("NUL")]
    [InlineData("nul.js")]
    [InlineData("NUL.tar.gz")]
    [InlineData("aux.d.ts")]
    [InlineData("COM1")]
    [InlineData("com9.txt")]
    [InlineData("LPT1")]
    [InlineData("lpt9.json")]
    [InlineData("COM¹")]
    [InlineData("LPT³.x")]
    [InlineData("CONIN$")]
    [InlineData("conout$")]
    [InlineData("NUL .txt")]
    // Trailing dots and spaces, which Windows silently removes.
    [InlineData("manifest.json.")]
    [InlineData("manifest.json ")]
    [InlineData("dir.")]
    [InlineData("...")]
    [InlineData(" ")]
    // Characters Windows reserves, including the stream separator.
    [InlineData("manifest.json::$DATA")]
    [InlineData("a:b")]
    [InlineData("C:")]
    [InlineData("a<b")]
    [InlineData("a>b")]
    [InlineData("a\"b")]
    [InlineData("a|b")]
    [InlineData("a?b")]
    [InlineData("a*b")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a\tb")]
    [InlineData("a\nb")]
    [InlineData("a\0b")]
    [InlineData("\u001Fa")]
    [InlineData("")]
    public void RejectsNamesWindowsCannotStoreFaithfully(string name) => Assert.False(ExtensionPaths.IsStorableName(name));

    [Fact]
    public void RejectsNamesLongerThanAFileSystemComponent()
    {
        Assert.True(ExtensionPaths.IsStorableName(new string('a', 255)));
        Assert.False(ExtensionPaths.IsStorableName(new string('a', 256)));
    }

    [Theory]
    [InlineData("a/b/c.js", "a|b|c.js")]
    [InlineData("/a/b.js", "a|b.js")]
    [InlineData("./a//b/./c.js", "a|b|c.js")]
    [InlineData("a\\b\\c.js", "a|b|c.js")]
    [InlineData("a/b/", "a|b")]
    public void SplitsOnEitherSlashAndDropsEmptyAndDotComponents(string path, string expected)
    {
        Assert.Equal(expected.Split('|'), ExtensionPaths.Split(path, collapseParentReferences: false)!);
        Assert.Equal(expected.Split('|'), ExtensionPaths.Split(path, collapseParentReferences: true)!);
    }

    [Theory]
    [InlineData("../evil.js")]
    [InlineData("a/../../evil.js")]
    [InlineData("a/b/../../../evil.js")]
    [InlineData("..\\evil.js")]
    [InlineData("a\\..\\..\\evil.js")]
    [InlineData("..")]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("./.")]
    [InlineData("C:/Windows/evil.js")]
    [InlineData("C:evil.js")]
    [InlineData("//server/share/evil.js:stream")]
    [InlineData("a/nul/b.js")]
    [InlineData("a /b.js")]
    public void RefusesPathsThatLeaveTheRootOrCannotBeStored(string path)
    {
        Assert.Null(ExtensionPaths.Split(path, collapseParentReferences: false));
        Assert.Null(ExtensionPaths.Split(path, collapseParentReferences: true));
    }

    [Fact]
    public void ParentReferencesAreCollapsedOnlyOnRequest()
    {
        Assert.Equal(["b.js"], ExtensionPaths.Split("a/../b.js", collapseParentReferences: true)!);
        Assert.Null(ExtensionPaths.Split("a/../b.js", collapseParentReferences: false));
        Assert.Equal(["x", "z.js"], ExtensionPaths.Split("x/y/../z.js", collapseParentReferences: true)!);
    }

    [Fact]
    public void ResolveStaysInsideTheRoot()
    {
        using var root = new TemporaryDirectory();
        Assert.Equal(Path.Combine(root.Path, "icons", "128.png"), ExtensionPaths.Resolve(root.Path, "/icons/128.png"));
        Assert.Equal(Path.Combine(root.Path, "b.js"), ExtensionPaths.Resolve(root.Path, "a/../b.js"));
        Assert.Null(ExtensionPaths.Resolve(root.Path, "../outside.js"));
        Assert.Null(ExtensionPaths.Resolve(root.Path, "a/../../outside.js"));
        Assert.Null(ExtensionPaths.Resolve(root.Path, "C:\\Windows\\win.ini"));
        Assert.Null(ExtensionPaths.Resolve(root.Path, ""));
    }
}
