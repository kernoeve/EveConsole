using Avalonia.Controls;
using Avalonia.Input;

namespace EveConsole.Views;

/// <summary>
/// Asks for one line of text. Returns what was typed, or null if cancelled.
///
/// <para>Deliberately trivial: the app had a yes/no dialog and nothing for "type something",
/// which is the whole interaction behind creating a label.</para>
/// </summary>
public partial class TextPromptDialog : Window
{
    public TextPromptDialog() : this("", "", "") { }

    public TextPromptDialog(string title, string label, string watermark, string initial = "")
    {
        InitializeComponent();

        Title           = title;
        LabelText.Text  = label.ToUpperInvariant();
        Entry.Watermark = watermark;
        Entry.Text      = initial;

        OkButton.Click     += (_, _) => Close(Entry.Text?.Trim());
        CancelButton.Click += (_, _) => Close(null);

        // Enter accepts, Escape cancels. A one-field dialog that needs the mouse to dismiss is a
        // dialog that interrupts more than it asks.
        Entry.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)       Close(Entry.Text?.Trim());
            else if (e.Key == Key.Escape) Close(null);
        };

        Opened += (_, _) => Entry.Focus();
    }
}
