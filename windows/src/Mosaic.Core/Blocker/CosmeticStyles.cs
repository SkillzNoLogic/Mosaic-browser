using System.Text.Json;

namespace Mosaic.Core.Blocker;

/// <summary>
/// Cosmetic filtering's stylesheets, computed from an engine on demand and cached: the generic
/// sheet once, each hostname's rules the first time a page on it loads. Safe to use from any thread.
/// </summary>
/// <remarks>Port of <c>CosmeticStyleCache</c> in <c>Sources/MosaicCore/Blocker/CosmeticStyles.swift</c>.</remarks>
public sealed class CosmeticStyleCache(BlockerEngine engine, int capacity = 256)
{
    private readonly Lock gate = new();
    private string? generic;
    /// <summary>Generic sheets with a site's exceptions left out, keyed by the exceptions.</summary>
    private readonly Dictionary<string, string> genericVariants = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CosmeticRules> byHost = new(StringComparer.Ordinal);
    private readonly Queue<string> hostOrder = new();

    public BlockerEngine Engine { get; } = engine;

    /// <summary>Site-specific rules for <paramref name="hostname"/> (without a port).</summary>
    public CosmeticRules RulesFor(string hostname)
    {
        var host = hostname.ToLowerInvariant();
        lock (gate)
        {
            if (byHost.TryGetValue(host, out var cached)) return cached;
        }
        var rules = Engine.CosmeticRulesFor(host);
        lock (gate)
        {
            if (!byHost.ContainsKey(host))
            {
                hostOrder.Enqueue(host);
                if (hostOrder.Count > capacity) byHost.Remove(hostOrder.Dequeue());
            }
            byHost[host] = rules;
        }
        return rules;
    }

    /// <summary>The generic stylesheet, without <paramref name="excluded"/>.</summary>
    public string GenericCss(IReadOnlyList<string>? excluded = null)
    {
        var key = (excluded ?? []).Order(StringComparer.Ordinal).ToList();
        var keyText = string.Join('\n', key);
        lock (gate)
        {
            if (key.Count == 0 && generic is not null) return generic;
            if (key.Count > 0 && genericVariants.TryGetValue(keyText, out var variant)) return variant;
        }
        var css = Engine.GenericCosmeticCss(key);
        lock (gate)
        {
            if (key.Count == 0)
            {
                generic = css;
            }
            else
            {
                if (genericVariants.Count >= 8) genericVariants.Clear();
                genericVariants[keyText] = css;
            }
        }
        return css;
    }

    /// <summary>
    /// Everything to hide on a page on <paramref name="hostname"/>: its site-specific rules and,
    /// with <paramref name="includeGeneric"/>, the generic sheet minus the site's exceptions.
    /// </summary>
    public string StylesheetFor(string hostname, bool includeGeneric)
    {
        var rules = RulesFor(hostname);
        if (!includeGeneric) return rules.Css;
        var genericCss = GenericCss(rules.GenericExceptions);
        if (rules.Css.Length == 0) return genericCss;
        if (genericCss.Length == 0) return rules.Css;
        return genericCss + "\n" + rules.Css;
    }
}

/// <summary>The script Mosaic runs in a Chromium frame to apply cosmetic filtering.</summary>
/// <remarks>Port of <c>CosmeticInjection</c> in <c>Sources/MosaicCore/Blocker/CosmeticStyles.swift</c>.</remarks>
public static class CosmeticInjection
{
    private static readonly JsonSerializerOptions Escaping = new()
    {
        // The default encoder escapes '<', '>' and '&' as <…, so "</script>" can never close a script element.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default,
    };

    /// <summary>
    /// JavaScript that adds <paramref name="css"/> to the document once. It uses a constructed
    /// stylesheet (<c>adoptedStyleSheets</c>), which a page's Content Security Policy does not
    /// restrict, and falls back to a <c>&lt;style&gt;</c> element. Null for no CSS.
    /// </summary>
    public static string? Script(string css)
    {
        if (css.Length == 0) return null;
        var literal = JsonSerializer.Serialize(css, Escaping);
        return $$"""
            (() => {
            const key = Symbol.for('mosaic.cosmetic'), doc = document;
            if (doc[key]) return;
            Object.defineProperty(doc, key, { value: true });
            const css = {{literal}};
            try {
            const sheet = new CSSStyleSheet();
            sheet.replaceSync(css);
            doc.adoptedStyleSheets = [...doc.adoptedStyleSheets, sheet];
            } catch (_) {
            const style = doc.createElement('style');
            style.textContent = css;
            (doc.head || doc.documentElement).appendChild(style);
            }
            })();
            """;
    }
}

/// <summary>
/// Requests the blocker stopped in Chromium tabs, for the blocker dashboard. Written from
/// WebView2's request handler, read by the dashboard; safe from any thread.
/// </summary>
/// <remarks>
/// Port of <c>BlockLog</c> in <c>Sources/MosaicCore/Blocker/CosmeticStyles.swift</c>. Entries are
/// kept per page host and looked up from a page's address by host (a page on
/// <c>news.example.com</c> also sees entries recorded under <c>example.com</c>). Only the request is
/// recorded; which filter matched is worked out when the dashboard asks
/// (<see cref="BlockerEngine.Details"/>), off the request path.
/// </remarks>
public sealed class BlockLog(int maxPages = 64, int maxEntries = 500)
{
    /// <summary>One blocked request. <c>DocumentUrl</c> is the page as WebView2 reported it; what to match filters against.</summary>
    public sealed record Entry(string Url, string Type, string DocumentUrl, int Count);

    private readonly Lock gate = new();
    private readonly Dictionary<string, List<Entry>> sites = new(StringComparer.Ordinal);
    private readonly List<string> siteOrder = [];

    private static string Host(string url) =>
        (Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Host.Length > 0 ? parsed.Host : url).ToLowerInvariant();

    /// <summary>Notes that a request from the page at <paramref name="documentUrl"/> was blocked.</summary>
    public void Record(string url, string documentUrl, string type)
    {
        var key = Host(documentUrl);
        lock (gate)
        {
            if (!sites.TryGetValue(key, out var entries))
            {
                siteOrder.Add(key);
                if (siteOrder.Count > maxPages)
                {
                    sites.Remove(siteOrder[0]);
                    siteOrder.RemoveAt(0);
                }
                sites[key] = entries = [];
            }
            var index = entries.FindIndex(entry => entry.Url == url && entry.Type == type);
            if (index >= 0) entries[index] = entries[index] with { Count = entries[index].Count + 1 };
            else if (entries.Count < maxEntries) entries.Add(new Entry(url, type, documentUrl, 1));
        }
    }

    private List<string> KeysFor(string pageUrl)
    {
        var host = Host(pageUrl);
        return [.. siteOrder.Where(key => host == key || host.EndsWith("." + key, StringComparison.Ordinal))];
    }

    /// <summary>Blocked requests of the page at <paramref name="pageUrl"/> (of its site), in the order first seen.</summary>
    public IReadOnlyList<Entry> EntriesFor(string pageUrl)
    {
        lock (gate) return [.. KeysFor(pageUrl).SelectMany(key => sites[key])];
    }

    /// <summary>Forgets a page's site (a page there is loading again).</summary>
    public void Clear(string pageUrl)
    {
        lock (gate)
        {
            foreach (var key in KeysFor(pageUrl))
            {
                sites.Remove(key);
                siteOrder.Remove(key);
            }
        }
    }
}
