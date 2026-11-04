using System.Globalization;
using System.Text.RegularExpressions;
using EveConsole.Localization;

namespace EveConsole.ViewModels;

/// <summary>How the Asset Browser's Detailed View groups its rows.</summary>
public enum AssetGroupBy { None, Owner, System, Station, Location }

/// <summary>
/// A group's own row in the Detailed View: its name in the Type Name column, indented by depth,
/// and the totals of everything under it in the columns that add up.
/// </summary>
public sealed class AssetGroupRow : GridRow
{
    public AssetGroupRow(Dictionary<string, string> data, string key, int level) : base(data)
    {
        Key   = key;
        Level = level;
    }

    /// <summary>The whole path's key, outermost first: what an open group is remembered by.</summary>
    public string Key   { get; }
    public int    Level { get; }
    public bool   IsExpanded { get; set; }

    public override bool IsGroupHeader => true;

    internal readonly List<AssetGroupRow> Groups = [];
    internal readonly List<GridRow>       Items  = [];
}

/// <summary>One asset row as read for grouping: the row shown, and the raw facts it is grouped and
/// totalled by.</summary>
internal sealed record AssetLeaf(
    GridRow Row, long ItemId, long OwnerId, string OwnerType, long RootId, long SystemId,
    string Flag, double Quantity, double Value, double Volume);

/// <summary>One item from the asset table, as the container walk needs it.</summary>
internal sealed record AssetNode(long ItemId, int TypeId, long LocationId, string Flag);

/// <summary>
/// Builds and flattens the Detailed View's groups.
///
/// <para>⚠️ Built here from the parent chain, not read from the Container column. That column's SQL
/// follows three hops and gives up beyond them — an item four containers deep has no path at all —
/// while the Location grouping has to go as deep as the hangar does.</para>
/// </summary>
internal static class AssetGroups
{
    /// <summary>The label column, where a group row's name and count go.</summary>
    internal const string LabelColumn = "Type Name";

    /// <summary>How far a container walk goes before it gives up: far past anything the game
    /// nests, and a guard against a loop in a half-written asset snapshot.</summary>
    private const int MaxDepth = 32;

    private static readonly Regex Division = new("^CorpSAG[1-7]$", RegexOptions.Compiled);

    /// <summary>A corporation hangar division, or its deliveries hangar: a grouping level of its own.</summary>
    internal static bool IsDivision(string flag) => Division.IsMatch(flag) || flag == "CorpDeliveries";

    /// <summary>
    /// Where an item sits, outermost first, below the station and the owner: corporation hangar
    /// divisions, POS structures, ships, asset safety wraps and containers, as deep as they go.
    ///
    /// <para>The office is not a level: a division is the office's hangar, and "Office" above every
    /// one of them says nothing. A division is not an item either — it is the flag of whatever sits
    /// directly in the office or hangar array — so it is put in after the item that holds it.</para>
    /// </summary>
    internal static List<(string Key, string Label)> ContainerPath(
        long itemId, long ownerId, long rootId, string ownFlag,
        IReadOnlyDictionary<(long ItemId, long OwnerId), AssetNode> assets,
        Func<int, string> typeName, Func<long, string, string> divisionName)
    {
        var chain = new List<(AssetNode Parent, string ChildFlag)>();
        var at    = assets.TryGetValue((itemId, ownerId), out var self) ? self.LocationId : rootId;
        var flag  = self?.Flag ?? ownFlag;
        var seen  = new HashSet<long> { itemId };

        while (at != rootId && chain.Count < MaxDepth
               && assets.TryGetValue((at, ownerId), out var parent) && seen.Add(at))
        {
            chain.Insert(0, (parent, flag));
            flag = parent.Flag;
            at   = parent.LocationId;
        }

        var path = new List<(string, string)>();

        // In a structure's own corporation hangar, with nothing holding it: the division is the top.
        // (`flag` is now the outermost item's own flag, or the item's own when nothing holds it.)
        if (IsDivision(flag))
            path.Add(($"d{rootId}:{flag}", divisionName(ownerId, flag)));

        foreach (var (parent, childFlag) in chain)
        {
            if (parent.TypeId != OfficeTypeId)
                path.Add(($"i{parent.ItemId}", typeName(parent.TypeId)));
            if (IsDivision(childFlag))
                path.Add(($"d{parent.ItemId}:{childFlag}", divisionName(ownerId, childFlag)));
        }
        return path;
    }

