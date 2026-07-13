using Avalonia.Controls;
using Avalonia.ReactiveUI;
using EveCortex.ViewModels;

namespace EveCortex.Views;

public partial class ErrorLogView : ReactiveUserControl<ErrorLogViewModel>
{
    public ErrorLogView()
    {
        InitializeComponent();
    }
}
