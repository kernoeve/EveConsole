using System.Collections.ObjectModel;
using Avalonia.Threading;
using EveConsole.Data;
using EveConsole.Localization;
using EveConsole.Services;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>One line of the cargo a courier contract is being recorded with.</summary>
public sealed class ContractedLineVm : ReactiveObject
{
    private readonly ContractedCargoVm _owner;

    public int    TypeId   { get; }
    public string TypeName { get; }

    private double _unitM3;
    public double UnitM3
    {
        get => _unitM3;
        internal set { _unitM3 = value; this.RaisePropertyChanged(nameof(VolumeText)); }
    }

    private decimal? _quantity;
    /// <summary>Cleared or zero leaves the line out.</summary>
    public decimal? Quantity
    {
        get => _quantity;
        set
        {
            if (value == _quantity) return;
            this.RaiseAndSetIfChanged(ref _quantity, value);
            this.RaisePropertyChanged(nameof(VolumeText));
            _owner.Totals();
        }
    }

    public long Units => (long)Math.Max(0, _quantity ?? 0);

    public string VolumeText => CourierCargoRowVm.M3(Units * _unitM3);

    public void OpenItem() => EntityNavigator.Instance.Item(TypeId);

    public ContractedLineVm(ContractedCargoVm owner, int typeId, string typeName, long quantity, double unitM3)
    {
        _owner    = owner;
        TypeId    = typeId;
        TypeName  = typeName;
        _quantity = quantity;
        _unitM3   = unitM3;
    }
}

