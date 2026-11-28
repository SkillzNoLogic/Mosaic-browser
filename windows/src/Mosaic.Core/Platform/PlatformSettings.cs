using System.Globalization;
using System.Text.Json.Nodes;
using Mosaic.Core.Browser;

namespace Mosaic.Core.Platform;

/// <summary>Persisted per-site engine rules (Settings › Engine). Port of <c>EngineRuleStore</c>.</summary>
public sealed class EngineRuleStore : ObservableObject
{
    public const string Key = "engineRules";

    private readonly PlatformStore store;
    private EngineRuleSet? ruleSet;

    public EngineRuleStore(PlatformStore store) => this.store = store;

    public EngineRuleSet RuleSet => ruleSet ??= EngineRuleSet.FromJson(store.Get(Key));

    public IReadOnlyList<EngineRule> Rules => RuleSet.Rules;

    public EngineKind? EngineFor(string? address) => EngineKindExtensions.FromId(RuleSet.EngineFor(address));

    public EngineRule? RuleFor(string? address) => RuleSet.RuleFor(address);

    /// <summary>Adds or replaces the rule for <paramref name="domain"/>. False if it is not a domain.</summary>
    public bool Set(string domain, EngineKind engine, bool includesSubdomains = true, Guid? id = null)
    {
        var updated = RuleSet.Copy();
        if (!updated.Upsert(new EngineRule(id ?? Guid.NewGuid(), domain, engine.Id(), includesSubdomains))) return false;
        Save(updated);
        return true;
    }

    public void Update(EngineRule rule)
    {
        var updated = RuleSet.Copy();
        if (updated.Upsert(rule)) Save(updated);
    }

    public void Remove(EngineRule rule)
    {
        var updated = RuleSet.Copy();
        updated.Remove(rule.Id);
        Save(updated);
    }

    private void Save(EngineRuleSet updated)
    {
        ruleSet = updated;
        store.Set(Key, updated.ToJson());
        Notify(nameof(Rules));
        Notify(nameof(RuleSet));
    }
}

/// <summary>
/// The languages web pages are asked for, applied to both engines (Settings › Languages). Port of
/// <c>LanguageSettings</c> in <c>PlatformServices.swift</c>.
/// </summary>
/// <remarks>
/// Gecko takes the list live, through the agent's <c>setAcceptLanguages</c> (Gecko's
/// <c>intl.accept_languages</c>), each time it starts and whenever the list changes. WebView2 takes
/// languages only when its environment is created (<c>CoreWebView2EnvironmentOptions.Language</c>
/// and Chromium's <c>--accept-lang</c> switch, see <see cref="ChromiumOptions"/>), so a change
/// reaches Chromium tabs the next time Mosaic starts; <see cref="ChromiumNeedsRestart"/> says so.
/// </remarks>
public sealed class LanguageSettings : ObservableObject
{
    public const string Key = "contentLanguages";

    private readonly PlatformStore store;
    private readonly Func<IReadOnlyList<string>> systemLanguages;
    private LanguagePreferences? preferences;
    private bool isCustom;
    private string? geckoError;
    private string? chromiumApplied;

    public LanguageSettings(PlatformStore store, Func<IReadOnlyList<string>>? systemLanguages = null)
    {
        this.store = store;
        this.systemLanguages = systemLanguages ?? DefaultSystemLanguages;
    }

    /// <summary>Raised after the list changed; the model passes it to Gecko.</summary>
    public event Action? Changed;

    public LanguagePreferences Preferences
    {
        get
        {
            if (preferences is null)
            {
                if (store.GetStrings(Key) is { Count: > 0 } saved)
                {
                    preferences = new LanguagePreferences(saved);
                    isCustom = true;
                }
                else
                {
                    preferences = LanguagePreferences.FromSystem(systemLanguages());
                    isCustom = false;
                }
            }
            return preferences;
        }
    }

