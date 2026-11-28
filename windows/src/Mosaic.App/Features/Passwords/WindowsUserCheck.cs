using System.Runtime.InteropServices;
using System.Security.Principal;
using Windows.Security.Credentials.UI;

namespace Mosaic.App.Features.Passwords;

/// <summary>
/// Proves the person at the PC is the signed-in Windows user before saved passwords are shown,
/// copied, edited or exported (the counterpart of Touch ID / the login password on macOS).
/// </summary>
/// <remarks>
/// <para>
/// First choice: <b>Windows Hello</b> (face, fingerprint or PIN) through
/// <c>UserConsentVerifier</c>, using its desktop interop so the prompt belongs to Mosaic's window
/// (<c>IUserConsentVerifierInterop::RequestVerificationForWindowAsync</c>).
/// </para>
/// <para>
/// When Hello is not set up or not available (no PIN, disabled by policy, a remote session), the
/// Windows credential prompt (<c>CredUIPromptForWindowsCredentials</c>) asks for the account's
/// password, which is then checked with <c>LogonUser</c> and must belong to the same account
/// (same SID). Cancelling Hello does not fall through to the password prompt: a "no" is a no.
/// </para>
/// </remarks>
internal static class WindowsUserCheck
{
    public static async Task<bool> VerifyAsync(nint window, string reason)
    {
        var message = $"Mosaic needs to confirm it’s you to {reason}.";
        UserConsentVerifierAvailability availability;
        try
        {
            availability = await UserConsentVerifier.CheckAvailabilityAsync();
        }
        catch (Exception)
        {
            availability = UserConsentVerifierAvailability.DeviceNotPresent;
        }
        if (availability == UserConsentVerifierAvailability.Available)
        {
            try
            {
                var result = await UserConsentVerifierInterop.RequestVerificationForWindowAsync(window, message);
                if (result == UserConsentVerificationResult.Verified) return true;
                // Cancelled, retries exhausted or device busy: only a missing Hello falls back.
                if (result is not (UserConsentVerificationResult.DeviceNotPresent or UserConsentVerificationResult.NotConfiguredForUser
                    or UserConsentVerificationResult.DisabledByPolicy))
                {
                    return false;
                }
            }
            catch (Exception)
            {
                // Hello could not be shown (for example in a remote session); ask for the password.
            }
        }
        return PromptForWindowsPassword(window, message);
    }

    // ---- Windows credential prompt ----

    private const int CreduiwinEnumerateCurrentUser = 0x200;
    private const int CredPackProtectedCredentials = 0x1;
    private const int ErrorCancelled = 1223;
    private const int Logon32LogonInteractive = 2;
    private const int Logon32LogonNetwork = 3;
    private const int Logon32ProviderDefault = 0;

    private static bool PromptForWindowsPassword(nint window, string message)
    {
        var current = WindowsIdentity.GetCurrent();
        var info = new CredUiInfo
        {
            Size = Marshal.SizeOf<CredUiInfo>(),
            Parent = window,
            Message = message,
            Caption = "Mosaic",
        };

        // The prompt starts on the current account (its name, an empty password).
        nint inBuffer = 0;
        var inSize = 0;
        CredPackAuthenticationBuffer(0, current.Name, "", 0, ref inSize);
        if (inSize > 0)
        {
            inBuffer = Marshal.AllocCoTaskMem(inSize);
            if (!CredPackAuthenticationBuffer(0, current.Name, "", inBuffer, ref inSize))
            {
                Marshal.FreeCoTaskMem(inBuffer);
                inBuffer = 0;
                inSize = 0;
            }
        }

        var package = 0;
        var save = false;
        nint outBuffer = 0;
        var outSize = 0;
        try
        {
            var status = CredUIPromptForWindowsCredentials(ref info, 0, ref package, inBuffer, inSize,
                out outBuffer, out outSize, ref save, CreduiwinEnumerateCurrentUser);
            if (status == ErrorCancelled || status != 0 || outBuffer == 0) return false;

            var user = new char[514];
            var domain = new char[338];
            var password = new char[514];
            int userLength = user.Length, domainLength = domain.Length, passwordLength = password.Length;
            try
            {
                if (!CredUnPackAuthenticationBuffer(CredPackProtectedCredentials, outBuffer, outSize,
                        user, ref userLength, domain, ref domainLength, password, ref passwordLength))
                {
                    return false;
                }
                var (name, realm) = Split(new string(user, 0, Math.Max(0, userLength - 1)), new string(domain, 0, Math.Max(0, domainLength - 1)));
                foreach (var logonType in new[] { Logon32LogonInteractive, Logon32LogonNetwork })
                {
                    if (!LogonUser(name, realm, password, logonType, Logon32ProviderDefault, out var token)) continue;
                    try
                    {
                        using var identity = new WindowsIdentity(token);
                        // Someone else's valid password is not this user's consent.
                        return identity.User is not null && identity.User == current.User;
                    }
                    finally
                    {
                        CloseHandle(token);
                    }
                }
                return false;
            }
            finally
            {
                Array.Clear(password);
            }
        }
        finally
        {
            if (inBuffer != 0) Marshal.FreeCoTaskMem(inBuffer);
            if (outBuffer != 0)
            {
                Marshal.Copy(new byte[outSize], 0, outBuffer, outSize);
                Marshal.FreeCoTaskMem(outBuffer);
            }
        }
    }

    /// <summary>"DOMAIN\user", "user@domain" or a bare name, into LogonUser's user and domain.</summary>
    private static (string User, string? Domain) Split(string user, string domain)
    {
        if (domain.Length > 0) return (user, domain);
        var slash = user.IndexOf('\\');
        if (slash > 0) return (user[(slash + 1)..], user[..slash]);
        // A UPN ("name@example.com", a Microsoft account) goes with a null domain.
        return (user, user.Contains('@') ? null : ".");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CredUiInfo
    {
        public int Size;
        public nint Parent;
        public string Message;
        public string Caption;
        public nint Banner;
    }

    [DllImport("credui.dll", CharSet = CharSet.Unicode)]
    private static extern int CredUIPromptForWindowsCredentials(ref CredUiInfo info, int authError, ref int authPackage,
        nint inAuthBuffer, int inAuthBufferSize, out nint outAuthBuffer, out int outAuthBufferSize,
        [MarshalAs(UnmanagedType.Bool)] ref bool save, int flags);

    [DllImport("credui.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredPackAuthenticationBuffer(int flags, string userName, string password, nint packedCredentials, ref int packedSize);

    [DllImport("credui.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredUnPackAuthenticationBuffer(int flags, nint authBuffer, int authBufferSize,
        [Out] char[] userName, ref int maxUserName, [Out] char[] domainName, ref int maxDomainName,
        [Out] char[] password, ref int maxPassword);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LogonUser(string user, string? domain, char[] password, int logonType, int logonProvider, out nint token);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
