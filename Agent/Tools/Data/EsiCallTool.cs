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

    /// <summary>
    /// Paths per batched call, and how much of each answer survives.
    ///
    /// <para>⚠️ The batch exists because corporationhistory is one character per request, and
    /// "where did the leavers go" is one request per leaver. Forty of those as separate tool
    /// calls would spend twice the round budget; as one call they spend one round. The per-item
    /// cut keeps what matters: ESI lists corporation history newest first, so the entries that
    /// say where someone went are the ones kept.</para>
    /// </summary>
    private const int MaxBatchPaths     = 40;
    private const int MaxBatchItemChars = 1_200;
    private const int MaxBatchChars     = 30_000;

    /// <summary>
    /// Longest a per-route rate limit is waited out before the refusal is handed to the model.
    ///
    /// <para>⚠️ The polling keeps its own per-route blocks and this tool cannot see them, so a
    /// 429 here is handled here: wait what Retry-After says, once, if it is short. Handing a raw
    /// 429 straight to the model produces an immediate retry — another 429, another round — and
    /// a forty-path batch on one route is exactly where a per-route limit bites.</para>
    /// </summary>
    private const int MaxRetryAfterSeconds = 15;

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
              1. POST characters/affiliation/  body: [id, id, …]  → current corporation_id and
                 alliance_id for up to 1,000 characters in ONE call. This alone answers "who is
                 still in the corp".
              2. Only for those who LEFT: GET characters/{id}/corporationhistory/ → every
                 corporation they have been in with start_date, newest first. One character per
                 path — so pass them ALL in `paths` and get every history back in ONE call.
              3. POST universe/names/ for every corporation id you now hold, in one call.
              Three calls for any number of people.
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

        Paths are relative — characters/123/ — no scheme, no host, no version prefix: every
        route in the current ESI specification works as written there. Only GET, plus the three
        POST lookups above; everything else is refused. Responses over 12,000 characters are cut off
        and say so: narrow the request or page it with ?page=N (the result says how many pages).
        `paths` (GET only) takes up to 40 paths and returns an object keyed by path — use it
        whenever you would otherwise call the same endpoint for several ids, so the whole set
        costs one call instead of one per id. Each answer in a batch is cut at 1,200 characters,
        which for a corporation history keeps the newest entries.
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
            paths = new
            {
                type        = "array",
                items       = new { type = "string" },
                description = "GET only. Several relative paths answered in ONE call, up to 40 — e.g. the " +
                              "corporation history of every character who left. Results come back as an " +
                              "object keyed by path. Use this instead of one call per id.",
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
        required = new[] { "method" },
    };

    public EsiCallTool(EsiClient esi) => _esi = esi;

    public async Task<string> ExecuteAsync(JsonElement input, CancellationToken ct = default)
    {
        var method    = Text(input, "method").ToUpperInvariant();
        var path      = Text(input, "path").Trim();
        var body      = input.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
        var character = Text(input, "character").Trim();

        var queryString = "";
        if (input.TryGetProperty("query", out var q) && q.ValueKind == JsonValueKind.Object)
        {
            var parts = q.EnumerateObject()
                         .Select(p => Uri.EscapeDataString(p.Name) + "=" + Uri.EscapeDataString(p.Value.ToString()))
                         .ToList();
            if (parts.Count > 0) queryString = string.Join("&", parts);
        }

        // ── Several paths at once ─────────────────────────────────────────────
        if (input.TryGetProperty("paths", out var ps) && ps.ValueKind == JsonValueKind.Array && ps.GetArrayLength() > 0)
        {
            if (method != "GET") return "`paths` is for GET only.";
            var list = ps.EnumerateArray().Select(p => p.GetString() ?? "").Where(p => p.Length > 0).Distinct().ToList();
            if (list.Count > MaxBatchPaths)
                return $"At most {MaxBatchPaths} paths per call — this has {list.Count}. Split it, or narrow the set first "
                     + "(affiliation first, then history only for those who left).";
            return await BatchAsync(list, queryString, ct);
        }

        // ── One path ──────────────────────────────────────────────────────────
        if (Normalise(path, queryString) is not { } normalised)
            return path.Length == 0
                ? "No path was given."
                : "The path must be relative — e.g. characters/123/ — with no scheme, host or '..'.";
        path = normalised;

        // ── The method ────────────────────────────────────────────────────────
        HttpMethod http;
        switch (method)
        {
            case "GET":
                http = HttpMethod.Get;
                body = null;
                break;
            case "POST":
                var bare = path[Root.Length..].Split('?')[0];
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
        var r = await CallAsync(http, path, body, characterId, ct);

        if (r.StatusCode == 0)
            return $"ESI call not made: {r.Error}";
        if (!r.IsSuccess)
            return $"ESI returned {r.StatusCode} for {method} {path[Root.Length..]}: {Trim(r.Error ?? "", 600)}"
                 + (r.StatusCode == 403 && characterId is null
                    ? " This endpoint needs a token — pass one of the capsuleer's characters as `character`."
                    : "")
                 + (r.StatusCode == 429
                    ? $" This route is rate-limited; do not retry it for {r.RetryAfterSeconds ?? 60} seconds."
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

    /// <summary>
    /// One request, waiting out a short per-route rate limit once. Anything the client refuses to
    /// send (offline, error-limited) comes back as status 0 untouched.
    /// </summary>
    private async Task<EsiClient.RawResult> CallAsync(
        HttpMethod method, string path, string? body, long? characterId, CancellationToken ct)
    {
        var r = await _esi.RequestRawAsync(method, path, body, characterId, ct);
        if (r.StatusCode == 429 && r.RetryAfterSeconds is { } wait && wait <= MaxRetryAfterSeconds)
        {
            await Task.Delay(TimeSpan.FromSeconds(wait + 1), ct);
            r = await _esi.RequestRawAsync(method, path, body, characterId, ct);
        }
        return r;
    }

    /// <summary>
    /// The ESI root. The client's own base address is /latest/, and the agent's calls do NOT use
    /// it.
    ///
    /// <para>⚠️ Measured: the root serves everything /latest/ serves, byte for byte, and also
    /// the routes /latest/ answers 404 for — /sovereignty/systems/ among them. Since the move to
    /// compatibility dates the root is the canonical form and the version prefixes are the
    /// legacy one, so an agent resolving against /latest/ would be cut off from exactly the
    /// endpoints added most recently. The host is fixed here; the input path can never carry
    /// one.</para>
    /// </summary>
    private const string Root = "https://esi.evetech.net/";

    /// <summary>
    /// The absolute URL the client will send, or null when the input is not a relative ESI path.
    /// A version prefix the model remembers from older documentation is stripped.
    /// </summary>
    private static string? Normalise(string path, string queryString)
    {
        path = path.Trim();
        if (path.Length == 0) return null;
        if (path.Contains("://") || path.Contains("..") || path.Any(char.IsWhiteSpace)) return null;
        path = path.TrimStart('/');
        foreach (var prefix in new[] { "latest/", "dev/", "legacy/", "v1/", "v2/", "v3/", "v4/", "v5/", "v6/" })
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { path = path[prefix.Length..]; break; }
        if (!path.EndsWith('/') && !path.Contains('?')) path += "/";
        if (queryString.Length > 0) path += (path.Contains('?') ? "&" : "?") + queryString;
        return Root + path;
    }

    /// <summary>
    /// Every path in turn, as one JSON object keyed by the path as given. Sequential rather than
    /// parallel: the client's gate allows two in flight and the polling is using it too, and a
    /// burst of forty from the agent should not crowd it out.
    /// </summary>
    private async Task<string> BatchAsync(List<string> paths, string queryString, CancellationToken ct)
    {
        var sb   = new StringBuilder("{");
        var done = 0;
        var cut  = 0;

        foreach (var given in paths)
        {
            string item;
            if (Normalise(given, queryString) is not { } path)
                item = """{"error":"not a relative ESI path"}""";
            else
            {
                var r = await CallAsync(HttpMethod.Get, path, null, null, ct);
                if (r.StatusCode == 0)
                    return $"ESI calls stopped after {done} of {paths.Count}: {r.Error}";
                if (r.StatusCode == 429)
                    return sb.Append("\n}").Append($"\n(stopped after {done} of {paths.Count}: this route is rate-limited — "
                                                 + $"do not retry it for {r.RetryAfterSeconds ?? 60} seconds)").ToString();
                item = r.IsSuccess
                    ? Shorten(r.Body, ref cut)
                    : JsonSerializer.Serialize(new { error = $"{r.StatusCode}: {Trim(r.Error ?? "", 200)}" });
            }

            if (done > 0) sb.Append(',');
            sb.Append('\n').Append(JsonSerializer.Serialize(given)).Append(": ").Append(item);
            done++;

            if (sb.Length > MaxBatchChars)
            {
                sb.Append($"\n}} …(cut at {MaxBatchChars:N0} characters after {done} of {paths.Count} paths — ask for fewer)");
                return sb.ToString();
            }
        }
        sb.Append("\n}");
        if (cut > 0)
            sb.Append($"\n({cut} answer(s) were shortened to about {MaxBatchItemChars:N0} characters; lists keep their newest entries and end with an omitted_older_entries count)");
        return sb.ToString();
    }

    /// <summary>
    /// A batch item at or under the size limit, and still JSON.
    ///
    /// <para>⚠️ Not a substring. Cutting a JSON array at a character count lands inside a string
    /// and the whole batch stops parsing — measured on the first run of this. An array is cut at
    /// an element boundary instead, keeping the leading elements, which for a corporation history
    /// are the newest, and ends with a marker element saying how many older ones were dropped. An
    /// object is kept whole up to three times the limit — one entity's public record is small —
    /// and beyond that replaced by a note to ask for it on its own.</para>
    /// </summary>
    private static string Shorten(string body, ref int cut)
    {
        if (body.Length <= MaxBatchItemChars) return body;

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                var kept  = new List<string>();
                var total = 2;
                foreach (var el in root.EnumerateArray())
                {
                    var s = el.GetRawText();
                    if (total + s.Length + 1 > MaxBatchItemChars) break;
                    kept.Add(s);
                    total += s.Length + 1;
                }
                var dropped = root.GetArrayLength() - kept.Count;
                cut++;
                kept.Add(JsonSerializer.Serialize(new { omitted_older_entries = dropped }));
                return "[" + string.Join(",", kept) + "]";
            }

            if (body.Length <= MaxBatchItemChars * 3) return body;
        }
        catch (JsonException) { /* not JSON — fall through to a plain cut */ }

        cut++;
        return JsonSerializer.Serialize(new
        {
            error = $"response too large for a batch ({body.Length:N0} characters) — request this path on its own",
        });
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
