using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive;
using Avalonia.Threading;
using EveConsole.Localization;
using EveConsole.Models;
using EveConsole.Services.Irc;
using ReactiveUI;

namespace EveConsole.ViewModels;

public sealed record IrcLoginChoice(IrcLoginKind Kind, string Label);

/// <summary>
/// The IRC tab of Settings: every server down the left, and how each is set up on the right. Conversations are
/// elsewhere, as Jabber's are: channels in Group Chat, people in Messages.
/// </summary>
public sealed class IrcViewModel : ReactiveObject
{
    private readonly IrcService _service;
    private int _refreshQueued;

    public IrcViewModel(IrcService service)
    {
        _service = service;

        AddServerCommand = ReactiveCommand.Create(BeginAdd);
        EditServerCommand = ReactiveCommand.Create(BeginEdit);
        SaveServerCommand = ReactiveCommand.CreateFromTask(SaveAsync);
        CancelEditCommand = ReactiveCommand.Create(() => { IsEditing = false; RaisePanes(); });
        DeleteServerCommand = ReactiveCommand.CreateFromTask(DeleteAsync);
        ConnectCommand = ReactiveCommand.Create(() => { if (Selected() is { } s) _service.Connect(s); });
        DisconnectCommand = ReactiveCommand.CreateFromTask(async () => { if (Selected() is { } s) await _service.DisconnectAsync(s); });
        TrustCertificateCommand = ReactiveCommand.CreateFromTask(async () => { if (Selected() is { } s) await _service.TrustCertificateAsync(s); });
        SetAwayCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (Selected() is { } s) await _service.SetAwayAsync(s, AwayMessage.Trim().Length > 0 ? AwayMessage.Trim() : IrcText.AwayDefault);
        });
        SetBackCommand = ReactiveCommand.CreateFromTask(async () => { if (Selected() is { } s) { AwayMessage = ""; await _service.SetAwayAsync(s, ""); } });
        ChangeNickCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (Selected() is { } s && NewNick.Trim().Length > 0) await _service.ChangeNickAsync(s, NewNick.Trim());
        });

        foreach (var command in new IHandleObservableErrors[]
                 { SaveServerCommand, DeleteServerCommand, DisconnectCommand, TrustCertificateCommand, SetAwayCommand, SetBackCommand, ChangeNickCommand })
            command.ThrownExceptions.Subscribe(e => StatusText = e.Message);

        LoginChoices =
        [
            new(IrcLoginKind.None, IrcText.LoginNone),
            new(IrcLoginKind.Sasl, IrcText.LoginSasl),
            new(IrcLoginKind.NickServ, IrcText.LoginNickServ),
            new(IrcLoginKind.ServerPassword, IrcText.LoginServerPassword),
        ];
        _editLogin = LoginChoices[0];

        _service.SessionChanged += _ => QueueRefresh();
        QueueRefresh();
    }

    // ── How a server stands, in words ─────────────────────────────────────────

    public static string StateDot(IrcState state) => state switch
    {
        IrcState.Connected => "green",
        IrcState.Failed => "red",
        IrcState.Offline => "grey",
        _ => "amber",
    };

    public static string StateText(IrcSession session) => session.State switch
    {
        IrcState.Connected => session.IsAway ? IrcText.StateConnectedAway : JabberText.StateConnected,
        IrcState.Connecting => JabberText.StateConnecting,
        IrcState.Reconnecting => session.NextAttempt is { } next
            ? string.Format(CultureInfo.CurrentCulture, JabberText.StateRetryAt, ChatTime.Clock(next))
            : JabberText.StateConnecting,
        IrcState.WaitingForWorker => JabberText.StateWaitingForWorker,
        IrcState.Failed => session.Problem switch
        {
            IrcProblem.Password => JabberText.ProblemPassword,
            IrcProblem.Certificate => JabberText.ProblemCertificate,
            IrcProblem.NoEncryption => IrcText.ProblemNoEncryption,
            IrcProblem.Refused => IrcText.ProblemRefused,
            IrcProblem.Nickname => IrcText.ProblemNickname,
            _ => JabberText.StateOffline,
        },
        _ => JabberText.StateOffline,
    };

    // ── The list ──────────────────────────────────────────────────────────────

    public ObservableCollection<JabberRowVm> Rows { get; } = [];

    private JabberRowVm? _selectedRow;
    public JabberRowVm? SelectedRow
    {
        get => _selectedRow;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedRow, value);
            IsEditing = false;
            StatusText = "";
            AwayMessage = Selected()?.Server.AwayMessage ?? "";
            NewNick = "";
            RaisePanes();
            RefreshDetail();
        }
    }

    private IrcSession? Selected() => SelectedRow is { } row ? _service.SessionOf(row.AccountId) : null;

    private void QueueRefresh()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1) return;
        _ = Task.Delay(150).ContinueWith(_ =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            Dispatcher.UIThread.Post(Refresh);
        }, TaskScheduler.Default);
    }

    private void Refresh()
    {
        var wanted = _service.Sessions.OrderBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(s => new JabberRowVm
            {
                Kind = JabberRowKind.Account, AccountId = s.Server.Id, Text = s.DisplayName,
                Detail = StateText(s), Dot = StateDot(s.State),
            }).ToList();

        var have = Rows.ToDictionary(r => r.Identity);
        for (var i = 0; i < wanted.Count; i++)
            wanted[i] = have.TryGetValue(wanted[i].Identity, out var kept) ? kept.Fill(wanted[i]) : wanted[i].Fill(wanted[i]);
        var keep = wanted.Select(r => r.Identity).ToHashSet();
        for (var i = Rows.Count - 1; i >= 0; i--)
            if (!keep.Contains(Rows[i].Identity)) { if (ReferenceEquals(Rows[i], SelectedRow)) SelectedRow = null; Rows.RemoveAt(i); }
        for (var i = 0; i < wanted.Count; i++)
        {
            if (i < Rows.Count && ReferenceEquals(Rows[i], wanted[i])) continue;
            var at = Rows.IndexOf(wanted[i]);
            if (at >= 0) Rows.Move(at, i); else Rows.Insert(i, wanted[i]);
        }

        RaisePanes();
        RefreshDetail();
    }

    // ── The pane on the right ─────────────────────────────────────────────────

    public bool ShowWelcome => Rows.Count == 0 && !IsEditing;
    public bool ShowNothing => Rows.Count > 0 && SelectedRow is null && !IsEditing;
    public bool ShowServer => SelectedRow is not null && !IsEditing;

    private void RaisePanes()
    {
        foreach (var name in new[] { nameof(ShowWelcome), nameof(ShowNothing), nameof(ShowServer), nameof(IsEditing) })
            this.RaisePropertyChanged(name);
    }

    private string _title = "", _subtitle = "", _state = "", _detail = "", _certificate = "", _notes = "", _status = "", _away = "", _newNick = "";
    private bool _canConnect, _isConnected, _hasCertificateProblem;

    public string Title { get => _title; private set => this.RaiseAndSetIfChanged(ref _title, value); }
    public string Subtitle { get => _subtitle; private set => this.RaiseAndSetIfChanged(ref _subtitle, value); }
    public string ServerState { get => _state; private set => this.RaiseAndSetIfChanged(ref _state, value); }
    public string ServerDetail { get => _detail; private set { this.RaiseAndSetIfChanged(ref _detail, value); this.RaisePropertyChanged(nameof(HasServerDetail)); } }
    public bool HasServerDetail => ServerDetail.Length > 0;
    public string Notes { get => _notes; private set { this.RaiseAndSetIfChanged(ref _notes, value); this.RaisePropertyChanged(nameof(HasNotes)); } }
    public bool HasNotes => Notes.Length > 0;
    public bool CanConnect { get => _canConnect; private set => this.RaiseAndSetIfChanged(ref _canConnect, value); }
    public bool IsConnected { get => _isConnected; private set => this.RaiseAndSetIfChanged(ref _isConnected, value); }
    public bool HasCertificateProblem { get => _hasCertificateProblem; private set => this.RaiseAndSetIfChanged(ref _hasCertificateProblem, value); }
    public string CertificateText { get => _certificate; private set => this.RaiseAndSetIfChanged(ref _certificate, value); }
    public string StatusText { get => _status; set { this.RaiseAndSetIfChanged(ref _status, value); this.RaisePropertyChanged(nameof(HasStatus)); } }
    public bool HasStatus => StatusText.Length > 0;

    /// <summary>What this person is away with. Blank when they are here.</summary>
    public string AwayMessage { get => _away; set => this.RaiseAndSetIfChanged(ref _away, value); }
    public string NewNick { get => _newNick; set => this.RaiseAndSetIfChanged(ref _newNick, value); }

    private void RefreshDetail()
    {
        if (Selected() is not { } s) return;
        Title = s.DisplayName;
        Subtitle = $"{s.Server.Host}:{s.Server.Port}" + (s.Server.Tls ? " · TLS" : "");
        ServerState = StateText(s);
        ServerDetail = s.State is IrcState.Failed or IrcState.Reconnecting ? s.Detail : "";
        CanConnect = s.State is IrcState.Offline or IrcState.Failed;
        IsConnected = s.State == IrcState.Connected;
        HasCertificateProblem = s.Problem == IrcProblem.Certificate && s.CertificateFingerprint.Length > 0;
        CertificateText = HasCertificateProblem
            ? string.Format(CultureInfo.CurrentCulture, JabberText.CertificateAsk, s.CertificateSubject, string.Join(' ', s.CertificateFingerprint.Chunk(4).Select(c => new string(c))))
            : "";

        var notes = new List<string>();
        if (IsConnected)
        {
            notes.Add(string.Format(CultureInfo.CurrentCulture, IrcText.ConnectedAs, s.Nick, s.Network.Length > 0 ? s.Network : s.Server.Host));
            notes.Add(s.HasHistory ? IrcText.HistoryKept : IrcText.HistoryNone);
            if (!s.IsEncrypted) notes.Add(IrcText.NotEncrypted);
        }
        Notes = string.Join("\n", notes);
        if (s.LastRefusal.Length > 0 && IsConnected) StatusText = s.LastRefusal;
    }

    // ── Adding and editing a server ───────────────────────────────────────────

    public IReadOnlyList<IrcLoginChoice> LoginChoices { get; }

    private bool _isEditing, _editIsExisting, _editTls = true, _editAutoConnect;
    private string _editName = "", _editHost = "", _editNick = "", _editUser = "", _editReal = "", _editAccount = "", _editPassword = "", _editAlertWords = "", _editOnConnect = "";
    private decimal _editPort = 6697;
    private IrcLoginChoice _editLogin;
    private int _editingId;

    public bool IsEditing { get => _isEditing; private set { this.RaiseAndSetIfChanged(ref _isEditing, value); RaisePanes(); } }
    public bool EditIsExisting { get => _editIsExisting; private set => this.RaiseAndSetIfChanged(ref _editIsExisting, value); }
    public string EditName { get => _editName; set => this.RaiseAndSetIfChanged(ref _editName, value); }
    public string EditHost { get => _editHost; set => this.RaiseAndSetIfChanged(ref _editHost, value); }
    public decimal EditPort { get => _editPort; set => this.RaiseAndSetIfChanged(ref _editPort, value); }

    public bool EditTls
    {
        get => _editTls;
        set
        {
            // The usual ports go with the choice, unless someone typed another.
            if (value && EditPort == 6667) EditPort = 6697;
            else if (!value && EditPort == 6697) EditPort = 6667;
            this.RaiseAndSetIfChanged(ref _editTls, value);
            this.RaisePropertyChanged(nameof(ShowPlainWarning));
        }
    }

    public bool ShowPlainWarning => !EditTls;
    public string EditNick { get => _editNick; set => this.RaiseAndSetIfChanged(ref _editNick, value); }
    public string EditUser { get => _editUser; set => this.RaiseAndSetIfChanged(ref _editUser, value); }
    public string EditReal { get => _editReal; set => this.RaiseAndSetIfChanged(ref _editReal, value); }

    public IrcLoginChoice EditLogin
    {
        get => _editLogin;
        set
        {
            this.RaiseAndSetIfChanged(ref _editLogin, value ?? LoginChoices[0]);
            this.RaisePropertyChanged(nameof(NeedsPassword));
            this.RaisePropertyChanged(nameof(NeedsAccount));
        }
    }

    public bool NeedsPassword => EditLogin.Kind != IrcLoginKind.None;
    public bool NeedsAccount => EditLogin.Kind is IrcLoginKind.Sasl or IrcLoginKind.NickServ;
    public string EditAccount { get => _editAccount; set => this.RaiseAndSetIfChanged(ref _editAccount, value); }
    public string EditPassword { get => _editPassword; set => this.RaiseAndSetIfChanged(ref _editPassword, value); }
    public bool EditAutoConnect { get => _editAutoConnect; set => this.RaiseAndSetIfChanged(ref _editAutoConnect, value); }
    public string EditAlertWords { get => _editAlertWords; set => this.RaiseAndSetIfChanged(ref _editAlertWords, value); }
    public string EditOnConnect { get => _editOnConnect; set => this.RaiseAndSetIfChanged(ref _editOnConnect, value); }

    private void BeginAdd()
    {
        _editingId = 0;
        EditIsExisting = false;
        EditName = EditHost = EditNick = EditUser = EditReal = EditAccount = EditPassword = EditAlertWords = EditOnConnect = "";
        _editTls = true;
        this.RaisePropertyChanged(nameof(EditTls));
        this.RaisePropertyChanged(nameof(ShowPlainWarning));
        EditPort = 6697;
        EditLogin = LoginChoices[0];
        EditAutoConnect = false;
        StatusText = "";
        IsEditing = true;
    }

    private void BeginEdit()
    {
        if (Selected() is not { } s) return;
        var server = s.Server;
        _editingId = server.Id;
        EditIsExisting = true;
        EditName = server.Name;
        EditHost = server.Host;
        _editTls = server.Tls;
        this.RaisePropertyChanged(nameof(EditTls));
        this.RaisePropertyChanged(nameof(ShowPlainWarning));
        EditPort = server.Port;
        EditNick = server.Nickname;
        EditUser = server.Username;
        EditReal = server.RealName;
        EditLogin = LoginChoices.FirstOrDefault(c => (int)c.Kind == server.LoginKind) ?? LoginChoices[0];
        EditAccount = server.Account;
        EditPassword = "";
        EditAutoConnect = server.AutoConnect;
        EditAlertWords = server.AlertWords;
        EditOnConnect = server.OnConnect;
        StatusText = "";
        IsEditing = true;
    }

    private async Task SaveAsync()
    {
        StatusText = "";
        var host = EditHost.Trim();
        var nick = EditNick.Trim();
        if (host.Length == 0) { StatusText = IrcText.NeedHost; return; }
        if (nick.Length == 0 || nick.Contains(' ') || nick.StartsWith('#') || nick.StartsWith(':')) { StatusText = IrcText.NeedNick; return; }
        if (NeedsPassword && !EditIsExisting && EditPassword.Length == 0) { StatusText = IrcText.NeedPassword; return; }

        var existing = _editingId > 0 ? _service.SessionOf(_editingId)?.Server : null;
        var server = new IrcServer
        {
            Id = _editingId,
            Name = EditName.Trim(),
            Host = host,
            Port = (int)Math.Clamp(EditPort, 1, 65535),
            Tls = EditTls,
            Nickname = nick,
            Username = EditUser.Trim(),
            RealName = EditReal.Trim(),
            LoginKind = (int)EditLogin.Kind,
            Account = EditAccount.Trim(),
            AlertWords = EditAlertWords.Trim(),
            OnConnect = EditOnConnect.Trim(),
            // What was accepted before, and the away chosen, stay with the server unless where it is changed.
            TrustedCertificate = existing is not null && existing.Host == host && existing.Port == (int)EditPort ? existing.TrustedCertificate : "",
            AwayMessage = existing?.AwayMessage ?? "",
            AutoConnect = EditAutoConnect,
        };

        // A blank password on an existing server keeps the saved one; no login removes it.
        string? password = !NeedsPassword ? "" : EditPassword.Length > 0 ? EditPassword : null;
        var session = await _service.SaveServerAsync(server, password);
        EditPassword = "";
        IsEditing = false;
        Refresh();
        SelectedRow = Rows.FirstOrDefault(r => r.AccountId == session.Server.Id);

        // A server changed while connected takes the change on its next connection: now.
        if (existing is not null && session.State is not (IrcState.Offline or IrcState.WaitingForWorker))
            await _service.ReconnectAsync(session);
        else if (existing is null && EditAutoConnect) _service.Connect(session);
    }

    private async Task DeleteAsync()
    {
        if (Selected() is not { } s) return;
        if (Confirm is { } confirm && !await confirm(string.Format(CultureInfo.CurrentCulture, IrcText.ConfirmDelete, s.DisplayName))) return;
        await _service.DeleteServerAsync(s.Server.Id);
        IsEditing = false;
        SelectedRow = null;
        Refresh();
    }

    /// <summary>Asks before something that cannot be undone. Set by the view.</summary>
    public Func<string, Task<bool>>? Confirm { get; set; }

    public ReactiveCommand<Unit, Unit> AddServerCommand { get; }
    public ReactiveCommand<Unit, Unit> EditServerCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveServerCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelEditCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteServerCommand { get; }
    public ReactiveCommand<Unit, Unit> ConnectCommand { get; }
    public ReactiveCommand<Unit, Unit> DisconnectCommand { get; }
    public ReactiveCommand<Unit, Unit> TrustCertificateCommand { get; }
    public ReactiveCommand<Unit, Unit> SetAwayCommand { get; }
    public ReactiveCommand<Unit, Unit> SetBackCommand { get; }
    public ReactiveCommand<Unit, Unit> ChangeNickCommand { get; }
}
