using System.Data.Common;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using Microsoft.Data.Sqlite;
using ReactiveUI;
using EveConsole.Data;

namespace EveConsole.ViewModels;

public class AssetBrowserViewModel : ReactiveObject
{
    private readonly string _connectionString;
    private CancellationTokenSource _cts = new();
    private const int PageSize = 5000;
    private int _offset;
    private string? _sortColumn;
    private bool    _sortDescending;

    private record ActiveFilter(string Column, FilterOp Op, string Value);
    private readonly List<ActiveFilter> _activeFilters = [];

    public static readonly List<string> FilterableColumns =
    [
        "Owner \"Type\"", "Owner \"Name\"", "\"Type\" \"Name\"", "Group", "Category",
        "Quantity", "\"Value\" Per Unit", "Value", "Build \"Cost\"", "Volume", "\"Total\" \"Volume\"", "ISK/m³",
        "Location \"Name\"", "Container", "Flag", "Solar System", "Region \"Name\"", "Security", "Location \"Type\"",
        "Is \"Singleton\"", "Is Blueprint Copy",
        "Owner \"Id\"", "Item \"Id\"", "\"Type\" \"Id\"", "Location \"Id\"",
    ];

    public static readonly HashSet<string> HiddenColumns =
    [
        "Owner \"Id\"", "Root Location \"Id\"",
        // Carried so the names above them can be links; never a column of their own.
        "Solar System \"Id\"", "Region \"Id\"", "Is Station",
    ];

    public string? SortColumn    => _sortColumn;
    public bool    SortDescending => _sortDescending;

    // ── Reactive state ────────────────────────────────────────────────────────

    public ObservableCollection<GridRow> Rows { get; } = [];

    private List<string> _columns = [];
    public List<string> Columns
    {
        get => _columns;
        private set => this.RaiseAndSetIfChanged(ref _columns, value);
    }

    private string _statusText = "Loading…";
    public string StatusText
    {
        get => _statusText;
        private set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    private bool _hasMore;
    public bool HasMore
    {
        get => _hasMore;
        private set => this.RaiseAndSetIfChanged(ref _hasMore, value);
    }

    // ── Aggregation tab data ───────────────────────────────────────────────────

    public ObservableCollection<GridRow> LocationRows { get; } = [];
    private List<string> _locationColumns = [];
    public List<string> LocationColumns
    {
        get => _locationColumns;
        private set => this.RaiseAndSetIfChanged(ref _locationColumns, value);
    }

    public ObservableCollection<GridRow> SystemRows { get; } = [];
    private List<string> _systemColumns = [];
    public List<string> SystemColumns
    {
        get => _systemColumns;
        private set => this.RaiseAndSetIfChanged(ref _systemColumns, value);
    }

    public ObservableCollection<GridRow> RegionRows { get; } = [];
    private List<string> _regionColumns = [];
    public List<string> RegionColumns
    {
        get => _regionColumns;
        private set => this.RaiseAndSetIfChanged(ref _regionColumns, value);
    }

    // Set by MainWindow to open an item in the Item Browser when the user right-clicks a row.
    public Action<int, string>? OpenInItemBrowser { get; set; }

    // ── Constructor ───────────────────────────────────────────────────────────

    public AssetBrowserViewModel(string connectionString)
    {
        _connectionString = connectionString;
        _ = LoadAsync();
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public async Task ApplyFiltersAsync(
        IReadOnlyList<(string? Column, FilterOp? Op, string? Value)> filters)
    {
        _activeFilters.Clear();
        foreach (var (col, op, val) in filters)
        {
            if (!string.IsNullOrWhiteSpace(col) && op is not null && !string.IsNullOrWhiteSpace(val))
                _activeFilters.Add(new ActiveFilter(col, op, val));
        }
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        await LoadAsync(_cts.Token);
    }

    public async Task ClearFiltersAsync()
    {
        _activeFilters.Clear();
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        await LoadAsync(_cts.Token);
    }

    // Called by agent tools — applies simple "contains" filters without needing the View's
    // filter-row UI controls.  The column names must be values from FilterableColumns.
    public Task ApplyAgentFilterAsync(IReadOnlyList<(string Column, string Value)> filters)
    {
        var containsOp = EsiExplorerViewModel.Operators[0]; // "Contains" / LIKE
        return ApplyFiltersAsync(filters
            .Select(f => ((string?)f.Column, (FilterOp?)containsOp, (string?)f.Value))
            .ToList());
    }

    public async Task SortAsync(string column)
    {
        if (_sortColumn == column)
            _sortDescending = !_sortDescending;
        else
        {
            _sortColumn     = column;
            _sortDescending = false;
        }
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        await LoadAsync(_cts.Token);
    }

    public async Task LoadMoreAsync()
    {
        if (!HasMore) return;
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            await using var conn = AppDb.Connect();
            await conn.OpenAsync(ct);
            await AppendPageAsync(conn, await CountAsync(conn, ct), ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => StatusText = $"Error: {ex.Message}");
        }
    }

    // ── Data loading ──────────────────────────────────────────────────────────

    private async Task LoadAsync(CancellationToken ct = default)
    {
        _offset = 0;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Rows.Clear();
            Columns    = [];
            HasMore    = false;
            StatusText = "Loading…";
            LocationRows.Clear(); LocationColumns = [];
            SystemRows.Clear();   SystemColumns   = [];
            RegionRows.Clear();   RegionColumns   = [];
        });

