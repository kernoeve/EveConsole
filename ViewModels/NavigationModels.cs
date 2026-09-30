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

public record NavGroup(string Title, NavItem[] Items);
