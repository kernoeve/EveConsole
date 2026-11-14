using Avalonia.Controls;
using EveConsole.ViewModels;

namespace EveConsole.Views;

/// <summary>The IRC tab of Settings.</summary>
public partial class IrcView : UserControl
{
    public IrcView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is IrcViewModel vm) vm.Confirm = AskAsync;
        };
    }

    private async Task<bool> AskAsync(string message)
    {
        // ⚠️ No window means nobody to ask, so nothing that cannot be undone goes ahead.
        if (TopLevel.GetTopLevel(this) is not Window owner) return false;
        return await new ConfirmDialog(message).ShowDialog<bool>(owner);
    }
}
