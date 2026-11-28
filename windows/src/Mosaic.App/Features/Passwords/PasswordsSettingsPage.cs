using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Mosaic.App.Features.System;
using Mosaic.Core.Browser;
using Mosaic.Core.Passwords;
using Windows.Storage.Pickers;

namespace Mosaic.App.Features.Passwords;

/// <summary>
/// Settings › Passwords &amp; Autofill (area: passwords): saved passwords, the save and fill
/// options, the never-save list, the generator, import and export. Port of
/// <c>PasswordsSettingsPane.swift</c>. Showing, copying, editing or exporting a password asks
/// for Windows Hello or the account password first (once per five minutes; export always asks).
/// </summary>
internal sealed class PasswordsSettingsPage : UserControl
{
    private readonly PasswordManager passwords;
    private readonly Dictionary<CredentialID, string> revealed = [];
    private PasswordAudit? audit;
    private string sample;

    private readonly ToggleSwitch offerToSave = new() { Header = null, OnContent = "Offer to save passwords", OffContent = "Offer to save passwords" };
    private readonly ToggleSwitch suggest = new() { OnContent = "Suggest strong passwords on sign-up forms", OffContent = "Suggest strong passwords on sign-up forms" };
    private readonly TextBox search = new() { PlaceholderText = "Search sites and usernames" };
    private readonly TextBlock savedHeader = new();
    private readonly StackPanel savedList = new() { Spacing = 4 };
    private readonly StackPanel neverList = new() { Spacing = 4 };
    private readonly StackPanel generatorPanel = new() { Spacing = 8 };
    private readonly MenuFlyoutItem exportItem = new() { Text = "Export to CSV…" };
    private readonly MenuFlyoutItem auditItem = new() { Text = "Check for Weak and Reused Passwords" };
    private bool updating;

    public PasswordsSettingsPage(BrowserModel model)
    {
        passwords = model.Passwords;
        sample = PasswordGenerator.Generate(passwords.Vault.Preferences.Generator);
        AutomationProperties.SetName(this, "Passwords & Autofill");
        AutomationProperties.SetName(search, "Search passwords");

        var page = new StackPanel { Spacing = 24, Padding = new Thickness(24, 16, 24, 32), MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Stretch };
        page.Children.Add(new TextBlock { Text = "Passwords & Autofill", Style = PasswordsUi.Style("TitleTextBlockStyle") });
        page.Children.Add(SavingSection());
        page.Children.Add(SavedSection());
        page.Children.Add(Section("Never Saved For", neverList, null));
        page.Children.Add(Section("Password Generator", generatorPanel, null));
        Content = new ScrollViewer { Content = page };

        offerToSave.Toggled += (_, _) => SetPreference(p => p with { OfferToSave = offerToSave.IsOn });
        suggest.Toggled += (_, _) => SetPreference(p => p with { SuggestStrongPasswords = suggest.IsOn });
        search.TextChanged += (_, _) => RebuildSaved();

        Loaded += (_, _) =>
        {
            passwords.Changed += Rebuild;
            passwords.NotifyChanged();
        };
        Unloaded += (_, _) =>
        {
            passwords.Changed -= Rebuild;
            // Nothing revealed survives leaving the page, and the next visit asks again.
            revealed.Clear();
            passwords.Unlock.Lock();
        };
        Rebuild();
    }

    private static Border Section(string title, UIElement body, string? footer)
    {
        var stack = new StackPanel { Spacing = 10 };
        var header = new TextBlock { Text = title, Style = PasswordsUi.Style("SubtitleTextBlockStyle") };
        AutomationProperties.SetHeadingLevel(header, AutomationHeadingLevel.Level2);
        stack.Children.Add(header);
        stack.Children.Add(body);
        if (footer is not null) stack.Children.Add(PasswordsUi.Caption(footer));
        return Card(stack);
    }

    private static Border Card(UIElement child) => new()
    {
        Child = child,
        Padding = new Thickness(16),
        CornerRadius = new CornerRadius(8),
        BorderThickness = new Thickness(1),
        Background = PasswordsUi.Brush("CardBackgroundFillColorDefaultBrush"),
        BorderBrush = PasswordsUi.Brush("CardStrokeColorDefaultBrush"),
    };

    private Border SavingSection()
    {
        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(offerToSave);
        body.Children.Add(suggest);
        body.Children.Add(new TextBlock
        {
            Text = "To fill a saved password, use the key button in the address bar or press Ctrl+\\. Mosaic fills only when you ask, and only on the exact site the password was saved for: the same scheme, host and port. It never fills into a frame from another site, and never offers to save in private tabs.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = PasswordsUi.Brush("TextFillColorSecondaryBrush"),
        });
        return Section("Saving and Filling", body,
            "Passwords are encrypted with Windows Data Protection for your Windows account and kept in this Mosaic profile. Mosaic keeps only non-secret details (when a password was last used, the never-save list) in its own settings.");
    }

