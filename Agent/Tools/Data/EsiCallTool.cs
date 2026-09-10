using System.Text;
using System.Text.Json;
using EveConsole.Api;
using EveConsole.Data;

namespace EveConsole.Agent.Tools.Data;

/// <summary>
/// Lets the agent ask ESI directly, for the things the local database does not hold.
///
/// <para>The database is a polled copy of what the capsuleer's own characters can see. It knows
/// who ACCEPTED a contract; it does not know where that person is NOW, because nothing about a
/// stranger is polled. "Which of the BNI capital buyers are still in Brave, and where did the
/// rest go" is answered by two public endpoints and nothing in the database.</para>
///
/// <para>⚠️ Read-only by construction. GET is always allowed; POST only for the three endpoints
/// that take a list of ids or names and return a lookup — they are queries that happen to be
/// spelled POST. Nothing here can open a window in the client, send a mail, or touch a fitting,
/// and no amount of prompting changes that, because the allowlist is code.</para>
///
/// <para>⚠️ Through <see cref="EsiClient"/>, never an HttpClient of its own, so the agent spends
/// the same error budget the polling does and is paused by the same offline flag. See
/// <see cref="EsiClient.RequestRawAsync"/> for why that matters.</para>
/// </summary>
public sealed class EsiCallTool : IAgentTool
{
    /// <summary>
    /// Longest response echoed back. ESI pages are 1,000 entries and a page of contracts is far
    /// past this — which is the point: the model should ask for less, not read more.
    /// </summary>
    private const int MaxResponseChars = 12_000;

    /// <summary>POST endpoints that are lookups, not actions.</summary>
    private static readonly string[] ReadOnlyPosts =
    [
        "universe/names/",
        "universe/ids/",
        "characters/affiliation/",
    ];

    private readonly EsiClient _esi;

    public string Name => "esi_call";

