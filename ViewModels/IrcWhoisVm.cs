using System.Collections.ObjectModel;
using System.Globalization;
using EveConsole.Localization;
using EveConsole.Services.Irc;
using ReactiveUI;

namespace EveConsole.ViewModels;

public sealed record IrcWhoisRow(string Label, string Value);

/// <summary>Who someone on IRC is, as the server says (WHOIS): asked once when the window opens.</summary>
public sealed class IrcWhoisVm : ReactiveObject
{
    private bool _isAsking = true;
    private string _message = "";

    public IrcWhoisVm(string nick) => Title = string.Format(CultureInfo.CurrentCulture, IrcText.WhoisTitle, nick);

    public string Title { get; }
    public ObservableCollection<IrcWhoisRow> Rows { get; } = [];

    public bool IsAsking { get => _isAsking; private set => this.RaiseAndSetIfChanged(ref _isAsking, value); }

    /// <summary>Why there is nothing to show: not online, no answer, or what went wrong. Blank when there are rows.</summary>
    public string Message { get => _message; private set { this.RaiseAndSetIfChanged(ref _message, value); this.RaisePropertyChanged(nameof(HasMessage)); } }
    public bool HasMessage => Message.Length > 0;

    /// <summary>Asks, and shows what came back. Call on the UI thread.</summary>
    public async Task LoadAsync(string nick, Func<Task<IrcWhois?>> ask)
    {
        try
        {
            var answer = await Task.Run(ask);
            if (answer is null) Message = IrcText.WhoisNoAnswer;
            else if (!answer.Found) Message = string.Format(CultureInfo.CurrentCulture, IrcText.WhoisNotOnline, nick);
            else foreach (var (label, value) in IrcService.WhoisRows(answer)) Rows.Add(new IrcWhoisRow(label, value));
        }
        catch (Exception e) when (e is not OperationCanceledException) { Message = e.Message; }
        finally { IsAsking = false; }
    }
}
