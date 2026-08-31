namespace EveConsole.Services;

/// <summary>
/// How much of a character's EVE-mail rate limit is left.
///
/// <para><b>⚠️ One bucket for everything.</b> Reading headers, fetching a body, sending, marking
/// read and managing labels all draw from <c>char-social</c> — 600 tokens per 15 minutes, per
/// character, confirmed from ESI's own spec. It is easy to look at the send endpoint alone and
/// conclude there is plenty of room; there is not, because the reads that find the mail to answer
/// come out of the same 600.</para>
///
/// <para><b>Why this exists.</b> A store's mailbox is open to other people. Somebody sending a
/// hundred mails — bored, malicious, or just a loop of their own — would have the shop answer all
/// of them, and every answer costs a read, a send and a mark-read. Without a ceiling that spends
/// the character's whole allowance in minutes, and what breaks is not the shop but everything
/// else that character does: its mail stops syncing, and so does anything else on char-social.</para>
///
/// <para><b>Who yields.</b> Consuming is unconditional — every call is recorded whether or not
/// there is room, because the token was spent regardless. Refusing is the CALLER's decision, and
/// only the automated shop makes it. A person reading their mail in the tool is never blocked by
/// this: if anything has to stop, it should be the robot answering strangers, not the owner
/// using their own client.</para>
/// </summary>
public sealed class MailBudget
{
    /// <summary>ESI's own figures for the char-social group.</summary>
    private const int MaxTokens = 600;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    /// <summary>
    /// What an automated store may spend, as a fraction of the whole.
    ///
    /// <para>⚠️ Deliberately not all of it. The ordinary mail poll, the Eve Mail tool and
    /// anything else the owner does share this allowance, and a shop that spent the lot would
    /// break those rather than itself. The remainder is their headroom, not slack.</para>
    /// </summary>
    private const double StoreShare = 0.6;

    private readonly Lock _gate = new();
    private readonly Dictionary<long, Bucket> _buckets = [];

    private sealed class Bucket
    {
        public double         Tokens   = MaxTokens;
        public DateTimeOffset LastFill = DateTimeOffset.UtcNow;
    }

    /// <summary>Records one call against a character's allowance.</summary>
    public void Spend(long characterId, int calls = 1)
    {
        lock (_gate)
        {
            var b = Refill(characterId);
            b.Tokens = Math.Max(0, b.Tokens - calls);
        }
    }

    /// <summary>
    /// Corrects the estimate from what ESI actually reported.
    ///
    /// <para>⚠️ Only ever downwards. The estimate starts full and cannot know what another client
    /// on the same token has already spent, so a lower figure from the server is the truth and a
    /// higher one is a stale header from a cached response.</para>
    /// </summary>
    public void Observe(long characterId, int? remaining)
    {
        if (remaining is not { } r) return;

        lock (_gate)
        {
            var b = Refill(characterId);
            if (r < b.Tokens) b.Tokens = r;
        }
    }

    /// <summary>Calls left in a character's allowance, as far as this knows.</summary>
    public int Remaining(long characterId)
    {
        lock (_gate) return (int)Refill(characterId).Tokens;
    }

    /// <summary>
    /// Whether an automated caller may spend <paramref name="calls"/> more.
    ///
    /// <para>Measured against the store's share rather than the whole, so a shop stops well
    /// before the character's mail does.</para>
    /// </summary>
    public bool StoreMayUse(long characterId, int calls)
    {
        var floor = MaxTokens * (1 - StoreShare);
        lock (_gate) return Refill(characterId).Tokens - calls >= floor;
    }

    /// <summary>For the status line — a number nobody has to reason about.</summary>
    public string Describe(long characterId) =>
        $"{Remaining(characterId)} of {MaxTokens} mail calls left in this 15-minute window";

    /// <summary>
    /// Tokens returned for the time that has passed. A rolling refill rather than a window that
    /// resets on the quarter hour: ESI's is a token bucket, and treating it as a step function
    /// would let a burst land just after a reset and be refused for the next fifteen minutes.
    /// </summary>
    private Bucket Refill(long characterId)
    {
        if (!_buckets.TryGetValue(characterId, out var b))
            _buckets[characterId] = b = new Bucket();

        var now     = DateTimeOffset.UtcNow;
        var elapsed = (now - b.LastFill).TotalSeconds;
        if (elapsed > 0)
        {
            b.Tokens   = Math.Min(MaxTokens, b.Tokens + elapsed * (MaxTokens / Window.TotalSeconds));
            b.LastFill = now;
        }
        return b;
    }
}
