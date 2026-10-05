using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using EveConsole.Localization;

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
            if (GetTopLevel(this)?.Clipboard is { } clip && await clip.TryGetTextAsync() is { } text
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
            ErrorText.Text      = FittingText.EftErrStart;
            ErrorText.IsVisible = true;
            return;
        }
        Close(text);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
