using Avalonia.Controls;
using EveConsole.Controls;
using EveConsole.Localization;
using EveConsole.Services;

namespace EveConsole.Views;

public partial class UniverseView : UserControl
{
    public UniverseView() => InitializeComponent();

    /// <summary>
    /// A right-click on a system: put it on the route avoid list, or take it off — whichever it
    /// is not. Regions and empty map have nothing to offer.
    /// </summary>
    private void OnMapContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not MapCanvas canvas || !e.TryGetPosition(canvas, out var at)) return;
        if (canvas.NodeAt(at) is not { IsRegion: false, IsWormhole: false } node) return;

        var avoided = RouteAvoidList.Contains(node.Id);
        var item = new MenuItem
        {
            Header = string.Format(avoided ? MapText.AvoidRemove : MapText.AvoidAdd, node.Label),
        };
        item.Click += (_, _) => RouteAvoidList.Toggle(node.Id);
        new ContextMenu { ItemsSource = new[] { item } }.Open(canvas);
        e.Handled = true;
    }
}