    private Border SavedSection()
    {
        var body = new StackPanel { Spacing = 8 };
        var bar = new Grid { ColumnSpacing = 8 };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.Children.Add(search);
        var add = new Button { Content = "Add" };
        ToolTipService.SetToolTip(add, "Add a password");
        add.Click += (_, _) => _ = EditAsync(null, PasswordGenerator.Generate(passwords.Vault.Preferences.Generator));
        Grid.SetColumn(add, 1);
        bar.Children.Add(add);
        var more = new MenuFlyout();
        var import = new MenuFlyoutItem { Text = "Import from CSV…" };
        import.Click += (_, _) => _ = ImportAsync();
        exportItem.Click += (_, _) => _ = ExportAsync();
        auditItem.Click += (_, _) => _ = AuditAsync();
        more.Items.Add(import);
        more.Items.Add(exportItem);
        more.Items.Add(new MenuFlyoutSeparator());
        more.Items.Add(auditItem);
        var moreButton = new DropDownButton { Content = Icons.Make("ellipsis", 14), Flyout = more };
        AutomationProperties.SetName(moreButton, "More password actions");
        Grid.SetColumn(moreButton, 2);
        bar.Children.Add(moreButton);
        body.Children.Add(bar);
        body.Children.Add(savedList);

        var stack = new StackPanel { Spacing = 10 };
        savedHeader.Style = PasswordsUi.Style("SubtitleTextBlockStyle");
        AutomationProperties.SetHeadingLevel(savedHeader, AutomationHeadingLevel.Level2);
        stack.Children.Add(savedHeader);
        stack.Children.Add(body);
        return Card(stack);
    }

    private void SetPreference(Func<PasswordPreferences, PasswordPreferences> change)
    {
        if (updating) return;
        passwords.Vault.Update(change);
        passwords.NotifyChanged();
    }

    private void Rebuild()
    {
        updating = true;
        var preferences = passwords.Vault.Preferences;
        offerToSave.IsOn = preferences.OfferToSave;
        suggest.IsOn = preferences.SuggestStrongPasswords;
        updating = false;
        RebuildSaved();
        RebuildNever();
        RebuildGenerator();
    }

    // ---- Saved passwords ----

