using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Mosaic.Core.Gecko;

namespace Mosaic.Core.Tests.Gecko;

/// <summary>
/// Port of <c>RDPClientTests</c> in <c>Tests/MosaicCoreTests/RDPClientTests.swift</c>, run over
/// both transports: TCP is what Windows uses, the Unix socket is what macOS uses.
/// </summary>
public class RdpClientTests
{
    public static TheoryData<FakeTransport> Transports => new() { FakeTransport.Tcp, FakeTransport.Unix };

    private static async Task<RdpClient> AttachAsync(FakeGecko server)
    {
        var client = await server.ConnectAsync();
        await client.AttachToParentProcessAsync();
        return client;
    }

    private static async Task<RdpException> ThrowsAsync(RdpErrorKind kind, Func<Task> action)
    {
        var error = await Assert.ThrowsAsync<RdpException>(action);
        Assert.Equal(kind, error.Kind);
        return error;
    }

    [Theory, MemberData(nameof(Transports))]
    public async Task EvaluatesPrimitives(FakeTransport transport)
    {
        using var server = new FakeGecko(transport);
        using var client = await AttachAsync(server);

        Assert.Equal(2, (await client.EvaluateAsync("1+1"))!.GetValue<int>());
        Assert.Equal("hello", (await client.EvaluateAsync("str"))!.GetValue<string>());
        Assert.True((await client.EvaluateAsync("flag"))!.GetValue<bool>());
        Assert.Null(await client.EvaluateAsync("undef"));
        Assert.Null(await client.EvaluateAsync("null"));
    }

    [Theory, MemberData(nameof(Transports))]
    public async Task ReturnsOtherObjectsAsTheirGrip(FakeTransport transport)
    {
        using var server = new FakeGecko(transport);
        using var client = await AttachAsync(server);

        var grip = Assert.IsType<JsonObject>(await client.EvaluateAsync("object"));
        Assert.Equal("obj1", (string?)grip["actor"]);
        Assert.Null(grip.Parent);
    }

    [Theory, MemberData(nameof(Transports))]
    public async Task FetchesLongStringsInFull(FakeTransport transport)
    {
        using var server = new FakeGecko(transport);
        using var client = await AttachAsync(server);

        Assert.Equal(FakeGecko.LongText, (await client.EvaluateAsync("long"))!.GetValue<string>());
    }

    [Theory, MemberData(nameof(Transports))]
    public async Task ReadsPacketsLargerThanItsBuffer(FakeTransport transport)
    {
        using var server = new FakeGecko(transport);
        using var client = await AttachAsync(server);

        Assert.Equal(FakeGecko.HugeText, (await client.EvaluateAsync("huge"))!.GetValue<string>());
        Assert.Equal("hello", (await client.EvaluateAsync("str"))!.GetValue<string>());
    }

    [Theory, MemberData(nameof(Transports))]
    public async Task ReassemblesPacketsThatArriveInPieces(FakeTransport transport)
    {
        using var server = new FakeGecko(transport) { Dribble = true };
        using var client = await AttachAsync(server);

        Assert.Equal(2, (await client.EvaluateAsync("1+1"))!.GetValue<int>());
        Assert.Equal("after noise", (await client.EvaluateAsync("noise"))!.GetValue<string>());
    }

    [Theory, MemberData(nameof(Transports))]
    public async Task SurfacesScriptExceptions(FakeTransport transport)
    {
        using var server = new FakeGecko(transport);
        using var client = await AttachAsync(server);

        var error = await ThrowsAsync(RdpErrorKind.Remote, () => client.EvaluateAsync("throw"));
        Assert.Contains("boom", error.Message);
        // The connection stays usable after a script error.
        Assert.Equal("hello", (await client.EvaluateAsync("str"))!.GetValue<string>());

        // A rejected awaited promise is an error too, not an empty result.
        await ThrowsAsync(RdpErrorKind.Remote, () => client.EvaluateAsync("reject"));
        Assert.Equal("hello", (await client.EvaluateAsync("str"))!.GetValue<string>());
    }

    [Theory, MemberData(nameof(Transports))]
    public async Task IgnoresUnrelatedEvents(FakeTransport transport)
    {
        using var server = new FakeGecko(transport);
        using var client = await AttachAsync(server);

        Assert.Equal("after noise", (await client.EvaluateAsync("noise"))!.GetValue<string>());
    }

