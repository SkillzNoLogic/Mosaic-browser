namespace Mosaic.Core.Library;

/// <summary>
/// "Frecency": how frequently and how recently an address was visited, the score Firefox's
/// address bar ranks history by. Each of the latest visits earns points by age (typed visits
/// count double); the average is scaled by the total number of visits.
/// </summary>
/// <remarks>Port of <c>Sources/MosaicCore/Library/Frecency.swift</c>.</remarks>
public static class Frecency
{
    /// <summary>How many of the latest visits are sampled.</summary>
    public const int SampleSize = 10;

    public static double Points(TimeSpan age, bool typed)
    {
        var days = age.TotalSeconds / 86_400;
        double points = days switch
        {
            < 4 => 100,
            < 14 => 70,
            < 31 => 50,
            < 90 => 30,
            _ => 10,
        };
        return typed ? points * 2 : points;
    }

    /// <summary><paramref name="visits"/> are the latest visits, any order; at most <see cref="SampleSize"/> are used.</summary>
    public static double Score(int visitCount, IEnumerable<(DateTimeOffset Time, bool Typed)> visits, DateTimeOffset now)
    {
        if (visitCount <= 0) return 0;
        var sample = visits.OrderByDescending(visit => visit.Time).Take(SampleSize).ToList();
        if (sample.Count == 0) return 0;
        var total = sample.Sum(visit => Points(now - visit.Time < TimeSpan.Zero ? TimeSpan.Zero : now - visit.Time, visit.Typed));
        return Math.Round(visitCount * total / sample.Count, MidpointRounding.AwayFromZero);
    }
}

/// <summary>Where an address-bar candidate came from.</summary>
public enum SuggestionSource { History, Bookmark, OpenTab }

/// <summary>Ranks address-bar candidates (history, bookmarks, open tabs) against what was typed.</summary>
/// <remarks>Port of <c>SuggestionRanker</c> in <c>Sources/MosaicCore/Library/Frecency.swift</c>.</remarks>
public static class SuggestionRanker
{
    public sealed record Candidate(
        string Url, string Title, double Frecency, SuggestionSource Source, bool IsBookmarked = false, Guid? TabId = null);

    /// <param name="Candidate">The candidate, merged with others for the same address.</param>
    /// <param name="Score">Higher is better.</param>
    /// <param name="MatchesHostPrefix">
    /// The first word typed starts the site's host ("git" for github.com), so the address bar
    /// could complete it inline.
    /// </param>
    public sealed record Ranked(Candidate Candidate, double Score, bool MatchesHostPrefix);

    /// <summary>Lower-cased terms, split on whitespace.</summary>
    public static IReadOnlyList<string> Terms(string input) =>
        input.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>How well an address matches <paramref name="terms"/>, or null when some term is missing.</summary>
    public static (double Quality, bool HostPrefix)? Match(IReadOnlyList<string> terms, string url, string title)
    {
        if (terms.Count == 0) return null;
        var first = terms[0];
        var lowerUrl = url.ToLowerInvariant();
        var lowerTitle = title.ToLowerInvariant();
        var display = LibraryUrl.Display(lowerUrl);
        var host = LibraryUrl.Host(lowerUrl);
        foreach (var term in terms)
        {
            if (!lowerUrl.Contains(term, StringComparison.Ordinal) && !lowerTitle.Contains(term, StringComparison.Ordinal)
                && !display.Contains(term, StringComparison.Ordinal))
            {
                return null;
            }
        }
        var quality = 1.0;
        var hostPrefix = false;
        if (host.StartsWith(first, StringComparison.Ordinal) || display.StartsWith(first, StringComparison.Ordinal))
        {
            quality = 4;
            hostPrefix = host.StartsWith(first, StringComparison.Ordinal);
        }
        else if (StartsWord(first, lowerTitle) || HostLabelPrefix(first, host))
        {
            quality = 2;
        }
        else if (StartsWord(first, display))
        {
            quality = 1.5;
        }
        // More of the typed words found at word starts in the title: a little better.
        var wordHits = terms.Skip(1).Count(term => StartsWord(term, lowerTitle) || StartsWord(term, display));
        quality += 0.25 * wordHits;
        return (quality, hostPrefix);
    }

    /// <summary>
    /// Best first. Candidates with the same address are merged (an open tab, a bookmark and a
    /// history entry for one page become one row, preferring the open tab).
    /// </summary>
    public static IReadOnlyList<Ranked> Rank(string input, IEnumerable<Candidate> candidates, int limit)
    {
        var terms = Terms(input);
        if (terms.Count == 0) return [];
        var best = new Dictionary<string, Ranked>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            if (Match(terms, candidate.Url, candidate.Title) is not { } match) continue;
            var (quality, hostPrefix) = match;
            var baseScore = candidate.Frecency + 1;
            if (candidate.IsBookmarked || candidate.Source == SuggestionSource.Bookmark) baseScore += 140;
            if (candidate.Source == SuggestionSource.OpenTab) baseScore += 60;
            var ranked = new Ranked(candidate, baseScore * quality, hostPrefix);
            var key = LibraryUrl.Normalized(candidate.Url);
            if (best.TryGetValue(key, out var existing))
            {
                var merged = existing.Score >= ranked.Score ? existing : ranked;
                var mergedCandidate = merged.Candidate;
                // Keep the strongest kind of row: an open tab beats a bookmark beats history.
                if (existing.Candidate.Source == SuggestionSource.OpenTab || ranked.Candidate.Source == SuggestionSource.OpenTab)
                {
                    mergedCandidate = existing.Candidate.Source == SuggestionSource.OpenTab ? existing.Candidate : ranked.Candidate;
                }
                var bookmarked = existing.Candidate.IsBookmarked || ranked.Candidate.IsBookmarked
                    || existing.Candidate.Source == SuggestionSource.Bookmark || ranked.Candidate.Source == SuggestionSource.Bookmark;
                var title = mergedCandidate.Title.Length > 0 ? mergedCandidate.Title
                    : existing.Candidate.Title.Length == 0 ? ranked.Candidate.Title : existing.Candidate.Title;
                best[key] = merged with
                {
                    Score = Math.Max(existing.Score, ranked.Score),
                    Candidate = mergedCandidate with { IsBookmarked = bookmarked, Title = title },
                };
            }
            else
            {
                best[key] = ranked;
            }
        }
        return [.. best.Values
            .OrderByDescending(ranked => ranked.Score)
            .ThenBy(ranked => ranked.Candidate.Url, StringComparer.Ordinal)
            .Take(limit)];
    }

    private static bool StartsWord(string term, string text)
    {
        var start = 0;
        while (start <= text.Length)
        {
            var found = text.IndexOf(term, start, StringComparison.Ordinal);
            if (found < 0) return false;
            if (found == 0) return true;
            var before = text[found - 1];
            if (!char.IsLetterOrDigit(before)) return true;
            start = found + 1;
        }
        return false;
    }

    private static bool HostLabelPrefix(string term, string host) =>
        host.Split('.').Any(label => label.StartsWith(term, StringComparison.Ordinal));
}
