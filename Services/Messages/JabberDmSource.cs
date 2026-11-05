using EveConsole.Localization;
using EveConsole.Models;
using EveConsole.Services.Jabber;

namespace EveConsole.Services.Messages;

/// <summary>Jabber's direct messages for the Messages window. Rooms stay in the Jabber tool.</summary>
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

    private static DmLine ToLine(JabberStoredMessage m) => new(m.Id, m.Sender, m.Body, m.SentAt, m.IsOutgoing);

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
        if (address.Resource.Length > 0 && session.Rooms.FirstOrDefault(r => r.Room.RoomJid == address.Bare) is var room && room is not null || address.Resource.Length > 0)
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
        var shown   = contact is { Name.Length: > 0 } ? contact.Name : key.Peer;
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
