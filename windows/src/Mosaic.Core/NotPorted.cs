namespace Mosaic.Core;

/// <summary>Marks a contract member whose body has not been written yet.</summary>
internal static class NotPorted
{
    public static Exception Yet([System.Runtime.CompilerServices.CallerMemberName] string member = "") =>
        new NotImplementedException($"{member} is part of the contract but has not been implemented yet.");
}
