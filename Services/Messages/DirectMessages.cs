namespace EveConsole.Services.Messages;

public enum DmPresence { Unknown, Online, Away, Offline }

/// <summary>One conversation with one person, on one server of one service.</summary>
public sealed record DmKey(string Source, int ServerId, string Peer)
{
    public string Id => $"{Source}|{ServerId}|{Peer}";

    /// <summary>A key as <see cref="Id"/> wrote it, or null for anything else.</summary>
    public static DmKey? Parse(string? id)
    {
        var parts = id?.Split('|', 3);
        return parts is { Length: 3 } && int.TryParse(parts[1], out var server) ? new DmKey(parts[0], server, parts[2]) : null;
    }
}

/// <summary>How a conversation stands right now, whichever service it is on.</summary>
public sealed record DmSummary(DmKey Key, string Name, string Server, string Service)
{
    public DmPresence Presence { get; init; }
    public string Status { get; init; } = "";

    /// <summary>Why nothing can be sent right now. Blank when it can.</summary>
    public string CannotSend { get; init; } = "";

    /// <summary>Something worth knowing before sending, which does not stop it.</summary>
    public string Note { get; init; } = "";

    public string LastText { get; init; } = "";
    public string LastAt { get; init; } = "";

    /// <summary>For putting the newest first across services, whose ids mean nothing to each other.</summary>
    public string SortAt => LastAt;

    public int Unread { get; init; }
    public int Alerts { get; init; }

    public bool CanAttach { get; init; }
    public long MostFileBytes { get; init; }

    /// <summary>Whether this service has contacts at all, and this person is one.</summary>
    public bool HasContacts { get; init; }
    public bool IsContact { get; init; }
}

public sealed record DmLine(long Id, string Sender, string Body, string SentAt, bool IsOutgoing);

/// <summary>
/// A service people can be messaged on. ⚠️ Everything the Messages window knows goes through this, so a new service
/// needs only one of these and nothing on the screen changes.
/// </summary>
public interface IDmSource
{
    string Source { get; }

    /// <summary>Someone's state or a connection changed, so what the list shows may have.</summary>
    event Action? Changed;

    event Action? UnreadChanged;

    /// <summary>A line was saved. The flag says it calls for attention.</summary>
    event Action<DmKey, DmLine, bool>? Arrived;

    /// <summary>Existing lines changed, as when someone corrects a message.</summary>
    event Action<DmKey>? Edited;

    event Action<DmKey, bool>? Typing;

    Task<IReadOnlyList<DmSummary>> ConversationsAsync(CancellationToken ct = default);

    /// <summary>How someone stands now, for a conversation that may have nothing saved yet.</summary>
    DmSummary Describe(DmKey key, string name);

    Task<IReadOnlyList<DmLine>> LoadAsync(DmKey key, long beforeId, int limit, CancellationToken ct = default);
    Task MarkReadAsync(DmKey key, CancellationToken ct = default);
    Task SendAsync(DmKey key, string text, CancellationToken ct = default);
    Task SetTypingAsync(DmKey key, bool typing, CancellationToken ct = default);
    Task SendFileAsync(DmKey key, Stream content, string fileName, long size, string contentType, CancellationToken ct = default);
    Task AddContactAsync(DmKey key, CancellationToken ct = default);
    Task RemoveContactAsync(DmKey key, CancellationToken ct = default);
}

/// <summary>Every service's direct messages as one list.</summary>
public sealed class DirectMessagesService
{
    private readonly Dictionary<string, IDmSource> _sources;

    public event Action? Changed;
    public event Action? UnreadChanged;
    public event Action<DmKey, DmLine, bool>? Arrived;
    public event Action<DmKey>? Edited;
    public event Action<DmKey, bool>? Typing;

    public DirectMessagesService(IEnumerable<IDmSource> sources)
    {
        _sources = sources.ToDictionary(s => s.Source);
        foreach (var source in _sources.Values)
        {
            source.Changed += () => Changed?.Invoke();
            source.UnreadChanged += () => UnreadChanged?.Invoke();
            source.Arrived += (key, line, alert) => Arrived?.Invoke(key, line, alert);
            source.Edited += key => Edited?.Invoke(key);
            source.Typing += (key, typing) => Typing?.Invoke(key, typing);
        }
    }

    public IDmSource? SourceOf(DmKey key) => _sources.GetValueOrDefault(key.Source);

    /// <summary>Everything saved, newest first.</summary>
    public async Task<IReadOnlyList<DmSummary>> ConversationsAsync(CancellationToken ct = default)
    {
        var lists = await Task.WhenAll(_sources.Values.Select(s => s.ConversationsAsync(ct)));
        return [.. lists.SelectMany(l => l).OrderByDescending(c => c.SortAt, StringComparer.Ordinal)];
    }
}
