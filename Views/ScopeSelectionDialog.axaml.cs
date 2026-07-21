using Avalonia.Controls;
using Avalonia.Interactivity;

namespace EveConsole.Views;

public partial class ScopeSelectionDialog : Window
{
    public ScopeSelectionDialog(string authContext)
    {
        InitializeComponent();
        DialogTitle.Text = authContext == "corporation"
            ? "Add Corporation — Select Scopes"
            : "Add Character — Select Scopes";
    }

    private void OnContinue(object? sender, RoutedEventArgs e) => Close(true);
    private void OnCancel(object? sender, RoutedEventArgs e)   => Close(false);
}
