namespace Mosaic.Core.WindowsIntegration;

/// <summary>A task on Mosaic's taskbar jump list: starts <c>Mosaic.exe</c> with <see cref="Arguments"/>.</summary>
/// <param name="IconIndex">Icon resource index in Mosaic.exe (0, the app icon, is the only one it has).</param>
public sealed record JumpListTask(string Title, string Arguments, string Description, int IconIndex = 0);

/// <summary>
/// The jump list's tasks. Each launches Mosaic, which the single-instance channel forwards to the
/// running window (see <see cref="ActivationRequest"/>), so they work without package identity.
/// </summary>
public static class JumpListTasks
{
    public static readonly IReadOnlyList<JumpListTask> Tasks =
    [
        new("New tab", ActivationRequest.NewTabSwitch, "Open a new tab in Mosaic"),
        new("New private tab", ActivationRequest.NewPrivateTabSwitch, "Open a private tab: nothing is saved"),
    ];

    /// <summary>
    /// A fingerprint of the tasks and the executable, stored after the list is written so later
    /// launches skip rewriting an identical list (keeps the shell work off every start).
    /// </summary>
    public static string Fingerprint(string executablePath) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            executablePath.ToUpperInvariant() + "|" + string.Join("|", Tasks.Select(t => $"{t.Title}>{t.Arguments}>{t.IconIndex}")))))[..16];
}

/// <summary>Splits a Windows command line into arguments, as <c>CommandLineToArgvW</c> does for arguments after the program name.</summary>
public static class CommandLineSplitter
{
    public static IReadOnlyList<string> Split(string commandLine)
    {
        var args = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        var hasArg = false;
        for (var i = 0; i < commandLine.Length; i++)
        {
            var c = commandLine[i];
            if (c == '\\')
            {
                var slashes = 0;
                while (i < commandLine.Length && commandLine[i] == '\\') { slashes++; i++; }
                if (i < commandLine.Length && commandLine[i] == '"')
                {
                    current.Append('\\', slashes / 2);
                    if (slashes % 2 == 1) current.Append('"');
                    else { inQuotes = !inQuotes; }
                }
                else
                {
                    current.Append('\\', slashes);
                    i--;
                }
                hasArg = true;
                continue;
            }
            if (c == '"')
            {
                if (inQuotes && i + 1 < commandLine.Length && commandLine[i + 1] == '"') { current.Append('"'); i++; }
                else inQuotes = !inQuotes;
                hasArg = true;
                continue;
            }
            if (!inQuotes && (c == ' ' || c == '\t'))
            {
                if (hasArg) { args.Add(current.ToString()); current.Clear(); hasArg = false; }
                continue;
            }
            current.Append(c);
            hasArg = true;
        }
        if (hasArg) args.Add(current.ToString());
        return args;
    }
}
