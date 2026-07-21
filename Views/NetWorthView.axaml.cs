using Avalonia.Controls;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class NetWorthView : UserControl
{
    private bool _initialized;

    public NetWorthView()
    {
        InitializeComponent();
        DataContextChanged += async (_, _) =>
        {
            if (!_initialized && DataContext is NetWorthViewModel vm)
            {
                _initialized = true;
                await vm.InitializeAsync();
            }
        };
    }
}
