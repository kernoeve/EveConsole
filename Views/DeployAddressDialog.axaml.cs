using Avalonia.Controls;
using Avalonia.Interactivity;
using EveConsole.Services.WebStore;
using EveConsole.ViewModels;

namespace EveConsole.Views;

/// <summary>
/// The address a store's site gets, asked before its first deploy: the free workers.dev one,
/// with the account's workers.dev name to claim when the account has none yet, or a domain of
/// the owner's own on their Cloudflare account.
///
/// <para>Closes with null when cancelled, else a <see cref="DeployAddressChoice"/>: a hostname,
/// or "" for workers.dev together with the name the account should get (""  when it has one).</para>
/// </summary>
public partial class DeployAddressDialog : Window
{
    private readonly DeployAddressPrompt _prompt;

    public DeployAddressDialog(DeployAddressPrompt prompt)
    {
        InitializeComponent();
        _prompt = prompt;

        if (prompt.AccountSubdomain is null)
        {
            SubdomainPanel.IsVisible = true;
            SubBox.Text = prompt.SuggestedSubdomain;
            SubBox.TextChanged += (_, _) => RefreshPreview();
        }
        RefreshPreview();

        if (prompt.CurrentHostname.Length > 0)
        {
            DomainOption.IsChecked = true;
            HostBox.Text = prompt.CurrentHostname;
        }
        DomainOption.IsCheckedChanged += (_, _) =>
        {
            HostBox.IsEnabled = DomainOption.IsChecked == true;
            if (HostBox.IsEnabled) HostBox.Focus();
        };
        HostBox.IsEnabled = DomainOption.IsChecked == true;
    }

    private void RefreshPreview()
    {
        var sub = _prompt.AccountSubdomain ?? CloudflareDeployService.Slug(SubBox.Text ?? "", 63, "<name>");
        FreePreview.Text = $"https://{_prompt.WorkerName}.{sub}.workers.dev";
    }

    private void OnContinue(object? sender, RoutedEventArgs e)
    {
        if (DomainOption.IsChecked != true)
        {
            if (_prompt.AccountSubdomain is not null) { Close(new DeployAddressChoice("", "")); return; }
            var sub = CloudflareDeployService.Slug(SubBox.Text ?? "", 63, "");
            if (!CloudflareDeployService.IsValidWorkerName(sub))
            {
                Problem.Text      = "The workers.dev name needs lower-case letters, digits and hyphens, up to 63 of them.";
                Problem.IsVisible = true;
                return;
            }
            Close(new DeployAddressChoice("", sub));
            return;
        }

        var host = CloudflareDeployService.CleanHostname(HostBox.Text ?? "");
        if (host is null)
        {
            Problem.Text      = "That is not a hostname: something like store.example.com, letters, digits, hyphens and dots only.";
            Problem.IsVisible = true;
            return;
        }
        Close(new DeployAddressChoice(host, ""));
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
