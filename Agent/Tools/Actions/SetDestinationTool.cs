using System.Data.Common;
using System.Text;
using System.Text.Json;
using EveConsole.Api;
using EveConsole.Data;

namespace EveConsole.Agent.Tools.Actions;

/// <summary>
/// Sets the in-game autopilot destination for one of the capsuleer's characters.
///
/// <para>The first tool that changes something in the game rather than in the app, and it is
/// its own tool for exactly that reason. esi_call is read-only by construction and stays so;
/// an action gets a tool that can do that one thing, with its inputs checked here, rather than
/// a hole opened in a generic caller. "Set destination UALX-3" over push-to-talk is one call.</para>
///
/// <para>⚠️ Which character. Several are often logged in at once — three were, the evening this
/// was written — and the app cannot know which client the capsuleer means. A named character
/// is used; failing that the single online one; failing that the call refuses and lists who is
/// online, so the model asks rather than guesses. Setting the same destination on every online
/// character is offered explicitly, for moving a fleet of alts together, and never inferred.</para>
///
/// <para>The system is resolved from the local SDE, not from ESI: it is faster, it works with
/// Tranquility down, and it can offer "did you mean" for a name half-heard over a microphone.</para>
/// </summary>
public sealed class SetDestinationTool : IAgentTool
{
    private const string Endpoint = "https://esi.evetech.net/ui/autopilot/waypoint/";

    private readonly EsiClient _esi;

    public string Name => "set_destination";

