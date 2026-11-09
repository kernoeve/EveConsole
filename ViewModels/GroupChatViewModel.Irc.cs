using System.Globalization;
using Avalonia.Threading;
using EveConsole.Localization;
using EveConsole.Models;
using EveConsole.Services;
using EveConsole.Services.Irc;
using EveConsole.Services.Jabber;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>
/// IRC in Group Chat: each server, its invitations and the channels it keeps, as Jabber's are. A channel is a room here:
/// saved conversation, people with their ranks, alerts and what it is for. What only IRC has (ranks as modes, /commands,
/// no correcting a message) is worked out in this file.
/// </summary>
public sealed partial class GroupChatViewModel
{
    private readonly IrcService? _irc;
    private List<IrcUnread> _ircUnread = [];
    private bool _addingIrc;
    private readonly Dictionary<int, string> _refusalsShown = [];

    /// <summary>Opens a conversation with someone on an IRC server in the Messages tool: server and nickname.</summary>
    public Action<int, string>? OpenIrcDirect { get; set; }

    /// <summary>Rooms are being added on an IRC server, where there is no nickname per room to choose.</summary>
    public bool IsAddingIrc
    {
        get => _addingIrc;
        private set
        {
            this.RaiseAndSetIfChanged(ref _addingIrc, value);
            this.RaisePropertyChanged(nameof(ShowJoinNick));
        }
    }

    public bool ShowJoinNick => !IsAddingIrc;

    private void HookIrc()
    {
        if (_irc is null) return;
        _irc.SessionChanged += session =>
        {
            QueueRefresh();
            // Something the server refused (not an operator, no such nick) is said where it was asked.
            var refusal = session.LastRefusal;
            if (refusal.Length > 0 && _refusalsShown.GetValueOrDefault(session.Server.Id) != refusal)
                Dispatcher.UIThread.Post(() =>
                {
                    _refusalsShown[session.Server.Id] = refusal;
                    if (SelectedRow is { IsIrc: true } open && open.ServerId == session.Server.Id) StatusText = refusal;
                });
        };
        _irc.UnreadChanged += QueueRefresh;
        _irc.MessageStored += m => Dispatcher.UIThread.Post(() => { OnIrcMessage(m); RaiseIrcPanelLine(m); });
        _irc.AlertArrived += m => Dispatcher.UIThread.Post(() => OnIrcAlert(m));
        _irc.ReactionsChanged += (server, conversation) => Dispatcher.UIThread.Post(() =>
        {
            if (SelectedRow is { IsIrc: true, IsRoom: true } row && row.ServerId == server && row.Key == conversation) _ = IrcLoadReactionsAsync(row);
        });
    }

    private IrcSession? IrcOf(GroupChatRowVm? row) => row is { IsIrc: true } && _irc is { } irc ? irc.SessionOf(row.ServerId) : null;

    private IEnumerable<IrcSession> IrcConnected() => _irc?.Sessions.Where(s => s.State == IrcState.Connected) ?? [];

    private async Task AddIrcRowsAsync(List<GroupChatRowVm> rows)
    {
        if (_irc is not { } irc) return;
        _ircUnread = await Task.Run(() => irc.UnreadAsync());
        var saved = await Task.Run(() => irc.SavedChannelsAsync());

        foreach (var session in irc.Sessions.OrderBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            var id = session.Server.Id;
            rows.Add(new GroupChatRowVm
            {
                Kind = GroupRowKind.IrcServer, ServerId = id, Text = session.DisplayName,
                Detail = $"{ShellText.NavIrc} · {IrcViewModel.StateText(session)}",
                Dot = IrcViewModel.StateDot(session.State),
            });

            foreach (var invite in session.Invites)
                rows.Add(new GroupChatRowVm
                {
                    Kind = GroupRowKind.IrcInvite, ServerId = id, Key = invite.Channel, Text = invite.Channel, Dot = "amber",
                    Detail = string.Format(CultureInfo.CurrentCulture, JabberText.RowInvitedBy, invite.From),
                });

            foreach (var channel in saved.Where(c => c.ServerId == id).OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var key = session.Fold(channel.Name);
                var state = session.ChannelOf(channel.Name);
                var count = _ircUnread.FirstOrDefault(u => u.ServerId == id && u.Conversation == key);
                rows.Add(new GroupChatRowVm
                {
                    Kind = GroupRowKind.IrcChannel, ServerId = id, Key = key, Text = state?.ServerName ?? channel.Name,
                    Detail = state is { Failure.Length: > 0 } ? state.Failure : RoomTypeNote(channel.RoomType),
                    RoomType = channel.RoomType,
                    Dot = state is { Joined: true } ? "green" : state is { Failure.Length: > 0 } ? "red" : "grey",
                    Unread = count.Count, Mentions = count.Mentions,
                });
            }
        }
    }

