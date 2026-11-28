using System.Text.Json;
using Mosaic.Core.Browser;

namespace Mosaic.App;

/// <summary>
/// What a launch asks for, as the single-instance channel carries it: made by a second launch
/// (<see cref="Payload"/>), applied by the running Mosaic (<see cref="Apply"/>), and applied to
/// the first launch's own command line (<see cref="ApplyLaunch"/>).
/// </summary>
/// <remarks>
/// For now: the addresses on the command line (<see cref="SingleInstance.AddressesIn"/>), each
/// opened in a new tab. When the system area's <c>ActivationRequest</c> is merged, the three
/// bodies become
/// <c>ActivationRequest.Parse(args, Environment.CommandLine, Environment.CurrentDirectory, File.Exists).ToPayload()</c>,
/// <c>ActivationRequest.FromPayload(payload)?.ApplyTo(model)</c> and
/// <c>ActivationRequest.Parse(…).ApplyTo(model)</c>.
/// </remarks>
internal static class Activation
{
    public static string Payload(string[] args) => JsonSerializer.Serialize(SingleInstance.AddressesIn(args));

    public static void Apply(string payload, BrowserModel model)
    {
        string[]? addresses;
        try
        {
            addresses = JsonSerializer.Deserialize<string[]>(payload);
        }
        catch (JsonException)
        {
            return;
        }
        // Only what a command line could have asked for.
        foreach (var address in SingleInstance.AddressesIn(addresses ?? [])) model.NewTab(address);
    }

    public static void ApplyLaunch(string[] args, BrowserModel model)
    {
        foreach (var address in SingleInstance.AddressesIn(args)) model.NewTab(address);
    }
}
