using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Mosaic.Core.Tests.Downloads;

/// <summary>
/// A tiny HTTP/1.1 file server on the loopback interface for download tests, like the throttled
/// server the macOS downloads scripts used: files are sent slowly (so a test can pause and resume
/// half way), byte ranges are honoured (so a resumed download continues where it stopped), and
/// every request is recorded. A raw socket rather than <c>HttpListener</c>, so it needs no URL
/// reservation on Windows and runs the same everywhere.
/// </summary>
internal sealed class ThrottledServer : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stopping = new();
    private readonly ConcurrentDictionary<string, (byte[] Body, string Type, string? Disposition, int BytesPerSecond)> files = new();

    public ThrottledServer()
    {
        listener.Start();
        _ = AcceptAsync();
    }

    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    public string Url(string path) => $"http://127.0.0.1:{Port}/{path.TrimStart('/')}";

    /// <summary>Requests as "GET /path" plus " range=bytes=N-" when one was asked for.</summary>
    public ConcurrentQueue<string> Requests { get; } = new();

    /// <param name="bytesPerSecond">0 sends at full speed.</param>
    public void Serve(string path, byte[] body, string type = "application/octet-stream", string? disposition = null, int bytesPerSecond = 0) =>
        files["/" + path.TrimStart('/')] = (body, type, disposition, bytesPerSecond);

    public static byte[] Pattern(int length)
    {
        var data = new byte[length];
        for (var index = 0; index < length; index++) data[index] = (byte)(index * 31 % 251);
        return data;
    }

    private async Task AcceptAsync()
    {
        while (!stopping.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(stopping.Token);
            }
            catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            _ = ServeAsync(client);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var _ = client;
        try
        {
            var stream = client.GetStream();
            var head = await ReadHeadAsync(stream);
            if (head is null) return;
            var lines = head.Split("\r\n");
            var parts = lines[0].Split(' ');
            if (parts.Length < 2) return;
            var path = parts[1].Split('?')[0];
            var range = lines.Skip(1).FirstOrDefault(line => line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))?[6..].Trim();
            Requests.Enqueue($"{parts[0]} {path}" + (range is null ? "" : $" range={range}"));

            if (!files.TryGetValue(path, out var file))
            {
                await WriteAsync(stream, "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                return;
            }
            long start = 0;
            if (range is not null && range.StartsWith("bytes=", StringComparison.Ordinal) &&
                long.TryParse(range[6..].Split('-')[0], out var from) && from < file.Body.Length)
            {
                start = from;
            }
            var length = file.Body.Length - start;
            var header = new StringBuilder();
            header.Append(start > 0 ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
            header.Append($"Content-Type: {file.Type}\r\n");
            header.Append($"Content-Length: {length}\r\n");
            header.Append("Accept-Ranges: bytes\r\n");
            header.Append("ETag: \"mosaic-test\"\r\n");
            header.Append("Last-Modified: Mon, 02 Mar 2026 09:00:00 GMT\r\n");
            if (start > 0) header.Append($"Content-Range: bytes {start}-{file.Body.Length - 1}/{file.Body.Length}\r\n");
            if (file.Disposition is { } disposition) header.Append($"Content-Disposition: {disposition}\r\n");
            header.Append("Connection: close\r\n\r\n");
            await WriteAsync(stream, header.ToString());
            if (parts[0] == "HEAD") return;

            const int chunk = 16 * 1024;
            for (var offset = start; offset < file.Body.Length; offset += chunk)
            {
                var count = (int)Math.Min(chunk, file.Body.Length - offset);
                await stream.WriteAsync(file.Body.AsMemory((int)offset, count), stopping.Token);
                if (file.BytesPerSecond > 0) await Task.Delay(TimeSpan.FromSeconds((double)count / file.BytesPerSecond), stopping.Token);
            }
        }
        catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // The client went away (a paused or cancelled download): nothing to do.
        }
    }

    private static async Task<string?> ReadHeadAsync(NetworkStream stream)
    {
        var buffer = new List<byte>();
        var one = new byte[1];
        while (buffer.Count < 64 * 1024)
        {
            if (await stream.ReadAsync(one) == 0) return null;
            buffer.Add(one[0]);
            var n = buffer.Count;
            if (n >= 4 && buffer[n - 4] == '\r' && buffer[n - 3] == '\n' && buffer[n - 2] == '\r' && buffer[n - 1] == '\n')
            {
                return Encoding.ASCII.GetString([.. buffer]);
            }
        }
        return null;
    }

    private Task WriteAsync(NetworkStream stream, string text) => stream.WriteAsync(Encoding.ASCII.GetBytes(text), stopping.Token).AsTask();

    public void Dispose()
    {
        stopping.Cancel();
        listener.Stop();
    }
}

public class ThrottledServerTests
{
    [Fact]
    public async Task ServesRangesSlowly()
    {
        using var server = new ThrottledServer();
        var body = ThrottledServer.Pattern(64 * 1024);
        server.Serve("a.bin", body, disposition: "attachment; filename=\"a.bin\"", bytesPerSecond: 256 * 1024);
        using var http = new HttpClient();
        var whole = await http.GetByteArrayAsync(server.Url("a.bin"));
        Assert.Equal(body, whole);
        using var request = new HttpRequestMessage(HttpMethod.Get, server.Url("a.bin"));
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(1000, null);
        using var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(body[1000..], await response.Content.ReadAsByteArrayAsync());
        Assert.Contains("GET /a.bin range=bytes=1000-", server.Requests);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(server.Url("missing"))).StatusCode);
    }
}
