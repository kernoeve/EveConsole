using System.Security.Cryptography;
using System.Text;
using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Services.Worklist;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>When each owner's asset list was current — see <see cref="AssetSnapshot"/>.</summary>
public static class AssetClock
{
    /// <summary>
    /// Each owner's clock for one kind of list ("assets" or "blueprints"), and whether the owner
    /// is a character or a corporation: ESI's own time for the copy last fetched, or, for an
    /// owner no poll has recorded one for yet, when that poll ran.
    ///
    /// <para>⚠️ ESI may answer an asset call with a copy most of an hour old. Measured against
    /// the poll's own time, everything done in that hour reads as already in the list, and is
    /// not: a job started ten minutes before the copy was fetched but forty after it was made
    /// still had its materials on the shelf.</para>
    /// </summary>
    public static async Task<Dictionary<long, (string Type, DateTimeOffset At)>> LoadAsync(
        AppDbContext db, string kind, CancellationToken ct)
    {
        var (charEndpoint, corpEndpoint) = kind == "blueprints"
            ? ("char.blueprints", "corp.blueprints")
            : ("char.assets", "corp.assets");

        // ⚠️ Compared and reduced in memory: a DateTimeOffset does not translate on SQLite.
        var clocks = (await db.EsiCallRecords.AsNoTracking()
                .Where(r => r.Endpoint == charEndpoint || r.Endpoint == corpEndpoint)
                .Select(r => new { r.OwnerId, r.Endpoint, r.LastCalledAt })
                .ToListAsync(ct))
            .GroupBy(r => r.OwnerId)
            .ToDictionary(g => g.Key, g => (
                Type: g.Any(r => r.Endpoint == corpEndpoint) ? "corporation" : "character",
                At:   g.Max(r => r.LastCalledAt)));

        foreach (var s in await db.AssetSnapshots.AsNoTracking().Where(s => s.Kind == kind).ToListAsync(ct))
            clocks[s.OwnerId] = (s.OwnerType, s.AsOf);
        return clocks;
    }

    /// <summary>The same, the time alone.</summary>
    public static async Task<Dictionary<long, DateTimeOffset>> TimesAsync(AppDbContext db, string kind, CancellationToken ct) =>
        (await LoadAsync(db, kind, ct)).ToDictionary(kv => kv.Key, kv => kv.Value.At);
}

