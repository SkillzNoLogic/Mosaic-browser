using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Microsoft.Win32;
using Mosaic.Core.Browser;

namespace Mosaic.App.Views;

/// <summary>
/// Install and manage Chrome extensions: from a Chrome Web Store link or ID, or a <c>.crx</c> file.
/// </summary>
/// <remarks>
/// The window shows <see cref="ExtensionManager"/> (its DataContext) and calls it; the manager's
/// methods report problems through <see cref="ExtensionManager.ErrorMessage"/> rather than
/// throwing, which is what makes the <c>async void</c> handlers here safe.
/// </remarks>
public partial class ExtensionsWindow : Window
{
    private readonly BrowserModel model;
    private readonly ExtensionManager manager;

    public ExtensionsWindow(BrowserModel model)
    {
        this.model = model;
        manager = model.Extensions;
        InitializeComponent();
        DataContext = manager;

        ChromiumFootnote.Visibility = model.ChromiumAvailable ? Visibility.Visible : Visibility.Collapsed;
        manager.PropertyChanged += Manager_PropertyChanged;
        Closed += (_, _) => manager.PropertyChanged -= Manager_PropertyChanged;
        Loaded += (_, _) =>
        {
            // Not in a scripted run, where the window opens without taking focus.
            if (model.AllowsFocusChanges) InstallInput.Focus();
        };
    }

    private void Done_Click(object sender, RoutedEventArgs e) => Close();

    // ---- Installing ----

    private void Manager_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ExtensionManager.IsBusy)) UpdateInstallButton();
    }

    private void InstallInput_TextChanged(object sender, TextChangedEventArgs e) => UpdateInstallButton();

    private void UpdateInstallButton() =>
        InstallButton.IsEnabled = manager.IsIdle && !string.IsNullOrWhiteSpace(InstallInput.Text);

    private async void InstallInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await InstallFromInputAsync();
    }

    private async void Install_Click(object sender, RoutedEventArgs e) => await InstallFromInputAsync();

    private async Task InstallFromInputAsync()
    {
        var input = InstallInput.Text;
        if (string.IsNullOrWhiteSpace(input) || manager.IsBusy) return;
        // Keep what was typed if it did not work, so it can be corrected.
        if (await manager.InstallFromWebStoreAsync(input)) InstallInput.Clear();
    }

    private async void ChooseFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a Chrome extension package",
            Filter = "Chrome extension packages (*.crx;*.zip)|*.crx;*.zip|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) == true) await manager.InstallFileAsync(dialog.FileName);
    }

    // ---- Rows ----

    private async void Enabled_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton toggle && ItemOf(sender) is { } item)
        {
            await manager.SetEnabledAsync(toggle.IsChecked == true, item.Extension);
        }
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) await manager.RemoveAsync(item.Extension);
    }

    private async void OpenPopup_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) await model.OpenExtensionPageAsync(item.Extension, "popup");
    }

    private async void OpenOptions_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) await model.OpenExtensionPageAsync(item.Extension, "options");
    }

    private static ExtensionItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as ExtensionItem;
}
