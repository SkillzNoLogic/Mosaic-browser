using System.Diagnostics;
using Microsoft.Win32;
using Mosaic.Core.WindowsIntegration;

namespace Mosaic.App.Features.System;

/// <summary><see cref="IRegistryStore"/> on the real <c>HKEY_CURRENT_USER</c>.</summary>
internal sealed class CurrentUserRegistry : IRegistryStore
{
    public string? GetString(string key, string? name)
    {
        using var opened = Registry.CurrentUser.OpenSubKey(key);
        return opened?.GetValue(name ?? "") switch
        {
            string text => text,
            null => null,
            var other => other.ToString(),
        };
    }

    public void SetString(string key, string? name, string value)
    {
        using var opened = Registry.CurrentUser.CreateSubKey(key, writable: true);
        opened.SetValue(name ?? "", value, RegistryValueKind.String);
    }

    public void DeleteValue(string key, string name)
    {
        using var opened = Registry.CurrentUser.OpenSubKey(key, writable: true);
        opened?.DeleteValue(name, throwOnMissingValue: false);
    }

    public void DeleteTree(string key) => Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);

    public bool KeyExists(string key)
    {
        using var opened = Registry.CurrentUser.OpenSubKey(key);
        return opened is not null;
    }
}

/// <summary>
/// Default-browser integration: registers on request only (never at startup), tells the shell
/// associations changed, opens Settings › Default apps at Mosaic's page, and reports status.
/// </summary>
internal sealed class DefaultBrowser : IDefaultBrowserService
{
    public static DefaultBrowser Shared { get; } = new();

    private readonly CurrentUserRegistry registry = new();

    private static DefaultBrowserRegistration For(string? appName) =>
        new(Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Mosaic.exe"),
            appName ?? DefaultBrowserRegistration.DefaultAppName);

    public DefaultBrowserStatus Status(string? appName = null) => For(appName).Status(registry);

    public DefaultBrowserStatus Register(string? appName = null)
    {
        var registration = For(appName);
        registration.Register(registry);
        SystemNative.SHChangeNotify(SystemNative.SHCNE_ASSOCCHANGED, SystemNative.SHCNF_IDLIST, 0, 0);
        return registration.Status(registry);
    }

    public DefaultBrowserStatus Unregister(string? appName = null)
    {
        var registration = For(appName);
        registration.Unregister(registry);
        SystemNative.SHChangeNotify(SystemNative.SHCNE_ASSOCCHANGED, SystemNative.SHCNF_IDLIST, 0, 0);
        return registration.Status(registry);
    }

    public bool OpenSettings(string? appName = null)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(For(appName).SettingsUri) { UseShellExecute = true });
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The user's effective handler for a scheme or extension, from the shell (AssocQueryString),
    /// which accounts for UserChoice validity the registry alone cannot show.
    /// </summary>
    public string? ShellDefault(string association)
    {
        var flags = association.StartsWith('.') ? 0u : SystemNative.ASSOCF_IS_PROTOCOL;
        uint length = 0;
        SystemNative.AssocQueryStringW(flags, SystemNative.ASSOCSTR_PROGID, association, null, null, ref length);
        if (length == 0) return null;
        var buffer = new char[length];
        if (SystemNative.AssocQueryStringW(flags, SystemNative.ASSOCSTR_PROGID, association, null, buffer, ref length) < 0) return null;
        return new string(buffer, 0, (int)Math.Max(0, length - 1));
    }

    /// <summary>
    /// What the "Make Mosaic the default browser" button does: register, then open Settings at
    /// Mosaic's page so the user can choose it (Windows does not let apps set the default).
    /// </summary>
    public DefaultBrowserStatus RegisterAndOpenSettings()
    {
        var status = Register();
        OpenSettings();
        return status;
    }
}
