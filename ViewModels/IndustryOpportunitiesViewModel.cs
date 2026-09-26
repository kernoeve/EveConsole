using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reactive;
using Avalonia.Collections;
using EveConsole.Models;
using EveConsole.Services;
using Microsoft.Data.Sqlite;
using ReactiveUI;
using EveConsole.Data;

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
    private readonly string               _connString;
    private readonly MarketHistoryService _historyService;
    private readonly BatchAddService      _batchSvc;

    // Per-unit build time is computed and cached in BuildCosts.BuildSeconds by
    // BuildCostService (using the default park's blueprint TE, skills, and structure
    // role/rig time bonuses) — we just read it here and convert to slot-days.
    private const double SecondsPerDay = 86_400.0;

    // ── Mode ──────────────────────────────────────────────────────────────────

    public List<IndustryModeOption> ModeOptions { get; } =
    [
        new("Build & Sell Order",         IndustryMode.BuildAndSellOrder),
        new("Build & Sell to Buy Order",  IndustryMode.BuildAndSellToBuyOrder),
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

    // Faction items (MetaGroupId = 4) are ME0 BPCs that are often not worth building.
    private bool _skipFactionItems = UiState.GetBool(UiState.IndustryOppsSkipFaction, true);
    public bool SkipFactionItems
    {
        get => _skipFactionItems;
        set
        {
            this.RaiseAndSetIfChanged(ref _skipFactionItems, value);
            UiState.SetBool(UiState.IndustryOppsSkipFaction, value);
        }
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

    // ── Excluded market groups (and everything nested under them) ────────────

    public ObservableCollection<ExcludedMarketGroupVm> ExcludedMarketGroups { get; } = [];

    public Func<Task<MarketGroupPickerResult?>>? ShowMarketGroupPickerDialog { get; set; }

    public BatchAddService GetBatchAddService() => _batchSvc;

    public ReactiveCommand<Unit, Unit>                  AddExcludedGroupCommand    { get; }
    public ReactiveCommand<ExcludedMarketGroupVm, Unit> RemoveExcludedGroupCommand { get; }

    private async Task AddExcludedGroupAsync()
    {
        if (ShowMarketGroupPickerDialog is null) return;
        var pick = await ShowMarketGroupPickerDialog();
        if (pick is null) return;
        if (ExcludedMarketGroups.Any(g => g.MarketGroupId == pick.MarketGroupId)) return;

        ExcludedMarketGroups.Add(new ExcludedMarketGroupVm(pick.MarketGroupId, pick.GroupName));
        await SaveExcludedGroupsAsync();
    }

    private async Task RemoveExcludedGroupAsync(ExcludedMarketGroupVm group)
    {
        ExcludedMarketGroups.Remove(group);
        await SaveExcludedGroupsAsync();
    }

    private async Task LoadExcludedGroupsAsync()
    {
        using var conn = AppDb.Connect();
        await conn.OpenAsync();
        using var cmd = conn.Command("""SELECT "ExcludedMarketGroupIds" FROM "IndustryOpportunitiesSettings" WHERE "Id" = 1""");
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

        ExcludedMarketGroups.Clear();
        foreach (var id in ids)
            if (names.TryGetValue(id, out var name))
                ExcludedMarketGroups.Add(new ExcludedMarketGroupVm(id, name));
    }

    private async Task SaveExcludedGroupsAsync()
    {
        var csv = string.Join(",", ExcludedMarketGroups.Select(g => g.MarketGroupId));
        using var conn = AppDb.Connect();
        await conn.OpenAsync();
        using var cmd = conn.Command("""UPDATE "IndustryOpportunitiesSettings" SET "ExcludedMarketGroupIds" = @ids WHERE "Id" = 1""");
        cmd.AddWithValue("@ids", csv);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<HashSet<int>> GetExcludedGroupIdsRecursiveAsync()
    {
        var result = new HashSet<int>();
        foreach (var g in ExcludedMarketGroups)
            result.UnionWith(await _batchSvc.GetDescendantGroupIdsAsync(g.MarketGroupId));
        return result;
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
    /// is also remembered between sessions, and starts as ISK Sold 30d, highest first.</para>
    /// </summary>
    public DataGridCollectionView ResultsView { get; }

    private static readonly HashSet<string> SortablePaths =
        typeof(IndustryRow).GetProperties().Select(p => p.Name).ToHashSet();

    private void RestoreSort()
    {
        var saved = (UiState.Get(UiState.IndustryOppsSort) ?? "").Split(':');
        var (path, direction) = saved.Length == 2 && SortablePaths.Contains(saved[0])
            ? (saved[0], saved[1] == "asc" ? ListSortDirection.Ascending : ListSortDirection.Descending)
            : (nameof(IndustryRow.IskVol30d), ListSortDirection.Descending);

        ResultsView.SortDescriptions.Add(DataGridSortDescription.FromPath(path, direction));
    }

    private void SaveSort()
    {
        // A header click clears the sort and adds the new one; only the second is worth keeping.
        if (ResultsView.SortDescriptions.FirstOrDefault() is { } sort)
            UiState.Set(UiState.IndustryOppsSort,
                $"{sort.PropertyPath}:{(sort.Direction == ListSortDirection.Ascending ? "asc" : "desc")}");
    }

    private string _statusText = "Select a market config, then click Calculate.";
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
        _historyService = historyService;
        _batchSvc       = batchSvc;
        _selectedMode   = ModeOptions.FirstOrDefault(m => m.Kind.ToString() == UiState.Get(UiState.IndustryOppsMode))
                       ?? ModeOptions[0];

        ResultsView = new DataGridCollectionView(Results);
        RestoreSort();
        ResultsView.SortDescriptions.CollectionChanged += (_, _) => SaveSort();
        CalculateCommand           = ReactiveCommand.CreateFromTask(CalculateAsync);
        AddExcludedGroupCommand    = ReactiveCommand.CreateFromTask(AddExcludedGroupAsync);
        RemoveExcludedGroupCommand = ReactiveCommand.CreateFromTask<ExcludedMarketGroupVm>(RemoveExcludedGroupAsync);
    }

    public async Task InitializeAsync()
    {
        await LoadMarketConfigsAsync();
        await LoadExcludedGroupsAsync();
    }

    // ── Market config loading ─────────────────────────────────────────────────

    private async Task LoadMarketConfigsAsync()
    {
        using var conn = AppDb.Connect();
        await conn.OpenAsync();

        using var cmd = conn.Command("""
            SELECT "Id", "LocationName", "Method", "LocationId"
            FROM "MarketPricingConfigs"
            WHERE "IsEnabled" = TRUE
            ORDER BY "SortOrder"
            """);

        var fresh = new List<IndustryMarketConfig>();
        using (var reader = await cmd.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                fresh.Add(new IndustryMarketConfig(
                    reader.GetInt32(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt64(3)));

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
            StatusText = "Please select a market config for pricing.";
            return;
        }

        double? minIskVol = null;
        if (!string.IsNullOrWhiteSpace(MinIskVolume))
        {
            if (!double.TryParse(MinIskVolume, out var mv) || mv < 0)
            {
                StatusText = "Please enter a valid minimum ISK volume (or leave blank for no filter).";
                return;
            }
            minIskVol = mv;
        }

        double? minUnitVol = null;
        if (!string.IsNullOrWhiteSpace(MinUnitVolume))
        {
            if (!double.TryParse(MinUnitVolume, out var uv) || uv < 0)
            {
                StatusText = "Please enter a valid minimum unit volume (or leave blank for no filter).";
                return;
            }
            minUnitVol = uv;
        }

        // Remembered once they have been used; see MinIskVolume.
        UiState.Set(UiState.IndustryOppsMinIskVol,  (MinIskVolume  ?? "").Trim());
        UiState.Set(UiState.IndustryOppsMinUnitVol, (MinUnitVolume ?? "").Trim());

        Results.Clear();
        StatusText = "Calculating…";
        IsCalculating = true;

        try
        {
            var candidates = await FetchCandidatesAsync(SelectedConfig.ConfigId);

            // Region is needed for the volume filters AND to price items that have no
            // sell orders off their 30-day history average. Resolve it best-effort.
            int?   regionId   = await ResolveRegionAsync(SelectedConfig);
            string regionName = regionId.HasValue ? await GetRegionNameAsync(regionId.Value) : "unresolved";
            bool needsVolume = minIskVol.HasValue || minUnitVol.HasValue;
            if (needsVolume && !regionId.HasValue)
            {
                StatusText = "Could not resolve this market config's region — " +
                             "the 30-day volume filters need a region to look up market history.";
                return;
            }

            var (typesWithSell, typesWithBuy, configHasRawOrders) =
                await LoadOrderPresenceAsync(SelectedConfig.ConfigId);

            var rows = await BuildRowsAsync(candidates, regionId, minIskVol, minUnitVol,
                                            typesWithSell, typesWithBuy, configHasRawOrders);
            // ResultsView sorts them — by the sort the user left, ISK Sold 30d if none. Added in
            // that default order anyway, so the list reads the same with the sort cleared.
            foreach (var r in rows.OrderByDescending(r => r.IskVol30d).ThenByDescending(r => r.ProfitPerSlotDay))
                Results.Add(r);

            int noSell = rows.Count(r => !r.HasSellOrders);
            var note   = noSell > 0 ? $"  ·  * {noSell} priced from 30-day avg (no sell orders)" : "";
            // Show the volume region so it's clear the 30-day filters use the Price At region.
            var volNote = needsVolume ? $" · 30d volume region: {regionName}" : "";
            StatusText = rows.Count > 0
                ? $"{rows.Count} profitable item{(rows.Count == 1 ? "" : "s")} · priced at {SelectedConfig.Name}{volNote}{note}"
                : "No profitable build opportunities found for this market config.";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            IsCalculating = false;
        }
    }

    // ── Core algorithm ────────────────────────────────────────────────────────

    private record Candidate(
        int TypeId, string TypeName,
        double BuildCost, double SellOrderPrice, double BuyOrderPrice,
        double BuildSeconds);

    private async Task<List<Candidate>> FetchCandidatesAsync(int configId)
    {
        var excludedGroupIds = await GetExcludedGroupIdsRecursiveAsync();
        var exclusionClause  = excludedGroupIds.Count > 0
            ? $"""AND (t."MarketGroupId" IS NULL OR t."MarketGroupId" NOT IN ({string.Join(",", excludedGroupIds)})) """
            : "";

        // Faction items are MetaGroupId 4.
        var factionClause = SkipFactionItems
            ? """AND (t."MetaGroupId" IS NULL OR t."MetaGroupId" != 4) """
            : "";

        // BPO-only: keep items whose (published) manufacturing/reaction blueprint either is
        // sold on the market (a buyable BPO) OR is invented from a source blueprint that is
        // sold on the market (e.g. a T2 BPC invented from a T1 BPO). Excludes items whose BPC
        // has no BPO and cannot be derived from one (faction/limited-run BPCs), plus
        // "Limited Time" items (MetaGroupId 19) — event blueprints that have a market group
        // in the SDE but can no longer be bought in game.
        //
        // ⚠️ A market group is not proof of a BPO. Storyline (3), Officer (5) and Deadspace (6)
        // products are skipped outright, the same loot tiers BuildCostService refuses to treat
        // as BPO-sourced: the one officer module with a blueprint — an event reward, never sold —
        // files it beside the ordinary T1 weapon-upgrade BPOs, and topped the list priced at
        // officer-module prices. Faction (4) keeps its own checkbox.
        var bpoClause = BpoOnly
            ? """
              AND (t."MetaGroupId" IS NULL OR t."MetaGroupId" NOT IN (3, 5, 6, 19))
              AND EXISTS (
                SELECT 1 FROM "SdeBlueprintProducts" bp
                JOIN "SdeTypes" bpt ON bpt."TypeId" = bp."TypeId"
                WHERE bp."ProductTypeId" = bc."TypeId"
                  AND bp."Activity" IN ('manufacturing','reaction')
                  AND bpt."Published" = TRUE
                  AND (
                    bpt."MarketGroupId" IS NOT NULL
                    OR EXISTS (
                      SELECT 1 FROM "SdeBlueprintProducts" inv
                      JOIN "SdeTypes" src ON src."TypeId" = inv."TypeId"
                      WHERE inv."Activity" = 'invention'
                        AND inv."ProductTypeId" = bp."TypeId"
                        AND src."MarketGroupId" IS NOT NULL)
                  ))
              """
            : "";

        using var conn = AppDb.Connect();
        await conn.OpenAsync();

        using var cmd = conn.Command(CandidateSql
            .Replace("/*EXCLUSION*/", exclusionClause)
            .Replace("/*FACTION*/", factionClause)
            .Replace("/*BPO*/", bpoClause));
        cmd.AddWithValue("@configId", configId);

        var list = new List<Candidate>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new Candidate(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetDouble(2),
                reader.GetDouble(3),
                reader.GetDouble(4),
                reader.GetDouble(5)));
        }
        return list;
    }

    // Which types have live buy/sell orders for this config (from the raw order snapshot),
    // and whether the config has any raw orders at all (Fuzzwork configs have none — for
    // those we trust the stored prices and treat every item as having sell orders).
    private async Task<(HashSet<int> WithSell, HashSet<int> WithBuy, bool HasRawOrders)>
        LoadOrderPresenceAsync(int configId)
    {
        var withSell = new HashSet<int>();
        var withBuy  = new HashSet<int>();

        using var conn = AppDb.Connect();
        await conn.OpenAsync();
        using var cmd = conn.Command("""
            SELECT DISTINCT "TypeId", "IsBuyOrder"
            FROM "MarketRawOrders"
            WHERE "ConfigId" = @configId
            """);
        cmd.AddWithValue("@configId", configId);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            int typeId = reader.GetInt32(0);
            if (reader.GetBoolean(1)) withBuy.Add(typeId);
            else                      withSell.Add(typeId);
        }
        return (withSell, withBuy, withSell.Count > 0 || withBuy.Count > 0);
    }

    private async Task<List<IndustryRow>> BuildRowsAsync(
        List<Candidate> candidates, int? regionId, double? minIskVol30d, double? minUnitVol30d,
        HashSet<int> typesWithSell, HashSet<int> typesWithBuy, bool configHasRawOrders)
    {
        var result = new List<IndustryRow>();

        foreach (var c in candidates)
        {
            if (c.BuildCost <= 0) continue;

            double sellInto;
            bool   hasSellOrders = true;

            if (SelectedMode.Kind == IndustryMode.BuildAndSellToBuyOrder)
            {
                // No buy orders for this item → nobody to sell to → skip.
                if (configHasRawOrders && !typesWithBuy.Contains(c.TypeId)) continue;
                sellInto = c.BuyOrderPrice;
            }
            else
            {
                bool sellOrders = !configHasRawOrders || typesWithSell.Contains(c.TypeId);
                if (sellOrders)
                {
                    sellInto = c.SellOrderPrice; // real lowest sell
                }
                else
                {
                    // No sell orders — these are often the most lucrative if in demand.
                    // Price them off the 30-day history average (what they actually trade
                    // at) rather than the build-cost gap-fill, and flag them with a "*".
                    sellInto = regionId.HasValue
                        ? await _historyService.Get30DayAveragePriceAsync(regionId.Value, c.TypeId)
                        : 0;
                    hasSellOrders = false;
                }
            }

            if (sellInto <= 0) continue;

            double profit = sellInto - c.BuildCost;
            if (profit <= 0) continue; // only surface opportunities

            // 30-day volumes (config region), from history cached by the background sweep —
            // no ESI here. Always read them for the columns; also apply them as filters.
            double unitVol = 0, iskVol = 0;
            if (regionId.HasValue)
            {
                unitVol = await _historyService.Get30DayUnitVolumeAsync(regionId.Value, c.TypeId);
                iskVol  = await _historyService.Get30DayIskVolumeAsync(regionId.Value, c.TypeId);
                if (minUnitVol30d.HasValue && unitVol < minUnitVol30d.Value) continue;
                if (minIskVol30d.HasValue  && iskVol  < minIskVol30d.Value)  continue;
            }

            double slotDays = c.BuildSeconds / SecondsPerDay;

            result.Add(new IndustryRow
            {
                TypeId           = c.TypeId,
                TypeName         = c.TypeName,
                BuildCost        = c.BuildCost,
                SellPrice        = sellInto,
                HasSellOrders    = hasSellOrders,
                ProfitPerUnit    = profit,
                Margin           = profit / c.BuildCost,
                BuildSeconds     = c.BuildSeconds,
                SlotDays         = slotDays,
                ProfitPerSlotDay = slotDays > 0 ? profit / slotDays : 0,
                UnitVol30d       = unitVol,
                IskVol30d        = iskVol,
            });
        }

        return result;
    }

    private async Task<string> GetRegionNameAsync(int regionId)
    {
        using var conn = AppDb.Connect();
        await conn.OpenAsync();
        using var cmd = conn.Command("""SELECT "Name" FROM "SdeRegions" WHERE "RegionId" = @id""");
        cmd.AddWithValue("@id", regionId);
        return (await cmd.ExecuteScalarAsync()) as string ?? $"Region {regionId}";
    }

    // Resolves the region id used for the 30-day volume lookups from a market config.
    private async Task<int?> ResolveRegionAsync(IndustryMarketConfig cfg)
    {
        // ESI Region configs store the region id directly in LocationId.
        if (cfg.Method == MarketMethod.EsiRegion) return (int)cfg.LocationId;

        using var conn = AppDb.Connect();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();

        // Maybe LocationId is already a region id (Fuzzwork region configs).
        cmd.CommandText = AppDb.CaseInsensitiveLike("""SELECT "RegionId" FROM "SdeRegions" WHERE "RegionId" = @loc""");
        cmd.AddWithValue("@loc", cfg.LocationId);
        var region = ToRegionId(await cmd.ExecuteScalarAsync());
        if (region.HasValue) return region;

        // NPC station: resolve via its solar system. SdeStations.RegionId is populated now
        // (by the importer, and by a startup repair for databases imported before that fix),
        // but the join is kept deliberately — it is correct whatever state the column is in.
        cmd.CommandText = AppDb.CaseInsensitiveLike("""
            SELECT ss."RegionId"
            FROM "SdeStations"     s
            JOIN "SdeSolarSystems" ss ON ss."SolarSystemId" = s."SolarSystemId"
            WHERE s."StationId" = @sid AND s."SolarSystemId" != 0
            """);
        cmd.Parameters.Clear();
        cmd.AddWithValue("@sid", (int)Math.Min(cfg.LocationId, int.MaxValue));
        region = ToRegionId(await cmd.ExecuteScalarAsync());
        if (region.HasValue) return region;

        // Player structure: resolved name record already has SolarSystemId.
        cmd.CommandText = AppDb.CaseInsensitiveLike("""
            SELECT ss."RegionId"
            FROM "EsiStructureNames" sn
            JOIN "SdeSolarSystems"   ss ON ss."SolarSystemId" = sn."SolarSystemId"
            WHERE sn."StructureId" = @lid AND sn."SolarSystemId" != 0
            """);
        cmd.Parameters.Clear();
        cmd.AddWithValue("@lid", cfg.LocationId);
        region = ToRegionId(await cmd.ExecuteScalarAsync());
        if (region.HasValue) return region;

        // Fallback: derive from any cached order at that location.
        cmd.CommandText = AppDb.CaseInsensitiveLike("""
            SELECT ss."RegionId"
            FROM "MarketRawOrders" o
            JOIN "SdeSolarSystems" ss ON ss."SolarSystemId" = o."SystemId"
            WHERE o."LocationId" = @lid AND o."SystemId" != 0
            LIMIT 1
            """);
        cmd.Parameters.Clear();
        cmd.AddWithValue("@lid", cfg.LocationId);
        region = ToRegionId(await cmd.ExecuteScalarAsync());
        return region;
    }

    // Treats NULL/DBNull and a 0 region id as "unresolved" so callers fall through.
    private static int? ToRegionId(object? scalar)
    {
        if (scalar is null or DBNull) return null;
        var id = Convert.ToInt32(scalar);
        return id != 0 ? id : null;
    }

    // ── SQL ───────────────────────────────────────────────────────────────────

    // Joins cached build cost + build time (from BuildCostService) with market prices
    // for the chosen config. One BuildCosts row per item (TypeId is PK), so no dedupe.
    private const string CandidateSql = """
        SELECT
            bc."TypeId",
            bc."TypeName",
            CAST(bc."TotalCost"    AS DOUBLE PRECISION)   AS "BuildCost",
            CAST(mip."SellPrice"   AS DOUBLE PRECISION)   AS "SellPrice",
            CAST(mip."BuyPrice"    AS DOUBLE PRECISION)   AS "BuyPrice",
            CAST(bc."BuildSeconds" AS DOUBLE PRECISION)   AS "BuildSeconds"
        FROM "BuildCosts" bc
        JOIN "MarketItemPrices" mip
              ON mip."TypeId" = bc."TypeId" AND mip."ConfigId" = @configId
        JOIN "SdeTypes" t ON t."TypeId" = bc."TypeId"
        WHERE bc."TotalCost" > 0 AND bc."BuildSeconds" > 0
        /*EXCLUSION*/
        /*FACTION*/
        /*BPO*/
        """;
}
