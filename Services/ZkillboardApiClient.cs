using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EveConsole.Models;

namespace EveConsole.Services;

/// <summary>
/// Thin HTTP wrapper around the three zKillboard surfaces this app uses. Read-only —
/// this app never posts kills to zKillboard.
///
///   • Filtered kills API (zkillboard.com/api) — full killmail body + a "zkb" sibling
///     object (hash, value, points, ...) at the root, per character/corp.
///   • Daily history dump (r2z2.zkillboard.com/history/raw) — one JSON OBJECT per day,
///     keyed by killmail id, universe-wide, each value the bare ESI killmail body with
///     NO hash anywhere in it. Used for backfill — since the full body is already in
///     hand, the missing hash is inert (nothing needs to re-fetch these via ESI).
///   • R2Z2 ephemeral stream (r2z2.zkillboard.com/ephemeral) — one JSON object per
///     sequence number, universe-wide: hash lives at the object's own root, and the ESI
///     killmail body is nested one level down under an "esi" key (NOT at the root —
///     confirmed against a live response; deserializing the root directly into
///     EsiKillMailFull silently yields a mostly-empty killmail). Used for "All kills"
///     live capture.
///
/// These three surfaces do NOT share one JSON shape — verified against live responses
/// after the naive "just deserialize into EsiKillMailFull, it's all the same ESI shape"
/// assumption broke in production (empty daily-dump results, then a JSON conversion
/// exception once the raw dump's real object-not-array shape was hit).
///
/// zKillboard publishes no rate-limit response headers (unlike ESI) — callers are
/// responsible for their own pacing between calls; this client only performs the HTTP
/// call and JSON parse.
/// </summary>
public class ZkillboardApiClient(IHttpClientFactory httpClientFactory, AppErrorLogger errorLogger)
{
    private readonly HttpClient _http  = httpClientFactory.CreateClient("zkillboard");

    /// <summary>Entity pages only, for a longer timeout — see its registration.</summary>
    private readonly HttpClient _pages = httpClientFactory.CreateClient("zkillboard-pages");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record ZkbRef(
        [property: JsonPropertyName("killmail_id")] int      KillmailId,
        [property: JsonPropertyName("zkb")]          ZkbHash? Zkb);

    private sealed record ZkbHash([property: JsonPropertyName("hash")] string? Hash);

    private sealed record ZkbSequence([property: JsonPropertyName("sequence")] long Sequence);

    /// <summary>A full killmail plus its hash, wherever the two sit in a given
    /// zKillboard response shape — EsiKillMailFull itself has no hash field (ESI's
    /// killmail-detail endpoint takes the hash as a URL parameter, not a body field).
    /// Hash is "" for daily-dump entries, which carry no hash at all — harmless, since
    /// we already have the full body and never need to re-fetch these via ESI.</summary>
    public sealed record ZkbFullKill(EsiKillMailFull Kill, string Hash);

    /// <summary>Out-parameter stand-in for GetDailyDumpAsync, which cannot return a
    /// second value alongside an IAsyncEnumerable. Distinguishes "zKillboard has not
    /// published this day's dump yet" (404 — retry later) from "the dump exists and
    /// simply yielded nothing after filtering" — which look identical to a caller that
    /// only counts results, and led to days being marked fully imported when they had in
    /// fact never been fetched at all.
    ///
    /// r2z2 publishes a day's dump well after that day ends — a completed day still 404ing
    /// several hours into the next one is normal, not an error.</summary>
    public sealed class DumpStatus
    {
        public bool Available { get; set; }
    }

