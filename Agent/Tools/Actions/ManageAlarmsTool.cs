using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EveConsole.Alarms;
using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Services;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Agent.Tools.Actions;

/// <summary>
/// Lets the agent create, list and remove alarms, so "tell me when X happens" becomes a real
/// standing alarm rather than a promise it cannot keep once the conversation moves on.
/// </summary>
public sealed class ManageAlarmsTool : IAgentTool
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly AlarmConditionRegistry          _registry;
    private readonly AlarmService                    _service;

    public ManageAlarmsTool(
        IDbContextFactory<AppDbContext> dbFactory,
        AlarmConditionRegistry          registry,
        AlarmService                    service)
    {
        _dbFactory = dbFactory;
        _registry  = registry;
        _service   = service;
    }

    public string Name => "manage_alarms";

    // $$ so the JSON braces in the examples below stay literal; {{ }} is the interpolation.
    public string Description =>
        $$"""
         Create, list or delete standing alarms. An alarm watches for something and then acts on
         its own — it keeps working after this conversation ends, so use it whenever the
         capsuleer asks to be told when something happens, rather than promising to watch.

         ACTIONS
         - list:   every alarm, with its condition and how often it has fired.
         - create: needs name, condition_type, condition (object), actions (array).
         - delete: needs id.

         CONDITION TYPES
         {{DescribeConditions()}}

         ACTIONS AVAILABLE (the "actions" array; each entry has a "kind")
         - {"kind":"agent_notify"} — the alarm tells YOU it fired and you tell the capsuleer.
           This is the right one when they said "tell me when…". Optionally add
           "instruction" with anything they want mentioned. Nothing else is needed: when it
           fires you get a message with the details and simply report it.
         - {"kind":"sound","sound":"<key>","volume":100} — plays a sound. Keys include
           chime-soft, chime-triad, ping-glass, bell-brass, bell-deep, gong-low, alert-double,
           alarm-urgent, two-tone-alert, klaxon-industrial, buzzer-harsh, siren-sweep, horn-low.
         - {"kind":"alert","title":"…","body":"…"} — a dismissible alert on the Overview.
         - {"kind":"dialog","title":"…","message":"…"} — a top-most pop-up window.
           In title/body/message you may use {alarm} {summary} {count} {time} {date}.

         OTHER FIELDS ON create
         - poll_seconds: how often to check (default 60, minimum 10).
         - repeat: "continuous" (default, stays armed) or "one_shot" (disables itself after
           firing once). Use one_shot for a reminder, continuous for a watch.
         - cooldown_seconds: optional minimum gap between firings.

         HOW ALARMS AVOID BEING NOISY — read before writing a query
         An alarm announces only things it has not announced before. Every match carries a key,
         and a key already seen is not news. For a `sql` condition that means the query MUST
         return a stable identifying column and you must name it in key_column — a row Id, an
         event Id. If you nominate something that changes every run (a timestamp of now, a
         count) the alarm will fire on every single check. If you are unsure, prefer a query
         whose rows are append-only, like a log table, and key on its Id.

         Do not filter the query to "since the last time I checked" — the alarm handles that.
         Write a query for the current state, bounded by a sensible recent window.

         The database is {{AgentSqlDialect.Name}}; see query_database for how dates and
         identifiers work on it. Getting a date comparison wrong widens the window silently,
         which on an alarm means firing constantly.

         WORKED EXAMPLES
         "Tell me when one of my characters jumps a gate":
           condition_type "sql", poll_seconds 60, condition:
             {"sql":"SELECT Id, OccurredAt, CharacterName, FromSystem, ToSystem
                     FROM "GameLogEvents" WHERE "Kind"='movement.jumped'
                       AND {{AgentSqlDialect.RecentLogRows("\"OccurredAt\"", 1)}}
                     ORDER BY Id DESC LIMIT 20",
              "key_column":"Id"}
           actions: [{"kind":"agent_notify"}]

         "Tell me when one of my characters logs in":
           condition_type "sql", poll_seconds 60, condition:
             {"sql":"SELECT s.CharacterId || ':' || s.LastLogin AS Id, c.Name AS Character,
                            t.Name AS Ship, s.LastLogin
                     FROM "CharacterStatuses" s
                     JOIN "Characters" c ON c."Id" = s."CharacterId"
                     LEFT JOIN "SdeTypes" t ON t."TypeId" = s."ShipTypeId"
                     WHERE {{AgentSqlDialect.IsTrue("s.\"Online\"")}}",
              "key_column":"Id"}
           actions: [{"kind":"agent_notify"}]
           (CharacterStatuses holds current state only, so the key pairs the character with
            their login time — that changes on each new login and nothing else.)
         """;

    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            action = new
            {
                type        = "string",
                @enum       = new[] { "list", "create", "delete" },
                description = "What to do.",
            },
            id             = new { type = "integer", description = "Alarm id, for delete." },
            name           = new { type = "string",  description = "A short name, for create." },
            condition_type = new { type = "string",  description = "Condition type key, for create." },
            condition      = new { type = "object",  description = "Condition parameters, for create." },
            actions        = new
            {
                type        = "array",
                items       = new { type = "object" },
                description = "What happens when it fires. At least one.",
            },
            poll_seconds     = new { type = "integer", description = "Check interval in seconds (default 60, min 10)." },
            repeat           = new { type = "string",  @enum = new[] { "continuous", "one_shot" } },
            cooldown_seconds = new { type = "integer", description = "Minimum gap between firings." },
        },
        required = new[] { "action" },
    };

    private string DescribeConditions()
    {
        // Built from the live registry, so a condition added later describes itself to the
        // agent without anyone remembering to update this text.
        var sb = new StringBuilder();
        foreach (var c in _registry.All)
        {
            sb.Append("         - \"").Append(c.TypeKey).Append("\": ").AppendLine(c.DisplayName);
            sb.Append("             ").AppendLine(c.Description);
            sb.Append("             parameters: ")
              .AppendLine(JsonSerializer.Serialize(c.ParameterSchema));
        }
        return sb.ToString().TrimEnd();
    }

    public async Task<string> ExecuteAsync(JsonElement input, CancellationToken ct = default)
    {
        var action = Str(input, "action")?.ToLowerInvariant() ?? "";

        try
        {
            return action switch
            {
                "list"   => await ListAsync(ct),
                "create" => await CreateAsync(input, ct),
                "delete" => await DeleteAsync(input, ct),
                _        => Error($"Unknown action '{action}'. Use list, create or delete."),
            };
        }
        catch (Exception ex)
        {
            return Error(ex.Message);
        }
    }

    private async Task<string> ListAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var alarms  = await db.Alarms.AsNoTracking().OrderBy(a => a.Id).ToListAsync(ct);
        var actions = await db.AlarmActions.AsNoTracking().ToListAsync(ct);

        if (alarms.Count == 0)
            return """{"alarms":[],"note":"No alarms are set up."}""";

        var list = alarms.Select(a =>
        {
            var condition = _registry.Find(a.ConditionType);
            string describe;
            try
            {
                describe = condition?.Describe(
                    JsonDocument.Parse(a.ConditionJson ?? "{}").RootElement) ?? a.ConditionType;
            }
            catch { describe = a.ConditionType; }

            return new
            {
                id             = a.Id,
                name           = a.Name,
                enabled        = a.Enabled,
                condition_type = a.ConditionType,
                condition      = describe,
                repeat         = a.Repeat == AlarmRepeat.OneShot ? "one_shot" : "continuous",
                poll_seconds   = a.PollSeconds,
                fire_count     = a.FireCount,
                last_fired     = a.LastFiredAt?.ToUniversalTime().ToString("u"),
                created_by     = a.CreatedBy,
                error          = a.LastError,
                actions        = actions.Where(x => x.AlarmId == a.Id)
                                        .OrderBy(x => x.Ordinal)
                                        .Select(x => x.Kind.ToString().ToLowerInvariant())
                                        .ToArray(),
            };
        });

        return JsonSerializer.Serialize(new { alarms = list });
    }

    private async Task<string> CreateAsync(JsonElement input, CancellationToken ct)
    {
        var name = Str(input, "name");
        if (string.IsNullOrWhiteSpace(name)) return Error("A 'name' is required.");

        var typeKey = Str(input, "condition_type");
        if (string.IsNullOrWhiteSpace(typeKey)) return Error("A 'condition_type' is required.");

        var condition = _registry.Find(typeKey);
        if (condition is null)
            return Error($"Unknown condition_type '{typeKey}'. Available: " +
                         string.Join(", ", _registry.All.Select(c => c.TypeKey)));

        var conditionJson = input.TryGetProperty("condition", out var cfg)
                         && cfg.ValueKind == JsonValueKind.Object
            ? cfg.GetRawText()
            : "{}";

        if (!input.TryGetProperty("actions", out var actionsEl)
            || actionsEl.ValueKind != JsonValueKind.Array
            || actionsEl.GetArrayLength() == 0)
        {
            return Error("At least one entry in 'actions' is required — an alarm that does " +
                         "nothing when it fires is not worth setting.");
        }

        var parsedActions = new List<(AlarmActionKind Kind, string Json)>();
        foreach (var a in actionsEl.EnumerateArray())
        {
            var kindText = Str(a, "kind")?.ToLowerInvariant() ?? "";
            var kind = kindText switch
            {
                "agent_notify" or "agent" or "notify" => AlarmActionKind.AgentNotify,
                "sound"                               => AlarmActionKind.Sound,
                "alert"                               => AlarmActionKind.Alert,
                "dialog"                              => AlarmActionKind.Dialog,
                _                                     => (AlarmActionKind?)null,
            } ?? throw new InvalidOperationException(
                $"Unknown action kind '{kindText}'. Use agent_notify, sound, alert or dialog.");

            // Everything except "kind" is that action's configuration.
            var cfgObj = new JsonObject();
            foreach (var p in a.EnumerateObject())
                if (!p.NameEquals("kind"))
                    cfgObj[p.Name] = JsonNode.Parse(p.Value.GetRawText());

            parsedActions.Add((kind, cfgObj.ToJsonString()));
        }

        // Fail before writing anything if the condition cannot read its own configuration —
        // better a clear error now than a silently dead alarm the capsuleer is relying on.
        try
        {
            var probe = JsonDocument.Parse(conditionJson).RootElement;
            _ = condition.Describe(probe);
        }
        catch (Exception ex)
        {
            return Error($"The condition configuration was rejected: {ex.Message}");
        }

        var repeat = string.Equals(Str(input, "repeat"), "one_shot", StringComparison.OrdinalIgnoreCase)
            ? AlarmRepeat.OneShot
            : AlarmRepeat.Continuous;

        var alarm = new Alarm
        {
            Name            = name.Trim(),
            Enabled         = true,
            ConditionType   = condition.TypeKey,
            ConditionJson   = conditionJson,
            Repeat          = repeat,
            PollSeconds     = Math.Max(10, Int(input, "poll_seconds") ?? 60),
            CooldownSeconds = Math.Max(0, Int(input, "cooldown_seconds") ?? 0),
            CreatedBy       = "agent",
            CreatedAt       = DateTimeOffset.Now,
        };

        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            db.Alarms.Add(alarm);
            await db.SaveChangesAsync(ct);

            var ordinal = 0;
            foreach (var (kind, json) in parsedActions)
                db.AlarmActions.Add(new AlarmAction
                {
                    AlarmId = alarm.Id, Kind = kind, ConfigJson = json, Ordinal = ordinal++,
                });

            await db.SaveChangesAsync(ct);
        }

        // Bank whatever already matches, so a brand-new alarm does not immediately announce
        // history, then let the loop pick it up on its next tick.
        await _service.PrimeAsync(alarm.Id, ct);
        _service.Invalidate(alarm.Id);

        return JsonSerializer.Serialize(new
        {
            created   = true,
            id        = alarm.Id,
            name      = alarm.Name,
            condition = condition.Describe(JsonDocument.Parse(conditionJson).RootElement),
            note      = "The alarm is armed. It will announce only things it has not announced " +
                        "before, and anything already matching right now was recorded as history " +
                        "rather than fired on.",
        });
    }

    private async Task<string> DeleteAsync(JsonElement input, CancellationToken ct)
    {
        var id = Int(input, "id");
        if (id is null) return Error("An 'id' is required. Use action 'list' to find it.");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var alarm = await db.Alarms.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (alarm is null) return Error($"No alarm with id {id}.");

        foreach (var sql in new[]
                 {
                     """DELETE FROM "AlarmActions"  WHERE "AlarmId" = {0}""",
                     """DELETE FROM "AlarmSeenKeys" WHERE "AlarmId" = {0}""",
                     """DELETE FROM "AlarmEvents"   WHERE "AlarmId" = {0}""",
                     """DELETE FROM "AlarmAlerts"   WHERE "AlarmId" = {0}""",
                     """DELETE FROM "Alarms"        WHERE "Id"      = {0}""",
                 })
            await db.Database.ExecuteSqlRawAsync(sql, [id.Value], ct);

        _service.Invalidate(id.Value);
        return JsonSerializer.Serialize(new { deleted = true, id, name = alarm.Name });
    }

    private static string Error(string message) =>
        JsonSerializer.Serialize(new { error = message });

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p)
        && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p)
        && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v) ? v : null;
}