    // ── A channel's conversation ──────────────────────────────────────────────

    private async Task<bool> OpenIrcChannelAsync(GroupChatRowVm row)
    {
        if (_irc is not { } irc) return false;
        var page = await Task.Run(() => irc.LoadConversationAsync(row.ServerId, row.Key, limit: PageSize));
        if (!ReferenceEquals(SelectedRow, row)) return false;

        var session = irc.SessionOf(row.ServerId);
        foreach (var m in page) Lines.Add(ToIrcLine(m, session));
        HasEarlier = page.Count == PageSize;
        await IrcLoadReactionsAsync(row);
        return true;
    }

    /// <summary>
    /// An IRC line as Group Chat shows it. ⚠️ No MessageId: IRC has no correcting or taking back a message, and the id
    /// is what turns those on. Replying and reacting go by <see cref="GroupLineVm.ReactTarget"/>, where the server passes
    /// tags on.
    /// </summary>
    private GroupLineVm ToIrcLine(IrcStoredMessage m, IrcSession? session) => new(
        m.Id,
        m.Kind switch { IrcMessageKind.Action => "* " + m.Sender, IrcMessageKind.Notice => "-" + m.Sender + "-", _ => m.Sender },
        m.Body, ChatTime.Of(m.SentAt), "", m.IsMention)
    {
        Avatar = new AvatarRef(IrcService.AvatarServiceName, m.ServerId, m.IsOutgoing ? "me" : "nick:" + m.Sender.ToLowerInvariant(), m.Sender),
        ReactTarget = session is { HasTags: true } ? m.MessageId : "",
        IsOutgoing = m.IsOutgoing,
        ReplyText = ReplyTextFor(m.ReplyId, "", Lines, l => l.ReactTarget, l => l.Sender, l => l.Body),
    };

    private void OnIrcMessage(IrcStoredMessage m)
    {
        if (!m.IsChannel || SelectedRow is not { IsIrc: true, IsRoom: true } row || row.ServerId != m.ServerId || row.Key != m.Conversation) return;
        if (Lines.Any(l => l.Id == m.Id)) return;
        Add(ToIrcLine(m, _irc?.SessionOf(m.ServerId)));
        _ = MarkOpenReadAsync();
    }

    private void OnIrcAlert(IrcStoredMessage m)
    {
        // A direct message is the Messages tool's to sound.
        if (!m.IsChannel) return;
        if (IsBeingLookedAt && SelectedRow is { IsIrc: true, IsRoom: true } open && open.ServerId == m.ServerId && open.Key == m.Conversation) return;
        Ring();
    }

    private void RaiseIrcPanelLine(IrcStoredMessage m)
    {
        if (m.IsChannel)
            PanelLineArrived?.Invoke(new GroupChatRowVm { Kind = GroupRowKind.IrcChannel, ServerId = m.ServerId, Key = m.Conversation }.Identity,
                ToIrcLine(m, _irc?.SessionOf(m.ServerId)));
    }

    private async Task<(IReadOnlyList<GroupLineVm>, bool)> IrcPanelLinesAsync(GroupChatRowVm row)
    {
        if (_irc is not { } irc || !row.IsRoom) return ([], false);
        var page = await Task.Run(() => irc.LoadConversationAsync(row.ServerId, row.Key, limit: PageSize));
        var session = irc.SessionOf(row.ServerId);
        return ([.. page.Select(m => ToIrcLine(m, session))], page.Count == PageSize);
    }