    public string Description =>
        "Sets the in-game autopilot destination to a solar system for one of the capsuleer's " +
        "characters — what they mean by \"set destination to UALX-3\", \"route me to Jita\", " +
        "\"set autopilot\". ALWAYS call this rather than saying you have done it. The system is " +
        "looked up by name locally; a near miss is answered with the closest names. Which " +
        "character: the one named, else the only one online, else you are told who is online " +
        "and must ask. all_online sets it for every online character at once — use that only " +
        "when the capsuleer says everyone, all, or the fleet. The route is cleared first unless " +
        "told to add to it. The character must be logged into the game for this to take effect.";

    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            system = new
            {
                type        = "string",
                description = "Solar system name, e.g. \"UALX-3\" or \"Jita\". Case does not matter; a unique prefix is accepted.",
            },
            character = new
            {
                type        = "string",
                description = "Optional. Which of the capsuleer's characters. Omit to use the only one online.",
            },
            all_online = new
            {
                type        = "boolean",
                description = "Set the destination for every character currently online. Only when the capsuleer asks for everyone.",
            },
            add_to_route = new
            {
                type        = "boolean",
                description = "Add as a waypoint at the end of the existing route instead of replacing it. Default false.",
            },
        },
        required = new[] { "system" },
    };

    public SetDestinationTool(EsiClient esi) => _esi = esi;

    public async Task<string> ExecuteAsync(JsonElement input, CancellationToken ct = default)
    {
        var systemName = Text(input, "system").Trim();
        var charName   = Text(input, "character").Trim();
        var allOnline  = input.TryGetProperty("all_online",   out var ao) && ao.ValueKind == JsonValueKind.True;
        var addToRoute = input.TryGetProperty("add_to_route", out var ar) && ar.ValueKind == JsonValueKind.True;

        if (systemName.Length == 0) return "No system was given.";

        await using var conn = AppDb.Connect();
        await conn.OpenAsync(ct);

        // ── The system ────────────────────────────────────────────────────────
        var system = await ResolveSystemAsync(conn, systemName, ct);
        if (system.Error is not null) return system.Error;

        // ── The character(s) ──────────────────────────────────────────────────
        var online = await OnlineCharactersAsync(conn, ct);
        List<(long Id, string Name, string Where)> targets;

        if (allOnline)
        {
            if (online.Count == 0) return "Nobody is logged in right now, so there is no one to set a destination for.";
            targets = online;
        }
        else if (charName.Length > 0)
        {
            var match = await ResolveCharacterAsync(conn, charName, ct);
            if (match is null)
                return $"No character named '{charName}' is set up in EVE Console. Online now: {Describe(online)}.";
            targets = [match.Value];
        }
        else if (online.Count == 1)
        {
            targets = online;
        }
        else if (online.Count == 0)
        {
            return "Nobody is logged in right now. The destination can only be set for a character who is in the game — "
                 + "ask which character, and whether they are logging in.";
        }
        else
        {
            return $"More than one character is online — ask which one. Online now: {Describe(online)}. "
                 + "Or pass all_online if the capsuleer means everyone.";
        }

        // ── The call(s) ───────────────────────────────────────────────────────
        var url = $"{Endpoint}?destination_id={system.Id}"
                + $"&add_to_beginning=false"
                + $"&clear_other_waypoints={(addToRoute ? "false" : "true")}";

        var lines = new List<string>();
        foreach (var (id, name, where) in targets)
        {
            var r = await _esi.RequestRawAsync(HttpMethod.Post, url, null, id, ct);
            if (r.StatusCode == 0)
                lines.Add($"{name}: not attempted — {r.Error}");
            else if (!r.IsSuccess)
                lines.Add($"{name}: ESI refused ({r.StatusCode}) — {Trim(r.Error ?? "", 200)}"
                        + (r.StatusCode is 401 or 403 ? " The character's token may lack the write_waypoint scope; re-authorising them fixes that." : ""));
            else
                lines.Add($"{name}: destination {(addToRoute ? "added" : "set")} to {system.Name}"
                        + (where.Length > 0 ? $" (currently in {where})" : "")
                        + (where == system.Name ? " — they are already there" : "")
                        + ".");
        }

        return string.Join("\n", lines)
             + (lines.All(l => l.Contains("destination ")) ? "" : "\nSay which succeeded and which did not.");
    }

    // ── Lookups ───────────────────────────────────────────────────────────────

    private static async Task<(int Id, string Name, string? Error)> ResolveSystemAsync(
        DbConnection conn, string name, CancellationToken ct)
    {
        // Exact first — many names are prefixes of others (J-, X-… in null-sec).
        await using (var exact = conn.Command("SELECT \"SolarSystemId\", \"Name\" FROM \"SdeSolarSystems\" WHERE LOWER(\"Name\") = LOWER(@n) LIMIT 1"))
        {
            AddParam(exact, "@n", name);
            await using var rdr = await exact.ExecuteReaderAsync(ct);
            if (await rdr.ReadAsync(ct)) return (rdr.GetInt32(0), rdr.GetString(1), null);
        }

        // Then a prefix, accepted only when it is unique.
        var candidates = new List<(int Id, string Name)>();
        await using (var like = conn.Command("SELECT \"SolarSystemId\", \"Name\" FROM \"SdeSolarSystems\" WHERE LOWER(\"Name\") LIKE LOWER(@p) || '%' ORDER BY \"Name\" LIMIT 8"))
        {
            AddParam(like, "@p", name);
            await using var rdr = await like.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct)) candidates.Add((rdr.GetInt32(0), rdr.GetString(1)));
        }

        return candidates.Count switch
        {
            1 => (candidates[0].Id, candidates[0].Name, null),
            0 => (0, "", $"No solar system called '{name}'. Check the spelling — over a microphone, dashes and zeros are often the trouble (UALX-3, C-FD0D)."),
            _ => (0, "", $"'{name}' matches several systems: {string.Join(", ", candidates.Select(c => c.Name))}. Ask which."),
        };
    }

    private static async Task<(long Id, string Name, string Where)?> ResolveCharacterAsync(
        DbConnection conn, string name, CancellationToken ct)
    {
        await using var cmd = conn.Command("""
            SELECT c."Id", c."Name", COALESCE(s."Name", '')
            FROM "Characters" c
            LEFT JOIN "CharacterStatuses" cs ON cs."CharacterId" = c."Id"
            LEFT JOIN "SdeSolarSystems" s ON s."SolarSystemId" = cs."SolarSystemId"
            WHERE LOWER(c."Name") = LOWER(@n)
            LIMIT 1
            """);
        AddParam(cmd, "@n", name);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        return await rdr.ReadAsync(ct) ? (rdr.GetInt64(0), rdr.GetString(1), rdr.GetString(2)) : null;
    }

    /// <summary>Everyone the app currently sees as logged in, with where they are.</summary>
    private static async Task<List<(long Id, string Name, string Where)>> OnlineCharactersAsync(
        DbConnection conn, CancellationToken ct)
    {
        var list = new List<(long, string, string)>();
        await using var cmd = conn.Command($"""
            SELECT c."Id", c."Name", COALESCE(s."Name", '')
            FROM "CharacterStatuses" cs
            JOIN "Characters" c ON c."Id" = cs."CharacterId"
            LEFT JOIN "SdeSolarSystems" s ON s."SolarSystemId" = cs."SolarSystemId"
            WHERE {AgentSqlDialect.IsTrue("cs.\"Online\"")}
            ORDER BY c."Name"
            """);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct)) list.Add((rdr.GetInt64(0), rdr.GetString(1), rdr.GetString(2)));
        return list;
    }

    private static string Describe(List<(long Id, string Name, string Where)> chars) =>
        chars.Count == 0 ? "nobody"
        : string.Join(", ", chars.Select(c => c.Where.Length > 0 ? $"{c.Name} ({c.Where})" : c.Name));

    private static void AddParam(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static string Text(JsonElement input, string name) =>
        input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";
}