    private void RebuildSaved()
    {
        var vault = passwords.Vault;
        savedHeader.Text = $"Saved Passwords ({vault.Credentials.Count})";
        exportItem.IsEnabled = vault.Credentials.Count > 0;
        auditItem.IsEnabled = vault.Credentials.Count > 0;
        savedList.Children.Clear();

        if (vault.Store is DpapiPasswordStore { UnreadableReason: { } unreadable } store)
        {
            var problem = new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Error, Title = "Saved passwords unavailable", Message = unreadable };
            var startOver = new Button { Content = "Start a New Vault" };
            ToolTipService.SetToolTip(startOver, "Keeps the old file beside the new one, in case it can be opened again as the Windows user who made it.");
            startOver.Click += (_, _) =>
            {
                store.SetAsideUnreadable();
                passwords.NotifyChanged();
            };
            problem.ActionButton = startOver;
            savedList.Children.Add(problem);
            return;
        }
        if (vault.LastError is { } error)
        {
            savedList.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning, Message = error });
        }
        if (audit is { } result)
        {
            var weak = result.Weak.Count;
            var reused = result.Reused.Count;
            savedList.Children.Add(new InfoBar
            {
                IsOpen = true,
                IsClosable = true,
                Severity = weak + reused == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
                Message = weak + reused == 0 ? "No weak or reused passwords. Checked on this PC only."
                    : $"{weak} weak, {reused} reused. Change them on their sites, then update them here. Checked on this PC only.",
            });
        }
        var list = vault.Search(search.Text);
        if (list.Count == 0)
        {
            savedList.Children.Add(new TextBlock
            {
                Text = search.Text.Trim().Length == 0
                    ? "No saved passwords yet. Mosaic offers to save one when you sign in to a site, or add one with Add."
                    : $"No passwords match “{search.Text.Trim()}”.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = PasswordsUi.Brush("TextFillColorSecondaryBrush"),
                Margin = new Thickness(0, 6, 0, 6),
            });
            return;
        }
        foreach (var credential in list) savedList.Children.Add(Row(credential));
    }

    private Grid Row(PasswordCredential credential)
    {
        var id = credential.Id;
        var row = new Grid { ColumnSpacing = 10, Padding = new Thickness(4, 6, 4, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        AutomationProperties.SetName(row, $"{credential.Origin.DisplayName}, {(credential.Username.Length == 0 ? "no username" : credential.Username)}");

        var secure = credential.Origin.IsSecure;
        var icon = Icons.Make(secure ? "globe" : "exclamationmark.triangle", 16);
        icon.Foreground = PasswordsUi.Brush(secure ? "TextFillColorSecondaryBrush" : "SystemFillColorCautionBrush");
        icon.VerticalAlignment = VerticalAlignment.Center;
        ToolTipService.SetToolTip(icon, secure ? credential.Origin.String : "Saved for an unencrypted (http) site");
        AutomationProperties.SetAccessibilityView(icon, AccessibilityView.Raw);
        row.Children.Add(icon);

        var names = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(new TextBlock { Text = credential.Origin.DisplayName, Style = PasswordsUi.Style("BodyStrongTextBlockStyle"), TextTrimming = TextTrimming.CharacterEllipsis });
        names.Children.Add(new TextBlock { Text = PasswordsUi.UsernameText(credential.Username), Foreground = PasswordsUi.Brush("TextFillColorSecondaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(names, 1);
        row.Children.Add(names);

        var badges = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        if (audit?.Weak.Contains(id) == true) badges.Children.Add(Badge("Weak", InfoBarSeverity.Warning));
        if (audit?.Reused.Contains(id) == true) badges.Children.Add(Badge("Reused", InfoBarSeverity.Error));
        Grid.SetColumn(badges, 2);
        row.Children.Add(badges);

        var shown = revealed.GetValueOrDefault(id);
        var secret = new TextBlock
        {
            Text = shown ?? "••••••••",
            FontFamily = PasswordsUi.Mono,
            MaxWidth = 180,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsTextSelectionEnabled = shown is not null,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(secret, shown is null ? "Password hidden" : "Password");
        Grid.SetColumn(secret, 3);
        row.Children.Add(secret);

        var eye = PasswordsUi.IconButton(shown is null ? "eye" : "eye.slash",
            shown is null ? $"Show password for {credential.Origin.DisplayName}" : "Hide password", () =>
            {
                if (revealed.Remove(id)) RebuildSaved();
                else _ = RevealAsync(id);
            });
        Grid.SetColumn(eye, 4);
        row.Children.Add(eye);

        var menu = new MenuFlyout();
        var copyUser = new MenuFlyoutItem { Text = "Copy Username", IsEnabled = credential.Username.Length > 0 };
        copyUser.Click += (_, _) => PasswordClipboard.Copy(credential.Username, secret: false);
        var copyPassword = new MenuFlyoutItem { Text = "Copy Password" };
        copyPassword.Click += (_, _) => _ = CopyPasswordAsync(id);
        var edit = new MenuFlyoutItem { Text = "Edit…" };
        edit.Click += (_, _) => _ = EditExistingAsync(credential);
        var delete = new MenuFlyoutItem { Text = "Delete…" };
        delete.Click += (_, _) => _ = DeleteAsync(credential);
        menu.Items.Add(copyUser);
        menu.Items.Add(copyPassword);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(edit);
        menu.Items.Add(delete);
        var actions = new Button { Content = Icons.Make("ellipsis", 14), Flyout = menu, Padding = new Thickness(6), MinWidth = 0, MinHeight = 0 };
        AutomationProperties.SetName(actions, $"Actions for {credential.Origin.DisplayName}");
        Grid.SetColumn(actions, 5);
        row.Children.Add(actions);
        return row;
    }

    /// <summary>A small coloured label ("Weak", "Reused").</summary>
    private static Border Badge(string text, InfoBarSeverity severity) => new()
    {
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(6, 1, 6, 1),
        Background = PasswordsUi.Brush(severity == InfoBarSeverity.Error ? "SystemFillColorCriticalBackgroundBrush" : "SystemFillColorCautionBackgroundBrush"),
        Child = new TextBlock
        {
            Text = text,
            Style = PasswordsUi.Style("CaptionTextBlockStyle"),
            Foreground = PasswordsUi.Brush(severity == InfoBarSeverity.Error ? "SystemFillColorCriticalBrush" : "SystemFillColorCautionBrush"),
        },
    };

    // ---- Never save ----

    private void RebuildNever()
    {
        neverList.Children.Clear();
        var never = passwords.Vault.Preferences.NeverSave;
        if (never.Count == 0)
        {
            neverList.Children.Add(new TextBlock
            {
                Text = "When you choose “Never for This Site” in the save prompt, the site is listed here.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = PasswordsUi.Brush("TextFillColorSecondaryBrush"),
            });
            return;
        }
        foreach (var origin in never)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = origin.DisplayName, VerticalAlignment = VerticalAlignment.Center });
            var remove = new Button { Content = "Remove" };
            AutomationProperties.SetName(remove, $"Offer to save passwords on {origin.DisplayName} again");
            remove.Click += (_, _) =>
            {
                passwords.Vault.SetNeverSave(false, origin);
                passwords.NotifyChanged();
            };
            Grid.SetColumn(remove, 1);
            row.Children.Add(remove);
            neverList.Children.Add(row);
        }
    }

    // ---- Generator ----

    private void RebuildGenerator()
    {
        var options = passwords.Vault.Preferences.Generator;
        generatorPanel.Children.Clear();

        var sampleRow = new Grid { ColumnSpacing = 4, Padding = new Thickness(8), CornerRadius = new CornerRadius(6), Background = PasswordsUi.Brush("SubtleFillColorSecondaryBrush") };
        sampleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sampleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        sampleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var sampleText = new TextBlock { Text = sample, FontFamily = PasswordsUi.Mono, IsTextSelectionEnabled = true, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(sampleText, "Generated password");
        sampleRow.Children.Add(sampleText);
        var again = PasswordsUi.IconButton("arrow.clockwise", "Generate another password", () =>
        {
            sample = PasswordGenerator.Generate(passwords.Vault.Preferences.Generator);
            RebuildGenerator();
        });
        Grid.SetColumn(again, 1);
        sampleRow.Children.Add(again);
        var copy = PasswordsUi.IconButton("doc.on.doc", "Copy generated password", () =>
        {
            PasswordClipboard.Copy(sample, secret: true);
            passwords.ShowNotice("Password copied");
        });
        Grid.SetColumn(copy, 2);
        sampleRow.Children.Add(copy);
        generatorPanel.Children.Add(sampleRow);

        var length = new Slider
        {
            Header = $"Length: {options.EffectiveLength}",
            Minimum = 8,
            Maximum = 64,
            StepFrequency = 1,
            Value = Math.Clamp(options.Length, 8, 64),
            MaxWidth = 320,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(length, "Password length");
        length.ValueChanged += (_, e) =>
        {
            var value = (int)Math.Round(e.NewValue);
            if (value == passwords.Vault.Preferences.Generator.Length) return;
            UpdateGenerator(g => g with { Length = value }, rebuild: false);
            length.Header = $"Length: {passwords.Vault.Preferences.Generator.EffectiveLength}";
        };
        // Rebuilt once the drag ends, not on every step of it.
        length.PointerCaptureLost += (_, _) => RebuildGenerator();
        generatorPanel.Children.Add(length);

        generatorPanel.Children.Add(Check("Lowercase letters (a–z)", options.Lowercase, on => g => g with { Lowercase = on }));
        generatorPanel.Children.Add(Check("Capital letters (A–Z)", options.Uppercase, on => g => g with { Uppercase = on }));
        generatorPanel.Children.Add(Check("Digits (0–9)", options.Digits, on => g => g with { Digits = on }));
        generatorPanel.Children.Add(Check($"Symbols ({PasswordGeneratorOptions.SymbolSet})", options.Symbols, on => g => g with { Symbols = on }));
        generatorPanel.Children.Add(Check("Avoid look-alike characters (l, 1, I, O, 0)", options.AvoidAmbiguous, on => g => g with { AvoidAmbiguous = on }));

        var restore = new Button { Content = "Restore Defaults", IsEnabled = options != PasswordGeneratorOptions.Default, HorizontalAlignment = HorizontalAlignment.Right };
        restore.Click += (_, _) => UpdateGenerator(_ => PasswordGeneratorOptions.Default);
        generatorPanel.Children.Add(restore);
        generatorPanel.Children.Add(PasswordsUi.Caption($"About {(int)options.EntropyBits} bits of randomness. Used for suggestions on sign-up forms and for new passwords here."));
    }

    private CheckBox Check(string text, bool value, Func<bool, Func<PasswordGeneratorOptions, PasswordGeneratorOptions>> change)
    {
        var box = new CheckBox { Content = text, IsChecked = value };
        box.Click += (_, _) => UpdateGenerator(change(box.IsChecked == true));
        return box;
    }

    private void UpdateGenerator(Func<PasswordGeneratorOptions, PasswordGeneratorOptions> change, bool rebuild = true)
    {
        passwords.Vault.Update(p => p with { Generator = change(p.Generator) });
        sample = PasswordGenerator.Generate(passwords.Vault.Preferences.Generator);
        if (rebuild) RebuildGenerator();
    }

    // ---- Actions ----

    private async Task<bool> AuthenticateAsync(string reason, bool fresh = false)
    {
        if (await passwords.Unlock.AuthenticateAsync(reason, fresh)) return true;
        passwords.ShowNotice("Mosaic could not confirm it’s you, so the password stays hidden.");
        return false;
    }

    private async Task RevealAsync(CredentialID id)
    {
        if (!await AuthenticateAsync("show a saved password")) return;
        try
        {
            revealed[id] = passwords.Vault.Password(id);
            RebuildSaved();
        }
        catch (PasswordStoreException error)
        {
            await MessageAsync(error.Message);
        }
    }

    private async Task CopyPasswordAsync(CredentialID id)
    {
        if (!await AuthenticateAsync("copy a saved password")) return;
        try
        {
            PasswordClipboard.Copy(passwords.Vault.Password(id), secret: true);
            passwords.ShowNotice("Password copied. It is cleared from the clipboard in a minute.");
        }
        catch (PasswordStoreException error)
        {
            await MessageAsync(error.Message);
        }
    }

    private async Task EditExistingAsync(PasswordCredential credential)
    {
        if (!await AuthenticateAsync("edit a saved password")) return;
        string password;
        try
        {
            password = passwords.Vault.Password(credential.Id);
        }
        catch (PasswordStoreException error)
        {
            await MessageAsync(error.Message);
            return;
        }
        await EditAsync(credential, password);
    }

    private async Task EditAsync(PasswordCredential? credential, string password)
    {
        var editor = new PasswordEditorDialog(passwords, credential, password) { XamlRoot = XamlRoot };
        if (await editor.ShowAsync() == ContentDialogResult.Primary)
        {
            if (credential is not null) revealed.Remove(credential.Id);
            passwords.NotifyChanged();
        }
    }

    private async Task DeleteAsync(PasswordCredential credential)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Delete the password for {(credential.Username.Length == 0 ? "this login" : credential.Username)} on {credential.Origin.DisplayName}?",
            Content = "This removes it from Mosaic. It can’t be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            passwords.Vault.Delete(credential.Id);
            revealed.Remove(credential.Id);
            passwords.NotifyChanged();
        }
        catch (PasswordStoreException error)
        {
            await MessageAsync($"Couldn’t delete: {error.Message}");
        }
    }

    private async Task AuditAsync()
    {
        if (!await AuthenticateAsync("check your saved passwords")) return;
        var vault = passwords.Vault;
        audit = await Task.Run(vault.Audit);
        RebuildSaved();
    }

    private async Task ImportAsync()
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(".csv");
        picker.FileTypeFilter.Add(".txt");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, PasswordsMounts.WindowHandle);
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        string message;
        try
        {
            var bytes = await File.ReadAllBytesAsync(file.Path);
            string text;
            try
            {
                text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                text = Encoding.Latin1.GetString(bytes);
            }
            var parsed = PasswordCsv.ParseExport(text);
            var vault = passwords.Vault;
            message = (await Task.Run(() => vault.ImportEntries(parsed))).Describe();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PasswordCsv.ImportException or PasswordStoreException)
        {
            message = error is IOException or UnauthorizedAccessException ? "Couldn’t read that file." : error.Message;
        }
        passwords.NotifyChanged();
        await MessageAsync(message);
    }

    private async Task ExportAsync()
    {
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Export passwords?",
            Content = new TextBlock
            {
                Text = "The file will contain all your saved passwords in plain text. Anyone who can read it can sign in as you. Keep it somewhere safe and delete it when you no longer need it.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Export…",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        if (!await AuthenticateAsync("export your saved passwords", fresh: true)) return;

        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, SuggestedFileName = "Mosaic Passwords" };
        picker.FileTypeChoices.Add("CSV file", [".csv"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, PasswordsMounts.WindowHandle);
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        try
        {
            var csv = PasswordCsv.Export(passwords.Vault.ExportEntries());
            await PasswordExportFile.WriteAsync(file.Path, csv);
            await MessageAsync("Passwords exported. Delete the file once you have imported it elsewhere.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PasswordStoreException)
        {
            await MessageAsync($"Couldn’t export: {error.Message}");
        }
    }

    private async Task MessageAsync(string text)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "OK",
        };
        await dialog.ShowAsync();
    }
}
