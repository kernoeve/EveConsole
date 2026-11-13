using System.Collections.ObjectModel;
using Avalonia.Threading;
using EveConsole.Data;
using EveConsole.Localization;
using EveConsole.Services.Worklist;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>One corporation in Config → Structures' list: whether its structures raise tasks.</summary>
public sealed class StructureCorpOption(long id, string name, bool on, Action changed) : ReactiveObject
{
    public long   Id   { get; } = id;
    public string Name { get; } = name;

    private bool _on = on;
    public bool IsOn
    {
        get => _on;
        set { if (value == _on) return; this.RaiseAndSetIfChanged(ref _on, value); changed(); }
    }

    /// <summary>Ticks or clears it without saving: the caller saves the list once.</summary>
    internal void IsOnQuiet(bool value) => this.RaiseAndSetIfChanged(ref _on, value, nameof(IsOn));
}

/// <summary>A choice of what a moon bay is measured by, as the drop-down shows it.</summary>
public sealed record StructurePickupOption(StructurePickupMeasure Measure, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Config → Structures: which corporations' structures raise tasks, which kinds of task, and the
/// levels they are raised at. Saved as it is changed; the list rebuilds a moment after the last change.
/// </summary>
public sealed class WorklistStructuresViewModel : ReactiveObject
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly WorklistSettings _settings;
    private bool _loaded;

    /// <summary>Asks for the list to be rebuilt once the settings settle. Set by the worklist.</summary>
    public Func<Task>? StructuresChanged { get; set; }

    public ObservableCollection<StructureCorpOption> Corps { get; } = [];

    public IReadOnlyList<StructurePickupOption> PickupMeasures { get; } =
    [
        new(StructurePickupMeasure.PercentFull, WorklistText.StructurePickupByPercent),
        new(StructurePickupMeasure.Volume,      WorklistText.StructurePickupByVolume),
        new(StructurePickupMeasure.MarketValue, WorklistText.StructurePickupByValue),
    ];

    public WorklistStructuresViewModel(IDbContextFactory<AppDbContext> dbFactory, WorklistSettings settings)
    {
        _dbFactory = dbFactory;
        _settings  = settings;

        _fuelOn   = settings.RaisesStructureTask(StructureTaskKind.Fuel);
        _pickupOn = settings.RaisesStructureTask(StructureTaskKind.Pickup);
        _ammoOn   = settings.RaisesStructureTask(StructureTaskKind.Ammo);
        _fitOn    = settings.RaisesStructureTask(StructureTaskKind.Fit);
        _minDays  = (decimal)settings.StructureFuelMinDays;
        _fillDays = (decimal)settings.StructureFuelFillDays;
        _pickupAt = (decimal)settings.StructurePickupAt;
        _measure  = PickupMeasures.First(m => m.Measure == settings.StructurePickupBy);
        _allCorps = settings.StructureCorporations is null;

        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        var corps = await Task.Run(async () =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            return await db.Corporations.AsNoTracking()
                .Select(c => new { Id = (long)c.Id, c.Name }).ToListAsync();
        });
        var chosen = _settings.StructureCorporations;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Corps.Clear();
            foreach (var c in corps.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
                Corps.Add(new StructureCorpOption(c.Id, c.Name, chosen is null || chosen.Contains(c.Id), SaveCorps));
            _loaded = true;
        });
    }

    // ── Corporations ─────────────────────────────────────────────────────────

    private bool _allCorps;
    /// <summary>Every corporation, now and any added later; off, only those ticked below.</summary>
    public bool AllCorps
    {
        get => _allCorps;
        set
        {
            if (value == _allCorps) return;
            this.RaiseAndSetIfChanged(ref _allCorps, value);
            // Turned off, the list starts from every one ticked, so nothing drops out unasked.
            if (!value) foreach (var c in Corps) c.IsOnQuiet(true);
            SaveCorps();
        }
    }

    private void SaveCorps()
    {
        if (!_loaded) return;
        var ids = _allCorps ? null : Corps.Where(c => c.IsOn).Select(c => c.Id).ToList();
        Save(() => _settings.SetStructureCorporationsAsync(ids));
    }

    // ── Kinds and levels ─────────────────────────────────────────────────────

    private bool _fuelOn, _pickupOn, _ammoOn, _fitOn;
    public bool FuelOn   { get => _fuelOn;   set => Kind(ref _fuelOn,   value, StructureTaskKind.Fuel); }
    public bool PickupOn { get => _pickupOn; set => Kind(ref _pickupOn, value, StructureTaskKind.Pickup); }
    public bool AmmoOn   { get => _ammoOn;   set => Kind(ref _ammoOn,   value, StructureTaskKind.Ammo); }
    public bool FitOn    { get => _fitOn;    set => Kind(ref _fitOn,    value, StructureTaskKind.Fit); }

    private void Kind(ref bool field, bool value, StructureTaskKind kind, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (field == value) return;
        field = value;
        this.RaisePropertyChanged(name);
        Save(() => _settings.SetRaisesStructureTaskAsync(kind, value));
    }

    private decimal _minDays, _fillDays, _pickupAt;
    public decimal MinFuelDays  { get => _minDays;  set => Number(ref _minDays,  value, WorklistSettings.StructureFuelDaysKey); }
    public decimal FillFuelDays { get => _fillDays; set => Number(ref _fillDays, value, WorklistSettings.StructureFillDaysKey); }
    public decimal PickupAt     { get => _pickupAt; set => Number(ref _pickupAt, value, WorklistSettings.StructurePickupAtKey); }

    private void Number(ref decimal field, decimal value, string key, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        value = Math.Max(0, value);
        if (field == value) return;
        field = value;
        this.RaisePropertyChanged(name);
        var v = (double)value;
        Save(() => _settings.SetStructureNumberAsync(key, v));
    }

    private StructurePickupOption _measure;
    /// <summary>⚠️ A null from the list is ignored: a ComboBox passes one while its items change.</summary>
    public StructurePickupOption? PickupMeasure
    {
        get => _measure;
        set
        {
            if (value is null || value == _measure) return;
            this.RaiseAndSetIfChanged(ref _measure, value);
            this.RaisePropertyChanged(nameof(PickupUnit));
            Save(() => _settings.SetStructurePickupByAsync(value.Measure));
        }
    }

    /// <summary>The unit after the pickup amount: %, m³ or ISK.</summary>
    public string PickupUnit => _measure.Measure switch
    {
        StructurePickupMeasure.MarketValue => "ISK",
        StructurePickupMeasure.Volume      => "m³",
        _                                  => "%",
    };

    private void Save(Func<Task> write)
    {
        _ = Task.Run(async () =>
        {
            await write();
            RefreshSoon();
        });
    }

    private CancellationTokenSource? _pending;

    /// <summary>Rebuilds the list a moment after the last change: setting up is several changes in a row.</summary>
    private void RefreshSoon()
    {
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _pending, cts)?.Cancel();
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(TimeSpan.FromSeconds(2), cts.Token); }
            catch (OperationCanceledException) { return; }
            if (StructuresChanged is { } refresh) await Dispatcher.UIThread.InvokeAsync(refresh);
        });
    }
}
