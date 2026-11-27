namespace Mosaic.Core.Search;

/// <summary>
/// Turns whatever was typed into the address bar into a URL: either the address itself,
/// or a search on the current search engine.
/// </summary>
/// <remarks>
/// Port of <c>Sources/MosaicCore/Search/AddressResolver.swift</c>; the expected behaviour is
/// pinned down by <c>Tests/MosaicCoreTests/SearchTests.swift</c>. URLs are returned as strings
/// exactly as typed (plus a scheme when one was missing), never re-normalised.
/// </remarks>
public static class AddressResolver
{
    /// <returns>The URL to load, or null for empty input.</returns>
    public static string? Resolve(string input, SearchEngine searchEngine) => throw NotPorted.Yet();

    /// <summary>True when the input would be opened as an address rather than searched.</summary>
    public static bool IsAddress(string input) => throw NotPorted.Yet();
}
