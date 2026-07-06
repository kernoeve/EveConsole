using System.Collections.ObjectModel;
using System.Reactive;
using EveCortex.Api;
using EveCortex.Data;
using EveCortex.Models;
using EveCortex.Services;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveCortex.ViewModels;

// ── Shared formatting helpers ───────────────────────────────────────────────────

internal static class ContractFmt
{
    public static string Isk(decimal v)
    {
        var abs = Math.Abs(v);
        if (abs >= 1_000_000_000_000m) return $"{v / 1_000_000_000_000m:F2}T";
        if (abs >= 1_000_000_000m)     return $"{v / 1_000_000_000m:F2}B";
        if (abs >= 1_000_000m)         return $"{v / 1_000_000m:F2}M";
        if (abs >= 1_000m)             return $"{v / 1_000m:F1}K";
        return $"{v:N0}";
    }

    public static string TypeLabel(string type) => type switch
    {
        "item_exchange" => "Item Exchange",
        "auction"       => "Auction",
        "courier"       => "Courier",
        "loan"          => "Loan",
        ""              => "",
        _               => string.Join(" ", type.Split('_')
                               .Select(w => w.Length > 0 ? char.ToUpperInvariant(w[0]) + w[1..] : w)),
    };

    public static string StatusLabel(string status) =>
        string.Join(" ", status.Split('_')
            .Select(w => w.Length > 0 ? char.ToUpperInvariant(w[0]) + w[1..] : w));

    // A stored status only reflects the last time we saw the contract. Public listings and
    // owned contracts that age off ESI are retained as "outstanding" forever; if their expiry
    // has passed they're really gone, so present them as Expired.
    public static bool IsExpired(string status, DateTimeOffset? expired) =>
        status == "outstanding" && expired is { } e && e < DateTimeOffset.UtcNow;

    public static string EffectiveStatusLabel(string status, DateTimeOffset? expired) =>
        IsExpired(status, expired) ? "Expired" : StatusLabel(status);

    public static string EffectiveStatusColor(string status, DateTimeOffset? expired) =>
        IsExpired(status, expired) ? "#a06a45" : StatusColor(status);

    public static string StatusColor(string status) => status switch
    {
        "outstanding"  => "#5b9bd5",
        "in_progress"  => "#c8a84b",
        "finished"     => "#5cb85c",
        "cancelled"    => "#888899",
        "rejected"     => "#d9534f",
        "failed"       => "#d9534f",
        "deleted"      => "#666677",
        "reversed"     => "#9b78c8",
        _              => "#aab",
    };

    public static string Date(DateTimeOffset? d) =>
        d.HasValue ? d.Value.ToLocalTime().ToString("MMM d, yyyy HH:mm") : "—";
}

// ── Row / detail view-models ────────────────────────────────────────────────────

public class ContractItemRowVm
{
    public string Kind      { get; }     // "Offered" / "Requested"
    public string KindColor { get; }
    public string TypeName  { get; }
    public string Quantity  { get; }
    public string Details   { get; }     // blueprint / singleton notes

    public ContractItemRowVm(ContractItem it, IReadOnlyDictionary<int, string> typeNames)
    {
        Kind      = it.IsIncluded ? "Offered" : "Requested";
        KindColor = it.IsIncluded ? "#5cb85c" : "#d9877a";
        TypeName  = typeNames.TryGetValue(it.TypeId, out var n) ? n : $"Type {it.TypeId}";
        Quantity  = it.Quantity.ToString("N0");

        var notes = new List<string>();
        if (it.IsBlueprintCopy == true || (it.RawQuantity is < -1))
            notes.Add("BPC");
        else if (it.RawQuantity == -1)
            notes.Add("BPO");
        if (it.Runs is > 0) notes.Add($"{it.Runs} runs");
        if (it.MaterialEfficiency is > 0 || it.TimeEfficiency is > 0)
            notes.Add($"ME {it.MaterialEfficiency ?? 0} / TE {it.TimeEfficiency ?? 0}");
        if (it.IsSingleton) notes.Add("assembled");
        Details = string.Join(", ", notes);
    }
}

public class ContractDetailVm
{
    public int    ContractId { get; }
    public string Title      { get; }
    public string TypeLabel  { get; }
    public string Status     { get; }
    public string StatusColor{ get; }
    public string Availability { get; }

    public string Issuer     { get; }
    public string Assignee   { get; }
    public string Acceptor   { get; }

    public string DateIssued    { get; }
    public string DateExpired   { get; }
    public string DateAccepted  { get; }
    public string DateCompleted { get; }

