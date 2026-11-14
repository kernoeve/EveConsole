using Avalonia.Controls;

namespace EveConsole.Views;

/// <summary>
/// A system with hostiles in it, on one line: the panel's list, its single line and its popup. See
/// <see cref="ViewModels.IntelSystemLineVm"/>. Read only; nothing here reaches a game client.
/// </summary>
public partial class IntelSystemLine : UserControl
{
    public IntelSystemLine() => InitializeComponent();
}
