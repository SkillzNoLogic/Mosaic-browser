using Mosaic.Core.Browser;

namespace Mosaic.Core.Passwords;

/// <summary>
/// Asks the person at the PC to prove they are the Windows user (Windows Hello: face,
/// fingerprint or PIN; or the account password) before saved passwords are shown, copied,
/// edited or exported. Port of <c>PasswordAuthenticator</c> in
/// <c>Sources/Mosaic/Features/Passwords/PasswordManager.swift</c>; the prompt itself is the
/// app's (<see cref="Verify"/>), the timing rules are here.
/// </summary>
public sealed class PasswordUnlock(TimeProvider? time = null, bool bypassesForAutomation = false)
{
    /// <summary>After a successful check, passwords stay viewable this long (or until <see cref="Lock"/>).</summary>
    public static readonly TimeSpan UnlockDuration = TimeSpan.FromMinutes(5);

    private readonly TimeProvider time = time ?? TimeProvider.System;
    private DateTimeOffset? unlockedUntil;

    /// <summary>
    /// The system prompt: given the reason ("show a saved password"), resolves to true when the
    /// user verified. Set by the app (Windows Hello, then Windows credentials). Without it,
    /// nothing is ever unlocked.
    /// </summary>
    public Func<string, Task<bool>>? Verify { get; set; }

    /// <summary>
    /// Scripted checks on an isolated profile cannot answer a system dialog and must not put one
    /// on the screen of the person using the PC. They only ever see that profile's own vault
    /// (<c>MOSAIC_DATA_DIR</c>), never the real passwords.
    /// </summary>
    public bool BypassesForAutomation { get; } = bypassesForAutomation;

    public bool IsUnlocked => unlockedUntil is { } until && until > time.GetUtcNow();

    /// <param name="reason">Shown in the system prompt: "show a saved password".</param>
    /// <param name="fresh">Ignores an earlier unlock (export always asks again).</param>
    public async Task<bool> AuthenticateAsync(string reason, bool fresh = false)
    {
        if (BypassesForAutomation) return true;
        if (!fresh && IsUnlocked) return true;
        if (Verify is not { } verify) return false;
        bool verified;
        try
        {
            verified = await verify(reason);
        }
        catch (Exception)
        {
            // A prompt that cannot be shown is a "no", never a way in.
            verified = false;
        }
        if (verified) unlockedUntil = time.GetUtcNow() + UnlockDuration;
        return verified;
    }

    public void Lock() => unlockedUntil = null;

    /// <summary>True when this run is an isolated profile driven by automation (see <see cref="BypassesForAutomation"/>).</summary>
    public static bool IsAutomatedIsolatedProfile(IEnumerable<string> arguments) =>
        PasswordProfile.IsIsolated && arguments.Contains(Automation.Flag);
}

/// <summary>Which profile's passwords this run uses.</summary>
public static class PasswordProfile
{
    /// <summary>An isolated profile: <c>MOSAIC_DATA_DIR</c> names the data folder (tests, portable copies).</summary>
    public static bool IsIsolated => Environment.GetEnvironmentVariable("MOSAIC_DATA_DIR") is { Length: > 0 };

    /// <summary>Folder for the vault and the password settings.</summary>
    public static string Folder(string dataRoot) => Path.Combine(dataRoot, "Passwords");
}
