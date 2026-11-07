using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using EveConsole.Services.Jabber;

namespace EveConsole.Views;

/// <summary>A picture in the picker, with every code it goes by for its tip.</summary>
public sealed class EmotePickCell(EmotePickItem item)
{
    public EmotePickItem Item => item;
    public string Tip => item.Pack.Length > 0 ? $"{string.Join("  ", item.Codes)}\n{item.Pack}" : string.Join("  ", item.Codes);
}

public sealed record EmotePickRow(IReadOnlyList<EmotePickCell> Cells);

/// <summary>
/// Every emote a server's packs have, in the packs' own order, with a search over their codes. A moving one shows its
/// first frame until the pointer is on it, so a window of a thousand costs no more than a page of stills.
/// </summary>
public partial class EmotePicker : UserControl
{
    private const int PerRow = 9;

    private EmoteSet _set = EmoteSet.None;

    public EmotePicker()
    {
        InitializeComponent();
        SearchBox.TextChanged += (_, _) => Fill();
        SearchBox.AddHandler(KeyDownEvent, OnSearchKey, RoutingStrategies.Tunnel);
    }

    /// <summary>One was chosen.</summary>
    public event Action<EmoteRef>? Picked;

    /// <summary>Shows a server's emotes, from the top and with no search.</summary>
    public void Show(EmoteSet set)
    {
        _set = set;
        SearchBox.Text = "";
        Hovered.Text = "";
        Fill();
        SearchBox.Focus();
    }

    private void Fill()
    {
        var found = _set.Search(SearchBox.Text ?? "");
        var rows = new List<EmotePickRow>((found.Count + PerRow - 1) / PerRow);
        for (var i = 0; i < found.Count; i += PerRow)
            rows.Add(new EmotePickRow([.. found.Skip(i).Take(PerRow).Select(p => new EmotePickCell(p))]));

        RowList.ItemsSource = rows;
        Nothing.IsVisible = rows.Count == 0;
        if (rows.Count > 0) RowList.ScrollIntoView(rows[0]);
    }

    /// <summary>Enter takes the first found, so a search and Enter is all it needs.</summary>
    private void OnSearchKey(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (RowList.ItemsSource is List<EmotePickRow> { Count: > 0 } rows) Picked?.Invoke(rows[0].Cells[0].Item.Emote);
    }

    private void OnPick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: EmotePickCell cell }) Picked?.Invoke(cell.Item.Emote);
    }

    private void OnCellEnter(object? sender, PointerEventArgs e) => Hover(sender, true);
    private void OnCellLeave(object? sender, PointerEventArgs e) => Hover(sender, false);

    private void Hover(object? sender, bool on)
    {
        if (sender is not Control { DataContext: EmotePickCell cell } button) return;
        if (button.FindDescendantOfType<EmoteView>() is { } view) view.Still = !on;
        Hovered.Text = on ? cell.Tip.Replace('\n', ' ') : "";
    }
}