/// <summary>
/// The cargo a hauling-service trip is ticked off with, before it is recorded: the trip's manifest
/// to start from, corrected to what actually went into the courier contract — a quantity changed,
/// a line taken off, or the lot replaced by the contract's items pasted from the game.
///
/// <para>⚠️ Nothing is saved here. The dialog closes with <see cref="Cargo"/> and the worklist
/// records that; cancelled, nothing is recorded and the trip stays on the list. Asked at the tick
/// because that is when the reader knows what went in — the contract itself only reaches the
/// Contracts tool, where its cargo can also be edited, a contracts poll later.</para>
/// </summary>
public sealed class ContractedCargoVm : ReactiveObject
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public ObservableCollection<ContractedLineVm> Rows { get; } = [];
    public bool HasRows => Rows.Any(r => r.Units > 0);

    /// <summary>Where the trip goes from and to, for the dialog's heading.</summary>
    public string Route { get; }

    /// <summary>The most one contract may hold on this trip's lane.</summary>
    public double LimitM3 { get; }

    private string _pasteText = "";
    public string PasteText { get => _pasteText; set => this.RaiseAndSetIfChanged(ref _pasteText, value); }

    private string _message = "";
    public string Message { get => _message; private set => this.RaiseAndSetIfChanged(ref _message, value); }

    private string _totalsText = "";
    public string TotalsText { get => _totalsText; private set => this.RaiseAndSetIfChanged(ref _totalsText, value); }

    private bool _isOver;
    /// <summary>More listed than one contract may hold: shown, not refused — the limit is ours,
    /// and a service that took more is the reader's to know.</summary>
    public bool IsOver { get => _isOver; private set => this.RaiseAndSetIfChanged(ref _isOver, value); }

    /// <summary>What to record: every line with a quantity.</summary>
    public IReadOnlyList<(int TypeId, long Quantity)> Cargo =>
        [.. Rows.Where(r => r.Units > 0).Select(r => (r.TypeId, r.Units))];

    public ContractedCargoVm(IDbContextFactory<AppDbContext> dbFactory, string route, double limitM3,
                             IEnumerable<(int TypeId, string TypeName, long Quantity)> manifest)
    {
        _dbFactory = dbFactory;
        Route      = route;
        LimitM3    = limitM3;
        foreach (var (typeId, name, qty) in manifest)
            Rows.Add(new ContractedLineVm(this, typeId, name, qty, 0));
        Totals();
        _ = LoadVolumesAsync();
    }

    private async Task LoadVolumesAsync()
    {
        try
        {
            var ids     = Rows.Select(r => r.TypeId).Distinct().ToList();
            var volumes = await Task.Run(() => VolumesAsync(ids));
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var r in Rows) r.UnitM3 = volumes.GetValueOrDefault(r.TypeId);
                Totals();
            });
        }
        catch (Exception ex) { Message = AppErrorLogger.Line(MarketText.CourierCargoFailed, ex); }
    }

    private async Task<Dictionary<int, double>> VolumesAsync(List<int> ids)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.SdeTypes.AsNoTracking().Where(t => ids.Contains(t.TypeId))
            .Select(t => new { t.TypeId, t.Volume, t.PackagedVolume })
            .ToDictionaryAsync(t => t.TypeId, t => t.PackagedVolume > 0 ? t.PackagedVolume : t.Volume);
    }

    internal void Totals()
    {
        var m3 = Rows.Sum(r => r.Units * r.UnitM3);
        IsOver = LimitM3 > 0 && m3 > LimitM3;
        TotalsText = IsOver
            ? string.Format(WorklistText.ContractedTotalsOver, CourierCargoRowVm.M3(m3), CourierCargoRowVm.M3(LimitM3))
            : string.Format(WorklistText.ContractedTotals, CourierCargoRowVm.M3(m3));
        this.RaisePropertyChanged(nameof(HasRows));
    }

    public void Remove(ContractedLineVm row)
    {
        Rows.Remove(row);
        Totals();
    }

    /// <summary>The pasted lines added to the list — an item already on it gets the quantities summed.</summary>
    public Task AddPastedAsync() => ApplyPastedAsync(replace: false);

    /// <summary>The pasted lines in place of the whole list.</summary>
    public Task ReplacePastedAsync() => ApplyPastedAsync(replace: true);

    private async Task ApplyPastedAsync(bool replace)
    {
        var lines = CourierCargoStore.Parse(PasteText);
        if (lines.Count == 0) { Message = MarketText.CourierCargoNothingPasted; return; }
        try
        {
            var (ids, names, volumes) = await Task.Run(async () =>
            {
                await using var db = await _dbFactory.CreateDbContextAsync();
                var found = await CourierCargoStore.ResolveAsync(db, lines.Select(l => l.Name));
                var types = found.Values.Distinct().ToList();
                var rows  = await db.SdeTypes.AsNoTracking().Where(t => types.Contains(t.TypeId))
                    .Select(t => new { t.TypeId, t.Name, t.Volume, t.PackagedVolume }).ToListAsync();
                return (found,
                        rows.ToDictionary(t => t.TypeId, t => SdeNames.Type(t.TypeId, t.Name)),
                        rows.ToDictionary(t => t.TypeId, t => t.PackagedVolume > 0 ? t.PackagedVolume : t.Volume));
            });

            var known   = lines.Where(l => ids.ContainsKey(l.Name)).ToList();
            var unknown = lines.Where(l => !ids.ContainsKey(l.Name)).Select(l => l.Name).ToList();

            // ⚠️ The list kept when nothing was recognised: a replace would empty it.
            if (known.Count > 0)
            {
                if (replace) Rows.Clear();
                foreach (var g in known.GroupBy(l => ids[l.Name]))
                {
                    var qty = g.Sum(l => l.Quantity);
                    if (Rows.FirstOrDefault(r => r.TypeId == g.Key) is { } row)
                        row.Quantity = row.Units + qty;
                    else
                        Rows.Add(new ContractedLineVm(this, g.Key,
                                                      names.GetValueOrDefault(g.Key, string.Format(MarketText.TypeNumbered, g.Key)),
                                                      qty, volumes.GetValueOrDefault(g.Key)));
                }
                Totals();
            }

            Message = unknown.Count == 0
                ? ""
                : string.Format(WorklistText.ContractedUnknown, string.Join(CommonText.ListSeparator, unknown));
            if (unknown.Count == 0) PasteText = "";
        }
        catch (Exception ex) { Message = AppErrorLogger.Line(MarketText.CourierCargoFailed, ex); }
    }
}
