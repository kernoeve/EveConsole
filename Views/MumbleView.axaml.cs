using Avalonia.Controls;
using EveConsole.ViewModels;

namespace EveConsole.Views;

/// <summary>The Mumble tool.</summary>
public partial class MumbleView : UserControl
{
    public MumbleView()
    {
        InitializeComponent();
        KeyCaptureHook.Attach(this);

        DataContextChanged += (_, _) =>
        {
            if (DataContext is MumbleViewModel vm) vm.Confirm = AskAsync;
        };
    }

    private async Task<bool> AskAsync(string message)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return false;
        return await new ConfirmDialog(message).ShowDialog<bool>(owner);
    }
}
