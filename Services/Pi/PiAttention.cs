namespace EveConsole.Services.Pi;

/// <summary>How much a colony needs its owner, as the tool's status chip shows it.</summary>
public enum PiColonyState
{
    /// <summary>Nothing due within the lead times.</summary>
    Ok,
    /// <summary>Something comes due within its lead time, or the data is too old to trust.</summary>
    Attention,
    /// <summary>Something has already happened: extractors stopped, storage full, inputs gone.</summary>
    Action,
}

/// <summary>
/// What one colony needs, at the forecast's moment, judged against the lead times — the one
/// reading the PI tool's chips, the Overview alerts and the worklist tasks all share, so they
/// cannot disagree about whether a colony is a problem.
///
/// <para>⚠️ Extractor times are exact (fixed when the program was set). Storage and input times
/// are estimates replayed from the last time the colony was opened in game; anything showing them
/// says so.</para>
/// </summary>
public sealed record PiColonyAttention
{
    public required DateTimeOffset Now { get; init; }

    /// <summary>When the first extractor stops; null on a colony without one.</summary>
    public DateTimeOffset? ExtractorsStopAt { get; init; }
    public bool ExtractorsStopped  { get; init; }
    /// <summary>Not stopped yet, but stopping within the lead time.</summary>
    public bool ExtractorsStopping { get; init; }

    /// <summary>When storage, a launchpad or the command center first fills (estimated).</summary>
    public DateTimeOffset? StorageFullAt { get; init; }
    public bool StorageFull    { get; init; }
    public bool StorageFilling { get; init; }

    /// <summary>When the first brought-in input runs out (estimated); factory planets.</summary>
    public DateTimeOffset? InputsRunOutAt { get; init; }
    public bool InputsOut { get; init; }
    public bool InputsLow { get; init; }

    public TimeSpan DataAge { get; init; }
    /// <summary>Older than the staleness setting: the estimates above are no longer trustworthy.</summary>
    public bool Stale { get; init; }

    public PiColonyState State =>
        ExtractorsStopped || StorageFull || InputsOut                 ? PiColonyState.Action
        : ExtractorsStopping || StorageFilling || InputsLow || Stale ? PiColonyState.Attention
        :                                                               PiColonyState.Ok;

    /// <summary>The soonest of the three clocks, for sorting by what needs doing first; null when
    /// none of them runs out within the horizon.</summary>
    public DateTimeOffset? NextActionAt =>
        new[] { ExtractorsStopAt, StorageFullAt, InputsRunOutAt }.Where(t => t is not null).Min();

    /// <summary>Judges a colony forecast to now — its <see cref="PiColonyForecast.At"/> is the
    /// moment judged.</summary>
    public static PiColonyAttention For(PiColonyForecast f, PiThresholds t)
    {
        var now   = f.At;
        var stop  = f.Kind == PiColonyKind.Extractor ? f.ExtractorsStopAt : null;
        var full  = f.StorageFullAt;
        var empty = f.InputsRunOutAt;

        return new PiColonyAttention
        {
            Now              = now,
            ExtractorsStopAt = stop,
            ExtractorsStopped  = stop is { } s && s <= now,
            ExtractorsStopping = stop is { } s2 && s2 > now && s2 <= now + t.ExtractorLead,
            StorageFullAt    = full,
            StorageFull      = full is { } fu && fu <= now,
            StorageFilling   = full is { } fu2 && fu2 > now && fu2 <= now + t.StorageLead,
            InputsRunOutAt   = empty,
            InputsOut        = empty is { } e && e <= now,
            InputsLow        = empty is { } e2 && e2 > now && e2 <= now + t.InputLead,
            DataAge          = f.DataAge,
            Stale            = f.DataAge > t.StaleAfter,
        };
    }
}

/// <summary>What there is to take off a colony and what it needs brought to it.</summary>
public static class PiHauls
{
    /// <summary>
    /// Output on hand counts as worth collecting, whatever the clocks say, once it takes up this
    /// share of the storage it sits in: half a launchpad is a hold's worth, and the trip is the
    /// same whether it fills tomorrow or next week.
    /// </summary>
    public const double CollectAtFill = 0.5;

    /// <summary>The types a colony makes and does not use itself: what piles up to be taken off.
    /// A type that is also brought in is never output, however the flows balance.</summary>
    public static HashSet<int> OutputTypes(PiColonyForecast f)
        => f.Flows.Where(x => x.ExportedPerDay > 0 && x.ImportedPerDay <= 0).Select(x => x.TypeId).ToHashSet();

    /// <summary>Output on hand in storage, launchpads and the command center at the forecast's
    /// moment, by type (estimated after the snapshot).</summary>
    public static Dictionary<int, long> OutputOnHand(PiColonyForecast f)
    {
        var output = OutputTypes(f);
        var onHand = new Dictionary<int, long>();
        foreach (var s in f.Storage)
            foreach (var (type, amount) in s.ContentsAt)
                if (amount > 0 && output.Contains(type))
                    onHand[type] = onHand.GetValueOrDefault(type) + amount;
        return onHand;
    }

    /// <summary>
    /// Whether the output on hand fills at least <see cref="CollectAtFill"/> of the storage it is
    /// in — counted over the pins that hold any output, so an empty spare silo does not hide a
    /// full launchpad.
    /// </summary>
    public static bool WorthCollecting(PiColonyForecast f, PiStaticData sd)
    {
        var output = OutputTypes(f);
        double used = 0, capacity = 0;
        foreach (var s in f.Storage)
        {
            var mine = s.ContentsAt.Where(c => c.Value > 0 && output.Contains(c.Key)).ToList();
            if (mine.Count == 0 || s.Capacity <= 0) continue;
            used     += mine.Sum(c => c.Value * sd.VolumeOf(c.Key));
            capacity += s.Capacity;
        }
        return capacity > 0 && used / capacity >= CollectAtFill;
    }

    /// <summary>
    /// What to bring so the colony has <paramref name="days"/> days of each input it is brought:
    /// a day's use at full rate times the days, less what is already on hand (estimated). Types
    /// already covered for that long are left out.
    /// </summary>
    public static List<(int TypeId, long Quantity)> InputToBring(PiColonyForecast f, int days)
        => f.Inputs
            .Select(i => (i.TypeId, Quantity: Math.Max(0, (long)Math.Ceiling(i.PerDay * days) - i.OnHandAt)))
            .Where(i => i.Quantity > 0)
            .OrderBy(i => i.TypeId)
            .ToList();
}
