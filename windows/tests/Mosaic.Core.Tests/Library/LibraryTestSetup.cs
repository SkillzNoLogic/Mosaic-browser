using System.Runtime.CompilerServices;
using Mosaic.Core.Library;

namespace Mosaic.Core.Tests.Library;

internal static class LibraryTestSetup
{
    /// <summary>
    /// Every browser model in the tests gets a library (the model's tab hooks create it); none of
    /// them may open the real profile's <c>Library.sqlite</c>, so by default they keep it in memory.
    /// </summary>
    [ModuleInitializer]
    internal static void KeepLibrariesInMemory() => LibraryOptions.DefaultDatabasePath = () => null;
}

/// <summary>A throwaway folder under the test output, deleted afterwards.</summary>
internal sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(AppContext.BaseDirectory, "test-data", "library-" + Guid.NewGuid().ToString("N"));

    public TempFolder() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}
