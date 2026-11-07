using EveConsole.Localization;
using EveConsole.Models;
using EveConsole.Services.Jabber;

namespace EveConsole.Services.Messages;

/// <summary>Jabber's direct messages for the Messages window. Rooms are Group Chat's.</summary>
public sealed class JabberDmSource : IDmSource
{
    public const string Name = "jabber";

    private readonly JabberService _service;

    public string Source => Name;

    public event Action? Changed;
    public event Action? UnreadChanged;
    public event Action<DmKey, DmLine, bool>? Arrived;
    public event Action<DmKey>? Edited;
    public event Action<DmKey, bool>? Typing;

    public JabberDmSource(JabberService service)
    {
        _service = service;
        _service.SessionChanged += _ => Changed?.Invoke();
        _service.UnreadChanged += () => UnreadChanged?.Invoke();
        _service.MessageStored += row =>
        {
            if (!row.IsRoom) Arrived?.Invoke(KeyOf(row.AccountId, row.Conversation), ToLine(row), row is { IsAlert: true, IsRead: false });
        };
        _service.ConversationEdited += (account, conversation) => Edited?.Invoke(KeyOf(account, conversation));
        _service.TypingChanged += (account, conversation, typing) => Typing?.Invoke(KeyOf(account, conversation), typing);
    }

    private static DmKey KeyOf(int account, string peer) => new(Name, account, peer);

    private static DmLine ToLine(JabberStoredMessage m) => new(m.Id, m.Sender, m.Body, m.SentAt, m.IsOutgoing, m.Deleted, m.DeletedReason);

    public async Task<IReadOnlyList<DmSummary>> ConversationsAsync(CancellationToken ct = default)
    {
        var rows = await _service.DirectSummariesAsync(ct);
        return [.. rows.Select(r => Describe(KeyOf(r.AccountId, r.Conversation), r.Conversation) with
        {
            LastText = r.LastText, LastAt = r.LastAt, Unread = r.Unread, Alerts = r.Alerts,
        })];
    }

    public DmSummary Describe(DmKey key, string name)
    {
        var session = _service.SessionOf(key.ServerId);
        var server  = session is null ? "" : session.Account.Name.Length > 0 ? session.Account.Name : session.Account.Address;
        if (session is null) return new DmSummary(key, name, server, ShellText.NavJabber) { CannotSend = string.Format(MessagesText.CannotSendNotConnected, ShellText.NavJabber) };

        var connected = session.State == JabberState.Connected;
        var notConnected = connected ? "" : string.Format(MessagesText.CannotSendNotConnected, server);

        // Written to through a room rather than to their own address, which only works while they are in it.
        var address = XmppAddress.Parse(key.Peer);
        if (address.Resource.Length > 0)
        {
            var here = session.Rooms.FirstOrDefault(r => r.Room.RoomJid == address.Bare);
            var inRoom = here is not null && session.Locked(_ => here.Occupants.ContainsKey(address.Resource));
            var roomName = here is { Room.Name.Length: > 0 } ? here.Room.Name : address.Local;

            return new DmSummary(key, address.Resource, $"{server} · {roomName}", ShellText.NavJabber)
            {
                Presence = inRoom ? DmPresence.Online : DmPresence.Offline,
                CannotSend = notConnected.Length > 0 ? notConnected : inRoom ? "" : string.Format(MessagesText.CannotSendLeftRoom, address.Resource),
                CanAttach = false,
            };
        }

        var contact = session.Contacts.FirstOrDefault(c => c.Jid == key.Peer);
        var shown   = contact is { Name.Length: > 0 } ? contact.Name : NameInRooms(session, key.Peer) ?? (name.Length > 0 ? name : key.Peer);
        var presence = contact is null ? DmPresence.Unknown : contact.Show switch
        {
            XmppShow.Offline => DmPresence.Offline,
            XmppShow.Online or XmppShow.Chat => DmPresence.Online,
            _ => DmPresence.Away,
        };

        return new DmSummary(key, shown, server, ShellText.NavJabber)
        {
            Presence = presence, Status = contact?.Status ?? "",
            CannotSend = notConnected,

            // ⚠️ Not a reason to stop. A Jabber server holds what is sent to someone away until they next connect.
            Note = connected && presence == DmPresence.Offline ? string.Format(MessagesText.NoteOfflineHeld, shown) : "",

            CanAttach = connected && session.UploadService is not null,
            MostFileBytes = session.UploadService?.MaxBytes ?? 0,
            HasContacts = true, IsContact = contact is not null,
        };
    }

