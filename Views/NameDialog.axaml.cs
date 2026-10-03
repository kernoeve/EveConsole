using Avalonia.Controls;
using Avalonia.Interactivity;
using EveConsole.Localization;

namespace EveConsole.Views;

public partial class NameDialog : Window
{
    public NameDialog(string title, string label, string? existingValue = null)
    {
        InitializeComponent();
        Title          = title;
        LabelText.Text = label;
        if (existingValue != null) NameBox.Text = existingValue;
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(name))
        {
            ErrorText.Text      = CommonText.NameRequired;
            ErrorText.IsVisible = true;
            return;
        }
        Close(name);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
