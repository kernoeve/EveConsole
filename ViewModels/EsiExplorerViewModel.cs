using System.Data.Common;
using System.Globalization;
using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Threading;
using Microsoft.Data.Sqlite;
using ReactiveUI;
using EveConsole.Data;

namespace EveConsole.ViewModels;

public record TableEntry(string DisplayName, string SqlTable, string? OrderBy = null);

public class GridRow
{
    private readonly Dictionary<string, string> _data;
    public GridRow(Dictionary<string, string> data) => _data = data;
    public string this[string col] => _data.TryGetValue(col, out var v) ? v : "";
    public IEnumerable<string> Keys => _data.Keys;

    /// <summary>Add or replace a value after construction. Used for columns that come
    /// from somewhere other than the query — the Industry Jobs rig note, for one,
    /// which needs an async lookup the synchronous reader loop can't do.</summary>
    public void Set(string col, string value) => _data[col] = value;
}

public class FilterOp(string label, string sql, bool useLike = false)
{
    public string Label   { get; } = label;
    public string Sql     { get; } = sql;
    public bool   UseLike { get; } = useLike;
    public override string ToString() => Label;
}

/// <summary>
/// Turning one typed-in filter into SQL that both engines accept.
///
/// <para>⚠️ The value always arrives as a string — it was typed into a box — while the column it
/// is compared against may be bigint, double, boolean or a timestamp. SQLite does not mind, and
/// this shape worked there for years. PostgreSQL does: filtering Location Id gave
/// <c>42883: operator does not exist: bigint = text</c>.</para>
///
/// <para>Two rules, and which one applies is decided by the OPERATOR, not by guessing at the
/// column's type from the value. Contains, Equal and their negations are text questions about
/// what is on screen, so the column is cast to text and the comparison is textual on any column.
/// Greater and Less are ordering questions, where text would sort 9 after 10 — those keep the
/// column as it is and type the PARAMETER instead, so a number is bound as a number.</para>
/// </summary>
internal static class SqlFilter
{
    private static bool IsTextual(FilterOp op) => op.UseLike || op.Sql is "=" or "!=";

public static string Clause(string column, FilterOp op, int index) =>
        op.UseLike
            // ⚠️ LOWER on both sides. PostgreSQL LIKE is case-SENSITIVE where SQLite is not, so
            // "isotropic" matched nothing against "Isotropic Neofullerene" on one engine only.
            ? $"LOWER(CAST(\"{column}\" AS TEXT)) {op.Sql} LOWER(@fv{index})"
            : IsTextual(op)
                ? $"CAST(\"{column}\" AS TEXT) {op.Sql} @fv{index}"
                : $"\"{column}\" {op.Sql} @fv{index}";

    public static object Value(FilterOp op, string value) =>
        op.UseLike     ? $"%{value}%"
      : IsTextual(op)  ? value
      : long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l
      : double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d
      : value;
}

public class EsiExplorerViewModel : ReactiveObject
{
    private readonly string _connectionString;
    private CancellationTokenSource _cts = new();
    private const int PageSize = 5000;
    private int _offset;
    private TableEntry? _currentEntry;
    private string?   _sortColumn;
    private bool      _sortDescending;

    private record ActiveFilter(string Column, FilterOp Op, string Value);
    private readonly List<ActiveFilter> _activeFilters = [];

    public static readonly List<FilterOp> Operators =
    [
        new("Contains",              "LIKE",     useLike: true),
        new("Does Not Contain",      "NOT LIKE", useLike: true),
        new("Equal",                 "="),
        new("Not Equal",             "!="),
        new("Greater Than",          ">"),
        new("Greater Than or Equal", ">="),
        new("Less Than",             "<"),
        new("Less Than or Equal",    "<="),
    ];

    // ── All tables (flat list — shared tables show full contents) ────────────