    public string StartLocation { get; }
    public string EndLocation   { get; }

    public string Price      { get; }
    public string Reward     { get; }
    public string Collateral { get; }
    public string Buyout     { get; }
    public string Volume     { get; }

    public bool   HasReward     { get; }
    public bool   HasCollateral { get; }
    public bool   HasBuyout     { get; }
    public bool   IsCourier     { get; }

    public ObservableCollection<ContractItemRowVm> Items { get; } = new();
    public bool HasItems => Items.Count > 0;

    public ContractDetailVm(
        ContractRecord c,
        IReadOnlyList<ContractItem> items,
        IReadOnlyDictionary<int, string> typeNames,
        IReadOnlyDictionary<long, string> names,
        IReadOnlyDictionary<long, string> locations)
    {
        ContractId  = c.ContractId;
        Title       = string.IsNullOrWhiteSpace(c.Title) ? "(no title)" : c.Title!;
        TypeLabel   = ContractFmt.TypeLabel(c.Type);
        Status      = ContractFmt.EffectiveStatusLabel(c.Status, c.DateExpired);
        StatusColor = ContractFmt.EffectiveStatusColor(c.Status, c.DateExpired);
        Availability = c.Availability switch
        {
            "public"   => "Public",
            "personal" => "Private",
            ""         => "",
            _          => ContractFmt.StatusLabel(c.Availability),
        };

        Issuer   = Party(names, c.IssuerId, c.IssuerCorporationId);
        Assignee = c.AssigneeId is > 0 ? Party(names, c.AssigneeId.Value, 0) : "—";
        Acceptor = c.AcceptorId is > 0 ? Party(names, c.AcceptorId.Value, 0) : "—";

        DateIssued    = ContractFmt.Date(c.DateIssued);
        DateExpired   = ContractFmt.Date(c.DateExpired);
        DateAccepted  = ContractFmt.Date(c.DateAccepted);
        DateCompleted = ContractFmt.Date(c.DateCompleted);

        StartLocation = Loc(locations, c.StartLocationId);
        EndLocation   = Loc(locations, c.EndLocationId);

        IsCourier     = c.Type == "courier";
        HasReward     = c.Reward > 0;
        HasCollateral = c.Collateral > 0;
        HasBuyout     = c.Buyout > 0;

        Price      = ContractFmt.Isk(c.Price)      + " ISK";
        Reward     = ContractFmt.Isk(c.Reward)     + " ISK";
        Collateral = ContractFmt.Isk(c.Collateral) + " ISK";
        Buyout     = ContractFmt.Isk(c.Buyout)     + " ISK";
        Volume     = $"{c.Volume:N1} m³";

        foreach (var it in items.OrderByDescending(i => i.IsIncluded)
                                 .ThenBy(i => typeNames.TryGetValue(i.TypeId, out var n) ? n : ""))
            Items.Add(new ContractItemRowVm(it, typeNames));
    }

    private static string Party(IReadOnlyDictionary<long, string> names, long id, int corpId)
    {
        if (id != 0 && names.TryGetValue(id, out var n) && !string.IsNullOrEmpty(n)) return n;
        if (id != 0) return $"ID {id}";
        if (corpId != 0 && names.TryGetValue(corpId, out var cn) && !string.IsNullOrEmpty(cn)) return cn;
        return "—";
    }

    private static string Loc(IReadOnlyDictionary<long, string> locations, long? id) =>
        id is > 0 && locations.TryGetValue(id.Value, out var n) && !string.IsNullOrEmpty(n)
            ? n : (id is > 0 ? $"Location {id}" : "—");
}

public class ContractRowVm
{
    public ContractRecord Record { get; }

    public int    ContractId    { get; }
    public string TypeLabel     { get; }
    public string Status        { get; }
    public string StatusColor   { get; }
    public string Issuer        { get; }
    public string Assignee      { get; }
    public string Acceptor      { get; }
    public string Region        { get; }
    public string Contents      { get; }
    public string DateIssued    { get; }
    public DateTimeOffset DateIssuedRaw { get; }
    public string Price         { get; }
    public decimal PriceRaw     { get; }
    public string Reward        { get; }
    public decimal RewardRaw    { get; }
    public string Volume        { get; }

    // Filter helpers (not bound to the grid).
    public long? AssigneeId { get; }
    public long? AcceptorId { get; }
    public string SearchText { get; }
    public IReadOnlyCollection<string> Categories { get; }

