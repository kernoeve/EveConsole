using EveConsole.Localization;
using EveConsole.Services.Pi;

namespace EveConsole.Services.Worklist;

/// <summary>
/// Planetary Industry work: extractors to restart, output to take off, input to bring, colonies to
/// set up, command centers to upgrade, and colonies whose data is too old to trust.
///
/// <para>Only characters that do PI (<see cref="PiCharacters"/>): <see cref="PiService"/> returns
/// no one else, so a character whose PI box is cleared raises nothing here. The lead times are the
/// ones the Overview alerts use (Settings → Industry), so a colony the alerts call "stopping" is
/// the colony this asks to have restarted. The alerts' own switches do not silence these: the
/// worklist has its own switch per source, and a task list that hides work because a banner was
/// turned off would be wrong in a way nobody could see.</para>
///
/// <para>The rules themselves are <see cref="PiTaskPlanner"/>, a pure function of the colonies, so
/// tools/PiEngineCheck can run them on constructed data.</para>
/// </summary>
public class PiGenerator(PiService pi) : IWorklistGenerator
{
    public const string SourceId = "planetary_industry";

    public string Id          => SourceId;
    public string DisplayName => WorklistText.SourcePlanetaryIndustry;

    public async Task<List<WorklistItem>> GenerateAsync(CancellationToken ct = default)
    {
        var now        = DateTimeOffset.UtcNow;
        var colonies   = await pi.ColoniesAsync(now, ct).ConfigureAwait(false);
        var characters = await pi.CharactersAsync(ct).ConfigureAwait(false);
        if (colonies.Count == 0 && characters.Count == 0) return [];

        var sd    = await pi.StaticDataAsync(ct).ConfigureAwait(false);
        var names = await pi.TypeNamesAsync(
            colonies.SelectMany(c => c.Forecast.Flows.Select(f => f.TypeId)), ct).ConfigureAwait(false);

        return PiTaskPlanner.Plan(colonies, characters, pi.Settings.Thresholds, sd, names);
    }
}

