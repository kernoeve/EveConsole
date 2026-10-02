using System.Globalization;
using System.Text.Json;
using EveConsole.Localization;
using EveConsole.Services.Pi;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Alarms.Conditions;

/// <summary>
/// Planetary Industry: an extractor stopped or stopping, storage full, or a factory planet's input
/// running out — on the colonies of the characters that do PI.
///
/// <para>One match per colony and event. The key names the character, the planet and what pins
/// the event in time: an extractor's program end (exact, fixed when the program was set), or the
/// snapshot a storage or input estimate was replayed from. So a colony sitting stopped is
/// announced once, re-running its extractors is a new program and news again, and opening the
/// colony in game — a newer snapshot — re-judges its estimates once.</para>
/// </summary>
public sealed class PiCondition : IAlarmCondition
{
    public string TypeKey     => "pi";
    public string DisplayName => "Planetary Industry";

    public string Description =>
        "Fires for the Planetary Industry colonies of characters that do PI: an extractor program " +
        "that has ended or ends within the lead time (exact), or storage or a launchpad full, or a " +
        "factory planet's brought-in input running out, within the lead time (both estimated from the " +
        "last time the colony was opened in game). Each colony's event fires once.";

    public object ParameterSchema => new
    {
        type = "object",
        properties = new
        {
            watch = new
            {
                type        = "string",
                @enum       = new[] { "all", "extractors", "storage", "inputs" },
                description = "What to watch for. Default all.",
            },
            lead_hours = new
            {
                type        = "integer",
                description = "Fire this many hours before it happens. Default 0: when it happens.",
            },
        },
    };

    // The editor's words; the three above are the agent's and stay English.
    public string ScreenName        => AlarmsText.CheckPi;
    public string ScreenDescription => AlarmsText.CheckPiNote;

    public AlarmFieldText? ScreenField(string property) => property switch
    {
        "watch"      => new(AlarmsText.PiWatchLabel, AlarmsText.PiWatchNote),
        "lead_hours" => new(AlarmsText.PiLeadLabel,  AlarmsText.PiLeadNote, AlarmsText.PiLeadSuffix),
        _            => null,
    };

    public string? ScreenOption(string property, string value) => (property, value) switch
    {
        ("watch", "all")        => AlarmsText.OptionPiAll,
        ("watch", "extractors") => AlarmsText.OptionPiExtractors,
        ("watch", "storage")    => AlarmsText.OptionPiStorage,
        ("watch", "inputs")     => AlarmsText.OptionPiInputs,
        _                       => null,
    };

    public string Describe(JsonElement config)
    {
        var what = ReadWatch(config) switch
        {
            "extractors" => "extractors stopping",
            "storage"    => "storage filling",
            "inputs"     => "inputs running out",
            _            => "extractors, storage and inputs",
        };
        var lead = ReadLeadHours(config);
        return lead > 0 ? $"PI: {what}, {lead} h ahead" : $"PI: {what}";
    }

    public (string Title, string Body) DefaultText(
        string alarmName, JsonElement config, IReadOnlyList<AlarmMatch> matches)
        => (Plurals.Format(AlarmsText.ResourceManager, nameof(AlarmsText.PiTitleOther), matches.Count),
            IAlarmCondition.JoinSummaries(matches));

    // The SDE's PI data changes only with an SDE import; one read serves every alarm for a while.
    private static readonly SemaphoreSlim StaticGate = new(1, 1);
    private static PiStaticData? _static;
    private static DateTimeOffset _staticAt;

