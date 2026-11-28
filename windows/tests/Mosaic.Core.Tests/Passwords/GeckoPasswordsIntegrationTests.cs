using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Mosaic.Core.Gecko;
using Mosaic.Core.Passwords;
using Mosaic.Core.Tests.Gecko;

namespace Mosaic.Core.Tests.Passwords;

/// <summary>
/// The shared password actors (<c>Resources/gecko/passwords.sys.mjs</c>,
/// <c>MosaicPasswords*.sys.mjs</c>, <c>passwords-page.js</c>) in a real, headless Gecko (the
/// Firefox named by <c>MOSAIC_GECKO_EXE</c>; CI uses the runner's), driven the way Windows Mosaic
/// drives them: <c>passwordsStart({separateQueue: true})</c>, a bare "passwords" marker in the
/// snapshot's events, the messages from <c>passwordsTake</c>. Pages come from a local server on
/// two loopback ports (two origins).
/// </summary>
public sealed class GeckoPasswordsIntegrationTests : IDisposable
{
    private readonly TemporaryDirectory profile = new();
    private readonly LoginServer site = new();
    private readonly LoginServer other = new();
    private GeckoEngine? engine;

    static GeckoPasswordsIntegrationTests() => Environment.SetEnvironmentVariable("MOZ_APP_NO_DOCK", "1");

    public void Dispose()
    {
        engine?.StopAndWait(TimeSpan.FromSeconds(5));
        site.Dispose();
        other.Dispose();
        profile.Dispose();
    }

    private static string AgentDirectory
    {
        get
        {
            for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
            {
                var candidate = Path.Combine(folder.FullName, "Resources", "gecko");
                if (File.Exists(Path.Combine(candidate, "passwords.sys.mjs"))) return candidate;
            }
            throw new DirectoryNotFoundException("Resources/gecko was not found above the test folder.");
        }
    }

    private async Task<GeckoEngine> StartAsync()
    {
        engine = new GeckoEngine(new GeckoEngineOptions
        {
            ProfileDirectory = profile.File("profile"),
            AgentDirectory = AgentDirectory,
            Runtime = GeckoRuntime.Locate(),
            ExtraArguments = ["--headless"],
            PollInterval = TimeSpan.FromMilliseconds(50),
        });
        engine.SnapshotReceived += snapshot =>
        {
            lock (markers)
            {
                markers.AddRange(snapshot.Events.Where(type => type == "passwords"));
                titles = snapshot.Tabs.ToDictionary(tab => tab.Id, tab => tab.Title);
            }
        };
        await engine.StartAsync();
        Assert.True((await engine.CallAsync("passwordsStart", new JsonObject { ["separateQueue"] = true }))!.GetValue<bool>());
        return engine;
    }

    private readonly List<string> markers = [];
    private Dictionary<int, string> titles = [];
    private readonly List<PasswordPageMessage> received = [];

