using System.Text.RegularExpressions;
using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>One Ansiblex seen through ESI: a gate in <see cref="SystemId"/>, named for where it
/// goes.</summary>
/// <param name="ToSystemId">Where its name says it goes, or 0 when the name does not say (it is
/// not in the "A » B" form, or B is not a system).</param>
public sealed record BridgeGate(
    long StructureId, int SystemId, int ToSystemId, string Name, string? Label,
    DateTimeOffset? FuelExpires, string State, long OwnerCorporationId, string OwnerName, string SystemName = "");

/// <summary>
/// A jump bridge between two systems: the gates ESI shows for it, a hand-entered row, or both.
/// <see cref="SystemA"/> and <see cref="SystemB"/> are ordered by id; the pair is undirected.
/// </summary>
/// <summary>
/// One direction of a bridge under the rules in force since 2026-09-22 (Cradle of War): who may
/// jump it, and what the jump costs the bridge's capacitor.
/// </summary>
/// <param name="AllianceId">The alliance holding sovereignty where the jump starts — only its
/// pilots may use the bridge from here. 0 when nobody holds it (nobody can).</param>
/// <param name="CapitalSystemId">That alliance's capital, 0 when it has none (or sovereignty could
/// not be read).</param>
/// <param name="DistanceLy">Straight-line light-years from the capital to where the jump lands.</param>
/// <param name="Zone">1 to 5 by that distance; 0 when it cannot be known.</param>
public sealed record BridgeDirection(
    int FromSystemId, int ToSystemId, long AllianceId, string AllianceName,
    int CapitalSystemId, string CapitalName, double? DistanceLy, int Zone)
{
    /// <summary>What a jump this way costs, as a multiple of the ship's base cost: nothing in
    /// zone 1, then 2, 6, 9 and 15.</summary>
    public int Multiplier => JumpBridgeService.ZoneMultiplier(Zone);
}

public sealed record JumpBridge(
    int SystemA, int SystemB, string NameA, string NameB,
    IReadOnlyList<BridgeGate> Gates, int? ManualId, string? Note,
    IReadOnlyList<BridgeDirection>? Directions = null)
{
    public bool FromEsi  => Gates.Count > 0;
    public bool IsManual => ManualId is not null;

    /// <summary>ESI shows a gate at each end. With one, the far gate belongs to a corporation
    /// the app has no token for — the name still says where the bridge goes.</summary>
    public bool BothEnds => Gates.Select(g => g.SystemId).Distinct().Count() == 2;

    /// <summary>The first of the gates to run dry: the bridge stops when either end does.</summary>
    public DateTimeOffset? FuelExpires => Gates.Where(g => g.FuelExpires is not null).Min(g => g.FuelExpires);
}

public sealed record JumpBridgeList(IReadOnlyList<JumpBridge> Bridges, IReadOnlyList<BridgeGate> Unread);

public sealed record BridgeImportResult(int Added, int AlreadyThere, IReadOnlyList<string> NotRead);

