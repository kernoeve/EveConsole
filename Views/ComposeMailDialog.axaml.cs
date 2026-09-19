using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using EveConsole.Models;
using EveConsole.Services;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class ComposeMailDialog : Window
{
    private readonly EveMailService _svc;
    private readonly ObservableCollection<EveMailResolvedRecipient> _recipients = [];
    private long _fromCharId;

    public ComposeMailDialog(ComposeMailArgs args, EveMailService svc)
    {
        InitializeComponent();
        _svc        = svc;
        _fromCharId = args.FromCharId;

        // Populate character dropdown
        FromCombo.ItemsSource   = args.Characters;
        FromCombo.DisplayMemberBinding = new Avalonia.Data.Binding("Name");
        var selected = args.Characters.FirstOrDefault(c => c.Id == args.FromCharId)
                    ?? args.Characters.FirstOrDefault();
        if (selected is not null)
        {
            FromCombo.SelectedItem = selected;
            _fromCharId = selected.Id;
        }

        SubjectBox.Text = args.InitialSubject;
        BodyBox.Text    = args.InitialBody;
        RecipientList.ItemsSource = _recipients;

        // A reply already knows who it is to; no search, no round trip.
        foreach (var r in args.InitialRecipients)
            if (_recipients.All(x => x.Id != r.Id)) _recipients.Add(r);

        if (!string.IsNullOrEmpty(args.InitialTo))
            RecipientSearchBox.Text = args.InitialTo;

        // A reply opens with the quote already in the body and the caret on the first — blank —
        // line, so typing starts above the quote. Deferred to Opened: focus and caret set before
        // the window is shown are reset when it is.
        if (args.StartInBody)
            Opened += (_, _) =>
            {
                BodyBox.Focus();
                BodyBox.CaretIndex = 0;
            };
    }

    private void OnFromCharacterChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (FromCombo.SelectedItem is Character c)
            _fromCharId = c.Id;
    }

    private async void OnAddRecipientClick(object? sender, RoutedEventArgs e)
        => await TryAddRecipientAsync();

    private async void OnRecipientKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return || e.Key == Key.Enter)
            await TryAddRecipientAsync();
    }

    private async Task TryAddRecipientAsync()
    {
        var name = RecipientSearchBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(name)) return;

        RecipientSearchStatus.IsVisible = false;
        StatusLabel.Text = "Searching…";

        var results = await _svc.ResolveRecipientAsync(_fromCharId, name);

        if (results.Count == 0)
        {
            RecipientSearchStatus.Text      = $"No character found matching \"{name}\".";
            RecipientSearchStatus.IsVisible = true;
            StatusLabel.Text = "";
            return;
        }

        var match = results[0];
        if (_recipients.All(r => r.Id != match.Id))
            _recipients.Add(match);

        RecipientSearchBox.Text = "";
        StatusLabel.Text = "";
    }

    private void OnRemoveRecipientClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is EveMailResolvedRecipient r)
            _recipients.Remove(r);
    }

    private void OnSendClick(object? sender, RoutedEventArgs e)
    {
        var subject = SubjectBox.Text?.Trim() ?? "";
        var body    = BodyBox.Text?.Trim()    ?? "";

        if (string.IsNullOrEmpty(subject)) { StatusLabel.Text = "Subject is required."; return; }
        if (string.IsNullOrEmpty(body))    { StatusLabel.Text = "Body is required."; return; }
        if (_recipients.Count == 0)        { StatusLabel.Text = "Add at least one recipient."; return; }

        Close(new ComposeMailResult
        {
            FromCharId = _fromCharId,
            Subject    = subject,
            Body       = body,
            Recipients = _recipients.Select(r => new EsiMailRecipientItem(r.Id, r.Type)).ToList(),
        });
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
