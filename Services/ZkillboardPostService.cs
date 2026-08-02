using System.Net;
using System.Text.Json;
using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;

namespace EveConsole.Services;

/// <summary>
/// Submits kills we have that zKillboard does not, via zKillboard's public add endpoint
/// (<c>POST https://zkillboard.com/api/killmail/add/{killID}/{hash}/</c> — no API key; the
/// killmail id + ESI hash is the credential, and zKillboard answers
/// <c>{"status":"success","new":true|false}</c>).
///
/// "zKillboard does not have it" is inferred from the absence of a ZkbKillFlag row, which
/// is only trustworthy inside a bounded window — hence three separate guards:
///
///   • A coverage floor read straight from the data — the time of the OLDEST kill we have
///     confirmed on zKillboard. Anything older than that predates everything zKillboard
///     has ever shown us, so its missing flag means nothing. Deriving this from the flags
///     rather than tracking a watermark means a historical backfill widens the window
///     automatically, and a database restored or copied from elsewhere still reports the
///     truth. Without it, ticking the box before running a backfill would try to submit
///     the entire pre-existing ESI-sourced history.
///   • A ceiling at the last day whose zKillboard dump we actually imported
///     (<see cref="ZkillboardSettings.LastFullDay"/>), plus <see cref="GraceHours"/> as a
///     floor on age. The ceiling is the one that matters: r2z2 publishes a day's dump well
///     after that day ends, so recent kills are routinely unflagged simply because the
///     dump confirming them does not exist yet. Without it, every kill since the last
///     published dump — in All scope, tens of thousands, nearly all of which arrived from
///     zKillboard's own firehose — would be submitted straight back to zKillboard.
///   • A stored hash — kills imported from zKillboard's daily dumps carry no hash (see
///     ZkillboardApiClient), but they are by definition already on zKillboard.
///
/// Passing all three only makes a kill a CANDIDATE. Each candidate is then confirmed
/// against zKillboard directly (<c>/api/killID/{id}/</c>) before anything is submitted,
/// because a kill's absence from a daily dump does not mean zKillboard lacks it — the
/// dumps run about 99.9% complete, and against a real 450K-kill database every single one
/// of the 422 dump-shortfall kills turned out to be present on zKillboard. Confirming
/// first turns "probably missing" into "actually missing", and flags the false positives
/// so they stop being reconsidered.
///
/// Every outcome we can act on is recorded as a ZkbKillFlag row so it is never submitted
/// twice. Transient failures (network, timeout, 5xx) deliberately write nothing and are
/// retried on a later cycle; a 401 "you cannot be trusted" halts submission for the rest
/// of the session rather than continuing to spend the account's trust budget.
///
/// Killmail rows themselves are never touched — this service only writes ZkbKillFlags.
/// </summary>
public sealed class ZkillboardPostService(
    IServiceScopeFactory  scopeFactory,
    ZkillboardSettings    settings,
    ZkillboardApiClient   api,
    IHttpClientFactory    httpClientFactory,
    AppErrorLogger        errorLogger) : ReactiveObject
{
    /// <summary>How old a kill must be before its missing flag is taken as real. Measured
    /// against kill time rather than row-insert time: our ESI pull picks a kill up within
    /// minutes of it happening, so the two only differ meaningfully for backfilled
    /// history, which the coverage window already handles.</summary>
    private const int GraceHours      = 6;
    private const int TickMinutes     = 2;
    private const int MaxPerCycle     = 50;
    private const int InterPostDelayMs = 1000;

    private readonly HttpClient _http = httpClientFactory.CreateClient("zkillboard");

    private CancellationTokenSource? _cts;
    private Task?                    _runTask;
    private bool                     _trustHalted;

    private string _statusText = "zKillboard posting: not started";
    public string StatusText
    {
        get => _statusText;
        private set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    /// <summary>The coverage floor, for display — refreshed every tick whether or not
    /// posting is switched on, so the settings page shows how far back the window
    /// reaches while the user decides.</summary>
    private string _coverageText = "checking…";
    public string CoverageText
    {
        get => _coverageText;
        private set => this.RaiseAndSetIfChanged(ref _coverageText, value);
    }

    public void Start()
    {
        if (_cts is not null) return;
        _cts     = new CancellationTokenSource();
        _runTask = Task.Run(() => RunAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        if (_cts is null) return;

        await _cts.CancelAsync();
        if (_runTask is not null)
            try { await _runTask; } catch (OperationCanceledException) { }

        _cts     = null;
        _runTask = null;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var coverage = await GetCoverageFloorAsync(db, ct);
                CoverageText = coverage is null
                    ? "none yet — no kills confirmed on zKillboard"
                    : DateTimeOffset.TryParse(coverage, out var c) ? c.ToString("yyyy-MM-dd") : coverage;

                if (settings.Enabled && settings.PostEnabled && !_trustHalted)
                    await PostPendingAsync(db, coverage, ct);
                else if (!_trustHalted)
                    StatusText = settings.Enabled ? "zKillboard posting: off" : "zKillboard posting: disabled";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                StatusText = $"zKillboard posting: error — {Truncate(ex.Message)}";
                errorLogger.Log(nameof(ZkillboardPostService), nameof(RunAsync), ex);
            }

            await Task.Delay(TimeSpan.FromMinutes(TickMinutes), ct);
        }
    }

    private async Task PostPendingAsync(AppDbContext db, string? coverageFloor, CancellationToken ct)
    {
        if (coverageFloor is null)
        {
            StatusText = "zKillboard posting: no kills confirmed on zKillboard yet — run a backfill first";
            return;
        }

        if (settings.LastFullDay is not { } lastFullDay)
        {
            StatusText = "zKillboard posting: no day fully imported from zKillboard yet";
            return;
        }

        var candidates = await GetCandidatesAsync(db, coverageFloor, lastFullDay, ct);
        if (candidates.Count == 0)
        {
            StatusText = $"zKillboard posting: nothing to submit (covered from {CoverageText})";
            return;
        }

        var newlyAdded  = 0;
        var duplicates  = 0;
        var rejected    = 0;
        var alreadyHeld = 0;

        foreach (var (killMailId, hash) in candidates)
        {
            ct.ThrowIfCancellationRequested();

            // Absence from a daily dump is only a hint — ask zKillboard directly before
            // submitting anything. In practice most candidates fail this check: the dumps
            // are ~99.9% complete, so the shortfall is dump omissions, not real gaps.
            var exists = await api.KillExistsOnZkbAsync(killMailId, ct);
            if (exists is null) continue;               // transient — re-evaluate next cycle
            if (exists.Value)
            {
                await ZkillboardKillImportService.MarkSeenOnZkbAsync(db, killMailId, ct);
                alreadyHeld++;
                await Task.Delay(InterPostDelayMs, ct);
                continue;
            }

            var outcome = await SubmitAsync(killMailId, hash, ct);
            if (outcome is null) break;                 // transient — leave unflagged, retry next cycle
            if (outcome.TrustHalted)
            {
                _trustHalted = true;
                StatusText   = "zKillboard posting: halted — zKillboard rejected this client as untrusted";
                break;
            }

            await RecordOutcomeAsync(db, killMailId, outcome, ct);
            if (outcome.AcceptedAsNew) newlyAdded++;
            else if (outcome.Result == "duplicate") duplicates++;
            else rejected++;

            await Task.Delay(InterPostDelayMs, ct);
        }

        await db.SaveChangesAsync(ct);

        if (!_trustHalted)
            StatusText = $"zKillboard posting: {newlyAdded:N0} submitted, {alreadyHeld + duplicates:N0} already on zKillboard"
                       + (rejected > 0 ? $", {rejected:N0} rejected" : "");
    }

    /// <summary>Time of the oldest kill we have confirmed on zKillboard, as the raw
    /// stored string — nothing older has ever been checked. Null when no kill has been
    /// confirmed yet, which means nothing is postable at all.
    ///
    /// Returned unparsed so it can go straight back into the candidate query's string
    /// comparison in the exact format the column stores.</summary>
    private static async Task<string?> GetCoverageFloorAsync(AppDbContext db, CancellationToken ct)
    {
        // COALESCE rather than a nullable projection: with no confirmed kills the
        // aggregate still returns one row, and '' is unambiguous to test for.
        var rows = await db.Database.SqlQueryRaw<CoverageFloor>("""
            SELECT COALESCE(MIN(d."KillMailTime"), '') AS "Floor"
            FROM "KillMailDetails" d
            INNER JOIN "ZkbKillFlags" f ON f."KillMailId" = d."KillMailId"
            WHERE f."SeenOnZkbAt" IS NOT NULL
            """).ToListAsync(ct);

        var floor = rows.FirstOrDefault()?.Floor;
        return string.IsNullOrEmpty(floor) ? null : floor;
    }

    private sealed class CoverageFloor
    {
        public string Floor { get; set; } = "";
    }

    /// <summary>Kills inside the coverage window, past the grace period, with a usable
    /// hash and no recorded zKillboard outcome. Raw SQL because KillMailTime is a
    /// DateTimeOffset, which EF Core's SQLite provider cannot translate in a Where.</summary>
    private static async Task<List<(int KillMailId, string Hash)>> GetCandidatesAsync(
        AppDbContext db, string coverageFloor, DateOnly lastFullDay, CancellationToken ct)
    {
        var from = coverageFloor;

        // Whichever bound is stricter: the end of the last dump-confirmed day, or the
        // grace period. In practice the dump lag dominates, but the grace period still
        // matters right after a day's dump lands.
        var byGrace = DateTimeOffset.UtcNow.AddHours(-GraceHours).ToString("yyyy-MM-dd HH:mm:ss") + "+00:00";
        var byDump  = lastFullDay.ToString("yyyy-MM-dd") + " 23:59:59+00:00";
        var cutoff  = string.CompareOrdinal(byGrace, byDump) < 0 ? byGrace : byDump;

#pragma warning disable EF1002 // the only interpolated value is our own const row cap; the two date bounds are parameterized
        return (await db.Database.SqlQueryRaw<PendingKill>($"""
            SELECT d."KillMailId" AS "KillMailId", d."KillMailHash" AS "Hash"
            FROM "KillMailDetails" d
            LEFT JOIN "ZkbKillFlags" f ON f."KillMailId" = d."KillMailId"
            WHERE f."KillMailId" IS NULL
              AND d."KillMailHash" <> ''
              AND d."KillMailTime" >= @p0
              AND d."KillMailTime" <= @p1
            ORDER BY d."KillMailTime" DESC
            LIMIT {MaxPerCycle}
            """, from, cutoff).ToListAsync(ct))
#pragma warning restore EF1002
            .Select(p => (p.KillMailId, p.Hash))
            .ToList();
    }

    private sealed class PendingKill
    {
        public int    KillMailId { get; set; }
        public string Hash       { get; set; } = "";
    }

    private sealed record PostOutcome(string Result, bool AcceptedAsNew, bool TrustHalted = false);

    /// <summary>Null for a transient failure the caller should retry later; a non-null
    /// outcome is a definitive answer worth recording.</summary>
    private async Task<PostOutcome?> SubmitAsync(int killMailId, string hash, CancellationToken ct)
    {
        var url = $"https://zkillboard.com/api/killmail/add/{killMailId}/{hash}/";
        try
        {
            using var response = await _http.PostAsync(url, content: null, ct);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return new PostOutcome("untrusted", false, TrustHalted: true);

            if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
                return new PostOutcome("rejected: invalid killmail", false);

            if (!response.IsSuccessStatusCode)
                return null; // 408 / 5xx / anything else — worth another try later

            var body = await response.Content.ReadAsStringAsync(ct);
            var isNew = TryReadNewFlag(body);

            // A non-JSON 2xx means zKillboard's form-style redirect landed on the kill
            // page — it accepted the submission, we just can't tell new from duplicate.
            return isNew switch
            {
                true  => new PostOutcome("new", true),
                false => new PostOutcome("duplicate", false),
                null  => new PostOutcome("accepted", false),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            errorLogger.Log(nameof(ZkillboardPostService), $"SubmitAsync {killMailId}", ex);
            return null;
        }
    }

    private static bool? TryReadNewFlag(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("new", out var n) && n.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? n.GetBoolean()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>zKillboard has the kill after any accepted submission, whether it was new
    /// or already known — so the seen timestamp is set in both cases, and only an outright
    /// rejection leaves it null.</summary>
    private static async Task RecordOutcomeAsync(
        AppDbContext db, int killMailId, PostOutcome outcome, CancellationToken ct)
    {
        var accepted = outcome.Result is "new" or "duplicate" or "accepted";
        var now      = DateTimeOffset.UtcNow;

        var existing = await db.ZkbKillFlags.FirstOrDefaultAsync(f => f.KillMailId == killMailId, ct);
        if (existing is null)
            db.ZkbKillFlags.Add(new ZkbKillFlag
            {
                KillMailId  = killMailId,
                SeenOnZkbAt = accepted ? now : null,
                PostedAt    = now,
                PostResult  = outcome.Result,
            });
        else
        {
            existing.PostedAt   = now;
            existing.PostResult = outcome.Result;
            if (accepted) existing.SeenOnZkbAt ??= now;
        }
    }

    private static string Truncate(string s, int max = 80) => s.Length <= max ? s : s[..max];
}