    /// <summary>False while the list simply follows Windows' preferred languages.</summary>
    public bool IsCustom
    {
        get
        {
            _ = Preferences;
            return isCustom;
        }
    }

    /// <summary>Why the last attempt to give the list to Gecko failed, if it did.</summary>
    public string? GeckoError { get => geckoError; set => Set(ref geckoError, value); }

    /// <summary>The list the Chromium environment was created with (null: Windows' own languages).</summary>
    public string? ChromiumApplied
    {
        get => chromiumApplied;
        set
        {
            if (Set(ref chromiumApplied, value)) Notify(nameof(ChromiumNeedsRestart));
        }
    }

    /// <summary>Chromium tabs still use another list than this one until Mosaic restarts.</summary>
    public bool ChromiumNeedsRestart => (IsCustom ? Preferences.PreferenceValue : null) != chromiumApplied;

    public void Edit(Action<LanguagePreferences> change)
    {
        var updated = Preferences.Copy();
        change(updated);
        if (updated.Equals(Preferences) || updated.Tags.Count == 0) return;
        preferences = updated;
        isCustom = true;
        store.Set(Key, new JsonArray([.. updated.Tags.Select(tag => JsonValue.Create(tag))]));
        Announce();
    }

    /// <summary>Replaces the whole list (automation, and an imported list).</summary>
    public void Replace(LanguagePreferences list)
    {
        if (list.Tags.Count == 0 || (IsCustom && list.Equals(Preferences))) return;
        preferences = list.Copy();
        isCustom = true;
        store.Set(Key, new JsonArray([.. list.Tags.Select(tag => JsonValue.Create(tag))]));
        Announce();
    }

    public void UseSystemLanguages()
    {
        store.Set(Key, null);
        preferences = LanguagePreferences.FromSystem(systemLanguages());
        isCustom = false;
        Announce();
    }

    private void Announce()
    {
        Notify(nameof(Preferences));
        Notify(nameof(IsCustom));
        Notify(nameof(ChromiumNeedsRestart));
        Changed?.Invoke();
    }

    /// <summary>What WebView2 should be created with: a custom list only (otherwise WebView2 follows Windows).</summary>
    public ChromiumLanguageOptions? ChromiumOptions => IsCustom ? ChromiumLanguageOptions.For(Preferences) : null;

    /// <summary>
    /// Reads the saved list straight from <c>platform.json</c> in <paramref name="dataDirectory"/>, for
    /// whoever creates the WebView2 environment before the browser model exists. Null when the list
    /// follows Windows.
    /// </summary>
    public static ChromiumLanguageOptions? ChromiumOptionsFor(string dataDirectory)
    {
        var store = new PlatformStore(Path.Combine(dataDirectory, PlatformStore.FileName));
        return store.GetStrings(Key) is { Count: > 0 } saved && new LanguagePreferences(saved) is { Tags.Count: > 0 } list
            ? ChromiumLanguageOptions.For(list)
            : null;
    }

    private static IReadOnlyList<string> DefaultSystemLanguages() => [CultureInfo.CurrentUICulture.Name];
}

/// <summary>How to hand a language list to WebView2 when its environment is created.</summary>
/// <param name="Language">For <c>CoreWebView2EnvironmentOptions.Language</c>: the first tag (menus, dialogs).</param>
/// <param name="BrowserArgument">For <c>AdditionalBrowserArguments</c>: <c>--accept-lang=en-GB,en,fr</c>, the Accept-Language list and <c>navigator.languages</c>.</param>
/// <param name="Value">The list itself, to report back through <see cref="LanguageSettings.ChromiumApplied"/>.</param>
public sealed record ChromiumLanguageOptions(string Language, string BrowserArgument, string Value)
{
    public static ChromiumLanguageOptions For(LanguagePreferences preferences) =>
        new(preferences.Tags[0], "--accept-lang=" + preferences.PreferenceValue, preferences.PreferenceValue);
}
