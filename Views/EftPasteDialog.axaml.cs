using Avalonia.Controls;
using Avalonia.Interactivity;

namespace EveConsole.Views;

/// <summary>Takes a fit pasted as EFT text. Closes with the text, or null when cancelled.</summary>
public partial class EftPasteDialog : Window
{
    public EftPasteDialog()
    {
        InitializeComponent();
        Opened += async (_, _) =>
        {
            EftBox.Focus();
            // Most people have just copied the fit; offer it rather than make them paste.
            if (GetTopLevel(this)?.Clipboard is { } clip && await clip.GetTextAsync() is { } text
                && text.TrimStart().StartsWith('['))
            {
                EftBox.Text = text;
                EftBox.SelectAll();
            }
        };
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        var text = EftBox.Text ?? "";
        if (!text.TrimStart().StartsWith('['))
        {
            ErrorText.Text      = "An EFT fit starts with [Ship, Fit name].";
            ErrorText.IsVisible = true;
            return;
        }
        Close(text);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
