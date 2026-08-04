using System.Globalization;
using System.Text.Json;

namespace EveConsole.Alarms.Conditions;

/// <summary>
/// Fires at a wall-clock instant, optionally repeating on a fixed interval.
///
/// <para>Deliberately stateless: the occurrences of a repeating timer are <c>at + k·every</c>,
/// so each one has a name of its own and the seen-key ledger decides which have already been
/// announced. Nothing needs to be written back after firing, and a timer that came due while
/// the app was closed still fires on the next start rather than being silently skipped.</para>
/// </summary>
public sealed class TimerCondition : IAlarmCondition
{
    /// <summary>How far back a repeating timer will look for occurrences it has not announced.</summary>
    private static readonly TimeSpan Lookback = TimeSpan.FromHours(24);

    /// <summary>Ceiling on occurrences returned in one pass, so a long outage cannot flood.</summary>
    private const int MaxOccurrences = 50;

    public string TypeKey     => "timer";
    public string DisplayName => "Date / time";

    public string Description =>
        "Fires at a specific date and time, and optionally repeats on a fixed interval " +
        "thereafter. Use for reminders and anything scheduled. An occurrence that came due " +
        "while the app was closed fires at the next start rather than being skipped.";

    public object ParameterSchema => new
    {
        type = "object",
        properties = new
        {
            at = new
            {
                type   = "string",
                format = "date-time",          // makes the editor render a date + time picker
                description = "When to fire, ISO 8601. Include an offset (e.g. 2026-08-04T18:00:00-05:00) " +
                              "or a trailing Z for UTC; a bare local time is read as the machine's local time.",
            },
            repeat_every_seconds = new
            {
                type        = "integer",
                description = "Optional. Repeat this often after the first firing. Omit or 0 for a one-time timer.",
            },
        },
        required = new[] { "at" },
    };

    public string Describe(JsonElement config)
    {
        if (!TryReadAt(config, out var at)) return "Timer (not configured)";

        var when   = at.ToLocalTime().ToString("ddd d MMM yyyy HH:mm", CultureInfo.CurrentCulture);
        var every  = ReadRepeatSeconds(config);
        return every > 0
            ? $"At {when}, then every {DescribeInterval(every)}"
            : $"At {when}";
    }

    public Task<IReadOnlyList<AlarmMatch>> EvaluateAsync(
        JsonElement config, AlarmEvaluationContext ctx, CancellationToken ct = default)
    {
        if (!TryReadAt(config, out var at))
            return Task.FromResult<IReadOnlyList<AlarmMatch>>([]);

        var now   = ctx.Now;
        var every = ReadRepeatSeconds(config);

        if (every <= 0)
        {
            return Task.FromResult<IReadOnlyList<AlarmMatch>>(
                now >= at ? [Occurrence(at)] : []);
        }

        if (now < at) return Task.FromResult<IReadOnlyList<AlarmMatch>>([]);

        // Walk back from the most recent occurrence rather than forward from the first, so an
        // alarm that has been repeating for months does not iterate its whole history.
        var interval = TimeSpan.FromSeconds(every);
        var elapsed  = now - at;
        var newest   = (long)(elapsed.Ticks / interval.Ticks);
        var earliest = now - Lookback;

        var matches = new List<AlarmMatch>();
        for (var k = newest; k >= 0 && matches.Count < MaxOccurrences; k--)
        {
            var occurrence = at + TimeSpan.FromTicks(interval.Ticks * k);
            if (occurrence < earliest) break;
            matches.Add(Occurrence(occurrence));
        }

        return Task.FromResult<IReadOnlyList<AlarmMatch>>(matches);
    }

    private static AlarmMatch Occurrence(DateTimeOffset at)
    {
        var stamp = at.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        return new AlarmMatch($"timer:{stamp}",
            $"Timer due {at.ToLocalTime():ddd d MMM HH:mm}")
        {
            Detail = new Dictionary<string, object?> { ["due"] = stamp },
        };
    }

    private static bool TryReadAt(JsonElement config, out DateTimeOffset at)
    {
        at = default;
        if (config.ValueKind != JsonValueKind.Object) return false;
        if (!config.TryGetProperty("at", out var p) || p.ValueKind != JsonValueKind.String) return false;

        var raw = p.GetString();
        if (string.IsNullOrWhiteSpace(raw)) return false;

        // A bare local time carries no offset, so DateTimeOffset.TryParse would attach the
        // machine's *current* offset — correct here, since that is what the user typed.
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal, out at);
    }

    private static int ReadRepeatSeconds(JsonElement config)
    {
        if (config.ValueKind != JsonValueKind.Object) return 0;
        if (!config.TryGetProperty("repeat_every_seconds", out var p)) return 0;
        return p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v) && v > 0 ? v : 0;
    }

    private static string DescribeInterval(int seconds) => seconds switch
    {
        < 60                      => $"{seconds}s",
        < 3600 when seconds % 60 == 0   => $"{seconds / 60} min",
        < 86400 when seconds % 3600 == 0 => $"{seconds / 3600} h",
        _ when seconds % 86400 == 0     => $"{seconds / 86400} d",
        _                               => TimeSpan.FromSeconds(seconds).ToString(),
    };
}
