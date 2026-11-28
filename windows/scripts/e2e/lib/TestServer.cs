// Local web server for Mosaic's end-to-end checks (scripts/e2e). Compiled by TestServer.psm1
// with Add-Type, so it is written in C# 5 (Windows PowerShell 5.1 compiles with the .NET
// Framework's CodeDOM compiler): no string interpolation, no ?., no expression-bodied members.
//
// A plain TcpListener, because HttpListener needs a URL reservation unless run as an
// administrator. One thread per connection, so a throttled download does not hold up the pages.
// Listens on 127.0.0.1 and, when it can, on [::1] with the same port, so that
// http://127.0.0.1:<port>/ and http://localhost:<port>/ are two different sites served by one
// server (cross-site frames, third-party cookies, per-site settings).
//
// Routes:
//   /<file>                     a file of the site folder; {{ORIGIN}}, {{ALT_ORIGIN}}, {{PORT}} replaced
//   /report?k=v&...             records a report (also POST, body recorded as "body"); 204
//   /echo                       HTML page listing the request's headers (title "Echo")
//   /download/<name>?size=<bytes>&rate=<bytes per second>&type=<mime>&inline=1
//                               a generated file; honours Range (resumable downloads)
//   /slow?ms=<n>&title=<t>      a page served after a delay
//   /setcookie?name=<n>&value=<v>&title=<t>   a page that sets a cookie
//   /redirect?to=<url>          302
//   /status/<code>              an empty response with that status
//   /tone.wav?seconds=<n>       a generated sine tone (audio checks)
//   /page?title=<t>&body=<b>    a minimal page with that title
//   /search?q=<q>               a page titled "Search: <q>" (custom search engine checks)

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace MosaicE2E
{
    public sealed class TestRequest
    {
        public DateTime Time;
        public string Method;
        public string Target;      // path and query, as requested
        public string Path;
        public string Query;
        public string Host;
        public Dictionary<string, string> Headers;
        public string Body;
        public int Status;
    }

    public sealed class TestReport
    {
        public DateTime Time;
        public string Key;
        public string Value;
        public string Token;
        public string Referer;
    }

    public sealed class TestServer
    {
        private readonly string siteRoot;
        private readonly List<TcpListener> listeners = new List<TcpListener>();
        private volatile bool running;

        public readonly ConcurrentQueue<TestRequest> Requests = new ConcurrentQueue<TestRequest>();
        public readonly ConcurrentQueue<TestReport> Reports = new ConcurrentQueue<TestReport>();

        public int Port { get; private set; }
        public string Origin { get { return "http://127.0.0.1:" + Port; } }
        public string AltOrigin { get { return "http://localhost:" + Port; } }
        public bool HasAltOrigin { get; private set; }

        public TestServer(string siteRoot)
        {
            this.siteRoot = siteRoot;
        }

        public void Start()
        {
            var v4 = new TcpListener(IPAddress.Loopback, 0);
            v4.Start();
            Port = ((IPEndPoint)v4.LocalEndpoint).Port;
            listeners.Add(v4);
            try
            {
                var v6 = new TcpListener(IPAddress.IPv6Loopback, Port);
                v6.Start();
                listeners.Add(v6);
                HasAltOrigin = true;
            }
            catch (Exception)
            {
                // No IPv6 loopback: localhost still reaches 127.0.0.1 in every browser.
                HasAltOrigin = true;
            }
            running = true;
            foreach (var listener in listeners)
            {
                var l = listener;
                var thread = new Thread(delegate () { AcceptLoop(l); });
                thread.IsBackground = true;
                thread.Name = "e2e-accept";
                thread.Start();
            }
        }

        public void Stop()
        {
            running = false;
            foreach (var listener in listeners)
            {
                try { listener.Stop(); } catch (Exception) { }
            }
        }

        public TestRequest[] RequestsSnapshot() { return Requests.ToArray(); }
        public TestReport[] ReportsSnapshot() { return Reports.ToArray(); }

        private void AcceptLoop(TcpListener listener)
        {
            while (running)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); }
                catch (Exception) { return; }
                var c = client;
                var thread = new Thread(delegate () { Serve(c); });
                thread.IsBackground = true;
                thread.Name = "e2e-conn";
                thread.Start();
            }
        }

        private static string ReadLine(Stream stream)
        {
            var bytes = new List<byte>();
            while (true)
            {
                int b = stream.ReadByte();
                if (b < 0) return bytes.Count == 0 ? null : Encoding.ASCII.GetString(bytes.ToArray());
                if (b == '\n') break;
                if (b != '\r') bytes.Add((byte)b);
                if (bytes.Count > 65536) break;
            }
            return Encoding.ASCII.GetString(bytes.ToArray());
        }

        private void Serve(TcpClient client)
        {
            try
            {
                // Browsers open connections ahead of need; keep-alive is not offered, so each
                // connection carries one request. An idle one is dropped after a while.
                client.ReceiveTimeout = 15000;
                client.SendTimeout = 60000;
                var stream = client.GetStream();
                string requestLine = ReadLine(stream);
                if (string.IsNullOrEmpty(requestLine)) return;
                var parts = requestLine.Split(' ');
                if (parts.Length < 2) return;
                var request = new TestRequest();
                request.Time = DateTime.UtcNow;
                request.Method = parts[0];
                request.Target = parts[1];
                request.Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                string line;
                while (!string.IsNullOrEmpty(line = ReadLine(stream)))
                {
                    int colon = line.IndexOf(':');
                    if (colon > 0) request.Headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
                }
                string host;
                request.Host = request.Headers.TryGetValue("Host", out host) ? host : "";
                int q = request.Target.IndexOf('?');
                request.Path = q >= 0 ? request.Target.Substring(0, q) : request.Target;
                request.Query = q >= 0 ? request.Target.Substring(q + 1) : "";
                string lengthText;
                int length;
                if (request.Headers.TryGetValue("Content-Length", out lengthText) && int.TryParse(lengthText, out length) && length > 0 && length < 1048576)
                {
                    var body = new byte[length];
                    int read = 0;
                    while (read < length)
                    {
                        int n = stream.Read(body, read, length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    request.Body = Encoding.UTF8.GetString(body, 0, read);
                }
                Requests.Enqueue(request);
                Route(request, stream);
                stream.Flush();
            }
            catch (Exception)
            {
                // A connection the browser closed or that timed out; serve the next one.
            }
            finally
            {
                try { client.Close(); } catch (Exception) { }
            }
        }

        public static Dictionary<string, string> ParseQuery(string query)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(query)) return result;
            foreach (var pair in query.Split('&'))
            {
                if (pair.Length == 0) continue;
                int eq = pair.IndexOf('=');
                string key = Uri.UnescapeDataString((eq >= 0 ? pair.Substring(0, eq) : pair).Replace('+', ' '));
                string value = eq >= 0 ? Uri.UnescapeDataString(pair.Substring(eq + 1).Replace('+', ' ')) : "";
                result[key] = value;
            }
            return result;
        }

        private static string Get(Dictionary<string, string> query, string key, string fallback)
        {
            string value;
            return query.TryGetValue(key, out value) ? value : fallback;
        }

        private static string Html(string text)
        {
            return WebUtility.HtmlEncode(text ?? "");
        }

        private void Route(TestRequest request, Stream stream)
        {
            var query = ParseQuery(request.Query);
            string path = request.Path;

            if (path == "/report")
            {
                string token = Get(query, "token", "");
                string referer;
                request.Headers.TryGetValue("Referer", out referer);
                foreach (var pair in query)
                {
                    if (pair.Key == "token") continue;
                    var report = new TestReport();
                    report.Time = DateTime.UtcNow;
                    report.Key = pair.Key;
                    report.Value = pair.Value;
                    report.Token = token;
                    report.Referer = referer ?? "";
                    Reports.Enqueue(report);
                }
                if (!string.IsNullOrEmpty(request.Body))
                {
                    var report = new TestReport();
                    report.Time = DateTime.UtcNow;
                    report.Key = "body";
                    report.Value = request.Body;
                    report.Token = token;
                    report.Referer = referer ?? "";
                    Reports.Enqueue(report);
                }
                Respond(request, stream, 204, "text/plain", new byte[0], null);
                return;
            }
            if (path == "/echo")
            {
                var sb = new StringBuilder();
                sb.Append("<!doctype html><html><head><meta charset=\"utf-8\"><title>Echo</title></head><body><h1>Echo</h1><dl>");
                sb.Append("<dt>target</dt><dd id=\"target\">" + Html(request.Target) + "</dd>");
                foreach (var header in request.Headers)
                {
                    sb.Append("<dt>" + Html(header.Key) + "</dt><dd>" + Html(header.Value) + "</dd>");
                }
                sb.Append("</dl></body></html>");
                RespondHtml(request, stream, sb.ToString(), null);
                return;
            }
            if (path == "/page")
            {
                string title = Get(query, "title", "Mosaic Test Page");
                string body = Get(query, "body", title);
                RespondHtml(request, stream, "<!doctype html><html><head><meta charset=\"utf-8\"><title>" + Html(title) +
                    "</title></head><body><h1>" + Html(title) + "</h1><p>" + Html(body) + "</p></body></html>", null);
                return;
            }
            if (path == "/search")
            {
                string q = Get(query, "q", "");
                RespondHtml(request, stream, "<!doctype html><html><head><meta charset=\"utf-8\"><title>Search: " + Html(q) +
                    "</title></head><body><h1>Search: " + Html(q) + "</h1></body></html>", null);
                return;
            }
            if (path == "/slow")
            {
                int ms;
                if (!int.TryParse(Get(query, "ms", "3000"), out ms)) ms = 3000;
                Thread.Sleep(Math.Min(ms, 120000));
                string title = Get(query, "title", "Slow Page");
                RespondHtml(request, stream, "<!doctype html><html><head><meta charset=\"utf-8\"><title>" + Html(title) +
                    "</title></head><body><h1>" + Html(title) + "</h1></body></html>", null);
                return;
            }
            if (path == "/setcookie")
            {
                string name = Get(query, "name", "e2e");
                string value = Get(query, "value", "1");
                string title = Get(query, "title", "Cookie Set");
                var extra = new List<string>();
                extra.Add("Set-Cookie: " + name + "=" + value + "; Path=/; Max-Age=86400; SameSite=Lax");
                RespondHtml(request, stream, "<!doctype html><html><head><meta charset=\"utf-8\"><title>" + Html(title) +
                    "</title></head><body><h1>" + Html(title) + "</h1><script>try{localStorage.setItem('e2e','" + Html(value) +
                    "')}catch(e){}</script></body></html>", extra);
                return;
            }
            if (path == "/redirect")
            {
                var extra = new List<string>();
                extra.Add("Location: " + Get(query, "to", "/"));
                Respond(request, stream, 302, "text/plain", new byte[0], extra);
                return;
            }
            if (path.StartsWith("/status/"))
            {
                int code;
                if (!int.TryParse(path.Substring(8), out code)) code = 500;
                Respond(request, stream, code, "text/plain", Encoding.ASCII.GetBytes("status " + code), null);
                return;
            }
            if (path == "/tone.wav")
            {
                double seconds;
                if (!double.TryParse(Get(query, "seconds", "30"), NumberStyles.Float, CultureInfo.InvariantCulture, out seconds)) seconds = 30;
                Respond(request, stream, 200, "audio/wav", Tone(Math.Min(seconds, 300)), null);
                return;
            }
            if (path.StartsWith("/download/"))
            {
                ServeDownload(request, stream, Uri.UnescapeDataString(path.Substring(10)), query);
                return;
            }
            ServeFile(request, stream, path);
        }

        private void ServeFile(TestRequest request, Stream stream, string path)
        {
            if (path == "/") path = "/index.html";
            string relative = Uri.UnescapeDataString(path.TrimStart('/')).Replace('/', System.IO.Path.DirectorySeparatorChar);
            if (relative.Contains("..")) { Respond(request, stream, 403, "text/plain", new byte[0], null); return; }
            string file = System.IO.Path.Combine(siteRoot, relative);
            if (!File.Exists(file))
            {
                Respond(request, stream, 404, "text/html; charset=utf-8",
                    Encoding.UTF8.GetBytes("<!doctype html><title>Not Found</title><h1>Not Found</h1>"), null);
                return;
            }
            string extension = System.IO.Path.GetExtension(file).ToLowerInvariant();
            string type = "application/octet-stream";
            bool text = true;
            switch (extension)
            {
                case ".html": type = "text/html; charset=utf-8"; break;
                case ".js": type = "text/javascript; charset=utf-8"; break;
                case ".css": type = "text/css; charset=utf-8"; break;
                case ".json": type = "application/json"; break;
                case ".txt": type = "text/plain; charset=utf-8"; break;
                case ".svg": type = "image/svg+xml"; break;
                case ".png": type = "image/png"; text = false; break;
                default: text = false; break;
            }
            byte[] content;
            if (text)
            {
                string body = File.ReadAllText(file, Encoding.UTF8)
                    .Replace("{{ORIGIN}}", Origin).Replace("{{ALT_ORIGIN}}", AltOrigin)
                    .Replace("{{PORT}}", Port.ToString(CultureInfo.InvariantCulture));
                content = Encoding.UTF8.GetBytes(body);
            }
            else
            {
                content = File.ReadAllBytes(file);
            }
            Respond(request, stream, 200, type, content, null);
        }

        private void ServeDownload(TestRequest request, Stream stream, string name, Dictionary<string, string> query)
        {
            long size;
            if (!long.TryParse(Get(query, "size", "1048576"), out size)) size = 1048576;
            long rate;
            if (!long.TryParse(Get(query, "rate", "0"), out rate)) rate = 0;
            string type = Get(query, "type", "application/octet-stream");
            bool inline = Get(query, "inline", "") == "1";
            long start = 0;
            string range;
            if (request.Headers.TryGetValue("Range", out range) && range.StartsWith("bytes="))
            {
                string from = range.Substring(6).Split('-')[0];
                long.TryParse(from, out start);
                if (start < 0 || start >= size) start = 0;
            }
            var head = new StringBuilder();
            int status = start > 0 ? 206 : 200;
            request.Status = status;
            head.Append("HTTP/1.1 " + status + (status == 206 ? " Partial Content" : " OK") + "\r\n");
            head.Append("Content-Type: " + type + "\r\n");
            head.Append("Content-Length: " + (size - start).ToString(CultureInfo.InvariantCulture) + "\r\n");
            head.Append("Accept-Ranges: bytes\r\n");
            head.Append("ETag: \"e2e-" + name + "-" + size + "\"\r\n");
            head.Append("Last-Modified: Mon, 02 Nov 2026 10:00:00 GMT\r\n");
            if (start > 0) head.Append("Content-Range: bytes " + start + "-" + (size - 1) + "/" + size + "\r\n");
            if (!inline) head.Append("Content-Disposition: attachment; filename=\"" + name + "\"\r\n");
            head.Append("Cache-Control: no-store\r\nConnection: close\r\n\r\n");
            var headBytes = Encoding.ASCII.GetBytes(head.ToString());
            stream.Write(headBytes, 0, headBytes.Length);
            if (request.Method == "HEAD") return;

            // Content: a repeating, position-dependent pattern, so a resumed file is checkable.
            var chunk = new byte[rate > 0 ? Math.Max(1024, Math.Min(65536, rate / 10)) : 65536];
            long position = start;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            long sent = 0;
            while (position < size)
            {
                int count = (int)Math.Min(chunk.Length, size - position);
                for (int i = 0; i < count; i++) chunk[i] = (byte)((position + i) % 251);
                stream.Write(chunk, 0, count);
                position += count;
                sent += count;
                if (rate > 0)
                {
                    long due = sent * 1000 / rate;
                    long wait = due - clock.ElapsedMilliseconds;
                    if (wait > 0) Thread.Sleep((int)Math.Min(wait, 2000));
                }
            }
        }

        private static byte[] Tone(double seconds)
        {
            const int sampleRate = 8000;
            int samples = (int)(sampleRate * seconds);
            var data = new MemoryStream();
            var writer = new BinaryWriter(data);
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + samples * 2);
            writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(sampleRate);
            writer.Write(sampleRate * 2);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(samples * 2);
            for (int i = 0; i < samples; i++)
            {
                writer.Write((short)(Math.Sin(2 * Math.PI * 440 * i / sampleRate) * 6000));
            }
            writer.Flush();
            return data.ToArray();
        }

        private void RespondHtml(TestRequest request, Stream stream, string html, List<string> extraHeaders)
        {
            Respond(request, stream, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html), extraHeaders);
        }

        private static string Reason(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 204: return "No Content";
                case 206: return "Partial Content";
                case 302: return "Found";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                default: return "Status";
            }
        }

        private void Respond(TestRequest request, Stream stream, int status, string type, byte[] content, List<string> extraHeaders)
        {
            request.Status = status;
            var head = new StringBuilder();
            head.Append("HTTP/1.1 " + status + " " + Reason(status) + "\r\n");
            head.Append("Content-Type: " + type + "\r\n");
            head.Append("Content-Length: " + content.Length + "\r\n");
            head.Append("Cache-Control: no-store\r\n");
            if (extraHeaders != null) foreach (var header in extraHeaders) head.Append(header + "\r\n");
            head.Append("Connection: close\r\n\r\n");
            var bytes = Encoding.ASCII.GetBytes(head.ToString());
            stream.Write(bytes, 0, bytes.Length);
            if (request.Method != "HEAD") stream.Write(content, 0, content.Length);
        }
    }
}
