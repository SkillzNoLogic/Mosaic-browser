using Mosaic.Core.Passwords;

namespace Mosaic.Core.Browser;

// Area: passwords. The model's password manager, created on first use (nothing on the startup path).
public sealed partial class BrowserModel
{
    private PasswordManager? passwords;

    /// <summary>
    /// Saved passwords, the save prompt and filling (see <see cref="PasswordManager"/>). Created
    /// and connected to the engines on first use; the vault itself is read later still, when a
    /// page or a view first needs it.
    /// </summary>
    public PasswordManager Passwords
    {
        get
        {
            if (passwords is null) UsePasswords(CreatePasswords());
            return passwords!;
        }
    }

    /// <summary>Replaces the password manager before first use (tests, previews).</summary>
    internal void UsePasswords(PasswordManager manager)
    {
        passwords = manager;
        manager.Attach();
    }

    private PasswordManager CreatePasswords()
    {
        var folder = PasswordProfile.Folder(AppPaths.DataRoot);
        var preferences = new PasswordPreferencesStore(Path.Combine(folder, "settings.json"));
        var unlock = new PasswordUnlock(time, PasswordUnlock.IsAutomatedIsolatedProfile(Environment.GetCommandLineArgs()));
        if (OperatingSystem.IsWindows())
        {
            // The vault lives in this profile's data folder, so an isolated profile
            // (MOSAIC_DATA_DIR) can never see the real profile's passwords.
            var store = DpapiPasswordStore.ForProfile(AppPaths.DataRoot); // reads nothing until first use
            return new PasswordManager(this, () => new PasswordVault(store, preferences, time), unlock, time, "dpapi");
        }
        // Only Windows can keep secrets here; elsewhere (development builds) nothing is persisted.
        return new PasswordManager(this, () => new PasswordVault(new InMemoryPasswordStore(time), preferences, time), unlock, time);
    }
}
