using Mosaic.Core.Browser;
using Mosaic.Core.Shell;

namespace Mosaic.App.Features.Privacy;

/// <summary>
/// How the privacy area asks the shell to show or hide Settings. The shell area owns settings
/// (<c>BrowserModel.ShowSettings</c>); until its code is part of the build these do nothing.
/// </summary>
internal static class ShellBridge
{
    /// <summary>Shows Settings at a category. Set by <see cref="PrivacyMounts"/> once the shell's API exists.</summary>
    public static Action<BrowserModel, SettingsCategory>? Show { get; set; }

    public static Action<BrowserModel>? Dismiss { get; set; }

    public static void ShowSettings(BrowserModel model, SettingsCategory category) => Show?.Invoke(model, category);

    public static void DismissSettings(BrowserModel model) => Dismiss?.Invoke(model);
}
