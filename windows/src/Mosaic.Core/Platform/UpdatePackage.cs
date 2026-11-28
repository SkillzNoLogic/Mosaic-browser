using System.Reflection;
using System.Text;
using Org.BouncyCastle.Math.EC.Rfc8032;

namespace Mosaic.Core.Platform;

/// <summary>
/// The parts of Mosaic's self-update that need no UI: which release file to download, how its
/// signature is checked, whether this copy of Mosaic can replace itself, and the helper that swaps
/// the install folder after Mosaic has quit. Windows counterpart of
/// <c>Sources/MosaicCore/Platform/UpdatePackage.swift</c>.
/// </summary>
/// <remarks>
/// <para>
/// Trust: every release zip is signed with Mosaic's own Ed25519 release key
/// (<c>Scripts/sign-update.swift</c>), the same key the macOS app checks, and Mosaic installs only
/// a zip whose signature verifies against the public key compiled in here. A compromised GitHub
/// account or download server cannot produce that signature. The private key exists only in the
/// release machine's Keychain; tests sign with throwaway keys of their own.
/// </para>
/// <para>
/// Ed25519 is verified with BouncyCastle's RFC 8032 implementation
/// (<c>Org.BouncyCastle.Math.EC.Rfc8032.Ed25519</c>, package BouncyCastle.Cryptography): .NET has no
/// Ed25519 of its own, Windows CNG offers Curve25519 only for key agreement, and BouncyCastle is
/// fully managed (the same code on x64, ARM64 and the test hosts, no native library to ship), MIT
/// licensed, widely deployed and checked against RFC 8032's test vectors. It rejects non-canonical
/// signatures (S ≥ L) and invalid public keys, like CryptoKit does on macOS.
/// </para>
/// </remarks>
public static class UpdatePackage
{
    /// <summary>Raw 32-byte Ed25519 public key, base64: the key in <c>UpdatePackage.swift</c>.</summary>
    public const string SigningPublicKey = "Z61pZlb7FV58rHTravn/GMwYPCiFZA5/QSvpFehHi/c=";

    /// <summary>The executable the helper starts after an update, and that a release must contain.</summary>
    public const string ExecutableName = "Mosaic.exe";

    /// <summary>The managed assembly whose version a downloaded release must carry.</summary>
    public const string AssemblyFileName = "Mosaic.dll";

    /// <summary>Release asset names: <c>Mosaic-0.3.1-windows-x64.zip</c> / <c>Mosaic-0.3.1-windows-arm64.zip</c>.</summary>
    public static string ZipName(SoftwareVersion version, MachineArchitecture architecture) =>
        $"Mosaic-{version.NumberString}-windows-{architecture.Id()}.zip";

    public static string SignatureName(SoftwareVersion version, MachineArchitecture architecture) =>
        ZipName(version, architecture) + ".sig";

    // ---- Signature ----

