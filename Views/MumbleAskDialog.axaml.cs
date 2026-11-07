using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace EveConsole.Views;

/// <summary>Asks for a line of text, saying what it is for, and offering what it may be if there is a list. Null if cancelled.</summary>
public partial class MumbleAskDialog : Window
{
    public MumbleAskDialog() : this("", "", "", "", null) { }

    public MumbleAskDialog(string title, string message, string label, string hint, IEnumerable<string>? choices)
    {
        InitializeComponent();

        Title = title;
        MessageText.Text = message;
        MessageText.IsVisible = message.Length > 0;
        LabelText.Text = label;
        Entry.Watermark = hint;
        Entry.ItemsSource = choices;

        // ⚠️ Handled ones too: the box takes Enter and Escape for its own list. Only these two make it a one-key dialog.
        Entry.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter) Close(Typed());
            else if (e.Key == Key.Escape) Close(null);
        }, RoutingStrategies.Bubble, handledEventsToo: true);

        Opened += (_, _) => Entry.Focus();
    }

    private string Typed() => Entry.Text?.Trim() ?? "";

    private void OnOk(object? sender, RoutedEventArgs e) => Close(Typed());
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