    /// <summary>The Office type: a corporation's office in a station or structure.</summary>
    internal const int OfficeTypeId = 27;

    /// <summary>
    /// The groups, from each row's path. Groups are ordered by name; rows keep the order they came
    /// in, which is the grid's sort.
    /// </summary>
    internal static List<AssetGroupRow> Build(
        IEnumerable<AssetLeaf> leaves, Func<AssetLeaf, List<(string Key, string Label)>> pathOf,
        IReadOnlySet<string> expanded)
    {
        var top   = new List<AssetGroupRow>();
        var index = new Dictionary<string, AssetGroupRow>();
        var sums  = new Dictionary<AssetGroupRow, (long Count, double Qty, double Value, double Volume, string Label)>();

        foreach (var leaf in leaves)
        {
            AssetGroupRow? parent = null;
            var key = "";
            var level = 0;
            foreach (var (k, label) in pathOf(leaf))
            {
                key = key.Length == 0 ? k : key + "/" + k;
                if (!index.TryGetValue(key, out var g))
                {
                    g = new AssetGroupRow(new Dictionary<string, string>(), key, level) { IsExpanded = expanded.Contains(key) };
                    index[key] = g;
                    (parent?.Groups ?? top).Add(g);
                    sums[g] = (0, 0, 0, 0, label);
                }
                var s = sums[g];
                sums[g] = (s.Count + 1, s.Qty + leaf.Quantity, s.Value + leaf.Value, s.Volume + leaf.Volume, s.Label);
                parent = g;
                level++;
            }
            parent?.Items.Add(leaf.Row);
            leaf.Row.Indent       = level;
            leaf.Row.IndentColumn = LabelColumn;
        }

        foreach (var (g, s) in sums)
        {
            g.Indent       = g.Level;
            g.IndentColumn = LabelColumn;
            g.Set(LabelColumn, Plurals.Format(AssetsText.ResourceManager, nameof(AssetsText.GroupRowItemsOther), s.Count, s.Label));
            g.Set("Quantity",     s.Qty.ToString("N0", CultureInfo.CurrentCulture));
            g.Set("Value",        s.Value.ToString("N2", CultureInfo.CurrentCulture));
            g.Set("Total Volume", s.Volume.ToString("N2", CultureInfo.CurrentCulture));
        }

        Sort(top, sums);
        return top;
    }

    private static void Sort(List<AssetGroupRow> groups,
                             Dictionary<AssetGroupRow, (long Count, double Qty, double Value, double Volume, string Label)> sums)
    {
        // Places the app could not name last: their labels are in angle brackets in every
        // language ("<Unknown Structure>"), and by name they sorted above every real one.
        static bool Unnamed(string label) => label.StartsWith('<');
        groups.Sort((a, b) =>
        {
            var u = Unnamed(sums[a].Label).CompareTo(Unnamed(sums[b].Label));
            if (u != 0) return u;
            var c = string.Compare(sums[a].Label, sums[b].Label, StringComparison.CurrentCultureIgnoreCase);
            return c != 0 ? c : string.CompareOrdinal(a.Key, b.Key);
        });
        foreach (var g in groups) Sort(g.Groups, sums);
    }

    /// <summary>What the grid shows: every group row, and inside each open one its groups and rows.</summary>
    internal static List<GridRow> Flatten(IEnumerable<AssetGroupRow> groups)
    {
        var rows = new List<GridRow>();
        void Add(AssetGroupRow g)
        {
            rows.Add(g);
            if (!g.IsExpanded) return;
            foreach (var child in g.Groups) Add(child);
            rows.AddRange(g.Items);
        }
        foreach (var g in groups) Add(g);
        return rows;
    }

    /// <summary>Every group, at every depth.</summary>
    internal static IEnumerable<AssetGroupRow> All(IEnumerable<AssetGroupRow> groups) =>
        groups.SelectMany(g => All(g.Groups).Prepend(g));
}
