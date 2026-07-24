using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Services;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveConsole.ViewModels;

// One selectable value in a multi-select filter column.
public class StructureFilterOption : ReactiveObject
{
    public long   Key   { get; }
    public string Label { get; }
    private readonly Action _onChanged;

    public StructureFilterOption(long key, string label, Action onChanged)
    {
        Key = key; Label = label; _onChanged = onChanged;
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { this.RaiseAndSetIfChanged(ref _isSelected, value); _onChanged(); }
    }
}

public class StructureRow
{
    public long   StructureId  { get; init; }
    public string Name         { get; init; } = "";
    public string SystemName   { get; init; } = "";
    public string Constellation{ get; init; } = "";
    public string Region       { get; init; } = "";
    public string CorpName     { get; init; } = "";
    public string AllianceName { get; init; } = "";
    public string TypeName     { get; init; } = "";
    public string StatusText   { get; init; } = "";
    public string Coordinates  { get; init; } = "";
    public string NearestCelestial { get; init; } = "";

    // Filter keys (0 = unknown/none).
    public long CorpId          { get; init; }
    public long AllianceId      { get; init; }
    public long SystemId        { get; init; }
    public long ConstellationId { get; init; }
    public long RegionId        { get; init; }
    public long TypeId          { get; init; }
}

