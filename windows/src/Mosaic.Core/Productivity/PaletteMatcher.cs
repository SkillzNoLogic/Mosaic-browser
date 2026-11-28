using System.Globalization;
using System.Text;

namespace Mosaic.Core.Productivity;

/// <summary>
/// Matching and ranking for the command palette: how well a typed query fits an item's title
/// and keywords. Port of <c>Sources/MosaicCore/Productivity/PaletteMatcher.swift</c>.
/// </summary>
/// <remarks>
/// Every whitespace-separated word of the query has to match somewhere (title or keywords);
/// each word is scored by the best kind of match it found, from an exact title to a loose
/// in-order ("fuzzy") match, and the item's score is the average. Ties go to shorter titles.
/// </remarks>
public static class PaletteMatcher
{
    /// <summary>Kinds of match, best first, with the scores they contribute.</summary>
    internal static class Grade
    {
        public const double Exact = 100;
        /// <summary>The text starts with the token as a whole word ("tab" in "Tab Groups").</summary>
        public const double LeadingWord = 92;
        /// <summary>A whole word of the text ("tab" in "New Tab").</summary>
        public const double Word = 86;
        /// <summary>The start of the first word ("tab" in "Tabs &amp; Workspaces").</summary>
        public const double Prefix = 84;
        public const double WordPrefix = 76;
        public const double Initials = 66;
        public const double Substring = 52;
        public const double Fuzzy = 34;
    }

    /// <summary>Keywords count for a little less than the title, so "Close Tab" beats an item that only lists "close".</summary>
    internal const double KeywordWeight = 0.8;

    /// <summary>Lower-cased, diacritic- and width-folded, so "Ecosia" matches "Écosia".</summary>
    public static string Normalize(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormKD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) builder.Append(c);
        }
        return builder.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }

    /// <summary>Score of <paramref name="query"/> for an item, or null when it does not match. An empty query matches with 0.</summary>
    public static double? Score(string query, string title, IEnumerable<string>? keywords = null)
    {
        var tokens = Normalize(query).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return 0;
        var normalizedTitle = Normalize(title);
        var normalizedKeywords = (keywords ?? []).Select(Normalize).ToList();

        var total = 0.0;
        foreach (var token in tokens)
        {
            var best = GradeOf(token, normalizedTitle);
            foreach (var keyword in normalizedKeywords)
            {
                if (GradeOf(token, keyword) is { } value && value * KeywordWeight > (best ?? -1)) best = value * KeywordWeight;
            }
            if (best is not { } found) return null;
            total += found;
        }
        var result = total / tokens.Length;
        // The whole query as the start of the title is the strongest signal of intent.
        var phrase = string.Join(' ', tokens);
        if (tokens.Length > 1 && normalizedTitle.StartsWith(phrase, StringComparison.Ordinal)) result += 8;
        // Prefer shorter titles among equals ("Tab" over "Tab Groups Settings").
        result -= normalizedTitle.Length * 0.05;
        return result;
    }

    /// <summary>Score of one query word against one normalised text.</summary>
    internal static double? GradeOf(string token, string text)
    {
        if (text.Length == 0) return null;
        if (text == token) return Grade.Exact;
        var words = Words(text);
        if (words.Count > 0 && words[0] == token) return Grade.LeadingWord;
        if (words.Contains(token)) return Grade.Word;
        if (text.StartsWith(token, StringComparison.Ordinal)) return Grade.Prefix;
        if (words.Skip(1).Any(word => word.StartsWith(token, StringComparison.Ordinal))) return Grade.WordPrefix;
        if (token.Length >= 2 && InitialsMatch(token, words)) return Grade.Initials;
        if (text.Contains(token, StringComparison.Ordinal)) return Grade.Substring;
        if (SubsequenceTightness(token, text) is { } tightness)
        {
            // 0…1, 1 being every character adjacent; a loose match is worth less.
            return Grade.Fuzzy * (0.5 + 0.5 * tightness);
        }
        return null;
    }

    internal static List<string> Words(string text)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                current.Append(c);
            }
            else if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }
        if (current.Length > 0) words.Add(current.ToString());
        return words;
    }

    /// <summary>"nt" → "New Tab": the token spells the leading letters of consecutive words, starting at any word.</summary>
    internal static bool InitialsMatch(string token, List<string> words)
    {
        if (words.Count < token.Length) return false;
        var initials = new string(words.Select(word => word[0]).ToArray());
        return initials.Contains(token, StringComparison.Ordinal);
    }

    /// <summary>If every character of <paramref name="token"/> appears in <paramref name="text"/> in order, how tightly (1 = adjacent).</summary>
    internal static double? SubsequenceTightness(string token, string text)
    {
        if (token.Length < 2 || token.Length > text.Length) return null;
        var index = 0;
        int? first = null;
        var last = 0;
        for (var position = 0; position < text.Length && index < token.Length; position++)
        {
            if (text[position] != token[index]) continue;
            first ??= position;
            last = position;
            index++;
        }
        if (index != token.Length || first is not { } start) return null;
        return (double)token.Length / (last - start + 1);
    }
}

