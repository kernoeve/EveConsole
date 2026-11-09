using EveConsole.Localization;
using EveConsole.Models;
using EveConsole.Services.Irc;
using EveConsole.Services.Jabber;

namespace EveConsole.Services.Messages;

/// <summary>
/// IRC's direct messages for the Messages window. Channels are Group Chat's.
///
/// <para>A conversation is keyed by the other person's nickname, folded the server's way, so case cannot split one in
/// two. IRC has no contacts, no files and no correcting a message; it has typing and reactions where the server passes
/// tags on.</para>
/// </summary>
public sealed class IrcDmSource : IDmSource
{
    public const string Name = "irc";

    private readonly IrcService _service;

    public string Source => Name;

    public event Action? Changed;
    public event Action? UnreadChanged;
    public event Action<DmKey, DmLine, bool>? Arrived;
    public event Action<DmKey>? Edited { add { } remove { } }   // IRC has no correcting what was said
    public event Action<DmKey>? ReactionsChanged;
    public event Action<DmKey, bool>? Typing;

    public IrcDmSource(IrcService service)
    {
        _service = service;
        _service.SessionChanged += _ => Changed?.Invoke();
        _service.UnreadChanged += () => UnreadChanged?.Invoke();
        _service.MessageStored += row =>
        {
            if (!row.IsChannel) Arrived?.Invoke(KeyOf(row.ServerId, row.Conversation), ToLine(row), row is { IsAlert: true, IsRead: false });
        };
        _service.ReactionsChanged += (server, conversation) =>
        {
            if (!IsChannel(server, conversation)) ReactionsChanged?.Invoke(KeyOf(server, conversation));
        };
        _service.TypingChanged += (server, conversation, _, typing) =>
        {
            if (!IsChannel(server, conversation)) Typing?.Invoke(KeyOf(server, conversation), typing);
        };
    }

    private bool IsChannel(int server, string conversation) =>
        _service.SessionOf(server) is { } s ? s.IsChannelName(conversation) : conversation.StartsWith('#');

    private static DmKey KeyOf(int server, string peer) => new(Name, server, peer);

    private static DmLine ToLine(IrcStoredMessage m) => new(
        m.Id,
        m.Kind switch { IrcMessageKind.Action => "* " + m.Sender, IrcMessageKind.Notice => "-" + m.Sender + "-", _ => m.Sender },
        m.Body, m.SentAt, m.IsOutgoing, Target: m.MessageId, ReplyId: m.ReplyId);

    /// <summary>The nickname as the person writes it: as seen in a channel or watched, else as last saved.</summary>
    private static string Shown(IrcSession? session, string folded, string fallback)
    {
        if (session is null) return fallback.Length > 0 ? fallback : folded;
        var person = session.Locked(s => s.PeopleMap.GetValueOrDefault(folded));
        if (person is not null) return person.Nick;
        var member = session.Locked(s => s.ChannelMap.Values.Select(c => c.Members.GetValueOrDefault(folded)).FirstOrDefault(m => m is not null));
        return member?.Nick ?? (fallback.Length > 0 ? fallback : folded);
    }

    public async Task<IReadOnlyList<DmSummary>> ConversationsAsync(CancellationToken ct = default)
    {
        var rows = await _service.DirectSummariesAsync(ct);
        return [.. rows.Select(r => Describe(KeyOf(r.ServerId, r.Conversation), r.PeerName) with
        {
            LastText = r.LastText, LastAt = r.LastAt, Unread = r.Unread, Alerts = r.Alerts,
        })];
    }

    public DmSummary Describe(DmKey key, string name)
    {
        var session = _service.SessionOf(key.ServerId);
        if (session is null) return new DmSummary(key, name, "", ShellText.NavIrc) { CannotSend = string.Format(MessagesText.CannotSendNotConnected, ShellText.NavIrc) };

        var shown = Shown(session, key.Peer, name);
        var connected = session.State == IrcState.Connected;
        var person = session.Locked(s => s.PeopleMap.GetValueOrDefault(key.Peer));
        var inChannel = session.Locked(s => s.ChannelMap.Values.Any(c => c.Members.ContainsKey(key.Peer)));
        var online = person?.Online ?? (inChannel ? true : null);
        var away = person?.Away == true || session.Locked(s => s.ChannelMap.Values.Any(c => c.Members.GetValueOrDefault(key.Peer)?.Away == true));

        return new DmSummary(key, shown, session.DisplayName, ShellText.NavIrc)
        {
            Presence = !connected || online is null ? DmPresence.Unknown : online == false ? DmPresence.Offline : away ? DmPresence.Away : DmPresence.Online,
            Status = person?.AwayMessage ?? "",

            // ⚠️ An IRC server delivers only to someone connected now, and keeps nothing for later (unless it keeps
            // history, which this cannot know for the other person). Said plainly rather than lost silently.
            CannotSend = !connected ? string.Format(MessagesText.CannotSendNotConnected, session.DisplayName)
                       : online == false ? string.Format(MessagesText.CannotSendOffline, shown)
                       : "",
        };
    }

