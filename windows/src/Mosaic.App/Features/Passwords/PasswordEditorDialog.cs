using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Mosaic.Core.Passwords;

namespace Mosaic.App.Features.Passwords;

/// <summary>Add or edit one password. Port of <c>PasswordEditor</c> in <c>PasswordsSettingsPane.swift</c>.</summary>
internal sealed class PasswordEditorDialog : ContentDialog
{
    private readonly PasswordManager passwords;
    private readonly PasswordCredential? credential;
    private readonly TextBox website = new() { Header = "Website", PlaceholderText = "example.com" };
    private readonly TextBox username = new() { Header = "Username", PlaceholderText = "Optional" };
    private readonly TextBox passwordText = new() { Header = "Password", FontFamily = PasswordsUi.Mono };
    private readonly TextBlock strength = new();
    private readonly InfoBar problem = new() { Severity = InfoBarSeverity.Error, IsClosable = false };

    public PasswordEditorDialog(PasswordManager passwords, PasswordCredential? credential, string password)
    {
        this.passwords = passwords;
        this.credential = credential;
        Title = credential is null ? "Add Password" : "Edit Password";
        PrimaryButtonText = "Save";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;

        website.Text = credential?.Origin.String ?? "";
        website.IsEnabled = credential is null;
        if (credential is not null) ToolTipService.SetToolTip(website, "To use the password on another site, add it there.");
        username.Text = credential?.Username ?? "";
        passwordText.Text = password;
        AutomationProperties.SetName(passwordText, "Password");

        var generate = new Button { Content = "Generate", VerticalAlignment = VerticalAlignment.Bottom };
        generate.Click += (_, _) => passwordText.Text = PasswordGenerator.Generate(passwords.Vault.Preferences.Generator);
        var passwordRow = new Grid { ColumnSpacing = 8 };
        passwordRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        passwordRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        passwordRow.Children.Add(passwordText);
        Grid.SetColumn(generate, 1);
        passwordRow.Children.Add(generate);

        var form = new StackPanel { Spacing = 12, MinWidth = 400 };
        form.Children.Add(website);
        form.Children.Add(username);
        form.Children.Add(passwordRow);
        form.Children.Add(strength);
        form.Children.Add(problem);
        Content = form;

        website.TextChanged += (_, _) => Validate();
        passwordText.TextChanged += (_, _) => Validate();
        PrimaryButtonClick += Save;
        Validate();
    }

    private void Validate()
    {
        var password = passwordText.Text;
        var level = PasswordStrengths.Evaluate(password);
        strength.Text = password.Length == 0 ? "" : $"Strength: {level.Title()}";
        strength.Foreground = PasswordsUi.StrengthBrush(level);
        IsPrimaryButtonEnabled = password.Length > 0 && (credential is not null || SiteOrigin.FromUserInput(website.Text) is not null);
        problem.IsOpen = false;
    }

    private void Save(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var vault = passwords.Vault;
        var name = username.Text.Trim();
        try
        {
            if (credential is not null)
            {
                vault.Edit(credential.Id, name, passwordText.Text);
                return;
            }
            if (SiteOrigin.FromUserInput(website.Text) is not { } origin)
            {
                Fail("Enter a website address, like example.com.", args);
                return;
            }
            var id = new CredentialID(origin, name);
            if (vault.Contains(id))
            {
                Fail(new PasswordStoreException(PasswordStoreErrorKind.Duplicate).Message, args);
                return;
            }
            vault.Save(id, passwordText.Text);
        }
        catch (PasswordStoreException error)
        {
            Fail(error.Message, args);
        }
    }

    private void Fail(string message, ContentDialogButtonClickEventArgs args)
    {
        problem.Message = message;
        problem.IsOpen = true;
        args.Cancel = true;
    }
}
