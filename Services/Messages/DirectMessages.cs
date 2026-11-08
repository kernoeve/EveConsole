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

/// <param name="Deleted">See <see cref="EveConsole.Models.JabberDeleted"/>: deleted messages keep their place.</param>
/// <param name="Target">What a reaction to it names: its own id. Blank where reactions are not a thing.</param>
/// <param name="ReplyId">What it answers, by <see cref="Target"/>. Blank for none.</param>
/// <param name="SeenByThem">This account's, and the person has seen it.</param>
public sealed record DmLine(long Id, string Sender, string Body, string SentAt, bool IsOutgoing, int Deleted = 0, string DeletedReason = "", string Target = "",
    string ReplyId = "", string ReplyQuote = "", bool SeenByThem = false);

/// <summary>Someone who can be written to right now, to start a conversation with.</summary>
public sealed record DmPerson(DmKey Key, string Name, string Server, string Service)
{
    public DmPresence Presence { get; init; } = DmPresence.Online;

    /// <summary>Other names they go by, which find them too: a saved contact may be known by another name in a room.</summary>
    public string OtherNames { get; init; } = "";
}

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

    /// <summary>Someone's reactions in a conversation changed.</summary>
    event Action<DmKey>? ReactionsChanged;

    /// <summary>The reactions to some of a conversation's messages, by what <see cref="DmLine.Target"/> each has.</summary>
    Task<Dictionary<string, List<EveConsole.Services.Jabber.JabberReactionCount>>> ReactionsAsync(DmKey key, IReadOnlyCollection<string> targets, CancellationToken ct = default) =>
        Task.FromResult(new Dictionary<string, List<EveConsole.Services.Jabber.JabberReactionCount>>());

    /// <summary>Whether this account's messages can be corrected and taken back, and answered as replies.</summary>
    bool CanEditMessages => false;

    /// <summary>Sends words that correct this account's message, or answer one, as <see cref="EveConsole.Services.Jabber.XmppSendExtras"/> says.</summary>
    Task SendAsync(DmKey key, string text, EveConsole.Services.Jabber.XmppSendExtras extras, CancellationToken ct = default) => SendAsync(key, text, ct);

    /// <summary>Takes back one of this account's messages, for everyone.</summary>
    Task DeleteOwnAsync(DmKey key, string ownId, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>Sends this person's whole set of reactions to one message.</summary>
    Task ReactAsync(DmKey key, string target, IReadOnlyList<string> emojis, CancellationToken ct = default) => Task.CompletedTask;

    event Action<DmKey, bool>? Typing;

    Task<IReadOnlyList<DmSummary>> ConversationsAsync(CancellationToken ct = default);

    /// <summary>How someone stands now, for a conversation that may have nothing saved yet.</summary>
    DmSummary Describe(DmKey key, string name);

    /// <summary>Everyone online on every server connected right now. Each once, and never this person themselves.</summary>
    IReadOnlyList<DmPerson> OnlinePeople();

    Task<IReadOnlyList<DmLine>> LoadAsync(DmKey key, long beforeId, int limit, CancellationToken ct = default);
    Task MarkReadAsync(DmKey key, CancellationToken ct = default);
    Task SendAsync(DmKey key, string text, CancellationToken ct = default);
    Task SetTypingAsync(DmKey key, bool typing, CancellationToken ct = default);
    Task SendFileAsync(DmKey key, Stream content, string fileName, long size, string contentType, CancellationToken ct = default);
    Task AddContactAsync(DmKey key, CancellationToken ct = default);
    Task RemoveContactAsync(DmKey key, CancellationToken ct = default);

    /// <summary>What this person goes by on that server, for their own picture's initial where they have none.</summary>
    string MyName(int serverId) => "";
}

/// <summary>Every service's direct messages as one list.</summary>
public sealed class DirectMessagesService
{
    private readonly Dictionary<string, IDmSource> _sources;

    public event Action? Changed;
    public event Action? UnreadChanged;
    public event Action<DmKey, DmLine, bool>? Arrived;
    public event Action<DmKey>? Edited;
    public event Action<DmKey>? ReactionsChanged;
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
            source.ReactionsChanged += key => ReactionsChanged?.Invoke(key);
            source.Typing += (key, typing) => Typing?.Invoke(key, typing);
        }
    }

    public IDmSource? SourceOf(DmKey key) => _sources.GetValueOrDefault(key.Source);

    /// <summary>Everyone online on anything connected whose name or server has these words in it, by name.</summary>
    public IReadOnlyList<DmPerson> FindOnline(string words)
    {
        words = words.Trim();
        return [.. _sources.Values.SelectMany(s => s.OnlinePeople())
            .Where(p => words.Length == 0
                     || p.Name.Contains(words, StringComparison.CurrentCultureIgnoreCase)
                     || p.Server.Contains(words, StringComparison.CurrentCultureIgnoreCase)
                     || p.OtherNames.Contains(words, StringComparison.CurrentCultureIgnoreCase)
                     || p.Key.Peer.Contains(words, StringComparison.CurrentCultureIgnoreCase))
            .OrderBy(p => !p.Name.StartsWith(words, StringComparison.CurrentCultureIgnoreCase))
            .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(p => p.Server, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>Everything saved, newest first.</summary>
    public async Task<IReadOnlyList<DmSummary>> ConversationsAsync(CancellationToken ct = default)
    {
        var lists = await Task.WhenAll(_sources.Values.Select(s => s.ConversationsAsync(ct)));
        return [.. lists.SelectMany(l => l).OrderByDescending(c => c.SortAt, StringComparer.Ordinal)];
    }
}
