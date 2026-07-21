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