/// <summary>
/// Which colonies get which tasks. Pure: the colonies, forecast to now, in; tasks out.
///
/// <para><b>Priorities</b>, against <see cref="WorklistPriority"/>:</para>
/// <list type="bullet">
/// <item>A stopped extractor or a factory planet out of input is <see cref="WorklistPriority.Missing"/>:
/// something that should be running is not, and every hour of it is output that never exists. The
/// extractor stopping soon keeps the same priority and says <see cref="WorklistReadiness.Waiting"/>
/// instead — the readiness column is what tells "not yet" apart, as it does for a job.</item>
/// <item>Taking output off before storage fills is <see cref="WorklistPriority.HaulUnblocking"/>: a
/// full store stops whatever feeds it and throws away each cycle's output, which is exactly a haul
/// that something else is waiting on. Bringing input is <see cref="WorklistPriority.Missing"/> —
/// the factory is about to lack something it is declared to have.</item>
/// <item>Setting up a colony, upgrading a command center and opening a colony to refresh its data
/// are <see cref="WorklistPriority.Housekeeping"/>: real, worth doing, and costing nothing by
/// waiting a day.</item>
/// </list>
///
/// <para><b>Keys</b> name the character and the planet (or system, for a grouped haul) and the kind
/// of task — never a quantity or a time, so a task keeps its age and its snooze as the estimates
/// move.</para>
/// </summary>
public static class PiTaskPlanner
{
    public static List<WorklistItem> Plan(
        IReadOnlyList<PiColonyStatus>    colonies,
        IReadOnlyList<PiCharacterStatus> characters,
        PiThresholds                     t,
        PiStaticData                     sd,
        IReadOnlyDictionary<int, string> typeNames)
    {
        var items = new List<WorklistItem>();
        // Colonies that already have a task: doing any of them means opening the colony in game,
        // which refreshes its data, so a separate "open it" task would be the same visit twice.
        var tasked = new HashSet<(long, int)>();

        var judged = colonies
            .Select(c => (Colony: c, A: PiColonyAttention.For(c.Forecast, t)))
            .OrderBy(x => x.Colony.CharacterName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(x => x.Colony.PlanetId)
            .ToList();

        // ── Restart extractors ──────────────────────────────────────────────────────
        foreach (var (c, a) in judged)
        {
            if (!a.ExtractorsStopped && !a.ExtractorsStopping) continue;
            var stop   = a.ExtractorsStopAt!.Value;
            var planet = PiNames.Planet(c);
            items.Add(Base(c, "extractors") with
            {
                Kind      = WorklistKind.Pi,
                Title     = string.Format(WorklistText.PiRestartTitle, planet),
                Detail    = Stale(a, a.ExtractorsStopped
                    ? string.Format(WorklistText.PiRestartStoppedDetail, PiFormat.Duration(a.Now - stop), PiFormat.When(stop))
                    : string.Format(WorklistText.PiRestartStoppingDetail, PiFormat.Duration(stop - a.Now), PiFormat.When(stop))),
                Readiness = a.ExtractorsStopped ? WorklistReadiness.Ready : WorklistReadiness.Waiting,
                BlockedBy = a.ExtractorsStopped ? "" : string.Format(WorklistText.PiRestartWaiting, PiFormat.Duration(stop - a.Now)),
                Priority  = WorklistPriority.Missing,
            });
            tasked.Add((c.CharacterId, c.PlanetId));
        }

        // ── Take output off — one stop per character and system ─────────────────────
        var hauls = judged
            .Select(x => (x.Colony, x.A, OnHand: PiHauls.OutputOnHand(x.Colony.Forecast)))
            .Where(x => x.OnHand.Count > 0
                     && (x.A.StorageFull || x.A.StorageFilling || PiHauls.WorthCollecting(x.Colony.Forecast, sd)))
            .GroupBy(x => (x.Colony.CharacterId, x.Colony.SolarSystemId));

        foreach (var stop in hauls)
        {
            var list  = stop.OrderBy(x => x.Colony.PlanetId).ToList();
            var first = list[0].Colony;
            var system = PiNames.System(first.SolarSystemId, first.SystemName);
            var single = list.Count == 1;

            var reasons = list.Select(x =>
            {
                var planet = PiNames.Planet(x.Colony);
                return x.A.StorageFull
                    ? string.Format(WorklistText.PiHaulOutFull, planet)
                    : x.A.StorageFilling
                        ? string.Format(WorklistText.PiHaulOutFilling, planet, PiFormat.Duration(x.A.StorageFullAt!.Value - x.A.Now))
                        : string.Format(WorklistText.PiHaulOutCollect, planet);
            });

            var lines = list.SelectMany(x => x.OnHand)
                .GroupBy(kv => kv.Key)
                .Select(g => new WorklistLine(g.Key, PiNames.Type(g.Key, typeNames.GetValueOrDefault(g.Key)), g.Sum(kv => kv.Value)))
                .OrderBy(l => l.TypeName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            items.Add(new WorklistItem
            {
                Key           = $"pi:haul_out:{first.CharacterId}:{first.SolarSystemId}",
                Source        = PiGenerator.SourceId,
                Kind          = WorklistKind.Pi,
                Title         = single
                    ? string.Format(WorklistText.PiHaulOutTitle, PiNames.Planet(first))
                    : Plurals.Format(WorklistText.ResourceManager, nameof(WorklistText.PiHaulOutSystemOther), list.Count, system),
                Detail        = string.Join(CommonText.ListSeparator, reasons)
                                + (list.Any(x => x.A.Stale) ? " " + WorklistText.PiStaleNote : ""),
                Readiness     = WorklistReadiness.Ready,
                CharacterId   = first.CharacterId,
                CharacterName = first.CharacterName,
                // Where the stop is: the planet itself, or the system for several.
                LocationId    = single ? first.PlanetId : first.SolarSystemId,
                LocationName  = single ? PiNames.Planet(first) : system,
                Lines         = lines,
                TypeId        = lines.Count > 0 ? lines[0].TypeId : 0,
                TypeName      = lines.Count > 0 ? lines[0].TypeName : "",
                Priority      = WorklistPriority.HaulUnblocking,
                DataAsOf      = list.Min(x => x.Colony.Forecast.SnapshotAt),
            });
            foreach (var x in list) tasked.Add((x.Colony.CharacterId, x.Colony.PlanetId));
        }

        // ── Bring input — factory planets ───────────────────────────────────────────
        foreach (var (c, a) in judged)
        {
            if (c.Forecast.Kind != PiColonyKind.Factory || (!a.InputsOut && !a.InputsLow)) continue;
            var bring = PiHauls.InputToBring(c.Forecast, t.InputDays);
            if (bring.Count == 0) continue;

            var planet = PiNames.Planet(c);
            var when   = PiFormat.Duration(a.InputsRunOutAt!.Value - a.Now);
            var lines  = bring
                .Select(b => new WorklistLine(b.TypeId, PiNames.Type(b.TypeId, typeNames.GetValueOrDefault(b.TypeId)), b.Quantity))
                .OrderBy(l => l.TypeName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            items.Add(Base(c, "haul_in") with
            {
                Kind            = WorklistKind.Pi,
                Title           = string.Format(WorklistText.PiHaulInTitle, planet),
                Detail          = Stale(a, a.InputsOut
                    ? Plurals.Format(WorklistText.ResourceManager, nameof(WorklistText.PiHaulInOutDetailOther), t.InputDays, when)
                    : Plurals.Format(WorklistText.ResourceManager, nameof(WorklistText.PiHaulInLowDetailOther), t.InputDays, when)),
                // Brought from wherever the hauler keeps it, to the planet.
                LocationId      = 0,
                LocationName    = "",
                DestinationId   = c.PlanetId,
                DestinationName = planet,
                Lines           = lines,
                TypeId          = lines[0].TypeId,
                TypeName        = lines[0].TypeName,
                Priority        = WorklistPriority.Missing,
            });
            tasked.Add((c.CharacterId, c.PlanetId));
        }

        // ── Set up a colony, upgrade a command center — from the colony list ───────
        foreach (var ch in characters.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (ch.ColoniesFree > 0)
                items.Add(new WorklistItem
                {
                    Key           = $"pi:setup:{ch.CharacterId}",
                    Source        = PiGenerator.SourceId,
                    Kind          = WorklistKind.Pi,
                    Title         = Plurals.Format(WorklistText.ResourceManager, nameof(WorklistText.PiSetUpTitleOther), ch.ColoniesFree),
                    Detail        = string.Format(WorklistText.PiSetUpDetail, ch.ColoniesUsed, ch.ColoniesAllowed),
                    Readiness     = WorklistReadiness.Ready,
                    CharacterId   = ch.CharacterId,
                    CharacterName = ch.Name,
                    Priority      = WorklistPriority.Housekeeping,
                    // Whose slots: the character is the whole of the task.
                    IconUrl       = WorklistIcons.Portrait(ch.CharacterId),
                });

            foreach (var slot in ch.Colonies.Where(s => s.UpgradeHeadroom > 0))
            {
                var planet = PiNames.Planet(slot.PlanetId, slot.PlanetName, slot.SolarSystemId, slot.SystemName);
                items.Add(new WorklistItem
                {
                    Key           = $"pi:upgrade:{ch.CharacterId}:{slot.PlanetId}",
                    Source        = PiGenerator.SourceId,
                    Kind          = WorklistKind.Pi,
                    Title         = string.Format(WorklistText.PiUpgradeTitle, planet, slot.MaxUpgradeLevel),
                    Detail        = string.Format(WorklistText.PiUpgradeDetail, slot.UpgradeLevel, slot.MaxUpgradeLevel),
                    Readiness     = WorklistReadiness.Ready,
                    CharacterId   = ch.CharacterId,
                    CharacterName = ch.Name,
                    LocationId    = slot.PlanetId,
                    LocationName  = planet,
                    Priority      = WorklistPriority.Housekeeping,
                    IconUrl       = PlanetTypeIds.GetValueOrDefault(slot.PlanetType.ToLowerInvariant()) is int pt and > 0 ? WorklistIcons.Type(pt) : null,
                });
                tasked.Add((ch.CharacterId, slot.PlanetId));
            }
        }

        // ── Open a colony whose data is too old — when nothing else will open it ────
        foreach (var (c, a) in judged)
        {
            if (!a.Stale || tasked.Contains((c.CharacterId, c.PlanetId))) continue;
            var planet = PiNames.Planet(c);
            items.Add(Base(c, "stale") with
            {
                Kind     = WorklistKind.Pi,
                Title    = string.Format(WorklistText.PiOpenTitle, planet),
                Detail   = string.Format(WorklistText.PiOpenDetail, PiFormat.Duration(a.DataAge)),
                Priority = WorklistPriority.Housekeeping,
            });
        }

        return items;
    }

    /// <summary>A task about one colony, at the planet.</summary>
    private static WorklistItem Base(PiColonyStatus c, string kind) => new()
    {
        Key           = $"pi:{kind}:{c.CharacterId}:{c.PlanetId}",
        Source        = PiGenerator.SourceId,
        Kind          = WorklistKind.Pi,
        Title         = "",
        Readiness     = WorklistReadiness.Ready,
        CharacterId   = c.CharacterId,
        CharacterName = c.CharacterName,
        LocationId    = c.PlanetId,
        LocationName  = PiNames.Planet(c),
        DataAsOf      = c.Forecast.SnapshotAt,
        // The planet: its own SDE type, so a restart shows the barren rock or the gas giant it is
        // about. A task carrying an item (an input to bring) shows that instead.
        IconUrl       = c.PlanetTypeId > 0 ? WorklistIcons.Type(c.PlanetTypeId) : null,
    };

    /// <summary>
    /// The planet types by ESI's word for them, for a colony slot, which knows only the word. The
    /// SDE's own type ids ("Planet (Barren)" and so on); the colony's own type is used where known.
    /// </summary>
    private static readonly Dictionary<string, int> PlanetTypeIds = new()
    {
        ["temperate"] = 11, ["ice"] = 12, ["gas"] = 13, ["oceanic"] = 2014,
        ["lava"] = 2015, ["barren"] = 2016, ["storm"] = 2017, ["plasma"] = 2063,
    };

    /// <summary>The detail, with a note when the estimates behind it come from old data.</summary>
    private static string Stale(PiColonyAttention a, string detail)
        => a.Stale ? detail + " " + WorklistText.PiStaleNote : detail;
}
