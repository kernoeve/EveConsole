using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;

namespace EveCortex.ViewModels;

// Condenses an ESI notification into the in-game style: a leading icon, a one-line
// summary, and a relative age — with the full detail left for a tooltip. Best-effort
// and generic: unknown types fall back to a humanized type label and a sender-based icon.
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

    // Entity (character/corp/alliance) ids referenced by known keys — for batch name resolution.
    public static IEnumerable<long> EntityIds(NotifFields f)
    {
        foreach (var (k, v) in f.Scalars)
            if (IsEntityKey(k) && long.TryParse(v, out var id) && id > 0)
                yield return id;
    }

    private static bool IsEntityKey(string key)
    {
        var k = key.ToLowerInvariant();
        return k.Contains("char") || k.Contains("corp") || k.Contains("owner")
            || k.Contains("alliance") || k.EndsWith("by") || k.Contains("ceo")
            || k.Contains("director") || k.Contains("applicant");
    }

    // ── One-liner ────────────────────────────────────────────────────────────────

    public static string OneLiner(string type, NotifFields f,
        IReadOnlyDictionary<long, string> names, IReadOnlyDictionary<long, string> structNames)
    {
        string Ent(string key) =>
            f.Scalars.TryGetValue(key, out var v) && long.TryParse(v, out var id)
            && names.TryGetValue(id, out var n) && n.Length > 0 ? n : "";

        string Structure() =>
            f.StructureId is long sid && structNames.TryGetValue(sid, out var n) && n.Length > 0
                ? n : (f.StructureName ?? "A structure");

        // "<subject> <verb> <object>" — empty subject ⇒ empty (caller supplies a fallback).
        string Line(string subject, string verb, string obj)
        {
            if (subject.Length == 0) return "";
            var s = obj.Length > 0 ? $"{subject} {verb} {obj}" : $"{subject} {verb}";
            return Regex.Replace(s, @"\s+", " ").Trim();
        }

        string charN = Ent("charID");
        string corpN = Ent("corpID");

        return type switch
        {
            "CorpAppNewMsg"      => Line(charN, "applied to join", corpN).AppendIfPlain("Someone applied to join your corp"),
            "CorpAppInvitedMsg"  => Line(charN, "was invited to", corpN).AppendIfPlain("A character was invited to your corp"),
            "CorpAppAcceptMsg" or
            "CharAppAcceptMsg"   => Line(charN, "joined", corpN).AppendIfPlain("A character joined your corp"),
            "CharAppWithdrawMsg" => (charN.Length > 0 ? $"{charN} withdrew their application" : "An application was withdrawn"),
            "CharAppRejectMsg"   => (charN.Length > 0 ? $"{charN}'s application was rejected" : "An application was rejected"),
            "CharTerminationMsg" => Line(charN, "left", corpN).AppendIfPlain("A character left your corp"),

            "OwnershipTransferred" => Ent("newOwnerCorpID") is { Length: > 0 } no
                ? $"'{Structure()}' transferred to {no}"
                : $"'{Structure()}' ownership transferred",

            "StructureOnline"        => $"{Structure()} came online",
            "StructureAnchoring"     => $"{Structure()} started anchoring",
            "StructureUnanchoring"   => $"{Structure()} started unanchoring",
            "StructureUnderAttack"   => $"{Structure()} is under attack",
            "StructureLostArmor"     => $"{Structure()} lost its armor timer",
            "StructureLostShields"   => $"{Structure()} lost its shield timer",
            "StructureWentHighPower" => $"{Structure()} went to high power",
            "StructureWentLowPower"  => $"{Structure()} went to low power",
            "StructureFuelAlert"     => $"{Structure()} is low on fuel",
            "StructureNoReagentsAlert"  => $"{Structure()} is out of reagents",
            "StructureLowReagentsAlert" => $"{Structure()} is low on reagents",
            "StructureItemsMovedToSafety" or
            "StructureItemsMovedIntoSafety" => "Items moved to asset safety",
            "StructureImpendingAbandonmentAssetsAtRisk" => "Assets at risk in an abandoned structure",
            "StructureAnchoringDenied" => $"{Structure()} anchoring denied",
            "StructureItemsDelivered"  => "Items delivered to a structure",

            "MoonminingExtractionStarted"   => "Moon extraction started",
            "MoonminingExtractionFinished"  => "Moon extraction ready to fracture",
            "MoonminingExtractionCancelled" => "Moon extraction cancelled",
            "MoonminingAutomaticFracture"   => "Moon automatically fractured",
            "MoonminingLaserFired"          => "Moon drill laser fired",

            "TowerAlertMsg" or "TowerResourceAlertMsg" => "Starbase (POS) alert",

            "CorpAllBillMsg"     => "Corporation bill issued",
            "InsurancePayoutMsg" => "Insurance payout received",
            "CloneActivationMsg2" or "CloneActivationMsg" => "Jump clone activated",
            "JumpCloneDeletedMsg1" or "JumpCloneDeletedMsg2" => "Jump clone deleted",
            "KillReportVictim"   => "You lost a ship",
            "KillReportFinalBlow" => "You got a killmail",

            "WarDeclared"           => "War declared",
            "WarInherited"          => "War inherited",
            "WarAllyInherited"      => "War ally inherited",
            "WarInvalid"            => "War declared invalid",
            "WarRetractedByConcord" => "War retracted by CONCORD",
            "WarHQRemovedFromSpace" => "War HQ removed from space",
            "OfferedToAlly"         => "Offered as a war ally",

            "CorporationGoalCreated"   => "Corp project created",
            "CorporationGoalCompleted" => "Corp project completed",
            "CorporationGoalClosed"    => "Corp project closed",

            _ => NotificationFormatter.Humanize(type),
        };
    }

    // ── Icon ─────────────────────────────────────────────────────────────────────
    // Returns an images.evetech.net path (relative) plus a fallback glyph. A null path
    // means "no image — use the glyph".
    public static (string? Path, string Glyph) Icon(string type, long senderId, string senderType, NotifFields f)
    {
        // Structure notifications → the structure's own type icon.
        if (f.StructureTypeId is long tid && tid > 0)
            return ($"types/{tid}/icon?size=64", "▣");

        // Character-centric application / membership notifications → that character's portrait.
        if (f.Scalars.TryGetValue("charID", out var cv) && long.TryParse(cv, out var cid) && cid > 0)
            return ($"characters/{cid}/portrait?size=64", "☺");

        // Otherwise fall back to the sender's portrait / logo.
        return senderType switch
        {
            "character"   when senderId > 0 => ($"characters/{senderId}/portrait?size=64", "☺"),
            "corporation" when senderId > 0 => ($"corporations/{senderId}/logo?size=64", "✦"),
            "alliance"    when senderId > 0 => ($"alliances/{senderId}/logo?size=64", "✦"),
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

file static class NotifStringExt
{
    // If the composed one-liner ended up empty (names didn't resolve), use a plain fallback.
    public static string AppendIfPlain(this string s, string fallback) =>
        string.IsNullOrWhiteSpace(s) ? fallback : s;
}
