using ReactiveUI;

namespace EveCortex.ViewModels;

public class ScopeItem : ReactiveObject
{
    private bool _isSelected = true;

    public string Scope        { get; }
    public string FriendlyName { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }

    public ScopeItem(string scope, bool stripCorporation = false)
    {
        Scope = scope;
        FriendlyName = MakeFriendlyName(scope, stripCorporation);
    }

    private static string MakeFriendlyName(string scope, bool stripCorporation)
    {
        // "esi-skills.read_skills.v1" → "Read Skills"
        var parts = scope.Split('.');
        if (parts.Length < 2) return scope;
        var name = string.Join(' ',
            parts[1].Split('_')
                    .Select(w => w.Length == 0 ? w : char.ToUpper(w[0]) + w[1..]));
        return stripCorporation
            ? name.Replace("Corporation ", "").Replace(" Corporation", "").Trim()
            : name;
    }
}

// Read-only scope data bound to the per-character details panel in Settings.
public record ScopeDisplayItem(string FriendlyName, bool IsGranted);
public record ScopeDisplayGroup(string Category, IReadOnlyList<ScopeDisplayItem> Items);

public class ScopeGroup : ReactiveObject
{
    public string                   Category { get; }
    public IReadOnlyList<ScopeItem> Items    { get; }

    private bool _allSelected = true;
    public bool AllSelected
    {
        get => _allSelected;
        set
        {
            this.RaiseAndSetIfChanged(ref _allSelected, value);
            foreach (var item in Items)
                item.IsSelected = value;
        }
    }

    public ScopeGroup(string category, IEnumerable<ScopeItem> items)
    {
        Category = category;
        Items    = [.. items];
    }
}
