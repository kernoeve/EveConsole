using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EveConsole.Services;
using ReactiveUI;

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
    public string Name         { get; }
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

    public bool CanConfirm => _selectedNode != null;

    private string _statusText = "Loading market groups…";
    public string StatusText
    {
        get => _statusText;
        private set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    public MarketGroupPickerViewModel(BatchAddService svc)
    {
        _svc = svc;
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
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

        // Sort each level by name
        roots.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        SortChildren(roots);

        RootNodes.Clear();
        foreach (var r in roots) RootNodes.Add(r);

        StatusText = "Select a market group to add its items.";
    }

    private static void SortChildren(IEnumerable<MarketGroupPickerNode> nodes)
    {
        foreach (var n in nodes)
        {
            var sorted = n.Children.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
            n.Children.Clear();
            foreach (var c in sorted) n.Children.Add(c);
            SortChildren(n.Children);
        }
    }
}
