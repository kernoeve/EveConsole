using Avalonia;
using EveConsole.ViewModels;

namespace EveConsole.Views;

/// <summary>
/// An Intel control as one line: the character, the nearest live threat to them, and how many more
/// within the control's range. Clicked, the full list drops down under it in a popup, the same list
/// the control shows in its list mode, as the Worklist's single line does.
/// </summary>
public sealed class PanelIntelLine : PanelDropLine
{
    public const double DropWidth = 460, DropHeight = 420;

    private readonly PanelIntelVm _vm;

    public PanelIntelLine(PanelIntelVm vm)
        : base(new PanelIntelLineContent { DataContext = vm },
               new PanelIntelControl { DataContext = vm, ManagesLifetime = false }, DropWidth, DropHeight)
    {
        _vm = vm;
        DataContext = vm;
        Padding = new Thickness(6, 0);
    }

    protected override void Shown() => _vm.Start();

    protected override void Hidden() => _vm.Dispose();
}
