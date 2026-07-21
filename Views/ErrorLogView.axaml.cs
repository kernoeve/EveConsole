using Avalonia.Controls;
using Avalonia.ReactiveUI;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class ErrorLogView : ReactiveUserControl<ErrorLogViewModel>
{
    public ErrorLogView()
    {
        InitializeComponent();
    }
}
