using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using EveConsole.ViewModels;

namespace EveConsole.Views;

/// <summary>One connected server's controls, for its <see cref="MumbleOverlayColumn"/>. The popups go at a click anywhere else, as a flyout does.</summary>
public partial class MumbleServerBar : UserControl
{
    public MumbleServerBar()
    {
        InitializeComponent();

        // ⚠️ Seen even when handled: a channel's row is a button, which takes the press for itself.
        Tree.AddHandler(PointerPressedEvent, OnTreePressed, RoutingStrategies.Bubble, handledEventsToo: true);
        Tree.AddHandler(PointerMovedEvent, OnTreeMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        Tree.AddHandler(PointerReleasedEvent, OnTreeReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        // Only the tree's own hold ending: taking it over from the row pressed raises this too, for the row.
        Tree.AddHandler(PointerCaptureLostEvent, (_, e) => { if (ReferenceEquals(e.Source, Tree)) EndDrag(null); }, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>Shows where this client is on the server, for the toolbar, rather than its name lit as it talks.</summary>
    public bool ShowsWhere
    {
        get => Where.IsVisible;
        set
        {
            Where.IsVisible = value;
            Who.IsVisible = !value;
        }
    }

    private Flyout? _openTree;

    private MumbleOverlayColumn? Column => DataContext as MumbleOverlayColumn;

    private void OnTreeOpening(object? sender, EventArgs e)
    {
        if (Column is not { } column) return;

        _openTree = (Flyout)sender!;
        column.TreeOpened();
        column.Owner.FillTree(column);
    }

    /// <summary>Where this client is, on a server with more channels than fit.</summary>
    private void OnTreeOpened(object? sender, EventArgs e)
    {
        if (Column is { } column)
            Tree.ScrollIntoView(column.Rows.Select((row, at) => (row, at)).FirstOrDefault(r => r.row.IsHere || r.row.IsMe).at);
    }

    private void OnTreeClosed(object? sender, EventArgs e)
    {
        _openTree = null;
        if (Column is { } column && column.TreeClosed()) column.Rows.Clear();
    }

    private void OnToggleChannel(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: MumbleRowVm row }) Column?.Owner.ToggleChannel(row);
    }

    private void OnChannelClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: MumbleRowVm row } || Column is not { } column) return;

        _ = column.Owner.JoinFromOverlayAsync(column, row.Id);
        _openTree?.Hide();
    }

    private MumbleRowMenu? _rowMenu;

    /// <summary>What can be done to what was right-clicked. ⚠️ Attached only while it shows: one menu, for hundreds of rows that come and go.</summary>
    private void OnRowMenu(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control { DataContext: MumbleRowVm row } at || Column is not { } column) return;

        e.Handled = true;
        column.Owner.SelectedRow = row;

        _rowMenu ??= new MumbleRowMenu { HideTree = () => _openTree?.Hide(), ShowTree = () => TreeButton.Flyout?.ShowAt(TreeButton) };
        _rowMenu.DataContext = column.Owner;
        _rowMenu.Host = TopLevel.GetTopLevel(this) as Window;

        at.ContextMenu = _rowMenu;
        _rowMenu.Closed += Detach;
        _rowMenu.Open(at);

        void Detach(object? s, RoutedEventArgs a)
        {
            _rowMenu.Closed -= Detach;
            at.ContextMenu = null;
        }
    }

    private void OnSoundOpening(object? sender, EventArgs e) => Column?.Owner.LoadDevices();

    // ── Dragging someone to a channel ────────────────────────────────────────────

    /// <summary>How far the pointer goes with the button down before it is a drag rather than a click.</summary>
    private const double DragAfter = 6;

    private MumbleRowVm? _dragging, _dropOn;
    private Point _pressedAt;
    private bool _dragStarted;

    private void OnTreePressed(object? sender, PointerPressedEventArgs e)
    {
        _dragging = null;
        _dragStarted = false;
        if (!e.GetCurrentPoint(Tree).Properties.IsLeftButtonPressed || RowOf(e.Source as Visual) is not { IsUser: true } row) return;

        _dragging = row;
        _pressedAt = e.GetPosition(Tree);
    }

    private void OnTreeMoved(object? sender, PointerEventArgs e)
    {
        if (_dragging is null) return;

        var at = e.GetPosition(Tree);
        if (!_dragStarted)
        {
            if (Math.Abs(at.X - _pressedAt.X) < DragAfter && Math.Abs(at.Y - _pressedAt.Y) < DragAfter) return;
            _dragStarted = true;

            // Held by the tree from here, so letting go over a channel doesn't also click it and join.
            e.Pointer.Capture(Tree);
            Tree.Cursor = new Cursor(StandardCursorType.DragMove);
        }

        DropOn(ChannelAt(at));
    }

    private async void OnTreeReleased(object? sender, PointerReleasedEventArgs e)
    {
        var person = _dragging;
        var to = _dropOn;
        var dragged = _dragStarted;
        EndDrag(e.Pointer);

        // Clicked rather than dragged: selected, for the selection key.
        if (!dragged && person is not null && Column is { } picking)
        {
            picking.Owner.SelectedRow = person;
            return;
        }

        if (!dragged || person is null || to is null || Column is not { } column) return;
        e.Handled = true;
        await column.Owner.DropPersonAsync(person, to.Id);
    }

    private void EndDrag(IPointer? pointer)
    {
        DropOn(null);
        var held = _dragStarted;
        _dragging = null;
        _dragStarted = false;
        Tree.Cursor = null;
        if (held && pointer?.Captured == Tree) pointer.Capture(null);
    }

    private void DropOn(MumbleRowVm? channel)
    {
        if (ReferenceEquals(channel, _dropOn)) return;
        if (_dropOn is { } was) was.IsDropTarget = false;
        _dropOn = channel;
        if (channel is not null) channel.IsDropTarget = true;
    }

    /// <summary>The channel under a point of the tree. Over a person, the channel they are in, as in the standard client.</summary>
    private MumbleRowVm? ChannelAt(Point at)
    {
        // By the rows' own places rather than what is drawn there: the gap between two rows counts as neither.
        var row = Tree.GetRealizedContainers()
            .Where(c => c.TranslatePoint(default, Tree) is { } topLeft && new Rect(topLeft, c.Bounds.Size).Contains(at))
            .Select(c => c.DataContext).OfType<MumbleRowVm>().FirstOrDefault();
        if (row is null || Column is not { } column) return null;
        if (row.IsChannel) return row;
        if (!row.IsUser) return null;

        // A person's channel is the first one above them that is less indented.
        var rows = column.Rows;
        for (var i = rows.IndexOf(row) - 1; i >= 0; i--)
            if (rows[i] is { IsChannel: true } channel && channel.Depth < row.Depth) return channel;
        return null;
    }

    private static MumbleRowVm? RowOf(Visual? visual) =>
        visual?.GetSelfAndVisualAncestors().OfType<Control>().Select(c => c.DataContext).OfType<MumbleRowVm>().FirstOrDefault();
}
