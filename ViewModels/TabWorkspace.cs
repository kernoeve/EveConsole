using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>
/// The tabs of one window: one side, or two while the right one holds a tab. The main window has
/// one, and so does every window a tab is dragged out into. A tab belongs to exactly one side of
/// one workspace; moving one between workspaces is the main view model's
/// (<see cref="MainWindowViewModel.MoveTab"/>), which knows them all.
/// </summary>
public class TabWorkspace : ReactiveObject
{
    /// <summary>The main window's, which is never closed and alone holds the Overview.</summary>
    public bool IsMain { get; }

    /// <summary>The first side, which holds every tab until one is dragged to the right.</summary>
    public ToolPane LeftPane  { get; }
    /// <summary>The second side, shown only while it holds a tab.</summary>
    public ToolPane RightPane { get; }

    public TabWorkspace(bool isMain)
    {
        IsMain    = isMain;
        LeftPane  = new ToolPane(false) { Workspace = this };
        RightPane = new ToolPane(true)  { Workspace = this };
        ActivePane = LeftPane;
        // A tab picked on a side by clicking it changes that side's selection; when it is the
        // active side, that is the tool on screen.
        foreach (var pane in new[] { LeftPane, RightPane })
            pane.WhenAnyValue(p => p.SelectedTab).Subscribe(_ =>
            {
                if (pane == ActivePane) { this.RaisePropertyChanged(nameof(SelectedTab)); this.RaisePropertyChanged(nameof(Title)); }
                this.RaisePropertyChanged(nameof(OtherSideTab));
            });
    }

    /// <summary>Every tab, on either side.</summary>
    public IEnumerable<ToolTab> OpenTabs => LeftPane.Tabs.Concat(RightPane.Tabs);
    public bool IsEmpty => LeftPane.Tabs.Count == 0 && RightPane.Tabs.Count == 0;
    public bool IsSplit => RightPane.Tabs.Count > 0;

