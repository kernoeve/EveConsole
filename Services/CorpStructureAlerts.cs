using System.Globalization;
using System.Text.RegularExpressions;
using EveConsole.Data;
using EveConsole.Localization;
using EveConsole.Models;
using EveConsole.Services.Pi;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

public enum CorpStructureAlertKind { Fuel, Attacked, FitMissing, FitWrong, AmmoLow, FightersLow }

/// <summary>
/// One structure alert for the Overview. <see cref="StructureId"/> is the structure it names, or
/// null for a count across a corporation's. An attack carries the notifications it stands for —
/// every copy the user's characters received — so dismissing it dismisses them all.
/// </summary>
public sealed record CorpStructureAlert(
    CorpStructureAlertKind Kind, long CorporationId, long? StructureId, string Message,
    IReadOnlyList<(long CharacterId, long NotificationId)> Notifications);

/// <summary>
/// The structure alerts: fuel running out, attacks, and structures that do not carry the fit set
/// for their type. Only for corporations with <see cref="Corporation.StructureAlerts"/> on.
/// </summary>
public static partial class CorpStructureAlerts
{
    /// <summary>The notifications that say a structure or tower is under fire, or has lost to it.</summary>
    public static readonly string[] AttackTypes =
        ["StructureUnderAttack", "StructureLostShields", "StructureLostArmor", "StructureDestroyed", "TowerAlertMsg"];

    /// <summary>
    /// How far back an attack is still raised. ⚠️ Without a limit the first run would raise every
    /// attack the notifications hold, months of them; a day covers being away overnight, and a
    /// structure that lost its shields shows its timer on Corp Structures after that.
    /// </summary>
    public static readonly TimeSpan AttackWindow = TimeSpan.FromHours(24);

    /// <summary>Named one by one up to this many a corporation; counted beyond it.</summary>
    private const int NamedAtMost = 3;

    /// <summary>
    /// How long the fuel and fit alerts are kept before being worked out again. The Overview asks
    /// every minute, and each answer reads every structure of every corporation; what they rest
    /// on — the structure list, the assets — is polled hourly. Attacks are read on every ask.
    /// </summary>
    private static readonly TimeSpan StructureCacheFor = TimeSpan.FromMinutes(5);
    private static (DateTimeOffset At, string Key, List<CorpStructureAlert> Alerts)? _cache;
    private static readonly Lock CacheLock = new();

    /// <summary>Drops the kept fuel and fit alerts: a structure type's fit has just been set.</summary>
    public static void Forget() { lock (CacheLock) _cache = null; }

