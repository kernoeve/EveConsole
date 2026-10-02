using Avalonia.Controls;
using Avalonia.Interactivity;
using EveConsole.Localization;
using EveConsole.ViewModels;

namespace EveConsole.Views;

/// <summary>Where to save a fit, and under what name. Closes with the choice, or null.</summary>
public partial class SaveFitDialog : Window
{
    public SaveFitDialog() => InitializeComponent();

    public SaveFitDialog(SaveRequest request) : this()
    {
        NameBox.Text        = request.Name;
        Targets.ItemsSource = request.Targets;
        Targets.SelectedItem = request.Suggested;
        Opened += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim() ?? "";
        if (name.Length == 0) { Show(FittingText.SaveErrNoName); return; }
        if (Targets.SelectedItem is not SaveTarget target) { Show(FittingText.SaveErrNoTarget); return; }
        if (!target.Enabled) { Show(target.Why ?? FittingText.SaveErrCannotSave); return; }
        Close(new SaveChoice(name, target));
    }

    private void Show(string message)
    {
        ErrorText.Text      = message;
        ErrorText.IsVisible = true;
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
