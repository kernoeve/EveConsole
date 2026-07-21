using System.Text;
using System.Text.RegularExpressions;
using EveConsole.Data;
using Microsoft.EntityFrameworkCore;
using YamlDotNet.Serialization;

namespace EveConsole.ViewModels;

// Turns an ESI notification's raw YAML "text" into a readable multi-line block: IDs become names
// (characters/corps/alliances via the shared resolver; systems/types/stations via the SDE;
// structures via stored names), EVE filetimes/durations become dates/spans, and HTML links become
// their text. Best-effort and generic across notification types; falls back to the raw text.
public static class NotificationFormatter
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder().Build();

    public static async Task<string> FormatAsync(
        string? text, ContractNameResolver names, IDbContextFactory<AppDbContext> dbFactory)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        object? tree;
        try { tree = Yaml.Deserialize<object>(new StringReader(text)); }
        catch { return text.Trim(); }               // not YAML we understand — show as-is
        if (tree is not IDictionary<object, object> and not IList<object>) return text.Trim();

        // Pass 1: collect ids by category.
        var entity = new HashSet<long>();
        var system = new HashSet<int>();
        var type   = new HashSet<int>();
        var station = new HashSet<int>();
        var structure = new HashSet<long>();
        var moon   = new HashSet<int>();
        Collect(tree, "", entity, system, type, station, structure, moon);

        // Resolve.
        var entityNames = entity.Count > 0 ? await names.ResolveAsync(entity) : Empty<long>();
        var moonNames   = moon.Count   > 0 ? await names.ResolveMoonsAsync(moon) : Empty<int>();
        Dictionary<int, string> systemNames = new(), typeNames = new(), stationNames = new();
        Dictionary<long, string> structureNames = new();
        if (system.Count + type.Count + station.Count + structure.Count > 0)
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            if (system.Count > 0)
                systemNames = await db.SdeSolarSystems.AsNoTracking().Where(s => system.Contains(s.SolarSystemId))
                    .ToDictionaryAsync(s => s.SolarSystemId, s => s.Name);
            if (type.Count > 0)
                typeNames = await db.SdeTypes.AsNoTracking().Where(t => type.Contains(t.TypeId))
                    .ToDictionaryAsync(t => t.TypeId, t => t.Name);
            if (station.Count > 0)
                stationNames = await db.SdeStations.AsNoTracking().Where(s => station.Contains(s.StationId))
                    .ToDictionaryAsync(s => s.StationId, s => s.Name);
            if (structure.Count > 0)
                structureNames = await db.EsiStructureNames.AsNoTracking().Where(s => structure.Contains(s.StructureId))
                    .ToDictionaryAsync(s => s.StructureId, s => s.Name);
        }

        var maps = new Maps(entityNames, systemNames, typeNames, stationNames, structureNames, moonNames);
        var sb = new StringBuilder();
        Render(tree, "", 0, maps, sb);
        return sb.ToString().TrimEnd();
    }

    private sealed record Maps(
        IReadOnlyDictionary<long, string> Entity,
        IReadOnlyDictionary<int, string>  System,
        IReadOnlyDictionary<int, string>  Type,
        IReadOnlyDictionary<int, string>  Station,
        IReadOnlyDictionary<long, string> Structure,
        IReadOnlyDictionary<int, string>  Moon);

    private static Dictionary<T, string> Empty<T>() where T : notnull => new();

    // ── Category of an id-bearing key ───────────────────────────────────────────
    private static string? Category(string key)
    {
        var k = key.ToLowerInvariant();
        if (k.EndsWith("typeid"))                                    return "type";
        if (k.EndsWith("structureid"))                              return "structure";
        if (k.EndsWith("stationid"))                                return "station";
        if (k.Contains("solarsystem") || k == "systemid" || k.EndsWith("systemid")) return "system";
        if (k.Contains("moon"))                                     return "moon";
        if (k.Contains("planet") || k.Contains("region") || k.Contains("constellation")) return null;
        if (k.Contains("char") || k.Contains("corp") || k.Contains("alliance")
            || k.Contains("sender") || k.Contains("owner") || k.Contains("startedby")
            || k.Contains("victim") || k.Contains("aggressor") || k.Contains("declaredby")
            || k.Contains("against") || k.Contains("member") || k.Contains("pilot")
            || k.Contains("ceo") || k.Contains("director") || k.Contains("applicant")
            || k.Contains("invoker") || k.Contains("killer") || k.Contains("creator")
            || k.Contains("creditor") || k.Contains("debtor"))
            return "entity";
        return null;
    }

    // A map whose KEYS are type ids (e.g. oreVolumeByType: { 45495: <volume> }).
    private static bool IsTypeKeyedMap(string parentKey)
    {
        var k = parentKey.ToLowerInvariant();
        return k.Contains("type") || k.Contains("ore") || k.Contains("volume") || k.Contains("material");
    }

    private const long StructureRange = 100_000_000_000L;   // ≥ this ⇒ an Upwell structure / item id

    private static bool Skip(string key)
    {
        var k = key.ToLowerInvariant();
        // Drop the redundant "…Link" duplicates and the showinfo/link boilerplate arrays.
        return k.EndsWith("link") || k.EndsWith("linkdata") || k.Contains("showinfo");
    }

    // ── Pass 1: collect ──────────────────────────────────────────────────────────
    private static void Collect(object? node, string key,
        HashSet<long> entity, HashSet<int> system, HashSet<int> type,
        HashSet<int> station, HashSet<long> structure, HashSet<int> moon, int depth = 0)
    {
        if (depth > 12 || node is null) return;
        switch (node)
        {
            case IDictionary<object, object> map:
                foreach (var (k, v) in map)
                {
                    var ks = k?.ToString() ?? "";
                    if (IsTypeKeyedMap(key) && int.TryParse(ks, out var tk) && tk > 0) type.Add(tk);
                    if (!Skip(ks)) Collect(v, ks, entity, system, type, station, structure, moon, depth + 1);
                }
                break;
            case IList<object> list:
                foreach (var item in list)
                    Collect(item, key, entity, system, type, station, structure, moon, depth + 1);
                break;
            case string s when long.TryParse(s, out var id) && id > 0:
                if (id >= StructureRange) { structure.Add(id); break; }   // structure/item range
                switch (Category(key))
                {
                    case "entity":    entity.Add(id); break;
                    case "system":    system.Add((int)id); break;
                    case "type":      type.Add((int)id); break;
                    case "station":   station.Add((int)id); break;
                    case "moon":      moon.Add((int)id); break;
                }
                break;
        }
    }

    // ── Pass 2: render ───────────────────────────────────────────────────────────
    private static void Render(object? node, string key, int indent, Maps maps, StringBuilder sb)
    {
        string pad = new(' ', indent * 2);
        switch (node)
        {
            case IDictionary<object, object> map:
                foreach (var (k, v) in map)
                {
                    var ks = k?.ToString() ?? "";
                    if (Skip(ks)) continue;
                    // In a type-keyed map (e.g. oreVolumeByType) the key itself is a type id → name it.
                    var label = IsTypeKeyedMap(key) && int.TryParse(ks, out var tk)
                             && maps.Type.TryGetValue(tk, out var tn) ? tn : Humanize(ks);
                    if (v is IDictionary<object, object> or IList<object>)
                    {
                        sb.Append(pad).Append(label).AppendLine(":");
                        Render(v, ks, indent + 1, maps, sb);
                    }
                    else
                    {
                        sb.Append(pad).Append(label).Append(": ")
                          .AppendLine(Scalar(ks, v?.ToString() ?? "", maps));
                    }
                }
                break;
            case IList<object> list:
                foreach (var item in list)
                {
                    if (item is IDictionary<object, object> or IList<object>)
                    {
                        sb.Append(pad).AppendLine("-");
                        Render(item, key, indent + 1, maps, sb);
                    }
                    else
                        sb.Append(pad).Append("- ").AppendLine(Scalar(key, item?.ToString() ?? "", maps));
                }
                break;
            default:
                sb.Append(pad).AppendLine(Scalar(key, node?.ToString() ?? "", maps));
                break;
        }
    }

    private static string Scalar(string key, string value, Maps maps)
    {
        if (value.Contains("<a ", StringComparison.OrdinalIgnoreCase))
            value = Regex.Replace(value, @"<a[^>]*>(.*?)</a>", "$1",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

        if (long.TryParse(value, out var id) && id > 0)
        {
            if (id >= StructureRange && maps.Structure.TryGetValue(id, out var st) && st.Length > 0)
                return st;

            switch (Category(key))
            {
                case "entity"    when maps.Entity.TryGetValue(id, out var n) && n.Length > 0: return n;
                case "system"    when id <= int.MaxValue && maps.System.TryGetValue((int)id, out var n): return n;
                case "type"      when id <= int.MaxValue && maps.Type.TryGetValue((int)id, out var n): return n;
                case "station"   when id <= int.MaxValue && maps.Station.TryGetValue((int)id, out var n): return n;
                case "moon"      when id <= int.MaxValue && maps.Moon.TryGetValue((int)id, out var n) && n.Length > 0: return n;
            }

            var k = key.ToLowerInvariant();
            if (k.EndsWith("time") || k.EndsWith("date") || k.Contains("timestamp"))
            {
                // ≥ ~1e16 ticks on a time key is an absolute EVE filetime; smaller is a duration.
                if (id >= 100_000_000_000_000_00L)
                {
                    try { return DateTime.FromFileTimeUtc(id).ToLocalTime().ToString("yyyy-MM-dd HH:mm"); }
                    catch { }
                }
                else
                {
                    try { return FormatDuration(TimeSpan.FromTicks(id)); }
                    catch { }
                }
            }
        }
        return value;
    }

    private static string FormatDuration(TimeSpan t)
    {
        if (t.TotalDays    >= 1) return $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes}m";
        if (t.TotalHours   >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes}m";
        return $"{(int)t.TotalSeconds}s";
    }

    // camelCase / lower-case key → "Title Case Words". Also used for notification type labels.
    public static string Humanize(string key)
    {
        if (string.IsNullOrEmpty(key)) return key;
        var spaced = Regex.Replace(key, @"(?<=[a-z0-9])(?=[A-Z])", " ");
        spaced = spaced.Replace("_", " ");
        spaced = Regex.Replace(spaced, @"\bID\b", "ID", RegexOptions.IgnoreCase);
        var words = spaced.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Equals("id", StringComparison.OrdinalIgnoreCase) ? "ID"
                       : char.ToUpperInvariant(w[0]) + w[1..]);
        return string.Join(" ", words);
    }
}
