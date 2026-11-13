using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia.Threading;
using EveConsole.Localization;
using EveConsole.Models;
using EveConsole.Services;
using EveConsole.Services.ClientLayouts;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>One kind of task and how many of it are ready, for a Worklist control's single line.</summary>
public sealed record WorklistKindCount(string Kind, int Count);

/// <summary>
/// The worklist of one character, for a panel: that character's ready tasks, opening into their
/// detail as in the Worklist tool, and nothing else from that tool. The character is the one in
/// whichever client was last in use on this panel's monitor (see <see cref="MonitorFocus"/>).
///
/// <para>⚠️ Its rows are copies. Opening a row of the tool's own would open it in the tool too, and
/// the tool's character filter would change what the tool shows.</para>
///
/// <para>⚠️ Only rows carrying this character's id. A task with no one character, such as delivering
/// finished jobs or refining, isn't anyone's here; a long haul shows only for its lead pilot.</para>
/// </summary>
public sealed class PanelWorklistVm : ReactiveObject, IDisposable
{
    private readonly WorklistViewModel _worklist;
    private readonly ObservableCollection<Character> _characters;
    private readonly MonitorFocus _focus;
    private readonly string _monitor;
    private readonly DispatcherTimer _stale;
    private List<WorklistRowVm> _parents = [];
    private bool _started;

    public PanelWorklistVm(WorklistViewModel worklist, ObservableCollection<Character> characters, MonitorFocus focus, string monitor)
    {
        _worklist = worklist;
        _characters = characters;
        _focus = focus;
        _monitor = monitor;

        // Nothing else refreshes the worklist on a timer unless the Overview happens to show it.
        _stale = new DispatcherTimer(TimeSpan.FromMinutes(1), DispatcherPriority.Background, (_, _) => _ = _worklist.RefreshIfStaleAsync(TimeSpan.FromMinutes(5)));
        _stale.Stop();
    }

    public ObservableCollection<string> Names { get; } = [];
    public BulkObservableCollection<WorklistRowVm> Rows { get; } = [];

    /// <summary>For the single line: how many of each kind of task there are, in the list's order.</summary>
    public ObservableCollection<WorklistKindCount> Counts { get; } = [];

    /// <summary>The character shown, or why there's no one.</summary>
    public string LineName => _selectedName ?? ClientLayoutsText.WorklistNoClientShort;

    private string? _selectedName;
    public string? SelectedName
    {
        get => _selectedName;
        set
        {
            // A ComboBox reports null while its list is being changed; that isn't a choice.
            if (value == null || _selectedName == value) return;
            // ⚠️ Acted on once the dropdown has finished choosing: choosing can reorder its names,
            // and changing its list while it was still committing the pick threw "index out of range".
            Dispatcher.UIThread.Post(() => _focus.Choose(_monitor, value));
        }
    }

    /// <summary>Opens the full Worklist tool, where any tool opens. Set by the panel.</summary>
    public Action? OpenWorklist { get; set; }

    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> OpenWorklistCommand =>
        _openWorklist ??= ReactiveCommand.Create(() => OpenWorklist?.Invoke());
    private ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit>? _openWorklist;

    private string _note = "";
    /// <summary>Why the list is empty, when it is.</summary>
    public string Note { get => _note; private set => this.RaiseAndSetIfChanged(ref _note, value); }

    public void Start()
    {
        if (_started) return;
        _started = true;
        _focus.Changed += OnFocusChanged;
        _worklist.PoolRows.CollectionChanged += OnPoolChanged;
        _worklist.PropertyChanged += OnWorklistChanged;
        _characters.CollectionChanged += OnPoolChanged;
        _stale.Start();
        ShowSelection();
        _ = _worklist.RefreshIfStaleAsync(TimeSpan.FromMinutes(5));
    }

    public void Dispose()
    {
        if (!_started) return;
        _started = false;
        _focus.Changed -= OnFocusChanged;
        _worklist.PoolRows.CollectionChanged -= OnPoolChanged;
        _worklist.PropertyChanged -= OnWorklistChanged;
        _characters.CollectionChanged -= OnPoolChanged;
        _stale.Stop();
    }

    private void OnFocusChanged(string monitor)
    {
        if (monitor == _monitor) ShowSelection();
    }

