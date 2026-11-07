using Avalonia.Controls;
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
        if (Column is { } column && sender is Flyout { Content: ScrollViewer { Content: ItemsControl list } })
            list.ScrollIntoView(column.Rows.Select((row, at) => (row, at)).FirstOrDefault(r => r.row.IsMe).at);
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

    private void OnSoundOpening(object? sender, EventArgs e) => Column?.Owner.LoadDevices();
}