    private async Task IrcLoadEarlierAsync(GroupChatRowVm row)
    {
        if (_irc is not { } irc) return;
        var page = await Task.Run(() => irc.LoadConversationAsync(row.ServerId, row.Key, Lines[0].Id, PageSize));
        var session = irc.SessionOf(row.ServerId);
        for (var i = page.Count - 1; i >= 0; i--) Lines.Insert(0, ToIrcLine(page[i], session));
        HasEarlier = page.Count == PageSize;
        await IrcLoadReactionsAsync(row);
    }

    private async Task IrcLoadReactionsAsync(GroupChatRowVm row)
    {
        if (_irc is not { } irc || irc.SessionOf(row.ServerId) is not { } session) return;
        var lines = Lines.Where(l => l.ReactTarget.Length > 0).ToList();
        if (lines.Count == 0) return;
        var counts = await Task.Run(() => irc.ReactionsAsync(row.ServerId, row.Key, [.. lines.Select(l => l.ReactTarget)], session.Nick));
        if (!ReferenceEquals(SelectedRow, row)) return;
        foreach (var line in lines) line.SetReactions(counts.GetValueOrDefault(line.ReactTarget));
    }

    private async Task IrcReactAsync(GroupChatRowVm row, GroupLineVm line, string emoji)
    {
        if (IrcOf(row) is not { } session) return;
        StatusText = "";
        var target = session.ChannelOf(row.Key)?.ServerName ?? row.Key;
        try { await Task.Run(() => _irc!.ReactAsync(session, target, row.Key, line.ReactTarget, emoji)); }
        catch (Exception e) when (e is not OperationCanceledException) { StatusText = e.Message; }
    }

    private async Task IrcSendAsync(IrcSession session, GroupChatRowVm row, string text, string replyTo)
    {
        var target = session.ChannelOf(row.Key)?.ServerName ?? row.Key;
        var said = await Task.Run(() => _irc!.SendAsync(session, target, text, replyTo));
        if (said.Length > 0) StatusText = said;
    }

    private void ShowIrc(GroupChatRowVm row, IrcSession session)
    {
        ServerState = IrcViewModel.StateText(session);
        CanConnect = session.State is IrcState.Offline or IrcState.Failed;
        var connected = session.State == IrcState.Connected;

        if (row.Kind == GroupRowKind.IrcServer)
        {
            Subtitle = $"{session.Server.Host}:{session.Server.Port}" + (session.Server.Tls ? " · TLS" : "");
            return;
        }
        if (row.Kind == GroupRowKind.IrcInvite) { Subtitle = row.ShownDetail; return; }

        var state = session.ChannelOf(row.Key);
        ChosenRoomType = RoomTypeChoices.FirstOrDefault(c => c.Type == row.ShownRoomType);
        CanSend = connected && state is { Joined: true };
        CanAttach = false;
        DraftHint = string.Format(CultureInfo.CurrentCulture, IrcText.MessageHint, state?.ServerName ?? row.Key);
        CannotSend = !connected ? string.Format(CultureInfo.CurrentCulture, MessagesText.CannotSendNotConnected, session.DisplayName)
                   : state is not { Joined: true } ? IrcText.NotInChannel
                   : "";
        Subtitle = state is { Topic.Length: > 0 } ? state.Topic : row.ShownText;
        if (connected) ArchiveText = session.HasHistory ? IrcText.HistoryKept : IrcText.HistoryNone;
        if (state is { Failure.Length: > 0 }) RoomProblem = state.Failure;

        Occupants.Clear();
        if (state is null) return;
        ChosenAlert = AlertChoices.FirstOrDefault(c => c.Mode == state.Channel.AlertMode);

        var support = session.Locked(s => s.Client?.Support) ?? new IrcSupport();
        var me = session.Fold(session.Nick);
        var people = session.Locked(_ => state.Members.Values.ToList());
        foreach (var p in people.OrderBy(p => p.Ranks.Length > 0 ? support.RankOfMode(p.Ranks[0]) : int.MaxValue)
                                .ThenBy(p => p.Nick, StringComparer.CurrentCultureIgnoreCase))
        {
            var symbol = p.Ranks.Length > 0 && support.RankOfMode(p.Ranks[0]) is var rank and >= 0 ? support.Ranks[rank].Symbol.ToString() : "";
            Occupants.Add(new JabberOccupantVm(symbol + p.Nick, p.Away ? "amber" : "green", RankText(p.Ranks, p.AwayMessage))
            {
                IrcMember = p,
                IrcSelf = session.Fold(p.Nick) == me,
                Avatar = new AvatarRef(IrcService.AvatarServiceName, session.Server.Id, session.Fold(p.Nick) == me ? "me" : "nick:" + p.Nick.ToLowerInvariant(), p.Nick),
            });
        }
    }

