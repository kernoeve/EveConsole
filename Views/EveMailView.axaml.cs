using Avalonia.Controls;
using EveCortex.Services;
using EveCortex.ViewModels;

namespace EveCortex.Views;

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

    private Window GetWindow() => (TopLevel.GetTopLevel(this) as Window)!;
}