    public List<TableEntry> AllTables { get; } = [
        new("Wallet Balances",     "EsiWalletBalances"),
        new("Wallet Journal",      "EsiWalletJournal",      "\"Date\" DESC"),
        new("Wallet Transactions", "EsiWalletTransactions", "\"Date\" DESC"),
        new("Skills",              "EsiSkills"),
        new("Skill Queue",         "EsiSkillQueue",         "QueuePosition"),
        new("Attributes",          "EsiCharacterAttributes"),
        new("Fatigue",             "EsiCharacterFatigues"),
        new("Clone State",         "EsiCloneStates"),
        new("Jump Clones",         "EsiJumpClones"),
        new("Jump Clone Implants", "EsiJumpCloneImplants"),
        new("Implants",            "EsiImplants"),
        new("Assets",              "EsiAssets"),
        new("Blueprints",          "EsiBlueprints"),
        new("Industry Jobs",       "EsiIndustryJobs",       "\"StartDate\" DESC"),
        new("Market Orders",       "EsiMarketOrders",       "\"Issued\" DESC"),
        new("Contracts",           "EsiContracts",          "\"DateIssued\" DESC"),
        new("Contacts",            "EsiContacts"),
        new("Kill Mails",          "EsiKillMailRefs"),
        new("Standings",           "EsiStandings"),
        new("Mining",              "EsiMining",             "\"Date\" DESC"),
        new("Notifications",       "EsiNotifications",      "\"Timestamp\" DESC"),
        new("Planetary Colonies",  "EsiPlanetaryColonies"),
        new("Agent Research",      "EsiAgentResearch"),
        new("Loyalty Points",      "EsiLoyaltyPoints"),
        new("Medals",              "EsiMedals"),
        new("Titles",              "EsiTitles"),
        new("Roles",               "EsiRoles"),
        new("Fittings",            "EsiFittings"),
        new("Fitting Items",       "EsiFittingItems"),
        new("Corp Divisions",      "EsiCorpDivisions",      "Division"),
        new("Corp Members",        "EsiCorpMembers"),
        new("Corp Member Roles",   "EsiCorpMemberRoles"),
        new("Corp Titles",         "EsiCorpTitles"),
        new("Corp Medals",         "EsiCorpMedals"),
        new("Corp Structures",     "EsiCorpStructures"),
        new("Corp Starbases",      "EsiCorpStarbases"),
        new("Corp Facilities",     "EsiCorpFacilities"),
        new("API Call Records",    "EsiCallRecords",        "\"LastCalledAt\" DESC"),
    ];

    // ── Reactive state ───────────────────────────────────────────────────────

