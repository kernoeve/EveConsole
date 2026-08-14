namespace EveConsole.Services.Worklist;

/// <summary>
/// Whether the player can act on an item right now.
///
/// This is the reason the tool exists: the cost being paid today is logging in to do something
/// and only then discovering the inputs are three jumps away. An item that cannot say whether
/// it is actionable has not solved anything.
/// </summary>
public enum WorklistReadiness
{
    /// <summary>Log in and do it.</summary>
    Ready,

    /// <summary>Something has to happen first, named in <see cref="WorklistItem.BlockedBy"/>.</summary>
    Blocked,

    /// <summary>Only time stands in the way — a job finishing, an order expiring.</summary>
    Waiting,
}

/// <summary>
/// One suggested piece of work.
///
/// Never stored. Every refresh regenerates the whole list from live data, so an item stops
/// appearing the moment its condition clears — that is the completion mechanism, and it means
/// there is no task state to drift out of step with the game.
///
/// <para><see cref="Key"/> carries the weight of that design. It must be derived purely from
/// what the item is about, so the same suggestion produces the same key on every refresh;
/// snoozing and "how long has this been sitting here" are both keyed off it. Anything varying
/// per run — a timestamp, a row id, a quantity that drifts — must stay out of it.</para>
/// </summary>
public sealed record WorklistItem
{
    /// <summary>Stable across refreshes. See the note on the type.</summary>
    public required string Key { get; init; }

    /// <summary>Which generator produced this, for grouping and filtering.</summary>
    public required string Source { get; init; }

    public required string Title  { get; init; }

    /// <summary>The specifics — quantity, price, how far off it is.</summary>
    public string Detail { get; init; } = "";

    public WorklistReadiness Readiness { get; init; } = WorklistReadiness.Ready;

    /// <summary>What is in the way. Only meaningful when not <see cref="WorklistReadiness.Ready"/>.</summary>
    public string BlockedBy { get; init; } = "";

    // Who and where. Both may be unset when a generator cannot route the item — an unrouted
    // item is still worth showing, with the gap made obvious rather than hidden.
    public long   CharacterId   { get; init; }
    public string CharacterName { get; init; } = "";
    public long   LocationId    { get; init; }
    public string LocationName  { get; init; } = "";

    public int    TypeId   { get; init; }
    public string TypeName { get; init; } = "";

    /// <summary>Higher sorts first. Generators set this relative to their own items; the
    /// service does not renormalise across sources.</summary>
    public int Priority { get; init; }

    /// <summary>
    /// How stale the data behind this item is. Detection runs off polled ESI data, so a
    /// suggestion can be minutes behind reality — showing the age is what stops the tool
    /// from recreating the very problem it was built to remove.
    /// </summary>
    public DateTimeOffset? DataAsOf { get; init; }

    /// <summary>Set by the service from stored state, not by generators.</summary>
    public DateTimeOffset? FirstSeenAt  { get; init; }
    public DateTimeOffset? SnoozedUntil { get; init; }

    public bool IsSnoozed => SnoozedUntil is { } s && s > DateTimeOffset.UtcNow;
}

/// <summary>
/// A source of worklist items.
///
/// Generators are deliberately dumb about presentation and about each other: each answers "what
/// should be done, given current state" for its own slice, and the service handles ordering,
/// snoozing and age. A generator that throws is reported and skipped rather than taking the
/// whole list down with it — a broken rule should cost you one section, not the tool.
/// </summary>
public interface IWorklistGenerator
{
    /// <summary>Stable identifier, used as the item <see cref="WorklistItem.Source"/>.</summary>
    string Id { get; }

    /// <summary>Shown as the section heading.</summary>
    string DisplayName { get; }

    Task<List<WorklistItem>> GenerateAsync(CancellationToken ct = default);
}
