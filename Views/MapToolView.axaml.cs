using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using EveConsole.ViewModels;
using ReactiveUI;

namespace EveConsole.Views;

/// <summary>The Universe Map tool: tabs of maps and system pages, one side or two.</summary>
public partial class MapToolView : UserControl
{
    private readonly List<IDisposable> _handlers = [];

    public MapToolView()
    {
        InitializeComponent();

        SplitDropZone.AddHandler(DragDrop.DragOverEvent, (_, e) =>
            e.DragEffects = InProcessDrag.Get<MapTabViewModel>(e, MapPaneView.TabFormat) is not null ? DragDropEffects.Move : DragDropEffects.None);
        SplitDropZone.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            if (Vm is { } vm && InProcessDrag.Get<MapTabViewModel>(e, MapPaneView.TabFormat) is { } tab) vm.MoveTab(tab, vm.RightPane);
            e.Handled = true;
        });
    }

    private MapToolViewModel? Vm => DataContext as MapToolViewModel;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Vm is not { } vm) return;

        _handlers.Add(vm.WhenAnyValue(x => x.IsSplit).Subscribe(split =>
            Sides.ColumnDefinitions[1].Width = split ? new GridLength(1, GridUnitType.Star) : new GridLength(0)));

        // Live only while on screen: the main window swaps tool views in and out.
        vm.SetOnScreen(true);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Vm?.SetOnScreen(false);
        foreach (var h in _handlers) h.Dispose();
        _handlers.Clear();
    }
}
