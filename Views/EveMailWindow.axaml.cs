using Avalonia.ReactiveUI;
using EveConsole.Services;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class EveMailWindow : ReactiveWindow<EveMailViewModel>
{
    // Exposed so EveMailView.GetSvc() can retrieve it when running detached.
    public EveMailService MailSvc { get; }

    public EveMailWindow(EveMailService svc)
    {
        MailSvc = svc;
        InitializeComponent();
    }
}
