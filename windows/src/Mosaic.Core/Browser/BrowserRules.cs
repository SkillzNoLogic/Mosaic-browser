using Mosaic.Core.Extensions;
using Mosaic.Core.Search;

namespace Mosaic.Core.Browser;

/// <summary>
/// The text rules the browser model relies on but does not own: what counts as an address, how a
/// Chrome Web Store link maps to an extension, which search engine comes next.
/// </summary>
/// <remarks>
/// The defaults are the real implementations in <see cref="AddressResolver"/>,
/// <see cref="ChromeWebStore"/> and <see cref="SearchEngine"/>. They are held as delegates so the
/// model's own behaviour can be tested in isolation from them.
/// </remarks>
public sealed class BrowserRules
{
    public static BrowserRules Default { get; } = new();

    /// <summary>The URL to load for what was typed, or null for empty input.</summary>
    public Func<string, SearchEngine, string?> ResolveAddress { get; init; } = AddressResolver.Resolve;

    /// <summary>True when the input would be opened as an address rather than searched.</summary>
    public Func<string, bool> IsAddress { get; init; } = AddressResolver.IsAddress;

    /// <summary>The extension ID in a bare ID or a Chrome Web Store listing URL; null otherwise.</summary>
    public Func<string, string?> WebStoreExtensionId { get; init; } = ChromeWebStore.ExtensionIdFrom;

    /// <summary>Where to download the extension's package.</summary>
    public Func<string, Uri> WebStoreDownloadUrl { get; init; } = id => ChromeWebStore.DownloadUrl(id);

    public Func<SearchEngine, SearchEngine> NextSearchEngine { get; init; } = engine => engine.Next;
}
