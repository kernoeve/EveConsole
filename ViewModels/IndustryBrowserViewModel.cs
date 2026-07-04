using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Threading;
using Microsoft.Data.Sqlite;
using ReactiveUI;

namespace EveCortex.ViewModels;

public class IndustryBrowserViewModel : ReactiveObject
{
    private readonly string _connectionString;
    private CancellationTokenSource _cts = new();
    private static readonly HttpClient _esiHttp = new() { Timeout = TimeSpan.FromSeconds(15) };

    // ── Display data ─────────────────────────────────────────────────────────

    private ObservableCollection<GridRow> _rows = [];
    public  ObservableCollection<GridRow> Rows
    {
        get => _rows;
        private set => this.RaiseAndSetIfChanged(ref _rows, value);
    }

    private GridRow? _selectedRow;
    public  GridRow? SelectedRow
    {
        get => _selectedRow;
        set => this.RaiseAndSetIfChanged(ref _selectedRow, value);
    }

    private string _statusText = "";
    public  string StatusText
    {
        get => _statusText;
        private set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    private List<string> _ownerOptions = ["All Owners"];
    public  List<string> OwnerOptions
    {
        get => _ownerOptions;
        private set => this.RaiseAndSetIfChanged(ref _ownerOptions, value);
    }

    // ── Column definitions ────────────────────────────────────────────────────

    public static readonly string[] DisplayColumns =
    [
        "Status", "Time Remaining", "Activity", "Product", "Runs", "Successful Runs",
        "Items Produced", "Facility", "Installer", "Owner", "Completed Date",
    ];

    // Hidden detail-panel accessors
    public const string ColBlueprintTypeId  = "Blueprint Type Id";
    public const string ColProductTypeId    = "Product Type Id";
    public const string ColFacilityTypeId   = "Facility Type Id";
    public const string ColME               = "ME";
    public const string ColTE               = "TE";
    public const string ColCompletedBy      = "Completed By";
    public const string ColActivityId       = "Activity Id";

    // ── Filter options ────────────────────────────────────────────────────────

    public static readonly string[] ActivityOptions =
    [
        "All Activities", "Manufacturing", "TE Research", "ME Research",
        "Copying", "Invention", "Reverse Eng.", "Reactions",
    ];

    public static readonly string[] StatusOptions =
    [
        "All Statuses", "active", "paused", "ready", "delivered", "cancelled", "reverted",
    ];

    public IndustryBrowserViewModel(string connectionString)
    {
        _connectionString = connectionString;
        EnsureSchema();
    }

    // ── Load ──────────────────────────────────────────────────────────────────

    public Task LoadAsync() => QueryAsync(null, "active", null, null, null, null);

    public Task ApplyFiltersAsync(string? activity, string? status, string? search,
                                  DateTimeOffset? startedFrom, DateTimeOffset? startedThru,
                                  string? owner)
        => QueryAsync(activity, status, search, startedFrom, startedThru, owner);

    // Called by the agent — fuzzy-matches inputs against known option lists.
    public Task ApplyAgentFilterAsync(string? activity, string? status, string? search, string? owner)
    {
        // Resolve activity: find best match in ActivityOptions (or null = All)
        var resolvedActivity = activity is null ? null
            : ActivityOptions.FirstOrDefault(a =>
                  a.Contains(activity, StringComparison.OrdinalIgnoreCase)
                  || activity.Contains(a, StringComparison.OrdinalIgnoreCase));

        // Resolve status: find best match in StatusOptions (or null = All Statuses)
        var resolvedStatus = status is null ? null
            : StatusOptions.FirstOrDefault(s =>
                  s.Equals(status, StringComparison.OrdinalIgnoreCase)
                  || s.Contains(status, StringComparison.OrdinalIgnoreCase));

        // Resolve owner: find best match in OwnerOptions (exact names from DB)
        string? resolvedOwner = null;
        if (!string.IsNullOrEmpty(owner))
        {
            resolvedOwner = _ownerOptions.FirstOrDefault(o =>
                o != "All Owners" && o.Contains(owner, StringComparison.OrdinalIgnoreCase));
        }

        return QueryAsync(resolvedActivity, resolvedStatus, search, null, null, resolvedOwner);
    }

    private async Task QueryAsync(string? activity, string? status, string? search,
                                  DateTimeOffset? startedFrom, DateTimeOffset? startedThru,
                                  string? owner)
    {
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        StatusText = "Loading…";

        try
        {
            var (rows, unresolvedIds) = await Task.Run(
                () => RunQuery(activity, status, search, startedFrom, startedThru, owner, ct), ct);

            // Resolve unknown names (installers + product types) via ESI /universe/names/
            if (unresolvedIds.Count > 0)
            {
                bool anyNew = await EnsureInstallerNamesAsync(unresolvedIds, ct);
                if (anyNew && !ct.IsCancellationRequested)
                    (rows, _) = await Task.Run(
                        () => RunQuery(activity, status, search, startedFrom, startedThru, owner, ct), ct);
            }

            if (ct.IsCancellationRequested) return;

            var ownerOpts = await Task.Run(() => LoadOwnerOptions(), ct);

            Dispatcher.UIThread.Post(() =>
            {
                Rows        = new ObservableCollection<GridRow>(rows);
                OwnerOptions = ownerOpts;
                var active  = rows.Count(r => r["Status"] is "Active" or "Paused" or "Ready");
                StatusText  = $"{rows.Count:N0} job{(rows.Count == 1 ? "" : "s")}" +
                              (active > 0 ? $"  ·  {active} active" : "");
            });
        }
        catch (OperationCanceledException) { }
    }

    private (List<GridRow>, List<long>) RunQuery(
        string? activity, string? status, string? search,
        DateTimeOffset? startedFrom, DateTimeOffset? startedThru, string? owner,
        CancellationToken ct)
    {
        var conds = new List<string>();
        if (!string.IsNullOrEmpty(activity) && activity != "All Activities")
            conds.Add("\"Activity\" = @activity");
        if (!string.IsNullOrEmpty(status) && status != "All Statuses")
            conds.Add("\"Status\" = @status");
        if (!string.IsNullOrEmpty(search))
            conds.Add("(\"Blueprint\" LIKE @search OR \"Product\" LIKE @search)");
        if (startedFrom.HasValue)
            conds.Add("\"Start Date\" >= @startedFrom");
        if (startedThru.HasValue)
            conds.Add("\"Start Date\" < @startedThru");
        if (!string.IsNullOrEmpty(owner) && owner != "All Owners")
            conds.Add("\"Owner\" = @owner");

        var where = conds.Count > 0 ? "WHERE " + string.Join(" AND ", conds) : "";

        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = new SqliteCommand(BuildSql(where), conn);

        if (!string.IsNullOrEmpty(activity) && activity != "All Activities")
            cmd.Parameters.AddWithValue("@activity", activity);
        if (!string.IsNullOrEmpty(status) && status != "All Statuses")
            cmd.Parameters.AddWithValue("@status", status);
        if (!string.IsNullOrEmpty(search))
            cmd.Parameters.AddWithValue("@search", $"%{search}%");
        if (startedFrom.HasValue)
            cmd.Parameters.AddWithValue("@startedFrom", startedFrom.Value.UtcDateTime.ToString("O"));
        if (startedThru.HasValue)
            cmd.Parameters.AddWithValue("@startedThru", startedThru.Value.UtcDateTime.AddDays(1).ToString("O"));
        if (!string.IsNullOrEmpty(owner) && owner != "All Owners")
            cmd.Parameters.AddWithValue("@owner", owner);

        ct.ThrowIfCancellationRequested();

        using var reader       = cmd.ExecuteReader();
        var rows               = new List<GridRow>();
        var unresolvedIds      = new List<long>();

        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();

            var dict = new Dictionary<string, string>(reader.FieldCount + 1);
            for (int i = 0; i < reader.FieldCount; i++)
            {
                var col = reader.GetName(i);
                var val = reader.IsDBNull(i) ? null : reader.GetValue(i);
                dict[col] = FormatValue(col, val);
            }

            // Compute time remaining in C# for initial render; SelectableCell recomputes live
            var jobStatus = dict.GetValueOrDefault("Status", "");
            dict["Time Remaining"] = "";
            if (dict.TryGetValue("End Date Raw", out var endRaw)
                && DateTimeOffset.TryParse(endRaw, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var endDate))
            {
                var rem = endDate.ToUniversalTime() - DateTimeOffset.UtcNow;
                var sl  = jobStatus.ToLowerInvariant();
                if (sl is "active" or "paused")
                    dict["Time Remaining"] = rem > TimeSpan.Zero ? FormatDuration(rem) : "Ready";
                else if (sl == "ready")
                    dict["Time Remaining"] = "Ready";
            }

            // Collect IDs that SdeTypes/Characters couldn't resolve — feed ESI /universe/names/
            var installerVal = dict.GetValueOrDefault("Installer", "");
            if (long.TryParse(installerVal.Replace(",", ""), out var instId))
                unresolvedIds.Add(instId);

            var productVal = dict.GetValueOrDefault("Product", "");
            if (long.TryParse(productVal.Replace(",", ""), out var prodTypeId))
                unresolvedIds.Add(prodTypeId);

            rows.Add(new GridRow(dict));
        }

        return (rows, unresolvedIds.Distinct().ToList());
    }