    /// <summary>Reads password events until <paramref name="found"/> matches one, as the app does.</summary>
    private async Task<PasswordPageMessage> UntilAsync(Func<PasswordPageMessage, bool> found, string what)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            received.AddRange(PasswordManager.DecodeGeckoEvents(await engine!.CallAsync("passwordsTake")));
            if (received.FirstOrDefault(found) is { } message) return message;
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(40),
                $"Gecko never reported {what}; got: {string.Join(" | ", received.Select(m => $"{m.Kind} {m.Origin} {m.Frame}"))}");
            await Task.Delay(100);
        }
    }

    private async Task UntilTitleAsync(int tab, string title)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            string? current;
            lock (markers) current = titles.GetValueOrDefault(tab);
            if (current == title) return;
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"Tab {tab} never showed \"{title}\" (last: \"{current}\").");
            await Task.Delay(50);
        }
    }

    [GeckoFact]
    public async Task ReportsSignInsWithTheTrueOriginAndRefusesCrossOriginFrames()
    {
        var gecko = await StartAsync();
        var origin = site.Origin;
        var tab = (await gecko.CallAsync("openTab", $"{site.Url}login?auto=alice:Pw-1234&frame={Uri.EscapeDataString(other.Url + "frame")}", true))!.GetValue<int>();

        var page = await UntilAsync(m => m.Kind == PasswordPageMessage.MessageKind.Page && m.Frame == PasswordPageMessage.FrameKind.Main && m.HasLogin == true, "the sign-in form");
        Assert.Equal(origin, page.Origin?.String);
        Assert.Equal(tab, page.Tab);
        Assert.False(page.IsPrivate);

        // The page fills and submits itself; the form posts to /home.
        var submit = await UntilAsync(m => m.Kind == PasswordPageMessage.MessageKind.Submit && m.Frame == PasswordPageMessage.FrameKind.Main, "the submitted sign-in");
        Assert.Equal(new PasswordSubmission(P.Origin(origin), "alice", "Pw-1234"), submit.Submission);
        await UntilAsync(m => m.Kind == PasswordPageMessage.MessageKind.Page && m.Frame == PasswordPageMessage.FrameKind.Main && m.Document != page.Document && m.HasLogin == false, "the page after signing in");

        // The same-origin frame's own sign-in is reported as such…
        var same = await UntilAsync(m => m.Kind == PasswordPageMessage.MessageKind.Submit && m.Frame == PasswordPageMessage.FrameKind.SameOrigin, "the same-origin frame's sign-in");
        Assert.Equal("sam", same.Username);
        // …and the cross-origin frame (another port: another origin) never gets through, although it submitted too.
        await Eventually.TrueAsync(() => other.FrameSubmits > 0, 30, "the cross-origin frame never submitted");
        await Task.Delay(1000);
        received.AddRange(PasswordManager.DecodeGeckoEvents(await gecko.CallAsync("passwordsTake")));
        Assert.DoesNotContain(received, m => m.Origin?.String == other.Origin || m.Frame == PasswordPageMessage.FrameKind.CrossOrigin || m.Username == "eve");

        // Windows reads events through the separate queue: the snapshot only carried markers.
        lock (markers) Assert.NotEmpty(markers);
    }

    [GeckoFact]
    public async Task FillsOnlyTheExactOrigin()
    {
        var gecko = await StartAsync();
        var tab = (await gecko.CallAsync("openTab", site.Url + "login", true))!.GetValue<int>();
        await UntilAsync(m => m.Kind == PasswordPageMessage.MessageKind.Page && m.HasLogin == true && m.Tab == tab, "the sign-in form");

        Assert.Equal(0, (await gecko.CallAsync("passwordsFill", tab, other.Origin, "bob", "nope"))!.GetValue<int>());
        Assert.Equal(2, (await gecko.CallAsync("passwordsFill", tab, site.Origin, "bob", "s3cr3t"))!.GetValue<int>());
        await UntilTitleAsync(tab, "filled:bob:6");

        await gecko.CallAsync("navigate", tab, site.Url + "signup");
        await UntilAsync(m => m.Kind == PasswordPageMessage.MessageKind.Page && m.HasNewPassword == true && m.Tab == tab, "the sign-up form");
        Assert.Equal(2, (await gecko.CallAsync("passwordsFillGenerated", tab, site.Origin, "Generated-Pass-20chrs"))!.GetValue<int>());
        await UntilTitleAsync(tab, "generated:21:21");
    }

    /// <summary>
    /// Test pages on a loopback port. A bare <see cref="TcpListener"/>: <see cref="HttpListener"/>
    /// needs a URL reservation on Windows.
    /// </summary>
    private sealed class LoginServer : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private int frameSubmits;

        public LoginServer()
        {
            listener.Start();
            Origin = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
            _ = AcceptAsync();
        }

        public string Origin { get; }
        public string Url => Origin + "/";
        public int FrameSubmits => Volatile.Read(ref frameSubmits);

        private async Task AcceptAsync()
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync();
                }
                catch (Exception error) when (error is SocketException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }
                _ = ServeAsync(client);
            }
        }

        private const string Login = """
            <!doctype html><title>Login</title>
            <form id="f" method="post" action="/home">
              <input type="email" id="u" name="email" autocomplete="username">
              <input type="password" id="p" name="pw" autocomplete="current-password">
              <button>Sign in</button>
            </form>
            <script>
              const q = new URLSearchParams(location.search);
              const show = () => { document.title = "filled:" + u.value + ":" + p.value.length; };
              u.addEventListener("input", show);
              p.addEventListener("input", show);
              if (q.get("frame")) {
                for (const src of ["/sameframe", q.get("frame")]) {
                  const frame = document.createElement("iframe");
                  frame.src = src;
                  document.body.appendChild(frame);
                }
              }
              if (q.get("auto")) {
                const [user, pass] = q.get("auto").split(":");
                setTimeout(() => { u.value = user; p.value = pass; f.requestSubmit(); }, 4000);
              }
            </script>
            """;

        /// <summary>A sign-in inside a frame, handled by script (the form disappears when it is done).</summary>
        private static string Frame(string user) => $$"""
            <!doctype html><title>frame</title>
            <form id="g"><input id="u" name="user" autocomplete="username"><input type="password" id="p"><button>Go</button></form>
            <script>
              g.addEventListener("submit", e => { e.preventDefault(); fetch("/frame-submitted"); g.remove(); });
              setTimeout(() => { u.value = "{{user}}"; p.value = "framepw-1"; g.requestSubmit(); }, 1500);
            </script>
            """;

        private const string Signup = """
            <!doctype html><title>Signup</title>
            <form><input type="email" autocomplete="username">
              <input type="password" id="n1" autocomplete="new-password"><input type="password" id="n2" autocomplete="new-password"></form>
            <script>
              const show = () => { document.title = "generated:" + n1.value.length + ":" + n2.value.length; };
              n1.addEventListener("input", show);
              n2.addEventListener("input", show);
            </script>
            """;

        private async Task ServeAsync(TcpClient client)
        {
            using var _ = client;
            try
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var requestLine = await reader.ReadLineAsync(patience.Token) ?? "";
                var length = 0;
                string? header;
                while (!string.IsNullOrEmpty(header = await reader.ReadLineAsync(patience.Token)))
                {
                    if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(header[15..].Trim());
                }
                if (length > 0) await reader.ReadAsync(new char[length], patience.Token);

                var path = requestLine.Split(' ') is [_, var target, ..] ? target.Split('?')[0] : "/";
                if (path == "/frame-submitted") Interlocked.Increment(ref frameSubmits);
                var body = path switch
                {
                    "/login" => Login,
                    "/home" => "<!doctype html><title>Home</title>Signed in.",
                    "/signup" => Signup,
                    "/frame" => Frame("eve"),
                    "/sameframe" => Frame("sam"),
                    _ => "<!doctype html><title>Other</title>",
                };
                var bytes = Encoding.UTF8.GetBytes(body);
                var head = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\n" +
                    "Cache-Control: no-store\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(head);
                await stream.WriteAsync(bytes);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or SocketException or FormatException)
            {
            }
        }

        public void Dispose() => listener.Stop();
    }
}