public class StructureBrowserViewModel : ReactiveObject
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly EsiPollingService               _polling;
    private readonly Api.EsiClient                   _esi;

    private List<StructureRow> _all = [];

    public ObservableCollection<StructureRow> Rows { get; } = [];

    public ObservableCollection<StructureFilterOption> CorpFilters      { get; } = [];
    public ObservableCollection<StructureFilterOption> AllianceFilters  { get; } = [];
    public ObservableCollection<StructureFilterOption> SystemFilters    { get; } = [];
    public ObservableCollection<StructureFilterOption> ConstellationFilters { get; } = [];
    public ObservableCollection<StructureFilterOption> RegionFilters    { get; } = [];
    public ObservableCollection<StructureFilterOption> TypeFilters      { get; } = [];

    private string _status = "";
    public string Status { get => _status; set => this.RaiseAndSetIfChanged(ref _status, value); }

    private bool _busy;
    public bool Busy { get => _busy; set => this.RaiseAndSetIfChanged(ref _busy, value); }

    public ReactiveCommand<Unit, Unit> RefreshCommand   { get; }
    public ReactiveCommand<Unit, Unit> ResolveCommand   { get; }
    public ReactiveCommand<Unit, Unit> ClearFilters     { get; }

    public StructureBrowserViewModel(IDbContextFactory<AppDbContext> dbFactory, EsiPollingService polling, Api.EsiClient esi)
    {
        _dbFactory = dbFactory;
        _polling   = polling;
        _esi       = esi;

        RefreshCommand = ReactiveCommand.CreateFromTask(LoadAsync);
        ResolveCommand = ReactiveCommand.CreateFromTask(ResolveAsync);
        ClearFilters   = ReactiveCommand.Create(() =>
        {
            foreach (var f in AllFilters()) f.IsSelected = false;
        });

        _ = LoadAsync();
    }

    private IEnumerable<StructureFilterOption> AllFilters() =>
        CorpFilters.Concat(AllianceFilters).Concat(SystemFilters)
                   .Concat(ConstellationFilters).Concat(RegionFilters).Concat(TypeFilters);

    private static string StatusLabel(int status) => (StructureStatus)status switch
    {
        StructureStatus.Resolved => "OK",
        StructureStatus.NoAccess => "No access",
        StructureStatus.NotFound => "Gone (404)",
        _                        => "Pending",
    };

    private async Task LoadAsync()
    {
        Busy = true;
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            var structs = await db.EsiStructureNames.AsNoTracking().ToListAsync();

            var sys   = await db.SdeSolarSystems.AsNoTracking()
                .ToDictionaryAsync(s => s.SolarSystemId, s => new { s.Name, s.ConstellationId, s.RegionId });
            var cons  = await db.SdeConstellations.AsNoTracking().ToDictionaryAsync(c => c.ConstellationId, c => c.Name);
            var regs  = await db.SdeRegions.AsNoTracking().ToDictionaryAsync(r => r.RegionId, r => r.Name);
            var allTypeIds = structs.Where(s => s.TypeId > 0).Select(s => s.TypeId).Distinct().ToList();
            var typeNames = await db.SdeTypes.AsNoTracking()
                .Where(t => allTypeIds.Contains(t.TypeId))
                .ToDictionaryAsync(t => t.TypeId, t => t.Name);

            var nameIds = structs.SelectMany(s => new[] { s.OwnerId, s.AllianceId })
                                 .Where(id => id > 0).Distinct().ToList();
            var names = await db.UniverseNames.AsNoTracking()
                .Where(u => nameIds.Contains(u.EntityId))
                .ToDictionaryAsync(u => u.EntityId, u => u.Name);

            // Resolve any corp/alliance names we don't have yet (immutable — cache them).
            var missing = nameIds.Where(id => !names.ContainsKey(id)).ToList();
            if (missing.Count > 0)
            {
                try
                {
                    var resolved = await _esi.GetNamesAsync(missing);
                    var toAdd = resolved
                        .Where(r => !names.ContainsKey(r.Id))
                        .GroupBy(r => r.Id).Select(g => g.First())
                        .Select(r => new UniverseName { EntityId = r.Id, Name = r.Name, Category = r.Category })
                        .ToList();
                    if (toAdd.Count > 0)
                    {
                        db.UniverseNames.AddRange(toAdd);
                        await db.SaveChangesAsync();
                        foreach (var u in toAdd) names[u.EntityId] = u.Name;
                    }
                }
                catch { /* names are best-effort; fall back to "Corp {id}" labels */ }
            }

            string CorpN(long id)  => id > 0 ? names.GetValueOrDefault(id, $"Corp {id}") : "";
            string AllyN(long id)  => id > 0 ? names.GetValueOrDefault(id, $"Alliance {id}") : "";

            _all = structs.Select(s =>
            {
                sys.TryGetValue(s.SolarSystemId, out var sinfo);
                long constId = sinfo?.ConstellationId ?? 0;
                long regId   = sinfo?.RegionId ?? 0;
                return new StructureRow
                {
                    StructureId     = s.StructureId,
                    Name            = string.IsNullOrEmpty(s.Name) ? $"Structure {s.StructureId}" : s.Name,
                    SystemName      = sinfo?.Name ?? "",
                    Constellation   = constId > 0 ? cons.GetValueOrDefault((int)constId, "") : "",
                    Region          = regId > 0 ? regs.GetValueOrDefault((int)regId, "") : "",
                    CorpName        = CorpN(s.OwnerId),
                    AllianceName    = AllyN(s.AllianceId),
                    TypeName        = s.TypeId > 0 ? typeNames.GetValueOrDefault(s.TypeId, $"Type {s.TypeId}") : "",
                    StatusText      = StatusLabel(s.Status),
                    Coordinates     = (s.X == 0 && s.Y == 0 && s.Z == 0)
                                        ? "" : $"{s.X:N0}, {s.Y:N0}, {s.Z:N0}",
                    NearestCelestial= s.NearestCelestial,
                    CorpId          = s.OwnerId,
                    AllianceId      = s.AllianceId,
                    SystemId        = s.SolarSystemId,
                    ConstellationId = constId,
                    RegionId        = regId,
                    TypeId          = s.TypeId,
                };
            }).ToList();

            BuildFilters();
            ApplyFilters();
            Status = $"{_all.Count} structure(s).";
        }
        catch (Exception ex) { Status = $"Load failed: {ex.Message}"; }
        finally { Busy = false; }
    }

    private void BuildFilters()
    {
        void Fill(ObservableCollection<StructureFilterOption> col,
                  Func<StructureRow, long> key, Func<StructureRow, string> label)
        {
            var selected = col.Where(o => o.IsSelected).Select(o => o.Key).ToHashSet();
            col.Clear();
            var items = _all.Where(r => key(r) > 0)
                            .GroupBy(key)
                            .Select(g => (Key: g.Key, Label: label(g.First())))
                            .Where(x => !string.IsNullOrEmpty(x.Label))
                            .OrderBy(x => x.Label);
            foreach (var (k, lbl) in items)
                col.Add(new StructureFilterOption(k, lbl, ApplyFilters) { IsSelected = selected.Contains(k) });
        }

        Fill(CorpFilters,          r => r.CorpId,          r => r.CorpName);
        Fill(AllianceFilters,      r => r.AllianceId,      r => r.AllianceName);
        Fill(SystemFilters,        r => r.SystemId,        r => r.SystemName);
        Fill(ConstellationFilters, r => r.ConstellationId, r => r.Constellation);
        Fill(RegionFilters,        r => r.RegionId,        r => r.Region);
        Fill(TypeFilters,          r => r.TypeId,          r => r.TypeName);
    }

    private void ApplyFilters()
    {
        HashSet<long> Sel(ObservableCollection<StructureFilterOption> col) =>
            col.Where(o => o.IsSelected).Select(o => o.Key).ToHashSet();

        var corp = Sel(CorpFilters);       var ally = Sel(AllianceFilters);
        var sysS = Sel(SystemFilters);     var conS = Sel(ConstellationFilters);
        var regS = Sel(RegionFilters);     var typS = Sel(TypeFilters);

        bool Match(StructureRow r) =>
            (corp.Count == 0 || corp.Contains(r.CorpId)) &&
            (ally.Count == 0 || ally.Contains(r.AllianceId)) &&
            (sysS.Count == 0 || sysS.Contains(r.SystemId)) &&
            (conS.Count == 0 || conS.Contains(r.ConstellationId)) &&
            (regS.Count == 0 || regS.Contains(r.RegionId)) &&
            (typS.Count == 0 || typS.Contains(r.TypeId));

        Rows.Clear();
        foreach (var r in _all.Where(Match).OrderBy(r => r.Region).ThenBy(r => r.SystemName).ThenBy(r => r.Name))
            Rows.Add(r);
        Status = $"{Rows.Count} of {_all.Count} structure(s).";
    }

    private async Task ResolveAsync()
    {
        Busy = true;
        Status = "Resolving structures via ESI…";
        try
        {
            await _polling.ForceResolveStructureNamesAsync();
            await LoadAsync();
        }
        catch (Exception ex) { Status = $"Resolve failed: {ex.Message}"; }
        finally { Busy = false; }
    }
}