/// <summary>
/// What has happened in the game since each owner's asset list was current, written into
/// <c>EsiAssets</c> so that everything reading assets — the Asset Browser, the Worklist, the Order
/// Tracker — sees it at once rather than at the next hourly poll.
///
/// <para><b>How.</b> Each poll's rows are kept as ESI gave them in <c>EsiAssetsPolled</c>, and
/// when the poll was current in <see cref="AssetSnapshot"/>. <c>EsiAssets</c> is that copy with
/// the activity since written on top. Whenever the activity changes — a jobs or contracts poll,
/// a haul ticked off, courier cargo entered — the owner's rows are copied from the kept poll and
/// written over again; the next asset poll replaces the lot, and only what happened after its
/// copy was made is written on top of it.</para>
///
/// <para><b>What.</b></para>
/// <list type="bullet">
/// <item>A job started: its materials out of the hangar (manufacturing and reactions, worked out
/// from the print's ME — ESI does not say), and its blueprint, by its own item id.</item>
/// <item>A job delivered: what it made into the hangar — items, or copies — and its blueprint
/// back, unless a copy's runs are spent.</item>
/// <item>A sell order placed: what it lists, out of the hangar. Cancelled or expired: what was left
/// on it, back. Bought on the market (wallet transactions): into the hangar where it was bought.
/// ⚠️ An item sold straight into a buy order is not: its transaction cannot be told from a sell
/// order filling, whose items left the hangar when the order was placed.</item>
/// <item>A contract made, deleted or accepted, a haul ticked off, a courier's cargo delivered:
/// <see cref="ContractLag.MovesAsync"/>, as every count read them before this existed. ⚠️ Not
/// cargo still in a courier, which is on no shelf.</item>
/// </list>
///
/// <para><b>Marked.</b> Every row added or changed carries <see cref="CharacterAsset.Pending"/>
/// and what did it. ⚠️ Where an added row is in the station is a default — a corporation's first
/// hangar division, a character's hangar — and its item id is made up (negative) until the poll
/// gives the real one; what is taken out comes off the first matching stacks there, never out of
/// a ship. A row the activity empties is gone.</para>
/// </summary>
public sealed class AssetActivity(
    IDbContextFactory<AppDbContext> dbFactory,
    ProductionCalculatorService     production,
    IndustryDemandService           demand,
    WorklistSettings                settings,
    AppErrorLogger                  errors)
{
    /// <summary>The columns both tables share, in one order.</summary>
    private const string Columns = """
        "OwnerId", "OwnerType", "ItemId", "TypeId", "LocationId", "LocationType", "LocationFlag",
        "Quantity", "IsSingleton", "IsBlueprintCopy", "RootLocationId", "RootLocationType",
        "SolarSystemId", "RegionId"
        """;

    private const int ShipCategory = 6;
    private const int OfficeFolder = 27;

    /// <summary>The app's one instance, for <see cref="Nudge"/>: the one whose
    /// <see cref="UpgradeAsync"/> ran at start.</summary>
    private static AssetActivity? s_instance;

    /// <summary>
    /// Something done in the app has changed the activity — a haul ticked off, courier cargo
    /// entered: written into the assets in the background, from wherever it was done.
    /// </summary>
    public static void Nudge()
    {
        if (s_instance is { } me) _ = Task.Run(() => me.RefreshAsync());
    }

    /// <summary>One process rewrites one owner at a time; PostgreSQL also takes a lock per owner,
    /// for the other PCs (see <see cref="RewriteAsync"/>).</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>A job's materials never change once it has started; worked out once.</summary>
    private readonly Dictionary<int, List<(int TypeId, long Units)>> _jobInputs = [];

    /// <summary>
    /// One thing done to an owner's hangar since its list was current.
    /// </summary>
    /// <param name="Units">Positive into the hangar, negative out of it.</param>
    /// <param name="ItemId">The item itself, when it is known: a job's blueprint.</param>
    /// <param name="Near">Where in the station it was taken from or put: a hangar, container or
    /// office the job names. Zero for anywhere.</param>
    private sealed record Change(
        string From, long OwnerId, string OwnerType, long Site, int TypeId, long Units,
        bool IsSingleton, long ItemId = 0, long Near = 0, bool? IsCopy = null, DateTimeOffset At = default)
    {
        public string Key => $"{From}|{Site}|{TypeId}|{Units}|{IsSingleton}|{ItemId}|{Near}|{IsCopy}";
    }

    // ── Entry points ──────────────────────────────────────────────────────────

    /// <summary>
    /// Called by an asset poll, inside its transaction, right after it has written the owner's
    /// rows as ESI gave them: keeps them, records when they were current, and writes on top
    /// what has happened since. Readers never see the rows without it.
    /// </summary>
    /// <param name="asOf">ESI's Last-Modified for the copy; the poll time when it sent none.</param>
    public async Task TakePollAsync(AppDbContext db, long ownerId, string ownerType, DateTimeOffset asOf, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (asOf > now) asOf = now;
        await LockOwnerAsync(db, ownerId, ct);

        await db.Database.ExecuteSqlRawAsync(
            """DELETE FROM "EsiAssetsPolled" WHERE "OwnerId" = {0} AND "OwnerType" = {1}""", [ownerId, ownerType], ct);
        await db.Database.ExecuteSqlRawAsync(
            $$"""INSERT INTO "EsiAssetsPolled" ({{Columns}}) SELECT {{Columns}} FROM "EsiAssets" WHERE "OwnerId" = {0} AND "OwnerType" = {1}""",
            [ownerId, ownerType], ct);

        var snap = await db.AssetSnapshots.FirstOrDefaultAsync(
            s => s.OwnerId == ownerId && s.OwnerType == ownerType && s.Kind == "assets", ct);
        if (snap is null)
            db.AssetSnapshots.Add(snap = new AssetSnapshot { OwnerId = ownerId, OwnerType = ownerType, Kind = "assets" });
        snap.AsOf       = asOf;
        snap.PolledAt   = now;
        snap.AppliedKey = "";
        await db.SaveChangesAsync(ct);

        // What has happened since, measured against the clock just written: read through this
        // same connection, so inside this transaction.
        //
        // ⚠️ Never at the cost of the poll. Should working it out fail, the rows go in as ESI gave
        // them with nothing marked applied, and the next refresh writes the activity on top.
        // Behind a savepoint: on PostgreSQL any failed statement spoils the whole transaction.
        var tx = db.Database.CurrentTransaction;
        if (tx is not null) await tx.CreateSavepointAsync("asset_activity", ct);
        try
        {
            var changes = (await ChangesAsync(db, ct)).GetValueOrDefault((ownerId, ownerType)) ?? [];
            await ApplyAsync(db, ownerId, ownerType, changes, ct);
            snap.AppliedKey = KeyOf(changes);
            await db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            if (tx is not null) await tx.RollbackToSavepointAsync("asset_activity", ct);
            db.ChangeTracker.Clear();
            errors.Log("AssetActivity", $"writing activity over the poll of {ownerType} {ownerId}", ex);
        }
    }

    /// <summary>
    /// Held by an asset poll from before its transaction to after it, so that a rewrite in this
    /// process never writes activity measured against the clock the poll is replacing.
    /// ⚠️ Taken before the transaction, never inside it: a rewrite holding this waits on the
    /// database's write lock, which the poll's transaction would hold.
    /// </summary>
    public async Task<IDisposable> HoldAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        return new Release(_gate);
    }

    private sealed class Release(SemaphoreSlim gate) : IDisposable
    {
        private int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) gate.Release(); }
    }

    /// <summary>
    /// When ESI's copy of an owner's blueprints was current, for the counts that correct the
    /// blueprint list (DeliveryLag.PrintsAsync). The blueprints themselves are not rewritten.
    /// </summary>
    public static async Task RecordBlueprintsAsync(AppDbContext db, long ownerId, string ownerType, DateTimeOffset? lastModified, CancellationToken ct)
    {
        var now  = DateTimeOffset.UtcNow;
        var asOf = lastModified is { } at && at < now ? at : now;
        var snap = await db.AssetSnapshots.FirstOrDefaultAsync(
            s => s.OwnerId == ownerId && s.OwnerType == ownerType && s.Kind == "blueprints", ct);
        if (snap is null)
            db.AssetSnapshots.Add(snap = new AssetSnapshot { OwnerId = ownerId, OwnerType = ownerType, Kind = "blueprints" });
        snap.AsOf     = asOf;
        snap.PolledAt = now;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// What an orders poll saw open: each order's first and last sighting, as of the time ESI made
    /// the copy. Called by the active-orders polls, before the rewrite they trigger.
    ///
    /// <para>⚠️ The first poll of an owner's orders knows nothing of which are new: what it finds
    /// is recorded as seen long ago, so a recently repriced order is not taken for a new one.</para>
    /// </summary>
    public static async Task SeeOrdersAsync(AppDbContext db, long ownerId, string ownerType,
        IReadOnlyCollection<long> orderIds, DateTimeOffset? lastModified, CancellationToken ct)
    {
        var now  = DateTimeOffset.UtcNow;
        var seen = lastModified is { } at && at < now ? at : now;

        var first = await db.AssetSnapshots.FirstOrDefaultAsync(
            s => s.OwnerId == ownerId && s.OwnerType == ownerType && s.Kind == "orders", ct);
        var longAgo = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var ids   = orderIds.Distinct().ToList();
        var known = await db.MarketOrderSightings.Where(o => ids.Contains(o.OrderId)).ToDictionaryAsync(o => o.OrderId, ct);
        foreach (var id in ids)
        {
            if (known.TryGetValue(id, out var o)) { if (seen > o.LastSeenAt) o.LastSeenAt = seen; }
            else db.MarketOrderSightings.Add(new MarketOrderSighting
            {
                OrderId = id, FirstSeenAt = first is null ? longAgo : seen, LastSeenAt = seen,
            });
        }

        if (first is null)
            db.AssetSnapshots.Add(first = new AssetSnapshot { OwnerId = ownerId, OwnerType = ownerType, Kind = "orders" });
        first.AsOf     = seen;
        first.PolledAt = now;
        await db.SaveChangesAsync(ct);

        // Not kept for ever: an order closed a fortnight ago is long past any asset poll.
        // ⚠️ Compared in memory: a DateTimeOffset does not translate on SQLite.
        var stale = (await db.MarketOrderSightings.ToListAsync(ct)).Where(o => now - o.LastSeenAt > TimeSpan.FromDays(14)).ToList();
        if (stale.Count > 0)
        {
            db.MarketOrderSightings.RemoveRange(stale);
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// A row added to an owner's assets after the poll's own rows — the active ship ESI leaves
    /// out — kept with them, so a rewrite does not lose it.
    /// </summary>
    public static Task KeepRowAsync(AppDbContext db, long ownerId, string ownerType, long itemId, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            $$"""
            INSERT INTO "EsiAssetsPolled" ({{Columns}})
            SELECT {{Columns}} FROM "EsiAssets" WHERE "OwnerId" = {0} AND "OwnerType" = {1} AND "ItemId" = {2}
            """, [ownerId, ownerType, itemId], ct);

    /// <summary>
    /// Writes the activity again for every owner whose activity has changed since it was last
    /// written. Called after the jobs and contracts polls, a haul ticked off, courier cargo
    /// entered. Cheap when nothing has changed: the activity is read, and nothing is written.
    /// Never throws; a failure is logged and the next call tries again.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var snaps = await db.AssetSnapshots.AsNoTracking().Where(s => s.Kind == "assets").ToListAsync(ct);
            if (snaps.Count == 0) return;

            var changes = await ChangesAsync(db, ct);
            foreach (var s in snaps)
            {
                var mine = changes.GetValueOrDefault((s.OwnerId, s.OwnerType)) ?? [];
                var key  = KeyOf(mine);
                if (key == s.AppliedKey) continue;

                try { await RewriteAsync(db, s.OwnerId, s.OwnerType, s.AsOf, mine, key, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    db.ChangeTracker.Clear();
                    errors.Log("AssetActivity", $"writing activity for {s.OwnerType} {s.OwnerId}", ex);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { errors.Log("AssetActivity", "refresh", ex); }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Once at start: an owner whose rows were written by a build before this one has no kept
    /// copy, and its rows are the poll's own — kept as they are, current as of that poll.
    /// </summary>
    public async Task UpgradeAsync(CancellationToken ct = default)
    {
        s_instance = this;   // run once at start, by the app's own instance
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var kept = (await db.AssetSnapshots.AsNoTracking().Where(s => s.Kind == "assets")
                    .Select(s => new { s.OwnerId, s.OwnerType }).ToListAsync(ct))
                .Select(s => (s.OwnerId, s.OwnerType)).ToHashSet();
            var owners = (await db.EsiAssets.AsNoTracking().Where(a => a.Pending == null)
                    .Select(a => new { a.OwnerId, a.OwnerType }).Distinct().ToListAsync(ct))
                .Select(o => (o.OwnerId, o.OwnerType))
                .Where(o => !kept.Contains(o))
                .ToList();
            if (owners.Count == 0) return;

            var polled = await AssetClock.LoadAsync(db, "assets", ct);
            foreach (var (owner, type) in owners)
            {
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                await db.Database.ExecuteSqlRawAsync(
                    """DELETE FROM "EsiAssetsPolled" WHERE "OwnerId" = {0} AND "OwnerType" = {1}""", [owner, type], ct);
                await db.Database.ExecuteSqlRawAsync(
                    $$"""INSERT INTO "EsiAssetsPolled" ({{Columns}}) SELECT {{Columns}} FROM "EsiAssets" WHERE "OwnerId" = {0} AND "OwnerType" = {1}""",
                    [owner, type], ct);
                var at = polled.TryGetValue(owner, out var c) ? c.At : DateTimeOffset.UtcNow;
                db.AssetSnapshots.Add(new AssetSnapshot
                {
                    OwnerId = owner, OwnerType = type, Kind = "assets", AsOf = at, PolledAt = at, AppliedKey = "",
                });
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                db.ChangeTracker.Clear();
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { errors.Log("AssetActivity", "keeping the existing asset polls", ex); }

        await RefreshAsync(ct);
    }

    // ── Rewriting an owner ────────────────────────────────────────────────────

    /// <summary>The owner's rows copied from its kept poll, and the activity written on top, in
    /// one transaction: nobody reads the rows half-written.</summary>
    private async Task RewriteAsync(AppDbContext db, long ownerId, string ownerType, DateTimeOffset measuredAgainst,
                                    List<Change> changes, string key, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockOwnerAsync(db, ownerId, ct);

        // ⚠️ A poll may have landed since the changes were worked out — on another PC, which this
        // process's gate does not hold back. They were measured against the clock it replaced;
        // written now, what its copy already shows would be written twice. Left to the next call.
        var asOf = (await db.AssetSnapshots.AsNoTracking()
                .Where(s => s.OwnerId == ownerId && s.OwnerType == ownerType && s.Kind == "assets")
                .Select(s => new { s.AsOf }).FirstOrDefaultAsync(ct))?.AsOf;
        if (asOf != measuredAgainst) return;

        await db.Database.ExecuteSqlRawAsync(
            """DELETE FROM "EsiAssets" WHERE "OwnerId" = {0} AND "OwnerType" = {1}""", [ownerId, ownerType], ct);
        await db.Database.ExecuteSqlRawAsync(
            $$"""INSERT INTO "EsiAssets" ({{Columns}}) SELECT {{Columns}} FROM "EsiAssetsPolled" WHERE "OwnerId" = {0} AND "OwnerType" = {1}""",
            [ownerId, ownerType], ct);

        await ApplyAsync(db, ownerId, ownerType, changes, ct);

        await db.AssetSnapshots
            .Where(s => s.OwnerId == ownerId && s.OwnerType == ownerType && s.Kind == "assets")
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.AppliedKey, key), ct);

        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
    }

    /// <summary>
    /// ⚠️ On PostgreSQL another PC may be rewriting the same owner, or polling it: the two would
    /// interleave their deletes and inserts and the second fail on a duplicate key — or, worse,
    /// succeed on half. One at a time, until the transaction ends. SQLite is one process.
    /// </summary>
    private static async Task LockOwnerAsync(AppDbContext db, long ownerId, CancellationToken ct)
    {
        if (DbEngine.IsPostgres)
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", [0x4153_5345_0000_0000L ^ ownerId], ct);
    }

    /// <summary>Writes the changes over the owner's rows as they stand: arrivals first, then what
    /// went out, each taken off what is there and never below nothing.</summary>
    private static async Task ApplyAsync(AppDbContext db, long ownerId, string ownerType, List<Change> changes, CancellationToken ct)
    {
        if (changes.Count == 0) return;

        var sites = changes.Select(c => c.Site).Distinct().ToList();
        var rows  = await db.EsiAssets
            .Where(a => a.OwnerId == ownerId && a.OwnerType == ownerType && sites.Contains(a.RootLocationId))
            .ToListAsync(ct);
        var byId = rows.ToDictionary(r => r.ItemId);

        // Which rows are ships, to leave alone what is in them.
        var typeIds = rows.Select(r => r.TypeId).Distinct().ToList();
        var ships = (await db.SdeTypes.AsNoTracking()
                .Where(t => typeIds.Contains(t.TypeId))
                .Join(db.SdeGroups.AsNoTracking(), t => t.GroupId, g => g.GroupId, (t, g) => new { t.TypeId, g.CategoryId })
                .Where(x => x.CategoryId == ShipCategory)
                .Select(x => x.TypeId).ToListAsync(ct))
            .ToHashSet();

        bool InShip(CharacterAsset row)
        {
            var at = row;
            for (var depth = 0; depth < 8 && at.LocationType == "item" && byId.TryGetValue(at.LocationId, out var parent); depth++)
            {
                if (parent.IsSingleton && ships.Contains(parent.TypeId)) return true;
                at = parent;
            }
            return false;
        }

        var places = await AssetLocations.LoadAsync(db, ct);

        // A top-level row's location type at a site, as the poll writes it: a station's, or
        // "other" for a structure — taken from a row already there where there is one.
        string TopType(long site) =>
            rows.FirstOrDefault(r => r.RootLocationId == site && r.LocationId == site)?.LocationType
            ?? (site is >= 60_000_000 and < 64_000_000 ? "station" : "other");
        string RootType(long site) =>
            rows.FirstOrDefault(r => r.RootLocationId == site)?.RootLocationType
            ?? (site is >= 60_000_000 and < 64_000_000 ? "station" : "other");

        // Where an arrival goes: inside what the activity names, if that is here; otherwise a
        // corporation's office, first division, or a character's hangar.
        (long LocationId, string LocationType, string Flag) PlaceFor(Change c)
        {
            if (c.Near != 0 && c.Near != c.Site && byId.TryGetValue(c.Near, out var inside))
            {
                var flag = inside.TypeId == OfficeFolder ? "CorpSAG1"
                    : rows.FirstOrDefault(r => r.LocationId == inside.ItemId)?.LocationFlag ?? "Unlocked";
                return (inside.ItemId, "item", flag);
            }
            if (ownerType == "corporation")
            {
                var office = rows.FirstOrDefault(r => r.RootLocationId == c.Site && r.TypeId == OfficeFolder);
                return office is null ? (c.Site, TopType(c.Site), "CorpSAG1") : (office.ItemId, "item", "CorpSAG1");
            }
            return (c.Site, TopType(c.Site), "Hangar");
        }

        static void Mark(CharacterAsset row, string kind, string from)
        {
            row.Pending     = row.Pending == "added" ? "added" : kind;
            row.PendingFrom = string.IsNullOrEmpty(row.PendingFrom) ? from
                : row.PendingFrom.Split(", ").Contains(from) ? row.PendingFrom : row.PendingFrom + ", " + from;
        }

        foreach (var c in changes.Where(c => c.Units > 0).OrderBy(c => c.At))
        {
            var (loc, locType, flag) = PlaceFor(c);
            var (system, region) = places.Resolve(c.Site, RootType(c.Site));

            CharacterAsset NewRow(long itemId, int quantity) => new()
            {
                ItemId = itemId, OwnerId = ownerId, OwnerType = ownerType, TypeId = c.TypeId,
                LocationId = loc, LocationType = locType, LocationFlag = flag,
                Quantity = quantity, IsSingleton = c.IsSingleton, IsBlueprintCopy = c.IsCopy,
                RootLocationId = c.Site, RootLocationType = RootType(c.Site),
                SolarSystemId = system, RegionId = region,
                Pending = "added", PendingFrom = c.From,
            };

            if (c.ItemId != 0)
            {
                // The item itself — a job's blueprint back from the job. Unless the list has it.
                if (byId.ContainsKey(c.ItemId)) continue;
                var row = NewRow(c.ItemId, 1);
                rows.Add(row); byId[row.ItemId] = row; db.EsiAssets.Add(row);
            }
            else if (c.IsSingleton)
            {
                // Each one its own row, as the poll lists them: a hull, a print.
                for (var n = 0; n < Math.Min(c.Units, 1000); n++)
                {
                    var id = MadeUpId($"{c.From}|{c.TypeId}|{c.Site}|{n}");
                    if (byId.ContainsKey(id)) continue;
                    var row = NewRow(id, 1);
                    rows.Add(row); byId[id] = row; db.EsiAssets.Add(row);
                }
            }
            else
            {
                // Packaged goods: onto a stack already added at that spot, or a new one.
                var stack = rows.FirstOrDefault(r => r.Pending == "added" && !r.IsSingleton && r.TypeId == c.TypeId
                                                  && r.LocationId == loc && r.LocationFlag == flag);
                if (stack is not null)
                {
                    stack.Quantity = (int)Math.Min(int.MaxValue, stack.Quantity + c.Units);
                    Mark(stack, "added", c.From);
                    continue;
                }
                var id  = MadeUpId($"{ownerId}|{c.TypeId}|{loc}|{flag}");
                var row = NewRow(id, (int)Math.Min(int.MaxValue, c.Units));
                rows.Add(row); byId[id] = row; db.EsiAssets.Add(row);
            }
        }

        foreach (var c in changes.Where(c => c.Units < 0).OrderBy(c => c.At))
        {
            if (c.ItemId != 0)
            {
                // The item itself — a job's blueprint, gone into the job.
                if (byId.TryGetValue(c.ItemId, out var row)) Remove(row);
                continue;
            }

            var left = -c.Units;
            var candidates = rows
                .Where(r => r.TypeId == c.TypeId && r.RootLocationId == c.Site && r.IsSingleton == c.IsSingleton
                         && r.Quantity > 0 && !InShip(r))
                // What the activity names first, then the poll's own stacks before anything written
                // since — so what arrived stays visible as arrived, marked with what brought it —
                // then the hangar floor before containers. The totals come out the same either way.
                .OrderByDescending(r => c.Near != 0 && r.LocationId == c.Near)
                .ThenBy(r => r.Pending == "added")
                .ThenByDescending(r => r.LocationType != "item" || (byId.TryGetValue(r.LocationId, out var p) && p.TypeId == OfficeFolder))
                .ThenBy(r => r.ItemId)
                .ToList();

            foreach (var row in candidates)
            {
                if (left <= 0) break;
                if (c.IsSingleton) { Remove(row); left--; continue; }

                var take = Math.Min(left, row.Quantity);
                row.Quantity -= (int)take;
                left -= take;
                if (row.Quantity <= 0) Remove(row);
                else Mark(row, "changed", c.From);
            }
        }

        // An item and whatever is inside it — a contracted hull's fittings go with it.
        void Remove(CharacterAsset row)
        {
            foreach (var inner in rows.Where(r => r.LocationType == "item" && r.LocationId == row.ItemId).ToList())
                Remove(inner);
            rows.Remove(row);
            byId.Remove(row.ItemId);
            db.EsiAssets.Remove(row);
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>An item id for a row the poll has not given one yet: negative, so it never meets
    /// a real one, and the same every time the same thing is written, so a rewrite keeps it.</summary>
    private static long MadeUpId(string what)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(what));
        return -(long)(BitConverter.ToUInt64(hash, 0) & 0x3FFF_FFFF_FFFF_FFFFUL) - 1;
    }

    private static string KeyOf(List<Change> changes)
    {
        if (changes.Count == 0) return "";
        var text = string.Join("\n", changes.Select(c => c.Key).Order(StringComparer.Ordinal));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];
    }

    // ── The activity ──────────────────────────────────────────────────────────

    /// <summary>Everything done since each owner's list was current, by owner.</summary>
    private async Task<Dictionary<(long OwnerId, string OwnerType), List<Change>>> ChangesAsync(AppDbContext db, CancellationToken ct)
    {
        var all    = new List<Change>();
        var clocks = await AssetClock.LoadAsync(db, "assets", ct);
        if (clocks.Count == 0) return [];

        // ── Contracts, hauls, couriers' cargo delivered ──────────────────────
        foreach (var m in await ContractLag.MovesAsync(db, ct))
        {
            // ⚠️ Not what a courier is still carrying: it is on no shelf until it is delivered.
            if (m.InTransit || m.Units == 0) continue;
            var from = m.ContractId != 0 ? $"contract {m.ContractId}" : "haul";
            all.Add(new Change(from, m.OwnerId, m.OwnerType, m.Site, m.TypeId, m.Units, m.IsSingleton, At: m.At));
        }

        await AddMarketAsync(db, clocks, all, ct);

        // ── Jobs ──────────────────────────────────────────────────────────────
        // ⚠️ A corp job is listed under the corporation and under its installer, by one id: the
        // corporation's row is the one whose hangar it works from.
        var jobs = (await db.EsiIndustryJobs.AsNoTracking().ToListAsync(ct))
            .GroupBy(j => j.JobId)
            .Select(g => g.FirstOrDefault(j => j.OwnerType == "corporation") ?? g.First())
            .Where(j => clocks.ContainsKey(j.OwnerId))
            .ToList();

        var started   = jobs.Where(j => j.StartDate > clocks[j.OwnerId].At).ToList();
        var delivered = jobs.Where(j => j.Status == "delivered" && j.CompletedDate is { } done && done > clocks[j.OwnerId].At).ToList();

        // What the prints are: original or copy, and a copy's runs left.
        var printIds = started.Concat(delivered).Select(j => j.BlueprintId).Distinct().ToList();
        var prints = printIds.Count == 0 ? [] : (await db.EsiBlueprints.AsNoTracking()
                .Where(b => printIds.Contains(b.ItemId))
                .Select(b => new { b.ItemId, b.Quantity, b.Runs, b.MaterialEfficiency }).ToListAsync(ct))
            .GroupBy(b => b.ItemId).ToDictionary(g => g.Key, g => g.First());

        // A job started and delivered since leaves its print where the snapshot had it: taking
        // it out and putting it back would leave it out, arrivals being written first.
        var roundTrip = delivered.Select(j => j.JobId).Intersect(started.Select(j => j.JobId)).ToHashSet();

        foreach (var j in started)
        {
            var from = $"job {j.JobId}";
            if (!roundTrip.Contains(j.JobId))
                all.Add(new Change(from, j.OwnerId, j.OwnerType, j.FacilityId, j.BlueprintTypeId, -1, true,
                                   ItemId: j.BlueprintId, At: j.StartDate));
            foreach (var (typeId, units) in await InputsAsync(j, prints.GetValueOrDefault(j.BlueprintId)?.MaterialEfficiency, ct))
                all.Add(new Change(from, j.OwnerId, j.OwnerType, j.FacilityId, typeId, -units, false,
                                   Near: j.BlueprintLocationId, At: j.StartDate));
        }

        var yields = await YieldsAsync(db, delivered.Select(j => j.BlueprintTypeId).Distinct().ToList(), ct);
        foreach (var j in delivered)
        {
            var from = $"job {j.JobId}";
            var at   = j.CompletedDate!.Value;
            var print = prints.GetValueOrDefault(j.BlueprintId);
            var isCopy = print is { Quantity: -2 };

            // The print back, unless a copy has spent its runs: manufacturing and invention use
            // them, copying and research run on originals.
            var spent = isCopy && j.ActivityId is 1 or 8 && print!.Runs - j.Runs <= 0;
            if (!spent && !roundTrip.Contains(j.JobId))
                all.Add(new Change(from, j.OwnerId, j.OwnerType, j.FacilityId, j.BlueprintTypeId, 1, true,
                                   ItemId: j.BlueprintId, Near: j.OutputLocationId, IsCopy: isCopy, At: at));
            else if (spent && roundTrip.Contains(j.JobId))
                all.Add(new Change(from, j.OwnerId, j.OwnerType, j.FacilityId, j.BlueprintTypeId, -1, true,
                                   ItemId: j.BlueprintId, At: at));

            switch (j.ActivityId)
            {
                // Items: runs times what a run yields — a reaction run is thousands of units.
                case 1 or 9 or 11 when j.ProductTypeId is int product:
                    var units = (long)j.Runs * yields.GetValueOrDefault((j.BlueprintTypeId, product), 1L);
                    all.Add(new Change(from, j.OwnerId, j.OwnerType, j.FacilityId, product, units, false,
                                       Near: j.OutputLocationId, At: at));
                    break;

                // Copies: one print per successful run.
                case 5 or 8 when (j.SuccessfulRuns ?? (j.ActivityId == 5 ? j.Runs : 0)) is > 0 and var made:
                    all.Add(new Change(from, j.OwnerId, j.OwnerType, j.FacilityId,
                                       j.ProductTypeId ?? (j.ActivityId == 5 ? j.BlueprintTypeId : 0), made, true,
                                       Near: j.OutputLocationId, IsCopy: true, At: at));
                    break;
            }
        }

        return all
            .Where(c => c.TypeId != 0 && c.Site > 0 && clocks.TryGetValue(c.OwnerId, out var clock) && clock.Type == c.OwnerType)
            .GroupBy(c => (c.OwnerId, c.OwnerType))
            .ToDictionary(g => g.Key, g => g.ToList());
    }

    /// <summary>A purchase, as the wallet transactions record it.</summary>
    private sealed class Bought
    {
        public long OwnerId { get; set; }
        public string OwnerType { get; set; } = "";
        public long LocationId { get; set; }
        public int TypeId { get; set; }
        public int Quantity { get; set; }
        public bool IsPersonal { get; set; }
        public DateTimeOffset Date { get; set; }
    }

    /// <summary>
    /// Sell orders placed, cancelled or expired, and purchases, since each owner's list was current.
    /// </summary>
    private static async Task AddMarketAsync(
        AppDbContext db, Dictionary<long, (string Type, DateTimeOffset At)> clocks, List<Change> all, CancellationToken ct)
    {
        var corpOf = (await db.Characters.AsNoTracking().Select(c => new { c.Id, c.CorporationId }).ToListAsync(ct))
            .ToDictionary(c => c.Id, c => (long)c.CorporationId);

        // Whose hangar: the corporation's for a corp order — listed under the corporation and
        // again under the character who placed it — the character's otherwise.
        bool OwnerOf(long ownerId, string ownerType, bool forCorp, out long owner, out string type)
        {
            (owner, type) = ownerType == "corporation" ? (ownerId, "corporation")
                : forCorp ? (corpOf.GetValueOrDefault(ownerId), "corporation")
                : (ownerId, "character");
            return owner != 0 && clocks.TryGetValue(owner, out var clock) && clock.Type == type;
        }

        // ── Sell orders ───────────────────────────────────────────────────────
        var sightings = (await db.MarketOrderSightings.AsNoTracking().ToListAsync(ct)).ToDictionary(s => s.OrderId);
        if (sightings.Count > 0)
        {
            var rows = (await db.EsiMarketOrders.AsNoTracking()
                    .Where(o => !o.IsBuyOrder)
                    .Select(o => new { o.OrderId, o.OwnerId, o.OwnerType, o.IsCorporation, o.TypeId, o.LocationId,
                                       o.VolumeTotal, o.VolumeRemain, o.Issued, o.State, o.IsHistory })
                    .ToListAsync(ct))
                .Where(o => sightings.ContainsKey(o.OrderId));

            foreach (var g in rows.GroupBy(o => o.OrderId))
            {
                var row = g.FirstOrDefault(o => o.OwnerType == "corporation") ?? g.First();
                if (!OwnerOf(row.OwnerId, row.OwnerType, g.Any(o => o.IsCorporation == true), out var owner, out var type)) continue;
                var at   = clocks[owner].At;
                var seen = sightings[g.Key];
                var from = $"order {g.Key}";

                // ⚠️ Placed since, and first seen since: a repriced order is issued again, but was
                // seen before. Its whole volume left the hangar when it was placed.
                var issued = g.Max(o => o.Issued);
                if (issued > at && seen.FirstSeenAt > at)
                    all.Add(new Change(from, owner, type, row.LocationId, row.TypeId, -g.Max(o => o.VolumeTotal), false, At: issued));

                // Closed with some left on it, and still open in a copy made since: what was left is
                // back in the hangar. A sold-out order leaves nothing; one closed before the copy,
                // the copy already has.
                if (g.FirstOrDefault(o => o.IsHistory) is { State: "cancelled" or "expired" } closed
                    && closed.VolumeRemain > 0 && seen.LastSeenAt > at)
                    all.Add(new Change(from, owner, type, row.LocationId, row.TypeId, closed.VolumeRemain, false, At: seen.LastSeenAt));
            }
        }

        // ── Purchases ─────────────────────────────────────────────────────────
        // Into the hangar where it was bought. ⚠️ Raw SQL for the date: a DateTimeOffset in a LINQ
        // Where does not translate on SQLite. A character's purchase for the corporation is in the
        // corporation's own transactions as well, and counted there.
        var floor = clocks.Values.Min(c => c.At).ToUniversalTime();
        var bought = await db.Database.SqlQueryRaw<Bought>(
                """
                SELECT "OwnerId", "OwnerType", "LocationId", "TypeId", "Quantity", "IsPersonal", "Date"
                FROM "EsiWalletTransactions" WHERE "IsBuy" = {0} AND "Date" > {1}
                """, true, floor)
            .ToListAsync(ct);
        foreach (var b in bought)
        {
            if (b.OwnerType != "corporation" && !b.IsPersonal) continue;
            if (!OwnerOf(b.OwnerId, b.OwnerType, false, out var owner, out var type)) continue;
            if (b.Date <= clocks[owner].At || b.Quantity <= 0) continue;
            all.Add(new Change("bought", owner, type, b.LocationId, b.TypeId, b.Quantity, false, At: b.Date));
        }
    }

    /// <summary>What a started job took, by the print's ME and the park's bonuses. Kept per job.</summary>
    private async Task<List<(int TypeId, long Units)>> InputsAsync(IndustryJob job, int? me, CancellationToken ct)
    {
        // Manufacturing and reactions only: research, copying and invention take the print's
        // time, and invention's datacores are not worth guessing at.
        if (job.ActivityId is not (1 or 9 or 11) || job.ProductTypeId is not int product) return [];

        lock (_jobInputs)
            if (_jobInputs.TryGetValue(job.JobId, out var known)) return known;

        List<(int, long)> inputs = [];
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var parkId = await WorklistSettings.ResolveParkIdAsync(db, settings.IndustryParkId, ct);
            if (parkId > 0)
            {
                var ctx = await production.LoadContextAsync(parkId, ct);
                inputs = [.. (await demand.JobMaterialsAsync(ctx, product, job.Runs, me, ct)).Select(kv => (kv.Key, kv.Value))];
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { errors.Log("AssetActivity", $"materials of job {job.JobId}", ex); return []; }

        lock (_jobInputs) _jobInputs[job.JobId] = inputs;
        return inputs;
    }

    private static async Task<Dictionary<(int Blueprint, int Product), long>> YieldsAsync(
        AppDbContext db, List<int> blueprintTypeIds, CancellationToken ct) =>
        blueprintTypeIds.Count == 0 ? [] : (await db.SdeBlueprintProducts.AsNoTracking()
            .Where(p => blueprintTypeIds.Contains(p.TypeId))
            .Select(p => new { p.TypeId, p.ProductTypeId, p.Quantity })
            .ToListAsync(ct))
        .GroupBy(p => (p.TypeId, p.ProductTypeId))
        .ToDictionary(g => g.Key, g => (long)Math.Max(1, g.Max(p => p.Quantity)));
}
