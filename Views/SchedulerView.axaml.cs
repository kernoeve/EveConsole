using System.Threading.Tasks;
using Avalonia.Controls;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class SchedulerView : UserControl
{
    public SchedulerView()
    {
        InitializeComponent();

        // The view model asks; the view is the only thing holding a window to hang a dialog off.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is SchedulerViewModel vm)
                vm.ConfirmDiscard = AskAsync;
        };
    }

    private async Task<bool> AskAsync(string message)
    {
        // ⚠️ No window means no dialog, and refusing on that basis would trap the user in an
        // editor they cannot leave. Let it through instead.
        if (TopLevel.GetTopLevel(this) is not Window owner) return true;

        return await new ConfirmDialog(message).ShowDialog<bool>(owner);
    }
}