    public ContractRowVm(
        ContractRecord c,
        IReadOnlyList<ContractItem> items,
        IReadOnlyDictionary<int, string> typeNames,
        IReadOnlyDictionary<long, string> names,
        string region,
        IReadOnlyCollection<string> categories)
    {
        Record        = c;
        ContractId    = c.ContractId;
        TypeLabel     = ContractFmt.TypeLabel(c.Type);
        Status        = ContractFmt.EffectiveStatusLabel(c.Status, c.DateExpired);
        StatusColor   = ContractFmt.EffectiveStatusColor(c.Status, c.DateExpired);
        Region        = region;
        Categories    = categories;

        Issuer   = c.IssuerId != 0 && names.TryGetValue(c.IssuerId, out var iN) && iN.Length > 0
            ? iN
            : (names.TryGetValue(c.IssuerCorporationId, out var icN) && icN.Length > 0 ? icN : $"ID {c.IssuerId}");
        AssigneeId = c.AssigneeId;
        AcceptorId = c.AcceptorId;
        Assignee = c.AssigneeId is > 0
            ? (names.TryGetValue(c.AssigneeId.Value, out var aN) && aN.Length > 0 ? aN : $"ID {c.AssigneeId}")
            : "—";
        Acceptor = c.AcceptorId is > 0
            ? (names.TryGetValue(c.AcceptorId.Value, out var cN) && cN.Length > 0 ? cN : $"ID {c.AcceptorId}")
            : "—";

        DateIssuedRaw = c.DateIssued;
        DateIssued    = c.DateIssued.ToLocalTime().ToString("MMM d, HH:mm");
        PriceRaw      = c.Price;
        Price         = c.Price  > 0 ? ContractFmt.Isk(c.Price)  : "—";
        RewardRaw     = c.Reward;
        Reward        = c.Reward > 0 ? ContractFmt.Isk(c.Reward) : "—";
        Volume        = c.Volume > 0 ? $"{c.Volume:N0}" : "—";

        // Contents summary: title if present, else a compact item summary.
        var included = items.Where(i => i.IsIncluded).ToList();
        if (!string.IsNullOrWhiteSpace(c.Title))
            Contents = c.Title!;
        else if (included.Count == 1)
            Contents = $"{Name(included[0])} ×{included[0].Quantity:N0}";
        else if (included.Count > 1)
            Contents = $"{Name(included[0])} +{included.Count - 1} more";
        else if (items.Count > 0)
            Contents = $"{items.Count} item(s)";
        else
            Contents = c.Type == "courier" ? "(courier)" : "—";

        SearchText = string.Join(" ",
            new[] { c.Title ?? "" }.Concat(items.Select(Name))).ToLowerInvariant();

        string Name(ContractItem i) => typeNames.TryGetValue(i.TypeId, out var n) ? n : $"Type {i.TypeId}";
    }
}

public class ContractPartyOption
{
    public string Label { get; }
    public long?  Id    { get; }
    public ContractPartyOption(string label, long? id) { Label = label; Id = id; }
    public override string ToString() => Label;
}

public class ContractOwnerOption
{
    public string  Label     { get; }
    public long?   OwnerId   { get; }
    public string? OwnerType { get; }
    public ContractOwnerOption(string label, long? ownerId, string? ownerType)
    { Label = label; OwnerId = ownerId; OwnerType = ownerType; }
    public override string ToString() => Label;
}

// ── Name / location resolver (shared by both tabs) ──────────────────────────────

