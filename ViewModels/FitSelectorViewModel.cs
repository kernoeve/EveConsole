using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Linq;
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

public record FitSelectorResult(EsiFittingData Fitting, int TargetGroupId, FitEntry? Entry = null);

/// <summary>Whose fittings the picker lists: a character's own, or a corporation's.</summary>
public sealed record FitOwner(FitSource Source, string Name);

// ── Tree node ─────────────────────────────────────────────────────────────────

public class FitTreeNode : ReactiveObject
{
    private static readonly IBrush PersonalBrush = new SolidColorBrush(Color.Parse("#c8a84b"));
    private static readonly IBrush CorpBrush     = new SolidColorBrush(Color.Parse("#5599cc"));
    private static readonly IBrush AppBrush      = new SolidColorBrush(Color.Parse("#6fbf73"));

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
        FitSource.App      => "",   // the owner beside it already says EVE Console
        _                  => ""
    };
    public IBrush SourceBrush => Source switch
    {
        FitSource.Personal => PersonalBrush,
        FitSource.Corp     => CorpBrush,
        FitSource.App      => AppBrush,
        _                  => Brushes.Transparent
    };
    /// <summary>Whose fitting it is, beside its name: the character, or the corporation.</summary>
    public string OwnerLabel => Entry is { } e ? FitSelectorViewModel.OwnerLabel(e) : "";
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
    private readonly FittingsService?                _svc;
    private readonly IReadOnlyList<FitEntry>         _localFits;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ObservableCollection<Character>   _characters;
    private readonly ObservableCollection<Corporation> _corporations;

    public ObservableCollection<FitTreeNode>   RootNodes   { get; } = [];

    // ── Owner filter ──────────────────────────────────────────────────────────

    /// <summary>A fitting's owner as the picker names it: a character's name, a corporation's
    /// followed by "(corp)" — a character and a corporation can share a name — or EVE Console.</summary>
    public static string OwnerLabel(FitEntry e) => e.Source == FitSource.Corp ? string.Format(FittingText.OwnerCorp, e.OwnerName) : e.OwnerName;

    /// <summary>Everyone (null), then each character with fittings, then each corporation. Chosen
    /// and matched by the owner itself; the label is only shown.</summary>
    public ObservableCollection<Choice<FitOwner?>> Owners { get; } = [new(null, FittingText.OwnerEveryone)];

    private FitOwner? _selectedOwner;
    public Choice<FitOwner?> SelectedOwner
    {
        get => Owners.FirstOrDefault(o => o.Value == _selectedOwner) ?? Owners[0];
        set
        {
            // A detaching ComboBox sets null; that is not a choice.
            if (value is null) { this.RaisePropertyChanged(); return; }
            if (value.Value == _selectedOwner) return;
            _selectedOwner = value.Value;
            this.RaisePropertyChanged();
            if (_allFits is not null) _ = BuildTreeAsync(Filtered(), CancellationToken.None);
        }
    }

    private string _searchText = "";
    /// <summary>Words to find in a fit's name or its hull's (as shown, or in English); every word must be found.</summary>
    public string SearchText { get => _searchText; set => this.RaiseAndSetIfChanged(ref _searchText, value); }

    private List<FitEntry>? _allFits;
    private Dictionary<int, string> _hullNames = new();   // English, by type id
    private List<FitEntry> Filtered()
    {
        IEnumerable<FitEntry> fits = _allFits!;
        if (_selectedOwner is { } owner) fits = fits.Where(f => f.Source == owner.Source && f.OwnerName == owner.Name);
        var words = _searchText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 0)
            fits = fits.Where(f => words.All(w => f.Data.Name.Contains(w, StringComparison.CurrentCultureIgnoreCase)
                || SdeNames.Matches(SdeNameKind.Type, f.Data.ShipTypeId, _hullNames.GetValueOrDefault(f.Data.ShipTypeId, ""), w)));
        return fits.ToList();
    }
    private bool Searching => _searchText.Trim().Length > 0;

    /// <summary>Deletes a fit saved in EVE Console; set by the fitting tool, which keeps them.</summary>
    public Func<long, Task>? DeleteLocal { get; init; }
    public bool AllowsDelete      => DeleteLocal is not null;
    public bool CanDeleteSelected => DeleteLocal is not null && _selectedNode?.Entry is { Source: FitSource.App };

    /// <summary>Deletes the EVE Console fit chosen, once the view has asked.</summary>
    public async Task DeleteSelectedAsync()
    {
        if (!CanDeleteSelected || _selectedNode!.Entry is not { SavedFitId: { } id } entry) return;
        await DeleteLocal!(id);
        _allFits!.Remove(entry);
        await BuildTreeAsync(Filtered(), CancellationToken.None);
    }
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
            this.RaisePropertyChanged(nameof(CanDeleteSelected));
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
    /// <summary>Whether the picker asks which Inventory Levels group the fit goes into. The
    /// fitting tool opens fits rather than adding them to a group, and turns this off.</summary>
    public bool ChooseGroup { get; init; } = true;

    /// <summary>The window's title and its confirm button: adding a fit's items to a group, or
    /// opening the fit in the fitting tool.</summary>
    public string WindowTitle => ChooseGroup ? CommonText.TitleAddItemsFromFit : FittingText.TitleOpenFit;
    public string ConfirmText => ChooseGroup ? CommonText.AddItems2 : FittingText.LoadFit;

    public bool CanConfirm     => _selectedNode?.IsFit == true && (!ChooseGroup || _selectedGroup != null);

    /// <param name="svc">Reads the game's fittings; null lists only <paramref name="localFits"/>.</param>
    /// <param name="localFits">Fits saved in EVE Console, listed with the game's.</param>
    public FitSelectorViewModel(
        FittingsService?                    svc,
        IDbContextFactory<AppDbContext>     dbFactory,
        ObservableCollection<Character>     characters,
        ObservableCollection<Corporation>   corporations,
        IReadOnlyList<FitGroupOption>       groupOptions,
        int                                 preselectedGroupId,
        IReadOnlyList<FitEntry>?            localFits = null)
    {
        _svc          = svc;
        _localFits    = localFits ?? [];
        _dbFactory    = dbFactory;
        _characters   = characters;
        _corporations = corporations;

        foreach (var g in groupOptions) Groups.Add(g);
        _selectedGroup = Groups.FirstOrDefault(g => g.GroupId == preselectedGroupId)
                         ?? Groups.FirstOrDefault();

        // Typing narrows the tree after a pause, not on every key.
        this.WhenAnyValue(x => x.SearchText).Skip(1)
            .Throttle(TimeSpan.FromMilliseconds(250))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(text => { if (_allFits is not null) _ = BuildTreeAsync(Filtered(), CancellationToken.None); });

        _ = LoadAsync(CancellationToken.None);
    }

    // ── Load ──────────────────────────────────────────────────────────────────

    private async Task LoadAsync(CancellationToken ct)
    {
        try
        {
            // EVE Console's own fits, then the game's; if the game cannot be reached the picker
            // still lists what is saved here, and says why the rest are missing.
            var fits = _localFits.ToList();
            string? gameError = null;
            if (_svc is not null)
            {
                try { fits.AddRange(await _svc.FetchAllFitsAsync(_characters, _corporations, ct)); }
                catch (Exception ex) { gameError = string.Format(CommonText.ErrorWithMessage, ex.Message); }
            }
            _allFits = fits;
            var hullIds = fits.Select(f => f.Data.ShipTypeId).Distinct().ToList();
            await using (var db = _dbFactory.CreateDbContext())
                _hullNames = await db.SdeTypes.AsNoTracking().Where(t => hullIds.Contains(t.TypeId))
                    .ToDictionaryAsync(t => t.TypeId, t => t.Name, ct);
            foreach (var f in fits.OrderBy(f => f.Source == FitSource.App ? 0 : 1).ThenBy(f => f.Source)
                                  .ThenBy(f => f.OwnerName, StringComparer.OrdinalIgnoreCase)
                                  .DistinctBy(f => (f.Source, f.OwnerName)))
                Owners.Add(new(new FitOwner(f.Source, f.OwnerName), OwnerLabel(f)));
            StatusText = CommonText.BuildingFitTree;
            await BuildTreeAsync(fits, ct);
            if (gameError is not null) StatusText = gameError;
        }
        catch (Exception ex)
        {
            StatusText = string.Format(CommonText.ErrorWithMessage, ex.Message);
            IsLoading  = false;
        }
    }

    private async Task BuildTreeAsync(List<FitEntry> fits, CancellationToken ct)
    {
        RootNodes.Clear();
        SelectedNode = null;
        if (fits.Count == 0)
        {
            StatusText = Searching ? FittingText.NoFitsMatch : CommonText.NoFitsFound;
            IsLoading  = false;
            return;
        }

        await using var db = _dbFactory.CreateDbContext();

        // Market groups and hulls are named in the interface language, and each level of the tree
        // is listed in the order of the names shown. The tree itself is built by id.
        await SdeNames.EnsureLoadedAsync(ct);

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
            var name = shipInfo.TryGetValue(typeId, out var si) ? SdeNames.Type(typeId, si.Name) : string.Format(CommonText.TypeIdWithId, typeId);
            if (!shipsByGroup.TryGetValue(groupId, out var ships))
                shipsByGroup[groupId] = ships = [];
            ships.Add((typeId, name, typeFits));
        }

        string GroupName(int id) => SdeNames.MarketGroup(id, allGroups[id].Name);

        // Root groups = relevant groups whose parent is NOT in the relevant set
        var rootIds = relevantGroupIds
            .Where(id => !allGroups[id].ParentGroupId.HasValue
                         || !relevantGroupIds.Contains(allGroups[id].ParentGroupId!.Value))
            .OrderBy(GroupName, StringComparer.CurrentCulture)
            .ToList();

        // Recursive tree builder
        FitTreeNode BuildGroupNode(int groupId)
        {
            var node = new FitTreeNode(startExpanded: true)
            {
                Kind = FitNodeKind.MarketGroup,
                Name = GroupName(groupId)
            };

            if (childrenMap.TryGetValue(groupId, out var children))
                foreach (var cid in children.OrderBy(GroupName, StringComparer.CurrentCulture))
                    node.Children.Add(BuildGroupNode(cid));

            if (shipsByGroup.TryGetValue(groupId, out var ships))
            {
                foreach (var (typeId, shipName, shipFits) in ships.OrderBy(s => s.Name, StringComparer.CurrentCulture))
                {
                    // A search opens every hull it found something under.
                    var shipNode = new FitTreeNode(startExpanded: Searching) { Kind = FitNodeKind.Ship, Name = shipName, TypeId = typeId };
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

        // The hull and every module, charge and drone in the interface language, each section in
        // the order of the names shown.
        await SdeNames.EnsureLoadedAsync(ct);
        string Name(int id) => typeNames.TryGetValue(id, out var english)
            ? SdeNames.Type(id, english)
            : string.Format(CommonText.TypeIdWithId, id);

        DetailLines.Add(new FitDetailLine { IsHeader = true, Text = CommonText.FitSectionHull });
        DetailLines.Add(new FitDetailLine { Text = $"1× {Name(entry.Data.ShipTypeId)}" });

        foreach (var cat in Enum.GetValues<FitSection>())
        {
            if (!byCategory.TryGetValue(cat, out var items)) continue;
            DetailLines.Add(new FitDetailLine { IsHeader = true, Text = Heading(cat) });
            foreach (var (typeId, qty) in items.OrderBy(kv => Name(kv.Key), StringComparer.CurrentCulture))
                DetailLines.Add(new FitDetailLine { Text = $"{qty:N0}× {Name(typeId)}" });
        }
    }
}
