using EveConsole.Services;
using System.Globalization;
using System.Reactive;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>
/// Miscellaneous UI preferences that do not belong to any of the data-source tabs.
/// Currently just the destination for clicking the EVE clock.
/// </summary>
public class OtherSettingsViewModel : ReactiveObject
{
    /// <summary>Sentinel entry in the dropdown; anything not matching a preset selects it
    /// and reveals the free-text box.</summary>
    public const string CustomOption = "Custom URL…";

    private readonly UiLinkSettings _settings;
    private readonly AppPreferencesService _prefs;
    private bool _loading = true;

    public const double UiScaleMinimum = UiScaleService.MinimumScale;
    public const double UiScaleMaximum = UiScaleService.MaximumScale;

    public ReactiveCommand<object?, Unit> ApplyUiScaleCommand { get; }

    private double _uiScale = UiScaleService.Scale;
    public double UiScale
    {
        get => _uiScale;
        set
        {
            var scale = Math.Clamp(value, UiScaleMinimum, UiScaleMaximum);
            if (Math.Abs(scale - _uiScale) < 0.001) return;
            this.RaiseAndSetIfChanged(ref _uiScale, scale);
            UiScaleService.SetScale(scale);
            this.RaisePropertyChanged(nameof(UiScaleLabel));
            _ = _prefs.SetAsync(UiScaleService.PreferenceKey,
                                scale.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    public string UiScaleLabel => $"{UiScale:P0}";

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

    public OtherSettingsViewModel(UiLinkSettings settings, AppPreferencesService prefs)
    {
        _settings = settings;
        _prefs = prefs;
        ApplyUiScaleCommand = ReactiveCommand.Create<object?>(ApplyUiScale);

        ThemeService.Changed += OnThemeChanged;

        var stored = settings.EveTimeUrl;
        var isPreset = stored == UiLinkSettings.EveOnlineTimeUrl
                    || stored == UiLinkSettings.NakamuraLabsUrl;

        _selectedEveTimeSite = isPreset ? stored : CustomOption;
        _customEveTimeUrl    = isPreset ? "" : stored;

        _loading = false;
    }

    private void ApplyUiScale(object? value)
    {
        if (value is null) return;

        if (double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture,
                            out var scale))
            UiScale = scale;
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

    private void Apply()
    {
        if (_loading) return;
        _settings.EveTimeUrl = EffectiveUrl;
        this.RaisePropertyChanged(nameof(EffectiveUrl));
    }
}
