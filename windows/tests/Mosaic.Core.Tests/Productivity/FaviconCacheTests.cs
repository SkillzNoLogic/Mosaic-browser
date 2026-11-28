using System.Net;
using System.Text;
using Mosaic.Core.Productivity;
using Mosaic.Core.Tests.Browser;
using static Mosaic.Core.Tests.Productivity.ProductivityTestSupport;

namespace Mosaic.Core.Tests.Productivity;

public class FaviconLinkTests
{
    [Fact]
    public void BestDeclaredIconsFirstSkippingSvgAndData()
    {
        var html = """
            <link rel="icon" href="/small.png" sizes="16x16">
            <LINK REL='shortcut icon' href='big.png' sizes='64x64'>
            <link rel="apple-touch-icon" href="https://cdn.example/touch.png">
            <link rel="icon" href="/vector.svg">
            <link rel="icon" type="image/svg+xml" href="/vector2">
            <link rel="icon" href="data:image/png;base64,AAAA">
            <link rel="stylesheet" href="/style.css">
            <link rel="icon" href="javascript:alert(1)">
            """;
        var links = FaviconCache.IconLinks(html, new Uri("https://site.example/dir/"));
        Assert.Equal(["https://cdn.example/touch.png", "https://site.example/dir/big.png", "https://site.example/small.png"],
            links.Select(u => u.AbsoluteUri));
    }
}

[Collection(ProductivityStatics.Name)]
public class FaviconCacheTests
{
    [Fact]
    public void FetchesFromTheSiteWithoutCookiesAndStoresOnlyReencodedImages() => TestLoop.Run(async () =>
    {
        var http = new RecordingHandler
        {
            Respond = request =>
            {
                var path = request.RequestUri!.AbsolutePath;
                var found = request.RequestUri.Host != "none.example" && path is "/" or "/icon.png";
                var response = new HttpResponseMessage(found ? HttpStatusCode.OK : HttpStatusCode.NotFound)
                {
                    Content = new ByteArrayContent(path == "/" ? Encoding.UTF8.GetBytes("""<link rel="icon" href="/icon.png" sizes="32x32">""") : [1, 2, 3]),
                };
                return Task.FromResult(response);
            },
        };
        using var fixture = Create();
        var folder = fixture.PathFor("favicons");
        var cache = new FaviconCache(folder, fixture.Time, http);
        FaviconCache.Reencode = bytes => Task.FromResult<byte[]?>(bytes.Length == 3 ? [9, 9] : null);
        try
        {
            var ready = new List<string>();
            cache.IconReady += ready.Add;
            Assert.Null(cache.IconPath("https://www.site.example/page", fetch: true));
            await Until(() => ready.Count == 1);
            Assert.Equal("site.example", ready[0]);
            var file = cache.IconPath("https://site.example/", fetch: false);
            Assert.Equal(Path.Combine(folder, "site.example.png"), file);
            Assert.Equal([9, 9], File.ReadAllBytes(file!));
            Assert.Equal(["https://www.site.example/", "https://www.site.example/icon.png"], http.Requests.Select(u => u.AbsoluteUri));
            Assert.Equal(2, http.RequestHeaders.Count);
            Assert.All(http.RequestHeaders, headers => Assert.False(headers.Contains("Cookie")));
            Assert.All(http.RequestHeaders, headers => Assert.False(headers.Contains("Referer")));

            // A site without a usable icon is not asked again for a day.
            Assert.Null(cache.IconPath("https://none.example/", fetch: true));
            await Pump();
            var asked = http.Requests.Count;
            Assert.Null(cache.IconPath("https://none.example/", fetch: true));
            await Pump();
            Assert.Equal(asked, http.Requests.Count);
        }
        finally
        {
            FaviconCache.Reencode = null;
        }
    });

    [Fact]
    public void NothingIsFetchedWithoutAnEncoder() => TestLoop.Run(async () =>
    {
        var http = new RecordingHandler();
        using var fixture = Create();
        var cache = new FaviconCache(fixture.PathFor("favicons"), fixture.Time, http);
        FaviconCache.Reencode = null;
        Assert.Null(cache.IconPath("https://site.example/", fetch: true));
        await Pump();
        Assert.Empty(http.Requests);
    });
}

/// <summary>Records requests (from any thread) with their headers.</summary>
internal sealed class RecordingHandler : HttpMessageHandler
{
    private readonly object gate = new();
    private readonly List<Uri> requests = [];
    private readonly List<System.Net.Http.Headers.HttpRequestHeaders> headers = [];

    public Func<HttpRequestMessage, Task<HttpResponseMessage>> Respond { get; set; } =
        _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

    public List<Uri> Requests { get { lock (gate) return [.. requests]; } }

    public List<System.Net.Http.Headers.HttpRequestHeaders> RequestHeaders { get { lock (gate) return [.. headers]; } }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            requests.Add(request.RequestUri!);
            headers.Add(request.Headers);
        }
        return Respond(request);
    }
}
