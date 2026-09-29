using System.Diagnostics;
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
/// zKillboard publishes no rate-limit response headers (unlike ESI). Callers of
/// zkillboard.com's API pace themselves. R2Z2 (the stream and the daily dumps) is paced
/// here instead: every request to it passes one gate — see "R2Z2: one gate" below.
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

    // ── R2Z2: one gate ────────────────────────────────────────────────────────
    //
    // ⚠️ R2Z2 allows 15 requests a second from an IP — an IP, not an app — and answers any more
    // with a 429, "exceeded rate limit of 15/s! ban will last up to 1 hour", refusing everything
    // from that IP meanwhile. On 2026-09-28 a release and a dev build on one machine, both on
    // the firehose and each sending ten requests at once, went over it. Both were then refused
    // for five hours: each retried every seven seconds and never backed off, over 25,000
    // refusals apiece, and both status lines said "caught up".
    //
    // So every R2Z2 request — the stream, the position search, the daily dumps — passes here:
    //   • At most R2Z2PerSecond start in any second, so three copies of the app on one machine
    //     stay under the limit together.
    //   • A 429 stops them all. Nothing is sent until the pause is over — Retry-After when R2Z2
    //     gives one; otherwise five minutes, doubling while it goes on refusing, up to an hour —
    //     and the pause is logged once, not once per refused request.

    /// <summary>R2Z2 requests that may start in any one second. R2Z2 allows 15 an IP.</summary>
    public const int R2Z2PerSecond = 4;

    // A second, and a little over, for the network's jitter between here and R2Z2's clock.
    private static readonly TimeSpan R2Z2Window   = TimeSpan.FromMilliseconds(1050);
    private static readonly TimeSpan FirstPause   = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan LongestPause = TimeSpan.FromHours(1);

    private readonly SemaphoreSlim _r2z2Turn   = new(1, 1);
    private readonly Queue<long>   _r2z2Starts = new();   // when the latest requests started
    private readonly object        _r2z2Lock   = new();
    private DateTimeOffset _r2z2PausedUntil;
    private int            _r2z2Refusals;                 // in a row, without an answer between

    /// <summary>While R2Z2 is refusing this machine, when it may be asked again; otherwise null.</summary>
    public DateTimeOffset? R2Z2PausedUntil
    {
        get { lock (_r2z2Lock) return _r2z2PausedUntil > DateTimeOffset.UtcNow ? _r2z2PausedUntil : null; }
    }

    /// <summary>What came of one R2Z2 request.</summary>
    public enum R2Z2Answer
    {
        /// <summary>200, with something usable in it.</summary>
        Found,
        /// <summary>404 — past the live edge, expired, or a dump not published yet — or a 200
        /// with nothing usable in it.</summary>
        Missing,
        /// <summary>R2Z2 is refusing this machine: a 429, or a request not sent because of an
        /// earlier one. <see cref="R2Z2PausedUntil"/> says until when.</summary>
        Refused,
        /// <summary>Anything else: no connection, a timeout, a 5xx.</summary>
        Failed,
    }

    /// <summary>One sequence of the stream; <see cref="Kill"/> is set when it was Found.</summary>
    public readonly record struct StreamEntry(R2Z2Answer Answer, ZkbFullKill? Kill);

    /// <summary>A daily dump was not fetched: R2Z2 is refusing this machine until <see cref="Until"/>.</summary>
    public sealed class R2Z2RefusedException(DateTimeOffset until)
        : Exception($"zKillboard is limiting this machine's requests until {until.ToLocalTime():t}")
    {
        public DateTimeOffset Until { get; } = until;
    }

    /// <summary>
    /// Sends one R2Z2 request through the gate. Null when it was not sent, because R2Z2 is
    /// refusing this machine. A 429 comes back as the response, having started the pause.
    /// </summary>
    private async Task<HttpResponseMessage?> SendR2Z2Async(
        string url, HttpCompletionOption completion, string context, CancellationToken ct)
    {
        if (R2Z2PausedUntil is not null) return null;

        await _r2z2Turn.WaitAsync(ct);
        try
        {
            // Another may start once the oldest of the last R2Z2PerSecond is a window old.
            if (_r2z2Starts.Count >= R2Z2PerSecond)
            {
                var wait = R2Z2Window - Stopwatch.GetElapsedTime(_r2z2Starts.Dequeue());
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            }
            _r2z2Starts.Enqueue(Stopwatch.GetTimestamp());
        }
        finally
        {
            _r2z2Turn.Release();
        }

        // A pause may have begun while this waited its turn.
        if (R2Z2PausedUntil is not null) return null;

        var response = await _http.GetAsync(url, completion, ct);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            await NoteRefusalAsync(response, context, ct);
        else if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound)
            lock (_r2z2Lock) _r2z2Refusals = 0;
        return response;
    }

    /// <summary>Starts the pause a 429 asks for, and logs it — once, however many requests
    /// were refused with it.</summary>
    private async Task NoteRefusalAsync(HttpResponseMessage response, string context, CancellationToken ct)
    {
        var body = "";
        try { body = (await response.Content.ReadAsStringAsync(ct)).Trim(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* the status says enough */ }

        DateTimeOffset until;
        int refusals;
        lock (_r2z2Lock)
        {
            var now = DateTimeOffset.UtcNow;

            // The rest of a batch was already in flight when the first refusal came back and
            // began the pause; they are that refusal, not more of them.
            if (_r2z2PausedUntil > now) return;

            refusals = ++_r2z2Refusals;
            var pause = RetryAfter(response, now)
                ?? TimeSpan.FromTicks(Math.Min(LongestPause.Ticks, FirstPause.Ticks << Math.Min(refusals - 1, 5)));
            until = _r2z2PausedUntil = now + pause;
        }

        errorLogger.Log(nameof(ZkillboardApiClient), $"R2Z2 refused {context}",
            $"HTTP 429{(body.Length > 0 ? $" {body}" : "")} — refusal {refusals} in a row; "
            + $"nothing more goes to R2Z2 until {until.ToLocalTime():t}");
    }

    /// <summary>The wait a 429's Retry-After asks for, if it gives one — up to two hours.</summary>
    private static TimeSpan? RetryAfter(HttpResponseMessage response, DateTimeOffset now)
    {
        var header = response.Headers.RetryAfter;
        var wait   = header?.Delta ?? (header?.Date is { } at ? at - now : null);
        return wait is { } w && w > TimeSpan.Zero ? (w < 2 * LongestPause ? w : 2 * LongestPause) : null;
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
    ///
    /// <para>Throws <see cref="R2Z2RefusedException"/>, having fetched nothing, while R2Z2 is
    /// refusing this machine.</para>
    /// </summary>
    public async IAsyncEnumerable<ZkbFullKill> GetDailyDumpAsync(
        DateOnly date,
        IReadOnlySet<long>? trackedCharacterIds = null,
        IReadOnlySet<long>? trackedCorpIds = null,
        DumpStatus? status = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var url = $"https://r2z2.zkillboard.com/history/raw/{date:yyyyMMdd}.json";

        using var response = await SendR2Z2Async(url, HttpCompletionOption.ResponseHeadersRead, $"GetDailyDumpAsync {date:yyyyMMdd}", ct);
        if (response is null || response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new R2Z2RefusedException(R2Z2PausedUntil ?? DateTimeOffset.UtcNow);
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
    /// ⚠️ Including when a probe is refused or fails partway. Those once read as "expired",
    /// the same as a 404, so a search that R2Z2 started refusing walked its lower bound up to
    /// the head — and the firehose, seeded there, would skip everything between.
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
            if (entry.Answer is R2Z2Answer.Refused or R2Z2Answer.Failed)
                return null;
            if (entry.Kill is null || entry.Kill.Kill.KillMailTime < target)
                lo = mid + 1;   // expired (so certainly older) or genuinely earlier
            else
                hi = mid;
        }

        return lo > head.Value ? head.Value : lo;
    }

    /// <summary>Current R2Z2 stream position ("now"). Used to seed the firehose cursor
    /// when there is no saved position, or the saved one has gone stale. Null when it could
    /// not be read — <see cref="R2Z2PausedUntil"/> says whether R2Z2 is refusing.</summary>
    public async Task<long?> GetSequenceAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await SendR2Z2Async("https://r2z2.zkillboard.com/ephemeral/sequence.json",
                HttpCompletionOption.ResponseContentRead, nameof(GetSequenceAsync), ct);
            if (response is null || response.StatusCode == HttpStatusCode.TooManyRequests) return null;
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<ZkbSequence>(JsonOptions, ct);
            return result?.Sequence;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            errorLogger.Log(nameof(ZkillboardApiClient), nameof(GetSequenceAsync),
                new TimeoutException("R2Z2 did not answer within 30 seconds"));
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            errorLogger.Log(nameof(ZkillboardApiClient), nameof(GetSequenceAsync), ex);
            return null;
        }
    }

    /// <summary>One entry from the R2Z2 firehose, and what R2Z2 made of the request: Missing
    /// on a 404 ("nothing at this sequence yet" — the documented signal to back off and
    /// retry), Refused while R2Z2 is limiting this machine, Failed on anything else. Response
    /// shape: <c>{"killmail_id":..,"hash":"..","esi":{ESI killmail body},"zkb":{...},
    /// "uploaded_at":..,"sequence_id":..}</c> — the killmail body is nested under "esi", not
    /// at the root.</summary>
    public async Task<StreamEntry> GetEphemeralAsync(long sequenceId, CancellationToken ct = default)
    {
        var url     = $"https://r2z2.zkillboard.com/ephemeral/{sequenceId}.json";
        var context = $"GetEphemeralAsync {sequenceId}";
        try
        {
            using var response = await SendR2Z2Async(url, HttpCompletionOption.ResponseContentRead, context, ct);
            if (response is null || response.StatusCode == HttpStatusCode.TooManyRequests)
                return new(R2Z2Answer.Refused, null);
            if (response.StatusCode == HttpStatusCode.NotFound) return new(R2Z2Answer.Missing, null);
            response.EnsureSuccessStatusCode();

            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var root = doc.RootElement;

            var hash = root.TryGetProperty("hash", out var h) ? h.GetString() : null;
            if (string.IsNullOrEmpty(hash)) return new(R2Z2Answer.Missing, null);

            var esiElement = root.TryGetProperty("esi", out var esi) ? esi : root;
            var kill = esiElement.Deserialize<EsiKillMailFull>(JsonOptions);
            return kill is null ? new(R2Z2Answer.Missing, null) : new(R2Z2Answer.Found, new ZkbFullKill(kill, hash));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // HttpClient's own timeout, not a cancel: a failure like any other.
            errorLogger.Log(nameof(ZkillboardApiClient), context, new TimeoutException("R2Z2 did not answer within 30 seconds"));
            return new(R2Z2Answer.Failed, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            errorLogger.Log(nameof(ZkillboardApiClient), context, ex);
            return new(R2Z2Answer.Failed, null);
        }
    }
}
