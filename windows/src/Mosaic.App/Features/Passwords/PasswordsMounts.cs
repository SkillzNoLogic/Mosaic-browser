using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Mosaic.Core.Browser;
using Mosaic.Core.Passwords;
using Mosaic.Core.Shell;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.System;

namespace Mosaic.App.Features.Passwords;

/// <summary>
/// Where the passwords area plugs into the app (area: passwords): the settings page, the
/// <c>passwords</c> toolbar component (key button), the prompt overlay, the Ctrl+\ fill
/// shortcut, the WebView2 bridge, the Windows Hello check, and automation.
/// </summary>
internal static class PasswordsMounts
{
    /// <summary>Mosaic's window, for Windows Hello and file pickers. Zero until the app has started.</summary>
    public static nint WindowHandle { get; private set; }

    private static Window? window;
    private static DateTimeOffset lastShortcut = DateTimeOffset.MinValue;

    /// <summary>
    /// Opens Settings › Passwords &amp; Autofill. The shell can replace this with its own (it
    /// owns the settings view); until then the page opens in a dialog over the window.
    /// </summary>
    public static Action<BrowserModel>? OpenSettings { get; set; }

#pragma warning disable CA2255 // The feature-mount pattern: only stores delegates (windows/AGENTS.md).
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Register()
    {
        FeatureMounts.SettingsPages[SettingsCategory.Passwords] = model => new PasswordsSettingsPage(model);
        FeatureMounts.Components["passwords"] = model => new PasswordsToolbarButton(model);
        FeatureMounts.Overlays.Add(("passwords.prompt", model => new PasswordPromptView(model)));
        FeatureMounts.Startup.Add(("passwords", Start));
        PasswordsAutomation.Register();
        PasswordsAutomation.Snapshot = SnapshotAsync;
    }

    private static void Start(BrowserModel model, Window mainWindow)
    {
        window = mainWindow;
        WindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(mainWindow);
        var passwords = model.Passwords; // connects to Gecko: event subscriptions only
        passwords.Unlock.Verify = reason => WindowsUserCheck.VerifyAsync(WindowHandle, reason);
        passwords.SettingsRequested += () => (OpenSettings ?? ShowSettingsDialog)(model);
        var bridge = new ChromiumPasswordBridge(model);
        passwords.Chromium = bridge;
        _ = bridge.StartAsync();
        // Decrypted on a worker, so neither the key button nor the first sign-in waits for it.
        _ = passwords.LoadVaultAsync();
        AddFillShortcut(model, mainWindow);
    }

    /// <summary>
    /// Ctrl+\ fills the selected tab, while Mosaic's own controls or a Chromium page have the
    /// keyboard (WebView2 forwards Ctrl shortcuts to the window's accelerators). Inside a Gecko
    /// page the shared page script catches it and Gecko reports it (fillShortcut).
    /// </summary>
    private static void AddFillShortcut(BrowserModel model, Window mainWindow)
    {
        if (mainWindow.Content is not UIElement root) return;
        var shortcut = new KeyboardAccelerator { Key = (VirtualKey)0xDC /* VK_OEM_5: \ | on US layouts */, Modifiers = VirtualKeyModifiers.Control };
        shortcut.Invoked += (sender, args) =>
        {
            args.Handled = true;
            // WebView2 can deliver one key press twice (down and up); one fill is enough.
            var now = DateTimeOffset.UtcNow;
            if (now - lastShortcut < TimeSpan.FromMilliseconds(500)) return;
            lastShortcut = now;
            _ = model.Passwords.FillSelectedAsync();
        };
        root.KeyboardAccelerators.Add(shortcut);
    }

    private static async void ShowSettingsDialog(BrowserModel model)
    {
        if (window?.Content?.XamlRoot is not { } root) return;
        FeatureMounts.CoverPage("passwords.settings", true);
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = root,
                Title = "Passwords & Autofill",
                Content = new PasswordsSettingsPage(model) { MaxHeight = 640, MinWidth = 560 },
                CloseButtonText = "Done",
            };
            await dialog.ShowAsync();
        }
        finally
        {
            FeatureMounts.CoverPage("passwords.settings", false);
        }
    }

    /// <summary>
    /// <c>passwordsSnapshot prompt|button|settings</c>: renders one of the views into a PNG, so
    /// scripts can check them before (or without) the shell placing them. Shown briefly in a popup
    /// over the window (WinUI renders only what is in the live tree); the page is covered meanwhile.
    /// </summary>
    private static async Task SnapshotAsync(BrowserModel model, string which, string path)
    {
        if (window?.Content?.XamlRoot is not { } root) throw new InvalidOperationException("The window is not ready.");
        var passwords = model.Passwords;
        FrameworkElement view;
        switch (which)
        {
            case "prompt":
                var tab = model.SelectedTab ?? throw new InvalidOperationException("There is no tab.");
                var prompt = passwords.PromptFor(tab) ?? new PasswordManager.Prompt.Save(
                    new PasswordSubmission(SiteOrigin.FromUrl("https://example.com")!, "alice@example.com", "correct-horse-battery"), new SaveOffer.Save());
                view = new PasswordPromptView(model).Card(tab, prompt);
                break;
            case "button":
                var button = new PasswordsToolbarButton(model);
                button.Loaded += (_, _) => button.Visibility = Visibility.Visible;
                view = new Border { Padding = new Thickness(8), Child = button };
                break;
            default:
                view = new PasswordsSettingsPage(model) { Width = 760, Height = 1500 };
                break;
        }
        var host = new Border { Child = view, Background = PasswordsUi.Brush("SolidBackgroundFillColorBaseBrush") };
        var popup = new Popup { Child = host, XamlRoot = root };
        FeatureMounts.CoverPage("passwords.snapshot", true);
        try
        {
            popup.IsOpen = true;
            await Task.Delay(700);
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(host);
            var pixels = (await bitmap.GetPixelsAsync()).ToArray();
            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels);
            await encoder.FlushAsync();
            stream.Seek(0);
            await using var file = File.Create(path);
            await stream.AsStreamForRead().CopyToAsync(file);
        }
        finally
        {
            popup.IsOpen = false;
            FeatureMounts.CoverPage("passwords.snapshot", false);
        }
    }
}
