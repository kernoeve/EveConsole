using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Services;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;
using EveConsole.Localization;

namespace EveConsole.ViewModels;

// ── Supporting types ──────────────────────────────────────────────────────────

public enum FitNodeKind { MarketGroup, Ship, Fit }

public record FitGroupOption(int GroupId, string GroupName)
{
    public override string ToString() => GroupName;
}

public record FitSelectorResult(EsiFittingData Fitting, int TargetGroupId);

// ── Tree node ─────────────────────────────────────────────────────────────────

public class FitTreeNode : ReactiveObject
{
    private static readonly IBrush PersonalBrush = new SolidColorBrush(Color.Parse("#c8a84b"));
    private static readonly IBrush CorpBrush     = new SolidColorBrush(Color.Parse("#5599cc"));

    public FitNodeKind    Kind    { get; init; }
    public string         Name    { get; init; } = "";
    public FitSource?     Source  { get; init; }
    public FitEntry?      Entry   { get; init; }
    public int?           TypeId  { get; init; }
    public List<FitTreeNode> Children { get; } = [];

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set => this.RaiseAndSetIfChanged(ref _isExpanded, value);
    }

    public FitTreeNode(bool startExpanded = false) => _isExpanded = startExpanded;

    public bool   IsFit  => Kind == FitNodeKind.Fit;
    public string SourceBadge => Source switch
    {
        FitSource.Personal => CommonText.FitBadgePersonal,
        FitSource.Corp     => CommonText.FitBadgeCorp,
        _                  => ""
    };
    public IBrush SourceBrush => Source switch
    {
        FitSource.Personal => PersonalBrush,
        FitSource.Corp     => CorpBrush,
        _                  => Brushes.Transparent
    };
}

// ── Detail panel line ─────────────────────────────────────────────────────────

public class FitDetailLine
{
    private static readonly IBrush HeaderBrush = new SolidColorBrush(Color.Parse("#7a9aaa"));
    private static readonly IBrush ItemBrush   = new SolidColorBrush(Color.Parse("#c0c0cc"));

    public bool   IsHeader   { get; init; }
    public string Text       { get; init; } = "";
    public IBrush Foreground => IsHeader ? HeaderBrush : ItemBrush;
    public Thickness Margin  => IsHeader ? new Thickness(0, 8, 0, 2) : new Thickness(8, 1, 0, 1);
    public string FontWeight => IsHeader ? "SemiBold" : "Normal";
}

// ── View-model ────────────────────────────────────────────────────────────────

public class FitSelectorViewModel : ReactiveObject
{
    private readonly FittingsService                 _svc;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ObservableCollection<Character>   _characters;
    private readonly ObservableCollection<Corporation> _corporations;

    public ObservableCollection<FitTreeNode>   RootNodes   { get; } = [];
    public ObservableCollection<FitGroupOption> Groups      { get; } = [];
    public ObservableCollection<FitDetailLine>  DetailLines { get; } = [];

