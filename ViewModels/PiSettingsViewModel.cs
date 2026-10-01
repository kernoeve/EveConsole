using EveConsole.Localization;
using EveConsole.Services.Pi;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>
/// Planetary Industry settings, on Settings → Industry: the tax rate charged on a planet until one
/// is learned for it from the journal — one for customs offices, one for skyhooks — and how far
/// ahead the PI alerts and worklist tasks look, when a colony's data is too old to trust, and how
/// many days of input a haul brings. Saved as they are changed.
/// </summary>
public class PiSettingsViewModel : ReactiveObject
{
    private readonly PiTaxService _tax;
    private readonly PiSettings   _settings;
    private readonly AutoSave     _autoSave;

    private decimal _customsTaxPercent;
    private decimal _skyhookTaxPercent;
    private decimal _extractorLeadHours;
    private decimal _storageLeadHours;
    private decimal _inputLeadHours;
    private decimal _staleDays;
    private decimal _inputDays;
    private string  _status = "";

    // decimal so Avalonia NumericUpDown binds without a converter
    public decimal CustomsTaxPercent
    {
        get => _customsTaxPercent;
        set => this.RaiseAndSetIfChanged(ref _customsTaxPercent, value);
    }

    public decimal SkyhookTaxPercent
    {
        get => _skyhookTaxPercent;
        set => this.RaiseAndSetIfChanged(ref _skyhookTaxPercent, value);
    }

    public decimal ExtractorLeadHours
    {
        get => _extractorLeadHours;
        set => this.RaiseAndSetIfChanged(ref _extractorLeadHours, value);
    }

    public decimal StorageLeadHours
    {
        get => _storageLeadHours;
        set => this.RaiseAndSetIfChanged(ref _storageLeadHours, value);
    }

    public decimal InputLeadHours
    {
        get => _inputLeadHours;
        set => this.RaiseAndSetIfChanged(ref _inputLeadHours, value);
    }

    public decimal StaleDays
    {
        get => _staleDays;
        set => this.RaiseAndSetIfChanged(ref _staleDays, value);
    }

    public decimal InputDays
    {
        get => _inputDays;
        set => this.RaiseAndSetIfChanged(ref _inputDays, value);
    }

    /// <summary>The longest lead time a box takes: the forecast's own horizon.</summary>
    public decimal MaxLeadHours => PiSettings.MaxLeadHours;

    public string Status
    {
        get => _status;
        private set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    public PiSettingsViewModel(PiTaxService tax, PiSettings settings)
    {
        _tax      = tax;
        _settings = settings;
        _autoSave = new AutoSave(SaveAsync,
            ex => Status = string.Format(CommonText.ErrorWithMessage, ex.Message));

        // The shared preferences are already in memory, so the values are there at once.
        using (_autoSave.Suspend())
        {
            CustomsTaxPercent  = (decimal)tax.CustomsDefaultPercent;
            SkyhookTaxPercent  = (decimal)tax.SkyhookDefaultPercent;
            ExtractorLeadHours = settings.ExtractorLeadHours;
            StorageLeadHours   = settings.StorageLeadHours;
            InputLeadHours     = settings.InputLeadHours;
            StaleDays          = settings.StaleDays;
            InputDays          = settings.InputDays;
        }

        Changed.Subscribe(e => { if (e.PropertyName != nameof(Status) && !_autoSave.IsSuspended) _autoSave.Changed(); });
    }

    /// <summary>Saves a change still waiting — the Settings window, closing.</summary>
    public Task FlushAsync() => _autoSave.FlushAsync();

    private async Task SaveAsync()
    {
        var customs   = (double)Math.Clamp(CustomsTaxPercent, 0, 100);
        var skyhook   = (double)Math.Clamp(SkyhookTaxPercent, 0, 100);
        var extractor = (int)ExtractorLeadHours;
        var storage   = (int)StorageLeadHours;
        var input     = (int)InputLeadHours;
        var stale     = (int)StaleDays;
        var days      = (int)InputDays;

        // Read above, on the UI thread; written off it. Each setter clamps to its own range.
        await Task.Run(async () =>
        {
            await _tax.SetCustomsDefaultPercentAsync(customs).ConfigureAwait(false);
            await _tax.SetSkyhookDefaultPercentAsync(skyhook).ConfigureAwait(false);
            await _settings.SetExtractorLeadHoursAsync(extractor).ConfigureAwait(false);
            await _settings.SetStorageLeadHoursAsync(storage).ConfigureAwait(false);
            await _settings.SetInputLeadHoursAsync(input).ConfigureAwait(false);
            await _settings.SetStaleDaysAsync(stale).ConfigureAwait(false);
            await _settings.SetInputDaysAsync(days).ConfigureAwait(false);
        });

        _autoSave.Flash(s => Status = s, SettingsText.Saved);
    }
}
