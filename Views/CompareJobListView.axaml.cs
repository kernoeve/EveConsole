using Avalonia.Controls;
using EveConsole.ViewModels;

namespace EveConsole.Views;

/// <summary>A Compare job list being edited. The decryptors are read the first time it shows.</summary>
public partial class CompareJobListView : UserControl
{
    public CompareJobListView()
    {
        InitializeComponent();
        // ⚠️ Both, as the Compare tab does: inside a tab or a template the content can attach
        // before its DataContext resolves.
        AttachedToVisualTree += async (_, _) => { if (DataContext is CompareJobListVm vm) await vm.LoadDecryptorsAsync(); };
        DataContextChanged   += async (_, _) => { if (DataContext is CompareJobListVm vm) await vm.LoadDecryptorsAsync(); };
    }
}
