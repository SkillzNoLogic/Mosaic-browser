using Mosaic.Core.Extensions;
using Mosaic.Core.Search;

namespace Mosaic.Core.Tests;

/// <summary>Checks on the parts of the contract that are complete in the skeleton.</summary>
public class ContractTests
{
    [Fact]
    public void BuiltInSearchEnginesHaveUniqueIds() =>
        Assert.Equal(SearchEngine.BuiltIn.Count, SearchEngine.BuiltIn.Select(engine => engine.Id).Distinct().Count());

    [Fact]
    public void GeckoIdIsDerivedFromChromeId() =>
        Assert.Equal("abc@crx.mosaic", ManifestConverter.GeckoIdFor("abc"));
}