public class ContractNameResolver
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly EsiClient                       _esi;
    private readonly AppErrorLogger                  _errorLogger;
    private readonly Dictionary<long, string> _names = new();

    public ContractNameResolver(IDbContextFactory<AppDbContext> dbFactory, EsiClient esi, AppErrorLogger errorLogger)
    {
        _dbFactory   = dbFactory;
        _esi         = esi;
        _errorLogger = errorLogger;
    }

    // Resolves character / corp / alliance IDs to names. Order: in-memory cache → persistent
    // UniverseNames table → local Characters/Corporations → ESI. Names are immutable, so anything
    // fetched from ESI is written to UniverseNames and never fetched again (this session or future).
    public async Task<IReadOnlyDictionary<long, string>> ResolveAsync(IEnumerable<long> ids)
    {
        var need = ids.Where(id => id > 0 && !_names.ContainsKey(id)).Distinct().ToList();
        if (need.Count > 0)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            // 1. Persistent name cache — populated across sessions.
            foreach (var kv in await db.UniverseNames.AsNoTracking()
                         .Where(u => need.Contains(u.EntityId))
                         .ToDictionaryAsync(u => u.EntityId, u => u.Name))
                _names[kv.Key] = kv.Value;

            // 2. Local (authoritative) tables for anything the cache missed.
            var miss = need.Where(id => !_names.ContainsKey(id)).ToList();
            if (miss.Count > 0)
            {
                foreach (var kv in await db.Characters.Where(c => miss.Contains(c.Id))
                             .ToDictionaryAsync(c => c.Id, c => c.Name))
                    _names[kv.Key] = kv.Value;

                var intMiss = miss.Where(id => id <= int.MaxValue).Select(id => (int)id).ToList();
                foreach (var kv in await db.Corporations.Where(c => intMiss.Contains(c.Id))
                             .ToDictionaryAsync(c => (long)c.Id, c => c.Name))
                    _names[kv.Key] = kv.Value;
            }

            // 3. ESI for the remainder (int64-capable), in batches of 1000. A single invalid id
            // fails its whole batch, so those are simply left as "ID n" and retried next time.
            var fetch = need.Where(id => !_names.ContainsKey(id)).Distinct().ToList();
            var resolved = new List<UniverseName>();
            for (int i = 0; i < fetch.Count; i += 1000)
            {
                var batch = fetch.Skip(i).Take(1000).ToList();
                try
                {
                    foreach (var n in await _esi.GetNamesAsync(batch))
                    {
                        _names[n.Id] = n.Name;
                        resolved.Add(new UniverseName { EntityId = n.Id, Name = n.Name, Category = n.Category });
                    }
                }
                catch (Exception ex) { _errorLogger.Log("ContractNameResolver", "GetNames", ex); }
            }

            // Persist newly-resolved names so future sessions skip the ESI round-trip.
            if (resolved.Count > 0)
            {
                try
                {
                    var have = (await db.UniverseNames.AsNoTracking()
                            .Where(u => resolved.Select(r => r.EntityId).Contains(u.EntityId))
                            .Select(u => u.EntityId).ToListAsync())
                        .ToHashSet();
                    var fresh = resolved.Where(r => !have.Contains(r.EntityId))
                        .GroupBy(r => r.EntityId).Select(g => g.First()).ToList();
                    if (fresh.Count > 0)
                    {
                        db.UniverseNames.AddRange(fresh);
                        await db.SaveChangesAsync();
                    }
                }
                catch (Exception ex) { _errorLogger.Log("ContractNameResolver", "PersistNames", ex); }
            }
        }

        var map = new Dictionary<long, string>();
        foreach (var id in ids.Where(id => id > 0).Distinct())
            map[id] = _names.TryGetValue(id, out var n) ? n : "";
        return map;
    }

    // Resolves station / structure location IDs to names.
    public async Task<IReadOnlyDictionary<long, string>> ResolveLocationsAsync(IEnumerable<long> ids)
    {
        var result = new Dictionary<long, string>();
        var idList = ids.Where(id => id > 0).Distinct().ToList();
        if (idList.Count == 0) return result;

        await using var db = await _dbFactory.CreateDbContextAsync();

        var stationIds = idList.Where(id => id < 100_000_000L).Select(id => (int)id).ToList();
        if (stationIds.Count > 0)
            foreach (var kv in await db.SdeStations.Where(s => stationIds.Contains(s.StationId))
                         .ToDictionaryAsync(s => (long)s.StationId, s => s.Name))
                result[kv.Key] = kv.Value;

        var structIds = idList.Where(id => id >= 100_000_000L).ToList();
        if (structIds.Count > 0)
            foreach (var kv in await db.EsiStructureNames.Where(s => structIds.Contains(s.StructureId))
                         .ToDictionaryAsync(s => s.StructureId, s => s.Name))
                result[kv.Key] = kv.Value;

        return result;
    }
}

// ── Parent view-model (hosts the two tabs) ──────────────────────────────────────

public class ContractsViewModel : ReactiveObject
{
    public OwnedContractsViewModel  Owned  { get; }
    public PublicContractsViewModel Public { get; }

    public ContractsViewModel(
        IDbContextFactory<AppDbContext> dbFactory, EsiClient esi, AppErrorLogger errorLogger)
    {
        var names = new ContractNameResolver(dbFactory, esi, errorLogger);
        Owned  = new OwnedContractsViewModel(dbFactory, errorLogger, names);
        Public = new PublicContractsViewModel(dbFactory, errorLogger, names);
    }
}

