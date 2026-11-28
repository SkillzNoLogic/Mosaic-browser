using System.Text.Json.Nodes;

namespace Mosaic.Core.Platform;

/// <summary>
/// "Always open example.com in Chromium": a site → engine rule. Port of
/// <c>Sources/MosaicCore/Platform/EngineRules.swift</c>.
/// </summary>
/// <param name="Id">Stable identity, so an edited rule replaces itself.</param>
/// <param name="Domain">Normalised domain: lowercase, no scheme, path, port or leading "www.".</param>
/// <param name="Engine">The engine by name ("gecko", "chromium"), as macOS stores it.</param>
/// <param name="IncludesSubdomains">Also match subdomains (mail.example.com for example.com).</param>
public sealed record EngineRule(Guid Id, string Domain, string Engine, bool IncludesSubdomains = true)
{
    public EngineRule(string domain, string engine, bool includesSubdomains = true)
        : this(Guid.NewGuid(), domain, engine, includesSubdomains)
    {
    }

    /// <summary>Whether this rule applies to <paramref name="host"/> (already normalised).</summary>
    public bool Matches(string host) =>
        host == Domain || (IncludesSubdomains && host.EndsWith("." + Domain, StringComparison.Ordinal));
}

/// <summary>
/// An ordered set of engine rules with the matching policy.
/// </summary>
/// <remarks>
/// Policy (as on macOS): rules apply to http and https pages only. When several rules match, the
/// most specific domain wins (mail.example.com beats example.com); for the same domain, a rule
/// that does not include subdomains (an exact rule) wins. One rule per domain: adding a rule for a
/// domain that already has one replaces it.
/// <para>
/// JSON: the shape Swift's <c>JSONEncoder</c> gives the macOS <c>EngineRuleSet</c>,
/// <c>{"rules":[{"id":"…","domain":"…","engine":"…","includesSubdomains":true}]}</c>, so rule
/// sets move between the two apps.
/// </para>
/// </remarks>
public sealed class EngineRuleSet : IEquatable<EngineRuleSet>
{
    private readonly List<EngineRule> rules = [];

    public EngineRuleSet(IEnumerable<EngineRule>? rules = null)
    {
        foreach (var rule in rules ?? []) Upsert(rule);
    }

    public IReadOnlyList<EngineRule> Rules => rules;

    /// <summary>The rule deciding <paramref name="address"/>'s engine, if any.</summary>
    public EngineRule? RuleFor(string? address)
    {
        if (address is null || !Uri.TryCreate(address, UriKind.Absolute, out var url)) return null;
        var scheme = url.Scheme.ToLowerInvariant();
        if (scheme is not ("http" or "https") || NormalizedHost(url.IdnHost) is not { } host) return null;
        EngineRule? best = null;
        foreach (var rule in rules)
        {
            if (!rule.Matches(host)) continue;
            if (best is null || rule.Domain.Length > best.Domain.Length
                || (rule.Domain.Length == best.Domain.Length && best.IncludesSubdomains && !rule.IncludesSubdomains))
            {
                best = rule;
            }
        }
        return best;
    }

    public string? EngineFor(string? address) => RuleFor(address)?.Engine;

    /// <summary>Adds <paramref name="rule"/>, replacing any rule for the same domain. False if the domain is invalid.</summary>
    public bool Upsert(EngineRule rule)
    {
        if (NormalizedDomain(rule.Domain) is not { } domain) return false;
        rule = rule with { Domain = domain };
        var index = rules.FindIndex(existing => existing.Id == rule.Id);
        if (index < 0) index = rules.FindIndex(existing => existing.Domain == domain);
        if (index >= 0)
        {
            rules[index] = rule;
            // Editing a rule onto another rule's domain must not leave two for one domain.
            rules.RemoveAll(existing => existing.Domain == domain && existing.Id != rule.Id);
        }
        else
        {
            rules.Add(rule);
        }
        rules.Sort((left, right) => string.CompareOrdinal(left.Domain, right.Domain));
        return true;
    }

    public void Remove(Guid id) => rules.RemoveAll(rule => rule.Id == id);

    public EngineRuleSet Copy() => new(rules);

    // ---- Normalisation ----

    /// <summary>
    /// Turns what someone typed ("https://www.Example.com/path", "example.com:8080",
    /// "*.example.com") into the domain a rule stores, or null if it is not a domain.
    /// </summary>
    public static string? NormalizedDomain(string? input)
    {
        if (input is null) return null;
        var text = input.Trim().ToLowerInvariant();
        if (text.StartsWith("*.", StringComparison.Ordinal)) text = text[2..];
        if (text.Contains("://", StringComparison.Ordinal) && Uri.TryCreate(text, UriKind.Absolute, out var url) && url.Host.Length > 0)
        {
            text = url.IdnHost;
        }
        else
        {
            // Strip a path, query or port typed without a scheme.
            if (text.IndexOfAny(['/', '?', '#']) is >= 0 and var cut) text = text[..cut];
            if (text.LastIndexOf(':') is >= 0 and var colon && !text.Contains(']')) text = text[..colon];
        }
        return NormalizedHost(text);
    }

    /// <summary>Lowercase, without a trailing dot or leading "www."; null if it is not a hostname.</summary>
    public static string? NormalizedHost(string? host)
    {
        if (host is null) return null;
        host = host.ToLowerInvariant();
        if (host.EndsWith('.')) host = host[..^1];
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
        if (host.Length == 0 || host.Length > 253) return null;
        foreach (var label in host.Split('.'))
        {
            if (label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-') return null;
            foreach (var character in label)
            {
                if (!(char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '-')) return null;
            }
        }
        return host;
    }

    // ---- JSON (same shape as macOS) ----

    public JsonObject ToJson()
    {
        var array = new JsonArray();
        foreach (var rule in rules)
        {
            array.Add(new JsonObject
            {
                // Swift's JSONEncoder writes UUIDs in upper case.
                ["id"] = rule.Id.ToString("D").ToUpperInvariant(),
                ["domain"] = rule.Domain,
                ["engine"] = rule.Engine,
                ["includesSubdomains"] = rule.IncludesSubdomains,
            });
        }
        return new JsonObject { ["rules"] = array };
    }

    /// <summary>Reads the macOS-shaped JSON; entries that cannot be read are skipped.</summary>
    public static EngineRuleSet FromJson(JsonNode? node)
    {
        var set = new EngineRuleSet();
        if (node is not JsonObject root || root["rules"] is not JsonArray array) return set;
        foreach (var item in array.OfType<JsonObject>())
        {
            if (Text(item["domain"]) is not { } domain || Text(item["engine"]) is not { } engine) continue;
            var id = Guid.TryParse(Text(item["id"]), out var parsed) ? parsed : Guid.NewGuid();
            var subdomains = item["includesSubdomains"] is not JsonValue flag || !flag.TryGetValue(out bool value) || value;
            set.Upsert(new EngineRule(id, domain, engine, subdomains));
        }
        return set;
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    public bool Equals(EngineRuleSet? other) => other is not null && rules.SequenceEqual(other.rules);
    public override bool Equals(object? obj) => obj is EngineRuleSet other && Equals(other);
    public override int GetHashCode() => rules.Count;
}
