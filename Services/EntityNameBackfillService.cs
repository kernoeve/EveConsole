using EveConsole.Api;
using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;

namespace EveConsole.Services;

/// <summary>
/// Fills the persistent UniverseNames cache ahead of demand, so screens that display
/// character/corporation/alliance names never have to resolve them on the render path.
///
/// This exists because of the zKillboard "All kills" scope: killmails now cover the whole
/// of New Eden, so a single Killmail Browser page references ~160 entities that are not
/// ours and have never been seen before. Resolving those inline made every refresh wait on
/// ESI — tolerable when it answered in under a second, minutes when it did not.
///
/// Character, corporation and alliance names are fixed at creation and cannot be changed
/// by players, so a resolved row is permanently valid and this only ever needs to look at
/// IDs that are missing. Measured against a 497K-killmail database: ~132K distinct
/// entities in total, which is ~133 requests at ESI's 1000-ID batch limit — a few minutes
/// once, then effectively nothing.
/// </summary>
public sealed class EntityNameBackfillService(
    IServiceScopeFactory scopeFactory,
    EsiClient            esi,
    AppErrorLogger       errorLogger) : ReactiveObject
{
    // ESI accepts 1000 ids per /universe/names/ call. Kept well under so one bad id
    // costs less on the bisecting retry below.
    private const int BatchSize        = 500;
    private const int InterBatchDelayMs = 400;
    private const int SweepIntervalMins = 60;
    private const int StartupDelaySecs  = 90;   // let the app's own startup work settle first

    // Upper bound on one pass, so a first run against a large database cannot turn into an
    // unbounded burst. At ~500 ids per request this is ~100 requests, a couple of minutes;
    // the hourly loop picks up whatever is left. Sized so a first-time population (~132K
    // entities measured against a 497K-killmail database) completes in a handful of passes
    // rather than most of a day.
    private const int MaxIdsPerSweep = 50_000;

    private CancellationTokenSource? _cts;
    private Task?                    _runTask;

    private string _statusText = "Name cache: not started";
    public string StatusText
    {
        get => _statusText;
        private set => this.RaiseAndSetIfChanged(ref _statusText, value);
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
        await Task.Delay(TimeSpan.FromSeconds(StartupDelaySecs), ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                StatusText = $"Name cache: error — {Truncate(ex.Message)}";
                errorLogger.Log(nameof(EntityNameBackfillService), nameof(RunAsync), ex);
            }

            await Task.Delay(TimeSpan.FromMinutes(SweepIntervalMins), ct);
        }
    }

    /// <summary>One pass: find referenced entity IDs with no cached name and resolve them.</summary>
    public async Task SweepAsync(CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var missing = await GetMissingIdsAsync(db, ct);
        if (missing.Count == 0)
        {
            StatusText = "○ Idle — all known entities resolved";
            return;
        }

        var capped   = missing.Count >= MaxIdsPerSweep;
        var resolved = 0;
        for (var offset = 0; offset < missing.Count; offset += BatchSize)
        {
            ct.ThrowIfCancellationRequested();

            var batch = missing.Skip(offset).Take(BatchSize).ToList();
            var names = await ResolveAsync(batch, ct);

            if (names.Count > 0)
            {
                await PersistAsync(db, names, ct);
                resolved += names.Count;
            }

            // Qualified as "this pass" because missing.Count is the per-sweep ceiling once
            // MaxIdsPerSweep bites — reporting it as a bare total made a capped run look
            // like the whole outstanding set was 50,000.
            StatusText = capped
                ? $"● Running — {resolved:N0} of {missing.Count:N0} this pass (more outstanding)"
                : $"● Running — {resolved:N0} of {missing.Count:N0} remaining names";
            await Task.Delay(InterBatchDelayMs, ct);
        }

        StatusText = capped
            ? $"○ Idle — added {resolved:N0} name(s) this pass, more outstanding"
            : $"○ Idle — added {resolved:N0} name(s), all known entities resolved";
    }

    /// <summary>
    /// Column names that hold a character / corporation / alliance id. Matched against the
    /// live schema rather than a hardcoded table list, so a table added later is swept
    /// automatically instead of quietly going unresolved.
    /// </summary>
    private static readonly string[] IdColumnPatterns =
    [
        "%CharacterId", "%CharId", "%CorporationId", "%CorpId", "%AllianceId",
        "InstallerId", "IssuerId", "AssigneeId", "AcceptorId", "ClientId",
        "FirstPartyId", "SecondPartyId", "TaxReceiverId", "CeoId", "CreatorId",
        "ContactId", "FromId", "SenderId", "RecipientId", "EntityId", "OwnerId",
    ];

    /// <summary>Tables excluded from the schema scan: SDE reference data holds type and
    /// location ids rather than entities, and UniverseNames is the cache itself.</summary>
    private static bool IsScannable(string table) =>
        !table.StartsWith("Sde", StringComparison.Ordinal)
        && table != "UniverseNames"
        && !table.StartsWith("sqlite_", StringComparison.Ordinal);

    /// <summary>Every (table, column) pair in the current schema that holds entity ids.</summary>
    private static async Task<List<(string Table, string Column)>> DiscoverIdColumnsAsync(
        AppDbContext db, CancellationToken ct)
    {
        var likes = string.Join(" OR ", IdColumnPatterns.Select((_, i) => $"p.name LIKE @p{i}"));
        var sql = $"""
            SELECT m.name || '.' || p.name AS "Value"
            FROM sqlite_master m
            JOIN pragma_table_info(m.name) p
            WHERE m.type = 'table' AND ({likes})
            ORDER BY m.name, p.name
            """;

#pragma warning disable EF1002 // only the LIKE patterns vary, and they are parameterized above
        var pairs = await db.Database
            .SqlQueryRaw<string>(sql, IdColumnPatterns.Cast<object>().ToArray())
            .ToListAsync(ct);
#pragma warning restore EF1002

        return pairs
            .Select(s => s.Split('.', 2))
            .Where(p => p.Length == 2 && IsScannable(p[0]))
            .Select(p => (Table: p[0], Column: p[1]))
            .ToList();
    }

    /// <summary>
    /// Entity ids referenced anywhere in the database but absent from UniverseNames.
    ///
    /// Killmail victims and final-blow attackers are collected first because they are what
    /// the Killmail Browser's list column renders — everything else (remaining attackers,
    /// wallet counterparties, contract issuers and assignees, industry installers, mail
    /// senders, contacts, standings) only shows up on a detail screen, so it is swept in
    /// the same pass but behind the visible set.
    ///
    /// Ids at or above 1e12 are player structures, which /universe/names/ cannot resolve
    /// at all — those have their own cache (EsiStructureNames) and are excluded here.
    /// </summary>
    private async Task<List<long>> GetMissingIdsAsync(AppDbContext db, CancellationToken ct)
    {
        var seen  = new HashSet<long>();
        var order = new List<long>();

        void Take(IEnumerable<long> ids)
        {
            foreach (var id in ids)
                if (seen.Add(id)) order.Add(id);
        }

        // Phase 1 — what the killmail list actually displays.
        Take(await db.Database.SqlQueryRaw<long>("""
            SELECT DISTINCT e."Value" AS "Value" FROM (
                SELECT "VictimCharId"     AS "Value" FROM "KillMailDetails" WHERE "VictimCharId"     > 0
                UNION SELECT "VictimCorpId"          FROM "KillMailDetails" WHERE "VictimCorpId"     > 0
                UNION SELECT "VictimAllianceId"      FROM "KillMailDetails" WHERE "VictimAllianceId" > 0
                UNION SELECT "CharacterId"           FROM "KillMailAttackers" WHERE "FinalBlow" = 1 AND "CharacterId"   > 0
                UNION SELECT "CorporationId"         FROM "KillMailAttackers" WHERE "FinalBlow" = 1 AND "CorporationId" > 0
                UNION SELECT "AllianceId"            FROM "KillMailAttackers" WHERE "FinalBlow" = 1 AND "AllianceId"    > 0
            ) e
            LEFT JOIN "UniverseNames" u ON u."EntityId" = e."Value"
            WHERE u."EntityId" IS NULL AND e."Value" < 1000000000000
            """).ToListAsync(ct));

        // Phase 2 — every other entity-id column in the schema.
        foreach (var (table, column) in await DiscoverIdColumnsAsync(db, ct))
        {
            if (order.Count >= MaxIdsPerSweep) break;
            ct.ThrowIfCancellationRequested();

            try
            {
#pragma warning disable EF1002 // table/column come from the schema itself, never from user input
                Take(await db.Database.SqlQueryRaw<long>($"""
                    SELECT DISTINCT t."{column}" AS "Value"
                    FROM "{table}" t
                    LEFT JOIN "UniverseNames" u ON u."EntityId" = t."{column}"
                    WHERE t."{column}" > 0 AND t."{column}" < 1000000000000
                      AND u."EntityId" IS NULL
                    LIMIT {MaxIdsPerSweep}
                    """).ToListAsync(ct));
#pragma warning restore EF1002
            }
            catch (Exception ex)
            {
                // A column that is not really an entity id (or a table mid-migration)
                // should not abort the sweep for everything else.
                errorLogger.Log(nameof(EntityNameBackfillService), $"scan {table}.{column}", ex);
            }
        }

        return order.Count > MaxIdsPerSweep ? order.Take(MaxIdsPerSweep).ToList() : order;
    }

    /// <summary>Halves a failing batch rather than falling back to one call per ID — ESI
    /// rejects the whole request over a single unresolvable ID, and fanning out to
    /// hundreds of sequential calls is exactly what made name resolution pathological.</summary>
    private async Task<List<EsiUniverseNameLong>> ResolveAsync(List<long> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        try
        {
            return await esi.GetNamesAsync(ids, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (ids.Count == 1)
            {
                // Deleted characters and closed corps never resolve. Nothing to do but
                // skip them; they will be retried on a later sweep, which is cheap.
                return [];
            }

            var half   = ids.Count / 2;
            var first  = await ResolveAsync(ids.Take(half).ToList(), ct);
            var second = await ResolveAsync(ids.Skip(half).ToList(), ct);
            return [.. first, .. second];
        }
    }

    private static async Task PersistAsync(
        AppDbContext db, List<EsiUniverseNameLong> names, CancellationToken ct)
    {
        var ids  = names.Select(n => n.Id).ToList();
        var have = (await db.UniverseNames.AsNoTracking()
                .Where(u => ids.Contains(u.EntityId))
                .Select(u => u.EntityId).ToListAsync(ct))
            .ToHashSet();

        var fresh = names
            .Where(n => !have.Contains(n.Id))
            .GroupBy(n => n.Id)
            .Select(g => new UniverseName
            {
                EntityId = g.Key,
                Name     = g.First().Name,
                Category = g.First().Category,
                PulledAt = DateTimeOffset.UtcNow,
            })
            .ToList();

        if (fresh.Count == 0) return;

        db.UniverseNames.AddRange(fresh);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }

    private static string Truncate(string s, int max = 80) => s.Length <= max ? s : s[..max];
}
