using System.Collections.ObjectModel;
using System.Reactive;
using EveConsole.Localization;
using EveConsole.Services.Messages;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>A room or Mumble server a Group Chat control can show.</summary>
public sealed record PanelRoomChoice(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// One group chat room on a panel: the room set for the control, changeable from its dropdown for
/// as long as the panel shows. Its own, apart from the Group Chat tool's selection.
/// </summary>
public sealed class PanelGroupChatVm : ReactiveObject, IDisposable
{
    private const int MostShown = 500;

    private readonly GroupChatViewModel _chat;
    private string? _wanted;
    private bool _started;

    public PanelGroupChatVm(GroupChatViewModel chat, string? room)
    {
        _chat = chat;
        _wanted = room;
        SendCommand = ReactiveCommand.CreateFromTask(SendAsync, this.WhenAnyValue(x => x.Draft, d => !string.IsNullOrWhiteSpace(d)));
        SendCommand.ThrownExceptions.Subscribe(e => StatusText = e.Message);
    }

    public ObservableCollection<PanelRoomChoice> Rooms { get; } = [];
    public ObservableCollection<GroupLineVm> Lines { get; } = [];
    public ReactiveCommand<Unit, Unit> SendCommand { get; }

    /// <summary>Asks the view to bring the newest message into sight.</summary>
    public event Action? ScrollToEndRequested;

    private PanelRoomChoice? _selected;
    public PanelRoomChoice? SelectedRoom
    {
        get => _selected;
        set
        {
            // A ComboBox reports null while its list is being changed; that isn't a choice.
            if (value is null || value == _selected) return;
            this.RaiseAndSetIfChanged(ref _selected, value);
            _wanted = value.Id;
            _ = LoadAsync();
        }
    }

    private string _draft = "", _status = "", _note = "";
    public string Draft { get => _draft; set => this.RaiseAndSetIfChanged(ref _draft, value); }
    public string StatusText { get => _status; private set { this.RaiseAndSetIfChanged(ref _status, value); this.RaisePropertyChanged(nameof(HasStatus)); } }
    public bool HasStatus => StatusText.Length > 0;

    /// <summary>Why nothing shows: no room set, or the one set is no longer in Group Chat.</summary>
    public string Note { get => _note; private set { this.RaiseAndSetIfChanged(ref _note, value); this.RaisePropertyChanged(nameof(HasNote)); } }
    public bool HasNote => Note.Length > 0;

    public void Start()
    {
        if (_started) return;
        _started = true;
        _chat.PanelLineArrived += OnLine;
        _chat.Rows.CollectionChanged += OnRowsChanged;
        FillRooms();
    }

    public void Dispose()
    {
        if (!_started) return;
        _started = false;
        _chat.PanelLineArrived -= OnLine;
        _chat.Rows.CollectionChanged -= OnRowsChanged;
    }

    private void OnRowsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => FillRooms();

    /// <summary>The rooms as Group Chat lists them now; the one wanted is chosen once it's there.</summary>
    private void FillRooms()
    {
        var rooms = _chat.PanelRooms().Select(r => new PanelRoomChoice(r.Id, r.Name)).ToList();
        if (!Rooms.SequenceEqual(rooms))
        {
            Rooms.Clear();
            foreach (var r in rooms) Rooms.Add(r);
        }

        var pick = Rooms.FirstOrDefault(r => r.Id == _wanted);
        if (pick is not null && pick != _selected)
        {
            _selected = pick;
            this.RaisePropertyChanged(nameof(SelectedRoom));
            _ = LoadAsync();
        }
        else if (pick is null && _selected is null)
            Note = _wanted is null ? ClientLayoutsText.GroupChatChooseRoom : ClientLayoutsText.GroupChatRoomGone;
    }

    private async Task LoadAsync()
    {
        Lines.Clear();
        StatusText = "";
        Note = "";
        if (_selected is not { } room || _chat.RowOf(room.Id) is not { } row) return;

        try
        {
            var (lines, _) = await _chat.PanelLinesAsync(row);
            if (_selected != room) return;
            foreach (var line in lines.TakeLast(MostShown)) Lines.Add(line);
            ScrollToEndRequested?.Invoke();
        }
        catch (Exception e) when (e is not OperationCanceledException) { StatusText = e.Message; }
    }

    private void OnLine(string roomId, GroupLineVm line)
    {
        if (_selected?.Id != roomId) return;
        if (line.Id != 0 && Lines.Any(l => l.Id == line.Id)) return;
        Lines.Add(line);
        if (Lines.Count > MostShown) Lines.RemoveAt(0);
        ScrollToEndRequested?.Invoke();
    }

    /// <summary>Clicked into or written in: what's showing has been read.</summary>
    public void MarkRead()
    {
        if (_selected is { } room && _chat.RowOf(room.Id) is { } row) _ = _chat.PanelMarkReadAsync(row);
    }

    private async Task SendAsync()
    {
        if (_selected is not { } room || _chat.RowOf(room.Id) is not { } row) return;
        var text = Draft;
        Draft = "";
        StatusText = "";
        try { await _chat.PanelSendAsync(row, text); }
        catch { Draft = text; throw; }   // nothing typed is lost to a dropped connection
        MarkRead();
    }
}

/// <summary>
/// Direct messages on a panel: everyone with a saved conversation down the side, and the one
/// picked beside it. Its own, apart from the Messages tool's tabs.
/// </summary>
public sealed class PanelDmVm : ReactiveObject, IDisposable
{
    private readonly MessagesViewModel _messages;
    private bool _started;

    public PanelDmVm(MessagesViewModel messages) => _messages = messages;

    public ObservableCollection<MessagesConversationVm> People { get; } = [];

    private MessagesConversationVm? _selectedPerson;
    private DmKey? _selectedKey;
    public MessagesConversationVm? SelectedPerson
    {
        get => _selectedPerson;
        set
        {
            if (value is null || value == _selectedPerson) return;
            this.RaiseAndSetIfChanged(ref _selectedPerson, value);
            if (value.Summary.Key != _selectedKey) _ = OpenAsync(value);
        }
    }

    private MessagesTabVm? _conversation;
    /// <summary>The conversation picked: its messages, and where to write.</summary>
    public MessagesTabVm? Conversation
    {
        get => _conversation;
        private set
        {
            this.RaiseAndSetIfChanged(ref _conversation, value);
            this.RaisePropertyChanged(nameof(HasConversation));
            this.RaisePropertyChanged(nameof(ShowNothingOpen));
        }
    }

    public bool HasConversation => Conversation is not null;
    public bool ShowNothingOpen => Conversation is null;
    public bool HasNoPeople => People.Count == 0;

    public void Start()
    {
        if (_started) return;
        _started = true;
        _messages.ConversationsChanged += FillPeople;
        _messages.LineArrived += OnLine;
        FillPeople();
    }

    public void Dispose()
    {
        if (!_started) return;
        _started = false;
        _messages.ConversationsChanged -= FillPeople;
        _messages.LineArrived -= OnLine;
    }

    /// <summary>Everyone, newest first. Replaced only when something shows differently, so the pick stays.</summary>
    private void FillPeople()
    {
        var all = _messages.AllConversations();
        if (!People.Select(p => p.Summary).SequenceEqual(all.Select(p => p.Summary)))
        {
            People.Clear();
            foreach (var p in all) People.Add(p);
            _selectedPerson = People.FirstOrDefault(p => p.Summary.Key == _selectedKey);
            this.RaisePropertyChanged(nameof(SelectedPerson));
            this.RaisePropertyChanged(nameof(HasNoPeople));
        }
        if (Conversation is { } open && all.FirstOrDefault(p => p.Summary.Key == open.Key) is { } now) open.Apply(now.Summary);
    }

    private async Task OpenAsync(MessagesConversationVm person)
    {
        var key = person.Summary.Key;
        _selectedKey = key;
        var tab = await _messages.OpenForPanelAsync(key);
        if (_selectedKey != key) return;
        Conversation = tab;
        MarkRead();
    }

    private void OnLine(DmKey key, DmLine line)
    {
        if (Conversation is { } open && open.Key == key) open.Add(line);
    }

    /// <summary>Clicked into or written in: the open conversation has been read.</summary>
    public void MarkRead()
    {
        if (Conversation is { } open) _ = _messages.MarkReadAsync(open.Key);
    }
}
