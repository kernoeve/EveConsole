using System.Globalization;
using System.Text.RegularExpressions;
using EveConsole.Models;

namespace EveConsole.Monitoring;

/// <summary>
/// Parsing rules for EVE game log lines.
///
/// A rule TABLE rather than a switch, because the log format is an undocumented
/// external contract that changes between client versions and can only be learned
/// by reading real logs. Lines no rule matches are optionally stored with
/// Kind = "unmatched" so the gaps are visible and new rules can be written.
///
/// ─── Verified against ~5,900 parsed lines from real 2023–2026 logs ──────────
/// Header:
///   ------------------------------------------------------------
///     Gamelog
///     Listener: Baltazar IV
///     Session Started: 2024.11.13 01:30:50
///   ------------------------------------------------------------
///
/// Lines are: [ YYYY.MM.DD HH:MM:SS ] (channel) payload
/// Channels seen: combat, notify, info, hint, question, warning, None
///
/// ⚠ THE ENTITY FORMAT CHANGED BETWEEN CLIENT VERSIONS. Both are live in the wild
///   depending on log age, and a parser written for one silently yields nothing on
///   the other:
///     2024 and earlier:  TehFresh[BLSFC](Purifier)
///     2026:              Huang Zi Tao [VAPOR][CA.S] Slasher
///   NPCs match neither — they are a bare name ("Hostile Frigate").
///
/// Confirmed NOT PRESENT in game logs, so don't go looking:
///   • Mining yield — no (mining) channel, no ore lines anywhere. ESI ledger only.
///   • Docking — undocking IS logged, docking is not. ESI /location/ only.
///   • Kill confirmations — no destroyed/wreck lines. Damage only.
///   • Login/logout — ESI /online/ only.
/// ─────────────────────────────────────────────────────────────────────────────
/// </summary>
public static class GameLogRules
{
    private const RegexOptions Opts =
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture;

    // ── Event kinds written to GameLogEvent.Kind ─────────────────────────────
    public const string KindDamageDealt      = "combat.damage_dealt";
    public const string KindDamageTaken      = "combat.damage_taken";
    public const string KindMissDealt        = "combat.miss_dealt";
    public const string KindMissTaken        = "combat.miss_taken";
    public const string KindEwar             = "combat.ewar";
    public const string KindRemoteAssist     = "combat.remote_assist";
    public const string KindCapsuleDestroyed = "combat.capsule_destroyed";
    public const string KindBounty           = "combat.bounty";
    public const string KindUnitsMined       = "industry.units_mined";
    public const string KindJumped           = "movement.jumped";
    public const string KindUndocked         = "movement.undocked";
    public const string KindUnmatched        = "unmatched";

    /// <summary>[ 2024.11.13 01:30:53 ] (channel) rest</summary>
    private static readonly Regex LineRx = new(
        @"^\[\s*(?<ts>\d{4}\.\d{2}\.\d{2}\s+\d{2}:\d{2}:\d{2})\s*\]\s*\((?<ch>[^)]*)\)\s*(?<body>.*)$",
        Opts);

    private static readonly Regex ListenerRx = new(@"^\s*Listener:\s*(?<name>.+?)\s*$", Opts);

    /// <summary>Colour/font markup the client embeds. Stripped before matching.</summary>
    private static readonly Regex TagRx = new(@"<[^>]*>", Opts);

    /// <summary>"4414 from Entity - Weapon - Hits" (after markup stripping).</summary>
    private static readonly Regex DamageRx = new(
        @"^(?<dmg>\d+)\s+(?<dir>from|to)\s+(?<entity>.+?)\s+-\s+(?<weapon>.+?)(?:\s+-\s+(?<quality>[A-Za-z' ]+?))?\s*$",
        Opts);

    /// <summary>Inbound: "fenix cn misses you completely - Dual 1000mm Railgun II".
    /// The weapon suffix is optional — NPC misses often omit it entirely
    /// ("Elder Blood Diviner misses you completely").</summary>
    private static readonly Regex MissTakenRx = new(
        @"^(?<entity>.+?)\s+misses\s+you\s+completely(?:\s*-\s*(?<weapon>.+?))?\.?\s*$", Opts);

    /// <summary>Outbound: "Your group of Focused Modulated Medium Energy Beam I misses
    /// Blood Apostle completely - Focused Modulated Medium Energy Beam I". Also covers
    /// single weapons and drones ("Your Vespa II misses X completely - Vespa II").</summary>
    private static readonly Regex MissDealtRx = new(
        @"^Your\s+(?:group\s+of\s+)?(?<weapon>.+?)\s+misses\s+(?<target>.+?)\s+completely\s*-\s*(?<weapon2>.+?)\s*$",
        Opts);

