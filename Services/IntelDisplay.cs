using EveConsole.Data;
using EveConsole.Localization;
using EveConsole.Models;
using EveConsole.Monitoring;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>
/// The words for what an intel report said besides who: its flags, the gate, and the hulls nobody
/// was named in. Shared by the system page, the map and the intel alarm so the three say it the
/// same way, in the interface language.
/// </summary>
public static class IntelDisplay
{
    private static readonly (IntelRules.IntelFlags Flag, Func<string> Word, string English)[] Words =
    [
        (IntelRules.IntelFlags.Spike,    () => MapText.IntelFlagSpike,    "spike"),
        (IntelRules.IntelFlags.Camp,     () => MapText.IntelFlagCamp,     "gate camp"),
        (IntelRules.IntelFlags.Bubbles,  () => MapText.IntelFlagBubbles,  "bubbles"),
        (IntelRules.IntelFlags.Hotdrop,  () => MapText.IntelFlagHotdrop,  "hotdrop risk"),
        (IntelRules.IntelFlags.Cyno,     () => MapText.IntelFlagCyno,     "cyno"),
        (IntelRules.IntelFlags.Wormhole, () => MapText.IntelFlagWormhole, "wormhole"),
        (IntelRules.IntelFlags.Probes,   () => MapText.IntelFlagProbes,   "combat probes"),
        (IntelRules.IntelFlags.Ess,      () => MapText.IntelFlagEss,      "at the ESS"),
        (IntelRules.IntelFlags.Skyhook,  () => MapText.IntelFlagSkyhook,  "at a skyhook"),
    ];

    /// <summary>The flags as words in the interface language, most pressing first.</summary>
    public static IReadOnlyList<string> Flags(int flags) =>
        [.. Words.Where(w => (flags & (int)w.Flag) != 0).Select(w => w.Word())];

    /// <summary>The same in English, for what the agent reads.</summary>
    public static IReadOnlyList<string> FlagsEnglish(int flags) =>
        [.. Words.Where(w => (flags & (int)w.Flag) != 0).Select(w => w.English)];

    /// <summary>"bubbles, gate camp · on the QZ-X77 gate", or null when the report said neither.</summary>
    public static string? Facts(int flags, string? gate)
    {
        var parts = new List<string>();
        if (Flags(flags) is { Count: > 0 } words) parts.Add(string.Join(", ", words));
        if (!string.IsNullOrWhiteSpace(gate)) parts.Add(string.Format(MapText.IntelOnGate, gate));
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>"3× Loki, Interdictor" as stored, back to counts and English names.</summary>
    public static IReadOnlyList<(int Count, string Name)> ParseShips(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return [];
        var list = new List<(int, string)>();
        foreach (var part in stored.Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var times = part.IndexOf("× ", StringComparison.Ordinal);
            list.Add(times > 0 && int.TryParse(part[..times], out var n) ? (n, part[(times + 2)..]) : (1, part));
        }
        return list;
    }

    /// <summary>
    /// A lookup from the English hull and class names given to the interface language's. One
    /// query for all of them, for a reader showing many reports at once; a name it does not know
    /// stays as it is.
    /// </summary>
    public static async Task<Func<string, string>> HullNamesAsync(
        AppDbContext db, IEnumerable<string> english, CancellationToken ct)
    {
        var names = english.Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (names.Count == 0) return n => n;

        var types = await db.SdeTypes.AsNoTracking()
            .Join(db.SdeGroups.AsNoTracking().Where(g => g.CategoryId == 6), t => t.GroupId, g => g.GroupId, (t, g) => t)
            .Where(t => names.Contains(t.Name)).Select(t => new { t.TypeId, t.Name }).ToListAsync(ct);
        var groups = await db.SdeGroups.AsNoTracking()
            .Where(g => g.CategoryId == 6 && names.Contains(g.Name)).Select(g => new { g.GroupId, g.Name }).ToListAsync(ct);

        var local = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in groups) local[g.Name] = SdeNames.Group(g.GroupId, g.Name);
        foreach (var t in types)  local[t.Name] = SdeNames.Type(t.TypeId, t.Name);
        return n => local.GetValueOrDefault(n, n);
    }

    /// <summary>The ships as read: "3× Loki, Interdictor", hull names in the interface language.</summary>
    public static string? Ships(string? stored, Func<string, string> hullName)
    {
        var ships = ParseShips(stored);
        return ships.Count == 0 ? null
            : string.Join(", ", ships.Select(s => s.Count > 1 ? $"{s.Count}× {hullName(s.Name)}" : hullName(s.Name)));
    }
}