    // ── Sorting ───────────────────────────────────────────────────────────────

    public void Sort(List<GridRow> rows, string column, bool descending)
    {
        IOrderedEnumerable<GridRow> sorted;

        if (column == "Time Remaining")
        {
            sorted = descending
                ? rows.OrderByDescending(TimeRemainingKey)
                : rows.OrderBy(TimeRemainingKey);
        }
        else
        {
            var sample = rows.FirstOrDefault(r => r[column].Length > 0)?[column] ?? "";
            bool numeric = double.TryParse(sample.Replace(",", ""), out _);
            sorted = numeric
                ? (descending ? rows.OrderByDescending(r => ParseNum(r[column]))
                              : rows.OrderBy(r => ParseNum(r[column])))
                : (descending ? rows.OrderByDescending(r => r[column])
                              : rows.OrderBy(r => r[column]));
        }

        Dispatcher.UIThread.Post(() => Rows = new ObservableCollection<GridRow>(sorted));
    }

    // Sort key for Time Remaining: Ready=0, active jobs by seconds left, done jobs last.
    private static long TimeRemainingKey(GridRow row)
    {
        var status = row["Status"].ToLowerInvariant();
        if (status is "delivered" or "cancelled" or "reverted") return long.MaxValue;
        if (status == "ready") return 0L;
        var raw = row["End Date Raw"];
        if (string.IsNullOrEmpty(raw)) return long.MaxValue - 1;
        if (!DateTimeOffset.TryParse(raw, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var end))
            return long.MaxValue - 1;
        var secs = (long)(end.ToUniversalTime() - DateTimeOffset.UtcNow).TotalSeconds;
        return secs < 0 ? 0L : secs;
    }