    public static async Task<List<CorpStructureAlert>> EvaluateAsync(
        IDbContextFactory<AppDbContext> dbFactory, AlertSettings s, CancellationToken ct = default)
    {
        var result = new List<CorpStructureAlert>();
        var wantStructures = s.StructureFuel || s.StructureFitMissing || s.StructureFitWrong
                          || s.StructureAmmoLow || s.StructureFightersLow;
        if (!wantStructures && !s.StructureAttacked) return result;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var corps = await db.Corporations.AsNoTracking()
            .Where(c => c.StructureAlerts)
            .Select(c => new { c.Id, c.Name })
            .ToListAsync(ct);
        if (corps.Count == 0) return result;

        var now = DateTimeOffset.UtcNow;

        // Kept for the same rules and corporations only: a change to either is worked out at once.
        var key = $"{s.StructureFuel}{s.StructureFuelDays}{s.StructureFitMissing}{s.StructureFitWrong}{s.StructureAmmoLow}"
                + $"{s.StructureAmmoPct}{s.StructureFightersLow}{s.StructureFightersPct}|{string.Join(",", corps.Select(c => c.Id))}";
        List<CorpStructureAlert>? kept;
        lock (CacheLock) kept = _cache is { } c && c.Key == key && now - c.At < StructureCacheFor ? c.Alerts : null;
        if (kept is not null)
        {
            result.AddRange(kept);
            wantStructures = false;
        }

        if (wantStructures)
        {
            var corpIds = corps.Select(c => (long)c.Id).ToList();
            var withAny = (await db.EsiCorpStructures.AsNoTracking().Where(x => corpIds.Contains(x.CorporationId))
                    .Select(x => x.CorporationId).Distinct().ToListAsync(ct))
                .Concat(await db.EsiCorpStarbases.AsNoTracking().Where(x => corpIds.Contains(x.CorporationId))
                    .Select(x => x.CorporationId).Distinct().ToListAsync(ct))
                .ToHashSet();

            foreach (var corp in corps.Where(c => withAny.Contains(c.Id)))
            {
                var infos = await CorpStructures.LoadAsync(dbFactory, corp.Id, ct);

                if (s.StructureFuel)
                {
                    var horizon = now.AddDays(Math.Max(1, s.StructureFuelDays));
                    var low = infos
                        .Where(i => (i.RunsOut is { } r && r <= horizon && (i.IsPowered || i.NeedsFuel))
                                 || (i.Kind == CorpStructureKind.Upwell && !i.IsPowered && i.NeedsFuel))
                        .OrderBy(i => i.RunsOut ?? DateTimeOffset.MinValue)
                        .ToList();
                    Add(result, CorpStructureAlertKind.Fuel, corp.Id, corp.Name, low,
                        i => i.RunsOut is { } r && r > now
                            ? string.Format(OverviewText.AlertStructureFuel, i.Name, i.SystemName, PiFormat.Duration(r - now))
                            : string.Format(OverviewText.AlertStructureFuelOut, i.Name, i.SystemName),
                        n => low[0].RunsOut is { } first && first > now
                            ? Plurals.Format(OverviewText.ResourceManager, nameof(OverviewText.AlertStructureFuelManyOther), n,
                                corp.Name, low[0].Name, PiFormat.Duration(first - now))
                            : Plurals.Format(OverviewText.ResourceManager, nameof(OverviewText.AlertStructureFuelManyOutOther), n,
                                corp.Name, low[0].Name));
                }

                var checks = infos.Where(i => i.FitCheck is { Known: true }).ToList();
                if (s.StructureFitMissing)
                {
                    var hits = checks.Where(i => i.FitCheck!.Missing.Count > 0).ToList();
                    Add(result, CorpStructureAlertKind.FitMissing, corp.Id, corp.Name, hits,
                        i => string.Format(OverviewText.AlertStructureFitMissing, i.Name, i.SystemName, Modules(i.FitCheck!.Missing)),
                        n => Plurals.Format(OverviewText.ResourceManager, nameof(OverviewText.AlertStructureFitMissingManyOther), n, corp.Name));
                }
                if (s.StructureFitWrong)
                {
                    var hits = checks.Where(i => i.FitCheck!.Wrong.Count > 0).ToList();
                    Add(result, CorpStructureAlertKind.FitWrong, corp.Id, corp.Name, hits,
                        i => string.Format(OverviewText.AlertStructureFitWrong, i.Name, i.SystemName,
                            Modules(i.FitCheck!.Instead), Modules(i.FitCheck.Wrong)),
                        n => Plurals.Format(OverviewText.ResourceManager, nameof(OverviewText.AlertStructureFitWrongManyOther), n, corp.Name));
                }
                if (s.StructureAmmoLow)
                {
                    var hits = checks.Where(i => i.FitCheck!.AmmoLow.Count > 0).ToList();
                    Add(result, CorpStructureAlertKind.AmmoLow, corp.Id, corp.Name, hits,
                        i => string.Format(OverviewText.AlertStructureAmmoLow, i.Name, i.SystemName, Stock(i.FitCheck!.AmmoLow)),
                        n => Plurals.Format(OverviewText.ResourceManager, nameof(OverviewText.AlertStructureAmmoLowManyOther), n, corp.Name));
                }
                if (s.StructureFightersLow)
                {
                    var hits = checks.Where(i => i.FitCheck!.FightersLow.Count > 0).ToList();
                    Add(result, CorpStructureAlertKind.FightersLow, corp.Id, corp.Name, hits,
                        i => string.Format(OverviewText.AlertStructureFightersLow, i.Name, i.SystemName, Stock(i.FitCheck!.FightersLow)),
                        n => Plurals.Format(OverviewText.ResourceManager, nameof(OverviewText.AlertStructureFightersLowManyOther), n, corp.Name));
                }
            }
            lock (CacheLock) _cache = (now, key, [.. result]);
        }

        if (s.StructureAttacked)
            result.AddRange(await AttacksAsync(db, corps.Select(c => (long)c.Id).ToList(), now, ct));

        return result;
    }

