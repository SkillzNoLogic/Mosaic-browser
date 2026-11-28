using Mosaic.Core.Downloads;

namespace Mosaic.Core.Tests.Downloads;

public class DownloadFileNameTests
{
    [Fact]
    public void KeepsOrdinaryNames()
    {
        Assert.Equal("report.pdf", DownloadFileName.Sanitize("report.pdf"));
        Assert.Equal("Ünïcödé résumé 2025.docx", DownloadFileName.Sanitize("Ünïcödé résumé 2025.docx"));
        Assert.Equal("👩‍💻 notes.txt", DownloadFileName.Sanitize("👩‍💻 notes.txt"));
    }

    [Fact]
    public void StripsPathsAndTraversal()
    {
        Assert.Equal("passwd", DownloadFileName.Sanitize("../../etc/passwd"));
        Assert.Equal("evil.exe", DownloadFileName.Sanitize(@"C:\Users\x\evil.exe"));
        Assert.Equal(DownloadFileName.Fallback, DownloadFileName.Sanitize("/"));
        Assert.Equal(DownloadFileName.Fallback, DownloadFileName.Sanitize(".."));
    }

    [Fact]
    public void RemovesControlAndBidiCharacters()
    {
        Assert.Equal("invoice.pdf", DownloadFileName.Sanitize("in\u0000voi\u0007ce\n.pdf"));
        Assert.Equal("invoicefdp.exe", DownloadFileName.Sanitize("invoice\u202Efdp.exe"));
    }

    [Fact]
    public void ReplacesCharactersWindowsForbids()
    {
        Assert.Equal("12-30 meeting.txt", DownloadFileName.Sanitize("12:30 meeting.txt"));
        Assert.Equal("what_ _quoted_ a_b_c_.txt", DownloadFileName.Sanitize("what? \"quoted\" a<b>c|.txt"));
        Assert.Equal("star_.png", DownloadFileName.Sanitize("star*.png"));
    }

    [Fact]
    public void TrimsHiddenAndPadding()
    {
        Assert.Equal("bash_profile", DownloadFileName.Sanitize(".bash_profile"));
        Assert.Equal("name.txt", DownloadFileName.Sanitize("  name.txt. "));
        Assert.Equal("photo.jpg .exe", DownloadFileName.Sanitize("photo.jpg            .exe"));
        Assert.Equal(DownloadFileName.Fallback, DownloadFileName.Sanitize(""));
        Assert.Equal(DownloadFileName.Fallback, DownloadFileName.Sanitize(null));
    }

    [Fact]
    public void PrefixesDeviceNames()
    {
        Assert.Equal("_CON", DownloadFileName.Sanitize("CON"));
        Assert.Equal("_nul.txt", DownloadFileName.Sanitize("nul.txt"));
        Assert.Equal("_COM1.tar.gz", DownloadFileName.Sanitize("COM1.tar.gz"));
        Assert.Equal("CONSOLE.txt", DownloadFileName.Sanitize("CONSOLE.txt"));
    }

    [Fact]
    public void LimitsLengthKeepingExtension()
    {
        var clean = DownloadFileName.Sanitize(new string('a', 400) + ".pdf");
        Assert.True(clean.Length <= DownloadFileName.MaxLength);
        Assert.EndsWith(".pdf", clean);
        // A surrogate pair is never cut in half.
        var emoji = DownloadFileName.Sanitize(string.Concat(Enumerable.Repeat("😀", 200)) + ".txt");
        Assert.True(emoji.Length <= DownloadFileName.MaxLength);
        Assert.False(char.IsHighSurrogate(emoji[^5]));
    }

    [Fact]
    public void SplitsCompoundExtensions()
    {
        Assert.Equal(("archive", "tar.gz"), DownloadFileName.Split("archive.tar.gz"));
        Assert.Equal(("photo", "JPG"), DownloadFileName.Split("photo.JPG"));
        Assert.Equal(("README", ""), DownloadFileName.Split("README"));
        Assert.Equal(("version 1.2 notes", ""), DownloadFileName.Split("version 1.2 notes"));
        Assert.Equal("gz", DownloadFileName.PathExtension("archive.tar.gz"));
    }

