using System.Collections.ObjectModel;
using Avalonia.Threading;
using EveConsole.Data;
using EveConsole.Localization;
using EveConsole.Models;
using EveConsole.Services;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>One line of a courier's cargo as entered: its quantity edited in place.</summary>
public sealed class CourierCargoRowVm : ReactiveObject
{
    private readonly CourierCargoVm _owner;
    private CancellationTokenSource? _pending;

    public long   Id       { get; }
    public int    TypeId   { get; }
    public string TypeName { get; }
    public double UnitM3   { get; }

    private decimal? _quantity;
    /// <summary>Saved a moment after the last change; cleared or zero takes the line off.</summary>
    public decimal? Quantity
    {
        get => _quantity;
        set
        {
            if (value == _quantity) return;
            this.RaiseAndSetIfChanged(ref _quantity, value);
            this.RaisePropertyChanged(nameof(VolumeText));
            _owner.Totals();
            var cts = new CancellationTokenSource();
            Interlocked.Exchange(ref _pending, cts)?.Cancel();
            var qty = (long)Math.Max(0, value ?? 0);
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(600, cts.Token); }
                catch (OperationCanceledException) { return; }
                await _owner.SaveQuantityAsync(this, qty);
            });
        }
    }

    public string VolumeText => M3((double)(_quantity ?? 0) * UnitM3);

    internal static string M3(double v) => v >= 10 ? $"{v:N0} m³" : $"{v:N1} m³";

    public void OpenItem() => EntityNavigator.Instance.Item(TypeId);

    public CourierCargoRowVm(CourierCargoVm owner, CourierCargo row, string typeName, double unitM3)
    {
        _owner    = owner;
        Id        = row.Id;
        TypeId    = row.TypeId;
        TypeName  = typeName;
        UnitM3    = unitM3;
        _quantity = row.Quantity;
        _ = ItemIcons.LoadAsync(TypeId, b => Icon = b);
    }

    private Avalonia.Media.Imaging.Bitmap? _icon;
    public Avalonia.Media.Imaging.Bitmap? Icon { get => _icon; private set => this.RaiseAndSetIfChanged(ref _icon, value); }
}

/// <summary>
/// A courier contract's cargo, entered by hand — ESI never lists it (see <see cref="CourierCargo"/>).
/// Shown on our own courier contracts in the Contracts tool: each line's quantity edited in place
/// or taken off, and lines added or put in place of the lot from text pasted out of the game.
/// Saved as it is changed.
/// </summary>
public sealed class CourierCargoVm : ReactiveObject
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ContractRecord _contract;

    public ObservableCollection<CourierCargoRowVm> Rows { get; } = [];
    public bool HasRows => Rows.Count > 0;

    private string _pasteText = "";
    public string PasteText { get => _pasteText; set => this.RaiseAndSetIfChanged(ref _pasteText, value); }

    private string _message = "";
    public string Message { get => _message; private set => this.RaiseAndSetIfChanged(ref _message, value); }

    private string _totalsText = "";
    /// <summary>The entered cargo's volume against the contract's own, a check that nothing is missing.</summary>
    public string TotalsText { get => _totalsText; private set => this.RaiseAndSetIfChanged(ref _totalsText, value); }

    public CourierCargoVm(IDbContextFactory<AppDbContext> dbFactory, ContractRecord contract)
    {
        _dbFactory = dbFactory;
        _contract  = contract;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var (rows, names, volumes) = await Task.Run(async () =>
            {
                var cargo = await CourierCargoStore.ForContractAsync(_dbFactory, _contract.ContractId);
                await using var db = await _dbFactory.CreateDbContextAsync();
                var ids   = cargo.Select(c => c.TypeId).Distinct().ToList();
                var types = await db.SdeTypes.AsNoTracking().Where(t => ids.Contains(t.TypeId))
                    .Select(t => new { t.TypeId, t.Name, t.Volume, t.PackagedVolume }).ToListAsync();
                return (cargo,
                        types.ToDictionary(t => t.TypeId, t => SdeNames.Type(t.TypeId, t.Name)),
                        types.ToDictionary(t => t.TypeId, t => t.PackagedVolume > 0 ? t.PackagedVolume : t.Volume));
            });

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Rows.Clear();
                foreach (var r in rows.OrderBy(r => names.GetValueOrDefault(r.TypeId, ""), StringComparer.CurrentCulture))
                    Rows.Add(new CourierCargoRowVm(this, r, names.GetValueOrDefault(r.TypeId, string.Format(MarketText.TypeNumbered, r.TypeId)),
                                                   volumes.GetValueOrDefault(r.TypeId)));
                this.RaisePropertyChanged(nameof(HasRows));
                Totals();
            });
        }
        catch (Exception ex) { Message = AppErrorLogger.Line(MarketText.CourierCargoFailed, ex); }
    }

    internal void Totals()
    {
        var m3 = Rows.Sum(r => (double)(r.Quantity ?? 0) * r.UnitM3);
        TotalsText = string.Format(MarketText.CourierCargoTotals,
                                   CourierCargoRowVm.M3(m3), CourierCargoRowVm.M3((double)_contract.Volume));
    }

    internal async Task SaveQuantityAsync(CourierCargoRowVm row, long quantity)
    {
        try
        {
            await CourierCargoStore.SetQuantityAsync(_dbFactory, row.Id, quantity);
            if (quantity <= 0) await LoadAsync();
        }
        catch (Exception ex) { Message = AppErrorLogger.Line(MarketText.CourierCargoFailed, ex); }
    }

    public async Task RemoveAsync(CourierCargoRowVm row)
    {
        try
        {
            await Task.Run(() => CourierCargoStore.SetQuantityAsync(_dbFactory, row.Id, 0));
            await LoadAsync();
        }
        catch (Exception ex) { Message = AppErrorLogger.Line(MarketText.CourierCargoFailed, ex); }
    }

    /// <summary>The pasted lines added to the cargo — an item already on it gets the quantities summed.</summary>
    public Task AddPastedAsync() => SavePastedAsync(replace: false);

    /// <summary>The pasted lines in place of everything entered so far.</summary>
    public Task ReplacePastedAsync() => SavePastedAsync(replace: true);

    private async Task SavePastedAsync(bool replace)
    {
        var lines = CourierCargoStore.Parse(PasteText);
        if (lines.Count == 0) { Message = MarketText.CourierCargoNothingPasted; return; }
        try
        {
            var unknown = await Task.Run(async () =>
            {
                await using var db = await _dbFactory.CreateDbContextAsync();
                var ids = await CourierCargoStore.ResolveAsync(db, lines.Select(l => l.Name));
                var known = lines.Where(l => ids.ContainsKey(l.Name)).Select(l => (ids[l.Name], l.Quantity)).ToList();
                // ⚠️ Nothing written when nothing was recognised: a replace would empty the cargo.
                if (known.Count > 0)
                    await CourierCargoStore.AddAsync(_dbFactory, _contract, known, replace);
                return lines.Where(l => !ids.ContainsKey(l.Name)).Select(l => l.Name).ToList();
            });

            Message = unknown.Count == 0
                ? MarketText.CourierCargoSaved
                : string.Format(MarketText.CourierCargoUnknown, string.Join(CommonText.ListSeparator, unknown));
            if (unknown.Count == 0) PasteText = "";
            await LoadAsync();
        }
        catch (Exception ex) { Message = AppErrorLogger.Line(MarketText.CourierCargoFailed, ex); }
    }
}