    /// <summary>Each of <paramref name="hits"/> on its own, or one counted line when there are
    /// more than <see cref="NamedAtMost"/> — forty structures low on fuel should be one line.</summary>
    private static void Add(List<CorpStructureAlert> into, CorpStructureAlertKind kind, long corpId, string corpName,
        List<CorpStructureInfo> hits, Func<CorpStructureInfo, string> one, Func<int, string> many)
    {
        if (hits.Count == 0) return;
        if (hits.Count > NamedAtMost)
        {
            into.Add(new CorpStructureAlert(kind, corpId, null, many(hits.Count), []));
            return;
        }
        foreach (var i in hits) into.Add(new CorpStructureAlert(kind, corpId, i.Id, one(i), []));
    }

    /// <summary>"2 × Standup Layered Armor Plating I, Standup Stasis Webifier I".</summary>
    internal static string Modules(IEnumerable<CorpFitShortfall> list) =>
        string.Join(", ", list.Select(m => m.Count > 1 ? string.Format(CorpText.CsCountOf, m.Count, m.Name) : m.Name));

    /// <summary>"Standup Heavy Missile 300 / 1,000".</summary>
    internal static string Stock(IEnumerable<CorpStockShortfall> list) =>
        string.Join(", ", list.Select(x => string.Format(CorpText.CsHaveOfWant, x.Name,
            x.Have.ToString("N0", CultureInfo.CurrentCulture), x.Want.ToString("N0", CultureInfo.CurrentCulture))));

    // ── Attacks ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Attacks on the corporations' structures and towers in the last <see cref="AttackWindow"/>,
    /// from the notifications the user's characters received: one alert per structure, its newest,
    /// standing for every copy not yet dismissed.
    /// </summary>
    private static async Task<List<CorpStructureAlert>> AttacksAsync(AppDbContext db, List<long> corpIds, DateTimeOffset now, CancellationToken ct)
    {
        var since = now - AttackWindow;
        var notes = (await db.EsiNotifications.AsNoTracking()
                .Where(n => AttackTypes.Contains(n.Type))
                .Select(n => new { n.CharacterId, n.NotificationId, n.Type, n.Timestamp, n.Text })
                .ToListAsync(ct))
            // ⚠️ Compared here, not in the query: SQLite cannot order or compare a DateTimeOffset.
            .Where(n => n.Timestamp >= since)
            .ToList();
        if (notes.Count == 0) return [];

        var dismissed = (await db.DismissedAlerts.AsNoTracking()
                .Select(d => new { d.CharacterId, d.NotificationId }).ToListAsync(ct))
            .Select(d => (d.CharacterId, d.NotificationId)).ToHashSet();
        notes = [.. notes.Where(n => !dismissed.Contains((n.CharacterId, n.NotificationId)))];
        if (notes.Count == 0) return [];

        var structures = await db.EsiCorpStructures.AsNoTracking()
            .Where(x => corpIds.Contains(x.CorporationId))
            .Select(x => new { x.StructureId, x.CorporationId, x.Name, x.TypeId, x.SystemId })
            .ToDictionaryAsync(x => x.StructureId, ct);
        var towers = (await db.EsiCorpStarbases.AsNoTracking()
                .Where(x => corpIds.Contains(x.CorporationId))
                .Select(x => new { x.StarbaseId, x.CorporationId, x.MoonId, x.SystemId })
                .ToListAsync(ct))
            .GroupBy(x => x.MoonId).ToDictionary(g => g.Key, g => g.First());

        // Each notification put to the structure it is about.
        var hits = new List<(long Key, long CorpId, int SystemId, string Name, string Type, DateTimeOffset At,
                             Dictionary<string, string> Fields, long CharacterId, long NotificationId)>();
        foreach (var n in notes)
        {
            var f = Fields(n.Text);
            if (n.Type == "TowerAlertMsg")
            {
                if (!Long(f, "moonID", out var moon) || !towers.TryGetValue(moon, out var t)) continue;
                hits.Add((t.StarbaseId, t.CorporationId, t.SystemId, "", n.Type, n.Timestamp, f, n.CharacterId, n.NotificationId));
            }
            else if (Long(f, "structureID", out var id) && structures.TryGetValue(id, out var st))
                hits.Add((st.StructureId, st.CorporationId, st.SystemId, st.Name, n.Type, n.Timestamp, f, n.CharacterId, n.NotificationId));
        }
        if (hits.Count == 0) return [];

        var systemIds = hits.Select(h => h.SystemId).Distinct().ToList();
        var systems = await db.SdeSolarSystems.AsNoTracking()
            .Where(x => systemIds.Contains(x.SolarSystemId))
            .ToDictionaryAsync(x => x.SolarSystemId, x => SdeNames.SolarSystem(x.SolarSystemId, x.Name), ct);
        var moonIds = hits.Where(h => h.Type == "TowerAlertMsg").Select(h => towers.Values.First(t => t.StarbaseId == h.Key).MoonId).Distinct().ToList();
        var moons = await db.SdeCelestials.AsNoTracking()
            .Where(c => moonIds.Contains(c.ItemId))
            .ToDictionaryAsync(c => c.ItemId, c => c.Name, ct);

        var result = new List<CorpStructureAlert>();
        foreach (var g in hits.GroupBy(h => h.Key))
        {
            var newest = g.OrderByDescending(h => h.At).First();
            var system = systems.GetValueOrDefault(newest.SystemId) ?? $"#{newest.SystemId}";
            var name   = newest.Name.Length > 0 ? newest.Name
                       : towers.Values.FirstOrDefault(t => t.StarbaseId == g.Key) is { } tw && moons.TryGetValue(tw.MoonId, out var m) ? m
                       : CorpText.CsStarbase;
            var ago    = PiFormat.Relative(newest.At, now);
            var f      = newest.Fields;

            var message = newest.Type switch
            {
                "StructureLostShields" => string.Format(OverviewText.AlertStructureLostShields, name, system, Timer(f) ?? "?", ago),
                "StructureLostArmor"   => string.Format(OverviewText.AlertStructureLostArmor, name, system, Timer(f) ?? "?", ago),
                "StructureDestroyed"   => string.Format(OverviewText.AlertStructureDestroyed, name, system, ago),
                "TowerAlertMsg"        => string.Format(OverviewText.AlertStructureAttacked, name, system,
                                              Pct(f, "shieldValue", 100), Pct(f, "armorValue", 100), Pct(f, "hullValue", 100), ago),
                _ => (f.GetValueOrDefault("allianceName") is { Length: > 0 } al ? $"{f.GetValueOrDefault("corpName")} ({al})"
                     : f.GetValueOrDefault("corpName")) is { Length: > 0 } by
                        ? string.Format(OverviewText.AlertStructureAttackedBy, name, system, by,
                              Pct(f, "shieldPercentage", 1), Pct(f, "armorPercentage", 1), Pct(f, "hullPercentage", 1), ago)
                        : string.Format(OverviewText.AlertStructureAttacked, name, system,
                              Pct(f, "shieldPercentage", 1), Pct(f, "armorPercentage", 1), Pct(f, "hullPercentage", 1), ago),
            };
            result.Add(new CorpStructureAlert(CorpStructureAlertKind.Attacked, newest.CorpId, g.Key, message,
                [.. g.Select(h => (h.CharacterId, h.NotificationId)).Distinct()]));
        }
        return [.. result.OrderByDescending(a => hits.Where(h => h.Key == a.StructureId).Max(h => h.At))];
    }

