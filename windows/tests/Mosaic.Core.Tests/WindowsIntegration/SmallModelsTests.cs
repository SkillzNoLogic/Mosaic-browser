using Mosaic.Core.WindowsIntegration;

namespace Mosaic.Core.Tests.WindowsIntegration;

public class DropTargetsTests
{
    [Fact]
    public void FilesOpenAsFileUris() =>
        Assert.Equal(["file:///C:/a/b%20c.html", "file:///D:/x.pdf"], DropTargets.From([@"C:\a\b c.html", @"D:\x.pdf"], null, null));

    [Fact]
    public void FilesWinOverTextAndLinks() =>
        Assert.Single(DropTargets.From([@"C:\a.html"], "https://example.com/", "words"));

    [Fact]
    public void ALinkOpensAsIs() =>
        Assert.Equal(["https://example.com/page"], DropTargets.From(null, "https://example.com/page", "Example"));

    [Fact]
    public void AnUnsafeLinkFallsBackToText() =>
        Assert.Equal(["Example"], DropTargets.From(null, "javascript:alert(1)", "Example"));

    [Fact]
    public void ListsOfUrlsOpenOneTabEach() =>
        Assert.Equal(["https://a.example/", "https://b.example/"], DropTargets.From(null, null, "https://a.example/\r\nhttps://b.example/\n"));

    [Fact]
    public void OtherTextIsSearched() =>
        Assert.Equal(["best pizza near me"], DropTargets.From(null, null, "best pizza\nnear me"));

    [Fact]
    public void LongTextIsCutAndManyFilesCapped()
    {
        Assert.Equal(DropTargets.MaxSearchLength, DropTargets.From(null, null, new string('x', 5000) + " y")[0].Length);
        var files = Enumerable.Range(0, 50).Select(i => $@"C:\f{i}.html");
        Assert.Equal(DropTargets.MaxTabs, DropTargets.From(files, null, null).Count);
    }

    [Fact]
    public void NothingUsefulIsNothing() => Assert.Empty(DropTargets.From([], "", "   "));
}

public class MenuItemSpecTests
{
    [Fact]
    public void AccessKeys()
    {
        Assert.Equal(("Copy", 'C'), new MenuItemSpec("&Copy").SplitAccessKey());
        Assert.Equal(("Save as", 'A'), new MenuItemSpec("Save &as").SplitAccessKey());
        Assert.Equal(("Tom & Jerry", (char?)null), new MenuItemSpec("Tom && Jerry").SplitAccessKey());
    }

    [Fact]
    public void TidyRemovesStraySeparators()
    {
        var a = new MenuItemSpec("A");
        var b = new MenuItemSpec("B");
        var tidy = MenuItemSpec.Tidy([MenuItemSpec.Separator, a, MenuItemSpec.Separator, MenuItemSpec.Separator, b, MenuItemSpec.Separator]);
        Assert.Equal([a, MenuItemSpec.Separator, b], tidy);
    }
}

public class JumpListAndCommandLineTests
{
    [Fact]
    public void TasksUseTheActivationSwitches()
    {
        Assert.Equal(["--new-tab", "--new-private-tab"], JumpListTasks.Tasks.Select(t => t.Arguments));
        foreach (var task in JumpListTasks.Tasks)
        {
            var request = ActivationRequest.Parse([task.Arguments], null, @"C:\", _ => false);
            Assert.False(request.IsPlainLaunch);
        }
    }

    [Fact]
    public void FingerprintDependsOnTheExecutable()
    {
        Assert.Equal(JumpListTasks.Fingerprint(@"C:\A\Mosaic.exe"), JumpListTasks.Fingerprint(@"c:\a\mosaic.EXE"));
        Assert.NotEqual(JumpListTasks.Fingerprint(@"C:\A\Mosaic.exe"), JumpListTasks.Fingerprint(@"C:\B\Mosaic.exe"));
    }

    [Theory]
    [InlineData("a b c", new[] { "a", "b", "c" })]
    [InlineData("\"a b\" c", new[] { "a b", "c" })]
    [InlineData("--single-argument https://x/?q=\"1\"", new[] { "--single-argument", "https://x/?q=1" })]
    [InlineData(@"C:\dir\ ""q\""x""", new[] { @"C:\dir\", "q\"x" })]
    [InlineData("  ", new string[0])]
    public void SplitsLikeWindows(string commandLine, string[] expected) =>
        Assert.Equal(expected, CommandLineSplitter.Split(commandLine));
}