    private ToolPane? _activePane;
    /// <summary>The side last clicked: tools open there, and its tab is the one "on screen".</summary>
    public ToolPane ActivePane
    {
        get => _activePane ?? LeftPane;
        set
        {
            if (value == _activePane || value.Workspace != this) return;
            if (_activePane is not null) _activePane.IsActive = false;
            _activePane = value;
            value.IsActive = true;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(SelectedTab));
            this.RaisePropertyChanged(nameof(Title));
            MarkPanes();
        }
    }

    /// <summary>The tool being worked in: the tab showing on the active side. Setting it shows a
    /// tab on whichever side holds it, and makes that side the active one.</summary>
    public ToolTab? SelectedTab
    {
        get => ActivePane.SelectedTab;
        set
        {
            if (value is null || PaneOf(value) is not { } pane) return;
            pane.SelectedTab = value;
            ActivePane = pane;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(Title));
        }
    }

    /// <summary>A window of its own is named for the tool it is showing.</summary>
    public string Title => SelectedTab?.Title ?? "";

    /// <summary>What is on the other side, when split.</summary>
    public ToolTab? OtherSideTab => IsSplit ? (ActivePane.IsRight ? LeftPane : RightPane).SelectedTab : null;

    public ToolPane? PaneOf(ToolTab tab) =>
        LeftPane.Tabs.Contains(tab) ? LeftPane : RightPane.Tabs.Contains(tab) ? RightPane : null;

    public bool Contains(ToolTab tab) => PaneOf(tab) is not null;

    /// <summary>Adds <paramref name="tab"/> to <paramref name="pane"/> (the active side when null),
    /// before position <paramref name="index"/> (at the end when null), and shows it.</summary>
    public void Add(ToolTab tab, ToolPane? pane = null, int? index = null)
    {
        var to = pane is not null && pane.Workspace == this ? pane : ActivePane;
        to.Tabs.Insert(Math.Clamp(index ?? to.Tabs.Count, 0, to.Tabs.Count), tab);
        SelectedTab = tab;
        PanesChanged();
    }

    /// <summary>Takes <paramref name="tab"/> out, showing another in its place.</summary>
    public void Remove(ToolTab tab)
    {
        TakeOut(tab);
        PanesChanged();
    }

    /// <summary>
    /// Puts <paramref name="tab"/>, already here, on <paramref name="to"/> before position
    /// <paramref name="index"/> (at the end when null): along a row of tabs, or across to the other
    /// side. Onto the right while there is only one side, it splits the window.
    /// </summary>
    public void Move(ToolTab tab, ToolPane to, int? index = null)
    {
        if (PaneOf(tab) is not { } from || to.Workspace != this) return;
        var i  = from.Tabs.IndexOf(tab);
        var at = Math.Clamp(index ?? to.Tabs.Count, 0, to.Tabs.Count);
        if (from == to)
        {
            if (at > i) at--;
            if (at != i) from.Tabs.Move(i, at);
        }
        else
        {
            TakeOut(tab);
            to.Tabs.Insert(Math.Min(at, to.Tabs.Count), tab);
        }
        SelectedTab = tab;
        PanesChanged();
    }

    /// <summary>Takes a tab off its side, showing Overview there if it is on that side, else a neighbour.</summary>
    private void TakeOut(ToolTab tab)
    {
        if (PaneOf(tab) is not { } pane) return;
        var i = pane.Tabs.IndexOf(tab);
        // Read before the removal: the strip clears its own selection when the selected tab
        // leaves it, and the side would be left showing nothing.
        var wasShowing = pane.SelectedTab == tab;
        pane.Tabs.RemoveAt(i);
        if (wasShowing || pane.SelectedTab is null)
            pane.SelectedTab = pane.Tabs.FirstOrDefault(t => t.Id == "overview")
                               ?? (pane.Tabs.Count == 0 ? null : pane.Tabs[Math.Min(i, pane.Tabs.Count - 1)]);
    }

    /// <summary>Two sides only while both hold a tab: when the last tab leaves either, the window
    /// goes back to one side.</summary>
    private void PanesChanged()
    {
        if (LeftPane.Tabs.Count == 0 && RightPane.Tabs.Count > 0)
        {
            var showing = RightPane.SelectedTab;
            var moving  = RightPane.Tabs.ToList();
            RightPane.Tabs.Clear();
            RightPane.SelectedTab = null;
            foreach (var t in moving) LeftPane.Tabs.Add(t);
            LeftPane.SelectedTab = showing;
        }
        if (RightPane.Tabs.Count == 0 && ActivePane == RightPane) ActivePane = LeftPane;
        this.RaisePropertyChanged(nameof(IsSplit));
        this.RaisePropertyChanged(nameof(IsEmpty));
        this.RaisePropertyChanged(nameof(SelectedTab));
        this.RaisePropertyChanged(nameof(Title));
        this.RaisePropertyChanged(nameof(OtherSideTab));
        this.RaisePropertyChanged(nameof(ShowSplitDropZone));
        MarkPanes();
    }

    private void MarkPanes()
    {
        LeftPane.IsDimmed  = IsSplit && ActivePane != LeftPane;
        RightPane.IsDimmed = IsSplit && ActivePane != RightPane;
    }

    // ── While a tab is dragged ────────────────────────────────────────────────

    private bool _isDraggingTab, _isDragSource;

    /// <summary>A tab is being dragged, from here or from another window.</summary>
    public bool IsDraggingTab
    {
        get => _isDraggingTab;
        set { this.RaiseAndSetIfChanged(ref _isDraggingTab, value); this.RaisePropertyChanged(nameof(ShowSplitDropZone)); }
    }

    /// <summary>The tab being dragged is one of these.</summary>
    public bool IsDragSource
    {
        get => _isDragSource;
        set { this.RaiseAndSetIfChanged(ref _isDragSource, value); this.RaisePropertyChanged(nameof(ShowSplitDropZone)); }
    }

    /// <summary>
    /// While dragging, on one side: the right half takes a tab to show two tools side by side —
    /// so long as something would be left on the left, which a tab from another window always leaves.
    /// </summary>
    public bool ShowSplitDropZone => _isDraggingTab && !IsSplit && LeftPane.Tabs.Count > (_isDragSource ? 1 : 0);
}