        try
        {
            await using var conn = AppDb.Connect();
            await conn.OpenAsync(ct);
            await AppendPageAsync(conn, await CountAsync(conn, ct), ct);
            await LoadAggregationAsync(conn, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => StatusText = $"Error: {ex.Message}");
        }
    }

    private async Task LoadAggregationAsync(DbConnection conn, CancellationToken ct)
    {
        var where = BuildWhere();
        await LoadAggTabAsync(conn, QueryPrefix + LocationAggSql(where), LocationRows,
            cols => LocationColumns = cols, ct);
        await LoadAggTabAsync(conn, QueryPrefix + SystemAggSql(where),   SystemRows,
            cols => SystemColumns   = cols, ct);
        await LoadAggTabAsync(conn, QueryPrefix + RegionAggSql(where),   RegionRows,
            cols => RegionColumns   = cols, ct);
    }

    private async Task LoadAggTabAsync(
        DbConnection conn, string sql,
        ObservableCollection<GridRow> collection,
        Action<List<string>> setColumns,
        CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        AddFilterParams(cmd);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        var colNames = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
        var rows = new List<GridRow>();
        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, string>(reader.FieldCount);
            for (int i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? "" : FormatValue(reader.GetName(i), reader.GetValue(i));
            rows.Add(new GridRow(row));
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            collection.Clear();
            foreach (var r in rows) collection.Add(r);
            setColumns(colNames);
        });
    }

    private async Task<int> CountAsync(DbConnection conn, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"{QueryPrefix} SELECT COUNT(*) FROM Base {BuildWhere()}";
        AddFilterParams(cmd);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct) ?? 0);
    }

    private async Task AppendPageAsync(DbConnection conn, int total, CancellationToken ct)
    {
        var where = BuildWhere();
        var order = _sortColumn is not null
            ? $"ORDER BY \"{_sortColumn}\" {(_sortDescending ? "DESC" : "ASC")}"
            : "ORDER BY \"Owner Name\", \"Type Name\"";

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            {QueryPrefix}
            SELECT * FROM Base {where} {order}
            LIMIT {PageSize} OFFSET {_offset}
            """;
        AddFilterParams(cmd);

        using var reader = await cmd.ExecuteReaderAsync(ct);

        List<string>? newColumns = null;
        if (_offset == 0)
            newColumns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();

        var newRows = new List<GridRow>();
        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, string>(reader.FieldCount);
            for (int i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? "" : FormatValue(reader.GetName(i), reader.GetValue(i));
            newRows.Add(new GridRow(row));
        }

        _offset += newRows.Count;
        var loadedCount = _offset;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            foreach (var r in newRows)
                Rows.Add(r);

            if (newColumns is not null)
                Columns = newColumns;

            HasMore    = loadedCount < total;
            StatusText = total == 0         ? "No assets."
                : loadedCount < total ? $"Showing {loadedCount:N0} of {total:N0} assets"
                                      : $"{total:N0} assets total";
        });
    }

    // ── SQL ───────────────────────────────────────────────────────────────────

    // Three-level CTE — shared by both the detail view and the aggregation queries:
    //   ContainerJoins — 3-hop self-join to build the Container display column.
    //   JobFacilities  — active/paused/ready industry jobs with pre-computed facility names
    //                    and item counts; used for the two UNION ALL branches in Base.
    //   Base — all display-ready columns. Aggregation queries wrap Base with GROUP BY.
    private static readonly string QueryPrefix = """
        WITH
        ContainerHops AS (
            SELECT
                a."ItemId", a."OwnerId", a."OwnerType",
                p1."TypeId" AS CP1TypeId,
                p2."TypeId" AS CP2TypeId,
                p3."TypeId" AS CP3TypeId,
                CASE
                    WHEN a."LocationType" != 'item'          THEN 0
                    WHEN a."LocationId" = a."RootLocationId"   THEN 0
                    WHEN p1."LocationId" = a."RootLocationId"  THEN 1
                    WHEN p2."LocationId" = a."RootLocationId"  THEN 2
                    WHEN p3."LocationId" = a."RootLocationId"  THEN 3
                    ELSE 0
                END AS ContainerDepth,
                -- Which hop's flag names the corp hangar division.
                --
                -- A division is not an item, so it owns no hop of its own: it is expressed as the
                -- LocationFlag of whatever sits directly inside the office. Verified against the
                -- data -- Morphite (Unlocked) -> Station Container (CorpSAG3) -> Office
                -- (OfficeFolder) -- so the flag that matters is one hop further out at each
                -- depth, which is why the path used to jump straight from Office to the
                -- container and lose the division between them.
                CASE
                    WHEN a."LocationType" != 'item'          THEN NULL
                    WHEN a."LocationId" = a."RootLocationId"   THEN NULL
                    WHEN p1."LocationId" = a."RootLocationId"  THEN a."LocationFlag"
                    WHEN p2."LocationId" = a."RootLocationId"  THEN p1."LocationFlag"
                    WHEN p3."LocationId" = a."RootLocationId"  THEN p2."LocationFlag"
                    ELSE NULL
                END AS DivFlag
            FROM "EsiAssets" a
            LEFT JOIN "EsiAssets" p1 ON p1."ItemId" = a."LocationId"  AND p1."OwnerId" = a."OwnerId" AND p1."OwnerType" = a."OwnerType"
                                   AND a."LocationType" = 'item' AND a."LocationId" != a."RootLocationId"
            LEFT JOIN "EsiAssets" p2 ON p2."ItemId" = p1."LocationId" AND p2."OwnerId" = a."OwnerId" AND p2."OwnerType" = a."OwnerType"
                                   AND p1."LocationId" != a."RootLocationId"
            LEFT JOIN "EsiAssets" p3 ON p3."ItemId" = p2."LocationId" AND p3."OwnerId" = a."OwnerId" AND p3."OwnerType" = a."OwnerType"
                                   AND p2."LocationId" != a."RootLocationId"
        ),
        -- Second pass only because SQLite cannot reference a computed alias from the same SELECT,
        -- and resolving the division name needs DivFlag to already exist.
        ContainerJoins AS (
            SELECT
                h."ItemId", h."OwnerId", h."OwnerType",
                h.CP1TypeId, h.CP2TypeId, h.CP3TypeId, h.ContainerDepth,
                -- 'CorpSAG_' is one character, so divisions 1-7 match and CorporationGoalDeliveries
                -- (which also sits under the office, but is not a hangar) does not. Falls back to
                -- the number when the corp has not named the division, or when we hold no
                -- divisions for that corp at all -- "\"Division\" 6" still beats showing nothing.
                CASE WHEN h.DivFlag LIKE 'CorpSAG_'
                     THEN COALESCE(NULLIF(cd."Name", ''), 'Division ' || SUBSTR(h.DivFlag, 8))
                     ELSE NULL
                END AS DivName
            FROM ContainerHops h
            LEFT JOIN "EsiCorpDivisions" cd ON h."OwnerType"     = 'corporation'
                                         AND cd."CorporationId" = h."OwnerId"
                                         AND cd."DivisionType"  = 'hangar'
                                         AND h.DivFlag LIKE 'CorpSAG_'
                                         AND cd."Division" = CAST(SUBSTR(h.DivFlag, 8) AS INTEGER)
        ),
        JobFacilities AS (
            SELECT
                j."JobId",
                j."OwnerId",
                j."OwnerType",
                j."FacilityId",
                j."BlueprintTypeId",
                j."ProductTypeId",
                j."ActivityId",
                j."Runs",
                CASE WHEN bl."Runs" IS NULL OR bl."Runs" < 0 THEN 0 ELSE 1 END AS BPIsCopy,
                CASE
                    WHEN j."ActivityId" IN (1, 9, 11) THEN
                        COALESCE((SELECT p2q."Quantity" FROM "SdeBlueprintProducts" p2q
                                  WHERE p2q."TypeId" = j."BlueprintTypeId"
                                    AND p2q."Activity" = CASE j."ActivityId"
                                        WHEN 1  THEN 'manufacturing'
                                        WHEN 9  THEN 'reaction'
                                        WHEN 11 THEN 'reaction'
                                    END
                                    AND (j."ProductTypeId" IS NULL OR p2q."ProductTypeId" = j."ProductTypeId")
                                  LIMIT 1), 1) * j."Runs"
                    WHEN j."ActivityId" IN (5, 8) THEN j."Runs"
                    ELSE NULL
                END AS ItemsProduced,
                COALESCE(NULLIF(sn_f."Name",''), st_f."Name", CAST(j."FacilityId" AS TEXT)) AS FacilityName,
                COALESCE(ss_st_f."Name", ss_sn_f."Name", '')  AS FacilitySolarSystem,
                COALESCE(r_st_f."Name",  r_sn_f."Name",  '')  AS FacilityRegion,
                CAST(ROUND(COALESCE(ss_st_f."Security", ss_sn_f."Security", 0.0), 1) AS TEXT) AS FacilitySecurity
            FROM "EsiIndustryJobs" j
            LEFT JOIN "EsiBlueprints"     bl     ON bl."ItemId"      = j."BlueprintId"  AND bl."OwnerId" = j."OwnerId" AND bl."OwnerType" = j."OwnerType"
            LEFT JOIN "SdeStations"       st_f   ON st_f."StationId" = j."FacilityId"
            LEFT JOIN "EsiStructureNames" sn_f   ON sn_f."StructureId" = j."FacilityId"
            LEFT JOIN "SdeSolarSystems"   ss_st_f ON ss_st_f."SolarSystemId" = st_f."SolarSystemId"
            LEFT JOIN "SdeSolarSystems"   ss_sn_f ON ss_sn_f."SolarSystemId" = sn_f."SolarSystemId"
            LEFT JOIN "SdeRegions"        r_st_f ON r_st_f."RegionId" = COALESCE(st_f."RegionId", ss_st_f."RegionId")
            LEFT JOIN "SdeRegions"        r_sn_f ON r_sn_f."RegionId" = ss_sn_f."RegionId"
            WHERE j."Status" IN ('active', 'paused', 'ready')
        ),
        Base AS (
            SELECT
                a."ItemId"          AS "Item \"Id\"",
                a."TypeId"          AS "\"Type\" \"Id\"",
                COALESCE(t."Name",  CAST(a."TypeId"  AS TEXT))           AS "\"Type\" \"Name\"",
                COALESCE(g."Name",  '')                                 AS "Group",
                COALESCE(cat."Name",'')                                 AS "Category",
                a."Quantity"        AS "Quantity",
                a."OwnerType"       AS "Owner \"Type\"",
                COALESCE(ch."Name", co."Name", CAST(a."OwnerId" AS TEXT))  AS "Owner \"Name\"",
                a."LocationId"      AS "Location \"Id\"",
                CASE a."RootLocationType"
                    WHEN 'station'      THEN COALESCE(st."Name",            '<Unknown Station>')
                    WHEN 'solar_system' THEN COALESCE(ss."Name",            '<Unknown System>')
                    WHEN 'other'        THEN COALESCE(NULLIF(sn."Name",''), '<Unknown Structure>')
                    ELSE                     '<Unresolved - Please Refresh>'
                END AS "Location \"Name\"",
                -- The division slots in immediately after the outermost name, which is the office
                -- whenever there is a division at all. Concatenating NULL yields NULL in SQLite,
                -- so the COALESCE is what makes the segment vanish rather than erase the path.
                CASE cj.ContainerDepth
                    WHEN 0 THEN NULL
                    WHEN 1 THEN COALESCE(ct1."Name", CAST(cj.CP1TypeId AS TEXT))
                              || COALESCE(' > ' || cj.DivName, '')
                    WHEN 2 THEN COALESCE(ct2."Name", CAST(cj.CP2TypeId AS TEXT))
                              || COALESCE(' > ' || cj.DivName, '')
                              || ' > ' || COALESCE(ct1."Name", CAST(cj.CP1TypeId AS TEXT))
                    WHEN 3 THEN COALESCE(ct3."Name", CAST(cj.CP3TypeId AS TEXT))
                              || COALESCE(' > ' || cj.DivName, '')
                              || ' > ' || COALESCE(ct2."Name", CAST(cj.CP2TypeId AS TEXT))
                              || ' > ' || COALESCE(ct1."Name", CAST(cj.CP1TypeId AS TEXT))
                    ELSE NULL
                END AS "Container",
                a."LocationFlag"    AS "Flag",
                CASE a."RootLocationType"
                    WHEN 'station'      THEN ss_sta."Name"
                    WHEN 'solar_system' THEN ss."Name"
                    WHEN 'other'        THEN ss_s."Name"
                    ELSE NULL
                END AS "Solar System",
                COALESCE(r_st."Name", r_ss."Name", r_s."Name")             AS "Region \"Name\"",
                ROUND(COALESCE(ss_sta."Security", ss."Security", ss_s."Security"), 1) AS "Security",
                -- Hidden: what the names above open. Carried in Base so the three aggregate views
                -- inherit them rather than each re-deriving the joins.
                COALESCE(ss_sta."SolarSystemId", ss."SolarSystemId", ss_s."SolarSystemId", 0) AS "Solar System \"Id\"",
                COALESCE(r_st."RegionId", r_ss."RegionId", r_s."RegionId", 0)                 AS "Region \"Id\"",
                -- NPC station to the entity browser, player structure to its own tool.
                -- RootLocationType already tells the two apart.
                CASE WHEN a."RootLocationType" = 'station' THEN 1 ELSE 0 END              AS "Is Station",
                a."LocationType"    AS "Location \"Type\"",
                t."Volume"          AS "Volume",
                t."Volume" * CAST(a."Quantity" AS REAL)                   AS "\"Total\" \"Volume\"",
                -- BPC: 0. BPO: NPC base price. Regular item: market price with build-cost fallback.
                CASE
                    WHEN a."IsBlueprintCopy" = TRUE THEN 0.0
                    WHEN a."IsBlueprintCopy" = FALSE THEN COALESCE(t."BasePrice", 0.0)
                    ELSE COALESCE(
                        NULLIF(
                            CASE WHEN mds."AssetValueConfigId" IS NOT NULL AND p."TypeId" IS NOT NULL THEN
                                CASE mds."AssetValuePriceType"
                                    WHEN 'Buy'  THEN p."BuyPrice"
                                    WHEN 'Sell' THEN p."SellPrice"
                                    ELSE             p."Midpoint"
                                END
                            ELSE NULL END,
                        0.0),
                        CASE WHEN bc."TotalCost" > 0
                             THEN bc."TotalCost" * (1.0 + COALESCE(mds."MissingPriceMarkupPct", 15.0) / 100.0)
                             ELSE NULL END
                    )
                END AS "\"Value\" Per Unit",
                CASE
                    WHEN a."IsBlueprintCopy" = TRUE THEN 0.0
                    WHEN a."IsBlueprintCopy" = FALSE THEN CAST(a."Quantity" AS REAL) * COALESCE(t."BasePrice", 0.0)
                    ELSE CAST(a."Quantity" AS REAL) * COALESCE(
                        NULLIF(
                            CASE WHEN mds."AssetValueConfigId" IS NOT NULL AND p."TypeId" IS NOT NULL THEN
                                CASE mds."AssetValuePriceType"
                                    WHEN 'Buy'  THEN p."BuyPrice"
                                    WHEN 'Sell' THEN p."SellPrice"
                                    ELSE             p."Midpoint"
                                END
                            ELSE NULL END,
                        0.0),
                        CASE WHEN bc."TotalCost" > 0
                             THEN bc."TotalCost" * (1.0 + COALESCE(mds."MissingPriceMarkupPct", 15.0) / 100.0)
                             ELSE 0.0 END
                    )
                END AS "Value",
                -- ISK/m³ at individual row level = price per unit ÷ volume per unit
                CASE
                    WHEN a."IsBlueprintCopy" = TRUE THEN NULL
                    WHEN a."IsBlueprintCopy" = FALSE AND t."BasePrice" > 0 AND t."Volume" > 0 THEN t."BasePrice" / t."Volume"
                    WHEN t."Volume" > 0 THEN
                        COALESCE(
                            NULLIF(
                                CASE WHEN mds."AssetValueConfigId" IS NOT NULL AND p."TypeId" IS NOT NULL THEN
                                    CASE mds."AssetValuePriceType"
                                        WHEN 'Buy'  THEN p."BuyPrice"  / t."Volume"
                                        WHEN 'Sell' THEN p."SellPrice" / t."Volume"
                                        ELSE             p."Midpoint"  / t."Volume"
                                    END
                                ELSE NULL END,
                            0.0),
                            CASE WHEN bc."TotalCost" > 0
                                 THEN bc."TotalCost" * (1.0 + COALESCE(mds."MissingPriceMarkupPct", 15.0) / 100.0) / t."Volume"
                                 ELSE NULL END
                        )
                    ELSE NULL
                END AS "ISK/m³",
                bc."TotalCost"      AS "Build \"Cost\"",
                a."IsSingleton"     AS "Is \"Singleton\"",
                a."IsBlueprintCopy" AS "Is Blueprint Copy",
                a."OwnerId"         AS "Owner \"Id\"",
                a."RootLocationId"  AS "Root Location \"Id\""
            FROM "EsiAssets" a
            LEFT JOIN ContainerJoins  cj     ON cj."ItemId"         = a."ItemId"         AND cj."OwnerId"          = a."OwnerId" AND cj."OwnerType" = a."OwnerType"
            LEFT JOIN "Characters"      ch     ON a."OwnerId"         = ch."Id"            AND a."OwnerType"         = 'character'
            LEFT JOIN "Corporations"    co     ON a."OwnerId"         = co."Id"            AND a."OwnerType"         = 'corporation'
            LEFT JOIN "SdeTypes"        t      ON a."TypeId"          = t."TypeId"
            LEFT JOIN "SdeGroups"       g      ON g."GroupId"         = t."GroupId"
            LEFT JOIN "SdeCategories"   cat    ON cat."CategoryId"    = g."CategoryId"
            LEFT JOIN "SdeStations"     st     ON a."RootLocationId"  = st."StationId"     AND a."RootLocationType"  = 'station'
            LEFT JOIN "SdeSolarSystems" ss_sta ON ss_sta."SolarSystemId" = st."SolarSystemId"
            LEFT JOIN "SdeSolarSystems" ss     ON a."RootLocationId"  = ss."SolarSystemId" AND a."RootLocationType"  = 'solar_system'
            LEFT JOIN "EsiStructureNames" sn   ON sn."StructureId"    = a."RootLocationId" AND a."RootLocationType"  = 'other'
            LEFT JOIN "SdeSolarSystems" ss_s   ON ss_s."SolarSystemId" = sn."SolarSystemId"
            LEFT JOIN "SdeRegions"      r_st   ON r_st."RegionId"     = st."RegionId"
            LEFT JOIN "SdeRegions"      r_ss   ON r_ss."RegionId"     = ss."RegionId"
            LEFT JOIN "SdeRegions"      r_s    ON r_s."RegionId"      = ss_s."RegionId"
            LEFT JOIN "SdeTypes"        ct1    ON ct1."TypeId"        = cj.CP1TypeId
            LEFT JOIN "SdeTypes"        ct2    ON ct2."TypeId"        = cj.CP2TypeId
            LEFT JOIN "SdeTypes"        ct3    ON ct3."TypeId"        = cj.CP3TypeId
            LEFT JOIN (SELECT "AssetValueConfigId", "AssetValuePriceType", "MissingPriceMarkupPct"
                       FROM "MarketDefaultSettings" WHERE "Id" = 1) mds ON 1=1
            LEFT JOIN "MarketItemPrices" p ON p."ConfigId" = mds."AssetValueConfigId" AND p."TypeId" = a."TypeId"
            LEFT JOIN "BuildCosts" bc ON bc."TypeId" = a."TypeId"
            WHERE a."TypeId" != 60  -- Exclude Asset Safety Wraps (not real items)

            UNION ALL

            -- ── Blueprint currently in an active/paused/ready industry job ────────
            SELECT
                CAST(-(jf."JobId" * 2)     AS INTEGER)                              AS "Item \"Id\"",
                jf."BlueprintTypeId"                                                 AS "\"Type\" \"Id\"",
                COALESCE(bt."Name", CAST(jf."BlueprintTypeId" AS TEXT))               AS "\"Type\" \"Name\"",
                COALESCE(bg."Name",   '')                                            AS "Group",
                COALESCE(bcat."Name", '')                                            AS "Category",
                1                                                                  AS "Quantity",
                jf."OwnerType"                                                       AS "Owner \"Type\"",
                COALESCE(ch."Name", co."Name", CAST(jf."OwnerId" AS TEXT))              AS "Owner \"Name\"",
                jf."FacilityId"                                                      AS "Location \"Id\"",
                jf.FacilityName                                                    AS "Location \"Name\"",
                NULL                                                               AS "Container",
                'Industry Job'                                                     AS "Flag",
                jf.FacilitySolarSystem                                             AS "Solar System",
                jf.FacilityRegion                                                  AS "Region \"Name\"",
                -- ⚠️ ROUNDed to match the asset branch above. The aggregate views GROUP BY Security, so an
                -- unrounded -0.29999 and a rounded -0.3 became two rows for one system, identical
                -- on screen and each holding part of the total.
                ROUND(jf.FacilitySecurity, 1)                                      AS "Security",
                -- Hidden ids, matching the asset branch so the UNION lines up. A job facility is
                -- named but never resolved to ids here, so these are zero: such a row still shows
                -- its system and region, they simply are not links.
                0                                                                  AS "Solar System \"Id\"",
                0                                                                  AS "Region \"Id\"",
                0                                                                  AS "Is Station",
                'item'                                                             AS "Location \"Type\"",
                bt."Volume"                                                          AS "Volume",
                bt."Volume"                                                          AS "\"Total\" \"Volume\"",
                CASE WHEN jf.BPIsCopy = 1 THEN 0.0 ELSE COALESCE(bt."BasePrice", 0.0) END AS "\"Value\" Per Unit",
                CASE WHEN jf.BPIsCopy = 1 THEN 0.0 ELSE COALESCE(bt."BasePrice", 0.0) END AS "Value",
                NULL                                                               AS "ISK/m³",
                NULL                                                               AS "Build \"Cost\"",
                0                                                                  AS "Is \"Singleton\"",
                jf.BPIsCopy                                                        AS "Is Blueprint Copy",
                jf."OwnerId"                                                         AS "Owner \"Id\"",
                jf."FacilityId"                                                      AS "Root Location \"Id\""
            FROM JobFacilities jf
            LEFT JOIN "Characters"    ch   ON ch."Id"   = jf."OwnerId" AND jf."OwnerType" = 'character'
            LEFT JOIN "Corporations"  co   ON co."Id"   = jf."OwnerId" AND jf."OwnerType" = 'corporation'
            LEFT JOIN "SdeTypes"      bt   ON bt."TypeId"  = jf."BlueprintTypeId"
            LEFT JOIN "SdeGroups"     bg   ON bg."GroupId" = bt."GroupId"
            LEFT JOIN "SdeCategories" bcat ON bcat."CategoryId" = bg."CategoryId"

            UNION ALL

            -- ── Product being produced by active/paused/ready industry job ─────────
            SELECT
                CAST(-(jf."JobId" * 2 + 1) AS INTEGER)                              AS "Item \"Id\"",
                jf."ProductTypeId"                                                   AS "\"Type\" \"Id\"",
                COALESCE(pt."Name", CAST(jf."ProductTypeId" AS TEXT))                 AS "\"Type\" \"Name\"",
                COALESCE(pg."Name",   '')                                            AS "Group",
                COALESCE(pcat."Name", '')                                            AS "Category",
                jf.ItemsProduced                                                   AS "Quantity",
                jf."OwnerType"                                                       AS "Owner \"Type\"",
                COALESCE(ch."Name", co."Name", CAST(jf."OwnerId" AS TEXT))              AS "Owner \"Name\"",
                jf."FacilityId"                                                      AS "Location \"Id\"",
                jf.FacilityName                                                    AS "Location \"Name\"",
                NULL                                                               AS "Container",
                'Industry Job'                                                     AS "Flag",
                jf.FacilitySolarSystem                                             AS "Solar System",
                jf.FacilityRegion                                                  AS "Region \"Name\"",
                -- ⚠️ ROUNDed to match the asset branch above. The aggregate views GROUP BY Security, so an
                -- unrounded -0.29999 and a rounded -0.3 became two rows for one system, identical
                -- on screen and each holding part of the total.
                ROUND(jf.FacilitySecurity, 1)                                      AS "Security",
                -- Hidden ids, matching the asset branch so the UNION lines up. A job facility is
                -- named but never resolved to ids here, so these are zero: such a row still shows
                -- its system and region, they simply are not links.
                0                                                                  AS "Solar System \"Id\"",
                0                                                                  AS "Region \"Id\"",
                0                                                                  AS "Is Station",
                'item'                                                             AS "Location \"Type\"",
                pt."Volume"                                                          AS "Volume",
                pt."Volume" * CAST(jf.ItemsProduced AS REAL)                        AS "\"Total\" \"Volume\"",
                CASE
                    WHEN jf."ActivityId" IN (5, 8) THEN 0.0          -- BPCs (Copying / Invention)
                    ELSE COALESCE(
                        NULLIF(
                            CASE WHEN mds."AssetValueConfigId" IS NOT NULL AND p."TypeId" IS NOT NULL THEN
                                CASE mds."AssetValuePriceType"
                                    WHEN 'Buy'  THEN p."BuyPrice"
                                    WHEN 'Sell' THEN p."SellPrice"
                                    ELSE             p."Midpoint"
                                END
                            ELSE NULL END,
                        0.0),
                        CASE WHEN bc."TotalCost" > 0
                             THEN bc."TotalCost" * (1.0 + COALESCE(mds."MissingPriceMarkupPct", 15.0) / 100.0)
                             ELSE NULL END
                    )
                END                                                                AS "\"Value\" Per Unit",
                CASE
                    WHEN jf."ActivityId" IN (5, 8) THEN 0.0
                    ELSE CAST(jf.ItemsProduced AS REAL) * COALESCE(
                        NULLIF(
                            CASE WHEN mds."AssetValueConfigId" IS NOT NULL AND p."TypeId" IS NOT NULL THEN
                                CASE mds."AssetValuePriceType"
                                    WHEN 'Buy'  THEN p."BuyPrice"
                                    WHEN 'Sell' THEN p."SellPrice"
                                    ELSE             p."Midpoint"
                                END
                            ELSE NULL END,
                        0.0),
                        CASE WHEN bc."TotalCost" > 0
                             THEN bc."TotalCost" * (1.0 + COALESCE(mds."MissingPriceMarkupPct", 15.0) / 100.0)
                             ELSE 0.0 END
                    )
                END                                                                AS "Value",
                NULL                                                               AS "ISK/m³",
                bc."TotalCost"                                                       AS "Build \"Cost\"",
                0                                                                  AS "Is \"Singleton\"",
                CASE WHEN jf."ActivityId" IN (5, 8) THEN 1 ELSE NULL END            AS "Is Blueprint Copy",
                jf."OwnerId"                                                         AS "Owner \"Id\"",
                jf."FacilityId"                                                      AS "Root Location \"Id\""
            FROM JobFacilities jf
            LEFT JOIN "Characters"    ch   ON ch."Id"   = jf."OwnerId" AND jf."OwnerType" = 'character'
            LEFT JOIN "Corporations"  co   ON co."Id"   = jf."OwnerId" AND jf."OwnerType" = 'corporation'
            LEFT JOIN "SdeTypes"      pt   ON pt."TypeId"   = jf."ProductTypeId"
            LEFT JOIN "SdeGroups"     pg   ON pg."GroupId"  = pt."GroupId"
            LEFT JOIN "SdeCategories" pcat ON pcat."CategoryId" = pg."CategoryId"
            LEFT JOIN (SELECT "AssetValueConfigId", "AssetValuePriceType", "MissingPriceMarkupPct"
                       FROM "MarketDefaultSettings" WHERE "Id" = 1) mds ON 1=1
            LEFT JOIN "MarketItemPrices" p ON p."ConfigId" = mds."AssetValueConfigId"
                                        AND p."TypeId"   = jf."ProductTypeId"
            LEFT JOIN "BuildCosts" bc ON bc."TypeId" = jf."ProductTypeId"
            WHERE jf."ProductTypeId" IS NOT NULL
              AND jf."ActivityId"    NOT IN (3, 4)   -- ME/TE Research returns the same blueprint; no new item
              AND jf.ItemsProduced IS NOT NULL
        )
        """;

    // Aggregation queries wrap the Base CTE — same WHERE clause, different GROUP BY.
    // The {0} token is replaced by the WHERE clause from BuildWhere() at query time.
    private static string LocationAggSql(string where) => $"""
        SELECT
            "Root Location \"Id\"" AS "Location \"Id\"", "Location \"Name\"", "Solar System", "Region \"Name\"", "Security",
            "Solar System \"Id\"", "Region \"Id\"", "Is Station",
            SUM("Quantity") AS "Item Count",
            SUM("\"Total\" \"Volume\"") AS "\"Total\" \"Volume\"",
            SUM("Value") AS "\"Total\" \"Value\"",
            CASE WHEN SUM("\"Total\" \"Volume\"") > 0 THEN SUM("Value") / SUM("\"Total\" \"Volume\"") ELSE NULL END AS "ISK/m³"
        FROM Base {where}
        GROUP BY "Root Location \"Id\"", "Location \"Name\"", "Solar System", "Region \"Name\"", "Security",
                 "Solar System \"Id\"", "Region \"Id\"", "Is Station"
        ORDER BY SUM("Value") DESC NULLS LAST
        """;

    private static string SystemAggSql(string where) => $"""
        SELECT
            "Solar System", "Region \"Name\"", "Security",
            "Solar System \"Id\"", "Region \"Id\"",
            SUM("Quantity") AS "Item Count",
            SUM("\"Total\" \"Volume\"") AS "\"Total\" \"Volume\"",
            SUM("Value") AS "\"Total\" \"Value\"",
            CASE WHEN SUM("\"Total\" \"Volume\"") > 0 THEN SUM("Value") / SUM("\"Total\" \"Volume\"") ELSE NULL END AS "ISK/m³"
        FROM Base {where}
        GROUP BY "Solar System", "Region \"Name\"", "Security", "Solar System \"Id\"", "Region \"Id\""
        ORDER BY SUM("Value") DESC NULLS LAST
        """;

    private static string RegionAggSql(string where) => $"""
        SELECT
            "Region \"Name\"",
            -- ⚠️ MAX, not MIN. A row whose region resolved to no id contributes 0, and MIN would
            -- let that one row blank the link for a region every other row identified.
            MAX("Region \"Id\"") AS "Region \"Id\"",
            MIN("Security") AS "Security",
            SUM("Quantity") AS "Item Count",
            SUM("\"Total\" \"Volume\"") AS "\"Total\" \"Volume\"",
            SUM("Value") AS "\"Total\" \"Value\"",
            CASE WHEN SUM("\"Total\" \"Volume\"") > 0 THEN SUM("Value") / SUM("\"Total\" \"Volume\"") ELSE NULL END AS "ISK/m³"
        FROM Base {where}
        GROUP BY "Region \"Name\""
        ORDER BY SUM("Value") DESC NULLS LAST
        """;

    private string BuildWhere()
    {
        if (_activeFilters.Count == 0) return "";
        var clauses = _activeFilters.Select((f, i) => $"\"{f.Column}\" {f.Op.Sql} @fv{i}");
        return $"WHERE {string.Join(" AND ", clauses)}";
    }

    private void AddFilterParams(DbCommand cmd)
    {
        for (int i = 0; i < _activeFilters.Count; i++)
        {
            var f   = _activeFilters[i];
            var val = f.Op.UseLike ? $"%{f.Value}%" : f.Value;
            cmd.AddWithValue($"@fv{i}", val);
        }
    }

    private static string FormatValue(string column, object value)
    {
        if (value is double d)
        {
            return column switch
            {
                "Security"                                    => d.ToString("F1"),
                "Volume" or "\"Total\" \"Volume\""                   => d.ToString("N2"),
                "Value" or "\"Value\" Per Unit" or "\"Total\" \"Value\"" or "Build \"Cost\"" => d.ToString("N2"),
                "ISK/m³"                                     => d.ToString("N2"),
                _                                            => d.ToString("N2"),
            };
        }
        if (column == "Item Count" && value is long l) return l.ToString("N0");
        return value.ToString()!;
    }
}
