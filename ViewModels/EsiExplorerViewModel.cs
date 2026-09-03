using System.Collections.ObjectModel;
using Avalonia.Threading;
using Microsoft.Data.Sqlite;
using ReactiveUI;

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
        StatusText = "Loading…";

        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);

            int total;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"""SELECT COUNT(*) FROM "{entry.SqlTable}" {BuildWhere()}""";
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
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);

            int total;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"""SELECT COUNT(*) FROM "{_currentEntry.SqlTable}" {BuildWhere()}""";
                AddFilterParams(cmd);
                total = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct) ?? 0);
            }

            await AppendPageAsync(conn, _currentEntry, total, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText = $"Error: {ex.Message}"; }
    }

    private async Task AppendPageAsync(SqliteConnection conn, TableEntry entry, int total, CancellationToken ct)
    {
        var where = BuildWhere();
        var order = _sortColumn is not null
            ? $"ORDER BY \"{_sortColumn}\" {(_sortDescending ? "DESC" : "ASC")}"
            : entry.OrderBy is not null ? $"ORDER BY {entry.OrderBy}" : "";

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT * FROM "{entry.SqlTable}" {where} {order}
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

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string BuildWhere()
    {
        if (_activeFilters.Count == 0) return "";
        var clauses = _activeFilters.Select((f, i) => $"\"{f.Column}\" {f.Op.Sql} @fv{i}");
        return $"WHERE {string.Join(" AND ", clauses)}";
    }

    private void AddFilterParams(SqliteCommand cmd)
    {
        for (int i = 0; i < _activeFilters.Count; i++)
        {
            var f   = _activeFilters[i];
            var val = f.Op.UseLike ? $"%{f.Value}%" : f.Value;
            cmd.Parameters.AddWithValue($"@fv{i}", val);
        }
    }
}
