using Avalonia.Controls;
using Avalonia.Interactivity;
using EveConsole.Localization;

namespace EveConsole.Views;

public partial class ScopeSelectionDialog : Window
{
    public ScopeSelectionDialog(string authContext)
    {
        InitializeComponent();
        DialogTitle.Text = authContext == "corporation"
            ? SettingsText.ScopesTitleCorporation
            : SettingsText.ScopesTitleCharacter;
    }

    private void OnContinue(object? sender, RoutedEventArgs e) => Close(true);
    private void OnCancel(object? sender, RoutedEventArgs e)   => Close(false);
}