    public string Description =>
        """
        Calls the EVE Online ESI API directly and returns the JSON. Use it for what the local
        database does NOT hold — the database is a polled copy of what the capsuleer's own
        characters can see, so it knows nothing current about anyone else. ESI does. For anything
        the database holds — assets, jobs, wallet, contracts, kills, market — use query_database;
        it is faster and already resolved.

        The questions this answers, and how:
          "Is X still in corp Y? Where did they go?"
              POST characters/affiliation/  body: [id, id, …]   → current corporation_id and
              alliance_id for up to 1,000 characters in ONE call. Then
              GET characters/{id}/corporationhistory/          → every corporation they have
              been in with start_date, newest first: where they went and when.
          Names for any ids:   POST universe/names/  body: [id, …]  (≤1,000; characters,
              corporations, alliances, types, systems, stations)
          Ids for names:       POST universe/ids/    body: ["name", …]
          Public info:         GET characters/{id}/, corporations/{id}/, alliances/{id}/,
              alliances/{id}/corporations/, universe/systems/{id}/, universe/types/{id}/,
              killmails/{id}/{hash}/
          Authenticated (needs a character): endpoints under characters/{id}/… and
              corporations/{id}/… that require a token — pass the character's NAME as
              `character` and the call is signed with their token. Members lists and corp
              wallets need roles the character may not have; ESI says so with a 403.

        Paths are relative — characters/123/ — no scheme, no host. Only GET, plus the three POST
        lookups above; everything else is refused. Responses over 12,000 characters are cut off
        and say so: narrow the request or page it with ?page=N (the result says how many pages).
        Character ids for people in the database are in Characters.Id, contract acceptors in
        EsiContracts.AcceptorId, kill victims and attackers in KillMailDetails and
        KillMailAttackers — query those first, then ask ESI about the ids.
        """;

    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            method = new
            {
                type        = "string",
                @enum       = new[] { "GET", "POST" },
                description = "GET for almost everything. POST only for universe/names/, universe/ids/ and characters/affiliation/.",
            },
            path = new
            {
                type        = "string",
                description = "Relative ESI path, e.g. \"characters/2112175987/corporationhistory/\". No scheme or host.",
            },
            query = new
            {
                type        = "object",
                description = "Optional query-string parameters, e.g. {\"page\": \"2\"} or {\"datasource\": \"tranquility\"}.",
                additionalProperties = new { type = "string" },
            },
            body = new
            {
                type        = "string",
                description = "For POST: the JSON body as a string, e.g. \"[95465499, 2112175987]\" or \"[\\\"Brave Newbies Inc.\\\"]\".",
            },
            character = new
            {
                type        = "string",
                description = "Optional. The name of one of the capsuleer's characters to sign the call as, for endpoints that need a token.",
            },
        },
        required = new[] { "method", "path" },
    };

    public EsiCallTool(EsiClient esi) => _esi = esi;

    public async Task<string> ExecuteAsync(JsonElement input, CancellationToken ct = default)
    {
        var method    = Text(input, "method").ToUpperInvariant();
        var path      = Text(input, "path").Trim();
        var body      = input.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
        var character = Text(input, "character").Trim();

        // ── The path ──────────────────────────────────────────────────────────
        if (path.Length == 0) return "No path was given.";
        if (path.Contains("://") || path.Contains("..") || path.Any(char.IsWhiteSpace))
            return "The path must be relative — e.g. characters/123/ — with no scheme, host or '..'.";
        path = path.TrimStart('/');
        if (path.StartsWith("latest/", StringComparison.OrdinalIgnoreCase)) path = path["latest/".Length..];
        if (!path.EndsWith('/') && !path.Contains('?')) path += "/";

        if (input.TryGetProperty("query", out var q) && q.ValueKind == JsonValueKind.Object)
        {
            var parts = q.EnumerateObject()
                         .Select(p => Uri.EscapeDataString(p.Name) + "=" + Uri.EscapeDataString(p.Value.ToString()))
                         .ToList();
            if (parts.Count > 0) path += (path.Contains('?') ? "&" : "?") + string.Join("&", parts);
        }

        // ── The method ────────────────────────────────────────────────────────
        HttpMethod http;
        switch (method)
        {
            case "GET":
                http = HttpMethod.Get;
                body = null;
                break;
            case "POST":
                var bare = path.Split('?')[0];
                if (!ReadOnlyPosts.Any(p => bare.Equals(p, StringComparison.OrdinalIgnoreCase)))
                    return $"POST is only allowed for {string.Join(", ", ReadOnlyPosts)} — lookups that take a list. "
                         + "This tool is read-only; nothing that changes state can be called through it.";
                if (string.IsNullOrWhiteSpace(body)) return "A POST to this endpoint needs a JSON array in body.";
                try { using var _ = JsonDocument.Parse(body); }
                catch (JsonException ex) { return $"body is not valid JSON: {ex.Message}"; }
                http = HttpMethod.Post;
                break;
            default:
                return "method must be GET or POST.";
        }

        // ── The character, when the call is to be signed ──────────────────────
        long? characterId = null;
        if (character.Length > 0)
        {
            characterId = await ResolveCharacterAsync(character, ct);
            if (characterId is null)
                return $"No character named '{character}' is set up in EVE Console. The names that are: "
                     + string.Join(", ", await CharacterNamesAsync(ct)) + ".";
        }

        // ── The call ──────────────────────────────────────────────────────────
        var r = await _esi.RequestRawAsync(http, path, body, characterId, ct);

        if (r.StatusCode == 0)
            return $"ESI call not made: {r.Error}";
        if (!r.IsSuccess)
            return $"ESI returned {r.StatusCode} for {method} {path}: {Trim(r.Error ?? "", 600)}"
                 + (r.StatusCode == 403 && characterId is null
                    ? " This endpoint needs a token — pass one of the capsuleer's characters as `character`."
                    : "");

        var sb = new StringBuilder();
        if (r.TotalPages > 1)
            sb.Append($"(page 1 of {r.TotalPages} — add query {{\"page\": \"2\"}} for the next) ");
        if (r.Body.Length > MaxResponseChars)
            sb.Append(r.Body, 0, MaxResponseChars)
              .Append($" …(cut at {MaxResponseChars:N0} of {r.Body.Length:N0} characters — narrow the request or page it)");
        else
            sb.Append(r.Body);
        return sb.ToString();
    }

    /// <summary>A character the capsuleer has set up, by name, case-insensitively.</summary>
    private static async Task<long?> ResolveCharacterAsync(string name, CancellationToken ct)
    {
        await using var conn = AppDb.Connect();
        await conn.OpenAsync(ct);
        await using var cmd = conn.Command("SELECT \"Id\" FROM \"Characters\" WHERE LOWER(\"Name\") = LOWER(@name) LIMIT 1");
        var p = cmd.CreateParameter(); p.ParameterName = "@name"; p.Value = name; cmd.Parameters.Add(p);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? null : Convert.ToInt64(result);
    }

    private static async Task<List<string>> CharacterNamesAsync(CancellationToken ct)
    {
        var names = new List<string>();
        await using var conn = AppDb.Connect();
        await conn.OpenAsync(ct);
        await using var cmd = conn.Command("SELECT \"Name\" FROM \"Characters\" ORDER BY \"Name\"");
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct)) names.Add(rdr.GetString(0));
        return names;
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static string Text(JsonElement input, string name) =>
        input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";
}