    /// <summary>A person's ranks in words, for the tip over their name, with why they are away if they are.</summary>
    private static string RankText(string ranks, string away)
    {
        var words = ranks.Select(r => r switch
        {
            'q' => IrcText.RankFounder,
            'a' => IrcText.RankAdmin,
            'o' => IrcText.RankOperator,
            'h' => IrcText.RankHalfOperator,
            'v' => IrcText.RankVoice,
            _ => "+" + r,
        }).ToList();
        if (away.Length > 0) words.Add(string.Format(CultureInfo.CurrentCulture, IrcText.AwayBecause, away));
        return string.Join(" · ", words);
    }

    // ── What the menus do on an IRC channel ───────────────────────────────────

    private (IrcSession Session, IrcChannelState State)? OpenIrcChannel() =>
        SelectedRow is { IsIrc: true, IsRoom: true } row && IrcOf(row) is { } session && session.ChannelOf(row.Key) is { } state ? (session, state) : null;

    /// <summary>The ranks this person can hand out where they are an operator: operator, half-operator where the server has it, voice.</summary>
    public IReadOnlyList<(char Mode, string Give, string Take)> IrcRankChoices()
    {
        if (OpenIrcChannel() is not (IrcSession session, IrcChannelState state) || !IrcService.IsOperator(session, IrcService.MyRanks(session, state.ServerName))) return [];
        var support = session.Locked(s => s.Client?.Support) ?? new IrcSupport();
        var list = new List<(char, string, string)>();
        if (support.RankOfMode('o') >= 0) list.Add(('o', IrcText.MenuGiveOperator, IrcText.MenuTakeOperator));
        if (support.RankOfMode('h') >= 0) list.Add(('h', IrcText.MenuGiveHalfOperator, IrcText.MenuTakeHalfOperator));
        if (support.RankOfMode('v') >= 0) list.Add(('v', GroupChatText.MenuGiveVoice, GroupChatText.MenuTakeVoice));
        return list;
    }

    /// <summary>Whether this person can put others out of the channel open: half-operator or above.</summary>
    public bool IrcCanModerate() =>
        OpenIrcChannel() is (IrcSession session, IrcChannelState state) && IrcService.IsOperator(session, IrcService.MyRanks(session, state.ServerName));

    private async Task IrcActAsync(Func<IrcSession, string, Task> act)
    {
        StatusText = "";
        if (OpenIrcChannel() is not (IrcSession session, IrcChannelState state)) return;
        try { await Task.Run(() => act(session, state.ServerName)); }
        catch (Exception e) when (e is not OperationCanceledException) { StatusText = e.Message; }
    }

    public Task IrcSetRankAsync(JabberOccupantVm person, char mode, bool give) =>
        person.IrcMember is { } member ? IrcActAsync((s, channel) => _irc!.SetRankAsync(s, channel, member.Nick, mode, give)) : Task.CompletedTask;

    public Task IrcKickAsync(JabberOccupantVm person, string reason) =>
        person.IrcMember is { } member ? IrcActAsync((s, channel) => _irc!.KickAsync(s, channel, member.Nick, reason)) : Task.CompletedTask;

    public Task IrcBanAsync(JabberOccupantVm person, string reason) =>
        person.IrcMember is { } member ? IrcActAsync((s, channel) => _irc!.BanAsync(s, channel, member, reason)) : Task.CompletedTask;

    public Task IrcSetTopicAsync(string topic) => IrcActAsync((s, channel) => _irc!.SetTopicAsync(s, channel, topic));

    public Task IrcInviteAsync(string nick) =>
        nick.Trim() is { Length: > 0 } who ? IrcActAsync((s, channel) => _irc!.InviteAsync(s, who, channel)) : Task.CompletedTask;