    /// <summary>The top-level "key: value" lines of a notification's YAML. Lists and nested maps
    /// are left out; an anchor ("&amp;id001 1234") gives its value.</summary>
    internal static Dictionary<string, string> Fields(string? text)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(text)) return map;
        foreach (Match m in FieldLine().Matches(text))
        {
            var v = m.Groups[2].Value.Trim();
            if (v.Length >= 2 && (v[0] == '\'' || v[0] == '"') && v[^1] == v[0]) v = v[1..^1];
            map[m.Groups[1].Value] = v;
        }
        return map;
    }

    [GeneratedRegex(@"^([A-Za-z_][A-Za-z0-9_]*):[ \t]*(?:&\w+[ \t]+)?([^\r\n]*)$", RegexOptions.Multiline)]
    private static partial Regex FieldLine();

    private static bool Long(Dictionary<string, string> f, string key, out long value)
    {
        value = 0;
        return f.TryGetValue(key, out var s) && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>A shield, armor or hull level as a whole percent; <paramref name="scale"/> 100 for
    /// a tower's 0–1 values, 1 for a structure's percentages.</summary>
    private static string Pct(Dictionary<string, string> f, string key, double scale) =>
        f.TryGetValue(key, out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? string.Format(CultureInfo.CurrentCulture, "{0:0}%", Math.Clamp(v * scale, 0, 100))
            : "?";

    /// <summary>The reinforcement timer a lost-shields or lost-armor notification gives, as a
    /// Windows file time.</summary>
    private static string? Timer(Dictionary<string, string> f)
    {
        if (!Long(f, "timestamp", out var ft) || ft < 10_000_000_000_000_000L) return null;
        try { return PiFormat.When(new DateTimeOffset(DateTime.FromFileTimeUtc(ft))); }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}
