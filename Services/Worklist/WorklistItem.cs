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
/// What kind of doing a task is, independent of which generator raised it.
///
/// <para>Separated from the description so the list can be scanned by activity. Five sources
/// between them produced eight different verbs for four actual kinds of work — "Run job" and
/// "Start job" are both a job, "Place buy order", "Increase buy order", "Raise bid" and "Acquire
/// BPO/BPC" are all buying — and reading the kind out of prose is work the reader should not have
/// to do.</para>
/// </summary>
public enum WorklistKind { Buy, Haul, Job, CorpProject, AssetSafety, SkillQueue }

/// <summary>
/// One item on a task that moves or acquires several things at once.
///
/// <para><see cref="Volume"/> and <see cref="Value"/> are filled in by
/// <c>WorklistService.ApplyVolumeAsync</c> off the same single price and volume lookup the task's
/// own totals come from — a generator that priced its own lines would be a second opinion about
/// the same numbers, and the manifest's figures have to add up to the row's.</para>
/// </summary>
public sealed record WorklistLine(int TypeId, string TypeName, long Quantity)
{
    public double Volume { get; init; }
    public double Value  { get; init; }

    /// <summary>Blank rather than "0 ISK" when unpriced: a manifest line whose type has no price
    /// on file is not worth nothing, it is worth an amount nobody has quoted.</summary>
    public string ValueText  => Value  > 0 ? Isk(Value) : "";
    public string VolumeText => Volume > 0 ? M3(Volume) : "";

    public bool HasItemLink => TypeId > 0 && TypeName.Length > 0;
    public void OpenItem() => EveConsole.Services.EntityNavigator.Instance.Item(TypeId);

    // Same abbreviations MarketFmt uses, restated here rather than referenced: that lives in the
    // view-model layer, and a record in Services reaching up into it to format a string would
    // point the dependency the wrong way for two short methods.
    private static string Isk(double v) => v >= 1e12 ? $"{v / 1e12:N2}T"
                                         : v >= 1e9  ? $"{v / 1e9:N2}B"
                                         : v >= 1e6  ? $"{v / 1e6:N2}M"
                                         : v >= 1e3  ? $"{v / 1e3:N1}K"
                                         : v.ToString("N0");

    private static string M3(double v) => v >= 1_000_000 ? $"{v / 1_000_000:N1}M m³"
                                        : v >= 1_000     ? $"{v / 1_000:N0}k m³"
                                        : $"{v:N0} m³";
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

    /// <summary>What sort of doing this is. Shown in its own column, so the title need not
    /// repeat it — "Run job — 40 × Capital Cargo Bay" becomes Job / "40 × Capital Cargo Bay".</summary>
    public required WorklistKind Kind { get; init; }

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

    /// <summary>Where the thing is going. Only a haul has two ends; everything else happens in
    /// one place and leaves this empty.</summary>
    public long   DestinationId   { get; init; }
    public string DestinationName { get; init; } = "";

    public int    TypeId   { get; init; }
    public string TypeName { get; init; } = "";

    /// <summary>Units this task concerns, for the tasks that are about one item. Multi-item tasks
    /// carry <see cref="Lines"/> instead and leave this at zero.</summary>
    public long Quantity { get; init; }

    /// <summary>
    /// Everything a multi-item task covers, shown by expanding the row.
    ///
    /// <para>Held apart from the description because a hauler needs a manifest, not a sentence.
    /// Empty for tasks about a single item, and the row then has nothing to expand.</para>
    /// </summary>
    public IReadOnlyList<WorklistLine> Lines { get; init; } = [];

    /// <summary>
    /// Total m³ of what this task moves or buys. Filled by the service rather than by generators,
    /// so the volumes come from one lookup and cannot disagree between sources.
    /// </summary>
    public double Volume { get; init; }

    /// <summary>
    /// What the task's contents are worth, at the same prices the asset valuation uses. Filled by
    /// the service alongside <see cref="Volume"/>, from the same one lookup.
    /// </summary>
    public double Value { get; init; }

