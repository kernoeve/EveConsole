using Avalonia.Controls;
using Avalonia.Interactivity;
using EveConsole.Services.WebStore;

namespace EveConsole.Views;

/// <summary>
/// The address a store's site gets, asked once before its first deploy: the free workers.dev
/// one, or a domain of the owner's own on their Cloudflare account.
///
/// <para>Closes with null when cancelled, "" for workers.dev, else the hostname — the same
/// value the Config tab's address field holds, so the two cannot disagree.</para>
/// </summary>
public partial class DeployAddressDialog : Window
{
    public DeployAddressDialog(string freePreview, string currentHostname)
    {
        InitializeComponent();
        FreePreview.Text = freePreview;
        if (currentHostname.Length > 0)
        {
            DomainOption.IsChecked = true;
            HostBox.Text = currentHostname;
        }
        DomainOption.IsCheckedChanged += (_, _) =>
        {
            HostBox.IsEnabled = DomainOption.IsChecked == true;
            if (HostBox.IsEnabled) HostBox.Focus();
        };
        HostBox.IsEnabled = DomainOption.IsChecked == true;
    }

    private void OnContinue(object? sender, RoutedEventArgs e)
    {
        if (DomainOption.IsChecked != true) { Close(""); return; }

        var host = CloudflareDeployService.CleanHostname(HostBox.Text ?? "");
        if (host is null)
        {
            Problem.Text      = "That is not a hostname: something like store.example.com, letters, digits, hyphens and dots only.";
            Problem.IsVisible = true;
            return;
        }
        Close(host);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
