using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Collections;
using EveConsole.Models;
using EveConsole.Services;
using Microsoft.Data.Sqlite;
using ReactiveUI;
using EveConsole.Data;
using EveConsole.Localization;

namespace EveConsole.ViewModels;

// Which market number to compare the build cost against.
public enum IndustryMode { BuildAndSellOrder, BuildAndSellToBuyOrder }
public record IndustryModeOption(string Label, IndustryMode Kind);

// A market pricing config the user can price against (reuses the Market Sources configs).
public record IndustryMarketConfig(int ConfigId, string Name, string Method, long LocationId);

public class IndustryRow
{
    public int    TypeId           { get; init; }
    public string TypeName         { get; init; } = "";

    public bool HasItemLink => TypeId > 0 && TypeName.Length > 0;
    public void OpenItem() => EveConsole.Services.EntityNavigator.Instance.Item(TypeId);

    public double BuildCost        { get; init; }
    public double SellPrice        { get; init; }   // market number we sell into (sell or buy order)
    public bool   HasSellOrders    { get; init; } = true; // false → priced from 30-day history avg
    public double ProfitPerUnit    { get; init; }
    public double Margin           { get; init; }   // profit / build cost
    public double BuildSeconds     { get; init; }   // to build ONE unit (ties up the slot this long)
    public double SlotDays         { get; init; }   // BuildSeconds / 86400
    public double ProfitPerSlotDay { get; init; }
    public double UnitVol30d       { get; init; }   // units traded in the config region, last 30d
    public double IskVol30d        { get; init; }   // ISK traded in the config region, last 30d

    // What the last 30 days actually paid, beside what the order book says now. With no sales
    // there is no price to judge by: the margin is NaN (shown "–", sorted below every number,
    // never passing a filter) and the profit 0.
    public bool   HasSales30d      => UnitVol30d > 0;
    public double AvgPrice30d      => HasSales30d ? IskVol30d / UnitVol30d : 0;   // volume-weighted
    public double Margin30d        => HasSales30d ? (AvgPrice30d - BuildCost) / BuildCost : double.NaN;
    public double Profit30d        => HasSales30d ? IskVol30d - BuildCost * UnitVol30d : 0;   // every unit sold, built at BuildCost

    public string BuildCostDisplay     => FormatIsk(BuildCost);
    // "*" marks a sell price derived from 30-day history because there are no sell orders.
    public string SellPriceDisplay     => HasSellOrders ? FormatIsk(SellPrice) : FormatIsk(SellPrice) + " *";
    public string ProfitUnitDisplay    => FormatIsk(ProfitPerUnit);
    public string MarginDisplay        => $"{Margin * 100:N1}%";
    public string BuildTimeDisplay      => FormatDuration(BuildSeconds);
    public string SlotDaysDisplay       => $"{SlotDays:N2}";
    public string ProfitPerSlotDayDisplay => FormatIsk(ProfitPerSlotDay);
    public string UnitVol30dDisplay     => $"{UnitVol30d:N0}";
    public string IskVol30dDisplay      => FormatIsk(IskVol30d);
    public string AvgPrice30dDisplay    => HasSales30d ? FormatIsk(AvgPrice30d) : "–";
    public string Margin30dDisplay      => HasSales30d ? $"{Margin30d * 100:N1}%" : "–";
    public string Profit30dDisplay      => HasSales30d ? FormatIsk(Profit30d) : "–";

    private static string FormatDuration(double seconds)
    {
        if (seconds <= 0) return "–";
        var ts = TimeSpan.FromSeconds(seconds);
        if (ts.TotalDays  >= 1) return $"{(int)ts.TotalDays}d {ts.Hours}h";
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        if (ts.TotalMinutes >= 1) return $"{(int)ts.TotalMinutes}m";
        return $"{(int)ts.TotalSeconds}s";
    }

    private static string FormatIsk(double v)
    {
        var abs = Math.Abs(v);
        var sign = v < 0 ? "-" : "";
        return abs switch
        {
            >= 1_000_000_000_000 => $"{sign}{abs / 1_000_000_000_000:N2}T",
            >= 1_000_000_000     => $"{sign}{abs / 1_000_000_000:N2}B",
            >= 1_000_000         => $"{sign}{abs / 1_000_000:N2}M",
            _                    => $"{sign}{abs:N2}",
        };
    }
}