    [Theory, MemberData(nameof(Transports))]
    public async Task GreetingArrivingAfterTheFirstRequestIsNotTakenAsItsReply(FakeTransport transport)
    {
        using var server = new FakeGecko(transport) { GreetLate = true };
        using var client = await AttachAsync(server);

        Assert.Equal(2, (await client.EvaluateAsync("1+1"))!.GetValue<int>());
    }

    [Theory, MemberData(nameof(Transports))]
    public async Task ConcurrentEvaluationsGetTheirOwnResults(FakeTransport transport)
    {
        using var server = new FakeGecko(transport);
        using var client = await AttachAsync(server);

        var results = await Task.WhenAll(Enumerable.Range(0, 40).Select(index =>
            Task.Run(async () => (await client.EvaluateAsync($"job{index}"))!.GetValue<string>())));

        Assert.Equal(Enumerable.Range(0, 40).Select(index => $"echo:job{index}"), results);
    }

    [Theory, MemberData(nameof(Transports))]
    public async Task EvaluatingBeforeAttachingFails(FakeTransport transport)
    {
        using var server = new FakeGecko(transport);
        using var client = await server.ConnectAsync();

        await ThrowsAsync(RdpErrorKind.Protocol, () => client.EvaluateAsync("1+1"));
    }

    [Theory, MemberData(nameof(Transports))]
    public async Task ADroppedConnectionFailsPendingWorkAndReportsCloseOnce(FakeTransport transport)
    {
        using var server = new FakeGecko(transport) { HangUpOn = "hang" };
        using var client = await AttachAsync(server);
        var closed = 0;
        client.Closed += () => Interlocked.Increment(ref closed);

        await ThrowsAsync(RdpErrorKind.Closed, () => client.EvaluateAsync("hang"));
        await ThrowsAsync(RdpErrorKind.Closed, () => client.EvaluateAsync("1+1"));

        await Eventually.TrueAsync(() => Volatile.Read(ref closed) > 0);
        Assert.True(client.IsClosed);
        client.Close();
        await Task.Delay(50);
        Assert.Equal(1, Volatile.Read(ref closed));
    }

    [Theory, MemberData(nameof(Transports))]
    public async Task ClosingFailsWorkInFlightAndReportsCloseOnce(FakeTransport transport)
    {
        // The server sits on "wait" until the client has closed the connection under it.
        using var gate = new ManualResetEventSlim();
        using var server = new FakeGecko(transport)
        {
            Responder = text =>
            {
                if (text == "wait") gate.Wait(TimeSpan.FromSeconds(10));
                return null;
            },
        };
        var client = await AttachAsync(server);
        var closed = 0;
        client.Closed += () => Interlocked.Increment(ref closed);

        var pending = client.EvaluateAsync("wait");
        client.Close();
        client.Close();
        client.Dispose();

        await ThrowsAsync(RdpErrorKind.Closed, () => pending);
        await ThrowsAsync(RdpErrorKind.Closed, () => client.EvaluateAsync("1+1"));
        await Eventually.TrueAsync(() => Volatile.Read(ref closed) > 0);
        gate.Set();
        await Task.Delay(50);
        Assert.Equal(1, Volatile.Read(ref closed));
    }

    [Fact]
    public async Task ConnectingToNothingThrows()
    {
        int unusedPort;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            unusedPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        await ThrowsAsync(RdpErrorKind.ConnectFailed, () => RdpClient.ConnectTcpAsync("127.0.0.1", unusedPort));
        await ThrowsAsync(RdpErrorKind.ConnectFailed,
            () => RdpClient.ConnectUnixAsync(Path.Combine(Path.GetTempPath(), "mosaic-no-such.sock")));
        await ThrowsAsync(RdpErrorKind.ConnectFailed,
            () => RdpClient.ConnectUnixAsync(Path.Combine(Path.GetTempPath(), new string('x', 300) + ".sock")));
    }

    [Fact]
    public async Task ConnectingCanBeCancelled()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RdpClient.ConnectTcpAsync("127.0.0.1", 9, cancelled.Token));
    }
}