// ── Tab 1: corporation & personal contracts ─────────────────────────────────────

public class OwnedContractsViewModel : ReactiveObject
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly AppErrorLogger                  _errorLogger;
    private readonly ContractNameResolver            _names;
    private bool _initialized;

    private List<ContractRowVm> _all = [];
    private IReadOnlyDictionary<int, List<ContractItem>> _itemsByContract = new Dictionary<int, List<ContractItem>>();
    private IReadOnlyDictionary<int, string> _typeNames = new Dictionary<int, string>();
    private IReadOnlyDictionary<long, string> _partyNames = new Dictionary<long, string>();
    private IReadOnlyDictionary<long, string> _locations = new Dictionary<long, string>();

    public ObservableCollection<ContractRowVm> Rows { get; } = new();

    public ObservableCollection<ContractOwnerOption> Owners     { get; } = new();
    public ObservableCollection<ContractPartyOption> Assignees  { get; } = new();
    public ObservableCollection<ContractPartyOption> Acceptors  { get; } = new();

    private ContractOwnerOption? _selectedOwner;
    public ContractOwnerOption? SelectedOwner
    {
        get => _selectedOwner;
        set { this.RaiseAndSetIfChanged(ref _selectedOwner, value); ApplyFilter(); }
    }

    private ContractPartyOption? _selectedAssignee;
    public ContractPartyOption? SelectedAssignee
    {
        get => _selectedAssignee;
        set { this.RaiseAndSetIfChanged(ref _selectedAssignee, value); ApplyFilter(); }
    }

    private ContractPartyOption? _selectedAcceptor;
    public ContractPartyOption? SelectedAcceptor
    {
        get => _selectedAcceptor;
        set { this.RaiseAndSetIfChanged(ref _selectedAcceptor, value); ApplyFilter(); }
    }

    private ContractRowVm? _selectedRow;
    public ContractRowVm? SelectedRow
    {
        get => _selectedRow;
        set { this.RaiseAndSetIfChanged(ref _selectedRow, value); BuildDetail(); }
    }

    private ContractDetailVm? _detail;
    public ContractDetailVm? Detail
    {
        get => _detail;
        private set => this.RaiseAndSetIfChanged(ref _detail, value);
    }

    private bool _isLoading;
    public bool IsLoading { get => _isLoading; private set => this.RaiseAndSetIfChanged(ref _isLoading, value); }

    private string _statusText = "";
    public string StatusText { get => _statusText; private set => this.RaiseAndSetIfChanged(ref _statusText, value); }

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }

    public OwnedContractsViewModel(
        IDbContextFactory<AppDbContext> dbFactory, AppErrorLogger errorLogger, ContractNameResolver names)
    {
        _dbFactory   = dbFactory;
        _errorLogger = errorLogger;
        _names       = names;
        RefreshCommand = ReactiveCommand.CreateFromTask(LoadAsync);
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        StatusText = "Loading contracts…";
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            var contracts = await db.EsiContracts.AsNoTracking()
                .Where(c => c.OwnerType == "character" || c.OwnerType == "corporation")
                .ToListAsync();

            var cids = contracts.Select(c => c.ContractId).Distinct().ToList();
            var items = await db.EsiContractItems.AsNoTracking()
                .Where(i => cids.Contains(i.ContractId))
                .ToListAsync();
            _itemsByContract = items.GroupBy(i => i.ContractId)
                .ToDictionary(g => g.Key, g => g.ToList());

            var typeIds = items.Select(i => i.TypeId).Distinct().ToList();
            _typeNames = await db.SdeTypes.Where(t => typeIds.Contains(t.TypeId))
                .ToDictionaryAsync(t => t.TypeId, t => t.Name);

            // Owner options from the distinct polled owners.
            var charIds = contracts.Where(c => c.OwnerType == "character").Select(c => c.OwnerId).Distinct().ToList();
            var corpIds = contracts.Where(c => c.OwnerType == "corporation").Select(c => (int)c.OwnerId).Distinct().ToList();
            var charNames = await db.Characters.Where(c => charIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name);
            var corpNames = await db.Corporations.Where(c => corpIds.Contains(c.Id)).ToDictionaryAsync(c => (long)c.Id, c => $"{c.Name} [{c.Ticker}]");

            // Resolve party names (issuer / assignee / acceptor / issuer corp).
            var partyIds = contracts.SelectMany(c => new[]
            {
                c.IssuerId, c.AssigneeId ?? 0, c.AcceptorId ?? 0, (long)c.IssuerCorporationId,
            });
            _partyNames = await _names.ResolveAsync(partyIds);

            _locations = await _names.ResolveLocationsAsync(
                contracts.SelectMany(c => new[] { c.StartLocationId ?? 0, c.EndLocationId ?? 0 }));

            _all = contracts
                .Select(c => new ContractRowVm(
                    c,
                    _itemsByContract.TryGetValue(c.ContractId, out var its) ? its : [],
                    _typeNames, _partyNames, "", []))
                .ToList();

            // Build combos.
            Owners.Clear();
            Owners.Add(new ContractOwnerOption("All owners", null, null));
            foreach (var kv in charNames.OrderBy(k => k.Value))
                Owners.Add(new ContractOwnerOption(kv.Value, kv.Key, "character"));
            foreach (var kv in corpNames.OrderBy(k => k.Value))
                Owners.Add(new ContractOwnerOption(kv.Value, kv.Key, "corporation"));

            BuildPartyCombo(Assignees, _all.Select(r => r.AssigneeId), "All assignees");
            BuildPartyCombo(Acceptors, _all.Select(r => r.AcceptorId), "All acceptors");

            _selectedOwner    = Owners.FirstOrDefault();    this.RaisePropertyChanged(nameof(SelectedOwner));
            _selectedAssignee = Assignees.FirstOrDefault(); this.RaisePropertyChanged(nameof(SelectedAssignee));
            _selectedAcceptor = Acceptors.FirstOrDefault(); this.RaisePropertyChanged(nameof(SelectedAcceptor));

            _initialized = true;
            ApplyFilter();
            StatusText = _all.Count == 0 ? "No corporation or personal contracts stored yet." : "";
        }
        catch (Exception ex)
        {
            _errorLogger.Log("OwnedContractsViewModel", "LoadAsync", ex);
            StatusText = "Error loading contracts.";
        }
        finally { IsLoading = false; }
    }

    private void BuildPartyCombo(ObservableCollection<ContractPartyOption> target,
        IEnumerable<long?> ids, string allLabel)
    {
        target.Clear();
        target.Add(new ContractPartyOption(allLabel, null));
        foreach (var id in ids.Where(i => i is > 0).Select(i => i!.Value).Distinct()
                     .OrderBy(i => _partyNames.TryGetValue(i, out var n) && n.Length > 0 ? n : $"ID {i}"))
        {
            var label = _partyNames.TryGetValue(id, out var n) && n.Length > 0 ? n : $"ID {id}";
            target.Add(new ContractPartyOption(label, id));
        }
    }

    private void ApplyFilter()
    {
        if (!_initialized) return;

        IEnumerable<ContractRowVm> q = _all;

        if (SelectedOwner?.OwnerId is { } oid && SelectedOwner.OwnerType is { } ot)
            q = q.Where(r => r.Record.OwnerId == oid && r.Record.OwnerType == ot);
        else
            q = q.GroupBy(r => r.ContractId).Select(g => g.First());   // de-dupe across owners

        if (SelectedAssignee?.Id is { } aid) q = q.Where(r => r.AssigneeId == aid);
        if (SelectedAcceptor?.Id is { } cid) q = q.Where(r => r.AcceptorId == cid);

        var rows = q.OrderByDescending(r => r.DateIssuedRaw).ToList();

        Rows.Clear();
        foreach (var r in rows) Rows.Add(r);

        if (SelectedRow is null || !Rows.Contains(SelectedRow))
            SelectedRow = Rows.FirstOrDefault();
    }

    private void BuildDetail()
    {
        if (SelectedRow is null) { Detail = null; return; }
        var c = SelectedRow.Record;
        var items = _itemsByContract.TryGetValue(c.ContractId, out var its) ? its : new List<ContractItem>();
        Detail = new ContractDetailVm(c, items, _typeNames, _partyNames, _locations);
    }
}