    /// <summary>
    /// Id+hash pairs for kills involving the given character/corp in the last
    /// <paramref name="pastSeconds"/> (must be a multiple of 3600, max 604800 — the
    /// caller is expected to have already clamped this; overlapping windows across
    /// calls are harmless since everything downstream is dedup-by-id).
    /// </summary>
    public async Task<List<(int KillmailId, string Hash)>> GetKillRefsAsync(
        string ownerType, long ownerId, int pastSeconds, CancellationToken ct = default)
    {
        var entityPath = ownerType switch
        {
            "character"   => $"characterID/{ownerId}",
            "corporation" => $"corporationID/{ownerId}",
            _ => throw new ArgumentOutOfRangeException(nameof(ownerType), ownerType, "must be \"character\" or \"corporation\""),
        };
        var url = $"https://zkillboard.com/api/kills/{entityPath}/pastSeconds/{pastSeconds}/";

        try
        {
            var refs = await _http.GetFromJsonAsync<List<ZkbRef>>(url, JsonOptions, ct);
            if (refs is null) return [];

            return refs
                .Where(r => !string.IsNullOrEmpty(r.Zkb?.Hash))
                .Select(r => (r.KillmailId, r.Zkb!.Hash!))
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            errorLogger.Log(nameof(ZkillboardApiClient), $"GetKillRefsAsync {ownerType}:{ownerId}", ex);
            return [];
        }
    }

    /// <summary>
    /// The deepest entity page zKillboard serves. Page 101 is answered with
    /// <c>{"error":"page value > 100 not allowed"}</c> (measured 2026-09-28), so an entity's
    /// most recent 20,000 kills and losses are all this API reaches — five weeks back for a
    /// large alliance.
    /// </summary>
    public const int MaxEntityPage = 100;

    /// <summary>A page of kills, or why there is none.</summary>
    /// <param name="Kills">The page; empty when it is past the end. Null with a <paramref name="Problem"/>.</param>
    public sealed record EntityPage(List<ZkbFullKill>? Kills, string? Problem);

    /// <summary>
    /// One page of an entity's killmails — kills and losses together, newest first — from
    /// <c>/api/{character|corporation|alliance}ID/{id}/page/{n}/</c>.
    ///
    /// <para>Each entry is the full ESI body with a "zkb" sibling at its root (hash, values), so
    /// a page stores without one ESI call per kill. Checked against the live API on large
    /// alliances (2026-09-28): pages hold up to 200 (the first held 198), strictly newest first,
    /// items included, up to <see cref="MaxEntityPage"/>.</para>
    ///
    /// <para>⚠️ Slow on a page nobody has asked for lately: 36.7s for page 10 of a large alliance,
    /// against 0.14s for page 1. On its own client with a two-minute timeout, and a timeout is
    /// reported as one — as a cancellation it read as the list having ended.</para>
    /// </summary>
    /// <param name="entityType">"character", "corporation" or "alliance".</param>
    public async Task<EntityPage> GetEntityPageAsync(
        string entityType, long entityId, int page, CancellationToken ct = default)
    {
        var path = entityType switch
        {
            "character"   => "characterID",
            "corporation" => "corporationID",
            "alliance"    => "allianceID",
            _ => throw new ArgumentOutOfRangeException(nameof(entityType), entityType, "must be character, corporation or alliance"),
        };
        if (page > MaxEntityPage)
            return new EntityPage(null, $"zKillboard serves no further back than page {MaxEntityPage}");

        var url = $"https://zkillboard.com/api/{path}/{entityId}/page/{page}/";
        var context = $"GetEntityPageAsync {entityType}:{entityId} page {page}";

        try
        {
            using var response = await _pages.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                errorLogger.Log(nameof(ZkillboardApiClient), context,
                    new HttpRequestException($"zKillboard answered HTTP {(int)response.StatusCode}"));
                return new EntityPage(null, $"zKillboard answered HTTP {(int)response.StatusCode}");
            }

            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

            // Its errors come back as an object with a message, and the message is the answer.
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                var said = doc.RootElement.ValueKind == JsonValueKind.Object
                        && doc.RootElement.TryGetProperty("error", out var e) ? e.ToString() : "an unexpected answer";
                errorLogger.Log(nameof(ZkillboardApiClient), context, new InvalidOperationException($"zKillboard: {said}"));
                return new EntityPage(null, $"zKillboard said: {said}");
            }

            var kills = new List<ZkbFullKill>();
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                var hash = entry.TryGetProperty("zkb", out var zkb) && zkb.TryGetProperty("hash", out var h)
                    ? h.GetString() : null;
                if (string.IsNullOrEmpty(hash)) continue;

