using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using EveConsole.Localization;
using EveConsole.ViewModels;
using ReactiveUI;

namespace EveConsole.Views;

/// <summary>
/// One side of a window's tabs: a tab strip and the tool showing. The dragging itself is the main
/// window's (<see cref="MainWindow"/>), because a tab can go to the other side, to another window,
/// or out to a window of its own; this reports presses and answers where things are.
/// </summary>
public partial class ToolPaneView : UserControl
{
    public ToolPaneView()
    {
        InitializeComponent();
        // A click anywhere on a side makes it the one being worked in, in its window.
        AddHandler(PointerPressedEvent, (_, _) =>
        {
            if (DataContext is ToolPane { Tabs.Count: > 0, Workspace: { } ws } pane) ws.ActivePane = pane;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        // The tool on this side. ⚠️ Not a binding: a view made from a data template is kept for the
        // next view model of the same kind, only its DataContext changed, and a tool's view isn't
        // made to be handed another copy of the tool. Emptying it first makes a new one.
        this.GetObservable(DataContextProperty)
            .Select(dc => dc is ToolPane pane ? pane.WhenAnyValue(p => p.SelectedTab).Select(t => t?.ViewModel) : Observable.Return<object?>(null))
            .Switch()
            .DistinctUntilChanged()
            .Subscribe(vm =>
            {
                PaneContent.Content = null;
                PaneContent.Content = vm;
            });
    }

    /// <summary>The window that runs tab dragging — the main one, which knows every window.</summary>
    private static MainWindow? Owner => MainWindow.Current;

    internal ToolPane? Pane => DataContext as ToolPane;

    /// <summary>The tool showing on this side — what a screenshot of the tab captures.</summary>
    internal Control ContentArea => PaneContent;

    private void OnTabPointerPressed(object? sender, PointerPressedEventArgs e)  => Owner?.OnTabPointerPressed(sender, e);
    private void OnTabPointerMoved(object? sender, PointerEventArgs e)           => Owner?.OnTabPointerMoved(sender, e);
    private void OnTabPointerReleased(object? sender, PointerReleasedEventArgs e) => Owner?.OnTabPointerReleased(sender, e);

    // ── The tab's right-click menu ──
    // The window's view model is the main one in every window (a window of tabs keeps it, for
    // closing), and the menu's items carry the tab they were opened on.

    private MainWindowViewModel? Vm => TopLevel.GetTopLevel(this)?.DataContext as MainWindowViewModel;

    private static ToolTab? TabOf(object? sender) => (sender as Control)?.DataContext as ToolTab;

    /// <summary>Just before the menu opens: says "on this side" when the window is split, and greys
    /// out what would close nothing.</summary>
    private void OnTabContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control { ContextMenu: { } menu } || Pane is not { } pane || TabOf(sender) is not { } tab) return;
        var split = pane.Workspace?.IsSplit == true;
        foreach (var item in menu.Items.OfType<MenuItem>())
            switch (item.Tag as string)
            {
                case "others":
                    item.Header    = split ? ShellText.MenuCloseOtherTabsSide : ShellText.MenuCloseOtherTabs;
                    item.IsEnabled = pane.Tabs.Any(t => t.CanClose && t != tab);
                    break;
                case "all":
                    item.Header    = split ? ShellText.MenuCloseAllTabsSide : ShellText.MenuCloseAllTabs;
                    item.IsEnabled = pane.Tabs.Any(t => t.CanClose);
                    break;
            }
    }

    private void OnCloseTab(object? sender, RoutedEventArgs e)
    {
        if (TabOf(sender) is { } tab) Vm?.CloseTab(tab);
    }

    private void OnCloseOtherTabs(object? sender, RoutedEventArgs e)
    {
        if (TabOf(sender) is { } tab) Vm?.CloseTabsBeside(tab, keepIt: true);
    }

    private void OnCloseAllTabs(object? sender, RoutedEventArgs e)
    {
        if (TabOf(sender) is { } tab) Vm?.CloseTabsBeside(tab, keepIt: false);
    }

    /// <summary><paramref name="control"/>'s bounds in <paramref name="relativeTo"/>'s coordinates.</summary>
    private static Rect BoundsIn(Control control, Visual relativeTo) =>
        control.TranslatePoint(default, relativeTo) is { } at ? new Rect(at, control.Bounds.Size) : default;

    /// <summary>Keeps the tabs clear of something at the strip's right end.</summary>
    internal void ReserveStripRight(double width) => Strip.Padding = new Thickness(0, 0, width, 0);

    internal Rect StripBounds(Visual relativeTo)   => BoundsIn(Strip, relativeTo);
    internal Rect ContentBounds(Visual relativeTo) => BoundsIn(PaneContent, relativeTo);

    /// <summary>
    /// Where along this side's tabs one dropped at <paramref name="at"/> (in <paramref name="relativeTo"/>'s
    /// coordinates) would go. The tabs wrap onto more rows, so a tab is passed once the point is
    /// below its row, or level with it and past its middle.
    /// </summary>
    internal int InsertIndexAt(Point at, Visual relativeTo)
    {
        var count = Pane?.Tabs.Count ?? 0;
        for (var i = 0; i < count; i++)
        {
            if (TabStrip.ContainerFromIndex(i) is not { } c) continue;
            var r = BoundsIn(c, relativeTo);
            if (at.Y < r.Top || (at.Y < r.Bottom && at.X < r.Center.X)) return i;
        }
        return count;
    }

    /// <summary>Marks where a dragged tab would land: before tab <paramref name="index"/>, or after the last.</summary>
    internal void ShowDropMark(int index)
    {
        var count = Pane?.Tabs.Count ?? 0;
        var anchor = index < count ? TabStrip.ContainerFromIndex(index) : count > 0 ? TabStrip.ContainerFromIndex(count - 1) : null;
        var r      = anchor is null ? default : BoundsIn(anchor, Strip);
        if (r == default)
        {
            Canvas.SetLeft(DropMark, 2); Canvas.SetTop(DropMark, 0); DropMark.Height = Math.Max(20, Strip.Bounds.Height);
        }
        else
        {
            Canvas.SetLeft(DropMark, Math.Max(0, (index < count ? r.Left : r.Right) - 1));
            Canvas.SetTop(DropMark, r.Top);
            DropMark.Height = r.Height;
        }
        DropMark.IsVisible = true;
    }

    internal void HideDropMark() => DropMark.IsVisible = false;
}
