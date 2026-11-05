using EveConsole.Localization;
using EveConsole.Models;
using EveConsole.Services.Mumble;

namespace EveConsole.Services.Messages;

/// <summary>Mumble's private messages for the Messages window. Channel chat stays in the Mumble tool.</summary>
public sealed class MumbleDmSource : IDmSource
{
    public const string Name = "mumble";

    private readonly MumbleService _service;

    public string Source => Name;

    public event Action? Changed;
    public event Action? UnreadChanged;
    public event Action<DmKey, DmLine, bool>? Arrived;
    public event Action<DmKey>? Edited { add { } remove { } }     // Mumble has no way to change what was said.
    public event Action<DmKey, bool>? Typing { add { } remove { } }

    public MumbleDmSource(MumbleService service)
    {
        _service = service;
        _service.SessionChanged += _ => Changed?.Invoke();
        _service.DirectUnreadChanged += () => UnreadChanged?.Invoke();
        _service.DirectStored += row =>
            Arrived?.Invoke(new DmKey(Name, row.ServerId, row.PeerKey), ToLine(row), row is { IsAlert: true, IsRead: false });
    }

    private static DmLine ToLine(MumbleDirectMessage m) => new(m.Id, m.Sender, m.Body, m.SentAt, m.IsOutgoing);

    public async Task<IReadOnlyList<DmSummary>> ConversationsAsync(CancellationToken ct = default)
    {
        var rows = await _service.DirectSummariesAsync(ct);
        return [.. rows.Select(r => Describe(new DmKey(Name, r.ServerId, r.PeerKey), r.PeerName) with
        {
            LastText = r.LastText, LastAt = r.LastAt, Unread = r.Unread, Alerts = r.Unread,
        })];
    }

    public DmSummary Describe(DmKey key, string name)
    {
        var session = _service.SessionOf(key.ServerId);
        var server  = session is null ? "" : session.Server.Name.Length > 0 ? session.Server.Name : session.Server.Host;
        if (session is null) return new DmSummary(key, name, server, ShellText.NavMumble) { CannotSend = string.Format(MessagesText.CannotSendNotConnected, ShellText.NavMumble) };

        var connected = session.State == MumbleState.Connected;
        var person    = connected ? _service.PersonOf(key.ServerId, key.Peer) : null;

        return new DmSummary(key, person?.Name ?? name, server, ShellText.NavMumble)
        {
            Presence = !connected ? DmPresence.Unknown : person is null ? DmPresence.Offline : DmPresence.Online,

            // ⚠️ Mumble cannot do what Jabber can: a server only delivers to who is connected right now, and holds nothing.
            CannotSend = !connected ? string.Format(MessagesText.CannotSendNotConnected, server)
                       : person is null ? string.Format(MessagesText.CannotSendOffline, name)
                       : "",
        };
    }

    public async Task<IReadOnlyList<DmLine>> LoadAsync(DmKey key, long beforeId, int limit, CancellationToken ct = default) =>
        [.. (await _service.LoadDirectAsync(key.ServerId, key.Peer, beforeId, limit, ct)).Select(ToLine)];

    public Task MarkReadAsync(DmKey key, CancellationToken ct = default) => _service.MarkDirectReadAsync(key.ServerId, key.Peer, ct);

    public Task SendAsync(DmKey key, string text, CancellationToken ct = default) => _service.SendDirectAsync(key.ServerId, key.Peer, text, ct);

    public Task SetTypingAsync(DmKey key, bool typing, CancellationToken ct = default) => Task.CompletedTask;

    public Task SendFileAsync(DmKey key, Stream content, string fileName, long size, string contentType, CancellationToken ct = default) =>
        throw new NotSupportedException("Mumble has no way to send a file.");

    public Task AddContactAsync(DmKey key, CancellationToken ct = default) => Task.CompletedTask;

    public Task RemoveContactAsync(DmKey key, CancellationToken ct = default) => Task.CompletedTask;
}
