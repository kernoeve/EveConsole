using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using EveConsole.Localization;
using EveConsole.Services;

namespace EveConsole.Views;

public sealed record ReactionHeading(string Text);
public sealed record ReactionCell(string Emoji, string Name);
public sealed record ReactionRow(IReadOnlyList<ReactionCell> Cells);

/// <summary>
/// The standard emojis to react with: the common ones first, then every one by group, or those whose name holds what is
/// typed. Reactions are Unicode emojis by the protocol, so every client can show them; a pack's emotes cannot be one.
/// </summary>
public partial class ReactionPicker : UserControl
{
    private const int PerRow = 9;

    public ReactionPicker()
    {
        InitializeComponent();
        QuickRow.ItemsSource = EmojiCatalog.Quick;
        SearchBox.TextChanged += (_, _) => Fill();
        SearchBox.AddHandler(KeyDownEvent, OnSearchKey, RoutingStrategies.Tunnel);
        Fill();
    }

    /// <summary>One was chosen.</summary>
    public event Action<string>? Picked;

    /// <summary>From the top, with nothing typed.</summary>
    public void Reset()
    {
        SearchBox.Text = "";
        Fill();
        SearchBox.Focus();
    }

    private void Fill()
    {
        var words = SearchBox.Text?.Trim() ?? "";
        var rows = new List<object>();

        if (words.Length == 0)
            foreach (var group in EmojiCatalog.Groups)
            {
                rows.Add(new ReactionHeading(GroupName(group.Key)));
                AddRows(rows, group.Emojis);
            }
        else AddRows(rows, EmojiCatalog.Search(words));

        RowList.ItemsSource = rows;
    }

    private static void AddRows(List<object> rows, IReadOnlyList<EmojiEntry> emojis)
    {
        for (var i = 0; i < emojis.Count; i += PerRow)
            rows.Add(new ReactionRow([.. emojis.Skip(i).Take(PerRow).Select(e => new ReactionCell(e.Emoji, e.Name))]));
    }

    public static string GroupName(string key) => key switch
    {
        "smileys"    => JabberText.EmojiSmileys,
        "people"     => JabberText.EmojiPeople,
        "hearts"     => JabberText.EmojiHearts,
        "nature"     => JabberText.EmojiNature,
        "food"       => JabberText.EmojiFood,
        "activities" => JabberText.EmojiActivities,
        "travel"     => JabberText.EmojiTravel,
        "objects"    => JabberText.EmojiObjects,
        _            => JabberText.EmojiSymbols,
    };

    /// <summary>Enter takes the first found.</summary>
    private void OnSearchKey(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (RowList.ItemsSource is List<object> rows && rows.OfType<ReactionRow>().FirstOrDefault() is { Cells.Count: > 0 } first)
            Picked?.Invoke(first.Cells[0].Emoji);
    }

    private void OnPick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Content: string emoji }) Picked?.Invoke(emoji);
    }
}