    /// <summary>
    /// True if <paramref name="signature"/> (base64, as in the <c>.sig</c> file; surrounding
    /// whitespace allowed) is a valid Ed25519 signature of <paramref name="data"/> by
    /// <paramref name="publicKey"/> (base64 raw key).
    /// </summary>
    public static bool Verify(ReadOnlySpan<byte> data, string signature, string publicKey = SigningPublicKey)
    {
        byte[] key, signatureBytes;
        try
        {
            key = Convert.FromBase64String(publicKey.Trim());
            signatureBytes = Convert.FromBase64String(signature.Trim());
        }
        catch (FormatException)
        {
            return false;
        }
        if (key.Length != Ed25519.PublicKeySize || signatureBytes.Length != Ed25519.SignatureSize) return false;
        try
        {
            return Ed25519.Verify(signatureBytes, key, data);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Verifies a file. The whole file is read into memory once (Ed25519 hashes the message as
    /// part of verification), so call this off the UI thread.
    /// </summary>
    public static bool VerifyFile(string path, string signature, string publicKey = SigningPublicKey) =>
        Verify(File.ReadAllBytes(path), signature, publicKey);

    // ---- Where Mosaic is running from ----

    /// <summary>
    /// Why the install folder at <paramref name="installDirectory"/> cannot be replaced in place, or
    /// null if it can.
    /// </summary>
    /// <param name="installDirectory">The folder holding <c>Mosaic.exe</c>.</param>
    /// <param name="temporaryDirectory">The user's temporary folder (Explorer runs an app opened from inside a zip from there).</param>
    /// <param name="hasExecutable">Whether <c>Mosaic.exe</c> is in the folder (false for a development run).</param>
    /// <param name="folderWritable">Whether this user can rename the folder and create one beside it.</param>
    public static UpdateLocationProblem? LocationProblem(
        string installDirectory, string temporaryDirectory, bool hasExecutable, bool folderWritable)
    {
        var install = installDirectory.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var temporary = temporaryDirectory.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        if (temporary.Length > 1 && install.StartsWith(temporary, StringComparison.OrdinalIgnoreCase)) return new UpdateLocationProblem.Temporary();
        if (!hasExecutable) return new UpdateLocationProblem.NotAPackage();
        if (!folderWritable) return new UpdateLocationProblem.FolderNotWritable(installDirectory.TrimEnd('\\', '/'));
        return null;
    }

    // ---- The installer helper ----

    /// <summary>
    /// Windows PowerShell (5.1, part of every Windows 10/11) commands, run detached as Mosaic quits.
    /// They wait for Mosaic to exit and for its child processes (WebView2's, the Gecko engine's,
    /// anything started from inside the install folder) to follow, stopping stragglers after ten
    /// seconds; move the install folder aside, move the verified new one into its place (restoring
    /// the old one if that fails), delete the old one, and, for "Restart to Update", start the new
    /// Mosaic with the arguments the old one had. The process list is taken while Mosaic still runs,
    /// and each process is held by handle, so a process ID reused later is never waited for or stopped.
    /// </summary>
    /// <remarks>
    /// The commands travel in <c>-EncodedCommand</c> rather than as a script file: execution policy
    /// governs script files only, so a machine set to run signed scripts only still installs updates.
    /// </remarks>
    public const string HelperFunction = """
        function Invoke-MosaicUpdate {
            param([int]$ProcessId, [string]$Target, [string]$Staged, [string]$Relaunch,
                  [string]$Executable, [string]$Arguments, [string]$WaitFor, [string]$Log)
            $ErrorActionPreference = 'Stop'
            function Say([string]$text) {
                $line = '{0} {1}' -f (Get-Date).ToString('yyyy-MM-dd HH:mm:ss.fff'), $text
                if ($Log) { try { Add-Content -LiteralPath $Log -Value $line -Encoding UTF8 } catch { } }
                [Console]::Out.WriteLine($line)
            }
            function Move-WithRetry([string]$from, [string]$to) {
                $crossVolume = [System.IO.Path]::GetPathRoot($from) -ne [System.IO.Path]::GetPathRoot($to)
                for ($attempt = 1; $attempt -le 40; $attempt++) {
                    try {
                        if ($crossVolume) {
                            Copy-Item -LiteralPath $from -Destination $to -Recurse -Force
                            Remove-Item -LiteralPath $from -Recurse -Force -ErrorAction SilentlyContinue
                        } else {
                            [System.IO.Directory]::Move($from, $to)
                        }
                        return $true
                    } catch {
                        if ($attempt -eq 40) { Say ("could not move {0} to {1}: {2}" -f $from, $to, $_.Exception.Message); return $false }
                        Start-Sleep -Milliseconds 250
                    }
                }
                return $false
            }
            try {
                $Target = $Target.TrimEnd('\')
                $prefix = $Target + '\'
                $mosaic = $null
                try { $mosaic = Get-Process -Id $ProcessId -ErrorAction Stop; [void]$mosaic.Handle } catch { $mosaic = $null }

                # Taken while Mosaic still runs: its descendants (a child must be younger than its
                # parent, so a stale parent ID never adopts an unrelated process), the processes it
                # named, and anything running from inside the install folder.
                $snapshot = @()
                try { $snapshot = @(Get-CimInstance -ClassName Win32_Process -ErrorAction Stop) } catch { Say ("process list unavailable: {0}" -f $_.Exception.Message) }
                $byId = @{}
                $children = @{}
                foreach ($p in $snapshot) {
                    $byId[[int]$p.ProcessId] = $p
                    $parent = [int]$p.ParentProcessId
                    if (-not $children.ContainsKey($parent)) { $children[$parent] = New-Object System.Collections.ArrayList }
                    [void]$children[$parent].Add([int]$p.ProcessId)
                }
                $queue = New-Object System.Collections.Queue
                $queue.Enqueue($ProcessId)
                if ($WaitFor) {
                    foreach ($text in $WaitFor.Split(',')) { $n = 0; if ([int]::TryParse($text, [ref]$n) -and $n -gt 0) { $queue.Enqueue($n) } }
                }
                $tracked = @{}
                while ($queue.Count -gt 0) {
                    $id = [int]$queue.Dequeue()
                    if ($tracked.ContainsKey($id)) { continue }
                    $tracked[$id] = $true
                    if (-not $children.ContainsKey($id) -or -not $byId.ContainsKey($id)) { continue }
                    $born = $byId[$id].CreationDate
                    foreach ($child in $children[$id]) {
                        $info = $byId[$child]
                        if ($child -ne $id -and $info -and $info.CreationDate -and $born -and $info.CreationDate -ge $born) { $queue.Enqueue($child) }
                    }
                }
                foreach ($p in $snapshot) {
                    $path = [string]$p.ExecutablePath
                    if ($path -and $path.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) { $tracked[[int]$p.ProcessId] = $true }
                }
                $waiting = New-Object System.Collections.ArrayList
                foreach ($id in @($tracked.Keys)) {
                    if ($id -eq $ProcessId -or $id -eq $PID -or $id -le 4) { continue }
                    try { $proc = Get-Process -Id $id -ErrorAction Stop; [void]$proc.Handle; [void]$waiting.Add($proc) } catch { }
                }
                Say ("waiting for Mosaic (process {0}) and {1} other process(es)" -f $ProcessId, $waiting.Count)

                if ($mosaic -and -not $mosaic.WaitForExit(60000)) { Say 'Mosaic did not quit; update not installed'; exit 1 }
                $deadline = (Get-Date).AddSeconds(10)
                while ((Get-Date) -lt $deadline) {
                    if (@($waiting | Where-Object { -not $_.HasExited }).Count -eq 0) { break }
                    Start-Sleep -Milliseconds 100
                }
                $alive = @($waiting | Where-Object { -not $_.HasExited })
                if ($alive.Count -gt 0) {
                    Say ("stopping process(es) that outlived Mosaic: {0}" -f (($alive | ForEach-Object { '{0} {1}' -f $_.ProcessName, $_.Id }) -join ', '))
                    foreach ($proc in $alive) { try { $proc.Kill() } catch { } }
                    foreach ($proc in $alive) { try { [void]$proc.WaitForExit(3000) } catch { } }
                }

                $backup = '{0}.previous-{1}' -f $Target, $PID
                if (Test-Path -LiteralPath $Target) {
                    if (-not (Move-WithRetry $Target $backup)) { Say 'could not move the old version aside; update not installed'; exit 1 }
                }
                if (-not (Move-WithRetry $Staged $Target)) {
                    if (Test-Path -LiteralPath $Target) { Remove-Item -LiteralPath $Target -Recurse -Force -ErrorAction SilentlyContinue }
                    if (Test-Path -LiteralPath $backup) { [void](Move-WithRetry $backup $Target) }
                    Say 'could not install the new version; the old version was restored'
                    exit 1
                }
                Remove-Item -LiteralPath $backup -Recurse -Force -ErrorAction SilentlyContinue
                Say ("installed {0}" -f $Target)
                if ($Relaunch -eq 'relaunch') {
                    $start = New-Object System.Diagnostics.ProcessStartInfo
                    $start.FileName = Join-Path $Target $Executable
                    $start.Arguments = $Arguments
                    $start.WorkingDirectory = $Target
                    $start.UseShellExecute = $false
                    try { [void][System.Diagnostics.Process]::Start($start); Say 'relaunched' } catch { Say ("could not relaunch: {0}" -f $_.Exception.Message) }
                }
                exit 0
            } catch {
                Say ("update failed: {0}" -f $_.Exception.Message)
                exit 1
            }
        }
        """;

    /// <summary>What the helper needs to know.</summary>
    public sealed record HelperRequest(
        int ProcessId, string Target, string Staged, bool Relaunch, IReadOnlyList<string> Arguments,
        IReadOnlyList<int> WaitFor, string Log, string Executable = ExecutableName);

    /// <summary>The full PowerShell text for <paramref name="request"/>: the function and its call.</summary>
    public static string HelperScript(HelperRequest request) =>
        HelperFunction + "\n" + string.Join(' ',
            "Invoke-MosaicUpdate",
            "-ProcessId", request.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-Target", PowerShellLiteral(request.Target.TrimEnd('\\', '/')),
            "-Staged", PowerShellLiteral(request.Staged.TrimEnd('\\', '/')),
            "-Relaunch", request.Relaunch ? "'relaunch'" : "'stay'",
            "-Executable", PowerShellLiteral(request.Executable),
            "-Arguments", PowerShellLiteral(WindowsCommandLine.Join(request.Arguments)),
            "-WaitFor", PowerShellLiteral(string.Join(',', request.WaitFor)),
            "-Log", PowerShellLiteral(request.Log)) + "\n";

    /// <summary>
    /// Arguments for <c>powershell.exe</c> that run <paramref name="request"/>: no profile, no
    /// prompts, no window, the commands base64-encoded (UTF-16LE) as <c>-EncodedCommand</c> requires.
    /// </summary>
    public static IReadOnlyList<string> HelperArguments(HelperRequest request) =>
    [
        "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-ExecutionPolicy", "Bypass",
        "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(HelperScript(request))),
    ];

    /// <summary>Windows PowerShell's own executable (not PowerShell 7, which may be absent).</summary>
    public static string PowerShellPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>
    /// A single-quoted PowerShell string. PowerShell also treats the typographic single quotes
    /// (U+2018–U+201B) as quote characters, so those are doubled too.
    /// </summary>
    public static string PowerShellLiteral(string text)
    {
        var builder = new StringBuilder("'");
        foreach (var character in text)
        {
            if (character is '\'' or '‘' or '’' or '‚' or '‛') builder.Append(character);
            builder.Append(character);
        }
        return builder.Append('\'').ToString();
    }
}

/// <summary>Why this copy of Mosaic cannot replace itself.</summary>
public abstract record UpdateLocationProblem
{
    public abstract string Message { get; }

    /// <summary>Opened from inside the zip: Explorer runs it from a temporary copy.</summary>
    public sealed record Temporary : UpdateLocationProblem
    {
        public override string Message =>
            "Windows is running Mosaic from a temporary folder (it was opened from inside the zip). Extract the zip to a folder of its own and start Mosaic from there to update automatically.";
    }

    /// <summary>A development run (no <c>Mosaic.exe</c> beside the code).</summary>
    public sealed record NotAPackage : UpdateLocationProblem
    {
        public override string Message =>
            "This copy of Mosaic was not installed from a release package, so it cannot update itself.";
    }

    public sealed record FolderNotWritable(string Folder) : UpdateLocationProblem
    {
        public override string Message =>
            $"Mosaic cannot replace itself because your account cannot change {Folder} (or the folder it is in). Move Mosaic to a folder you own, such as your user folder, to update automatically.";
    }
}

/// <summary>Command lines the way Windows programs split them (<c>CommandLineToArgvW</c> / the C runtime).</summary>
public static class WindowsCommandLine
{
    /// <summary>Quotes each argument only as far as needed and joins them with spaces.</summary>
    public static string Join(IEnumerable<string> arguments) => string.Join(' ', arguments.Select(Quote));

    public static string Quote(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0) return argument;
        var builder = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }
            if (character == '"')
            {
                // Backslashes before a quote are doubled, and the quote itself escaped.
                builder.Append('\\', backslashes * 2 + 1);
            }
            else
            {
                builder.Append('\\', backslashes);
            }
            backslashes = 0;
            builder.Append(character);
        }
        // Backslashes before the closing quote are doubled so they stay literal.
        builder.Append('\\', backslashes * 2);
        return builder.Append('"').ToString();
    }
}

/// <summary>This build's version, as releases are compared against it.</summary>
public static class MosaicVersion
{
    /// <summary>
    /// The informational version of the app (<c>windows/Directory.Build.props</c>), without the
    /// build metadata .NET appends ("0.3.1+5f2c…" → "0.3.1").
    /// </summary>
    public static string Current { get; } = Read(Assembly.GetEntryAssembly() ?? typeof(MosaicVersion).Assembly);

    internal static string Read(Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var text = informational ?? assembly.GetName().Version?.ToString(3) ?? "0";
        return text.Split('+')[0];
    }
}