    public async Task<IReadOnlyList<AlarmMatch>> EvaluateAsync(
        JsonElement config, AlarmEvaluationContext ctx, CancellationToken ct = default)
    {
        var watch = ReadWatch(config);
        var lead  = TimeSpan.FromHours(ReadLeadHours(config));

        await using var db = await ctx.DbFactory.CreateDbContextAsync(ct);
        var ids = await PiCharacters.IdsAsync(db, ct);
        if (ids.Count == 0) return [];
        var layouts = await PiLayoutStore.LoadAsync(db, ids, ct);
        if (layouts.Count == 0) return [];

        var sd = await StaticAsync(db, ct);

        var idList  = ids.ToList();
        var names   = await db.Characters.AsNoTracking().Where(c => idList.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var planetIds = layouts.Select(l => (long)l.PlanetId).Distinct().ToList();
        var planets = await db.SdeCelestials.AsNoTracking().Where(c => planetIds.Contains(c.ItemId))
            .ToDictionaryAsync(c => c.ItemId, c => c.Name, ct);

        var matches = new List<AlarmMatch>();
        foreach (var layout in layouts)
        {
            var f      = PiEngine.Forecast(layout, sd, ctx.Now);
            var who    = names.GetValueOrDefault(layout.CharacterId, layout.CharacterId.ToString(CultureInfo.InvariantCulture));
            var planet = planets.GetValueOrDefault(layout.PlanetId, $"planet {layout.PlanetId}");
            var where  = $"{planet} ({who})";
            var snap   = Stamp(f.SnapshotAt);

            if (watch is "all" or "extractors" && f.Kind == PiColonyKind.Extractor
                && f.ExtractorsStopAt is { } stop && stop <= ctx.Now + lead)
                matches.Add(Match("extractors", layout, Stamp(stop), stop <= ctx.Now
                    ? $"Extractors on {where} stopped at {Show(stop)}"
                    : $"Extractors on {where} stop at {Show(stop)}", stop));

            if (watch is "all" or "storage" && f.StorageFullAt is { } full && full <= ctx.Now + lead)
                matches.Add(Match("storage", layout, snap, full <= ctx.Now
                    ? $"Storage on {where} is full (estimated, since {Show(full)})"
                    : $"Storage on {where} fills at {Show(full)} (estimated)", full));

            if (watch is "all" or "inputs" && f.Kind == PiColonyKind.Factory
                && f.InputsRunOutAt is { } empty && empty <= ctx.Now + lead)
                matches.Add(Match("inputs", layout, snap, empty <= ctx.Now
                    ? $"Input on {where} ran out (estimated, at {Show(empty)})"
                    : $"Input on {where} runs out at {Show(empty)} (estimated)", empty));
        }
        return matches;
    }

    private static AlarmMatch Match(string kind, PiColonyLayout l, string stamp, string summary, DateTimeOffset when)
        => new($"pi:{kind}:{l.CharacterId}:{l.PlanetId}:{stamp}", summary)
        {
            Detail = new Dictionary<string, object?>
            {
                ["kind"]         = kind,
                ["character_id"] = l.CharacterId,
                ["planet_id"]    = l.PlanetId,
                ["at"]           = when.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                ["estimated"]    = kind != "extractors",
            },
        };

    private static async Task<PiStaticData> StaticAsync(Data.AppDbContext db, CancellationToken ct)
    {
        await StaticGate.WaitAsync(ct);
        try
        {
            if (_static is null || DateTimeOffset.UtcNow - _staticAt > TimeSpan.FromMinutes(10))
            {
                _static   = await PiStaticDataLoader.LoadAsync(db, ct);
                _staticAt = DateTimeOffset.UtcNow;
            }
            return _static;
        }
        finally { StaticGate.Release(); }
    }

    /// <summary>UTC, so a key never changes with the machine's zone.</summary>
    private static string Stamp(DateTimeOffset t)
        => t.ToUniversalTime().ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);

    private static string Show(DateTimeOffset t)
        => FormattableString.Invariant($"{t.ToUniversalTime():ddd d MMM HH:mm} EVE");

    private static string ReadWatch(JsonElement config)
    {
        var s = config.ValueKind == JsonValueKind.Object && config.TryGetProperty("watch", out var p)
                && p.ValueKind == JsonValueKind.String ? p.GetString()?.Trim().ToLowerInvariant() : null;
        return s is "extractors" or "storage" or "inputs" ? s : "all";
    }

    /// <summary>The editor writes every field as text, the agent a number: both are read.</summary>
    private static int ReadLeadHours(JsonElement config)
    {
        if (config.ValueKind != JsonValueKind.Object || !config.TryGetProperty("lead_hours", out var p)) return 0;
        var v = p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var n) ? n
              : p.ValueKind == JsonValueKind.String && int.TryParse(p.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s
              : 0;
        return Math.Clamp(v, 0, PiSettings.MaxLeadHours);
    }
}
