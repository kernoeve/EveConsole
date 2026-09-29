using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EveConsole.Services;
using ReactiveUI;
using EveConsole.Localization;

namespace EveConsole.ViewModels;

public record MarketGroupPickerResult(int MarketGroupId, string GroupName, int TargetQty);

public record BlueprintPickerResult(
    int    BlueprintTypeId,
    int    ProductTypeId,
    string ProductName,
    int    ME,
    int    Runs,
    bool   WholeChain,
    int?   ParkId);

// ── Tree node ─────────────────────────────────────────────────────────────────

public class MarketGroupPickerNode : ReactiveObject
{
    public int    MarketGroupId { get; }

    /// <summary>English: what the pick hands back (MarketGroupPickerResult.GroupName).</summary>
    public string Name         { get; }

    /// <summary>The name in the interface language, which the tree shows and sorts on.</summary>
    public string DisplayName  { get; }

    public ObservableCollection<MarketGroupPickerNode> Children { get; } = [];

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set => this.RaiseAndSetIfChanged(ref _isExpanded, value);
    }

    public MarketGroupPickerNode(int id, string name)
    {
        MarketGroupId = id;
        Name          = name;
        DisplayName   = SdeNames.MarketGroup(id, name);
    }
}

// ── View-model ────────────────────────────────────────────────────────────────

public class MarketGroupPickerViewModel : ReactiveObject
{
    private readonly BatchAddService _svc;

    public ObservableCollection<MarketGroupPickerNode> RootNodes { get; } = [];

    private MarketGroupPickerNode? _selectedNode;
    public MarketGroupPickerNode? SelectedNode
    {
        get => _selectedNode;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedNode, value);
            this.RaisePropertyChanged(nameof(CanConfirm));
        }
    }

    private int _targetQty = 1;
    public int TargetQty
    {
        get => _targetQty;
        set => this.RaiseAndSetIfChanged(ref _targetQty, Math.Max(1, value));
    }

    // Inventory Levels uses a per-item target quantity; Sale Posting does not, so it hides the field.
    public bool ShowQuantity { get; }

    public bool CanConfirm => _selectedNode != null;

    private string _statusText = CommonText.LoadingMarketGroups;
    public string StatusText
    {
        get => _statusText;
        private set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    public MarketGroupPickerViewModel(BatchAddService svc, bool showQuantity = true)
    {
        _svc = svc;
        ShowQuantity = showQuantity;
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        await SdeNames.EnsureLoadedAsync(ct);   // the nodes keep the names they are built with
        var allGroups    = await _svc.LoadAllMarketGroupsAsync(ct);
        var withItems    = await _svc.GetGroupIdsWithItemsAsync(ct);

        // Mark all ancestors of leaf groups as "has items reachable"
        var groupMap     = allGroups.ToDictionary(g => g.MarketGroupId);
        var reachable    = new HashSet<int>(withItems);
        foreach (var leafId in withItems)
        {
            var g = groupMap.GetValueOrDefault(leafId);
            while (g?.ParentGroupId.HasValue == true)
            {
                reachable.Add(g.ParentGroupId!.Value);
                g = groupMap.GetValueOrDefault(g.ParentGroupId!.Value);
            }
        }

        // Build children map filtered to reachable groups
        var nodeMap  = new Dictionary<int, MarketGroupPickerNode>();
        foreach (var g in allGroups.Where(g => reachable.Contains(g.MarketGroupId)))
            nodeMap[g.MarketGroupId] = new MarketGroupPickerNode(g.MarketGroupId, g.Name);

        // Build parent-child relationships
        var roots = new List<MarketGroupPickerNode>();
        foreach (var g in allGroups.Where(g => reachable.Contains(g.MarketGroupId)))
        {
            if (!nodeMap.TryGetValue(g.MarketGroupId, out var node)) continue;
            if (g.ParentGroupId.HasValue && nodeMap.TryGetValue(g.ParentGroupId.Value, out var parent))
                parent.Children.Add(node);
            else
                roots.Add(node);
        }

        // Sort each level by the name shown
        roots.Sort((a, b) => StringComparer.CurrentCulture.Compare(a.DisplayName, b.DisplayName));
        SortChildren(roots);

        RootNodes.Clear();
        foreach (var r in roots) RootNodes.Add(r);

        StatusText = CommonText.SelectMarketGroup;
    }

    private static void SortChildren(IEnumerable<MarketGroupPickerNode> nodes)
    {
        foreach (var n in nodes)
        {
            var sorted = n.Children.OrderBy(c => c.DisplayName, StringComparer.CurrentCulture).ToList();
            n.Children.Clear();
            foreach (var c in sorted) n.Children.Add(c);
            SortChildren(n.Children);
        }
    }
}
