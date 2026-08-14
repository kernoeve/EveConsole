using Avalonia.Controls;
using Avalonia.Input;
using EveConsole.Services;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class SystemPageView : UserControl
{
    public SystemPageView() => InitializeComponent();

    /// <summary>
    /// Opens the double-clicked kill in the Killmail Browser.
    ///
    /// <para>Code-behind rather than a command because the gesture belongs to the row as a whole,
    /// and the row's own DataContext is the killmail — there is nothing for a binding to route
    /// through that a handler does not already have.</para>
    /// </summary>
    private void OnKillRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: KillmailListRowVm row })
            EntityNavigator.Instance.Killmail(row.KillMailId);
    }
}