    private TableEntry? _selectedTable;
    public TableEntry? SelectedTable
    {
        get => _selectedTable;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedTable, value);
            _activeFilters.Clear();
            _sortColumn     = null;
            _sortDescending = false;
            if (value is not null)
            {
                _cts.Cancel();
                _cts = new CancellationTokenSource();
                _ = LoadTableAsync(value, _cts.Token);
            }
        }
    }

    public ObservableCollection<GridRow> Rows { get; } = [];

    private List<string> _columns = [];
    public List<string> Columns
    {
        get => _columns;
        private set => this.RaiseAndSetIfChanged(ref _columns, value);
    }

    private string _statusText = "Select a table from the list to view its data.";
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

    private bool _hasTable;
    /// <summary>A table is chosen, so there is something to export.</summary>
    public bool HasTable
    {
        get => _hasTable;
        private set => this.RaiseAndSetIfChanged(ref _hasTable, value);
    }

    private bool _isExporting;
    /// <summary>An export is running: the button reads Cancel, and a second cannot start.</summary>
    public bool IsExporting
    {
        get => _isExporting;
        private set { this.RaiseAndSetIfChanged(ref _isExporting, value); this.RaisePropertyChanged(nameof(ExportButtonText)); }
    }

    public string ExportButtonText => IsExporting ? "Cancel export" : "Export CSV";

    // ── Constructor ──────────────────────────────────────────────────────────

    public EsiExplorerViewModel(string connectionString)
    {
        _connectionString = connectionString;
    }

    // ── Filter ────────────────────────────────────────────────────────────────

    public async Task ApplyFiltersAsync(
        IReadOnlyList<(string? Column, FilterOp? Op, string? Value)> filters)
    {
        _activeFilters.Clear();
        foreach (var (col, op, val) in filters)
        {
            if (!string.IsNullOrWhiteSpace(col) && op is not null && !string.IsNullOrWhiteSpace(val))
                _activeFilters.Add(new ActiveFilter(col, op, val));
        }

        if (_currentEntry is null) return;
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        await LoadTableAsync(_currentEntry, _cts.Token);
    }

    public async Task ClearFiltersAsync()
    {
        _activeFilters.Clear();
        if (_currentEntry is null) return;
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        await LoadTableAsync(_currentEntry, _cts.Token);
    }

    // ── Sort ─────────────────────────────────────────────────────────────────

    public async Task SortAsync(string column)
    {
        if (_sortColumn == column)
            _sortDescending = !_sortDescending;
        else
        {
            _sortColumn     = column;
            _sortDescending = false;
        }

        if (_currentEntry is null) return;
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        await LoadTableAsync(_currentEntry, _cts.Token);
    }

    // ── Data loading ─────────────────────────────────────────────────────────

    private async Task LoadTableAsync(TableEntry entry, CancellationToken ct)
    {
        _currentEntry = entry;
        _offset       = 0;

        Rows.Clear();
        Columns    = [];
        HasMore    = false;
        HasTable   = true;
        StatusText = "Loading…";

        try
        {
            await using var conn = AppDb.Connect();
            await conn.OpenAsync(ct);

            int total;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = AppDb.CaseInsensitiveLike($"""SELECT COUNT(*) FROM "{entry.SqlTable}" {BuildWhere()}""");
                AddFilterParams(cmd);
                total = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct) ?? 0);
            }

            await AppendPageAsync(conn, entry, total, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText = $"Error: {ex.Message}"; }
    }

    public async Task LoadMoreAsync()
    {
        if (!HasMore || _currentEntry is null) return;

        _cts.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        StatusText = "Loading…";

        try
        {
            await using var conn = AppDb.Connect();
            await conn.OpenAsync(ct);

            int total;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = AppDb.CaseInsensitiveLike($"""SELECT COUNT(*) FROM "{_currentEntry.SqlTable}" {BuildWhere()}""");
                AddFilterParams(cmd);
                total = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct) ?? 0);
            }

            await AppendPageAsync(conn, _currentEntry, total, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText = $"Error: {ex.Message}"; }
    }

    private async Task AppendPageAsync(DbConnection conn, TableEntry entry, int total, CancellationToken ct)
    {
        using var cmd = conn.Command($"""
            SELECT * FROM "{entry.SqlTable}" {BuildWhere()} {BuildOrder(entry)}
            LIMIT {PageSize} OFFSET {_offset}
            """);
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
                row[reader.GetName(i)] = reader.IsDBNull(i) ? "" : reader.GetValue(i).ToString()!;
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
            StatusText = total == 0         ? "No rows."
                : loadedCount < total ? $"Showing {loadedCount:N0} of {total:N0} rows"
                                      : $"{total:N0} rows total";
        });
    }

    // ── Export ────────────────────────────────────────────────────────────────

    private CancellationTokenSource? _exportCts;

    public void CancelExport() => _exportCts?.Cancel();

    /// <summary>A word from the view for the status line.</summary>
    public void ShowStatus(string text) => StatusText = text;

    /// <summary>
    /// Writes every row of the table that matches the filters, in the grid's order, as CSV — all
    /// of them, not the page the grid holds — one row at a time off the reader on a worker, so a
    /// table of a million rows never sits in memory and the window stays live. Numbers are written
    /// invariant and dates as UTC "yyyy-MM-dd HH:mm:ss", for the tools that will read the file.
    /// The filters are read once, as the export starts. True when the whole table was written.
    /// </summary>
    public async Task<bool> ExportCsvAsync(Stream stream, string fileName)
    {
        if (_currentEntry is null || IsExporting) return false;
        var entry = _currentEntry;
        var sql   = $"""SELECT * FROM "{entry.SqlTable}" {BuildWhere()} {BuildOrder(entry)}""";
        var args  = _activeFilters.Select(f => SqlFilter.Value(f.Op, f.Value)).ToList();
        var cts   = new CancellationTokenSource();
        _exportCts  = cts;
        IsExporting = true;
        StatusText  = "Exporting…";
        var written = 0;
        try
        {
            await Task.Run(async () =>
            {
                var ct = cts.Token;
                await using var conn = AppDb.Connect();
                await conn.OpenAsync(ct);
                using var cmd = conn.Command(sql);
                for (var i = 0; i < args.Count; i++) cmd.AddWithValue($"@fv{i}", args[i]);
                using var reader = await cmd.ExecuteReaderAsync(ct);

                await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1 << 16) { NewLine = "\r\n" };
                var fields = new string[reader.FieldCount];
                for (var i = 0; i < fields.Length; i++) fields[i] = CsvField(reader.GetName(i));
                await writer.WriteLineAsync(string.Join(",", fields));
                while (await reader.ReadAsync(ct))
                {
                    ct.ThrowIfCancellationRequested();   // the SQLite reader does not look at the token itself
                    for (var i = 0; i < fields.Length; i++)
                        fields[i] = reader.IsDBNull(i) ? "" : CsvField(CsvValue(reader.GetValue(i)));
                    await writer.WriteLineAsync(string.Join(",", fields));
                    if (++written % 5000 == 0)
                    {
                        var soFar = written;
                        await Dispatcher.UIThread.InvokeAsync(() => StatusText = $"Exporting… {soFar:N0} rows");
                    }
                }
                await writer.FlushAsync(ct);
            });
            StatusText = $"Exported {written:N0} rows to {fileName}";
            return true;
        }
        catch (OperationCanceledException)
        {
            StatusText = $"Export cancelled after {written:N0} rows; the file was not kept.";
            return false;
        }
        catch (Exception ex)
        {
            StatusText = $"Export failed: {ex.Message}";
            return false;
        }
        finally
        {
            IsExporting = false;
            _exportCts  = null;
        }
    }

    private static readonly char[] CsvSpecial = ['"', ',', '\r', '\n'];

    /// <summary>A field as RFC 4180 has it: quoted when it holds a comma, a quote or a line break,
    /// a quote inside doubled.</summary>
    private static string CsvField(string s) =>
        s.IndexOfAny(CsvSpecial) < 0 ? s : "\"" + s.Replace("\"", "\"\"") + "\"";

    /// <summary>A value as a tool reads it: invariant numbers, UTC dates, whatever the engine gave otherwise.</summary>
    private static string CsvValue(object v) => v switch
    {
        DateTime d       => d.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        DateTimeOffset d => d.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        IFormattable f   => f.ToString(null, CultureInfo.InvariantCulture),
        _                => v.ToString() ?? "",
    };

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>The grid's order: the column clicked, else the table's own.</summary>
    private string BuildOrder(TableEntry entry) =>
        _sortColumn is not null ? $"ORDER BY \"{_sortColumn}\" {(_sortDescending ? "DESC" : "ASC")}"
        : entry.OrderBy is not null ? $"ORDER BY {entry.OrderBy}" : "";

    private string BuildWhere()
    {
        if (_activeFilters.Count == 0) return "";
        var clauses = _activeFilters.Select((f, i) => SqlFilter.Clause(f.Column, f.Op, i));
        return $"WHERE {string.Join(" AND ", clauses)}";
    }

    private void AddFilterParams(DbCommand cmd)
    {
        for (int i = 0; i < _activeFilters.Count; i++)
        {
            var f   = _activeFilters[i];
            cmd.AddWithValue($"@fv{i}", SqlFilter.Value(f.Op, f.Value));
        }
    }
}