    private static double ParseNum(string s) =>
        double.TryParse(s.Replace(",", ""), out var v) ? v : double.MinValue;

    // ── Name resolution ───────────────────────────────────────────────────────

    private void EnsureSchema()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = new SqliteCommand("""
            CREATE TABLE IF NOT EXISTS UniverseNames (
                EntityId INTEGER PRIMARY KEY,
                Name     TEXT    NOT NULL DEFAULT '',
                Category TEXT    NOT NULL DEFAULT ''
            )
            """, conn);
        cmd.ExecuteNonQuery();
    }

    private async Task<bool> EnsureInstallerNamesAsync(List<long> ids, CancellationToken ct)
    {
        var toResolve = new List<long>();

        using (var conn = new SqliteConnection(_connectionString))
        {
            conn.Open();
            foreach (var id in ids.Distinct())
            {
                using var chk = new SqliteCommand(
                    "SELECT 1 FROM UniverseNames WHERE EntityId=@id LIMIT 1", conn);
                chk.Parameters.AddWithValue("@id", id);
                if (chk.ExecuteScalar() is null) toResolve.Add(id);
            }
        }

        if (toResolve.Count == 0) return false;

        bool anyStored = false;
        const string EsiNames = "https://esi.evetech.net/latest/universe/names/?datasource=tranquility";

        for (int i = 0; i < toResolve.Count; i += 1000)
        {
            ct.ThrowIfCancellationRequested();
            var batch = toResolve.Skip(i).Take(1000).ToList();
            try
            {
                var json  = JsonSerializer.Serialize(batch);
                using var body = new StringContent(json, Encoding.UTF8, "application/json");
                using var resp = await _esiHttp.PostAsync(EsiNames, body, ct);
                if (!resp.IsSuccessStatusCode) continue;

                var raw     = await resp.Content.ReadAsStringAsync(ct);
                var entries = JsonSerializer.Deserialize<List<UniverseNameEntry>>(raw);
                if (entries is null || entries.Count == 0) continue;

                using var conn = new SqliteConnection(_connectionString);
                conn.Open();
                using var tx = conn.BeginTransaction();
                foreach (var e in entries)
                {
                    using var ins = new SqliteCommand("""
                        INSERT OR REPLACE INTO UniverseNames (EntityId, Name, Category)
                        VALUES (@id, @name, @cat)
                        """, conn, tx);
                    ins.Parameters.AddWithValue("@id",   e.Id);
                    ins.Parameters.AddWithValue("@name", e.Name);
                    ins.Parameters.AddWithValue("@cat",  e.Category);
                    ins.ExecuteNonQuery();
                    anyStored = true;
                }
                tx.Commit();
            }
            catch { /* network error — ignore, show ID */ }
        }

        return anyStored;
    }

    private sealed record UniverseNameEntry(
        [property: JsonPropertyName("id")]       long   Id,
        [property: JsonPropertyName("name")]     string Name,
        [property: JsonPropertyName("category")] string Category
    );

    // ── Owner filter options ──────────────────────────────────────────────────

    private List<string> LoadOwnerOptions()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = new SqliteCommand("""
            SELECT DISTINCT COALESCE(ch.Name, co.Name, CAST(j.OwnerId AS TEXT)) AS Owner
            FROM EsiIndustryJobs j
            LEFT JOIN Characters   ch ON ch.Id = j.OwnerId AND j.OwnerType = 'character'
            LEFT JOIN Corporations co ON co.Id  = j.OwnerId AND j.OwnerType = 'corporation'
            ORDER BY Owner
            """, conn);

        var list = new List<string> { "All Owners" };
        using var r = cmd.ExecuteReader();
        while (r.Read()) { var s = r.IsDBNull(0) ? "" : r.GetString(0); if (s.Length > 0) list.Add(s); }
        return list;
    }

    // ── Formatting ────────────────────────────────────────────────────────────

    private static string FormatValue(string col, object? val)
    {
        if (val is null) return "";

        if (val is double d)
            return col == "Security" ? d.ToString("F1") : d.ToString("N2");

        if (val is long or int)
            return Convert.ToInt64(val).ToString("N0");

        if (val is decimal m)
            return m.ToString("N2");

        if (val is string s)
        {
            if (col == "Cost"
                && decimal.TryParse(s, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var cost))
                return cost.ToString("N2");

            if ((col is "Start Date" or "End Date" or "Completed Date")
                && DateTimeOffset.TryParse(s, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                return dt.UtcDateTime.ToString("yyyy-MM-dd HH:mm");

            if (col == "Status" && s.Length > 0)
                return char.ToUpper(s[0]) + s[1..];

            // End Date Raw passes through as-is for real-time countdown
        }

        return val.ToString()!;
    }

    private static string FormatDuration(TimeSpan ts)
    {
        if (ts.TotalDays >= 1)    return $"{(int)ts.TotalDays}d {ts.Hours}h {ts.Minutes}m";
        if (ts.TotalHours >= 1)   return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        if (ts.TotalMinutes >= 1) return $"{(int)ts.TotalMinutes}m {ts.Seconds}s";
        return $"{ts.Seconds}s";
    }

    // ── SQL ───────────────────────────────────────────────────────────────────

    private static string BuildSql(string where) => $"""
        WITH Base AS (
            SELECT
                j.JobId                                                                       AS "Job Id",
                CASE j.ActivityId
                    WHEN 1  THEN 'Manufacturing'
                    WHEN 3  THEN 'TE Research'
                    WHEN 4  THEN 'ME Research'
                    WHEN 5  THEN 'Copying'
                    WHEN 7  THEN 'Reverse Eng.'
                    WHEN 8  THEN 'Invention'
                    WHEN 9  THEN 'Reactions'
                    WHEN 11 THEN 'Reactions'
                    ELSE CAST(j.ActivityId AS TEXT)
                END                                                                           AS "Activity",
                COALESCE(bp.Name, CAST(j.BlueprintTypeId AS TEXT))                           AS "Blueprint",
                COALESCE(prod.Name, un_prod.Name, CAST(j.ProductTypeId AS TEXT), '')         AS "Product",
                j.Runs                                                                        AS "Runs",
                j.LicensedRuns                                                                AS "Max Runs",
                j.SuccessfulRuns                                                              AS "Successful Runs",
                -- Items produced = qty per run × runs (from SDE blueprint products)
                CASE
                    WHEN j.ActivityId IN (1, 9, 11) THEN
                        COALESCE((
                            SELECT p2.Quantity FROM SdeBlueprintProducts p2
                            WHERE p2.TypeId = j.BlueprintTypeId
                              AND p2.Activity = CASE j.ActivityId
                                  WHEN 1  THEN 'manufacturing'
                                  WHEN 9  THEN 'reaction'
                                  WHEN 11 THEN 'reaction'
                              END
                              AND (j.ProductTypeId IS NULL OR p2.ProductTypeId = j.ProductTypeId)
                            LIMIT 1
                        ), 1) * j.Runs
                    WHEN j.ActivityId IN (5, 8) THEN j.Runs
                    ELSE NULL
                END                                                                           AS "Items Produced",
                COALESCE(NULLIF(sn.Name, ''), st.Name, CAST(j.FacilityId AS TEXT))          AS "Facility",
                COALESCE(ss_st.Name, ss_sn.Name)                                             AS "Solar System",
                ROUND(COALESCE(ss_st.Security, ss_sn.Security, 0.0), 1)                     AS "Security",
                COALESCE(r_st.Name,  r_sn.Name)                                              AS "Region",
                COALESCE(ch_inst.Name, un_inst.Name, CAST(j.InstallerId AS TEXT))            AS "Installer",
                COALESCE(ch_own.Name, co.Name, CAST(j.OwnerId AS TEXT))                      AS "Owner",
                j.Cost                                                                        AS "Cost",
                j.Status                                                                      AS "Status",
                j.StartDate                                                                   AS "Start Date",
                j.EndDate                                                                     AS "End Date",
                j.EndDate                                                                     AS "End Date Raw",
                j.CompletedDate                                                               AS "Completed Date",
                -- Hidden: detail panel only
                j.BlueprintTypeId                                                             AS "Blueprint Type Id",
                COALESCE(j.ProductTypeId, 0)                                                 AS "Product Type Id",
                COALESCE(st.StationTypeId, cs.TypeId)                                        AS "Facility Type Id",
                COALESCE(bl.MaterialEfficiency, 0)                                           AS "ME",
                COALESCE(bl.TimeEfficiency, 0)                                               AS "TE",
                COALESCE(ch_comp.Name, '')                                                   AS "Completed By",
                j.ActivityId                                                                  AS "Activity Id",
                j.Probability                                                                 AS "Probability",
                j.OwnerId                                                                     AS "Owner Id",
                j.OwnerType                                                                   AS "Owner Type"
            FROM EsiIndustryJobs j
            LEFT JOIN Characters       ch_inst  ON ch_inst.Id        = j.InstallerId
            LEFT JOIN UniverseNames    un_inst  ON un_inst.EntityId   = j.InstallerId
            LEFT JOIN UniverseNames    un_prod  ON un_prod.EntityId   = j.ProductTypeId
            LEFT JOIN Characters       ch_own   ON ch_own.Id          = j.OwnerId  AND j.OwnerType = 'character'
            LEFT JOIN Corporations     co       ON co.Id               = j.OwnerId  AND j.OwnerType = 'corporation'
            LEFT JOIN SdeTypes         bp       ON bp.TypeId           = j.BlueprintTypeId
            LEFT JOIN SdeTypes         prod     ON prod.TypeId         = j.ProductTypeId
            LEFT JOIN SdeStations      st       ON st.StationId        = j.FacilityId
            LEFT JOIN EsiStructureNames sn      ON sn.StructureId      = j.FacilityId
            LEFT JOIN SdeSolarSystems  ss_st    ON ss_st.SolarSystemId = st.SolarSystemId
            LEFT JOIN SdeSolarSystems  ss_sn    ON ss_sn.SolarSystemId = sn.SolarSystemId
            LEFT JOIN SdeRegions       r_st     ON r_st.RegionId       = COALESCE(st.RegionId, ss_st.RegionId)
            LEFT JOIN SdeRegions       r_sn     ON r_sn.RegionId       = ss_sn.RegionId
            LEFT JOIN EsiBlueprints    bl       ON bl.ItemId           = j.BlueprintId
                                               AND bl.OwnerId          = j.OwnerId
                                               AND bl.OwnerType        = j.OwnerType
            LEFT JOIN Characters       ch_comp  ON ch_comp.Id          = j.CompletedCharacterId
            LEFT JOIN (SELECT DISTINCT StructureId, TypeId FROM EsiCorpStructures) cs
                                                ON cs.StructureId      = j.FacilityId
        )
        SELECT * FROM Base
        {where}
        ORDER BY
            CASE WHEN "Status" IN ('active', 'paused') THEN 0
                 WHEN "Status" = 'ready'               THEN 1
                 ELSE 2 END,
            "End Date" ASC
        """;
}
