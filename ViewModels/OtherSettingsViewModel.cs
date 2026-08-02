using EveConsole.Services;
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
    private bool _loading = true;

    public string[] EveTimeSiteOptions { get; } =
    [
        UiLinkSettings.EveOnlineTimeUrl,
        UiLinkSettings.NakamuraLabsUrl,
        CustomOption,
    ];

    public OtherSettingsViewModel(UiLinkSettings settings)
    {
        _settings = settings;

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

    private void Apply()
    {
        if (_loading) return;
        _settings.EveTimeUrl = EffectiveUrl;
        this.RaisePropertyChanged(nameof(EffectiveUrl));
    }
}