    /// <summary>"You mined 20 units of Fullerite-C84 with a lost residue of 20 units".
    /// Per-cycle yield with the ore name — far better than the ESI ledger, which is a
    /// daily aggregate on a ten-minute cache.</summary>
    private static readonly Regex MinedRx = new(
        @"^You\s+mined\s+(?<units>[\d,]+)\s+units?\s+of\s+(?<ore>.+?)(?:\s+with\s+a\s+lost\s+residue\s+of\s+(?<residue>[\d,]+)\s+units?)?\.?\s*$",
        Opts);

    /// <summary>"34,166 ISK added to next bounty payout (payment adjusted)". The closest
    /// thing to an NPC-kill confirmation the logs contain.</summary>
    private static readonly Regex BountyRx = new(
        @"^(?<isk>[\d,]+(?:\.\d+)?)\s+ISK\s+added\s+to\s+next\s+bounty\s+payout(?:\s*\((?<note>[^)]*)\))?\.?\s*$",
        Opts);

    /// <summary>"Warp scramble attempt from A to B"</summary>
    private static readonly Regex EwarRx = new(
        @"^(?<kind>Warp\s+(?:scramble|disruption)\s+attempt)\s+from\s+(?<src>.+?)\s+to\s+(?<dst>.+?)\s*$", Opts);

    /// <summary>"156 GJ energy neutralized Entity - Standup XL Energy Neutralizer II"</summary>
    private static readonly Regex NeutRx = new(
        @"^(?<amount>\d+)\s+GJ\s+energy\s+(?<verb>neutralized|drained)\s+(?:from\s+|by\s+)?(?<entity>.+?)\s+-\s+(?<module>.+?)\s*$",
        Opts);

    /// <summary>"Your target locks broken by Entity - Burst Jammer II"</summary>
    private static readonly Regex JamRx = new(
        @"^Your\s+target\s+locks?\s+broken\s+by\s+(?<entity>.+?)\s+-\s+(?<module>.+?)\s*$", Opts);

    /// <summary>"187 remote shield boosted by Entity - Large ... Remote Shield Booster"</summary>
    private static readonly Regex RemoteAssistRx = new(
        @"^(?<amount>\d+)\s+remote\s+(?<what>shield|armor|armour|hull|capacitor)\s+(?<verb>boosted|repaired|transmitted)\s+(?:by|to)\s+(?<entity>.+?)\s+-\s+(?<module>.+?)\s*$",
        Opts);

    /// <summary>"Jumping from UALX-3 to Y-ORBJ"</summary>
    private static readonly Regex JumpRx = new(
        @"^Jumping\s+from\s+(?<from>.+?)\s+to\s+(?<to>.+?)\s*$", Opts);

    /// <summary>"Undocking from Jita IV - Moon 4 - Caldari Navy Assembly Plant to Jita solar system."</summary>
    private static readonly Regex UndockRx = new(
        @"^Undocking\s+from\s+(?<loc>.+?)\s+to\s+(?<sys>.+?)\s+solar\s+system\.?\s*$", Opts);

    /// <summary>"Capsule belonging to ZIMPLE123 self-destructs."</summary>
    private static readonly Regex CapsuleRx = new(
        @"^Capsule\s+belonging\s+to\s+(?<name>.+?)\s+self-destructs\.?\s*$", Opts);

    /// <summary>Player entity, 2026 form: "Name [CORP][ALLI] ShipType" (alliance optional).</summary>
    private static readonly Regex EntityModernRx = new(
        @"^(?<name>.+?)\s+\[(?<corp>[^\]]+)\](?:\s*\[(?<alli>[^\]]+)\])?\s+(?<ship>[^\[\]]+?)\s*$", Opts);

    /// <summary>Player entity, 2024 form: "Name[CORP](ShipType)".</summary>
    private static readonly Regex EntityLegacyRx = new(
        @"^(?<name>.+?)\[(?<corp>[^\]]+)\]\((?<ship>[^)]+)\)\s*$", Opts);

    public sealed record ParsedLine(DateTimeOffset? Timestamp, string Channel, string Body);

    /// <summary>A combat participant. Ship type is present only for players, and only
    /// because being in combat with them means you are on grid — which is exactly what
    /// makes it observable.</summary>
    public sealed record Entity(string Name, string? Corp, string? Alliance, string? Ship);