    private FitTreeNode? _selectedNode;
    public FitTreeNode? SelectedNode
    {
        get => _selectedNode;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedNode, value);
            this.RaisePropertyChanged(nameof(CanConfirm));
            this.RaisePropertyChanged(nameof(HasSelectedFit));
            _ = LoadDetailLinesAsync(value?.Entry, CancellationToken.None);
        }
    }

    private FitGroupOption? _selectedGroup;
    public FitGroupOption? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedGroup, value);
            this.RaisePropertyChanged(nameof(CanConfirm));
        }
    }

    private string _statusText = CommonText.FetchingFits;
    public string StatusText
    {
        get => _statusText;
        private set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    private bool _isLoading = true;
    public bool IsLoading
    {
        get => _isLoading;
        private set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }

    public bool HasSelectedFit => _selectedNode?.IsFit == true;
    public bool CanConfirm     => _selectedNode?.IsFit == true && _selectedGroup != null;

    public FitSelectorViewModel(
        FittingsService                     svc,
        IDbContextFactory<AppDbContext>     dbFactory,
        ObservableCollection<Character>     characters,
        ObservableCollection<Corporation>   corporations,
        IReadOnlyList<FitGroupOption>       groupOptions,
        int                                 preselectedGroupId)
    {
        _svc          = svc;
        _dbFactory    = dbFactory;
        _characters   = characters;
        _corporations = corporations;

        foreach (var g in groupOptions) Groups.Add(g);
        _selectedGroup = Groups.FirstOrDefault(g => g.GroupId == preselectedGroupId)
                         ?? Groups.FirstOrDefault();

        _ = LoadAsync(CancellationToken.None);
    }

    // ── Load ──────────────────────────────────────────────────────────────────

    private async Task LoadAsync(CancellationToken ct)
    {
        try
        {
            var fits = await _svc.FetchAllFitsAsync(_characters, _corporations, ct);
            StatusText = CommonText.BuildingFitTree;
            await BuildTreeAsync(fits, ct);
        }
        catch (Exception ex)
        {
            StatusText = string.Format(CommonText.ErrorWithMessage, ex.Message);
            IsLoading  = false;
        }
    }

    private async Task BuildTreeAsync(List<FitEntry> fits, CancellationToken ct)
    {
        if (fits.Count == 0)
        {
            StatusText = CommonText.NoFitsFound;
            IsLoading  = false;
            return;
        }

        await using var db = _dbFactory.CreateDbContext();

        // Group fits by ship TypeId
        var fitsByShip = fits
            .GroupBy(f => f.Data.ShipTypeId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var shipTypeIds = fitsByShip.Keys.ToList();

        // Load ship type info and direct market group
        var shipInfo = await db.SdeTypes
            .Where(t => shipTypeIds.Contains(t.TypeId))
            .Select(t => new { t.TypeId, t.Name, t.MarketGroupId })
            .ToDictionaryAsync(t => t.TypeId, ct);

        // Load all market groups into memory (small table)
        var allGroups = await db.SdeMarketGroups
            .ToDictionaryAsync(g => g.MarketGroupId, ct);

        // Walk up the parent chain for each ship and collect relevant group IDs
        var relevantGroupIds = new HashSet<int>();
        var shipGroupIdMap   = new Dictionary<int, int>(); // TypeId → direct MarketGroupId

        foreach (var typeId in shipTypeIds)
        {
            if (!shipInfo.TryGetValue(typeId, out var info) || info.MarketGroupId == null)
                continue;

            shipGroupIdMap[typeId] = info.MarketGroupId.Value;

            int? cur = info.MarketGroupId.Value;
            while (cur.HasValue && allGroups.ContainsKey(cur.Value))
            {
                relevantGroupIds.Add(cur.Value);
                cur = allGroups[cur.Value].ParentGroupId;
            }
        }

        // Build parent → children map (only relevant groups)
        var childrenMap = new Dictionary<int, List<int>>();
        foreach (var gid in relevantGroupIds)
        {
            var parentId = allGroups[gid].ParentGroupId;
            if (!parentId.HasValue || !relevantGroupIds.Contains(parentId.Value)) continue;
            if (!childrenMap.TryGetValue(parentId.Value, out var list))
                childrenMap[parentId.Value] = list = [];
            list.Add(gid);
        }

        // Build ship-by-direct-group map
        var shipsByGroup = new Dictionary<int, List<(int TypeId, string Name, List<FitEntry> Fits)>>();
        foreach (var (typeId, groupId) in shipGroupIdMap)
        {
            if (!fitsByShip.TryGetValue(typeId, out var typeFits)) continue;
            var name = shipInfo.TryGetValue(typeId, out var si) ? si.Name : string.Format(CommonText.TypeIdWithId, typeId);
            if (!shipsByGroup.TryGetValue(groupId, out var ships))
                shipsByGroup[groupId] = ships = [];
            ships.Add((typeId, name, typeFits));
        }

        // Root groups = relevant groups whose parent is NOT in the relevant set
        var rootIds = relevantGroupIds
            .Where(id => !allGroups[id].ParentGroupId.HasValue
                         || !relevantGroupIds.Contains(allGroups[id].ParentGroupId!.Value))
            .OrderBy(id => allGroups[id].Name)
            .ToList();

        // Recursive tree builder
        FitTreeNode BuildGroupNode(int groupId)
        {
            var node = new FitTreeNode(startExpanded: true)
            {
                Kind = FitNodeKind.MarketGroup,
                Name = allGroups[groupId].Name
            };

            if (childrenMap.TryGetValue(groupId, out var children))
                foreach (var cid in children.OrderBy(id => allGroups[id].Name))
                    node.Children.Add(BuildGroupNode(cid));

            if (shipsByGroup.TryGetValue(groupId, out var ships))
            {
                foreach (var (typeId, shipName, shipFits) in ships.OrderBy(s => s.Name))
                {
                    var shipNode = new FitTreeNode { Kind = FitNodeKind.Ship, Name = shipName, TypeId = typeId };
                    foreach (var entry in shipFits.OrderBy(f => f.Data.Name))
                    {
                        shipNode.Children.Add(new FitTreeNode
                        {
                            Kind   = FitNodeKind.Fit,
                            Name   = entry.Data.Name,
                            Source = entry.Source,
                            Entry  = entry
                        });
                    }
                    node.Children.Add(shipNode);
                }
            }

            return node;
        }

        foreach (var rootId in rootIds)
            RootNodes.Add(BuildGroupNode(rootId));

        StatusText = string.Format(CommonText.FitsSummary, fits.Count, fitsByShip.Count);
        IsLoading  = false;
    }

    // ── Fit detail panel ──────────────────────────────────────────────────────

    private static readonly HashSet<string> SkipFlags = new(StringComparer.OrdinalIgnoreCase)
        { "Invalid", "Implant", "BoosterBay" };

    /// <summary>
    /// Where a fitted item sits, declared in the order the detail panel lists them.
    ///
    /// <para>⚠️ Grouped and ordered by this, never by the heading: the heading is looked up only
    /// when it is shown, so a translated one cannot split or reorder the sections.</para>
    /// </summary>
    private enum FitSection { High, Med, Low, Rigs, Subsystems, Drones, Fighters, Cargo, FleetHangar, Other }

    private static FitSection GetCategory(string flag)
    {
        if (flag.StartsWith("HiSlot",    StringComparison.OrdinalIgnoreCase)) return FitSection.High;
        if (flag.StartsWith("MedSlot",   StringComparison.OrdinalIgnoreCase)) return FitSection.Med;
        if (flag.StartsWith("LoSlot",    StringComparison.OrdinalIgnoreCase)) return FitSection.Low;
        if (flag.StartsWith("RigSlot",   StringComparison.OrdinalIgnoreCase)) return FitSection.Rigs;
        if (flag.StartsWith("SubSystem", StringComparison.OrdinalIgnoreCase)) return FitSection.Subsystems;
        if (flag.Equals("DroneBay",      StringComparison.OrdinalIgnoreCase)) return FitSection.Drones;
        if (flag.Equals("FighterBay",    StringComparison.OrdinalIgnoreCase)) return FitSection.Fighters;
        if (flag.Equals("Cargo",         StringComparison.OrdinalIgnoreCase) ||
            flag.Equals("CargoHold",     StringComparison.OrdinalIgnoreCase)) return FitSection.Cargo;
        if (flag.Equals("FleetHangar",   StringComparison.OrdinalIgnoreCase)) return FitSection.FleetHangar;
        return FitSection.Other;
    }

    /// <summary>A section's heading in the detail panel.</summary>
    private static string Heading(FitSection section) => section switch
    {
        FitSection.High        => CommonText.FitSectionHigh,
        FitSection.Med         => CommonText.FitSectionMed,
        FitSection.Low         => CommonText.FitSectionLow,
        FitSection.Rigs        => CommonText.FitSectionRigs,
        FitSection.Subsystems  => CommonText.FitSectionSubsystems,
        FitSection.Drones      => CommonText.FitSectionDrones,
        FitSection.Fighters    => CommonText.FitSectionFighters,
        FitSection.Cargo       => CommonText.FitSectionCargo,
        FitSection.FleetHangar => CommonText.FitSectionFleetHangar,
        _                      => CommonText.FitSectionOther,
    };

    private async Task LoadDetailLinesAsync(FitEntry? entry, CancellationToken ct)
    {
        DetailLines.Clear();
        if (entry == null) return;

        await using var db = _dbFactory.CreateDbContext();

        // Aggregate items by TypeId per category
        var byCategory = new Dictionary<FitSection, Dictionary<int, int>>();
        foreach (var item in entry.Data.Items)
        {
            if (SkipFlags.Contains(item.Flag)) continue;
            var cat = GetCategory(item.Flag);
            if (!byCategory.TryGetValue(cat, out var map)) byCategory[cat] = map = [];
            map.TryGetValue(item.TypeId, out var q);
            map[item.TypeId] = q + item.Quantity;
        }

        // Fetch type names in one query
        var allTypeIds = byCategory.Values.SelectMany(m => m.Keys)
            .Append(entry.Data.ShipTypeId)
            .Distinct()
            .ToList();

        var typeNames = await db.SdeTypes
            .Where(t => allTypeIds.Contains(t.TypeId))
            .ToDictionaryAsync(t => t.TypeId, t => t.Name, ct);

        string Name(int id) => typeNames.GetValueOrDefault(id, string.Format(CommonText.TypeIdWithId, id));

        DetailLines.Add(new FitDetailLine { IsHeader = true, Text = CommonText.FitSectionHull });
        DetailLines.Add(new FitDetailLine { Text = $"1× {Name(entry.Data.ShipTypeId)}" });

        foreach (var cat in Enum.GetValues<FitSection>())
        {
            if (!byCategory.TryGetValue(cat, out var items)) continue;
            DetailLines.Add(new FitDetailLine { IsHeader = true, Text = Heading(cat) });
            foreach (var (typeId, qty) in items.OrderBy(kv => Name(kv.Key)))
                DetailLines.Add(new FitDetailLine { Text = $"{qty:N0}× {Name(typeId)}" });
        }
    }
}
