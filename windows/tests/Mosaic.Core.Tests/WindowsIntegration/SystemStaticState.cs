namespace Mosaic.Core.Tests.WindowsIntegration;

/// <summary>Tests that touch the system area's static hubs run one at a time.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SystemStaticState
{
    public const string Name = "System static state";
}
