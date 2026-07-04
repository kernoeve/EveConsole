using Avalonia.ReactiveUI;
using EveCortex.Services;
using EveCortex.ViewModels;

namespace EveCortex.Views;

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