    public static string StripMarkup(string s) =>
        TagRx.Replace(s, "").Replace("&gt;", ">").Replace("&lt;", "<").Trim();

    public static string? TryParseListener(string line)
        => ListenerRx.Match(line) is { Success: true } m ? m.Groups["name"].Value : null;

    /// <summary>Split a raw line into timestamp, channel and plain-text body.
    /// Null for header decoration and blanks.</summary>
    public static ParsedLine? TryParseLine(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var m = LineRx.Match(raw);
        if (!m.Success) return null;

        DateTimeOffset? ts = DateTimeOffset.TryParseExact(
            m.Groups["ts"].Value, "yyyy.MM.dd HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed : null;

        return new ParsedLine(ts, m.Groups["ch"].Value, StripMarkup(m.Groups["body"].Value));
    }

    /// <summary>Parse a participant, tolerating both known entity formats. Falls back
    /// to a bare name, which is what NPCs look like.</summary>
    public static Entity ParseEntity(string raw)
    {
        var s = raw.Trim();

        if (EntityLegacyRx.Match(s) is { Success: true } legacy)
            return new Entity(legacy.Groups["name"].Value.Trim(), legacy.Groups["corp"].Value,
                              null, legacy.Groups["ship"].Value.Trim());

        if (EntityModernRx.Match(s) is { Success: true } modern)
            return new Entity(modern.Groups["name"].Value.Trim(), modern.Groups["corp"].Value,
                              modern.Groups["alli"].Success ? modern.Groups["alli"].Value : null,
                              modern.Groups["ship"].Value.Trim());

        return new Entity(s, null, null, null);
    }

    /// <summary>ISO-8601 UTC, lexicographically sortable — see GameLogEvent.OccurredAt
    /// for why this is a string.</summary>
    public static string FormatTimestamp(DateTimeOffset ts) =>
        ts.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    /// <summary>
    /// Turn a parsed line into a row, or null if no rule matches.
    /// SourceFile and LineNumber are filled in by the importer.
    /// </summary>
    public static GameLogEvent? Match(ParsedLine line, long? characterId, string? characterName)
    {
        var body = line.Body;

        GameLogEvent Row(string kind) => new()
        {
            Kind          = kind,
            OccurredAt    = line.Timestamp is { } t ? FormatTimestamp(t) : "",
            CharacterId   = characterId,
            CharacterName = characterName,
            RawText       = body.Length > 500 ? body[..500] : body,
        };

        // ── (None): movement ─────────────────────────────────────────────────
        if (JumpRx.Match(body) is { Success: true } jump)
        {
            var r = Row(KindJumped);
            r.FromSystem = jump.Groups["from"].Value.Trim();
            r.ToSystem   = jump.Groups["to"].Value.Trim();
            return r;
        }

        if (UndockRx.Match(body) is { Success: true } undock)
        {
            var r = Row(KindUndocked);
            r.LocationName = undock.Groups["loc"].Value.Trim();
            r.ToSystem     = undock.Groups["sys"].Value.Trim();
            return r;
        }

        // ── (combat) ─────────────────────────────────────────────────────────
        if (line.Channel.Equals("combat", StringComparison.OrdinalIgnoreCase))
        {
            if (DamageRx.Match(body) is { Success: true } dmg
                && int.TryParse(dmg.Groups["dmg"].Value, out var amount))
            {
                var e        = ParseEntity(dmg.Groups["entity"].Value);
                var outbound = dmg.Groups["dir"].Value.Equals("to", StringComparison.OrdinalIgnoreCase);
                var r        = Row(outbound ? KindDamageDealt : KindDamageTaken);

                r.Amount  = amount;
                r.Weapon  = dmg.Groups["weapon"].Value.Trim();
                r.Quality = dmg.Groups["quality"].Success ? dmg.Groups["quality"].Value.Trim() : null;
                if (outbound) SetTarget(r, e); else SetSource(r, e);
                return r;
            }

            if (MissDealtRx.Match(body) is { Success: true } missOut)
            {
                var r = Row(KindMissDealt);
                r.Amount     = 0;
                r.Weapon     = missOut.Groups["weapon"].Value.Trim();
                r.SourceName = characterName;
                SetTarget(r, ParseEntity(missOut.Groups["target"].Value));
                return r;
            }

            if (MissTakenRx.Match(body) is { Success: true } missIn)
            {
                var r = Row(KindMissTaken);
                r.Amount = 0;
                r.Weapon = missIn.Groups["weapon"].Success ? missIn.Groups["weapon"].Value.Trim() : null;
                SetSource(r, ParseEntity(missIn.Groups["entity"].Value));
                r.TargetName = characterName;
                return r;
            }

            if (NeutRx.Match(body) is { Success: true } neut
                && int.TryParse(neut.Groups["amount"].Value, out var gj))
            {
                var r = Row(KindEwar);
                r.Amount  = gj;
                r.Quality = $"Energy {neut.Groups["verb"].Value}";
                r.Weapon  = neut.Groups["module"].Value.Trim();
                SetSource(r, ParseEntity(neut.Groups["entity"].Value));
                return r;
            }

            if (JamRx.Match(body) is { Success: true } jam)
            {
                var r = Row(KindEwar);
                r.Quality    = "Target lock broken";
                r.Weapon     = jam.Groups["module"].Value.Trim();
                r.TargetName = characterName;
                SetSource(r, ParseEntity(jam.Groups["entity"].Value));
                return r;
            }

            if (RemoteAssistRx.Match(body) is { Success: true } assist
                && int.TryParse(assist.Groups["amount"].Value, out var repAmount))
            {
                var r = Row(KindRemoteAssist);
                r.Amount  = repAmount;
                r.Quality = $"remote {assist.Groups["what"].Value} {assist.Groups["verb"].Value}";
                r.Weapon  = assist.Groups["module"].Value.Trim();
                SetSource(r, ParseEntity(assist.Groups["entity"].Value));
                return r;
            }

            if (EwarRx.Match(body) is { Success: true } ewar)
            {
                var r = Row(KindEwar);
                r.Quality = ewar.Groups["kind"].Value.Trim();
                SetSource(r, ParseEntity(ewar.Groups["src"].Value));
                SetTarget(r, ParseEntity(ewar.Groups["dst"].Value));
                return r;
            }
        }

        // ── (mining) ─────────────────────────────────────────────────────────
        if (line.Channel.Equals("mining", StringComparison.OrdinalIgnoreCase)
            && MinedRx.Match(body) is { Success: true } mined)
        {
            var r = Row(KindUnitsMined);
            r.Amount     = ParseQuantity(mined.Groups["units"].Value);
            r.TargetName = mined.Groups["ore"].Value.Trim();
            r.SourceName = characterName;
            if (mined.Groups["residue"].Success)
                r.SecondaryAmount = ParseQuantity(mined.Groups["residue"].Value);
            return r;
        }

        // ── (bounty) ─────────────────────────────────────────────────────────
        if (line.Channel.Equals("bounty", StringComparison.OrdinalIgnoreCase)
            && BountyRx.Match(body) is { Success: true } bounty)
        {
            var r = Row(KindBounty);
            r.Amount     = ParseQuantity(bounty.Groups["isk"].Value);
            r.SourceName = characterName;
            r.Quality    = bounty.Groups["note"].Success ? bounty.Groups["note"].Value.Trim() : null;
            return r;
        }

        // ── (notify) ─────────────────────────────────────────────────────────
        if (CapsuleRx.Match(body) is { Success: true } capsule)
        {
            var r = Row(KindCapsuleDestroyed);
            r.TargetName = capsule.Groups["name"].Value.Trim();
            return r;
        }

        return null;
    }

    /// <summary>Build an "unmatched" row, used when the importer is asked to record
    /// parse gaps so new rules can be written against real data.</summary>
    public static GameLogEvent Unmatched(ParsedLine line, long? characterId, string? characterName) => new()
    {
        Kind          = KindUnmatched,
        OccurredAt    = line.Timestamp is { } t ? FormatTimestamp(t) : "",
        CharacterId   = characterId,
        CharacterName = characterName,
        Quality       = line.Channel,
        RawText       = line.Body.Length > 500 ? line.Body[..500] : line.Body,
    };

    /// <summary>Quantities are thousands-separated in the log ("34,166 ISK").</summary>
    private static long? ParseQuantity(string s) =>
        long.TryParse(s.Replace(",", ""), NumberStyles.Any, CultureInfo.InvariantCulture, out var v)
            ? v : null;

    private static void SetSource(GameLogEvent r, Entity e)
    {
        r.SourceName     = e.Name;
        r.SourceShip     = e.Ship;
        r.SourceCorp     = e.Corp;
        r.SourceAlliance = e.Alliance;
    }

    private static void SetTarget(GameLogEvent r, Entity e)
    {
        r.TargetName     = e.Name;
        r.TargetShip     = e.Ship;
        r.TargetCorp     = e.Corp;
        r.TargetAlliance = e.Alliance;
    }
}
