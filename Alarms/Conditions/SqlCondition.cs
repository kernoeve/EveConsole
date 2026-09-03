using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using EveConsole.Data;

namespace EveConsole.Alarms.Conditions;

/// <summary>
/// Runs a read-only SELECT on an interval and fires on the rows it returns — or on their
/// absence. The escape hatch: anything in the database can be watched without a bespoke check,
/// which is what lets the agent build an alarm for a request nobody anticipated.
///
/// <para>Each row becomes a match keyed on a column the query nominates, so a row that stays in
/// the result set is announced once rather than every poll. Getting that key right is the whole
/// game: name a column that is stable and unique per row (an Id), never one that changes each
/// time the query runs (a timestamp of "now", a running total), or the alarm fires forever.</para>
/// </summary>
public sealed class SqlCondition : IAlarmCondition
{
    private const int MaxRows = 50;

    public string TypeKey     => "sql";
    public string DisplayName => "Database query";

    public string Description =>
        "Runs a SELECT against the local EVE Console database on an interval and fires on the " +
        "rows it returns, or on there being none. Use when no purpose-built check fits. " +
        "Only SELECT is permitted.";

    /// <summary>
    /// A query runs over live, mutable state: a row can leave the result set and come back, and
    /// that is a real event rather than a repeat. It is also what allows an absence alarm to
    /// re-arm once rows return.
    /// </summary>
    public bool ForgetsUnseenKeys => true;

    public object ParameterSchema => new
    {
        type = "object",
        properties = new
        {
            sql = new
            {
                type = "string",
                description =
                    "The SELECT to run. Only SELECT (or a WITH…SELECT) is allowed. Keep it small and " +
                    "add a LIMIT — it runs on every check. Include a stable identifying column and " +
                    "name it in key_column.",
            },
            key_column = new
            {
                type = "string",
                description =
                    "Column holding a stable, unique identifier for each row — an Id or similar. This " +
                    "is what stops a row being announced twice. Never nominate something that changes " +
                    "between runs, or the alarm will fire on every check. Defaults to a column called " +
                    "Id if the query has one, otherwise the whole row is used as its own key.",
            },
            label_column = new
            {
                type        = "string",
                description = "Optional column to use as the alert text. Defaults to the whole row.",
            },
            mode = new
            {
                type        = "string",
                @enum       = new[] { "present", "absent" },
                description =
                    "'present' (default) fires on rows returned. 'absent' fires when the query returns " +
                    "nothing at all, and re-arms once rows come back — for watching that something " +
                    "expected has stopped happening.",
            },
        },
        required = new[] { "sql" },
    };

    public string Describe(JsonElement config)
    {
        var sql = ReadString(config, "sql");
        if (string.IsNullOrWhiteSpace(sql)) return "Query (not configured)";

        var absent  = IsAbsentMode(config);
        var flat    = string.Join(" ", sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var trimmed = flat.Length > 70 ? flat[..70] + "…" : flat;

        return absent ? $"No rows from: {trimmed}" : $"Rows from: {trimmed}";
    }

    public async Task<IReadOnlyList<AlarmMatch>> EvaluateAsync(
        JsonElement config, AlarmEvaluationContext ctx, CancellationToken ct = default)
    {
        var sql = ReadString(config, "sql")?.Trim();
        if (string.IsNullOrWhiteSpace(sql)) return [];

        if (!IsReadOnly(sql))
            throw new InvalidOperationException("Only SELECT queries are permitted in an alarm.");

        var keyColumn   = ReadString(config, "key_column");
        var labelColumn = ReadString(config, "label_column");
        var absent      = IsAbsentMode(config);

        await using var conn = AppDb.Connect();
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;

        var rows = new List<Dictionary<string, object?>>();
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (rows.Count < MaxRows && await r.ReadAsync(ct))
            {
                var row = new Dictionary<string, object?>(r.FieldCount, StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < r.FieldCount; i++)
                    row[r.GetName(i)] = await r.IsDBNullAsync(i, ct) ? null : r.GetValue(i);
                rows.Add(row);
            }
        }

        if (absent)
        {
            // One fixed key: "the thing I was watching for is missing". ForgetsUnseenKeys drops
            // it as soon as rows return, which is what re-arms the alarm.
            return rows.Count == 0
                ? [new AlarmMatch("absent", "The watched query returned no rows.")]
                : [];
        }

        var matches = new List<AlarmMatch>(rows.Count);
        foreach (var row in rows)
        {
            var key = ResolveKey(row, keyColumn);

            var summary = labelColumn is not null && row.TryGetValue(labelColumn, out var label)
                ? label?.ToString() ?? "(null)"
                : DescribeRow(row);

            matches.Add(new AlarmMatch($"sql:{key}", summary)
            {
                Detail = row,
            });
        }

        return matches;
    }

    /// <summary>
    /// The nominated column, else a column called Id, else the whole row. Falling back to the
    /// whole row is deliberately conservative: it means an unchanged row is never announced
    /// twice, at the cost of re-announcing one whose other columns happen to change.
    /// </summary>
    private static string ResolveKey(Dictionary<string, object?> row, string? keyColumn)
    {
        if (!string.IsNullOrWhiteSpace(keyColumn) && row.TryGetValue(keyColumn, out var explicitKey))
            return explicitKey?.ToString() ?? "null";

        if (row.TryGetValue("Id", out var id)) return id?.ToString() ?? "null";

        return DescribeRow(row);
    }

    private static string DescribeRow(Dictionary<string, object?> row)
    {
        var sb = new StringBuilder();
        foreach (var (k, v) in row)
        {
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(k).Append('=').Append(v);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Same rule the agent's query_database tool applies: first keyword must be SELECT or WITH.
    /// A semicolon is refused outright so a statement cannot be chained onto the end of one.
    /// </summary>
    private static bool IsReadOnly(string sql)
    {
        var trimmed = sql.TrimEnd().TrimEnd(';');
        if (trimmed.Contains(';')) return false;

        var first = trimmed.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)
                           .FirstOrDefault() ?? "";

        return first.Equals("SELECT", StringComparison.OrdinalIgnoreCase)
            || first.Equals("WITH",   StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAbsentMode(JsonElement config) =>
        string.Equals(ReadString(config, "mode"), "absent", StringComparison.OrdinalIgnoreCase);

    private static string? ReadString(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p)
        && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}