    [Fact]
    public void FindsAFreeName()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "a.pdf", "A (1).pdf", "b.tar.gz" };
        Assert.Equal("a (2).pdf", DownloadFileName.Unique("a.pdf", taken.Contains));
        Assert.Equal("b (1).tar.gz", DownloadFileName.Unique("b.tar.gz", taken.Contains));
        Assert.Equal("c", DownloadFileName.Unique("c", taken.Contains));
    }

    [Fact]
    public void SuggestsNamesFromUrls()
    {
        Assert.Equal("My File.zip", DownloadFileName.Suggested("https://x.org/files/My%20File.zip?x=1"));
        Assert.Equal("x.org", DownloadFileName.Suggested("https://x.org/"));
        Assert.Equal(DownloadFileName.Fallback, DownloadFileName.Suggested("not a url"));
    }

    [Fact]
    public void ReadsContentDisposition()
    {
        Assert.Equal("report.pdf", DownloadFileName.FromContentDisposition("attachment; filename=\"report.pdf\""));
        Assert.Equal("plain.txt", DownloadFileName.FromContentDisposition("attachment; filename=plain.txt"));
        Assert.Equal("€ rates.csv", DownloadFileName.FromContentDisposition(
            "attachment; filename=\"EUR rates.csv\"; filename*=UTF-8''%E2%82%AC%20rates.csv"));
        Assert.Equal("na\"me.txt", DownloadFileName.FromContentDisposition("attachment; filename=\"na\\\"me.txt\""));
        Assert.Null(DownloadFileName.FromContentDisposition("inline"));
        Assert.Null(DownloadFileName.FromContentDisposition(null));
        // The raw name keeps what sanitising would remove, for the safety check.
        Assert.Equal("invoice\u202Efdp.exe",
            DownloadFileName.FromContentDisposition("attachment; filename*=UTF-8''invoice%E2%80%AEfdp.exe"));
    }
}

public class DownloadSafetyTests
{
    [Fact]
    public void ClassifiesWindowsRiskyTypes()
    {
        Assert.Equal(DownloadRisk.Application, DownloadSafety.Risk("setup.EXE"));
        Assert.Equal(DownloadRisk.Application, DownloadSafety.Risk("screensaver.scr"));
        Assert.Equal(DownloadRisk.Installer, DownloadSafety.Risk("tool.msi"));
        Assert.Equal(DownloadRisk.Installer, DownloadSafety.Risk("app.msix"));
        Assert.Equal(DownloadRisk.DiskImage, DownloadSafety.Risk("disc.iso"));
        Assert.Equal(DownloadRisk.DiskImage, DownloadSafety.Risk("disk.vhdx"));
        foreach (var script in new[] { "a.bat", "a.cmd", "a.ps1", "a.vbs", "a.js", "a.hta", "a.wsf", "a.jar" })
        {
            Assert.Equal(DownloadRisk.Script, DownloadSafety.Risk(script));
        }
        Assert.Equal(DownloadRisk.Shortcut, DownloadSafety.Risk("link.lnk"));
        Assert.Equal(DownloadRisk.Shortcut, DownloadSafety.Risk("site.url"));
        Assert.Equal(DownloadRisk.Configuration, DownloadSafety.Risk("tweak.reg"));
        Assert.Equal(DownloadRisk.MacProgram, DownloadSafety.Risk("Tool.dmg"));
        Assert.Null(DownloadSafety.Risk("photo.jpg"));
        Assert.Null(DownloadSafety.Risk("noextension"));
    }

    [Fact]
    public void SpotsDoubleExtensions()
    {
        Assert.Equal([new DownloadMisleading.DoubleExtension("pdf", "exe")], DownloadSafety.MisleadingReasons("invoice.pdf.exe"));
        Assert.Equal([new DownloadMisleading.DoubleExtension("jpg", "scr")], DownloadSafety.MisleadingReasons("holiday.JPG.scr"));
        Assert.Empty(DownloadSafety.MisleadingReasons("archive.tar.gz"));
        Assert.Empty(DownloadSafety.MisleadingReasons("app-1.2.3.msi"));
        Assert.Empty(DownloadSafety.MisleadingReasons("notes.v2.pdf"));
    }

    [Fact]
    public void SpotsRightToLeftOverride()
    {
        // Displays as "invoiceexe.pdf".
        Assert.Contains(new DownloadMisleading.BidiOverride(), DownloadSafety.MisleadingReasons("invoice\u202Efdp.exe"));
    }

    [Fact]
    public void SpotsPaddedExtensions()
    {
        Assert.Contains(new DownloadMisleading.PaddedExtension(), DownloadSafety.MisleadingReasons("photo.jpg            .exe"));
        Assert.Empty(DownloadSafety.MisleadingReasons("my holiday photo.jpg"));
    }

