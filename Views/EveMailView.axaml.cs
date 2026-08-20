using Avalonia.Controls;
using EveConsole.Services;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class EveMailView : UserControl
{
    public EveMailView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is not EveMailViewModel vm) return;

        vm.ShowComposeDialog = async args =>
        {
            var svc    = GetSvc();
            var dialog = new ComposeMailDialog(args, svc);
            return await dialog.ShowDialog<ComposeMailResult?>(GetWindow());
        };

        _ = vm.LoadMailsAsync();
    }

    private EveMailService GetSvc()
    {
        var win = TopLevel.GetTopLevel(this) as Window;
        if (win?.DataContext is MainWindowViewModel mwvm) return mwvm.MailSvc;
        if (win is EveMailWindow eww)                    return eww.MailSvc;
        throw new InvalidOperationException("EveMailService not available.");
    }


    // The From line reads the page's own view model; each recipient carries its own link.
    private void OnOpenMailFrom(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => (DataContext as EveMailViewModel)?.SelectedMail?.OpenFrom();

    private void OnOpenRecipient(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => ((sender as Control)?.DataContext as EveMailPartyVm)?.Open();
    private Window GetWindow() => (TopLevel.GetTopLevel(this) as Window)!;
}
