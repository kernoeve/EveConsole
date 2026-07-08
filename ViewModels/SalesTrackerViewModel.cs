using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive.Linq;
using EveCortex.Data;
using EveCortex.Models;
using EveCortex.Services;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveCortex.ViewModels;

// One sale on the Sales Tracker grid (a market transaction or a contract sale).
public class SaleRowVm
{
    public DateTimeOffset When { get; }
    public long   WhenSort { get; }
    public string WhenText { get; }
    public string Kind      { get; }   // "Market" or "Contract"
    public string OwnerType { get; }   // "character" or "corporation" (for filtering)
    public long   OwnerId   { get; }
    public bool   OwnerIsPersonal { get; }
    public string Owner    { get; }
    public string Location { get; }
    public string Buyer    { get; }
    public string Items    { get; }
    public string Units    { get; }
    public string Total  { get; } public double TotalRaw  { get; }
    public string Build  { get; } public double BuildRaw  { get; }
    public string Market { get; } public double MarketRaw { get; }
    public string Profit    { get; } public double ProfitRaw    { get; }
    public string ProfitPct { get; } public double ProfitPctRaw { get; }

    public SaleRowVm(DateTimeOffset when, string kind, string ownerType, long ownerId, bool ownerIsPersonal,
        string owner, string location, string buyer,
        string items, string units, double total, double? build, double? market)
    {
        When     = when;
        WhenSort = when.UtcTicks;
        WhenText = when.UtcDateTime.ToString("yyyy-MM-dd HH:mm");
        Kind      = kind;
        OwnerType = ownerType;
        OwnerId   = ownerId;
        OwnerIsPersonal = ownerIsPersonal;
        Owner    = owner;
        Location = location;
        Buyer    = buyer;
        Items    = items;
        Units    = units;
        TotalRaw  = total;      Total  = MarketFmt.Isk(total);
        BuildRaw  = build  ?? 0; Build  = build  is double b ? MarketFmt.Isk(b) : "—";
        MarketRaw = market ?? 0; Market = market is double m ? MarketFmt.Isk(m) : "—";

        // Profit is measured against build cost (sale price − build cost).
        var profit = build is double bc ? total - bc : (double?)null;
        ProfitRaw = profit ?? double.MinValue;
        Profit    = profit is double p ? MarketFmt.Isk(p) : "—";
        var pct = build is double bc2 && bc2 != 0 ? (total - bc2) / bc2 * 100 : (double?)null;
        ProfitPctRaw = pct ?? double.MinValue;
        ProfitPct    = pct is double pp ? $"{pp:N1}%" : "—";
    }
}

public enum OwnerScope { All, CharsAndPersonalCorps, Specific }
public record SalesOwnerOption(string Label, OwnerScope Scope, long OwnerId = 0, string OwnerType = "")
{ public override string ToString() => Label; }
public record SalesTypeOption(string Label, string? Kind)
{ public override string ToString() => Label; }

