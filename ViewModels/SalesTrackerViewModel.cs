using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive.Linq;
using EveCortex.Data;
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
    public string Kind     { get; }   // "Market" or "Contract"
    public string Owner    { get; }
    public string Items    { get; }
    public string Units    { get; }
    public string Total  { get; } public double  TotalRaw  { get; }
    public string Fees   { get; } public double  FeesRaw   { get; }
    public string Net    { get; } public double  NetRaw    { get; }
    public string Build  { get; } public double  BuildRaw  { get; }
    public string Market { get; } public double  MarketRaw { get; }

    public SaleRowVm(DateTimeOffset when, string kind, string owner, string items, string units,
        double total, double fees, double net, double? build, double? market)
    {
        When     = when;
        WhenSort = when.UtcTicks;
        WhenText = when.UtcDateTime.ToString("yyyy-MM-dd HH:mm");
        Kind     = kind;
        Owner    = owner;
        Items    = items;
        Units    = units;
        TotalRaw = total;  Total = MarketFmt.Isk(total);
        FeesRaw  = fees;   Fees  = MarketFmt.Isk(fees);
        NetRaw   = net;    Net   = MarketFmt.Isk(net);
        BuildRaw  = build  ?? 0; Build  = build  is double b ? MarketFmt.Isk(b) : "—";
        MarketRaw = market ?? 0; Market = market is double m ? MarketFmt.Isk(m) : "—";
    }
}