                var kill = entry.Deserialize<EsiKillMailFull>(JsonOptions);
                if (kill is not null && kill.KillMailId > 0) kills.Add(new ZkbFullKill(kill, hash));
            }
            return new EntityPage(kills, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The client's timeout, not the caller: HttpClient reports both the same way.
            errorLogger.Log(nameof(ZkillboardApiClient), context,
                new TimeoutException("zKillboard did not answer within two minutes"));
            return new EntityPage(null, "zKillboard did not answer within two minutes");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            errorLogger.Log(nameof(ZkillboardApiClient), context, ex);
            return new EntityPage(null, "zKillboard could not be reached");
        }
    }

    /// <summary>Where an entity ranks among all others on zKillboard, overall and by figure.
    /// Null where zKillboard gives no rank — an entity with little activity is unranked.</summary>
    public sealed record ZkbRanks(long? Overall, long? ShipsDestroyed, long? ShipsLost,
        long? PointsDestroyed, long? PointsLost, long? IskDestroyed, long? IskLost);

    /// <summary>One period's figures: all time, the last 90 days, or the last 7.</summary>
    public sealed record ZkbPeriod(long ShipsDestroyed, long ShipsLost, long PointsDestroyed, long PointsLost,
        double IskDestroyed, double IskLost, ZkbRanks? Ranks)
    {
        public static readonly ZkbPeriod None = new(0, 0, 0, 0, 0, 0, null);
        public bool IsEmpty => ShipsDestroyed == 0 && ShipsLost == 0;

        /// <summary>Kills and losses with no one else involved, in the period.</summary>
        public long SoloKills  { get; init; }
        public long SoloLosses { get; init; }

        /// <summary>The average number of pilots on its kills in the period, or null when
        /// zKillboard gives no breakdown to work it from.</summary>
        public double? AvgGangSize { get; init; }
    }

    /// <summary>The summary zKillboard shows above an entity's kill list, for each period.</summary>
    public sealed record ZkbStats(ZkbPeriod AllTime, ZkbPeriod Recent, ZkbPeriod Weekly);

    /// <summary>An entity's zKillboard stats, or why there are none.</summary>
    /// <param name="Stats">Null with no <paramref name="Problem"/>: zKillboard has no record of it.</param>
    public sealed record EntityStats(ZkbStats? Stats, string? Problem);

    /// <summary>
    /// An entity's summary from <c>/api/stats/{character|corporation|alliance}ID/{id}/</c>: kills,
    /// losses, points and ISK for all time, the last 90 days and the last 7, with ranks, solo kills
    /// and losses and the average gang — what zKillboard shows above an entity's kill list, and
    /// what its danger and gang ratios are worked out from for each period.
    ///
    /// <para>Shapes measured 2026-09-28. It answers with a 302 to <c>…/kills/</c>, which the
    /// client follows. All time is the totals at the root, ranked by
    /// <c>rankings.alltime.all.ranks</c>; 90 and 7 days are <c>rankings.{recent,weekly}.all</c>,
    /// each with <c>metrics</c> and usually <c>ranks</c> — but a quiet entity's period can have
    /// metrics and no ranks, or be an empty list, or be missing. An id it has no record of is
    /// answered, with a 200, as <c>{"error":"Invalid type or id"}</c>.</para>
    ///
    /// <para>Figures are read where zKillboard's own entity page reads them (its
    /// view/overview.php), so the panel and that page agree — but for a quiet entity's solo
    /// counts, which that page misses (see Period below).</para>
    /// </summary>
    public async Task<EntityStats> GetEntityStatsAsync(
        string entityType, long entityId, CancellationToken ct = default)
    {
        var path = entityType switch
        {
            "character"   => "characterID",
            "corporation" => "corporationID",
            "alliance"    => "allianceID",
            _ => throw new ArgumentOutOfRangeException(nameof(entityType), entityType, "must be character, corporation or alliance"),
        };
        var context = $"GetEntityStatsAsync {entityType}:{entityId}";

        try
        {
            using var response = await _pages.GetAsync($"https://zkillboard.com/api/stats/{path}/{entityId}/", ct);
            if (!response.IsSuccessStatusCode)
                return new EntityStats(null, $"zKillboard answered HTTP {(int)response.StatusCode}");

            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new EntityStats(null, "zKillboard sent an unexpected answer");

            // No record is not a failure: most entities someone looks up have none.
            if (root.TryGetProperty("error", out _) || !root.TryGetProperty("id", out _))
                return new EntityStats(null, null);

            static JsonElement Obj(JsonElement e, string name) =>
                e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object
                    ? v : default;
            static long L(JsonElement e, string name) =>
                e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
                    ? (v.TryGetInt64(out var n) ? n : (long)v.GetDouble()) : 0;
            static long? Rank(JsonElement e, string name) =>
                e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
                    ? (v.TryGetInt64(out var n) ? n : (long)v.GetDouble()) : null;
            static double D(JsonElement e, string name) =>
                e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
                    ? v.GetDouble() : 0;

            static ZkbPeriod From(JsonElement metrics, JsonElement ranks) => new(
                L(metrics, "shipsDestroyed"), L(metrics, "shipsLost"),
                L(metrics, "pointsDestroyed"), L(metrics, "pointsLost"),
                D(metrics, "iskDestroyed"), D(metrics, "iskLost"),
                ranks.ValueKind == JsonValueKind.Object
                    ? new ZkbRanks(Rank(ranks, "overall"), Rank(ranks, "shipsDestroyed"), Rank(ranks, "shipsLost"),
                                   Rank(ranks, "pointsDestroyed"), Rank(ranks, "pointsLost"),
                                   Rank(ranks, "iskDestroyed"), Rank(ranks, "iskLost"))
                    : null);

            // zKillboard's own average: each attacker-count bucket's kills at its lower bound, solo
            // as one, rounded half away from zero as PHP rounds. Measured against its all-time
            // avgGangSize: 13.7 for a large alliance, 37.6 for a pilot. The buckets exist per
            // period, as labels, recentLabels and weeklyLabels.
            static double? AvgGang(JsonElement labels)
            {
                if (labels.ValueKind != JsonValueKind.Object) return null;
                (string Key, int Size)[] buckets =
                    [("solo", 1), ("#:2+", 2), ("#:5+", 5), ("#:10+", 10), ("#:25+", 25), ("#:50+", 50), ("#:100+", 100), ("#:1000+", 1000)];
                long kills = 0;
                double weighted = 0;
                foreach (var (key, size) in buckets)
                {
                    var count = L(Obj(labels, key), "shipsDestroyed");
                    kills    += count;
                    weighted += (double)size * count;
                }
                return kills == 0 ? null : Math.Round(weighted / kills, 1, MidpointRounding.AwayFromZero);
            }

            // 90 and 7 days: figures and ranks from that period's rankings, solo kills and losses
            // from its solo rankings. Those leave out a quiet entity even when it has solo kills (a
            // pilot measured with 18 had no solo row at all), so failing them, the period's own solo
            // bucket, which is zKillboard's count of the same kills.
            ZkbPeriod Period(string key, string labelsKey)
            {
                var period = Obj(Obj(root, "rankings"), key);
                var all    = Obj(period, "all");
                if (all.ValueKind != JsonValueKind.Object) return ZkbPeriod.None;

                var labels = Obj(root, labelsKey);
                var solo   = Obj(Obj(period, "solo"), "metrics") is { ValueKind: JsonValueKind.Object } ranked
                    ? ranked : Obj(labels, "solo");
                return From(Obj(all, "metrics"), Obj(all, "ranks")) with
                {
                    SoloKills   = L(solo, "shipsDestroyed"),
                    SoloLosses  = L(solo, "shipsLost"),
                    AvgGangSize = AvgGang(labels),
                };
            }

            // All time: the totals at the root, which zKillboard's page shows and works its
            // published ratios out from, with the ranks from the all-time rankings.
            var allTime = From(root, Obj(Obj(Obj(Obj(root, "rankings"), "alltime"), "all"), "ranks")) with
            {
                SoloKills   = L(root, "soloKills"),
                SoloLosses  = L(root, "soloLosses"),
                AvgGangSize = AvgGang(Obj(root, "labels")),
            };

            return new EntityStats(new ZkbStats(
                allTime,
                Period("recent", "recentLabels"),
                Period("weekly", "weeklyLabels")), null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            errorLogger.Log(nameof(ZkillboardApiClient), context, new TimeoutException("zKillboard did not answer within two minutes"));
            return new EntityStats(null, "zKillboard did not answer within two minutes");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            errorLogger.Log(nameof(ZkillboardApiClient), context, ex);
            return new EntityStats(null, "zKillboard could not be reached");
        }
    }

    /// <summary>
    /// The full killmail dump for one calendar day (universe-wide). The root is a JSON
    /// OBJECT keyed by killmail id — e.g. <c>{"137236407": {ESI killmail body}, ...}</c>
    /// — not an array, and entries carry no hash (see ZkbFullKill remarks). Parsed as
    /// one JsonDocument rather than a manually-streamed reader: simpler, and a day's
    /// dump (tens of MB) is an acceptable one-shot allocation for an occasional backfill.
    ///
    /// When <paramref name="trackedCharacterIds"/>/<paramref name="trackedCorpIds"/> are
    /// given (Mine+Corp scope backfill), each entry's involvement is checked directly
    /// against the raw JsonElement BEFORE deserializing — a day can hold tens of
    /// thousands of killmails universe-wide, and fully materializing the attacker/item
    /// object graph for entries that are about to be discarded anyway was the dominant
    /// cost in an early version of this method. Leave both null (All scope) to
    /// deserialize and yield every entry.
    /// </summary>
    public async IAsyncEnumerable<ZkbFullKill> GetDailyDumpAsync(
        DateOnly date,
        IReadOnlySet<long>? trackedCharacterIds = null,
        IReadOnlySet<long>? trackedCorpIds = null,
        DumpStatus? status = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var url = $"https://r2z2.zkillboard.com/history/raw/{date:yyyyMMdd}.json";

        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) yield break;
        response.EnsureSuccessStatusCode();
        if (status is not null) status.Available = true;

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            ct.ThrowIfCancellationRequested();

            if (trackedCharacterIds is not null && trackedCorpIds is not null
                && !ElementInvolvesTracked(prop.Value, trackedCharacterIds, trackedCorpIds))
                continue;

            var kill = prop.Value.Deserialize<EsiKillMailFull>(JsonOptions);
            if (kill is not null)
                yield return new ZkbFullKill(kill, "");
        }
    }

    /// <summary>Cheap involvement check straight against the raw JSON — no object
    /// allocation — so GetDailyDumpAsync can skip deserializing (and its attacker/item
    /// lists) for the vast majority of a day's kills that don't involve a tracked
    /// character/corp in Mine+Corp scope.</summary>
    private static bool ElementInvolvesTracked(
        JsonElement kill, IReadOnlySet<long> trackedCharacterIds, IReadOnlySet<long> trackedCorpIds)
    {
        bool Matches(JsonElement entity)
        {
            if (entity.TryGetProperty("character_id", out var c) && c.TryGetInt64(out var cid)
                && trackedCharacterIds.Contains(cid))
                return true;
            if (entity.TryGetProperty("corporation_id", out var p) && p.TryGetInt64(out var pid)
                && trackedCorpIds.Contains(pid))
                return true;
            return false;
        }

        if (kill.TryGetProperty("victim", out var victim) && Matches(victim))
            return true;

        if (kill.TryGetProperty("attackers", out var attackers) && attackers.ValueKind == JsonValueKind.Array)
            foreach (var attacker in attackers.EnumerateArray())
                if (Matches(attacker))
                    return true;

        return false;
    }

    /// <summary>
    /// Does zKillboard itself have this kill? <c>/api/killID/{id}/</c> returns a
    /// one-element array when it does and a bare <c>[]</c> when it does not (HTTP 200
    /// either way). Null when the answer could not be established.
    ///
    /// Needed because absence from a daily dump does NOT mean absence from zKillboard —
    /// measured against a real database, the dumps omit roughly 0.1% of the kills
    /// zKillboard actually holds. This is the authoritative check, used to confirm a kill
    /// really is missing before submitting it.
    /// </summary>
    public async Task<bool?> KillExistsOnZkbAsync(int killmailId, CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync($"https://zkillboard.com/api/killID/{killmailId}/", ct);
            if (!response.IsSuccessStatusCode) return null;

            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            errorLogger.Log(nameof(ZkillboardApiClient), $"KillExistsOnZkbAsync {killmailId}", ex);
            return null;
        }
    }

    /// <summary>
    /// Lowest retained sequence whose killmail time is at or after <paramref name="target"/>
    /// — i.e. "where in the stream was this moment". Null if the position could not be
    /// established.
    ///
    /// R2Z2 publishes no time→sequence index, but sequences are time-ordered and every
    /// entry carries its killmail time, so this bisects the retained range (~17 probes for
    /// a full 8-day window). Entries older than retention 404; since the search only ever
    /// looks below the current head, a 404 means "expired", which is itself a valid signal
    /// to move the lower bound up.
    ///
    /// Measured 2026-08-02: retention runs ~8 days (oldest entry 7.86 days behind the
    /// head), far longer than the ~24h the docs imply. LookbackSequences is sized past
    /// that so the bisect starts below the real floor and finds it rather than assuming it.
    /// </summary>
    public async Task<long?> FindSequenceAtAsync(DateTimeOffset target, CancellationToken ct = default)
    {
        const long LookbackSequences = 160_000; // ~11 days at the observed ~14K kills/day

        var head = await GetSequenceAsync(ct);
        if (head is null) return null;

        var lo = Math.Max(1, head.Value - LookbackSequences);
        var hi = head.Value;

        while (lo < hi)
        {
            ct.ThrowIfCancellationRequested();
            var mid = lo + (hi - lo) / 2;

            var entry = await GetEphemeralAsync(mid, ct);
            if (entry is null || entry.Kill.KillMailTime < target)
                lo = mid + 1;   // expired (so certainly older) or genuinely earlier
            else
                hi = mid;
        }

        return lo > head.Value ? head.Value : lo;
    }

    /// <summary>Current R2Z2 stream position ("now"). Used to seed the firehose cursor
    /// when there is no saved position, or the saved one has gone stale.</summary>
    public async Task<long?> GetSequenceAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _http.GetFromJsonAsync<ZkbSequence>(
                "https://r2z2.zkillboard.com/ephemeral/sequence.json", JsonOptions, ct);
            return result?.Sequence;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            errorLogger.Log(nameof(ZkillboardApiClient), nameof(GetSequenceAsync), ex);
            return null;
        }
    }

    /// <summary>One entry from the R2Z2 firehose. Null on a 404 ("nothing at this
    /// sequence yet" — the documented signal to back off and retry), or on any other
    /// failure. Response shape: <c>{"killmail_id":..,"hash":"..","esi":{ESI killmail
    /// body},"zkb":{...},"uploaded_at":..,"sequence_id":..}</c> — the killmail body is
    /// nested under "esi", not at the root.</summary>
    public async Task<ZkbFullKill?> GetEphemeralAsync(long sequenceId, CancellationToken ct = default)
    {
        var url = $"https://r2z2.zkillboard.com/ephemeral/{sequenceId}.json";
        try
        {
            using var response = await _http.GetAsync(url, ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();

            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var root = doc.RootElement;

            var hash = root.TryGetProperty("hash", out var h) ? h.GetString() : null;
            if (string.IsNullOrEmpty(hash)) return null;

            var esiElement = root.TryGetProperty("esi", out var esi) ? esi : root;
            var kill = esiElement.Deserialize<EsiKillMailFull>(JsonOptions);
            return kill is null ? null : new ZkbFullKill(kill, hash);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            errorLogger.Log(nameof(ZkillboardApiClient), $"GetEphemeralAsync {sequenceId}", ex);
            return null;
        }
    }
}
