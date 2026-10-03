using ReactiveUI;

namespace EveConsole.ViewModels;

public class ToolTab
{
    public string Id        { get; }
    public string Title     { get; }
    public bool   CanClose  { get; }
    public object ViewModel { get; }

    public ToolTab(string id, string title, object viewModel, bool canClose = true)
    {
        Id = id; Title = title; ViewModel = viewModel; CanClose = canClose;
    }
}

/// <summary>
/// One side of the main window: a row of tabs and the tool showing. There are two; the second is
/// shown only while it holds a tab, and the window goes back to one side when either empties.
/// </summary>
public class ToolPane(bool isRight) : ReactiveObject
{
    public bool IsRight { get; } = isRight;
    /// <summary>The window's tabs this side belongs to.</summary>
    public TabWorkspace? Workspace { get; init; }
    public System.Collections.ObjectModel.ObservableCollection<ToolTab> Tabs { get; } = [];

    private ToolTab? _selectedTab;
    public ToolTab? SelectedTab { get => _selectedTab; set => this.RaiseAndSetIfChanged(ref _selectedTab, value); }

    private bool _isActive;
    /// <summary>The side last clicked: new tools open there, and it is what "on screen" means.</summary>
    public bool IsActive { get => _isActive; set => this.RaiseAndSetIfChanged(ref _isActive, value); }

    private bool _isDimmed;
    /// <summary>Split, and not the active side: its selected tab is marked more quietly.</summary>
    public bool IsDimmed { get => _isDimmed; set => this.RaiseAndSetIfChanged(ref _isDimmed, value); }
}

public class NavItem : ReactiveObject
{
    public string ToolId { get; }
    public string Title  { get; }

    private bool _isOpen;
    public bool IsOpen
    {
        get => _isOpen;
        set => this.RaiseAndSetIfChanged(ref _isOpen, value);
    }

    public NavItem(string toolId, string title) { ToolId = toolId; Title = title; }
}

/// <summary>
/// A section of the navigation: its tools, shown or folded away. Whether it is folded is this
/// machine's (UiState, "nav.group.&lt;id&gt;.collapsed"), kept by <paramref name="Id"/> because the
/// title is in the interface language. Folded, it still shows whether any of its tools is open.
/// </summary>
public class NavGroup : ReactiveObject
{
    public string    Id    { get; }
    public string    Title { get; }
    public NavItem[] Items { get; }

    public NavGroup(string id, string title, NavItem[] items)
    {
        Id = id; Title = title; Items = items;
        try { _isExpanded = !EveConsole.Services.UiState.GetBool(StateKey, false); } catch { }
        foreach (var item in items)
            item.WhenAnyValue(i => i.IsOpen).Subscribe(_ =>
            {
                this.RaisePropertyChanged(nameof(HasOpenTool));
                this.RaisePropertyChanged(nameof(ShowsOpenMark));
            });
        ToggleCommand = ReactiveCommand.Create(() => { IsExpanded = !IsExpanded; });
    }

    private string StateKey => $"nav.group.{Id}.collapsed";

    private bool _isExpanded = true;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (value == _isExpanded) return;
            this.RaiseAndSetIfChanged(ref _isExpanded, value);
            this.RaisePropertyChanged(nameof(Chevron));
            this.RaisePropertyChanged(nameof(ShowsOpenMark));
            try { EveConsole.Services.UiState.SetBool(StateKey, !value); } catch { }
        }
    }

    public string Chevron => _isExpanded ? "▾" : "▸";
    public bool HasOpenTool => Items.Any(i => i.IsOpen);
    /// <summary>Folded, the header carries the open-tool mark its tools would.</summary>
    public bool ShowsOpenMark => !_isExpanded && HasOpenTool;
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> ToggleCommand { get; }
}