/// <summary>
/// Jump bridges: the Ansiblexes ESI can see, and the ones entered by hand.
///
/// <para><b>From ESI.</b> A corporation's structures (with a token that can read them) include
/// its Ansiblexes, but nothing in ESI says where one leads. The name does: the game shows gates
/// by name, so they are named "Source » Destination - label" by near-universal convention, and
/// that is read here — as SMT and RIFT read it. The source is taken from where the structure
/// actually is, not from the name. A gate whose name does not say is listed as unread rather
/// than guessed at.</para>
///
/// <para><b>Pairs.</b> A bridge is two gates, one in each system, each named for the other. Both
/// are shown when both belong to corporations the app can read; when only one is, the bridge is
/// still known from that gate's name.</para>
///
/// <para><b>By hand.</b> Bridges ESI cannot see — other corporations', allies' — are stored in
/// <see cref="ManualJumpBridge"/>, one row per pair.</para>
///
/// <para>⚠️ Only gates in the current structure list count. The structure-name cache also holds
/// gates that have since gone (destroyed, unanchored, handed on); a name alone is no proof the
/// gate still stands.</para>
/// </summary>
public sealed class JumpBridgeService(
    IDbContextFactory<AppDbContext> dbFactory,
    Func<CancellationToken, Task<List<Api.EsiClient.EsiSovSystem>?>>? sovereignty = null,
    CorpActivityService? names = null)
{
    /// <summary>Ansiblex Jump Bridge.</summary>
    public const int AnsiblexTypeId = 35841;

    // ── Rules since 2026-09-22 (Cradle of War) ───────────────────────────────
    //
    // ⚠️ Zones do not bar anyone. They set how much of the bridge's capacitor (1,250 TJ, about
    // 200 TJ an hour back) a jump uses: the ship's base cost times the zone's multiplier. The
    // zone is the straight-line distance, in light-years, from the using alliance's capital to
    // the jump's END — so the same bridge is cheaper towards the capital than away from it. Who
    // may jump: only pilots of the alliance holding sovereignty where the jump starts; an access
    // list can narrow that, never widen it. Capitals cannot use a bridge at all, bar Rorquals,
    // freighters and jump freighters. Source: patch notes 24.01, and CCP's 2026-07-21 blog for
    // the per-class costs.

    private const double MetresPerLightYear = 9_460_730_472_580_800.0;

    public static int ZoneFor(double lightYears) =>
        lightYears <= 5 ? 1 : lightYears <= 10 ? 2 : lightYears <= 15 ? 3 : lightYears <= 20 ? 4 : 5;

    public static int ZoneMultiplier(int zone) => zone switch { 1 => 0, 2 => 2, 3 => 6, 4 => 9, 5 => 15, _ => 0 };

    /// <summary>Who holds each system and where each alliance's capital is, from ESI's public
    /// /sovereignty/systems. Read at most every half hour; the last good reading stands in when
    /// a read fails.</summary>
    private (Dictionary<int, long> Holder, Dictionary<long, int> Capital)? _sov;
    private DateTimeOffset _sovAt = DateTimeOffset.MinValue;

    private async Task<(Dictionary<int, long> Holder, Dictionary<long, int> Capital)?> SovereigntyAsync(CancellationToken ct)
    {
        if (sovereignty is null) return _sov;
        if (_sov is not null && DateTimeOffset.UtcNow - _sovAt < TimeSpan.FromMinutes(30)) return _sov;
        try
        {
            var systems = await sovereignty(ct) ?? [];
            var holder  = new Dictionary<int, long>();
            var capital = new Dictionary<long, int>();
            foreach (var s in systems)
                if (s.Claim?.Alliance is { AllianceId: > 0 } a)
                {
                    holder[s.SolarSystemId] = a.AllianceId;
                    if (a.IsCapitalSystem) capital[a.AllianceId] = s.SolarSystemId;
                }
            _sov   = (holder, capital);
            _sovAt = DateTimeOffset.UtcNow;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch
        {
            // Kept as it was; asked again on the next reading.
            _sovAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(25);
        }
        return _sov;
    }

    private Dictionary<int, (double X, double Y, double Z)>? _positions;

    /// <summary>Raised after a hand-entered bridge is added or removed, so the maps redraw.</summary>
    public event Action? Changed;

    private Dictionary<string, int>? _systemIds;
    private Dictionary<int, string>? _systemNames;

    /// <summary>System names and ids, English, loaded once.</summary>
    private async Task<(Dictionary<string, int> Ids, Dictionary<int, string> Names)> SystemsAsync(
        AppDbContext db, CancellationToken ct)
    {
        if (_systemIds is null || _systemNames is null)
        {
            var rows = await db.SdeSolarSystems.AsNoTracking()
                .Select(s => new { s.SolarSystemId, s.Name, s.X, s.Y, s.Z }).ToListAsync(ct);
            _systemNames = rows.ToDictionary(r => r.SolarSystemId, r => r.Name);
            // True 3D positions, not the map's 2D layout: the zones are straight-line light-years.
            _positions   = rows.ToDictionary(r => r.SolarSystemId, r => (r.X, r.Y, r.Z));
            _systemIds   = rows.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                               .ToDictionary(g => g.Key, g => g.First().SolarSystemId, StringComparer.OrdinalIgnoreCase);
        }
        return (_systemIds, _systemNames);
    }

    // ── Reading ──────────────────────────────────────────────────────────────

    public async Task<JumpBridgeList> GetAsync(CancellationToken ct = default)
    {
        using var db = dbFactory.CreateDbContext();
        return await GetAsync(db, includeManual: true, ct);
    }

    private async Task<JumpBridgeList> GetAsync(AppDbContext db, bool includeManual, CancellationToken ct)
    {
        var (ids, systemNames) = await SystemsAsync(db, ct);

        var gates = await db.EsiCorpStructures.AsNoTracking()
            .Where(s => s.TypeId == AnsiblexTypeId)
            .Select(s => new { s.StructureId, s.SystemId, s.Name, s.FuelExpires, s.State, s.CorporationId })
            .ToListAsync(ct);

        // The same gate can be listed by two corporations only in theory; keep one.
        gates = gates.GroupBy(g => g.StructureId).Select(g => g.First()).ToList();

        // A name from the list itself where ESI sent one, else from the structure-name cache,
        // which older lists (and polls from before names were kept) relied on.
        var structureIds = gates.Select(g => g.StructureId).ToList();
        var cached = structureIds.Count == 0 ? [] : await db.EsiStructureNames.AsNoTracking()
            .Where(n => structureIds.Contains(n.StructureId))
            .Select(n => new { n.StructureId, n.Name })
            .ToDictionaryAsync(n => n.StructureId, n => n.Name, ct);
        var known = structureIds.Count == 0 ? [] : await db.Structures.AsNoTracking()
            .Where(n => structureIds.Contains(n.StructureId))
            .Select(n => new { n.StructureId, n.Name })
            .ToDictionaryAsync(n => n.StructureId, n => n.Name, ct);

        var corpIds = gates.Select(g => (int)g.CorporationId).Distinct().ToList();
        var corps = corpIds.Count == 0 ? [] : await db.Corporations.AsNoTracking()
            .Where(c => corpIds.Contains(c.Id))
            .ToDictionaryAsync(c => (long)c.Id, c => c.Name, ct);

        var read   = new List<BridgeGate>();
        var unread = new List<BridgeGate>();
        foreach (var g in gates)
        {
            var name = !string.IsNullOrWhiteSpace(g.Name) ? g.Name
                     : cached.GetValueOrDefault(g.StructureId) is { Length: > 0 } c ? c
                     : known.GetValueOrDefault(g.StructureId) ?? "";
            var link = ParseName(name, ids);
            var to   = link is { } l && l.To != g.SystemId ? l.To : 0;
            var gate = new BridgeGate(g.StructureId, g.SystemId, to, name, link?.Label,
                                      g.FuelExpires, g.State ?? "", g.CorporationId,
                                      corps.GetValueOrDefault(g.CorporationId) ?? "",
                                      systemNames.GetValueOrDefault(g.SystemId) ?? "");
            (to > 0 ? read : unread).Add(gate);
        }

        var manual = includeManual ? await db.ManualJumpBridges.AsNoTracking().ToListAsync(ct) : [];

        // Pairs, keyed lower id first. Gates and hand-entered rows for the same pair merge.
        var pairs = new Dictionary<(int, int), (List<BridgeGate> Gates, ManualJumpBridge? Manual)>();
        static (int, int) Key(int a, int b) => a < b ? (a, b) : (b, a);

        foreach (var gate in read)
        {
            var key = Key(gate.SystemId, gate.ToSystemId);
            if (!pairs.TryGetValue(key, out var p)) pairs[key] = p = ([], null);
            p.Gates.Add(gate);
        }
        foreach (var m in manual)
        {
            var key = Key(m.FromSystemId, m.ToSystemId);
            pairs[key] = pairs.TryGetValue(key, out var p) ? (p.Gates, m) : ([], m);
        }

        // Who may jump each way, and the zone: from sovereignty, when it could be read.
        var sov = await SovereigntyAsync(ct);
        var allianceIds = sov is { } s0
            ? pairs.Keys.SelectMany(k => new[] { k.Item1, k.Item2 })
                   .Select(id => s0.Holder.GetValueOrDefault(id)).Where(a => a > 0).Distinct().ToList()
            : [];
        var allianceNames = await AllianceNamesAsync(db, allianceIds, ct);

        BridgeDirection? Direction(int from, int to)
        {
            if (sov is not { } s) return null;
            var alliance = s.Holder.GetValueOrDefault(from);
            var capital  = alliance > 0 ? s.Capital.GetValueOrDefault(alliance) : 0;
            double? ly = null;
            if (capital > 0 && _positions!.TryGetValue(capital, out var c) && _positions.TryGetValue(to, out var e))
            {
                var dx = c.X - e.X; var dy = c.Y - e.Y; var dz = c.Z - e.Z;
                ly = Math.Sqrt(dx * dx + dy * dy + dz * dz) / MetresPerLightYear;
            }
            return new BridgeDirection(from, to, alliance, allianceNames.GetValueOrDefault(alliance) ?? "",
                capital, capital > 0 ? systemNames.GetValueOrDefault(capital) ?? "" : "",
                ly, ly is double d ? ZoneFor(d) : 0);
        }

        var bridges = pairs
            .Select(kv => new JumpBridge(
                kv.Key.Item1, kv.Key.Item2,
                systemNames.GetValueOrDefault(kv.Key.Item1) ?? kv.Key.Item1.ToString(),
                systemNames.GetValueOrDefault(kv.Key.Item2) ?? kv.Key.Item2.ToString(),
                kv.Value.Gates.OrderBy(g => g.SystemId).ToList(),
                kv.Value.Manual?.Id, kv.Value.Manual?.Note,
                sov is null ? null : [Direction(kv.Key.Item1, kv.Key.Item2)!, Direction(kv.Key.Item2, kv.Key.Item1)!]))
            .OrderBy(b => b.NameA, StringComparer.OrdinalIgnoreCase)
            .ThenBy(b => b.NameB, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new JumpBridgeList(bridges, unread);
    }

    /// <summary>
    /// Alliance names from the local name cache. ⚠️ Never waits on ESI: one that is not cached yet
    /// is looked up in the background and shown by id until then.
    /// </summary>
    private async Task<Dictionary<long, string>> AllianceNamesAsync(AppDbContext db, List<long> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        var found = await db.UniverseNames.AsNoTracking()
            .Where(u => ids.Contains(u.EntityId))
            .Select(u => new { u.EntityId, u.Name })
            .ToDictionaryAsync(u => u.EntityId, u => u.Name, ct);

        var missing = ids.Where(id => !found.ContainsKey(id)).ToList();
        if (missing.Count > 0 && names is not null && _resolving.IsCompleted)
            _resolving = Task.Run(async () =>
            {
                try { await names.ResolveNamesAsync(missing); }
                catch { /* shown by id; asked again next time */ }
            });

        foreach (var id in missing)
            found[id] = string.Format(Localization.MapText.BridgeAllianceId, id);
        return found;
    }

    private Task _resolving = Task.CompletedTask;

    // ── Names ────────────────────────────────────────────────────────────────

    /// <summary>
    /// "Source » Destination - label" as the game shows an Ansiblex, and the looser forms bridge
    /// lists are shared in: "A &lt;-&gt; B", "A -&gt; B", "A → B", "A ⇄ B". The label is whatever
    /// follows " - " (a hyphen with spaces round it — a system name can hold a hyphen, never a
    /// spaced one).
    /// </summary>
    private static readonly Regex NameRx = new(
        @"^\s*(?<a>[^\s»→⇄↔<>]+)\s*(?:»|>>|<->|<=>|->|=>|→|⇄|↔)\s*(?<b>[^\s»→⇄↔<>]+)(?:\s+-\s+(?<label>.*))?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Where a gate's name says it goes; null when it does not say, or names no system.</summary>
    internal static (int From, int To, string? Label)? ParseName(string name, IReadOnlyDictionary<string, int> systems)
    {
        var m = NameRx.Match(name ?? "");
        if (!m.Success) return null;
        if (!systems.TryGetValue(m.Groups["a"].Value, out var from)) from = 0;
        if (!systems.TryGetValue(m.Groups["b"].Value, out var to)) return null;
        var label = m.Groups["label"].Success ? m.Groups["label"].Value.Trim() : null;
        return (from, to, string.IsNullOrEmpty(label) ? null : label);
    }

    // ── By hand ──────────────────────────────────────────────────────────────

    /// <summary>Adds a bridge between two systems. False when it is already there, or the two are
    /// the same system.</summary>
    public async Task<bool> AddAsync(int systemA, int systemB, string? note = null, CancellationToken ct = default)
    {
        if (systemA <= 0 || systemB <= 0 || systemA == systemB) return false;
        var (from, to) = systemA < systemB ? (systemA, systemB) : (systemB, systemA);

        // Known already — by hand, or from ESI — is not added twice.
        if ((await GetAsync(ct)).Bridges.Any(b => b.SystemA == from && b.SystemB == to)) return false;

        using var db = dbFactory.CreateDbContext();

        db.ManualJumpBridges.Add(new ManualJumpBridge
        {
            FromSystemId = from, ToSystemId = to, Note = note?.Trim() ?? "", CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        Changed?.Invoke();
        return true;
    }

    public async Task RemoveAsync(int id, CancellationToken ct = default)
    {
        using var db = dbFactory.CreateDbContext();
        await db.ManualJumpBridges.Where(b => b.Id == id).ExecuteDeleteAsync(ct);
        Changed?.Invoke();
    }

    /// <summary>
    /// Adds every bridge a pasted list names, one per line. A line counts when two of its words
    /// are systems: "A » B - label" from the game, "A B", "A &lt;-&gt; B", or a line from another
    /// tool's export with ids or notes around the names. Pairs already known — by hand or from
    /// ESI — are counted, not added again. Lines naming fewer than two systems are handed back.
    /// </summary>
    public async Task<BridgeImportResult> ImportAsync(string text, CancellationToken ct = default)
    {
        using (var db0 = dbFactory.CreateDbContext()) await SystemsAsync(db0, ct);
        var systems = _systemIds!;

        var existing = (await GetAsync(ct)).Bridges.Select(b => (b.SystemA, b.SystemB)).ToHashSet();

        var added = 0; var already = 0;
        var notRead = new List<string>();
        var pending = new List<(int, int, string?)>();

        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            // The game's form first: it carries a label worth keeping as the note.
            int a = 0, b = 0; string? label = null;
            if (ParseName(line, systems) is { From: > 0 } parsed)
                (a, b, label) = parsed;
            else
            {
                var found = Words.Split(line).Where(w => w.Length > 0)
                                 .Select(w => systems.GetValueOrDefault(w)).Where(id => id > 0)
                                 .Distinct().Take(2).ToList();
                if (found.Count == 2) (a, b) = (found[0], found[1]);
            }

            if (a <= 0 || b <= 0 || a == b) { notRead.Add(line); continue; }

            var key = a < b ? (a, b) : (b, a);
            if (existing.Contains(key) || pending.Any(p => (p.Item1, p.Item2) == key)) { already++; continue; }
            pending.Add((key.Item1, key.Item2, label));
        }

        if (pending.Count > 0)
        {
            using var db = dbFactory.CreateDbContext();
            var now = DateTimeOffset.UtcNow;
            db.ManualJumpBridges.AddRange(pending.Select(p => new ManualJumpBridge
            {
                FromSystemId = p.Item1, ToSystemId = p.Item2, Note = p.Item3 ?? "", CreatedAt = now,
            }));
            await db.SaveChangesAsync(ct);
            added = pending.Count;
            Changed?.Invoke();
        }

        return new BridgeImportResult(added, already, notRead);
    }

    /// <summary>What separates the words of a pasted line: space, tab, commas, the arrows.</summary>
    private static readonly Regex Words = new(@"[\s,;|»→⇄↔<>=]+|(?<=\s)-+(?=\s)", RegexOptions.Compiled);
}
