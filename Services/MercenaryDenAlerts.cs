using EveConsole.Data;
using EveConsole.Localization;
using EveConsole.Models;
using EveConsole.Services.Pi;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

public enum MercenaryDenAlertKind { Anarchy, Attacked, Reinforced, NewOperation }

/// <summary>
/// One mercenary den alert for the Overview. <see cref="DenId"/> is the den it names, or null when
/// a notification could not be put to one. Attacks and new operations carry the notifications
/// they stand for, so dismissing one dismisses every copy.
/// </summary>
public sealed record MercenaryDenAlert(
    MercenaryDenAlertKind Kind, long CharacterId, long? DenId, string Message,
    IReadOnlyList<(long CharacterId, long NotificationId)> Notifications);

/// <summary>
/// The mercenary den alerts, for the user's characters' own dens: anarchy at or above the level
/// set, a den attacked or reinforced, and a new tactical operation. ⚠️ ESI tells a den's anarchy
/// only to its owner; a den of anyone else's on the corporation's skyhook is not seen.
/// </summary>
public static class MercenaryDenAlerts
{
    public static readonly string[] AttackTypes  = ["MercenaryDenAttacked", "MercenaryDenReinforced"];
    public const string NewOperationType = "MercenaryDenNewMTO";

    public static async Task<List<MercenaryDenAlert>> EvaluateAsync(
        IDbContextFactory<AppDbContext> dbFactory, AlertSettings s, CancellationToken ct = default)
    {
        var result = new List<MercenaryDenAlert>();
        if (!s.DenAnarchy && !s.DenAttacked && !s.DenNewMto) return result;

        var dens = await MercenaryDens.LoadAsync(dbFactory, ct);
        var now  = DateTimeOffset.UtcNow;

        string Name(MercenaryDenInfo d) => string.Format(OverviewText.AlertDenName, d.Owner, d.Planet);

        if (s.DenAnarchy)
            foreach (var d in dens.Where(d => d.AnarchyLevel >= Math.Clamp(s.DenAnarchyLevel, 0, 4)).OrderByDescending(d => d.AnarchyAmount))
                result.Add(new MercenaryDenAlert(MercenaryDenAlertKind.Anarchy, d.CharacterId, d.DenId,
                    string.Format(OverviewText.AlertDenAnarchy, Name(d), d.AnarchyLevel, d.AnarchyAmount), []));

        if (s.DenAttacked)
            foreach (var d in dens.Where(d => d.ReinforcedUntil is { } r && r > now))
                result.Add(new MercenaryDenAlert(MercenaryDenAlertKind.Reinforced, d.CharacterId, d.DenId,
                    string.Format(OverviewText.AlertDenReinforced, Name(d), PiFormat.When(d.ReinforcedUntil!.Value),
                        PiFormat.Relative(d.ReinforcedUntil.Value, now)), []));

        var types = new List<string>();
        if (s.DenAttacked) types.AddRange(AttackTypes);
        if (s.DenNewMto)   types.Add(NewOperationType);
        if (types.Count == 0) return result;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var since = now - CorpStructureAlerts.AttackWindow;
        var notes = (await db.EsiNotifications.AsNoTracking()
                .Where(n => types.Contains(n.Type))
                .Select(n => new { n.CharacterId, n.NotificationId, n.Type, n.Timestamp, n.Text })
                .ToListAsync(ct))
            // ⚠️ Compared here, not in the query: SQLite cannot order or compare a DateTimeOffset.
            .Where(n => n.Timestamp >= since)
            .ToList();
        if (notes.Count == 0) return result;
        var dismissed = (await db.DismissedAlerts.AsNoTracking()
                .Select(d => new { d.CharacterId, d.NotificationId }).ToListAsync(ct))
            .Select(d => (d.CharacterId, d.NotificationId)).ToHashSet();
        notes = [.. notes.Where(n => !dismissed.Contains((n.CharacterId, n.NotificationId)))];
        var owners = await db.Characters.AsNoTracking()
            .Select(c => new { c.Id, c.Name }).ToDictionaryAsync(c => c.Id, c => c.Name, ct);

        // Each notification put to its den: by the den's id where it gives one, else by the planet
        // among the receiving character's dens, else the character's only den.
        MercenaryDenInfo? DenOf(long characterId, Dictionary<string, string> f)
        {
            var mine = dens.Where(d => d.CharacterId == characterId).ToList();
            foreach (var key in new[] { "mercenaryDenID", "structureID", "itemID", "denID" })
                if (f.TryGetValue(key, out var v) && long.TryParse(v, out var id) && mine.FirstOrDefault(d => d.DenId == id) is { } byId)
                    return byId;
            if (f.TryGetValue("planetID", out var p) && long.TryParse(p, out var pid)
                && mine.FirstOrDefault(d => d.PlanetId == pid) is { } byPlanet)
                return byPlanet;
            return mine.Count == 1 ? mine[0] : null;
        }

        foreach (var g in notes
                     .Select(n => (n, f: CorpStructureAlerts.Fields(n.Text)))
                     .GroupBy(x => (x.n.Type == NewOperationType, Den: DenOf(x.n.CharacterId, x.f)?.DenId ?? -x.n.CharacterId)))
        {
            var newest = g.OrderByDescending(x => x.n.Timestamp).First();
            var den    = dens.FirstOrDefault(d => d.DenId == g.Key.Den);
            var name   = den is not null ? Name(den)
                       : string.Format(OverviewText.AlertDenUnknown, owners.GetValueOrDefault(newest.n.CharacterId) ?? $"#{newest.n.CharacterId}");
            var ago    = PiFormat.Relative(newest.n.Timestamp, now);
            var (kind, message) = newest.n.Type switch
            {
                NewOperationType         => (MercenaryDenAlertKind.NewOperation, string.Format(OverviewText.AlertDenNewMto, name, ago)),
                "MercenaryDenReinforced" => (MercenaryDenAlertKind.Reinforced,   string.Format(OverviewText.AlertDenReinforcedNote, name, ago)),
                _                        => (MercenaryDenAlertKind.Attacked,     string.Format(OverviewText.AlertDenAttacked, name, ago)),
            };
            result.Add(new MercenaryDenAlert(kind, newest.n.CharacterId, den?.DenId, message,
                [.. g.Select(x => (x.n.CharacterId, x.n.NotificationId)).Distinct()]));
        }
        return result;
    }
}