    [Fact]
    public void AssessmentDecidesWarnings()
    {
        Assert.True(DownloadSafety.Assess("setup.exe", "setup.exe").NeedsWarning);
        Assert.True(DownloadSafety.Assess("run.ps1", "run.ps1").NeedsWarning);
        Assert.False(DownloadSafety.Assess("photo.png", "photo.png").NeedsWarning);
        // A macOS program cannot run on Windows: noted, not warned about…
        Assert.False(DownloadSafety.Assess("Tool.dmg", "Tool.dmg").NeedsWarning);
        Assert.Null(DownloadSafety.Assess("Tool.dmg", "Tool.dmg").Summary);
        // …unless its name is disguised.
        Assert.True(DownloadSafety.Assess("cv.pdf.app", "cv.pdf.app").NeedsWarning);
        // The raw suggested name is checked even though sanitising removed the override.
        var saved = DownloadFileName.Sanitize("invoice\u202Efdp.zip");
        var assessment = DownloadSafety.Assess("invoice\u202Efdp.zip", saved);
        Assert.True(assessment.NeedsWarning);
        Assert.Contains("reverse", assessment.Summary);
        Assert.Equal("This file is a program.", DownloadSafety.Assess("a.exe", "a.exe").Summary);
    }

    [Fact]
    public void AutoOpensOnlySafeFiles()
    {
        Assert.True(DownloadSafety.IsSafeToOpenAutomatically("paper.pdf"));
        Assert.True(DownloadSafety.IsSafeToOpenAutomatically("song.mp3"));
        Assert.False(DownloadSafety.IsSafeToOpenAutomatically("archive.zip"));
        Assert.False(DownloadSafety.IsSafeToOpenAutomatically("setup.exe"));
        Assert.False(DownloadSafety.IsSafeToOpenAutomatically("macros.xlsm"));
        Assert.False(DownloadSafety.IsSafeToOpenAutomatically("invoice.pdf.exe"));
    }
}

public class DownloadOrganizerTests
{
    private static readonly string Downloads = Path.Combine(Path.GetTempPath(), "dl");

    [Fact]
    public void OffByDefault()
    {
        var rules = new DownloadOrganizationRules();
        Assert.False(rules.IsEnabled);
        Assert.Null(rules.Subfolder("a.jpg"));
        Assert.Equal(Downloads, rules.Directory("a.jpg", null, Downloads));
    }

    [Fact]
    public void SortsByTypeWhenOn()
    {
        var rules = new DownloadOrganizationRules(isEnabled: true);
        Assert.Equal("Images", rules.Subfolder("a.JPG"));
        Assert.Equal("Documents", rules.Subfolder("b.pdf"));
        Assert.Equal("Archives", rules.Subfolder("c.tar.gz"));
        Assert.Equal("Installers", rules.Subfolder("d.msi"));
        Assert.Equal("Installers", rules.Subfolder("setup.exe"));
        Assert.Null(rules.Subfolder("e.unknownext"));
        Assert.Equal(Path.Combine(Downloads, "Music"), rules.Directory("song.flac", null, Downloads));
    }

    [Fact]
    public void FallsBackToMimeType()
    {
        var rules = new DownloadOrganizationRules(isEnabled: true);
        Assert.Equal("Images", rules.Subfolder("download", "image/png"));
        Assert.Equal("Videos", rules.Subfolder("download", "video/mp4; codecs=avc1"));
        Assert.Equal("Installers", rules.Subfolder("download", "application/x-msdownload"));
        Assert.Null(rules.Subfolder("download", "application/octet-stream"));
    }

    [Fact]
    public void RespectsCustomAndMissingFolders()
    {
        var rules = new DownloadOrganizationRules(isEnabled: true)
            .WithFolder(DownloadFileCategory.Images, @"..\Pictures\From Web")
            .WithFolder(DownloadFileCategory.Documents, null);
        // Folder names are a single safe component.
        Assert.Equal("From Web", rules.Subfolder("a.png"));
        Assert.Null(rules.Subfolder("b.pdf"));
        rules = rules.WithFolder(DownloadFileCategory.Audio, "   ");
        Assert.Null(rules.Subfolder("c.mp3"));
    }

    [Fact]
    public void RoundTripsThroughJson()
    {
        var rules = new DownloadOrganizationRules(isEnabled: true).WithFolder(DownloadFileCategory.Video, "Films");
        Assert.Equal(rules, DownloadOrganizationRules.FromJson(rules.ToJson()));
    }

    [Fact]
    public void ReadsTheMacOSEncoding()
    {
        // Swift's JSONEncoder writes an enum-keyed dictionary as a flat key/value array.
        var json = System.Text.Json.Nodes.JsonNode.Parse("""{"isEnabled":true,"folders":["images","Pics","video","Films"]}""");
        var rules = DownloadOrganizationRules.FromJson(json);
        Assert.True(rules.IsEnabled);
        Assert.Equal("Pics", rules.Subfolder("a.png"));
        Assert.Equal("Films", rules.Subfolder("a.mkv"));
        Assert.Null(rules.Subfolder("a.pdf"));
    }
}

