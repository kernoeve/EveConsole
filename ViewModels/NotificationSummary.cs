using System.Text.RegularExpressions;
using YamlDotNet.Serialization;

namespace EveConsole.ViewModels;

// The small pieces a notification is listed with: its leading icon and its age ("3 hours ago"),
// plus the few top-level fields the icon is chosen from. What a notification says is laid out by
// NotificationBody (the Notifications tool) and NotificationBrief (the Overview's cards).
public static class NotificationSummary
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder().Build();

    // Top-level fields we pull out of a notification's YAML "text".
    public sealed class NotifFields
    {
        public Dictionary<string, string> Scalars { get; } = new(StringComparer.OrdinalIgnoreCase);
        public long?   StructureTypeId { get; set; }
        public long?   StructureId     { get; set; }
        public string? StructureName   { get; set; }
    }

    public static NotifFields Parse(string? text)
    {
        var f = new NotifFields();
        if (string.IsNullOrWhiteSpace(text)) return f;

        object? tree;
        try { tree = Yaml.Deserialize<object>(new StringReader(text)); }
        catch { return f; }
        if (tree is not IDictionary<object, object> map) return f;

        foreach (var (k, v) in map)
        {
            var key = k?.ToString() ?? "";
            if (v is IList<object> list)
            {
                // structureShowInfoData: [ "showinfo", <typeId>, <structureId> ]
                if (key.Contains("ShowInfoData", StringComparison.OrdinalIgnoreCase)
                    && list.Count >= 2 && long.TryParse(list[1]?.ToString(), out var tId))
                    f.StructureTypeId ??= tId;
            }
            else if (v is not IDictionary<object, object>)
            {
                f.Scalars[key] = v?.ToString() ?? "";
            }
        }

        if (f.Scalars.TryGetValue("structureTypeID", out var st) && long.TryParse(st, out var sti)) f.StructureTypeId = sti;
        if (f.Scalars.TryGetValue("structureID",     out var sd) && long.TryParse(sd, out var sid)) f.StructureId = sid;
        if (f.Scalars.TryGetValue("structureName",   out var sn) && sn.Length > 0)                  f.StructureName = sn;

        // Fall back to the type id embedded in a structureLink (e.g. showinfo:35835//1050…).
        if (f.StructureTypeId is null && f.Scalars.TryGetValue("structureLink", out var link))
        {
            var m = Regex.Match(link, @"showinfo:(\d+)//");
            if (m.Success && long.TryParse(m.Groups[1].Value, out var lt)) f.StructureTypeId = lt;
        }
        return f;
    }

    // ── Icon ─────────────────────────────────────────────────────────────────────
    // Returns an images.evetech.net path (relative) plus a fallback glyph. A null path
    // means "no image — use the glyph".
    //
    // senderFirst: the Notifications tool puts this icon beside the sender's name, so apart from
    // a structure's own icon it shows the sender; the Overview list shows the character a
    // membership notification is about instead.
    public static (string? Path, string Glyph) Icon(string type, long senderId, string senderType, NotifFields f,
                                                    bool senderFirst = false)
    {
        // Structure notifications → the structure's own type icon.
        if (f.StructureTypeId is long tid && tid > 0)
            return ($"types/{tid}/icon?size=64", "▣");

        // A starbase is named by its control tower's type.
        if (type.StartsWith("Tower", StringComparison.Ordinal)
            && f.Scalars.TryGetValue("typeID", out var tv) && long.TryParse(tv, out var tower) && tower > 0)
            return ($"types/{tower}/icon?size=64", "▣");

        // Character-centric application / membership notifications → that character's portrait.
        if (!senderFirst && f.Scalars.TryGetValue("charID", out var cv) && long.TryParse(cv, out var cid) && cid > 0)
            return ($"characters/{cid}/portrait?size=64", "☺");

        // Otherwise fall back to the sender's portrait / logo. A faction's logo is served
        // under corporations/, as the entity browser's is.
        return senderType switch
        {
            "character"   when senderId > 0 => ($"characters/{senderId}/portrait?size=64", "☺"),
            "corporation" when senderId > 0 => ($"corporations/{senderId}/logo?size=64", "✦"),
            "alliance"    when senderId > 0 => ($"alliances/{senderId}/logo?size=64", "✦"),
            "faction"     when senderId > 0 => ($"corporations/{senderId}/logo?size=64", "✦"),
            _ => (null, "✉"),
        };
    }

    // ── Relative age ("45 seconds ago", "3 hours and 14 minutes ago") ─────────────
    public static string Age(DateTimeOffset ts)
    {
        var d = DateTimeOffset.UtcNow - ts;
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;

        if (d.TotalSeconds < 60) return U((int)d.TotalSeconds, "second") + " ago";
        if (d.TotalMinutes < 60) return Two((int)d.TotalMinutes, "minute", d.Seconds, "second");
        if (d.TotalHours   < 24) return Two((int)d.TotalHours,   "hour",   d.Minutes, "minute");
        if (d.TotalDays    < 30) return Two((int)d.TotalDays,    "day",    d.Hours,   "hour");
        return ts.ToLocalTime().ToString("MMM d, yyyy");
    }

    private static string U(int n, string unit) => $"{n} {unit}{(n == 1 ? "" : "s")}";
    private static string Two(int a, string au, int b, string bu) =>
        (b > 0 ? $"{U(a, au)} and {U(b, bu)}" : U(a, au)) + " ago";
}
