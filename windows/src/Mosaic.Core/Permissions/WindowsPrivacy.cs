using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Mosaic.Core.Permissions;

/// <summary>A device Windows guards with its own privacy switches (Settings › Privacy &amp; security).</summary>
public enum WindowsCapability { Camera, Microphone, Location }

/// <summary>What Windows' privacy settings say about one capability for desktop apps such as Mosaic.</summary>
public enum WindowsCapabilityAccess
{
    /// <summary>Allowed, or nothing says otherwise.</summary>
    Allowed,
    /// <summary>"Let desktop apps access your camera" (or microphone, location) is off.</summary>
    DeniedForDesktopApps,
    /// <summary>"Let apps access your camera" is off for this user.</summary>
    DeniedForUser,
    /// <summary>The device-wide switch (camera access, location services) is off.</summary>
    DeniedForDevice,
    /// <summary>Not Windows, or the settings could not be read.</summary>
    Unknown,
}

/// <summary>
/// Windows' privacy settings for camera, microphone and location, which apply to Mosaic on top of
/// its own per-site decisions: a desktop app gets no system prompt, so a site Mosaic allows can
/// still be refused by Windows. Mosaic reads the switches so its prompt can say so and point to
/// the right Settings page.
/// </summary>
/// <remarks>
/// The switches live under <c>Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\&lt;capability&gt;</c>:
/// the key's <c>Value</c> in HKLM is the device-wide switch, in HKCU the per-user "let apps"
/// switch, and its <c>NonPackaged</c> subkey's <c>Value</c> the "let desktop apps" switch.
/// "Deny" means off; anything else (or nothing) means on.
/// </remarks>
public static class WindowsPrivacy
{
    private const string ConsentStore = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";

    public static string StoreName(WindowsCapability capability) => capability switch
    {
        WindowsCapability.Camera => "webcam",
        WindowsCapability.Microphone => "microphone",
        _ => "location",
    };

    /// <summary>The Settings page that has the switch (<c>ms-settings:</c> URI).</summary>
    public static string SettingsUri(WindowsCapability capability) => capability switch
    {
        WindowsCapability.Camera => "ms-settings:privacy-webcam",
        WindowsCapability.Microphone => "ms-settings:privacy-microphone",
        _ => "ms-settings:privacy-location",
    };

    public static string Noun(WindowsCapability capability) => capability switch
    {
        WindowsCapability.Camera => "camera",
        WindowsCapability.Microphone => "microphone",
        _ => "location",
    };

    /// <summary>The capability Windows guards for a permission kind; null for kinds it does not.</summary>
    public static WindowsCapability? CapabilityFor(PermissionKind kind) => kind switch
    {
        PermissionKind.Camera => WindowsCapability.Camera,
        PermissionKind.Microphone => WindowsCapability.Microphone,
        PermissionKind.Location => WindowsCapability.Location,
        _ => null,
    };

    /// <summary>Reads the switches for <paramref name="capability"/> from the registry. Never throws.</summary>
    public static WindowsCapabilityAccess Read(WindowsCapability capability)
    {
        if (!OperatingSystem.IsWindows()) return WindowsCapabilityAccess.Unknown;
        try
        {
            return ReadRegistry(capability);
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return WindowsCapabilityAccess.Unknown;
        }
    }

    [SupportedOSPlatform("windows")]
    private static WindowsCapabilityAccess ReadRegistry(WindowsCapability capability)
    {
        var key = $@"{ConsentStore}\{StoreName(capability)}";
        static bool Denied(RegistryKey root, string path)
        {
            using var opened = root.OpenSubKey(path);
            return string.Equals(opened?.GetValue("Value") as string, "Deny", StringComparison.OrdinalIgnoreCase);
        }
        return Interpret(
            device: Denied(Registry.LocalMachine, key),
            user: Denied(Registry.CurrentUser, key),
            desktopApps: Denied(Registry.CurrentUser, key + @"\NonPackaged"));
    }

    /// <summary>The most fundamental switch that is off decides.</summary>
    internal static WindowsCapabilityAccess Interpret(bool device, bool user, bool desktopApps) =>
        device ? WindowsCapabilityAccess.DeniedForDevice
        : user ? WindowsCapabilityAccess.DeniedForUser
        : desktopApps ? WindowsCapabilityAccess.DeniedForDesktopApps
        : WindowsCapabilityAccess.Allowed;

    /// <summary>
    /// The sentence a permission prompt shows when Windows would refuse the device even if the
    /// person allows the site; null when Windows allows it (or cannot be asked).
    /// </summary>
    public static string? Explanation(WindowsCapability capability, WindowsCapabilityAccess access)
    {
        var noun = Noun(capability);
        var page = capability switch
        {
            WindowsCapability.Camera => "Camera",
            WindowsCapability.Microphone => "Microphone",
            _ => "Location",
        };
        return access switch
        {
            WindowsCapabilityAccess.DeniedForDesktopApps =>
                $"Windows is blocking {noun} access for desktop apps such as Mosaic. Turn on “Let desktop apps access your {noun}” in Settings › Privacy & security › {page}.",
            WindowsCapabilityAccess.DeniedForUser =>
                $"Windows is blocking {noun} access for all apps. Turn on “{page} access” and “Let apps access your {noun}” in Settings › Privacy & security › {page}.",
            WindowsCapabilityAccess.DeniedForDevice => capability == WindowsCapability.Location
                ? "Location services are turned off in Windows. Turn them on in Settings › Privacy & security › Location."
                : $"{page} access is turned off for this device in Settings › Privacy & security › {page}.",
            _ => null,
        };
    }
}
