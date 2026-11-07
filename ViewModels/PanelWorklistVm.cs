using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia.Threading;
using EveConsole.Localization;
using EveConsole.Models;
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
            .Select(r => new WorklistRowVm(r.Item, r.Sequence) { ExpansionChanged = OnExpansionChanged })
            .ToList();
        RowExpansion.Carry(_parents, fresh);
        _parents = fresh;
        Rows.ResetTo(FamilyRows.WithChildren(fresh, r => r.IsExpanded && r.CanExpand, r => r.Children));
        _ = Task.WhenAll(fresh.Select(r => r.LoadIconAsync()));

        Counts.Clear();
        foreach (var kind in fresh.GroupBy(r => WorklistRowVm.KindLabel(r.Item)))
            Counts.Add(new WorklistKindCount(kind.Key, kind.Count()));

        Note = name == null ? ClientLayoutsText.WorklistNoClient
            : id == null ? string.Format(System.Globalization.CultureInfo.CurrentCulture, ClientLayoutsText.WorklistNotInApp, name)
            : fresh.Count == 0 ? string.Format(System.Globalization.CultureInfo.CurrentCulture, ClientLayoutsText.WorklistNothing, name)
            : "";
    }

    private void OnExpansionChanged(WorklistRowVm row)
    {
        FamilyRows.Toggle(Rows, row, row.IsExpanded, row.Children);
        if (row.IsExpanded) _ = Task.WhenAll(row.Children.Select(c => c.LoadIconAsync()));
    }
}