/// <summary>
/// One thing the palette could show, reduced to what ranking needs. <c>Boost</c> is added to the
/// match score: it lets open tabs outrank history entries of the same page.
/// </summary>
public sealed record PaletteCandidate(string Id, string Title, IReadOnlyList<string> Keywords, double Boost = 0)
{
    public PaletteCandidate(string id, string title) : this(id, title, []) { }
}

public static class PaletteRanker
{
    /// <summary>Points for having been chosen recently; the most recent gets the full amount.</summary>
    internal const double RecencyBonus = 14;

    /// <summary>
    /// Matching candidates, best first. <paramref name="recent"/> lists ids chosen recently, newest
    /// first. With an empty query every candidate is returned: recent ones first, then in the
    /// order given (boosts only matter when something is typed).
    /// </summary>
    public static IReadOnlyList<PaletteCandidate> Rank(
        IReadOnlyList<PaletteCandidate> candidates, string query, IReadOnlyList<string>? recent = null, int limit = int.MaxValue)
    {
        recent ??= [];
        var recency = new Dictionary<string, double>();
        for (var index = 0; index < recent.Count; index++)
        {
            recency.TryAdd(recent[index], RecencyBonus * (1 - (double)index / Math.Max(recent.Count, 1)));
        }
        var empty = string.IsNullOrWhiteSpace(query);
        var scored = new List<(PaletteCandidate Candidate, double Score, int Order)>();
        for (var order = 0; order < candidates.Count; order++)
        {
            var candidate = candidates[order];
            if (PaletteMatcher.Score(query, candidate.Title, candidate.Keywords) is not { } match) continue;
            var bonus = recency.GetValueOrDefault(candidate.Id);
            scored.Add((candidate, empty ? bonus : match + candidate.Boost + bonus, order));
        }
        scored.Sort((left, right) => left.Score != right.Score ? right.Score.CompareTo(left.Score) : left.Order.CompareTo(right.Order));
        return scored.Take(limit).Select(item => item.Candidate).ToList();
    }
}

/// <summary>Ids of recently chosen palette items, newest first, capped.</summary>
public sealed class PaletteRecents
{
    private readonly List<string> ids;

    public PaletteRecents(IEnumerable<string>? ids = null, int capacity = 20)
    {
        Capacity = capacity;
        this.ids = (ids ?? []).Take(capacity).ToList();
    }

    public int Capacity { get; }

    public IReadOnlyList<string> Ids => ids;

    public void Note(string id)
    {
        ids.RemoveAll(existing => existing == id);
        ids.Insert(0, id);
        if (ids.Count > Capacity) ids.RemoveRange(Capacity, ids.Count - Capacity);
    }
}