    /// <summary>What someone goes by in a room this account is in, for one whose real address the room shows.</summary>
    private static string? NameInRooms(JabberSession session, string bareAddress) =>
        session.Locked(s => s.RoomMap.Values
            .SelectMany(r => r.Occupants.Values)
            .FirstOrDefault(o => !o.Self && XmppAddress.Parse(o.RealJid).Bare == bareAddress)?.Nick);

    public IReadOnlyList<DmPerson> OnlinePeople()
    {
        var found = new Dictionary<DmKey, DmPerson>();

        foreach (var session in _service.Sessions.Where(s => s.State == JabberState.Connected))
        {
            var server = session.Account.Name.Length > 0 ? session.Account.Name : session.Account.Address;
            var me = XmppAddress.Parse(session.Account.Address).Bare;

            foreach (var contact in session.Contacts.Where(c => c.Show != XmppShow.Offline && c.Jid != me))
            {
                var key = KeyOf(session.Account.Id, contact.Jid);
                found[key] = new DmPerson(key, contact.Name.Length > 0 ? contact.Name : contact.Jid, server, ShellText.NavJabber)
                {
                    Presence = contact.Show is XmppShow.Online or XmppShow.Chat ? DmPresence.Online : DmPresence.Away,
                };
            }

            // People in the rooms too, who need not be contacts. Where a room hides real addresses, they can only be
            // reached through that room, under the name they have there.
            var rooms = session.Locked(s => s.RoomMap.Values.Where(r => r.Joined)
                .Select(r => (r.Room, r.Nick, People: r.Occupants.Values.ToList())).ToList());

            foreach (var (room, nick, people) in rooms)
                foreach (var person in people.Where(o => !o.Self && !o.Left && o.Nick != nick))
                {
                    var real = XmppAddress.Parse(person.RealJid).Bare;
                    if (real == me) continue;

                    var key = KeyOf(session.Account.Id, real.Length > 0 ? real : $"{room.RoomJid}/{person.Nick}");
                    if (found.TryGetValue(key, out var known))
                    {
                        if (known.Name != person.Nick) found[key] = known with { OtherNames = $"{known.OtherNames} {person.Nick}" };
                        continue;
                    }

                    var where = real.Length > 0 ? server : $"{server} · {(room.Name.Length > 0 ? room.Name : XmppAddress.Parse(room.RoomJid).Local)}";
                    found[key] = new DmPerson(key, person.Nick, where, ShellText.NavJabber)
                    {
                        Presence = person.Show is XmppShow.Online or XmppShow.Chat ? DmPresence.Online : DmPresence.Away,
                    };
                }
        }

        return [.. found.Values];
    }

    public async Task<IReadOnlyList<DmLine>> LoadAsync(DmKey key, long beforeId, int limit, CancellationToken ct = default) =>
        [.. (await _service.LoadConversationAsync(key.ServerId, key.Peer, beforeId, limit, ct)).Select(ToLine)];

    public Task MarkReadAsync(DmKey key, CancellationToken ct = default) => _service.MarkReadAsync(key.ServerId, key.Peer, ct);

    private JabberSession SessionOf(DmKey key) =>
        _service.SessionOf(key.ServerId) ?? throw new XmppException(XmppFailure.Network, "Not connected.");

    public Task SendAsync(DmKey key, string text, CancellationToken ct = default) => _service.SendAsync(SessionOf(key), key.Peer, false, text, ct);

    public Task SetTypingAsync(DmKey key, bool typing, CancellationToken ct = default) =>
        _service.SessionOf(key.ServerId) is { } session ? _service.SendTypingAsync(session, key.Peer, typing, ct) : Task.CompletedTask;

    public Task SendFileAsync(DmKey key, Stream content, string fileName, long size, string contentType, CancellationToken ct = default) =>
        _service.SendFileAsync(SessionOf(key), key.Peer, false, content, fileName, size, contentType, ct);

    public Task AddContactAsync(DmKey key, CancellationToken ct = default) => _service.AddContactAsync(SessionOf(key), key.Peer, "", ct);

    public Task RemoveContactAsync(DmKey key, CancellationToken ct = default) => _service.RemoveContactAsync(SessionOf(key), key.Peer, ct);
}