    /// <summary>
    /// Which slot pool a job occupies. Null for everything that is not a job — the three pools
    /// are separate capacity and a summary that lumped them would hide which one is full.
    /// </summary>
    public IndustryPool? Pool { get; init; }

    /// <summary>
    /// Material efficiency of the blueprint this job would actually install, or null where no
    /// blueprint is involved.
    ///
    /// <para>The materials were planned at this figure rather than at a default, so it is the
    /// number that explains the quantities on the row — a job planned against an ME10 original
    /// asks for less than the same job against a fresh copy, and without it the difference looks
    /// arbitrary.</para>
    /// </summary>
    public int? BlueprintMe { get; init; }

    /// <summary>Higher sorts first. Generators set this relative to their own items; the
    /// service does not renormalise across sources.</summary>
    public int Priority { get; init; }

    /// <summary>
    /// Marks items that are the same real-world purchase and should be shown as one.
    ///
    /// <para>Two generators can independently want the same thing at the same station — the job
    /// materials need 33,013 Dysprosium at Jita and an inventory rule wants another 109,268 — and
    /// as separate rows that reads as two errands when it is one order. The service folds every
    /// item sharing a key into a single task.</para>
    ///
    /// <para>⚠️ Their quantities are NOT added. Each generator has already subtracted the same
    /// stock from its own demand, so adding the two answers credits that stock twice — see
    /// <see cref="GrossDemand"/>.</para>
    ///
    /// <para>Null means never merge, which is the default and the right answer for anything that
    /// is not simply "acquire this many of this type here": a BPO/BPC bought on contract, or an
    /// order-maintenance task like raising a bid, which has no quantity to add up.</para>
    /// </summary>
    public string? MergeKey { get; init; }

    /// <summary>
    /// What this demand needs in total, before any supply is counted against it — and
    /// <see cref="SupplyCredited"/> is what it then subtracted.
    ///
    /// <para><b>⚠️ Why the merge cannot just add quantities.</b> Demands are additive; the stock
    /// that fills them is not. A job needing 540,933 gas and a rule wanting 500,000 on the shelf
    /// are two demands on one pile, and each generator independently subtracted the whole pile —
    /// the same 125,298 on hand, the same 12,886 on order, the same 333,374 recoverable from
    /// compressed. Adding the two answers gave 97,817 where the real requirement was 569,375: the
    /// entire supply had been credited twice.</para>
    ///
    /// <para>So a merging item reports both halves and the service nets once. Null on anything
    /// that cannot express itself that way, which falls back to adding — right for a lone item,
    /// and no worse than what it did before for anything else.</para>
    /// </summary>
    public long? GrossDemand { get; init; }

    /// <summary>
    /// The supply this item already subtracted from <see cref="GrossDemand"/> to reach its
    /// <see cref="Quantity"/> — on hand, on order, and anything recoverable.
    ///
    /// <para>⚠️ Counted ONCE across a merge, at the largest figure any contributor claimed, not
    /// summed. Two views of one pile are still one pile. Where the contributors' scopes differ the
    /// largest view is an approximation — it is the pile most of them can reach — but it can never
    /// credit more stock than genuinely exists, which is the direction that matters.</para>
    /// </summary>
    public long? SupplyCredited { get; init; }

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

    /// <summary>
    /// The key under which quantity purchases of one type at one station combine. Station and
    /// type, and nothing else — buying the same thing somewhere else is a different errand, and
    /// which generator asked for it is exactly the distinction the reader does not care about.
    /// </summary>
    public static string BuyMergeKey(long locationId, int typeId) => $"buy:{locationId}:{typeId}";

    /// <summary>
    /// A short marker kept on the front of the title through a merge, for the one distinction the
    /// item name and the kind column cannot carry between them.
    ///
    /// <para>Only "BPO/BPC" uses it: a blueprint is acquired on contract rather than ordered on
    /// the market, and merging a job's demand for one with a stocking rule's must not lose that.
    /// Null everywhere else.</para>
    /// </summary>
    public string? TitleTag { get; init; }
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