// ── Tab 2: public contracts ─────────────────────────────────────────────────────

public class PublicContractsViewModel : ReactiveObject
{
    private const int LoadCap = 2000;

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly AppErrorLogger                  _errorLogger;
    private readonly ContractNameResolver            _names;
    private bool _initialized;

    private List<ContractRowVm> _all = [];
    private IReadOnlyDictionary<int, List<ContractItem>> _itemsByContract = new Dictionary<int, List<ContractItem>>();
    private IReadOnlyDictionary<int, string> _typeNames = new Dictionary<int, string>();
    private IReadOnlyDictionary<long, string> _partyNames = new Dictionary<long, string>();
    private IReadOnlyDictionary<long, string> _locations = new Dictionary<long, string>();

    // Market-group tree (child → parent) and names, for root-category resolution.
    private Dictionary<int, int?>  _parentOf = new();
    private Dictionary<int, string> _mgName  = new();

    public ObservableCollection<ContractRowVm> Rows { get; } = new();

    public ObservableCollection<ContractRegionOption> Regions    { get; } = new();
    public ObservableCollection<string>               Categories { get; } = new();

    private ContractRegionOption? _selectedRegion;
    public ContractRegionOption? SelectedRegion
    {
        get => _selectedRegion;
        set { this.RaiseAndSetIfChanged(ref _selectedRegion, value); if (_initialized) _ = LoadAsync(); }
    }

