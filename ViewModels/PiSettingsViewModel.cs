using EveConsole.Localization;
using EveConsole.Services.Pi;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>
/// Planetary Industry settings: the tax rate charged on a planet until one is learned for it from
/// the journal — one for customs offices, one for skyhooks. Saved as they are changed.
/// </summary>
public class PiSettingsViewModel : ReactiveObject
{
    private readonly PiTaxService _tax;
    private readonly AutoSave     _autoSave;

    private decimal _customsTaxPercent;
    private decimal _skyhookTaxPercent;
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

    public string Status
    {
        get => _status;
        private set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    public PiSettingsViewModel(PiTaxService tax)
    {
        _tax      = tax;
        _autoSave = new AutoSave(SaveAsync,
            ex => Status = string.Format(CommonText.ErrorWithMessage, ex.Message));

        // The shared preferences are already in memory, so the values are there at once.
        using (_autoSave.Suspend())
        {
            CustomsTaxPercent = (decimal)tax.CustomsDefaultPercent;
            SkyhookTaxPercent = (decimal)tax.SkyhookDefaultPercent;
        }

        Changed.Subscribe(e => { if (e.PropertyName != nameof(Status) && !_autoSave.IsSuspended) _autoSave.Changed(); });
    }

    /// <summary>Saves a change still waiting — the Settings window, closing.</summary>
    public Task FlushAsync() => _autoSave.FlushAsync();

    private async Task SaveAsync()
    {
        var customs = (double)Math.Clamp(CustomsTaxPercent, 0, 100);
        var skyhook = (double)Math.Clamp(SkyhookTaxPercent, 0, 100);

        await Task.Run(async () =>
        {
            await _tax.SetCustomsDefaultPercentAsync(customs).ConfigureAwait(false);
            await _tax.SetSkyhookDefaultPercentAsync(skyhook).ConfigureAwait(false);
        });

        _autoSave.Flash(s => Status = s, SettingsText.Saved);
    }
}