public class MarkOfTheWebTests
{
    [Fact]
    public void WritesWhatBrowsersWrite()
    {
        Assert.Equal(
            "[ZoneTransfer]\r\nZoneId=3\r\nReferrerUrl=https://example.com/page\r\nHostUrl=https://cdn.example.com/a.zip\r\n",
            MarkOfTheWeb.Content("https://cdn.example.com/a.zip", "https://example.com/page"));
    }

    [Fact]
    public void LeavesOutCredentialsAndNonWebAddresses()
    {
        var content = MarkOfTheWeb.Content("https://user:secret@example.com/a.zip", "file:///C:/page.html");
        Assert.DoesNotContain("secret", content);
        Assert.DoesNotContain("ReferrerUrl", content);
        Assert.Contains("HostUrl=https://example.com/a.zip", content);
        Assert.Contains("HostUrl=about:internet", MarkOfTheWeb.Content("blob:https://x/1", null));
        Assert.Contains("HostUrl=ftp://x/a", MarkOfTheWeb.Content("ftp://x/a", null));
    }

    [Fact]
    public void ReadsTheZone()
    {
        Assert.Equal(3, MarkOfTheWeb.ZoneOf(MarkOfTheWeb.Content("https://x/a", null)));
        Assert.Equal(4, MarkOfTheWeb.ZoneOf("[ZoneTransfer]\nZoneId=4\n"));
        Assert.Null(MarkOfTheWeb.ZoneOf("[Other]\nZoneId=3\n"));
        Assert.Null(MarkOfTheWeb.ZoneOf(null));
    }

    [Fact]
    public void EnsureMarksAFileOnWindows()
    {
        var file = Path.Combine(Path.GetTempPath(), $"mosaic-motw-{Guid.NewGuid():N}.zip");
        File.WriteAllText(file, "x");
        try
        {
            var result = MarkOfTheWeb.Ensure(file, "https://example.com/a.zip", "https://example.com/");
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(MarkOfTheWeb.Result.Unsupported, result);
                return;
            }
            Assert.Equal(MarkOfTheWeb.Result.Written, result);
            Assert.Equal(3, MarkOfTheWeb.ZoneOf(MarkOfTheWeb.Read(file)));
            Assert.Contains("HostUrl=https://example.com/a.zip", MarkOfTheWeb.Read(file));
            Assert.Equal(MarkOfTheWeb.Result.AlreadyMarked, MarkOfTheWeb.Ensure(file, "https://other/", null));
        }
        finally
        {
            File.Delete(file);
        }
    }
}

public class DownloadFormatTests
{
    [Fact]
    public void FormatsSizesTheWindowsWay()
    {
        Assert.Equal("0 bytes", DownloadFormat.Bytes(0));
        Assert.Equal("1 byte", DownloadFormat.Bytes(1));
        Assert.Equal("1023 bytes", DownloadFormat.Bytes(1023));
        Assert.Equal(1.5.ToString("0.##") + " KB", DownloadFormat.Bytes(1536));
        Assert.Equal("100 MB", DownloadFormat.Bytes(100L * 1024 * 1024));
        Assert.Equal("0 bytes", DownloadFormat.Bytes(-5));
    }

    [Fact]
    public void FormatsTimeLeft()
    {
        Assert.Equal("20 s left", DownloadFormat.Remaining(20));
        Assert.Equal("3 min 5 s left", DownloadFormat.Remaining(185));
        Assert.Equal("1 h 12 min left", DownloadFormat.Remaining(4320));
        Assert.Equal("1 s left", DownloadFormat.Remaining(0));
    }

    [Fact]
    public void DescribesEachState()
    {
        var record = new DownloadRecord { Url = "https://x/a", FileName = "a", TotalBytes = 2048, ReceivedBytes = 1024, Speed = 512 };
        Assert.Equal("1 KB of 2 KB · 512 bytes/s · 2 s left", DownloadFormat.Status(record));
        Assert.StartsWith("Paused", DownloadFormat.Status(record with { State = DownloadState.Paused }));
        Assert.Equal("2 KB", DownloadFormat.Status(record with { State = DownloadState.Completed }));
        Assert.Equal("Failed · Network error", DownloadFormat.Status(record with { State = DownloadState.Failed, ErrorMessage = "Network error" }));
        Assert.Equal("Cancelled", DownloadFormat.Status(record with { State = DownloadState.Cancelled }));
        Assert.Equal("Sent to Free Download Manager", DownloadFormat.Status(record with { State = DownloadState.HandedOff }));
    }
}