public class IndustryOpportunitiesViewModel : ReactiveObject
{
    private readonly string                        _connString;
    private readonly BatchAddService               _batchSvc;
    private readonly IndustryOpportunitiesService  _service;

    // ── Mode ──────────────────────────────────────────────────────────────────

    public List<IndustryModeOption> ModeOptions { get; } =
    [
        new(IndustryText.ModeBuildSellOrder,      IndustryMode.BuildAndSellOrder),
        new(IndustryText.ModeBuildSellToBuyOrder, IndustryMode.BuildAndSellToBuyOrder),
    ];

    // ⚠️ Every choice on this screen is kept here and remembered in UiState, never left to its
    // control. The tab rebuilds its controls each time it is shown, so anything held only by a
    // control — the chosen market, the grid's sort — was lost on leaving the tab and coming back.

    private IndustryModeOption _selectedMode;
    public IndustryModeOption SelectedMode
    {
        get => _selectedMode;
        set
        {
            if (value is null) return;
            this.RaiseAndSetIfChanged(ref _selectedMode, value);
            UiState.Set(UiState.IndustryOppsMode, value.Kind.ToString());
        }
    }

    // ── Market config (pricing source) ────────────────────────────────────────

    public ObservableCollection<IndustryMarketConfig> MarketConfigs { get; } = [];

    /// <summary>True while the market list is being rebuilt.</summary>
    private bool _reloadingConfigs;

    private IndustryMarketConfig? _selectedConfig;
    public IndustryMarketConfig? SelectedConfig
    {
        get => _selectedConfig;
        set
        {
            // ⚠️ A null while the list is rebuilt is the dropdown losing the item it showed, not
            // the user clearing the choice — it is what used to empty "Price At".
            if (value is null && _reloadingConfigs) return;
            this.RaiseAndSetIfChanged(ref _selectedConfig, value);
            if (value is not null) UiState.SetLong(UiState.IndustryOppsPriceAt, value.ConfigId);
        }
    }

    // ── Filters ────────────────────────────────────────────────────────────────

    // The two volume boxes are remembered when Calculate uses them rather than on every
    // keystroke; until then the view model, which outlives the tab, holds what was typed.
    /// <summary>Lowest margin kept, as a percentage of the build cost — the Margin column's
    /// figure. Remembered when Calculate uses it, as the volume boxes are.</summary>
    private string _minMargin = UiState.Get(UiState.IndustryOppsMinMargin) ?? "";
    public string MinMargin
    {
        get => _minMargin;
        set => this.RaiseAndSetIfChanged(ref _minMargin, value);
    }

    /// <summary>Lowest 30-day margin kept, in percent — the Margin 30d column's figure. An item
    /// that did not sell in the last 30 days has none, so it is left out while this is set.</summary>
    private string _minMargin30d = UiState.Get(UiState.IndustryOppsMinMargin30d) ?? "";
    public string MinMargin30d
    {
        get => _minMargin30d;
        set => this.RaiseAndSetIfChanged(ref _minMargin30d, value);
    }

    private string _minIskVolume = UiState.Get(UiState.IndustryOppsMinIskVol) ?? "";
    public string MinIskVolume
    {
        get => _minIskVolume;
        set => this.RaiseAndSetIfChanged(ref _minIskVolume, value);
    }

    private string _minUnitVolume = UiState.Get(UiState.IndustryOppsMinUnitVol) ?? "";
    public string MinUnitVolume
    {
        get => _minUnitVolume;
        set => this.RaiseAndSetIfChanged(ref _minUnitVolume, value);
    }

    /// <summary>
    /// The meta groups kept, each a tick box, remembered as they are ticked.
    ///
    /// <para>Faction starts unticked: its items are mostly ME0 copies that are often not worth
    /// building, which is why the box this replaced, "Skip faction", was on by default. Somebody
    /// who had turned that off starts with Faction ticked.</para>
    /// </summary>
    public MultiChoice MetaGroups { get; private set; } = null!;