// Sales Tracker — lists market sales (wallet transactions) and contract sales (item_exchange
// contracts sold for ISK), with build/market value pulled from TypePriceSnapshots (nearest day).
public class SalesTrackerViewModel : ReactiveObject
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly AppErrorLogger                  _errorLogger;
    private readonly CorpActivityService             _names;

    private readonly List<SaleRowVm> _all = new();

    public ObservableCollection<SaleRowVm> Rows { get; } = new();

    // ── Filters ───────────────────────────────────────────────────────────────
    public ObservableCollection<SalesOwnerOption> OwnerOptions { get; } =
    [
        new("All",                             OwnerScope.All),
        new("All Characters and Personal Corps", OwnerScope.CharsAndPersonalCorps),
    ];
    private SalesOwnerOption _selectedOwner;
    public SalesOwnerOption SelectedOwner
    {
        get => _selectedOwner;
        set { this.RaiseAndSetIfChanged(ref _selectedOwner, value ?? OwnerOptions[1]); ApplyFilters(); }
    }

    public IReadOnlyList<SalesTypeOption> SaleTypeOptions { get; } =
    [
        new("All types", null),
        new("Market",    "Market"),
        new("Contract",  "Contract"),
    ];
    private SalesTypeOption _selectedType;
    public SalesTypeOption SelectedType
    {
        get => _selectedType;
        set { this.RaiseAndSetIfChanged(ref _selectedType, value ?? SaleTypeOptions[0]); ApplyFilters(); }
    }

    private string _dateFrom;
    public string DateFrom { get => _dateFrom; set { this.RaiseAndSetIfChanged(ref _dateFrom, value); ApplyFilters(); } }
    private string _dateThru = "";
    public string DateThru { get => _dateThru; set { this.RaiseAndSetIfChanged(ref _dateThru, value); ApplyFilters(); } }

    private bool _isLoading;
    public bool IsLoading { get => _isLoading; private set => this.RaiseAndSetIfChanged(ref _isLoading, value); }

    private string _statusText = "";
    public string StatusText { get => _statusText; private set => this.RaiseAndSetIfChanged(ref _statusText, value); }

    public SalesTrackerViewModel(IDbContextFactory<AppDbContext> dbFactory, AppErrorLogger errorLogger,
        CorpActivityService names)
    {
        _dbFactory   = dbFactory;
        _errorLogger = errorLogger;
        _names       = names;
        _selectedOwner = OwnerOptions[1];                                  // All Characters and Personal Corps
        _selectedType  = SaleTypeOptions[0];                               // All types
        _dateFrom      = DateTime.UtcNow.AddDays(-90).ToString("yyyy-MM-dd"); // last 90 days

        Observable.Interval(TimeSpan.FromMinutes(5))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(tick => { _ = LoadAsync(); });

        _ = LoadAsync();
    }

    private void ApplyFilters()
    {
        IEnumerable<SaleRowVm> q = _all;

        q = _selectedOwner?.Scope switch
        {
            OwnerScope.CharsAndPersonalCorps => q.Where(r => r.OwnerType == "character" || (r.OwnerType == "corporation" && r.OwnerIsPersonal)),
            OwnerScope.Specific              => q.Where(r => r.OwnerType == _selectedOwner.OwnerType && r.OwnerId == _selectedOwner.OwnerId),
            _                                => q,
        };

        if (_selectedType?.Kind is string kind)
            q = q.Where(r => r.Kind == kind);

        if (TryDate(_dateFrom, out var from)) q = q.Where(r => r.When.UtcDateTime.Date >= from);
        if (TryDate(_dateThru, out var thru)) q = q.Where(r => r.When.UtcDateTime.Date <= thru);

        var list = q.ToList();
        Rows.Clear();
        foreach (var r in list) Rows.Add(r);
        StatusText = list.Count == 0 ? "No sales match the filters." : $"{list.Count:N0} sale(s)";
    }

    private static bool TryDate(string s, out DateTime date)
    {
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        { date = d.Date; return true; }
        date = default; return false;
    }

    // Market sales: one row per sell transaction. Location = station/structure; buyer = the client.
    private const string MarketSql =
        """
        SELECT t."TransactionId" AS SaleId, t."OwnerId" AS OwnerId, t."OwnerType" AS OwnerType,
               t."Date" AS DateStr, t."TypeId" AS TypeId, t."Quantity" AS Quantity,
               CAST(t."UnitPrice" AS REAL) AS UnitPrice, t."ClientId" AS BuyerId,
               COALESCE((SELECT "Name" FROM "SdeStations"       WHERE "StationId"   = t."LocationId"),
                        (SELECT "Name" FROM "EsiStructureNames" WHERE "StructureId" = t."LocationId")) AS Location
        FROM "EsiWalletTransactions" t
        WHERE t."IsBuy" = 0
        """;

    // Contract sales: item-exchange contracts finished for ISK, issued BY the tracked owner (so an
    // accepted purchase is excluded). Buyer = the acceptor; location = the items' location.
    private const string ContractSql =
        """
        SELECT c."ContractId" AS SaleId, c."OwnerId" AS OwnerId, c."OwnerType" AS OwnerType,
               c."DateCompleted" AS DateStr, CAST(c."Price" AS REAL) AS Price, COALESCE(c."AcceptorId", 0) AS BuyerId,
               COALESCE((SELECT "Name" FROM "SdeStations"       WHERE "StationId"   = c."StartLocationId"),
                        (SELECT "Name" FROM "EsiStructureNames" WHERE "StructureId" = c."StartLocationId")) AS Location
        FROM "EsiContracts" c
        WHERE c."Type" = 'item_exchange' AND c."Status" = 'finished' AND CAST(c."Price" AS REAL) > 0
          AND ( (c."OwnerType" = 'character'   AND c."IssuerId" = c."OwnerId" AND c."ForCorporation" = 0)
             OR (c."OwnerType" = 'corporation' AND c."IssuerCorporationId" = c."OwnerId") )
        """;

    private const string ContractItemSql =
        """
        SELECT ci."ContractId" AS ContractId, ci."TypeId" AS TypeId, ci."Quantity" AS Quantity
        FROM "EsiContractItems" ci
        JOIN "EsiContracts" c ON c."ContractId" = ci."ContractId"
        WHERE ci."IsIncluded" = 1
          AND c."Type" = 'item_exchange' AND c."Status" = 'finished' AND CAST(c."Price" AS REAL) > 0
          AND ( (c."OwnerType" = 'character'   AND c."IssuerId" = c."OwnerId" AND c."ForCorporation" = 0)
             OR (c."OwnerType" = 'corporation' AND c."IssuerCorporationId" = c."OwnerId") )
        """;

    private async Task LoadAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        StatusText = "Loading…";
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            var market    = await db.Database.SqlQueryRaw<MarketSaleDto>(MarketSql).ToListAsync();
            var contracts = (await db.Database.SqlQueryRaw<ContractSaleDto>(ContractSql).ToListAsync())
                            .DistinctBy(c => c.SaleId).ToList();
            var citems    = await db.Database.SqlQueryRaw<ContractItemDto>(ContractItemSql).ToListAsync();

            // Item + owner names.
            var typeIds = market.Select(m => m.TypeId).Concat(citems.Select(i => i.TypeId)).Distinct().ToList();
            var typeNames = await db.SdeTypes.AsNoTracking().Where(t => typeIds.Contains(t.TypeId))
                .ToDictionaryAsync(t => t.TypeId, t => t.Name);

            // Nearest-day price snapshots for the sold types (resolved in memory — a correlated
            // "nearest date" subquery can't reference the outer sale date in SQLite).
            var snaps = await db.TypePriceSnapshots.AsNoTracking().Where(s => typeIds.Contains(s.TypeId))
                .Select(s => new { s.TypeId, s.Date, s.BuildCost, s.MarketValue }).ToListAsync();
            var snapByType = snaps
                .Select(s => (s.TypeId, Date: ParseDay(s.Date), s.BuildCost, s.MarketValue))
                .GroupBy(s => s.TypeId)
                .ToDictionary(g => g.Key, g => g.ToList());

            (double? Build, double? Market) Snap(int typeId, DateTimeOffset when)
            {
                if (!snapByType.TryGetValue(typeId, out var list) || list.Count == 0) return (null, null);
                var target = when.UtcDateTime.Date;
                var best = list[0]; var bestDist = double.MaxValue;
                foreach (var s in list)
                {
                    var d = Math.Abs((s.Date - target).TotalDays);
                    if (d < bestDist) { bestDist = d; best = s; }
                }
                return (best.BuildCost, best.MarketValue);
            }

            // Owner names + the personal-corp flag.
            var allChars = await db.Characters.AsNoTracking().Select(c => new { c.Id, c.Name }).ToListAsync();
            var allCorps = await db.Corporations.AsNoTracking().Select(c => new { c.Id, c.Name, c.IsPersonal }).ToListAsync();
            var charNames    = allChars.ToDictionary(c => (long)c.Id, c => c.Name);
            var corpNames    = allCorps.ToDictionary(c => (long)c.Id, c => c.Name);
            var corpPersonal = allCorps.ToDictionary(c => (long)c.Id, c => c.IsPersonal);
            bool IsPersonal(long id, string type) => type == "corporation" && corpPersonal.TryGetValue(id, out var p) && p;

            string OwnerName(long id, string type) => type == "corporation"
                ? (corpNames.TryGetValue(id, out var cn) ? cn : $"Corp {id}")
                : (charNames.TryGetValue(id, out var pn) ? pn : $"Char {id}");
            string TypeName(int id) => typeNames.TryGetValue(id, out var n) ? n : $"Type {id}";

            BuildOwnerOptions(allChars.Select(c => ((long)c.Id, c.Name)), allCorps.Select(c => ((long)c.Id, c.Name)));

            // Buyer names — external players. Resolve from local caches, fall back to ESI once and
            // persist to the shared UniverseNames cache so later loads stay offline.
            var buyerIds = market.Select(m => m.BuyerId)
                .Concat(contracts.Select(c => c.BuyerId)).Where(id => id > 0).Distinct().ToList();
            var buyerNames = await ResolveBuyersAsync(db, buyerIds, charNames, corpNames);
            string BuyerName(long id) => id <= 0 ? "" : (buyerNames.TryGetValue(id, out var n) ? n : id.ToString());

            var itemsByContract = citems.GroupBy(i => i.ContractId).ToDictionary(g => g.Key, g => g.ToList());
            var rows = new List<SaleRowVm>(market.Count + contracts.Count);

            foreach (var m in market)
            {
                var (bu, mv) = Snap(m.TypeId, ParseDate(m.DateStr));
                rows.Add(new SaleRowVm(
                    ParseDate(m.DateStr), "Market", m.OwnerType, m.OwnerId, IsPersonal(m.OwnerId, m.OwnerType),
                    OwnerName(m.OwnerId, m.OwnerType), m.Location ?? "", BuyerName(m.BuyerId),
                    TypeName(m.TypeId), m.Quantity.ToString("N0"), m.Quantity * m.UnitPrice,
                    bu is double b ? b * m.Quantity : null, mv is double v ? v * m.Quantity : null));
            }

            foreach (var c in contracts)
            {
                var when = ParseDate(c.DateStr);
                var its  = itemsByContract.TryGetValue(c.SaleId, out var list) ? list : [];
                // One item: name + count. Many: first item + "+N more items", and units are just
                // "Multiple" (the per-item counts still drive build/market below, but listing them
                // for a big multi-item contract isn't useful).
                string names, units;
                if (its.Count == 0)      { names = "(no items)"; units = ""; }
                else if (its.Count == 1) { names = TypeName(its[0].TypeId); units = its[0].Quantity.ToString("N0"); }
                else                     { names = $"{TypeName(its[0].TypeId)} +{its.Count - 1} more items"; units = "Multiple"; }
                var build = SumOrNull(its.Select(i => Snap(i.TypeId, when).Build is double b ? b * i.Quantity : (double?)null));
                var mkt   = SumOrNull(its.Select(i => Snap(i.TypeId, when).Market is double m ? m * i.Quantity : (double?)null));
                rows.Add(new SaleRowVm(
                    when, "Contract", c.OwnerType, c.OwnerId, IsPersonal(c.OwnerId, c.OwnerType),
                    OwnerName(c.OwnerId, c.OwnerType), c.Location ?? "", BuyerName(c.BuyerId),
                    names, units, c.Price, build, mkt));
            }

            _all.Clear();
            _all.AddRange(rows.OrderByDescending(r => r.WhenSort));
            ApplyFilters();
        }
        catch (Exception ex)
        {
            _errorLogger.Log("SalesTrackerViewModel", "Load", ex);
            StatusText = "Error loading sales.";
        }
        finally { IsLoading = false; }
    }

    // Populate the owner filter with every tracked character and corp (once).
    private void BuildOwnerOptions(IEnumerable<(long Id, string Name)> chars, IEnumerable<(long Id, string Name)> corps)
    {
        if (OwnerOptions.Count > 2) return;   // already built (keeps the current selection intact)
        foreach (var (id, name) in chars.OrderBy(c => c.Name))
            OwnerOptions.Add(new SalesOwnerOption(name, OwnerScope.Specific, id, "character"));
        foreach (var (id, name) in corps.OrderBy(c => c.Name))
            OwnerOptions.Add(new SalesOwnerOption(name, OwnerScope.Specific, id, "corporation"));
    }

    private async Task<Dictionary<long, string>> ResolveBuyersAsync(
        AppDbContext db, List<long> ids, Dictionary<long, string> chars, Dictionary<long, string> corps)
    {
        var names = new Dictionary<long, string>();
        if (ids.Count == 0) return names;

        foreach (var u in await db.UniverseNames.AsNoTracking().Where(u => ids.Contains(u.EntityId)).ToListAsync())
            names[u.EntityId] = u.Name;
        foreach (var id in ids)
            if (!names.ContainsKey(id) && chars.TryGetValue(id, out var cn)) names[id] = cn;
        foreach (var id in ids)
            if (!names.ContainsKey(id) && corps.TryGetValue(id, out var on)) names[id] = on;

        var missing = ids.Where(id => !names.ContainsKey(id)).ToList();
        if (missing.Count == 0) return names;

        try
        {
            var resolved = await _names.ResolveNamesAsync(missing);
            foreach (var kv in resolved)
            {
                names[kv.Key] = kv.Value;
                db.UniverseNames.Add(new UniverseName { EntityId = kv.Key, Name = kv.Value, Category = "" });
            }
            if (resolved.Count > 0) await db.SaveChangesAsync();
        }
        catch (Exception ex) { _errorLogger.Log("SalesTrackerViewModel", "ResolveBuyers", ex); }

        return names;
    }

    // Sum of the present values; null only when every item lacked a snapshot.
    private static double? SumOrNull(IEnumerable<double?> values)
    {
        double sum = 0; var any = false;
        foreach (var v in values) if (v.HasValue) { sum += v.Value; any = true; }
        return any ? sum : null;
    }

    private static DateTimeOffset ParseDate(string s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)
            ? d : DateTimeOffset.MinValue;

    private static DateTime ParseDay(string s) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.Date : DateTime.MinValue;

    private sealed class MarketSaleDto
    {
        public long SaleId { get; set; } public long OwnerId { get; set; } public string OwnerType { get; set; } = "";
        public string DateStr { get; set; } = ""; public int TypeId { get; set; } public int Quantity { get; set; }
        public double UnitPrice { get; set; } public long BuyerId { get; set; } public string? Location { get; set; }
    }
    private sealed class ContractSaleDto
    {
        public long SaleId { get; set; } public long OwnerId { get; set; } public string OwnerType { get; set; } = "";
        public string DateStr { get; set; } = ""; public double Price { get; set; }
        public long BuyerId { get; set; } public string? Location { get; set; }
    }
    private sealed class ContractItemDto
    {
        public long ContractId { get; set; } public int TypeId { get; set; } public long Quantity { get; set; }
    }
}
