using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using EveConsole.ViewModels;

namespace EveConsole.Views;

/// <summary>The Jabber tool.</summary>
public partial class JabberView : UserControl
{
    public JabberView()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is JabberViewModel vm) vm.Confirm = AskAsync;
        };

        StatusBox.LostFocus += (_, _) => (DataContext as JabberViewModel)?.ApplyStatusMessage();
        StatusBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) (DataContext as JabberViewModel)?.ApplyStatusMessage(); };
    }

    private void OnMessagePerson(object? sender, RoutedEventArgs e) => (DataContext as JabberViewModel)?.MessageSelectedPerson();

    private void OnRowDoubleTapped(object? sender, TappedEventArgs e) => (DataContext as JabberViewModel)?.MessageSelectedPerson();

    private async Task<bool> AskAsync(string message)
    {
        // ⚠️ No window means nobody to ask, so nothing that cannot be undone goes ahead.
        if (TopLevel.GetTopLevel(this) is not Window owner) return false;
        return await new ConfirmDialog(message).ShowDialog<bool>(owner);
    }
}