    private void LoadMetaGroups()
    {
        var saved = UiState.Get(UiState.IndustryOppsMetaGroups);
        int[] on = saved is not null
            ? [.. saved.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => int.TryParse(s, out var id) ? id : -1)]
            : UiState.GetBool(UiState.IndustryOppsSkipFaction, true)
                ? OppsMetaGroups.Default
                : [.. OppsMetaGroups.All.Select(g => g.Id)];

        MetaGroups = new MultiChoice(OppsMetaGroups.All.Select(g =>
            new CategoryChoice(g.Id.ToString(), g.Label) { Selected = on.Contains(g.Id) }));
        MetaGroups.SelectionChanged += () =>
            UiState.Set(UiState.IndustryOppsMetaGroups, string.Join(",", MetaGroups.SelectedKeys));
    }

    // Only items whose blueprint is a buyable BPO, or is invented from one (T2 from a T1
    // BPO). Excludes only items built from BPCs with no obtainable BPO — faction and
    // limited-run items — whose blueprint/contract cost we can't account for.
    private bool _bpoOnly = UiState.GetBool(UiState.IndustryOppsBpoOnly, true);
    public bool BpoOnly
    {
        get => _bpoOnly;
        set
        {
            this.RaiseAndSetIfChanged(ref _bpoOnly, value);
            UiState.SetBool(UiState.IndustryOppsBpoOnly, value);
        }
    }

    // Only items whose own blueprint — an original or a copy — sits with one of the user's
    // characters or personal corporations: what can be started without buying a print first
    // (issue #152). Off by default: the list is also for finding what is worth a print.
    private bool _ownedOnly = UiState.GetBool(UiState.IndustryOppsOwnedOnly, false);
    public bool OwnedOnly
    {
        get => _ownedOnly;
        set
        {
            this.RaiseAndSetIfChanged(ref _ownedOnly, value);
            UiState.SetBool(UiState.IndustryOppsOwnedOnly, value);
        }
    }

    // ── Name filter ───────────────────────────────────────────────────────────

    /// <summary>Narrows the results to the items whose name contains what was typed, as it is
    /// typed. See <see cref="ItemNameFilter{T}"/>.</summary>
    public ItemNameFilter<IndustryRow> NameFilter { get; }

    // ── Excluded market groups (and everything nested under them) ────────────

    public ObservableCollection<ExcludedMarketGroupVm> ExcludedMarketGroups { get; } = [];

    public Func<Task<MarketGroupPickerResult?>>? ShowMarketGroupPickerDialog { get; set; }

    public BatchAddService GetBatchAddService() => _batchSvc;

    public ReactiveCommand<Unit, Unit>                  AddExcludedGroupCommand    { get; }
    public ReactiveCommand<ExcludedMarketGroupVm, Unit> RemoveExcludedGroupCommand { get; }

    // ── Included market groups: when any are chosen, only items in them (or nested under them) ──

    public ObservableCollection<ExcludedMarketGroupVm> IncludedMarketGroups { get; } = [];

    public ReactiveCommand<Unit, Unit>                  AddIncludedGroupCommand    { get; }
    public ReactiveCommand<ExcludedMarketGroupVm, Unit> RemoveIncludedGroupCommand { get; }

    private const string ExcludedColumn = "ExcludedMarketGroupIds";
    private const string IncludedColumn = "IncludedMarketGroupIds";

    private Task AddExcludedGroupAsync() => AddGroupAsync(ExcludedMarketGroups, ExcludedColumn);
    private Task AddIncludedGroupAsync() => AddGroupAsync(IncludedMarketGroups, IncludedColumn);

    private Task RemoveExcludedGroupAsync(ExcludedMarketGroupVm group) => RemoveGroupAsync(ExcludedMarketGroups, ExcludedColumn, group);
    private Task RemoveIncludedGroupAsync(ExcludedMarketGroupVm group) => RemoveGroupAsync(IncludedMarketGroups, IncludedColumn, group);

    private async Task AddGroupAsync(ObservableCollection<ExcludedMarketGroupVm> list, string column)
    {
        if (ShowMarketGroupPickerDialog is null) return;
        var pick = await ShowMarketGroupPickerDialog();
        if (pick is null) return;
        if (list.Any(g => g.MarketGroupId == pick.MarketGroupId)) return;

        list.Add(new ExcludedMarketGroupVm(pick.MarketGroupId,
            SdeNames.MarketGroup(pick.MarketGroupId, pick.GroupName)));
        await SaveGroupsAsync(list, column);
    }

    private async Task RemoveGroupAsync(ObservableCollection<ExcludedMarketGroupVm> list, string column, ExcludedMarketGroupVm group)
    {
        list.Remove(group);
        await SaveGroupsAsync(list, column);
    }

    private async Task LoadExcludedGroupsAsync()
    {
        await LoadGroupsAsync(ExcludedMarketGroups, ExcludedColumn);
        await LoadGroupsAsync(IncludedMarketGroups, IncludedColumn);
    }

    /// <summary>
    /// One of the two lists from the settings row.
    ///
    /// <para>⚠️ Each statement is written out whole, chosen by the column, never the column name
    /// spliced in: PgSqlCheck can only typecheck SQL it can read, and took a spliced name for a
    /// parameter.</para>
    /// </summary>
    private static async Task LoadGroupsAsync(ObservableCollection<ExcludedMarketGroupVm> list, string column)
    {
        // The names are shown once and kept, so they wait for the interface language's first.
        await SdeNames.EnsureLoadedAsync();

        using var conn = AppDb.Connect();
        await conn.OpenAsync();
        using var cmd = conn.Command(column == IncludedColumn
            ? """SELECT "IncludedMarketGroupIds" FROM "IndustryOpportunitiesSettings" WHERE "Id" = 1"""
            : """SELECT "ExcludedMarketGroupIds" FROM "IndustryOpportunitiesSettings" WHERE "Id" = 1""");
        var raw = (await cmd.ExecuteScalarAsync()) as string ?? "";

        var ids = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var id) ? id : (int?)null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToList();
        if (ids.Count == 0) return;

        using var nameCmd = conn.Command($"""SELECT "MarketGroupId", "Name" FROM "SdeMarketGroups" WHERE "MarketGroupId" IN ({string.Join(",", ids)})""");
        var names = new Dictionary<int, string>();
        using (var reader = await nameCmd.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                names[reader.GetInt32(0)] = reader.GetString(1);

        list.Clear();
        foreach (var id in ids)
            if (names.TryGetValue(id, out var name))
                list.Add(new ExcludedMarketGroupVm(id, SdeNames.MarketGroup(id, name)));
    }

    private static async Task SaveGroupsAsync(ObservableCollection<ExcludedMarketGroupVm> list, string column)
    {
        var csv = string.Join(",", list.Select(g => g.MarketGroupId));
        using var conn = AppDb.Connect();
        await conn.OpenAsync();
        using var cmd = conn.Command(column == IncludedColumn
            ? """UPDATE "IndustryOpportunitiesSettings" SET "IncludedMarketGroupIds" = @ids WHERE "Id" = 1"""
            : """UPDATE "IndustryOpportunitiesSettings" SET "ExcludedMarketGroupIds" = @ids WHERE "Id" = 1""");
        cmd.AddWithValue("@ids", csv);
        await cmd.ExecuteNonQueryAsync();
    }

    // ── Item navigation callback (set by MainWindow) ──────────────────────────

    public Action<int, string>? ItemNavigationRequested { get; set; }

    public void RequestItemNavigation(int typeId, string typeName)
        => ItemNavigationRequested?.Invoke(typeId, typeName);

    // ── Results ───────────────────────────────────────────────────────────────

    public ObservableCollection<IndustryRow> Results { get; } = [];

    /// <summary>
    /// The results as the grid shows them, sorted.
    ///
    /// <para>⚠️ Owned here, not by the grid. A grid bound to <see cref="Results"/> keeps its sort
    /// in a view of its own, which goes with the grid when the tab is left; bound to this, a
    /// rebuilt grid finds the sort — and draws its header arrow — from where it was left. The sort
    /// is also remembered between sessions, and starts as Profit 30d, highest first.</para>
    /// </summary>
    public DataGridCollectionView ResultsView { get; }

    private static readonly HashSet<string> SortablePaths =
        typeof(IndustryRow).GetProperties().Select(p => p.Name).ToHashSet();

    private void RestoreSort()
    {
        var saved = (UiState.Get(UiState.IndustryOppsSort) ?? "").Split(':');
        var (path, direction) = saved.Length == 2 && SortablePaths.Contains(saved[0])
            ? (saved[0], saved[1] == "asc" ? ListSortDirection.Ascending : ListSortDirection.Descending)
            : (nameof(IndustryRow.Profit30d), ListSortDirection.Descending);

        ResultsView.SortDescriptions.Add(DataGridSortDescription.FromPath(path, direction));
    }

    private void SaveSort()
    {
        // A header click clears the sort and adds the new one; only the second is worth keeping.
        if (ResultsView.SortDescriptions.FirstOrDefault() is { } sort)
            UiState.Set(UiState.IndustryOppsSort,
                $"{sort.PropertyPath}:{(sort.Direction == ListSortDirection.Ascending ? "asc" : "desc")}");
    }

    private string _statusText = IndustryText.OppsStatusStart;
    public string StatusText
    {
        get => _statusText;
        private set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    private bool _isCalculating;
    public bool IsCalculating { get => _isCalculating; private set => this.RaiseAndSetIfChanged(ref _isCalculating, value); }

    // ── Command ───────────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> CalculateCommand { get; }

    // ── Construction ──────────────────────────────────────────────────────────

    public IndustryOpportunitiesViewModel(string connString, MarketHistoryService historyService, BatchAddService batchSvc)
    {
        _connString     = connString;
        _batchSvc       = batchSvc;
        _service        = new IndustryOpportunitiesService(historyService, batchSvc);
        _selectedMode   = ModeOptions.FirstOrDefault(m => m.Kind.ToString() == UiState.Get(UiState.IndustryOppsMode))
                       ?? ModeOptions[0];

        ResultsView = new DataGridCollectionView(Results);
        NameFilter  = new ItemNameFilter<IndustryRow>(ResultsView, Results, r => r.TypeName);
        Results.CollectionChanged += (_, _) => NameFilter.Update();
        RestoreSort();
        ResultsView.SortDescriptions.CollectionChanged += (_, _) => SaveSort();
        LoadMetaGroups();
        CalculateCommand           = ReactiveCommand.CreateFromTask(CalculateAsync);
        AddExcludedGroupCommand    = ReactiveCommand.CreateFromTask(AddExcludedGroupAsync);
        RemoveExcludedGroupCommand = ReactiveCommand.CreateFromTask<ExcludedMarketGroupVm>(RemoveExcludedGroupAsync);
        AddIncludedGroupCommand    = ReactiveCommand.CreateFromTask(AddIncludedGroupAsync);
        RemoveIncludedGroupCommand = ReactiveCommand.CreateFromTask<ExcludedMarketGroupVm>(RemoveIncludedGroupAsync);
    }

    public async Task InitializeAsync()
    {
        await LoadMarketConfigsAsync();
        await LoadExcludedGroupsAsync();
    }

    // ── Market config loading ─────────────────────────────────────────────────

    private async Task LoadMarketConfigsAsync()
    {
        var fresh = await IndustryOpportunitiesService.LoadMarketConfigsAsync();

        // ⚠️ The choice is carried across by id. The list is rebuilt from new objects every time
        // the tab is shown, and the old choice is none of them — keeping the object, as this did,
        // left the dropdown showing nothing.
        var keepId = _selectedConfig?.ConfigId ?? UiState.GetLong(UiState.IndustryOppsPriceAt, 0);

        _reloadingConfigs = true;
        try
        {
            MarketConfigs.Clear();
            foreach (var c in fresh) MarketConfigs.Add(c);
        }
        finally { _reloadingConfigs = false; }

        _selectedConfig = MarketConfigs.FirstOrDefault(c => c.ConfigId == keepId) ?? MarketConfigs.FirstOrDefault();
        this.RaisePropertyChanged(nameof(SelectedConfig));
    }

    // ── Calculate ─────────────────────────────────────────────────────────────

    private async Task CalculateAsync()
    {
        if (SelectedConfig is null)
        {
            StatusText = IndustryText.OppsErrNoConfig;
            return;
        }

        double? minIskVol = null;
        if (!string.IsNullOrWhiteSpace(MinIskVolume))
        {
            if (!double.TryParse(MinIskVolume, out var mv) || mv < 0)
            {
                StatusText = IndustryText.OppsErrMinIskVolume;
                return;
            }
            minIskVol = mv;
        }

        double? minUnitVol = null;
        if (!string.IsNullOrWhiteSpace(MinUnitVolume))
        {
            if (!double.TryParse(MinUnitVolume, out var uv) || uv < 0)
            {
                StatusText = IndustryText.OppsErrMinUnitVolume;
                return;
            }
            minUnitVol = uv;
        }

        double? minMargin = null;
        if (!string.IsNullOrWhiteSpace(MinMargin))
        {
            // "15" and "15%" both mean fifteen percent.
            if (!double.TryParse(MinMargin.Trim().TrimEnd('%'), out var mm) || mm < 0)
            {
                StatusText = IndustryText.OppsErrMinMargin;
                return;
            }
            minMargin = mm / 100;
        }

        double? minMargin30d = null;
        if (!string.IsNullOrWhiteSpace(MinMargin30d))
        {
            if (!double.TryParse(MinMargin30d.Trim().TrimEnd('%'), out var mm30) || mm30 < 0)
            {
                StatusText = IndustryText.OppsErrMinMargin30d;
                return;
            }
            minMargin30d = mm30 / 100;
        }

        if (MetaGroups.AllSelected == false)
        {
            StatusText = IndustryText.OppsErrNoMetaGroup;
            return;
        }

        // Remembered once they have been used; see MinIskVolume.
        UiState.Set(UiState.IndustryOppsMinIskVol,  (MinIskVolume  ?? "").Trim());
        UiState.Set(UiState.IndustryOppsMinUnitVol, (MinUnitVolume ?? "").Trim());
        UiState.Set(UiState.IndustryOppsMinMargin,  (MinMargin     ?? "").Trim());
        UiState.Set(UiState.IndustryOppsMinMargin30d, (MinMargin30d ?? "").Trim());

        Results.Clear();
        StatusText = IndustryText.OppsStatusCalculating;
        IsCalculating = true;

        try
        {
            var query = new IndustryOppsQuery
            {
                Mode             = SelectedMode.Kind,
                ConfigId         = SelectedConfig.ConfigId,
                MinIskVol30d     = minIskVol,
                MinUnitVol30d    = minUnitVol,
                MinMargin        = minMargin,
                MinMargin30d     = minMargin30d,
                MetaGroups       = [.. MetaGroups.SelectedKeys.Select(int.Parse)],
                BpoOnly          = BpoOnly,
                OwnedOnly        = OwnedOnly,
                ExcludedGroupIds = [.. ExcludedMarketGroups.Select(g => g.MarketGroupId)],
                IncludedGroupIds = [.. IncludedMarketGroups.Select(g => g.MarketGroupId)],
            };
            var needsVolume = query.NeedsVolume;

            var result = await _service.CalculateAsync(query);
            if (result is null)
            {
                StatusText = IndustryText.OppsErrNoConfig;
                return;
            }
            if (result.NoRegion)
            {
                StatusText = IndustryText.OppsErrNoRegion;
                return;
            }
            var rows       = result.Rows;
            var regionName = result.RegionName;

            // ResultsView sorts them — by the sort the user left, Profit 30d if none. They come in
            // that default order anyway, so the list reads the same with the sort cleared.
            foreach (var r in rows)
                Results.Add(r);

            int noSell = rows.Count(r => !r.HasSellOrders);
            var note   = noSell > 0 ? "  ·  " + string.Format(IndustryText.OppsNoteHistoryPriced, noSell) : "";
            // Show the volume region so it's clear the 30-day filters use the Price At region.
            var volNote = needsVolume ? " · " + string.Format(IndustryText.OppsNoteVolumeRegion, regionName) : "";
            StatusText = rows.Count > 0
                ? Plurals.Format(IndustryText.ResourceManager, nameof(IndustryText.OppsStatusProfitableOther),
                                 rows.Count, SelectedConfig.Name) + volNote + note
                : IndustryText.OppsStatusNone;
        }
        catch (Exception ex)
        {
            StatusText = string.Format(CommonText.ErrorWithMessage, ex.Message);
        }
        finally
        {
            IsCalculating = false;
        }
    }
}