// Sales Tracker — lists market sales (wallet transactions) and contract sales (item_exchange
// contracts sold for ISK), with build/market value pulled from TypePriceSnapshots as of the sale.
public class SalesTrackerViewModel : ReactiveObject
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly AppErrorLogger                  _errorLogger;

    public ObservableCollection<SaleRowVm> Rows { get; } = new();

    private bool _isLoading;
    public bool IsLoading { get => _isLoading; private set => this.RaiseAndSetIfChanged(ref _isLoading, value); }

    private string _statusText = "";
    public string StatusText { get => _statusText; private set => this.RaiseAndSetIfChanged(ref _statusText, value); }

    public SalesTrackerViewModel(IDbContextFactory<AppDbContext> dbFactory, AppErrorLogger errorLogger)
    {
        _dbFactory   = dbFactory;
        _errorLogger = errorLogger;

        Observable.Interval(TimeSpan.FromMinutes(5))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(tick => { _ = LoadAsync(); });

        _ = LoadAsync();
    }

    // Market sales: one row per sell transaction. Transaction (sales) tax is the transaction_tax
    // journal entry sharing the owner + timestamp. Build/market value is the snapshot as of the day.
    private const string MarketSql =
        """
        SELECT t."TransactionId" AS SaleId, t."OwnerId" AS OwnerId, t."OwnerType" AS OwnerType,
               t."Date" AS DateStr, t."TypeId" AS TypeId, t."Quantity" AS Quantity,
               CAST(t."UnitPrice" AS REAL) AS UnitPrice,
               (SELECT COALESCE(-SUM(CAST(jt."Amount" AS REAL)), 0) FROM "EsiWalletJournal" jt
                 WHERE jt."RefType" = 'transaction_tax' AND jt."OwnerId" = t."OwnerId" AND jt."Date" = t."Date") AS Fees,
               (SELECT s."BuildCost"   FROM "TypePriceSnapshots" s
                 WHERE s."TypeId" = t."TypeId" AND s."Date" <= substr(t."Date", 1, 10)
                 ORDER BY s."Date" DESC LIMIT 1) AS BuildUnit,
               (SELECT s."MarketValue" FROM "TypePriceSnapshots" s
                 WHERE s."TypeId" = t."TypeId" AND s."Date" <= substr(t."Date", 1, 10)
                 ORDER BY s."Date" DESC LIMIT 1) AS MarketUnit
        FROM "EsiWalletTransactions" t
        WHERE t."IsBuy" = 0
        """;

    // Contract sales: item-exchange contracts finished for ISK, issued BY the tracked owner (so an
    // accepted purchase is excluded). Contract broker fee links via the journal ContextId.
    private const string ContractSql =
        """
        SELECT c."ContractId" AS SaleId, c."OwnerId" AS OwnerId, c."OwnerType" AS OwnerType,
               c."DateCompleted" AS DateStr, CAST(c."Price" AS REAL) AS Price,
               (SELECT COALESCE(-SUM(CAST(jf."Amount" AS REAL)), 0) FROM "EsiWalletJournal" jf
                 WHERE jf."ContextId" = c."ContractId"
                   AND jf."RefType" IN ('contract_brokers_fee', 'contract_brokers_fee_corp')) AS Fees
        FROM "EsiContracts" c
        WHERE c."Type" = 'item_exchange' AND c."Status" = 'finished' AND CAST(c."Price" AS REAL) > 0
          AND ( (c."OwnerType" = 'character'   AND c."IssuerId" = c."OwnerId" AND c."ForCorporation" = 0)
             OR (c."OwnerType" = 'corporation' AND c."IssuerCorporationId" = c."OwnerId") )
        """;

    private const string ContractItemSql =
        """
        SELECT ci."ContractId" AS ContractId, ci."TypeId" AS TypeId, ci."Quantity" AS Quantity,
               (SELECT s."BuildCost"   FROM "TypePriceSnapshots" s
                 WHERE s."TypeId" = ci."TypeId" AND s."Date" <= substr(c."DateCompleted", 1, 10)
                 ORDER BY s."Date" DESC LIMIT 1) AS BuildUnit,
               (SELECT s."MarketValue" FROM "TypePriceSnapshots" s
                 WHERE s."TypeId" = ci."TypeId" AND s."Date" <= substr(c."DateCompleted", 1, 10)
                 ORDER BY s."Date" DESC LIMIT 1) AS MarketUnit
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

            // Name lookups.
            var typeIds = market.Select(m => m.TypeId).Concat(citems.Select(i => i.TypeId)).Distinct().ToList();
            var typeNames = await db.SdeTypes.AsNoTracking().Where(t => typeIds.Contains(t.TypeId))
                .ToDictionaryAsync(t => t.TypeId, t => t.Name);

            var charIds = market.Where(m => m.OwnerType == "character").Select(m => m.OwnerId)
                .Concat(contracts.Where(c => c.OwnerType == "character").Select(c => c.OwnerId)).Distinct().ToList();
            var corpIds = market.Where(m => m.OwnerType == "corporation").Select(m => m.OwnerId)
                .Concat(contracts.Where(c => c.OwnerType == "corporation").Select(c => c.OwnerId)).Distinct().ToList();
            var charNames = await db.Characters.AsNoTracking().Where(c => charIds.Contains(c.Id))
                .ToDictionaryAsync(c => (long)c.Id, c => c.Name);
            var corpNames = await db.Corporations.AsNoTracking().Where(c => corpIds.Contains(c.Id))
                .ToDictionaryAsync(c => (long)c.Id, c => c.Name);

            string OwnerName(long id, string type) => type == "corporation"
                ? (corpNames.TryGetValue(id, out var cn) ? cn : $"Corp {id}")
                : (charNames.TryGetValue(id, out var pn) ? pn : $"Char {id}");
            string TypeName(int id) => typeNames.TryGetValue(id, out var n) ? n : $"Type {id}";

            var itemsByContract = citems.GroupBy(i => i.ContractId).ToDictionary(g => g.Key, g => g.ToList());
            var rows = new List<SaleRowVm>(market.Count + contracts.Count);

            foreach (var m in market)
            {
                var total = m.Quantity * m.UnitPrice;
                rows.Add(new SaleRowVm(
                    ParseDate(m.DateStr), "Market", OwnerName(m.OwnerId, m.OwnerType),
                    TypeName(m.TypeId), m.Quantity.ToString("N0"),
                    total, m.Fees, total - m.Fees,
                    m.BuildUnit is double b ? b * m.Quantity : null,
                    m.MarketUnit is double mv ? mv * m.Quantity : null));
            }

            foreach (var c in contracts)
            {
                var its = itemsByContract.TryGetValue(c.SaleId, out var list) ? list : [];
                var names = string.Join(", ", its.Select(i => TypeName(i.TypeId)));
                var units = string.Join(", ", its.Select(i => i.Quantity.ToString("N0")));
                rows.Add(new SaleRowVm(
                    ParseDate(c.DateStr), "Contract", OwnerName(c.OwnerId, c.OwnerType),
                    names.Length == 0 ? "(no items)" : names, units,
                    c.Price, c.Fees, c.Price - c.Fees,
                    SumOrNull(its.Select(i => i.BuildUnit.HasValue  ? i.BuildUnit.Value  * i.Quantity : (double?)null)),
                    SumOrNull(its.Select(i => i.MarketUnit.HasValue ? i.MarketUnit.Value * i.Quantity : (double?)null))));
            }

            var ordered = rows.OrderByDescending(r => r.WhenSort).ToList();
            Rows.Clear();
            foreach (var r in ordered) Rows.Add(r);
            StatusText = ordered.Count == 0 ? "No sales found." : $"{ordered.Count:N0} sale(s)";
        }
        catch (Exception ex)
        {
            _errorLogger.Log("SalesTrackerViewModel", "Load", ex);
            StatusText = "Error loading sales.";
        }
        finally { IsLoading = false; }
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

    private sealed class MarketSaleDto
    {
        public long SaleId { get; set; } public long OwnerId { get; set; } public string OwnerType { get; set; } = "";
        public string DateStr { get; set; } = ""; public int TypeId { get; set; } public int Quantity { get; set; }
        public double UnitPrice { get; set; } public double Fees { get; set; }
        public double? BuildUnit { get; set; } public double? MarketUnit { get; set; }
    }
    private sealed class ContractSaleDto
    {
        public long SaleId { get; set; } public long OwnerId { get; set; } public string OwnerType { get; set; } = "";
        public string DateStr { get; set; } = ""; public double Price { get; set; } public double Fees { get; set; }
    }
    private sealed class ContractItemDto
    {
        public long ContractId { get; set; } public int TypeId { get; set; } public long Quantity { get; set; }
        public double? BuildUnit { get; set; } public double? MarketUnit { get; set; }
    }
}
