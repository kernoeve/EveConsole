using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Reactive;
using EveConsole.Api;
using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Services;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveConsole.ViewModels;

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
        "closed"       => "#7c7c8a",
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
    public int    TypeId    { get; }

    public bool HasItemLink => TypeId > 0;
    public void OpenItem() => EntityNavigator.Instance.Item(TypeId);

    public ContractItemRowVm(ContractItem it, IReadOnlyDictionary<int, string> typeNames)
    {
        Kind      = it.IsIncluded ? "Offered" : "Requested";
        KindColor = it.IsIncluded ? "#5cb85c" : "#d9877a";
        TypeName  = typeNames.TryGetValue(it.TypeId, out var n) ? n : $"\"Type\" {it.TypeId}";
        TypeId    = it.TypeId;
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

    // ── Party links ───────────────────────────────────────────────────────────
    //
    // The issuer falls back to the issuing corporation exactly as the name does, so the link
    // always opens whoever the text names.
    private readonly long _issuerId;
    private readonly long _assigneeId;
    private readonly long _acceptorId;

    public bool HasIssuerLink   => _issuerId   > 0;
    public bool HasAssigneeLink => _assigneeId > 0;
    public bool HasAcceptorLink => _acceptorId > 0;

    public void OpenIssuer()   => Open(_issuerId);
    public void OpenAssignee() => Open(_assigneeId);
    public void OpenAcceptor() => Open(_acceptorId);

    private static void Open(long id)
    {
        if (id <= 0) return;
        EntityNavigator.Instance.Entity(EntityLinks.KindOf(id), id);
    }

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
        _issuerId   = c.IssuerId != 0 ? c.IssuerId : c.IssuerCorporationId;
        _assigneeId = c.AssigneeId ?? 0;
        _acceptorId = c.AcceptorId ?? 0;

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
    // Truly active right now = outstanding and not past expiry. "closed" rows (dropped off the
    // public list) and past-expiry rows are historical.
    public bool IsActive { get; }

    // ── Links ─────────────────────────────────────────────────────────────────
    //
    // Contents links only where the summary actually starts with an item name; a title, a bare
    // count or "(courier)" names nothing to open.
    public int  ContentsTypeId { get; }
    public bool HasContentsLink => ContentsTypeId > 0;
    public void OpenContents() => EntityNavigator.Instance.Item(ContentsTypeId);

    /// <summary>The issuing character where there is one, otherwise the issuing corporation —
    /// the same fallback the displayed name uses, so the link always matches the text.</summary>
    public bool HasIssuerLink => _issuerLinkId > 0;
    public void OpenIssuer() => EntityNavigator.Instance.Entity(
        EntityLinks.KindOf(_issuerLinkId), _issuerLinkId);

    public bool HasAssigneeLink => AssigneeId is > 0;
    public void OpenAssignee()
    {
        if (AssigneeId is not > 0) return;
        EntityNavigator.Instance.Entity(EntityLinks.KindOf(AssigneeId.Value), AssigneeId.Value);
    }

    private readonly long _issuerLinkId;

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
        IsActive      = c.Status == "outstanding" && (c.DateExpired is null || c.DateExpired > DateTimeOffset.UtcNow);
        TypeLabel     = ContractFmt.TypeLabel(c.Type);
        Status        = ContractFmt.EffectiveStatusLabel(c.Status, c.DateExpired);
        StatusColor   = ContractFmt.EffectiveStatusColor(c.Status, c.DateExpired);
        Region        = region;
        Categories    = categories;

        Issuer   = c.IssuerId != 0 && names.TryGetValue(c.IssuerId, out var iN) && iN.Length > 0
            ? iN
            : (names.TryGetValue(c.IssuerCorporationId, out var icN) && icN.Length > 0 ? icN : $"ID {c.IssuerId}");
        // Matches the name above: the character when one is named, otherwise the corporation.
        _issuerLinkId = c.IssuerId != 0 ? c.IssuerId : c.IssuerCorporationId;
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
        {
            Contents       = $"{Name(included[0])} ×{included[0].Quantity:N0}";
            ContentsTypeId = included[0].TypeId;
        }
        else if (included.Count > 1)
        {
            // The summary leads with the first item's name, so that is what the link opens.
            Contents       = $"{Name(included[0])} +{included.Count - 1} more";
            ContentsTypeId = included[0].TypeId;
        }
        else if (items.Count > 0)
            Contents = $"{items.Count} item(s)";
        else
            Contents = c.Type == "courier" ? "(courier)" : "—";

        SearchText = string.Join(" ",
            new[] { c.Title ?? "" }.Concat(items.Select(Name))).ToLowerInvariant();

        string Name(ContractItem i) => typeNames.TryGetValue(i.TypeId, out var n) ? n : $"\"Type\" {i.TypeId}";
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
    /// <summary>
    /// Concurrent because one resolver instance serves the Contracts tab and the Overview's
    /// notification detail, and the latter formats four notifications at once. As a plain
    /// Dictionary, simultaneous writes corrupted it — and a torn dictionary does not merely throw
    /// once, it goes on losing entries and returning wrong names for the life of the view model.
    /// </summary>
    private readonly ConcurrentDictionary<long, string> _names = new();

    /// <summary>
    /// Only one resolve runs at a time.
    ///
    /// <para>The concurrent dictionary makes each individual write safe; it does not make the
    /// method's check-fetch-store cycle atomic. Two callers still both find an id missing, both
    /// wait on the database and ESI, and both fetch it — which matters here because the
    /// bad-id binary split deliberately spends 400s against ESI's shared error limit, and paying
    /// that twice for the same ids is exactly what the limit exists to discourage. Holding the
    /// gate across the whole cycle means the second caller finds the answer already cached.</para>
    /// </summary>
    private readonly SemaphoreSlim _resolveGate = new(1, 1);

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
        await _resolveGate.WaitAsync();
        try
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

            // 3. ESI for the remainder (int64-capable). universe/names returns 404 for the WHOLE
            // batch if any single id is invalid (a deleted entity), so a naive batch of 1000 blanks
            // everyone when one id is bad. universe/names rejects such a batch with 400 (or 404 on
            // older gateways). Resolve with binary-split fallback: on 400/404, split and recurse to
            // isolate the bad id(s); a genuinely-invalid id (isolated to size 1) is cached with an
            // empty name so it isn't re-fetched. Transient failures (420/5xx/network) skip the chunk
            // without blacklisting, to be retried on a later load.
            var fetch = need.Where(id => !_names.ContainsKey(id)).Distinct().ToList();
            var resolved = new List<UniverseName>();

            async Task ResolveChunkAsync(List<long> chunk)
            {
                if (chunk.Count == 0) return;
                if (chunk.Count > 1000)
                {
                    for (int i = 0; i < chunk.Count; i += 1000)
                        await ResolveChunkAsync(chunk.Skip(i).Take(1000).ToList());
                    return;
                }

                // Splitting bad-id batches generates 400s that count against ESI's shared error
                // limit — back off rather than push over it (bounded: the limit resets ~60s).
                for (int waited = 0; _esi.IsErrorLimitBlocked && waited < 40; waited++)
                    try { await Task.Delay(2000); } catch { return; }

                try
                {
                    foreach (var n in await _esi.GetNamesAsync(chunk))
                    {
                        _names[n.Id] = n.Name;
                        resolved.Add(new UniverseName { EntityId = n.Id, Name = n.Name, Category = n.Category });
                    }
                }
                catch (System.Net.Http.HttpRequestException ex)
                    when (ex.StatusCode is System.Net.HttpStatusCode.NotFound
                                        or System.Net.HttpStatusCode.BadRequest)
                {
                    if (chunk.Count == 1)
                    {
                        // Definitively invalid id (deleted character etc.) — cache empty so we
                        // stop retrying it this session and in future ones.
                        _names[chunk[0]] = "";
                        resolved.Add(new UniverseName { EntityId = chunk[0], Name = "", Category = "_unresolved" });
                        return;
                    }
                    int mid = chunk.Count / 2;
                    await ResolveChunkAsync(chunk.Take(mid).ToList());
                    await ResolveChunkAsync(chunk.Skip(mid).ToList());
                }
                catch (Exception ex) { _errorLogger.Log("ContractNameResolver", "GetNames", ex); }
            }

            await ResolveChunkAsync(fetch);

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
        finally { _resolveGate.Release(); }
    }

    // Resolves moon IDs to names via /universe/moons/{id}/ (universe/names can't) — cached in
    // UniverseNames (category "moon"), one ESI call per new moon.
    public async Task<IReadOnlyDictionary<int, string>> ResolveMoonsAsync(IEnumerable<int> moonIds)
    {
        var ids = moonIds.Where(m => m > 0).Distinct().ToList();
        var result = new Dictionary<int, string>();
        if (ids.Count == 0) return result;

        var longIds = ids.Select(i => (long)i).ToList();
        await using var db = await _dbFactory.CreateDbContextAsync();
        foreach (var kv in await db.UniverseNames.AsNoTracking()
                     .Where(u => longIds.Contains(u.EntityId))
                     .ToDictionaryAsync(u => (int)u.EntityId, u => u.Name))
            result[kv.Key] = kv.Value;

        var fresh = new List<UniverseName>();
        foreach (var m in ids.Where(i => !result.ContainsKey(i)))
        {
            try
            {
                var moon = await _esi.GetMoonAsync(m);
                if (moon is { Name.Length: > 0 })
                {
                    result[m] = moon.Name;
                    fresh.Add(new UniverseName { EntityId = m, Name = moon.Name, Category = "moon" });
                }
            }
            catch (Exception ex) { _errorLogger.Log("ContractNameResolver", "GetMoon", ex); }
        }

        if (fresh.Count > 0)
        {
            try
            {
                // INSERT OR IGNORE rather than read-then-add. The old form asked which ids were
                // already stored and inserted the rest, which is only safe if nothing else writes
                // in between — and something does: two resolvers running together both saw a moon
                // missing and both tried to add it, failing on the primary key a millisecond
                // apart. Letting the database settle it removes the window entirely.
                //
                // Every NOT NULL column is named. PulledAt is the only nullable one, and it is
                // given a value rather than skipped so the row records when it was fetched.
                foreach (var f in fresh.GroupBy(x => x.EntityId).Select(g => g.First()))
                    await db.Database.ExecuteSqlInterpolatedAsync(
                        $"""
                         INSERT INTO "UniverseNames" ("EntityId", "Name", "Category", "PulledAt")
                         VALUES ({f.EntityId}, {f.Name}, {f.Category}, {DateTimeOffset.UtcNow:o}) ON CONFLICT DO NOTHING
                         """);
            }
            catch (Exception ex) { _errorLogger.Log("ContractNameResolver", "PersistMoons", ex); }
        }
        return result;
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

    /// <summary>Which tab is showing. Bound two-way, so selecting a contract from elsewhere can
    /// bring the owned list forward rather than landing on whichever tab was last open.</summary>
    private int _tabIndex;
    public int TabIndex
    {
        get => _tabIndex;
        set => this.RaiseAndSetIfChanged(ref _tabIndex, value);
    }

    /// <summary>
    /// Shows a specific contract: the owned tab, with that row selected so the detail pane fills
    /// in. Used by the Order Tracker-s contract link.
    /// </summary>
    public void SelectById(int contractId)
    {
        TabIndex = 0;
        if (Owned.Rows.FirstOrDefault(r => r.ContractId == contractId) is { } row)
            Owned.SelectedRow = row;
    }

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

    // Reassigned wholesale on each filter/load — a single notification instead of one per row,
    // which is what makes tens of thousands of public contracts bind without stalling the UI.
    private IReadOnlyList<ContractRowVm> _rows = [];
    public IReadOnlyList<ContractRowVm> Rows
    {
        get => _rows;
        private set => this.RaiseAndSetIfChanged(ref _rows, value);
    }

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

        Rows = rows;

        if (SelectedRow is null || !rows.Contains(SelectedRow))
            SelectedRow = rows.FirstOrDefault();
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

// Server-side paged view: filtering, sorting and paging all run against the whole table in the DB,
// so only one page (PageSize rows) is materialised, and issuer names are resolved for just that page.
public class PublicContractsViewModel : ReactiveObject
{
    private const int PageSize = 200;

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly AppErrorLogger                  _errorLogger;
    private readonly ContractNameResolver            _names;
    private bool _initialized;

    private IReadOnlyDictionary<int, List<ContractItem>> _itemsByContract = new Dictionary<int, List<ContractItem>>();
    private IReadOnlyDictionary<int, string> _typeNames = new Dictionary<int, string>();
    private IReadOnlyDictionary<long, string> _partyNames = new Dictionary<long, string>();
    private IReadOnlyDictionary<long, string> _locations = new Dictionary<long, string>();

    // Market-group tree, for root-category resolution and category-filter descendant sets.
    private Dictionary<int, int?>   _parentOf   = new();
    private Dictionary<int, string> _mgName     = new();
    private Dictionary<int, List<int>> _childrenOf = new();
    private Dictionary<string, int> _categoryRootId = new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<ContractRowVm> _rows = [];
    public IReadOnlyList<ContractRowVm> Rows
    {
        get => _rows;
        private set => this.RaiseAndSetIfChanged(ref _rows, value);
    }

    public ObservableCollection<ContractRegionOption> Regions    { get; } = new();
    public ObservableCollection<string>               Categories { get; } = new();
    public IReadOnlyList<string>                       StatusOptions { get; } = ["Active", "Historical", "All"];

    // Matches the Contents column: the title if any, else the first item's type name (included
    // items first, then by record). The subquery is a PK-indexed seek per row, so it's cheap.
    private const string ContentsSortExpr =
        "COALESCE(NULLIF(TRIM(c.\"Title\"), ''), " +
        "(SELECT t.\"Name\" FROM \"EsiContractItems\" i JOIN \"SdeTypes\" t ON t.\"TypeId\" = i.\"TypeId\" " +
        "WHERE i.\"ContractId\" = c.\"ContractId\" ORDER BY i.\"IsIncluded\" DESC, i.\"RecordId\" LIMIT 1), '')";

    // Sort is server-side (whole table), driven by this combo — the grid's own column sort would
    // only reorder the current page, which is the confusing behaviour we're replacing.
    public IReadOnlyList<ContractSortOption> SortOptions { get; } =
    [
        new("Price: low → high",  "CAST(c.\"Price\" AS REAL) ASC, c.\"ContractId\" DESC"),
        new("Price: high → low",  "CAST(c.\"Price\" AS REAL) DESC, c.\"ContractId\" DESC"),
        new("Newest first",       "c.\"DateIssued\" DESC"),
        new("Oldest first",       "c.\"DateIssued\" ASC"),
        new("Reward: high → low", "CAST(c.\"Reward\" AS REAL) DESC, c.\"ContractId\" DESC"),
        new("Volume: high → low", "CAST(c.\"Volume\" AS REAL) DESC, c.\"ContractId\" DESC"),
        new("Contents (A → Z)",   ContentsSortExpr + " ASC, c.ContractId DESC"),
    ];

    private ContractSortOption _selectedSort;
    public ContractSortOption SelectedSort
    {
        get => _selectedSort;
        set { this.RaiseAndSetIfChanged(ref _selectedSort, value ?? SortOptions[0]); ResetToFirstPageAndReload(); }
    }

    private string _selectedStatus = "Active";
    public string SelectedStatus
    {
        get => _selectedStatus;
        set { this.RaiseAndSetIfChanged(ref _selectedStatus, value ?? "Active"); ResetToFirstPageAndReload(); }
    }

    public IReadOnlyList<string> ContractTypeOptions { get; } =
        ["All types", "Item Exchange", "Auction", "Courier"];

    private string _selectedContractType = "All types";
    public string SelectedContractType
    {
        get => _selectedContractType;
        set { this.RaiseAndSetIfChanged(ref _selectedContractType, value ?? "All types"); ResetToFirstPageAndReload(); }
    }

    private ContractRegionOption? _selectedRegion;
    public ContractRegionOption? SelectedRegion
    {
        get => _selectedRegion;
        set { this.RaiseAndSetIfChanged(ref _selectedRegion, value); ResetToFirstPageAndReload(); }
    }

    private string _selectedCategory = "All categories";
    public string SelectedCategory
    {
        get => _selectedCategory;
        set { this.RaiseAndSetIfChanged(ref _selectedCategory, value ?? "All categories"); ResetToFirstPageAndReload(); }
    }

    private string _typeFilter = "";
    public string TypeFilter
    {
        get => _typeFilter;
        set { this.RaiseAndSetIfChanged(ref _typeFilter, value); DebounceReload(); }
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

    // ── Paging ──────────────────────────────────────────────────────────────────
    private int _currentPage = 1;
    public int CurrentPage
    {
        get => _currentPage;
        private set { this.RaiseAndSetIfChanged(ref _currentPage, value); RaisePaging(); }
    }

    private int _totalCount;
    public int TotalCount
    {
        get => _totalCount;
        private set { this.RaiseAndSetIfChanged(ref _totalCount, value); RaisePaging(); }
    }

    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
    public bool CanPrev => CurrentPage > 1;
    public bool CanNext => CurrentPage < TotalPages;
    public string PageInfo => TotalCount == 0
        ? "No results"
        : $"Page {CurrentPage:N0} of {TotalPages:N0}  ·  {TotalCount:N0} contracts";

    private void RaisePaging()
    {
        this.RaisePropertyChanged(nameof(TotalPages));
        this.RaisePropertyChanged(nameof(CanPrev));
        this.RaisePropertyChanged(nameof(CanNext));
        this.RaisePropertyChanged(nameof(PageInfo));
    }

    private bool _isLoading;
    public bool IsLoading { get => _isLoading; private set => this.RaiseAndSetIfChanged(ref _isLoading, value); }

    private string _statusText = "";
    public string StatusText { get => _statusText; private set => this.RaiseAndSetIfChanged(ref _statusText, value); }

    public ReactiveCommand<Unit, Unit> RefreshCommand      { get; }
    public ReactiveCommand<Unit, Unit> ClearFiltersCommand { get; }
    public ReactiveCommand<Unit, Unit> FirstPageCommand    { get; }
    public ReactiveCommand<Unit, Unit> PrevPageCommand     { get; }
    public ReactiveCommand<Unit, Unit> NextPageCommand     { get; }
    public ReactiveCommand<Unit, Unit> LastPageCommand     { get; }

    public PublicContractsViewModel(
        IDbContextFactory<AppDbContext> dbFactory, AppErrorLogger errorLogger, ContractNameResolver names)
    {
        _dbFactory   = dbFactory;
        _errorLogger = errorLogger;
        _names       = names;
        _selectedSort = SortOptions[0];

        RefreshCommand      = ReactiveCommand.CreateFromTask(ReloadPageAsync);
        ClearFiltersCommand = ReactiveCommand.Create(() =>
        {
            _typeFilter = ""; this.RaisePropertyChanged(nameof(TypeFilter));
            _selectedCategory = "All categories"; this.RaisePropertyChanged(nameof(SelectedCategory));
            ResetToFirstPageAndReload();
        });
        FirstPageCommand = ReactiveCommand.Create(() => GoToPage(1));
        PrevPageCommand  = ReactiveCommand.Create(() => GoToPage(CurrentPage - 1));
        NextPageCommand  = ReactiveCommand.Create(() => GoToPage(CurrentPage + 1));
        LastPageCommand  = ReactiveCommand.Create(() => GoToPage(TotalPages));

        _ = InitAsync();
    }

    private void GoToPage(int page)
    {
        int target = Math.Clamp(page, 1, TotalPages);
        if (target == CurrentPage || !_initialized) return;
        CurrentPage = target;
        _ = ReloadPageAsync();
    }

    private void ResetToFirstPageAndReload()
    {
        if (!_initialized) return;
        _currentPage = 1; this.RaisePropertyChanged(nameof(CurrentPage)); RaisePaging();
        _ = ReloadPageAsync();
    }

    // Coalesce rapid typing into a single reload.
    private int _filterGen;
    private async void DebounceReload()
    {
        if (!_initialized) return;
        int gen = ++_filterGen;
        try { await Task.Delay(350); } catch { return; }
        if (gen == _filterGen) ResetToFirstPageAndReload();
    }

    private async Task InitAsync()
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            var mgs = await db.SdeMarketGroups.AsNoTracking().ToListAsync();
            _parentOf = mgs.ToDictionary(g => g.MarketGroupId, g => g.ParentGroupId);
            _mgName   = mgs.ToDictionary(g => g.MarketGroupId, g => g.Name);
            _childrenOf = mgs.Where(g => g.ParentGroupId is not null)
                .GroupBy(g => g.ParentGroupId!.Value)
                .ToDictionary(gr => gr.Key, gr => gr.Select(g => g.MarketGroupId).ToList());

            Categories.Clear();
            Categories.Add("All categories");
            _categoryRootId.Clear();
            foreach (var g in mgs.Where(g => g.ParentGroupId == null)
                         .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
            {
                Categories.Add(g.Name);
                _categoryRootId[g.Name] = g.MarketGroupId;
            }

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
            await ReloadPageAsync();
        }
        catch (Exception ex)
        {
            _errorLogger.Log("PublicContractsViewModel", "InitAsync", ex);
            StatusText = "Error initialising public contracts.";
        }
    }

    // Builds the WHERE clause + positional parameters ({0},{1}…) from the current filters. Item-based
    // filters (type name, category) use EXISTS against the item table; the category set is a bounded
    // list of the selected root's descendant market-group ids, inlined as safe integer literals.
    private (string Where, object[] Parameters) BuildFilter()
    {
        var parts = new List<string> { "c.\"OwnerType\" = 'public'" };
        var ps    = new List<object>();

        if (_selectedRegion?.RegionId is int rid)
        {
            parts.Add($"c.\"RegionId\" = {{{ps.Count}}}");
            ps.Add(rid);
        }

        var contractType = _selectedContractType switch
        {
            "Item Exchange" => "item_exchange",
            "Auction"       => "auction",
            "Courier"       => "courier",
            _               => null,
        };
        if (contractType is not null)
        {
            parts.Add($"c.\"Type\" = {{{ps.Count}}}");
            ps.Add(contractType);
        }

        // A real date, not a string formatted the way SQLite happens to store one. Postgres has a
        // real timestamptz column and refuses "timestamp with time zone > text"; SQLite binds a
        // DateTimeOffset to exactly this format anyway, so both get what they expect.
        var now = DateTimeOffset.UtcNow;
        if (_selectedStatus == "Active")
        {
            parts.Add($"c.\"Status\" = 'outstanding' AND (c.\"DateExpired\" IS NULL OR c.\"DateExpired\" > {{{ps.Count}}})");
            ps.Add(now);
        }
        else if (_selectedStatus == "Historical")
        {
            parts.Add($"NOT (c.\"Status\" = 'outstanding' AND (c.\"DateExpired\" IS NULL OR c.\"DateExpired\" > {{{ps.Count}}}))");
            ps.Add(now);
        }

        var typeF = _typeFilter.Trim();
        if (typeF.Length > 0)
        {
            parts.Add($"EXISTS (SELECT 1 FROM \"EsiContractItems\" i JOIN \"SdeTypes\" t ON t.\"TypeId\" = i.\"TypeId\" "
                    + $"WHERE i.\"ContractId\" = c.\"ContractId\" AND t.\"Name\" LIKE {{{ps.Count}}})");
            ps.Add($"%{typeF}%");
        }

        if (_selectedCategory is { Length: > 0 } cat && cat != "All categories"
            && _categoryRootId.TryGetValue(cat, out var rootId))
        {
            var ids = DescendantGroupIds(rootId);
            if (ids.Count > 0)
                parts.Add($"EXISTS (SELECT 1 FROM \"EsiContractItems\" i JOIN \"SdeTypes\" t ON t.\"TypeId\" = i.\"TypeId\" "
                        + $"WHERE i.\"ContractId\" = c.\"ContractId\" AND t.\"MarketGroupId\" IN ({string.Join(",", ids)}))");
        }

        return (string.Join(" AND ", parts), ps.ToArray());
    }

    private List<int> DescendantGroupIds(int rootId)
    {
        var result = new List<int>();
        var stack  = new Stack<int>();
        stack.Push(rootId);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            result.Add(id);
            if (_childrenOf.TryGetValue(id, out var kids))
                foreach (var k in kids) stack.Push(k);
        }
        return result;
    }

    private async Task ReloadPageAsync()
    {
        if (!_initialized || IsLoading) return;
        IsLoading = true;
        StatusText = "Loading…";
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            var (where, ps) = BuildFilter();

            // Filter values (type name, region) are passed as parameters via `ps`; only the
            // placeholder string, the fixed sort expression and computed integers are interpolated,
            // so this is not an injection vector despite EF1002.
#pragma warning disable EF1002
            // Count of the WHOLE filtered set (no ORDER BY/LIMIT so EF can wrap it in COUNT).
            int total = await db.EsiContracts
                .FromSqlRaw($"SELECT * FROM \"EsiContracts\" AS c WHERE {where}", ps)
                .AsNoTracking().CountAsync();
            TotalCount = total;

            int pages = Math.Max(1, (int)Math.Ceiling(total / (double)PageSize));
            if (_currentPage > pages) { _currentPage = pages; this.RaisePropertyChanged(nameof(CurrentPage)); RaisePaging(); }
            int offset = (_currentPage - 1) * PageSize;

            // One page, sorted DB-side.
            var contracts = total == 0
                ? new List<ContractRecord>()
                : await db.EsiContracts.FromSqlRaw(
                        $"SELECT * FROM \"EsiContracts\" AS c WHERE {where} " +
                        $"ORDER BY {_selectedSort.Sql} LIMIT {PageSize} OFFSET {offset}", ps)
                    .AsNoTracking().ToListAsync();
#pragma warning restore EF1002

            var cids = contracts.Select(c => c.ContractId).Distinct().ToList();
            var items = cids.Count == 0 ? new List<ContractItem>()
                : await db.EsiContractItems.AsNoTracking().Where(i => cids.Contains(i.ContractId)).ToListAsync();
            _itemsByContract = items.GroupBy(i => i.ContractId).ToDictionary(g => g.Key, g => g.ToList());

            var typeIds = items.Select(i => i.TypeId).Distinct().ToList();
            var types = await db.SdeTypes.Where(t => typeIds.Contains(t.TypeId))
                .Select(t => new { t.TypeId, t.Name, t.MarketGroupId }).ToListAsync();
            _typeNames = types.ToDictionary(t => t.TypeId, t => t.Name);
            var typeCategory = types.ToDictionary(t => t.TypeId, t => RootCategory(t.MarketGroupId));

            var regionIds = contracts.Select(c => c.RegionId).Distinct().ToList();
            var regionNames = await db.SdeRegions.Where(r => regionIds.Contains(r.RegionId))
                .ToDictionaryAsync(r => r.RegionId, r => r.Name);

            // Names only for this page's issuers — cheap, and cached across pages/sessions.
            _partyNames = await _names.ResolveAsync(
                contracts.SelectMany(c => new[] { c.IssuerId, (long)c.IssuerCorporationId }));
            _locations  = await _names.ResolveLocationsAsync(
                contracts.SelectMany(c => new[] { c.StartLocationId ?? 0, c.EndLocationId ?? 0 }));

            var rows = contracts.Select(c =>
            {
                var its = _itemsByContract.TryGetValue(c.ContractId, out var list) ? list : new List<ContractItem>();
                var cats = its.Select(i => typeCategory.TryGetValue(i.TypeId, out var cc) ? cc : "")
                              .Where(s => s.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var region = regionNames.TryGetValue(c.RegionId, out var rn) ? rn : "";
                return new ContractRowVm(c, its, _typeNames, _partyNames, region, cats);
            }).ToList();

            Rows = rows;
            SelectedRow = rows.FirstOrDefault();
            StatusText = "";
        }
        catch (Exception ex)
        {
            _errorLogger.Log("PublicContractsViewModel", "ReloadPageAsync", ex);
            StatusText = "Error loading public contracts.";
        }
        finally { IsLoading = false; }
    }

    // Walks the market-group tree to the top-level ancestor's name (for a row's category tags).
    private string RootCategory(int? marketGroupId)
    {
        if (marketGroupId is not { } id) return "";
        int guard = 0;
        while (_parentOf.TryGetValue(id, out var parent) && parent is { } p && guard++ < 32)
            id = p;
        return _mgName.TryGetValue(id, out var name) ? name : "";
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

public class ContractSortOption
{
    public string Label { get; }
    public string Sql   { get; }   // ORDER BY expression (trusted, not user input)
    public ContractSortOption(string label, string sql) { Label = label; Sql = sql; }
    public override string ToString() => Label;
}
