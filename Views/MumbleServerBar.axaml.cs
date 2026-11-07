using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using EveConsole.ViewModels;

namespace EveConsole.Views;

/// <summary>One connected server's controls, for its <see cref="MumbleOverlayColumn"/>. The popups go at a click anywhere else, as a flyout does.</summary>
public partial class MumbleServerBar : UserControl
{
    public MumbleServerBar() => InitializeComponent();

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
}
