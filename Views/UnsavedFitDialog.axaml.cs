using Avalonia.Controls;
using Avalonia.Interactivity;
using EveConsole.ViewModels;

namespace EveConsole.Views;

/// <summary>Save, don't save, or stay: asked before a changed fit is replaced.</summary>
public partial class UnsavedFitDialog : Window
{
    public UnsavedFitDialog() => InitializeComponent();
    public UnsavedFitDialog(string message) : this() => MessageText.Text = message;

    private void OnSave(object? sender, RoutedEventArgs e)    => Close(UnsavedChoice.Save);
    private void OnDiscard(object? sender, RoutedEventArgs e) => Close(UnsavedChoice.Discard);
    private void OnCancel(object? sender, RoutedEventArgs e)  => Close(UnsavedChoice.Cancel);
}