    private string _selectedCategory = "All categories";
    public string SelectedCategory
    {
        get => _selectedCategory;
        set { this.RaiseAndSetIfChanged(ref _selectedCategory, value ?? "All categories"); ApplyFilter(); }
    }

    private string _typeFilter = "";
    public string TypeFilter
    {
        get => _typeFilter;
        set { this.RaiseAndSetIfChanged(ref _typeFilter, value); ApplyFilter(); }
    }

    private ContractRowVm? _selectedRow;
    public ContractRowVm? SelectedRow
    {
        get => _selectedRow;
        set { this.RaiseAndSetIfChanged(ref _selectedRow, value); BuildDetail(); }
    }

    private ContractDetailVm? _detail;
    public ContractDetailVm? Detail
    {
        get => _detail;
        private set => this.RaiseAndSetIfChanged(ref _detail, value);
    }

    private bool _isLoading;
    public bool IsLoading { get => _isLoading; private set => this.RaiseAndSetIfChanged(ref _isLoading, value); }

    private string _statusText = "";
    public string StatusText { get => _statusText; private set => this.RaiseAndSetIfChanged(ref _statusText, value); }

    public ReactiveCommand<Unit, Unit> RefreshCommand    { get; }
    public ReactiveCommand<Unit, Unit> ClearFiltersCommand { get; }

    public PublicContractsViewModel(
        IDbContextFactory<AppDbContext> dbFactory, AppErrorLogger errorLogger, ContractNameResolver names)
    {
        _dbFactory   = dbFactory;
        _errorLogger = errorLogger;
        _names       = names;
        RefreshCommand      = ReactiveCommand.CreateFromTask(LoadAsync);
        ClearFiltersCommand = ReactiveCommand.Create(() =>
        {
            _typeFilter = ""; this.RaisePropertyChanged(nameof(TypeFilter));
            _selectedCategory = "All categories"; this.RaisePropertyChanged(nameof(SelectedCategory));
            ApplyFilter();
        });
        _ = InitAsync();
    }

    private async Task InitAsync()
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            // Market-group tree for root-category resolution + top-level category list.
            var mgs = await db.SdeMarketGroups.AsNoTracking().ToListAsync();
            _parentOf = mgs.ToDictionary(g => g.MarketGroupId, g => g.ParentGroupId);
            _mgName   = mgs.ToDictionary(g => g.MarketGroupId, g => g.Name);