    /// <summary>The nicknames of the people in every channel this server has, to offer when inviting.</summary>
    public IReadOnlyList<string> IrcKnownNicks() =>
        OpenIrcChannel() is (IrcSession session, _)
            ? [.. session.Channels.SelectMany(c => session.Locked(_ => c.Members.Values.Select(m => m.Nick).ToList()))
                                  .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)]
            : [];

    // ── Adding channels ───────────────────────────────────────────────────────

    private async Task BrowseIrcAsync(IrcSession session)
    {
        var stamp = ++_browseStamp;

        IsAddingIrc = true;
        _addingAccount = session.Server.Id;
        AddingTo = string.Format(CultureInfo.CurrentCulture, GroupChatText.AddingTo, session.DisplayName);
        JoinRoom = JoinNick = JoinPassword = "";
        JoinRoomType = RoomTypeChoices[0];
        _allChoices.Clear();
        RoomChoices = [];
        _lastJoined = "";

        _rowBeforeAdding = SelectedRow;
        _selectedRow = null;
        this.RaisePropertyChanged(nameof(SelectedRow));
        _roomFilter = "";
        this.RaisePropertyChanged(nameof(RoomFilter));
        StatusText = "";

        _isAddingRooms = true;
        this.RaisePropertyChanged(nameof(IsAddingRooms));
        RaisePanes();
        IsLoadingRooms = true;
        try
        {
            var joined = session.Channels.Where(c => c.Joined).Select(c => session.Fold(c.ServerName)).ToHashSet(StringComparer.Ordinal);
            var listed = await Task.Run(() => _irc!.ListChannelsAsync(session));
            if (stamp != _browseStamp) return;

            foreach (var entry in listed)
            {
                var choice = new GroupRoomChoiceVm(new XmppRoomInfo(entry.Channel, entry.Channel), joined.Contains(session.Fold(entry.Channel)));
                choice.Apply(new XmppRoomDetails(entry.Channel, entry.Channel, "", entry.Topic, entry.People, false, false, false, false));
                _allChoices.Add(choice);
            }
            _allChoices.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name));
            FillChoices();
            if (_allChoices.Count == 0) StatusText = IrcText.NoChannelsListed;
        }
        catch (Exception e) when (e is not OperationCanceledException) { StatusText = e.Message; }
        finally
        {
            if (stamp == _browseStamp) IsLoadingRooms = false;
        }
    }

    private JabberRoomTypeChoice IrcTypeOfKept(string room)
    {
        var type = _irc?.SessionOf(_addingAccount)?.ChannelOf(room)?.Channel.RoomType ?? JabberRoomType.General;
        return RoomTypeChoices.FirstOrDefault(c => c.Type == type) ?? RoomTypeChoices[0];
    }

    private void IrcSyncAddingRooms()
    {
        if (_irc?.SessionOf(_addingAccount) is not { } session) return;
        var joined = session.Channels.Where(c => c.Joined).Select(c => session.Fold(c.ServerName)).ToHashSet(StringComparer.Ordinal);
        foreach (var choice in _allChoices) choice.IsJoined = joined.Contains(session.Fold(choice.Jid));

        if (_lastJoined.Length > 0 && session.ChannelOf(_lastJoined) is { Failure.Length: > 0 } refused)
        {
            StatusText = $"{refused.ServerName}: {refused.Failure}";
            _lastJoined = "";
        }
    }

    private async Task IrcJoinRoomAsync()
    {
        if (_irc?.SessionOf(_addingAccount) is not { } session) return;
        var room = JoinRoom.Trim();
        if (room.Length == 0) return;

        StatusText = "";
        var type = JoinRoomType.Type;
        if (session.ChannelOf(room) is { Joined: true })
            await Task.Run(() => _irc.SetRoomTypeAsync(session, room, type));
        else
            await Task.Run(() => _irc.JoinChannelAsync(session, room, JoinPassword, type));

        _lastJoined = room.StartsWith('#') || session.IsChannelName(room) ? room : "#" + room;
        JoinPassword = "";
        JoinRoom = "";
        RoomChoice = null;
        JoinRoomType = RoomTypeChoices[0];
        await RefreshAsync();
    }
}
