using System.Reactive;
using EveConsole.Localization;
using EveConsole.Services;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>
/// Miscellaneous UI preferences that do not belong to any of the data-source tabs: appearance,
/// language, and the destination for clicking the EVE clock.
/// </summary>
public class OtherSettingsViewModel : ReactiveObject
{
    /// <summary>Sentinel entry in the dropdown; anything not matching a preset selects it
    /// and reveals the free-text box. Only ever compared within one run, so it can be in the
    /// interface language: the choice is stored as the address it comes to, never as this.</summary>
    public static string CustomOption => SettingsText.CustomUrlOption;

    private readonly UiLinkSettings _settings;
    private bool _loading = true;

    // ── Language ──────────────────────────────────────────────────────────────

    /// <summary>"System default", then every language the app has text for, by its own name.</summary>
    public IReadOnlyList<LanguageChoice> LanguageChoices { get; } = Languages.Choices();

    private LanguageChoice? _selectedLanguage;

    /// <summary>
    /// The language chosen for this machine. Stored the moment it is picked, and shown from the
    /// next start — see Languages for why not at once — so a change offers a restart.
    /// </summary>
    public LanguageChoice? SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedLanguage, value);
            if (_loading || value is null) return;
            try   { Languages.Choose(value.Code); RestartError = null; }
            catch (Exception ex) { RestartError = ex.Message; }
            this.RaisePropertyChanged(nameof(LanguageNeedsRestart));
        }
    }

    /// <summary>A language is chosen that this run is not showing.</summary>
    public bool LanguageNeedsRestart => Languages.RestartNeeded;

    /// <summary>The language on screen is still being translated, so parts of it are English.</summary>
    public bool LanguageIsPreview => Languages.Active.Preview;

    private string? _restartError;
    /// <summary>Why the restart or the choice did not go through, in words; null when nothing failed.</summary>
    public string? RestartError
    {
        get => _restartError;
        private set => this.RaiseAndSetIfChanged(ref _restartError, value);
    }

    /// <summary>Restarts into the chosen language. Returns only when the restart failed.</summary>
    public ReactiveCommand<Unit, Unit> RestartCommand { get; }

    // ── UI scale ──────────────────────────────────────────────────────────────

    /// <summary>The scales on offer, 50% to 200%.</summary>
    public IReadOnlyList<UiScaleChoice> UiScales { get; } = UiScaleChoice.All;

    private UiScaleChoice? _selectedUiScale = UiScaleChoice.Current();

    /// <summary>
    /// The scale in force. Picking one applies it to every open window at once and remembers it
    /// for this machine; like the theme, its effect is its own preview.
    /// </summary>
    public UiScaleChoice? SelectedUiScale
    {
        get => _selectedUiScale;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedUiScale, value);
            if (value is not null) UiScaleService.Apply(value.Scale);
        }
    }

    /// <summary>Follows the scale when the status bar changes it: the guard in Apply makes
    /// arriving at the scale already on a no-op, so the setter re-applying is harmless.</summary>
    private void OnUiScaleChanged() => SelectedUiScale = UiScaleChoice.Current();

    // ── Appearance ────────────────────────────────────────────────────────────

    public IReadOnlyList<ThemeChoice> Themes { get; } = ThemeService.All;

    private ThemeChoice? _selectedTheme =
        ThemeService.All.FirstOrDefault(t => t.Key == ThemeService.Current);

    /// <summary>
    /// The theme, applied the moment it is chosen.
    ///
    /// <para>⚠️ No Apply button and no restart. A theme is the one setting whose effect IS its own
    /// preview, so making somebody confirm a colour scheme they cannot see yet gets the choice
    /// wrong in both directions. Everything bound through the palette repaints live.</para>
    /// </summary>
    public ThemeChoice? SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedTheme, value);
            if (value is not null && value.Key != ThemeService.Current) ThemeService.Apply(value.Key);
        }
    }

    /// <summary>
    /// Follows the theme when it is changed somewhere else — the label on the title bar picks the
    /// same themes, and a combo still naming the old one is just wrong.
    ///
    /// <para>⚠️ Assigning the property is what updates it, and the setter re-applies. The guard
    /// above is on the KEY rather than a flag, so arriving at the theme already on is a no-op
    /// however it got here.</para>
    /// </summary>
    private void OnThemeChanged() =>
        SelectedTheme = ThemeService.All.FirstOrDefault(t => t.Key == ThemeService.Current);

    public string[] EveTimeSiteOptions { get; } =
    [
        UiLinkSettings.EveOnlineTimeUrl,
        UiLinkSettings.NakamuraLabsUrl,
        CustomOption,
    ];

    public OtherSettingsViewModel(UiLinkSettings settings)
    {
        _settings = settings;

        _selectedLanguage = LanguageChoices.FirstOrDefault(c => c.Code == Languages.Chosen)
                         ?? LanguageChoices[0];
        RestartCommand = ReactiveCommand.Create(() =>
        {
            if (AppLauncher.Restart() is { } error)
                RestartError = string.Format(SettingsText.RestartFailed, error);
        });

        ThemeService.Changed   += OnThemeChanged;
        UiScaleService.Changed += OnUiScaleChanged;

        var stored = settings.EveTimeUrl;
        var isPreset = stored == UiLinkSettings.EveOnlineTimeUrl
                    || stored == UiLinkSettings.NakamuraLabsUrl;

        _selectedEveTimeSite = isPreset ? stored : CustomOption;
        _customEveTimeUrl    = isPreset ? "" : stored;

        _loading = false;
    }

    private string _selectedEveTimeSite;
    public string SelectedEveTimeSite
    {
        get => _selectedEveTimeSite;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedEveTimeSite, value);
            this.RaisePropertyChanged(nameof(IsCustomSelected));
            Apply();
        }
    }

    public bool IsCustomSelected => SelectedEveTimeSite == CustomOption;

    private string _customEveTimeUrl;
    public string CustomEveTimeUrl
    {
        get => _customEveTimeUrl;
        set { this.RaiseAndSetIfChanged(ref _customEveTimeUrl, value); Apply(); }
    }

    /// <summary>What clicking the clock will actually open, so the tab can show it back
    /// rather than leaving the user to infer it from two controls.</summary>
    public string EffectiveUrl => IsCustomSelected
        ? (string.IsNullOrWhiteSpace(CustomEveTimeUrl) ? UiLinkSettings.EveOnlineTimeUrl : CustomEveTimeUrl.Trim())
        : SelectedEveTimeSite;

    /// <summary>The same, as the sentence under the controls.</summary>
    public string EffectiveUrlText => string.Format(SettingsText.EveTimeLinkEffective, EffectiveUrl);

    private void Apply()
    {
        if (_loading) return;
        _settings.EveTimeUrl = EffectiveUrl;
        this.RaisePropertyChanged(nameof(EffectiveUrl));
        this.RaisePropertyChanged(nameof(EffectiveUrlText));
    }
}