    private void OnPoolChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void ShowSelection()
    {
        var names = _focus.NamesOn(_monitor);
        foreach (var gone in Names.Where(n => !names.Contains(n)).ToList()) Names.Remove(gone);
        // In the order given: the client's own characters come first.
        for (var i = 0; i < names.Count; i++)
        {
            var at = Names.IndexOf(names[i]);
            if (at < 0) Names.Insert(Math.Min(i, Names.Count), names[i]);
            else if (at != i) Names.Move(at, i);
        }

        _selectedName = _focus.SelectedOn(_monitor);
        this.RaisePropertyChanged(nameof(SelectedName));
        this.RaisePropertyChanged(nameof(LineName));
        Rebuild();
    }

    private void Rebuild()
    {
        var name = _selectedName;
        var id = name == null ? null
            : _characters.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))?.Id;

        var fresh = id is not { } characterId ? [] : _worklist.PoolRows
            .Where(r => r.Item.CharacterId == characterId)
            // ⚠️ By task name here, and only here: this list can't be sorted by the one reading
            // it, and alike tasks side by side are run through faster. The Worklist's own grids
            // keep their order. Then the Worklist's order, among tasks of one name.
            //
            // A Transfer by its item, not its title: the title leads with the quantity, which
            // scatters the same item ("1 × Tritanium", "176,793 × Tritanium") across the list.
            // One of several items has no single item, and keeps its title.
            .OrderBy(r => r.Item.Kind == EveConsole.Services.Worklist.WorklistKind.Transfer && r.Item.TypeName.Length > 0 ? r.Item.TypeName : r.Item.Title,
                     StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(r => r.Sequence)
            .Select(r => new WorklistRowVm(r.Item, r.Sequence) { ExpansionChanged = OnExpansionChanged })
            .ToList();
        RowExpansion.Carry(_parents, fresh);
        _parents = fresh;
        Rows.ResetTo(FamilyRows.WithChildren(fresh, r => r.IsExpanded && r.CanExpand, r => r.Children));
        _ = Task.WhenAll(fresh.Select(r => r.LoadIconAsync()));

        Counts.Clear();
        foreach (var kind in fresh.GroupBy(r => WorklistRowVm.KindLabel(r.Item)))
            Counts.Add(new WorklistKindCount(kind.Key, kind.Count()));

        _shownId = id;
        _shownCount = fresh.Count;
        ShowNote();
    }

    private long? _shownId;
    private int _shownCount;

    /// <summary>
    /// Why the list is empty, when it is. Before the worklist's first build that's "loading" while
    /// one is coming; but a first build that failed outright (the database out of reach, say) leaves
    /// it unbuilt, and then the worklist's own status, which says what went wrong, rather than a
    /// "loading" that never ends.
    /// </summary>
    private void ShowNote()
    {
        var name = _selectedName;
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        var failed = !_worklist.HasBuilt && !_worklist.IsLoading && AppStartup.IsSettled && _worklist.Status.Length > 0;
        Note = name == null ? ClientLayoutsText.WorklistNoClient
            : _shownId == null ? string.Format(culture, ClientLayoutsText.WorklistNotInApp, name)
            : _shownCount == 0 && failed ? _worklist.Status
            : _shownCount == 0 && !_worklist.HasBuilt ? ClientLayoutsText.WorklistLoading
            : _shownCount == 0 ? string.Format(culture, ClientLayoutsText.WorklistNothing, name)
            : "";
    }

    /// <summary>A build starting or stopping can change why the list is empty, without changing its rows.</summary>
    private void OnWorklistChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WorklistViewModel.IsLoading) or nameof(WorklistViewModel.Status))
            Dispatcher.UIThread.Post(ShowNote);
    }

    // Ticking a stop off from here: the worklist this list is cut from records it and rebuilds,
    // and the rebuilt rows come back through PoolRows.
    public Task MarkHauledAsync(WorklistRowVm stopRow) => _worklist.MarkHauledAsync(stopRow);
    public ContractedCargoVm? ContractedEditor(WorklistRowVm stopRow) => _worklist.ContractedEditor(stopRow);
    public Task MarkContractedAsync(WorklistRowVm stopRow, IReadOnlyList<(int TypeId, long Quantity)> cargo) =>
        _worklist.MarkContractedAsync(stopRow, cargo);

    private void OnExpansionChanged(WorklistRowVm row)
    {
        FamilyRows.Toggle(Rows, row, row.IsExpanded, row.Children);
        if (row.IsExpanded) _ = Task.WhenAll(row.Children.Select(c => c.LoadIconAsync()));
    }
}