    public IReadOnlyList<DmPerson> OnlinePeople()
    {
        var found = new Dictionary<DmKey, DmPerson>();
        foreach (var session in _service.Sessions.Where(s => s.State == IrcState.Connected))
        {
            var me = session.Fold(session.Nick);
            var people = session.Locked(s => s.ChannelMap.Values.SelectMany(c => c.Members).ToList());
            foreach (var (folded, member) in people)
            {
                if (folded == me) continue;
                var key = KeyOf(session.Server.Id, folded);
                found.TryAdd(key, new DmPerson(key, member.Nick, session.DisplayName, ShellText.NavIrc)
                {
                    Presence = member.Away ? DmPresence.Away : DmPresence.Online,
                });
            }
            foreach (var person in session.People.Where(p => p.Online == true))
            {
                var key = KeyOf(session.Server.Id, session.Fold(person.Nick));
                found.TryAdd(key, new DmPerson(key, person.Nick, session.DisplayName, ShellText.NavIrc)
                {
                    Presence = person.Away ? DmPresence.Away : DmPresence.Online,
                });
            }
        }
        return [.. found.Values];
    }

    public async Task<IReadOnlyList<DmLine>> LoadAsync(DmKey key, long beforeId, int limit, CancellationToken ct = default) =>
        [.. (await _service.LoadConversationAsync(key.ServerId, key.Peer, beforeId, limit, ct)).Select(ToLine)];

    public Task MarkReadAsync(DmKey key, CancellationToken ct = default) => _service.MarkReadAsync(key.ServerId, key.Peer, ct);

    private IrcSession SessionOf(DmKey key) =>
        _service.SessionOf(key.ServerId) ?? throw new InvalidOperationException(string.Format(MessagesText.CannotSendNotConnected, ShellText.NavIrc));

    public async Task SendAsync(DmKey key, string text, CancellationToken ct = default)
    {
        var session = SessionOf(key);
        var said = await _service.SendAsync(session, Shown(session, key.Peer, key.Peer), text, "", ct);
        if (said.Length > 0) throw new InvalidOperationException(said);
    }

    public Task SetTypingAsync(DmKey key, bool typing, CancellationToken ct = default) =>
        _service.SessionOf(key.ServerId) is { } session ? _service.SendTypingAsync(session, Shown(session, key.Peer, key.Peer), typing, ct) : Task.CompletedTask;

    public Task<Dictionary<string, List<JabberReactionCount>>> ReactionsAsync(DmKey key, IReadOnlyCollection<string> targets, CancellationToken ct = default) =>
        _service.ReactionsAsync(key.ServerId, key.Peer, targets, _service.SessionOf(key.ServerId)?.Nick ?? "", ct);

    /// <summary>Sends this person's whole set of reactions to a message: on IRC, each emoji added or taken off since.</summary>
    public async Task ReactAsync(DmKey key, string target, IReadOnlyList<string> emojis, CancellationToken ct = default)
    {
        var session = SessionOf(key);
        var now = await _service.ReactionsAsync(key.ServerId, key.Peer, [target], session.Nick, ct);
        var mine = now.GetValueOrDefault(target)?.Where(c => c.Mine).Select(c => c.Emoji).ToHashSet() ?? [];
        var peer = Shown(session, key.Peer, key.Peer);
        foreach (var emoji in mine.Except(emojis).Concat(emojis.Except(mine)))
            await _service.ReactAsync(session, peer, key.Peer, target, emoji, ct);
    }

    public Task SendFileAsync(DmKey key, Stream content, string fileName, long size, string contentType, CancellationToken ct = default) =>
        throw new NotSupportedException("IRC has no way to send a file here.");

    public Task AddContactAsync(DmKey key, CancellationToken ct = default) => Task.CompletedTask;
    public Task RemoveContactAsync(DmKey key, CancellationToken ct = default) => Task.CompletedTask;

    public string MyName(int serverId) => _service.SessionOf(serverId) is { } s ? s.Nick.Length > 0 ? s.Nick : s.Server.Nickname : "";
}