            Categories.Clear();
            Categories.Add("All categories");
            foreach (var name in mgs.Where(g => g.ParentGroupId == null).Select(g => g.Name)
                         .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                Categories.Add(name);

            // Regions that currently have public contracts.
            var regionIds = await db.EsiContracts.Where(c => c.OwnerType == "public")
                .Select(c => c.RegionId).Distinct().ToListAsync();
            var regionNames = await db.SdeRegions.Where(r => regionIds.Contains(r.RegionId))
                .ToDictionaryAsync(r => r.RegionId, r => r.Name);

            Regions.Clear();
            Regions.Add(new ContractRegionOption("All regions", null));
            foreach (var kv in regionNames.OrderBy(k => k.Value))
                Regions.Add(new ContractRegionOption(kv.Value, kv.Key));

            _selectedRegion = Regions.FirstOrDefault();
            this.RaisePropertyChanged(nameof(SelectedRegion));
            _initialized = true;
            await LoadAsync();
        }
        catch (Exception ex)
        {
            _errorLogger.Log("PublicContractsViewModel", "InitAsync", ex);
            StatusText = "Error initialising public contracts.";
        }
    }

    private async Task LoadAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        StatusText = "Loading public contracts…";
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            var query = db.EsiContracts.AsNoTracking().Where(c => c.OwnerType == "public");
            if (SelectedRegion?.RegionId is { } rid)
                query = query.Where(c => c.RegionId == rid);

            // Order by ContractId (ascending IDs ≈ chronological) to avoid a DateTimeOffset sort,
            // newest first, capped for responsiveness.
            var contracts = await query.OrderByDescending(c => c.ContractId).Take(LoadCap).ToListAsync();

            var cids = contracts.Select(c => c.ContractId).Distinct().ToList();
            var items = await db.EsiContractItems.AsNoTracking()
                .Where(i => cids.Contains(i.ContractId))
                .ToListAsync();
            _itemsByContract = items.GroupBy(i => i.ContractId).ToDictionary(g => g.Key, g => g.ToList());

            var typeIds = items.Select(i => i.TypeId).Distinct().ToList();
            var types = await db.SdeTypes.Where(t => typeIds.Contains(t.TypeId))
                .Select(t => new { t.TypeId, t.Name, t.MarketGroupId })
                .ToListAsync();
            _typeNames = types.ToDictionary(t => t.TypeId, t => t.Name);
            var typeCategory = types.ToDictionary(t => t.TypeId, t => RootCategory(t.MarketGroupId));

            var loadedRegionIds = contracts.Select(c => c.RegionId).Distinct().ToList();
            var regionNames = await db.SdeRegions
                .Where(r => loadedRegionIds.Contains(r.RegionId))
                .ToDictionaryAsync(r => r.RegionId, r => r.Name);

            var partyIds = contracts.SelectMany(c => new[] { c.IssuerId, (long)c.IssuerCorporationId });
            _partyNames = await _names.ResolveAsync(partyIds);
            _locations  = await _names.ResolveLocationsAsync(
                contracts.SelectMany(c => new[] { c.StartLocationId ?? 0, c.EndLocationId ?? 0 }));

            _all = contracts.Select(c =>
            {
                var its = _itemsByContract.TryGetValue(c.ContractId, out var list) ? list : new List<ContractItem>();
                var cats = its.Select(i => typeCategory.TryGetValue(i.TypeId, out var cat) ? cat : "")
                              .Where(s => s.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var region = regionNames.TryGetValue(c.RegionId, out var rn) ? rn : "";
                return new ContractRowVm(c, its, _typeNames, _partyNames, region, cats);
            }).ToList();

            ApplyFilter();
            StatusText = contracts.Count >= LoadCap
                ? $"Showing newest {LoadCap:N0} — narrow by region to see more."
                : _all.Count == 0 ? "No public contracts stored for this selection." : "";
        }
        catch (Exception ex)
        {
            _errorLogger.Log("PublicContractsViewModel", "LoadAsync", ex);
            StatusText = "Error loading public contracts.";
        }
        finally { IsLoading = false; }
    }

    // Walks the market-group tree to the top-level ancestor's name.
    private string RootCategory(int? marketGroupId)
    {
        if (marketGroupId is not { } id) return "";
        int guard = 0;
        while (_parentOf.TryGetValue(id, out var parent) && parent is { } p && guard++ < 32)
            id = p;
        return _mgName.TryGetValue(id, out var name) ? name : "";
    }

    private void ApplyFilter()
    {
        if (!_initialized) return;

        var typeF = _typeFilter.Trim();
        var catF  = _selectedCategory;
        bool catAll = string.IsNullOrEmpty(catF) || catF == "All categories";

        var rows = _all.Where(r =>
        {
            if (typeF.Length > 0 && !r.SearchText.Contains(typeF, StringComparison.OrdinalIgnoreCase)) return false;
            if (!catAll && !r.Categories.Contains(catF)) return false;
            return true;
        }).ToList();

        Rows.Clear();
        foreach (var r in rows) Rows.Add(r);

        if (SelectedRow is null || !Rows.Contains(SelectedRow))
            SelectedRow = Rows.FirstOrDefault();
    }

    private void BuildDetail()
    {
        if (SelectedRow is null) { Detail = null; return; }
        var c = SelectedRow.Record;
        var items = _itemsByContract.TryGetValue(c.ContractId, out var its) ? its : new List<ContractItem>();
        Detail = new ContractDetailVm(c, items, _typeNames, _partyNames, _locations);
    }
}

public class ContractRegionOption
{
    public string Label    { get; }
    public int?   RegionId { get; }
    public ContractRegionOption(string label, int? regionId) { Label = label; RegionId = regionId; }
    public override string ToString() => Label;
}
