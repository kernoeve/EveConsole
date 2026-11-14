// Direction, stored clears and follow-up lines: the parser on its own, the thread rules on their own, the database
// side of them on both engines, and a parser pass that must give the same reports live and in two re-parses.
//
// Every system, pilot, reporter and channel here is invented.
using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Monitoring;
using EveConsole.Services;
using EveConsole.Services.Intel;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Tools.IntelCheck;

internal static class FollowUpChecks
{
    private const int S1 = 30_000_001, S2 = 30_000_002, S3 = 30_000_003, S4 = 30_000_004;
    private const long Alpha = 90_000_001, Bravo = 90_000_003, Zeltrix = 90_000_002, Mirrel = 90_000_004;
    private const int LokiId = 990_001, SabreId = 990_002;

    private static IntelVocabulary Vocabulary() => new(
        [(S1, "QZ-X77", 10_000_001), (S2, "XQ1-Z2", 10_000_001), (S3, "ZZQ-71", 10_000_001), (S4, "X2-QZ7", 10_000_001)],
        [],
        [(LokiId, "Loki"), (SabreId, "Sabre")],
        [],
        [("Interdictor", "Interdictor")]);

    private static readonly HashSet<string> Characters = new(StringComparer.Ordinal) { "Zeltrix Quorvane", "Mirrel Okonda" };

    private static IntelLexicon Lexicon() =>
        new(Vocabulary(), IntelChannelHints.None, (run, _) => Characters.Contains(run));

    // ── The parser ───────────────────────────────────────────────────────────────────────────────────────────

    public static void Parser(Checks c)
    {
        var lex = Lexicon();

        void Place(string line, string system, string? from = null, string? heading = null, int? count = null, string? note = null)
        {
            var p = IntelRules.Parse(line, lex);
            c.Check($"parse \"{line}\": in {system}, from {from ?? "-"}, heading {heading ?? "-"}",
                p is { Kind: IntelRules.IntelKind.Sighting } && p.SystemName == system && p.FromSystem == from && p.HeadingSystem == heading
                && (count is null || p.PlayerCount == count) && (note is null || p.Note == note),
                p is null ? "null" : $"in {p.SystemName}, from {p.FromSystem ?? "-"}, heading {p.HeadingSystem ?? "-"}, {p.PlayerCount}, note \"{p.Note}\"");
        }

        // The arrows: in the right-hand system, from the left-hand one, however they are typed.
        Place("QZ-X77 -> XQ1-Z2 Zeltrix Quorvane", "XQ1-Z2", from: "QZ-X77", count: 1, note: "");
        Place("QZ-X77 > XQ1-Z2 +3", "XQ1-Z2", from: "QZ-X77", count: 3);
        Place("QZ-X77 → XQ1-Z2 +2", "XQ1-Z2", from: "QZ-X77", count: 2);
        Place("QZ-X77 => XQ1-Z2 +1", "XQ1-Z2", from: "QZ-X77", count: 1);
        Place("QZ-X77 --> XQ1-Z2 +1", "XQ1-Z2", from: "QZ-X77");
        Place("QZ-X77->XQ1-Z2 Zeltrix Quorvane", "XQ1-Z2", from: "QZ-X77", count: 1);
        Place("QZ-X77  >  XQ1-Z2  Zeltrix Quorvane", "XQ1-Z2", from: "QZ-X77", count: 1);
        Place("QZ-X77 -> XQ1-Z2 -> ZZQ-71 +4", "ZZQ-71", from: "XQ1-Z2", count: 4);
        Place("QZ-X77 Zeltrix Quorvane >", "QZ-X77");                    // an arrow to nothing is not one

        // Into: the system after the word is the place, ahead of any other.
        Place("Zeltrix Quorvane jumped into XQ1-Z2", "XQ1-Z2", note: "");
        Place("Zeltrix Quorvane jumped XQ1-Z2", "XQ1-Z2");
        Place("Zeltrix Quorvane jumped to XQ1-Z2", "XQ1-Z2");
        Place("Zeltrix Quorvane entered XQ1-Z2 +1", "XQ1-Z2", count: 2);
        Place("QZ-X77 Zeltrix Quorvane jumped into XQ1-Z2", "XQ1-Z2");
        Place("XQ1-Z2 Zeltrix Quorvane jumped in from QZ-X77", "XQ1-Z2", from: "QZ-X77");
        Place("XQ1-Z2 Zeltrix Quorvane left QZ-X77", "XQ1-Z2", from: "QZ-X77");
        Place("XQ1-Z2 Zeltrix Quorvane not QZ-X77", "XQ1-Z2");          // did not go there: not where they came from

        // Heading: only intended. They are still where they were reported.
        Place("QZ-X77 Zeltrix Quorvane heading XQ1-Z2", "QZ-X77", heading: "XQ1-Z2", note: "");
        Place("QZ-X77 Zeltrix Quorvane going to XQ1-Z2", "QZ-X77", heading: "XQ1-Z2", note: "");
        Place("QZ-X77 +2 moving towards XQ1-Z2", "QZ-X77", heading: "XQ1-Z2", count: 2);
        Place("QZ-X77 +2 heading into XQ1-Z2", "QZ-X77", heading: "XQ1-Z2");
        Place("QZ-X77 +3 heading to XQ1-Z2 gate", "QZ-X77", heading: "XQ1-Z2", count: 3);

        // A bare "to" is chatter, and so is a heading word with no system after it.
        Place("QZ-X77 Zeltrix Quorvane to XQ1-Z2", "QZ-X77");
        Place("QZ-X77 Zeltrix Quorvane going to bed", "QZ-X77");

        // What the parser did before stays as it was.
        var clr = IntelRules.Parse("QZ-X77 clr", lex);
        c.Check("parse: a system called clear is still a clear", clr is { Kind: IntelRules.IntelKind.Clear, SystemName: "QZ-X77" });

        // Every way a clear is typed is a clear; a clear asked about is a question, not a report.
        foreach (var said in new[] { "QZ-X77 clr", "QZ-X77  clr", "QZ-X77* clr", "QZ-X77 clr.", "QZ-X77 CLR", "QZ-X77 clear", "QZ-X77 cleared",
                                     "QZ-X77 empty", "clr QZ-X77", "QZ-X77 is clr", "QZ-X77 all clear", "QZ-X77 still clr", "QZ-X77 clr o7" })
        {
            var p = IntelRules.Parse(said, lex);
            c.Check($"parse: \"{said}\" is a clear of QZ-X77", p is { Kind: IntelRules.IntelKind.Clear, SystemName: "QZ-X77" },
                p is null ? "null" : $"{p.Kind} {p.SystemName}");
        }
        foreach (var question in new[] { "QZ-X77 clr?", "QZ-X77 clear?", "QZ-X77 status?" })
            c.Check($"parse: \"{question}\" is a question, not a clear", IntelRules.Parse(question, lex) is null);

        // A clear turned round, asked about anywhere in the line, waited for or not the line's whole claim is no clear:
        // a false one retires the live sightings in that system and hides real hostiles.
        foreach (var notClear in new[] { "QZ-X77 not clr", "QZ-X77 not clear", "QZ-X77 no clr", "QZ-X77 isnt clr", "QZ-X77 isn't clear",
                                         "anyone know if QZ-X77 is clr", "QZ-X77 clr?? anyone", "QZ-X77 clr? anyone", "is QZ-X77 clr ?",
                                         "wait for QZ-X77 to clr", "QZ-X77 will be clr soon", "XQ1-Z2 clr QZ-X77 red" })
        {
            var p = IntelRules.Parse(notClear, lex);
            c.Check($"parse: \"{notClear}\" is not a clear", p is null || p.Kind != IntelRules.IntelKind.Clear,
                p is null ? "null" : $"{p.Kind} {p.SystemName}");
        }
        foreach (var notClear in new[] { "not clr", "no clr", "clr?? anyone", "will be clr soon", "isnt clear" })
            c.Check($"follow-up: \"{notClear}\" is not a clear", F(notClear) is not { Clear: true });
        var nv = IntelRules.Parse("QZ-X77 Zeltrix Quorvane nv", lex);
        c.Check("parse: no visual is still a flag on the sighting", nv is { NoVisual: true, PlayerCount: 1, FromSystem: null, HeadingSystem: null });
        c.Check("parse: a question is still not a report", IntelRules.Parse("QZ-X77 Update?", lex) is null);
        var ships = IntelRules.Parse("QZ-X77 3 lokis", lex);
        c.Check("parse: ships with nobody named are still people", ships is { PlayerCount: 3 } && ships.Ships.Single().Count == 3);

        // A line whose only system is where they are going, came from, or did not go has no place.
        c.Check("parse: heading somewhere is no place of its own", IntelRules.Parse("Zeltrix Quorvane heading XQ1-Z2", lex) is null);
        c.Check("parse: leaving somewhere is no place of its own", IntelRules.Parse("Zeltrix Quorvane left QZ-X77", lex) is null);

        // Never asked about as a name: an arrow, or a run across one.
        var asked = IntelRules.NameCandidates("QZ-X77->XQ1-Z2 Zeltrix Quorvane > Bo", s => lex.System(s) is not null);
        c.Check("parse: no name is asked about across an arrow",
            asked.Count > 0 && !asked.Any(a => a.Contains('>') || a.Contains('→')), string.Join(" | ", asked));

        // ── Follow-up lines ──────────────────────────────────────────────────────────────────────────────────
        IntelRules.ParsedFollowUp? F(string line) => IntelRules.ParseFollowUp(line, lex);

        c.Check("follow-up \"+3\" adds three", F("+3") is { Plus: 3, Total: 0, Clear: false });
        c.Check("follow-up \"=12\" sets the total", F("=12") is { Total: 12, Plus: 0 });
        c.Check("follow-up \"12 total\" sets the total", F("12 total") is { Total: 12 });
        c.Check("follow-up \"nv\" is no visual", F("nv") is { NoVisual: true, Clear: false });
        c.Check("follow-up \"clr\" is a clear", F("clr") is { Clear: true });
        c.Check("follow-up \"spike\" is a flag", F("spike") is { Flags: IntelRules.IntelFlags.Spike });
        var pilot = F("Zeltrix Quorvane Loki");
        c.Check("follow-up names a pilot and their hull",
            pilot is { Pilots.Count: 1 } && pilot.Pilots[0] is { Name: "Zeltrix Quorvane", Ship: "Loki", ShipTypeId: LokiId });
        c.Check("follow-up lists ships", F("2 lokis") is { Ships.Count: 1 } s2 && s2.Ships[0].Count == 2);
        var heading = F("heading XQ1-Z2");
        c.Check("follow-up \"heading XQ1-Z2\" gives the heading, and no note", heading is { HeadingSystem: "XQ1-Z2", Note: "" },
            heading is null ? "null" : $"{heading.HeadingSystem} \"{heading.Note}\"");
        c.Check("follow-up \"going to XQ1-Z2\" too", F("going to XQ1-Z2") is { HeadingSystem: "XQ1-Z2" });
        c.Check("follow-up: a bare \"to XQ1-Z2\" names a place, so it is no follow-up", F("to XQ1-Z2") is null);
        c.Check("follow-up: \"+2 clear\", with people counted, is not a clear", F("+2 clear") is { Clear: false, Plus: 2 });
        c.Check("follow-up: chatter adds nothing", F("lol") is null && F("o7") is null);
        c.Check("follow-up: a question is not one", F("anyone?") is null && F("Zeltrix Quorvane?") is null);
        c.Check("follow-up: a question with a count still is", F("+2?") is { Plus: 2 });
        c.Check("follow-up: a line about a system they left is not one", F("Zeltrix Quorvane left QZ-X77") is null);
    }

    // ── The thread rules, in memory ──────────────────────────────────────────────────────────────────────────

    private static string T(int seconds) => IntelLastSeenWriter.Format(new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero).AddSeconds(seconds));

    private static IntelReport Stored(int id, int at, string reporter = "Scout Alpha", string channel = "intelcheck-a",
                                      int system = S1, int count = 1, int kind = IntelReportKind.Sighting, int? parent = null,
                                      string? ships = null, int flags = 0, int? heading = null) => new()
    {
        Id = id, ReportedAt = T(at), ReporterName = reporter, ChannelName = channel, SystemId = system, SystemName = "QZ-X77",
        PlayerCount = count, Kind = kind, ParentReportId = parent, Ships = ships, Flags = flags, HeadingSystemId = heading,
        ChatMessageId = id, Message = "stored",
    };

    private static IntelReportCharacter Pilot(long id, string name, string? ship = null, int? shipId = null) =>
        new() { CharacterId = id, CharacterName = name, ShipName = ship, ShipTypeId = shipId };

    private static IntelReport Line(int chatId, int at, string reporter = "Scout Alpha", string channel = "intelcheck-a") => new()
    {
        ChatMessageId = chatId, ReportedAt = T(at), ReporterName = reporter, ChannelName = channel, Message = "line",
    };

    private static Dictionary<long, List<string>> Kills(long id, params string[] times) => new() { [id] = [.. times] };

    public static void Threads(Checks c)
    {
        var lex = Lexicon();
        var noKills = new Dictionary<long, List<string>>();
        var window = TimeSpan.FromSeconds(IntelThreads.DefaultWindowSeconds);
        var named = TimeSpan.FromMinutes(15);

        var t = new IntelThreads();
        t.AddStored(Stored(10, 0, count: 3, ships: "3× Loki"), [Pilot(Zeltrix, "Zeltrix Quorvane")]);

        var head = t.BySameReporter("intelcheck-a", "Scout Alpha", T(60), window);
        c.Check("threads: the reporter's own report within the window", head?.Report.Id == 10);
        c.Check("threads: not after the window", t.BySameReporter("intelcheck-a", "Scout Alpha", T(181), window) is null);
        c.Check("threads: the window is the setting's", t.BySameReporter("intelcheck-a", "Scout Alpha", T(60), TimeSpan.FromSeconds(30)) is null);
        c.Check("threads: not another reporter's", t.BySameReporter("intelcheck-a", "Scout Bravo", T(60), window) is null);
        c.Check("threads: not another channel's", t.BySameReporter("intelcheck-b", "Scout Alpha", T(60), window) is null);
        c.Check("threads: never a report from after the line", t.BySameReporter("intelcheck-a", "Scout Alpha", T(-5), window) is null);

        c.Check("threads: a named pilot's current report, whoever made it",
            t.ByPilot([Zeltrix], T(600), named, noKills)?.Report.Id == 10);
        c.Check("threads: not once the named lifetime is over", t.ByPilot([Zeltrix], T(901), named, noKills) is null);
        c.Check("threads: not for a pilot it does not hold", t.ByPilot([Mirrel], T(600), named, noKills) is null);
        c.Check("threads: not when the pilot has been on a kill since",
            t.ByPilot([Zeltrix], T(600), named, Kills(Zeltrix, T(300))) is null);
        c.Check("threads: a kill in the same second as the report wins it, as for last seen",
            t.ByPilot([Zeltrix], T(600), named, Kills(Zeltrix, T(0))) is null);
        c.Check("threads: a kill before the report changes nothing",
            t.ByPilot([Zeltrix], T(600), named, Kills(Zeltrix, T(-60)))?.Report.Id == 10);

        // "+2" a minute later: the report restated, three named and unnamed people become five.
        var plus = IntelRules.ParseFollowUp("+2", lex)!;
        var basis = t.Newest(head!, T(60));
        var (r1, p1) = IntelThreads.Restate(basis, plus, [], Line(11, 60), null);
        c.Check("restate: \"+2\" adds to the unnamed count", r1 is { PlayerCount: 5, SystemId: S1, Kind: IntelReportKind.Sighting }
            && p1.Count == 1 && p1[0].CharacterId == Zeltrix, $"{r1.PlayerCount}");
        c.Check("restate: the restatement is the line's own", r1 is { ChatMessageId: 11, ReporterName: "Scout Alpha", Source: IntelReportSource.Chat }
            && r1.ReportedAt == T(60));
        c.Check("restate: the report's pilot rows are copies, not the report's own", !ReferenceEquals(p1[0], basis.Pilots[0]));
        var e1 = t.AddFollowUp(basis, r1, p1, DateTimeOffset.UtcNow);
        c.Check("restate: its parent is the thread's first report", r1.ParentReportId == 10);

        // "=10": the next follow-up restates the restatement, and still points at the first report.
        var basis2 = t.Newest(t.BySameReporter("intelcheck-a", "Scout Alpha", T(120), window)!, T(120));
        c.Check("threads: the latest of the thread is what is restated", ReferenceEquals(basis2, e1));
        var (r2, _) = IntelThreads.Restate(basis2, IntelRules.ParseFollowUp("=10", lex)!, [], Line(12, 120), null);
        t.AddFollowUp(basis2, r2, [], DateTimeOffset.UtcNow);
        c.Check("restate: \"=N\" sets the total", r2.PlayerCount == 10);
        c.Check("restate: a chain is one level deep", r2.ParentReportId == 10);

        // Ships, flags, no visual, the heading and a hull for a pilot.
        var more = IntelRules.ParseFollowUp("Zeltrix Quorvane Sabre  2 lokis  Sabre  spike nv heading XQ1-Z2", lex)!;
        var (r3, p3) = IntelThreads.Restate(basis, more, [Pilot(Zeltrix, "Zeltrix Quorvane", "Sabre", SabreId)], Line(13, 130), S2);
        c.Check("restate: a hull already listed is not counted twice; a new one is added",
            r3.Ships == "3× Loki, Sabre", r3.Ships ?? "null");
        c.Check("restate: flags and no visual are added", (r3.Flags & (int)IntelRules.IntelFlags.Spike) != 0 && r3.NoVisual);
        c.Check("restate: the heading is the follow-up's", r3.HeadingSystemId == S2);
        c.Check("restate: the pilot's hull is the follow-up's", p3.Single() is { ShipTypeId: SabreId, ShipName: "Sabre" });
        c.Check("restate: the report's own pilot row is untouched", basis.Pilots[0].ShipTypeId is null);
        c.Check("restate: count is the larger of people and ships", r3.PlayerCount == 4, $"{r3.PlayerCount}");

        var keep = new IntelThreads();
        keep.AddStored(Stored(30, 0, flags: (int)IntelRules.IntelFlags.Camp, heading: S3), []);
        var kb = keep.BySameReporter("intelcheck-a", "Scout Alpha", T(10), window)!;
        var (r4, _) = IntelThreads.Restate(kb, IntelRules.ParseFollowUp("+1", lex)!, [], Line(31, 10), null);
        c.Check("restate: without a heading of its own the report's is kept", r4.HeadingSystemId == S3);
        c.Check("restate: and its flags", r4.Flags == (int)IntelRules.IntelFlags.Camp);

        // A clear follow-up: a clear row on the thread's system, which supersedes nothing itself.
        var (rc, pc) = IntelThreads.Restate(basis, IntelRules.ParseFollowUp("clr", lex)!, [], Line(14, 140), null);
        c.Check("restate: \"clr\" is a clear row on the thread's system", rc is { Kind: IntelReportKind.Clear, SystemId: S1, PlayerCount: 0 } && pc.Count == 0);

        // A clear after the report ends the thread.
        var cleared = new IntelThreads();
        cleared.AddStored(Stored(20, 0), [Pilot(Zeltrix, "Zeltrix Quorvane")]);
        cleared.AddStored(Stored(21, 30, reporter: "Scout Bravo", channel: "intelcheck-b", count: 0, kind: IntelReportKind.Clear), []);
        c.Check("threads: a clear of the system since ends the thread",
            cleared.BySameReporter("intelcheck-a", "Scout Alpha", T(60), window) is null
            && cleared.ByPilot([Zeltrix], T(60), named, noKills) is null);
        c.Check("threads: a clear after the line does not",
            cleared.BySameReporter("intelcheck-a", "Scout Alpha", T(20), window)?.Report.Id == 20);
        c.Check("threads: a clear is never a parent", cleared.BySameReporter("intelcheck-b", "Scout Bravo", T(60), window) is null);

        // A pilot of the report called in another system since: "+2" from the first reporter does not copy them
        // back. Their report elsewhere is the newer one, and must stay so.
        var moved = new IntelThreads();
        moved.AddStored(Stored(50, 0, count: 2), [Pilot(Zeltrix, "Zeltrix Quorvane"), Pilot(Mirrel, "Mirrel Okonda")]);
        moved.AddStored(Stored(51, 30, reporter: "Scout Bravo", channel: "intelcheck-b", system: S2), [Pilot(Mirrel, "Mirrel Okonda")]);
        var mb = moved.Newest(moved.BySameReporter("intelcheck-a", "Scout Alpha", T(60), window)!, T(60));
        var mGone = moved.Gone(mb, T(60), noKills);
        c.Check("gone: a pilot called in another system since", mGone.SetEquals([Mirrel]), string.Join(",", mGone));
        var (rm, pm) = IntelThreads.Restate(mb, plus, [], Line(52, 60), null, mGone);
        c.Check("restate: a pilot called elsewhere since is not copied back",
            pm.Select(p => p.CharacterId).SequenceEqual([Zeltrix]), string.Join(",", pm.Select(p => p.CharacterId)));
        c.Check("restate: and the reporter's count stands", rm.PlayerCount == 4, $"{rm.PlayerCount}");
        var (_, pn) = IntelThreads.Restate(mb, IntelRules.ParseFollowUp("Mirrel Okonda", lex)!, [Pilot(Mirrel, "Mirrel Okonda")], Line(53, 60), null, mGone);
        c.Check("restate: a pilot the line names is in, wherever they were called",
            pn.Select(p => p.CharacterId).Order().SequenceEqual([Zeltrix, Mirrel]));
        c.Check("gone: not before they were called elsewhere", moved.Gone(mb, T(20), noKills).Count == 0);

        // Called elsewhere and then back here: their latest is here, so they stay.
        moved.AddStored(Stored(54, 45, reporter: "Scout Bravo", channel: "intelcheck-b"), [Pilot(Mirrel, "Mirrel Okonda")]);
        c.Check("gone: not when their latest sighting is back in the report's system", moved.Gone(mb, T(60), noKills).Count == 0);

        // A pilot of the report on a kill since: ended, not copied. A kill before the report changes nothing.
        c.Check("gone: a copied pilot seen on a kill since", moved.Gone(mb, T(60), Kills(Zeltrix, T(40))).SetEquals([Zeltrix]));
        c.Check("gone: a kill in the same second as the report too", moved.Gone(mb, T(60), Kills(Zeltrix, T(0))).SetEquals([Zeltrix]));
        c.Check("gone: not for a kill before the report", moved.Gone(mb, T(60), Kills(Zeltrix, T(-10))).Count == 0);
        c.Check("gone: nor one after the line", moved.Gone(mb, T(60), Kills(Zeltrix, T(70))).Count == 0);

        // A follow-up from another channel that adds only a heading or a hull is not a relayed copy of the report
        // it adds to; a relayed copy of the follow-up itself still is.
        var cross = new IntelThreads();
        var first = Stored(60, 0, reporter: "Scout Bravo", channel: "intelcheck-b");
        cross.AddStored(first, [Pilot(Zeltrix, "Zeltrix Quorvane")]);
        var held = new IntelDuplicates();
        held.Add(first, [Pilot(Zeltrix, "Zeltrix Quorvane")]);
        var cb = cross.ByPilot([Zeltrix], T(40), named, noKills)!;
        var (rh, ph) = IntelThreads.Restate(cb, IntelRules.ParseFollowUp("Zeltrix Quorvane heading XQ1-Z2", lex)!,
                                            [Pilot(Zeltrix, "Zeltrix Quorvane")], Line(61, 40), S2);
        c.Check("copy: a follow-up adding a heading, from another channel, is not a copy", !held.IsCopy(rh, ph));
        var (rl, pl) = IntelThreads.Restate(cb, IntelRules.ParseFollowUp("Zeltrix Quorvane Loki", lex)!,
                                            [Pilot(Zeltrix, "Zeltrix Quorvane", "Loki", LokiId)], Line(62, 40), null);
        c.Check("copy: nor one adding a hull", !held.IsCopy(rl, pl));
        held.Add(rl, pl);
        // Other words, so only the facts can match it.
        var relayed = Line(63, 45, channel: "intelcheck-c");
        relayed.Message = "relay: Zeltrix Quorvane Loki";
        var (rr, pr) = IntelThreads.Restate(cross.AddFollowUp(cb, rl, pl, DateTimeOffset.UtcNow), IntelRules.ParseFollowUp("Zeltrix Quorvane Loki", lex)!,
                                            [Pilot(Zeltrix, "Zeltrix Quorvane", "Loki", LokiId)], relayed, null);
        c.Check("copy: a relayed copy of the follow-up still is", held.IsCopy(rr, pr));
        var (rs, ps) = IntelThreads.Restate(cb, IntelRules.ParseFollowUp("Zeltrix Quorvane", lex)!,
                                            [Pilot(Zeltrix, "Zeltrix Quorvane")], Line(64, 40), null);
        c.Check("copy: (setup) a follow-up adding nothing at all is taken for one", held.IsCopy(rs, ps));

        // A thread whose first report is new in this batch: the follow-up learns its id once it is saved.
        var fresh = new IntelThreads();
        var newHead = new IntelReport { ReportedAt = T(0), ReporterName = "Scout Alpha", ChannelName = "intelcheck-a", SystemId = S1, PlayerCount = 1 };
        fresh.AddNew(newHead, [Pilot(Zeltrix, "Zeltrix Quorvane")]);
        var fp = fresh.BySameReporter("intelcheck-a", "Scout Alpha", T(30), window)!;
        var (child, _) = IntelThreads.Restate(fresh.Newest(fp, T(30)), plus, [], Line(41, 30), null);
        fresh.AddFollowUp(fp, child, [], DateTimeOffset.UtcNow);
        c.Check("threads: a new first report is superseded by its restatement before it is saved", newHead.Obsolete);
        c.Check("threads: (setup) the follow-up has no parent id before the save", child.ParentReportId is null);
        newHead.Id = 77;   // as the save gives it
        fresh.LinkSaved();
        c.Check("threads: and points at its first report once that is saved", child.ParentReportId == 77);

        // Ships as stored, read back.
        c.Check("ships: written and read back", IntelThreads.FormatShips(IntelThreads.ReadShips("3× Loki, Interdictor, 2× Sabre")) == "3× Loki, Interdictor, 2× Sabre");
        c.Check("ships: none is null", IntelThreads.FormatShips([]) is null && IntelThreads.ReadShips(null).Count == 0);
        c.Check("ships: the same hull twice is two of it", IntelThreads.FormatShips([("Loki", 1), ("Loki", 1), ("Sabre", 1)]) == "2× Loki, Sabre");
    }

    // ── The database side, on either engine ──────────────────────────────────────────────────────────────────

    public static async Task DatabaseAsync(Checks c, string engine, DbContextOptions<AppDbContext> options)
    {
        await using (var db = new AppDbContext(options))
        {
            await db.IntelReportCharacters.ExecuteDeleteAsync();
            await db.IntelReports.ExecuteDeleteAsync();
            await db.AppPreferences.Where(p => p.Key == IntelThreads.KeyWindow).ExecuteDeleteAsync();
        }

        // The window, as the shared setting holds it.
        async Task<double> WindowWith(string? value)
        {
            await using var db = new AppDbContext(options);
            await db.AppPreferences.Where(p => p.Key == IntelThreads.KeyWindow).ExecuteDeleteAsync();
            if (value is not null)
            {
                db.AppPreferences.Add(new AppPreference { Key = IntelThreads.KeyWindow, Value = value });
                await db.SaveChangesAsync();
            }
            return (await IntelThreads.WindowAsync(db)).TotalSeconds;
        }
        c.Check($"{engine}: the follow-up window is 180 s by default", await WindowWith(null) == 180);
        c.Check($"{engine}: and the shared setting's when set", await WindowWith("45") == 45);
        c.Check($"{engine}: no less than 30 s", await WindowWith("5") == 30);
        c.Check($"{engine}: no more than 600 s", await WindowWith("9999") == 600);
        c.Check($"{engine}: the default for junk", await WindowWith("soon") == 180);
        await WindowWith(null);

        // A first report and a follow-up stored by earlier batches, a report from long before, and a kill.
        int headId, followId;
        var t0 = new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);
        await using (var db = new AppDbContext(options))
        {
            var h = Stored(0, 0, count: 1); h.Id = 0; h.ChatMessageId = 0;
            db.IntelReports.Add(h);
            await db.SaveChangesAsync();
            headId = h.Id;
            db.IntelReportCharacters.Add(new IntelReportCharacter { IntelReportId = headId, CharacterId = Zeltrix, CharacterName = "Zeltrix Quorvane" });
            var f = Stored(0, 60, count: 3, parent: headId); f.Id = 0; f.ChatMessageId = 0;
            var old = Stored(0, -7200, count: 9); old.Id = 0; old.ChatMessageId = 0;
            db.IntelReports.AddRange(f, old);
            await db.SaveChangesAsync();
            followId = f.Id;
            db.IntelReportCharacters.Add(new IntelReportCharacter { IntelReportId = followId, CharacterId = Zeltrix, CharacterName = "Zeltrix Quorvane" });

            await db.KillMailDetails.Where(k => k.KillMailId == 8_801).ExecuteDeleteAsync();
            await db.KillMailAttackers.Where(k => k.KillMailId == 8_801).ExecuteDeleteAsync();
            db.KillMailDetails.Add(new KillMailDetail
            {
                KillMailId = 8_801, KillMailHash = "followup", KillMailTime = t0.AddSeconds(300), SolarSystemId = S2,
                VictimCharId = 93_000_009, VictimCorpId = 98_000_019, VictimShipTypeId = LokiId,
            });
            db.KillMailAttackers.Add(new KillMailAttacker { KillMailId = 8_801, CharacterId = Zeltrix, CorporationId = 98_000_002 });
            await db.SaveChangesAsync();
        }

        var lex = Lexicon();
        await using (var db = new AppDbContext(options))
        {
            var threads = await IntelThreads.LoadAsync(db, T(-900), T(200));
            var parent = threads.BySameReporter("intelcheck-a", "Scout Alpha", T(120), TimeSpan.FromSeconds(180));
            c.Check($"{engine}: the stored follow-up is the reporter's latest", parent?.Report.Id == followId, $"{parent?.Report.Id}");
            c.Check($"{engine}: with its pilot", parent?.Pilots.SingleOrDefault()?.CharacterId == Zeltrix);
            c.Check($"{engine}: a report from before the window is not loaded",
                threads.BySameReporter("intelcheck-a", "Scout Alpha", T(-7100), TimeSpan.FromSeconds(180)) is null);

            var (report, pilots) = IntelThreads.Restate(threads.Newest(parent!, T(120)), IntelRules.ParseFollowUp("+1", lex)!, [], Line(0, 120), null);
            threads.AddFollowUp(parent!, report, pilots, DateTimeOffset.UtcNow);
            c.Check($"{engine}: a follow-up of a stored follow-up points at the stored first report", report.ParentReportId == headId);

            db.IntelReports.Add(report);
            await db.SaveChangesAsync();
            threads.LinkSaved();
            await threads.SupersedeRestatedAsync(db, DateTimeOffset.UtcNow);
        }
        await using (var db = new AppDbContext(options))
        {
            var rows = await db.IntelReports.AsNoTracking().ToDictionaryAsync(r => r.Id);
            c.Check($"{engine}: the stored report it restated is superseded", rows[followId].Obsolete);
            c.Check($"{engine}: the first report is not touched by it", !rows[headId].Obsolete);
            c.Check($"{engine}: the restatement is saved with its parent", rows.Values.Any(r => r.ParentReportId == headId && r.PlayerCount == 4));

            var kills = await IntelThreads.KillTimesAsync(db, [Zeltrix, Mirrel], t0, t0.AddHours(1));
            c.Check($"{engine}: the kill times of the pilots asked about",
                kills.TryGetValue(Zeltrix, out var times) && times.SequenceEqual([T(300)]) && !kills.ContainsKey(Mirrel),
                string.Join(",", kills.Select(k => $"{k.Key}:{string.Join("/", k.Value)}")));
            var none = await IntelThreads.KillTimesAsync(db, [Zeltrix], t0.AddSeconds(400), t0.AddHours(1));
            c.Check($"{engine}: and only inside the times asked about", none.Count == 0);

            var threads = await IntelThreads.LoadAsync(db, T(-900), T(900));
            c.Check($"{engine}: a pilot on a kill since their report is not attached to it",
                threads.ByPilot([Zeltrix], T(400), TimeSpan.FromMinutes(15), kills) is null);
            c.Check($"{engine}: (setup) before the kill they would be",
                threads.ByPilot([Zeltrix], T(250), TimeSpan.FromMinutes(15), kills) is not null);
        }
    }

    // ── A parser pass, live and re-parsed ────────────────────────────────────────────────────────────────────

    private const string ChA = "intelcheck-a", ChB = "intelcheck-b";

    /// <summary>
    /// Lines arriving a few at a time, each group parsed as it lands, as on a running worker; then the whole
    /// history re-parsed twice. Every report must come out the same all three times: a follow-up's parent comes
    /// only from before it, so when it is parsed cannot matter.
    /// </summary>
    public static async Task PassAsync(Checks c, string profile)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={Path.Combine(profile, "followups.db")}").Options;
        await using (var db = new AppDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            foreach (var (id, name) in new[] { (S1, "QZ-X77"), (S2, "XQ1-Z2"), (S3, "ZZQ-71"), (S4, "X2-QZ7") })
                db.SdeSolarSystems.Add(new SdeSolarSystem { SolarSystemId = id, ConstellationId = 20_000_001, RegionId = 10_000_001, Name = name, Security = -0.4 });
            // One hull, so a line can give a pilot's.
            db.SdeGroups.Add(new SdeGroup { GroupId = 9_901, CategoryId = 6, Name = "Strategic Cruiser", Published = true });
            db.SdeTypes.Add(new SdeType { TypeId = LokiId, GroupId = 9_901, Name = "Loki", Published = true });
            db.UniverseNames.AddRange(
                new UniverseName { EntityId = Alpha,   Name = "Scout Alpha",      Category = "character" },
                new UniverseName { EntityId = Bravo,   Name = "Scout Bravo",      Category = "character" },
                new UniverseName { EntityId = Zeltrix, Name = "Zeltrix Quorvane", Category = "character" },
                new UniverseName { EntityId = Mirrel,  Name = "Mirrel Okonda",    Category = "character" });
            // On a kill here, so the parser takes the names for characters without asking anyone.
            db.KillMailAttackers.Add(new KillMailAttacker { KillMailId = 1, CharacterId = Zeltrix });
            foreach (var id in new[] { Alpha, Bravo, Zeltrix, Mirrel })
                db.CharacterAffiliations.Add(new CharacterAffiliation { CharacterId = id, CorporationId = 98_000_001, PulledAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        var prefs = Prefs.Over(options);
        await prefs.SetAsync(MonitoringSettings.KeyChatIntelChannels, ChA + "\n" + ChB);
        await prefs.LoadAsync();
        var logger  = new AppErrorLogger(null!);
        var signals = new ClientSignals(logger);
        signals.Start();
        var lease = new WorkerLease(logger);
        await lease.AcquireAsync();
        var worker = new IntelService(new Factory(options), null!, new MonitoringSettings(prefs), logger, signals, lease);

        var t0 = DateTimeOffset.UtcNow.AddMinutes(-50);
        var ids = new Dictionary<string, int>();

        async Task Lines(params (string Key, string Channel, string Sender, int At, string Text)[] lines)
        {
            await using var db = new AppDbContext(options);
            var rows = lines.Select(l => (l.Key, Row: new ChatMessage
            {
                OccurredAt = ChatLogRules.FormatTimestamp(t0.AddSeconds(l.At)), ChannelName = l.Channel, SenderName = l.Sender,
                Message = l.Text, SourceFile = l.Channel + ".txt", LineNumber = l.At,
            })).ToList();
            foreach (var (_, row) in rows) { db.ChatMessages.Add(row); await ChatMessageWrites.SaveAsync(db); }
            foreach (var (key, row) in rows) ids[key] = row.Id;
            await worker.ProcessNewAsync(waitForTurn: true);
        }

        // A report and three follow-ups from its reporter, in one batch: the first report is new when they come.
        await Lines(("r1", ChA, "Scout Alpha", 0,   "QZ-X77  Zeltrix Quorvane"),
                    ("r2", ChA, "Scout Alpha", 60,  "+2"),
                    ("r3", ChA, "Scout Alpha", 120, "nv"),
                    ("r4", ChA, "Scout Alpha", 150, "heading XQ1-Z2"));
        // Another reporter names the pilot: their current report, stored by now.
        await Lines(("r5", ChB, "Scout Bravo", 400, "Zeltrix Quorvane"));
        // Too late for the reporter's window, with no pilot named; then a clear of a thread, and a line after it.
        await Lines(("u6", ChA, "Scout Alpha", 700, "+5"),
                    ("r7", ChA, "Scout Alpha", 800, "XQ1-Z2  Mirrel Okonda"),
                    ("r8", ChA, "Scout Alpha", 830, "clr"),
                    ("u9", ChA, "Scout Alpha", 860, "+1"));
        // Direction, and a clear with a system of its own.
        await Lines(("r10", ChA, "Scout Alpha", 1000, "ZZQ-71 -> QZ-X77  Mirrel Okonda"),
                    ("r11", ChA, "Scout Alpha", 1030, "ZZQ-71 clr"));
        // The pilot is on a kill; after it, a line naming them is not attached to the report before it.
        await using (var db = new AppDbContext(options))
        {
            db.KillMailDetails.Add(new KillMailDetail
            {
                KillMailId = 8_802, KillMailHash = "pass", KillMailTime = t0.AddSeconds(1100), SolarSystemId = S2,
                VictimCharId = 93_000_010, VictimCorpId = 98_000_020, VictimShipTypeId = LokiId,
            });
            db.KillMailAttackers.Add(new KillMailAttacker { KillMailId = 8_802, CharacterId = Mirrel });
            await db.SaveChangesAsync();
        }
        await Lines(("u13", ChB, "Scout Bravo", 1200, "Mirrel Okonda"),
                    ("r14", ChB, "Scout Bravo", 1210, "X2-QZ7  Zeltrix Quorvane heading QZ-X77"),
                    ("r15", ChA, "Scout Alpha", 1300, "Zeltrix Quorvane jumped into ZZQ-71"));
        // A follow-up from another channel within the copy window that adds a heading, then one that adds a hull,
        // then a relayed copy of that one.
        await Lines(("r16", ChB, "Scout Bravo", 1500, "X2-QZ7  Zeltrix Quorvane"),
                    ("r17", ChA, "Scout Alpha", 1540, "Zeltrix Quorvane heading QZ-X77"),
                    ("r18", ChA, "Scout Alpha", 1560, "Zeltrix Quorvane Loki"));
        await Lines(("u19", ChB, "Scout Alpha", 1570, "Zeltrix Quorvane Loki"));
        // A pilot of a report called in another system, then "+2" on that report.
        await Lines(("r20", ChA, "Scout Alpha", 1700, "QZ-X77  Zeltrix Quorvane  Mirrel Okonda"),
                    ("r21", ChB, "Scout Bravo", 1730, "XQ1-Z2  Mirrel Okonda"),
                    ("r22", ChA, "Scout Alpha", 1760, "+2"));
        // Before anything later names them: their report elsewhere stands, and that is where they were last seen.
        bool movedStands;
        int? movedLastSeen;
        await using (var db = new AppDbContext(options))
        {
            movedStands = await db.IntelReports.AsNoTracking().AnyAsync(r => r.ChatMessageId == ids["r21"] && !r.Obsolete);
            movedLastSeen = await db.IntelLastSeens.AsNoTracking().Where(s => s.CharacterId == Mirrel).Select(s => (int?)s.SystemId).FirstOrDefaultAsync();
        }
        // A pilot of a report on a kill, named nowhere in the batch of the follow-up that would copy them.
        await Lines(("r23", ChA, "Scout Alpha", 1900, "ZZQ-71  Mirrel Okonda  Zeltrix Quorvane"));
        await using (var db = new AppDbContext(options))
        {
            db.KillMailDetails.Add(new KillMailDetail
            {
                KillMailId = 8_803, KillMailHash = "copied", KillMailTime = t0.AddSeconds(1920), SolarSystemId = S2,
                VictimCharId = 93_000_011, VictimCorpId = 98_000_021, VictimShipTypeId = LokiId,
            });
            db.KillMailAttackers.Add(new KillMailAttacker { KillMailId = 8_803, CharacterId = Zeltrix });
            await db.SaveChangesAsync();
        }
        await Lines(("r24", ChA, "Scout Alpha", 1950, "Mirrel Okonda"));
        // One system called clear in both channels within the copy window, word for word; another in other words. Every
        // clear of a system has the same facts (nobody named, nothing counted): these were dropped as copies, and the
        // lines, with no report, showed in the Feed as chatter.
        await Lines(("r25", ChA, "Scout Alpha", 2100, "X2-QZ7 clr"),
                    ("r26", ChB, "Scout Bravo", 2130, "X2-QZ7 clr"),
                    ("r27", ChA, "Scout Alpha", 2200, "XQ1-Z2 clear"),
                    ("r28", ChB, "Scout Bravo", 2290, "XQ1-Z2 clr"));
        // A clear, a pilot called after it, and the clear relayed into the other channel a minute later: the relay is
        // stored but retires nothing, so the pilot called in between stands.
        await Lines(("r29", ChA, "Scout Alpha", 2500, "ZZQ-71 clr"),
                    ("r30", ChA, "Scout Alpha", 2530, "ZZQ-71  Zeltrix Quorvane"));
        await Lines(("r31", ChB, "Scout Bravo", 2560, "ZZQ-71 clr"));
        List<int> chatter;
        await using (var db = new AppDbContext(options))
            chatter = await IntelSnapshotService.ChatterQuery(db, [ChA, ChB], int.MaxValue, "0").Select(m => m.Id).ToListAsync();

        var live = await ReportsAsync(options);
        IntelReport? R(string key) => live.Reports.GetValueOrDefault(ids[key]);
        int? Parent(string key) => R(key)?.ParentReportId is { } p ? live.ById[p].ChatMessageId : null;

        c.Check("pass: a report with its own system starts a thread", R("r1") is { Kind: IntelReportKind.Sighting, ParentReportId: null, PlayerCount: 1, SystemId: S1 });
        c.Check("pass: \"+2\" from the reporter restates it with two more",
            R("r2") is { SystemId: S1, PlayerCount: 3, Kind: IntelReportKind.Sighting } && Parent("r2") == ids["r1"],
            $"{R("r2")?.PlayerCount} parent {Parent("r2")}");
        c.Check("pass: \"nv\" adds no visual, and points at the first report, not the follow-up",
            R("r3") is { NoVisual: true, PlayerCount: 3 } && Parent("r3") == ids["r1"]);
        c.Check("pass: \"heading XQ1-Z2\" gives the heading; they are still in QZ-X77",
            R("r4") is { SystemId: S1, HeadingSystemId: S2, NoVisual: true } && Parent("r4") == ids["r1"]);
        c.Check("pass: what each follow-up restated is superseded", R("r1")!.Obsolete && R("r2")!.Obsolete && R("r3")!.Obsolete);
        c.Check("pass: a line naming the pilot from another reporter restates their current report",
            R("r5") is { SystemId: S1, ReporterName: "Scout Bravo", ChannelName: ChB, PlayerCount: 3, HeadingSystemId: S2 }
            && Parent("r5") == ids["r1"] && live.Pilots[ids["r5"]].SequenceEqual([Zeltrix]),
            $"{R("r5")?.SystemId} {R("r5")?.PlayerCount} parent {Parent("r5")}");
        c.Check("pass: and supersedes the report from an earlier pass", R("r4")!.Obsolete);
        c.Check("pass: a line outside the window naming nobody is not stored", R("u6") is null);
        c.Check("pass: \"clr\" from the reporter is a clear row on the thread's system",
            R("r8") is { Kind: IntelReportKind.Clear, SystemId: S2, PlayerCount: 0 } && Parent("r8") == ids["r7"]);
        c.Check("pass: which retires the report it followed", R("r7")!.Obsolete);
        c.Check("pass: a line after the clear is not attached to the cleared report", R("u9") is null);
        c.Check("pass: \"A -> B\" is in B, from A", R("r10") is { SystemId: S1, FromSystemId: S3, HeadingSystemId: null, ParentReportId: null });
        c.Check("pass: a clear with its own system is stored as a clear row", R("r11") is { Kind: IntelReportKind.Clear, SystemId: S3, ParentReportId: null, PlayerCount: 0 });
        c.Check("pass: a pilot on a kill since their report is not attached to it", R("u13") is null);
        c.Check("pass: a heading on a report with its own system", R("r14") is { SystemId: S4, HeadingSystemId: S1 });
        c.Check("pass: \"jumped into\" places them there", R("r15") is { SystemId: S3, FromSystemId: null });
        c.Check("pass: clears name nobody", live.Pilots[ids["r8"]].Count == 0 && live.Pilots[ids["r11"]].Count == 0);
        c.Check("pass: a follow-up adding a heading, from another channel within the copy window, is kept",
            R("r17") is { SystemId: S4, HeadingSystemId: S1, ChannelName: ChA } && Parent("r17") == ids["r16"]);
        c.Check("pass: and one adding a hull", R("r18") is { SystemId: S4, HeadingSystemId: S1 } && Parent("r18") == ids["r16"]);
        c.Check("pass: a relayed copy of that follow-up is not: it is a copy of it, naming nobody",
            R("u19") is { Kind: IntelReportKind.Copy, PlayerCount: 0 } && Parent("u19") == ids["r18"] && live.Pilots[ids["u19"]].Count == 0,
            R("u19") is { } u19 ? $"kind {u19.Kind} parent {Parent("u19")}" : "no report");
        c.Check("pass: and not chatter", !chatter.Contains(ids["u19"]));
        c.Check("pass: \"+2\" does not copy back a pilot called in another system since",
            R("r22") is { SystemId: S1, PlayerCount: 4 } && live.Pilots[ids["r22"]].SequenceEqual([Zeltrix]),
            $"{R("r22")?.PlayerCount} pilots {string.Join(",", live.Pilots.GetValueOrDefault(ids["r22"]) ?? [])}");
        c.Check("pass: whose report there stands", movedStands);
        c.Check("pass: and is where they were last seen", movedLastSeen == S2, $"{movedLastSeen}");
        c.Check("pass: and \"+2\" restates the rest", Parent("r22") == ids["r20"]);
        c.Check("pass: a copied pilot on a kill since is not copied, though the batch never named them",
            R("r24") is { SystemId: S3 } && live.Pilots[ids["r24"]].SequenceEqual([Mirrel]),
            $"pilots {string.Join(",", live.Pilots.GetValueOrDefault(ids["r24"]) ?? [])}");
        foreach (var (key, system) in new[] { ("r25", S4), ("r26", S4), ("r27", S2), ("r28", S2) })
            c.Check($"pass: a clear called in both channels within the copy window is a clear row in each ({key})",
                R(key) is { Kind: IntelReportKind.Clear, PlayerCount: 0 } r && r.SystemId == system,
                R(key) is { } x ? $"kind {x.Kind} in {x.SystemId}" : "no report");
        c.Check("pass: and none of them is chatter", new[] { "r25", "r26", "r27", "r28" }.All(k => !chatter.Contains(ids[k])),
            string.Join(",", chatter));
        c.Check("pass: a relayed clear is stored, and the pilot called between the clear and its relay is not retired by it",
            R("r31") is { Kind: IntelReportKind.Clear, SystemId: S3 } && R("r30") is { Kind: IntelReportKind.Sighting, Obsolete: false },
            $"relay {R("r31")?.Kind}, between obsolete {R("r30")?.Obsolete}");

        // The same history re-parsed, twice: the same reports each time, and the same as live.
        var first = await worker.BackfillAsync();
        var again = await ReportsAsync(options);
        await worker.BackfillAsync();
        var twice = await ReportsAsync(options);
        var diff1 = Differences(live, again);
        var diff2 = Differences(again, twice);
        c.Check("pass: a re-parse gives the same reports as the live passes", first == live.Reports.Count && diff1.Count == 0,
            $"{first} written of {live.Reports.Count}; " + string.Join(" | ", diff1.Take(4)));
        c.Check("pass: and a second re-parse the same again", diff2.Count == 0, string.Join(" | ", diff2.Take(4)));

        signals.Stop();
    }

    private sealed record Snapshot(
        Dictionary<int, IntelReport> Reports,   // by chat message id
        Dictionary<int, IntelReport> ById,
        Dictionary<int, List<long>>  Pilots);   // by chat message id

    private static async Task<Snapshot> ReportsAsync(DbContextOptions<AppDbContext> options)
    {
        await using var db = new AppDbContext(options);
        var reports = await db.IntelReports.AsNoTracking().ToListAsync();
        var pilots = await db.IntelReportCharacters.AsNoTracking().ToListAsync();
        var byId = reports.ToDictionary(r => r.Id);
        return new Snapshot(
            reports.ToDictionary(r => r.ChatMessageId),
            byId,
            reports.ToDictionary(r => r.ChatMessageId,
                                 r => pilots.Where(p => p.IntelReportId == r.Id).Select(p => p.CharacterId).Order().ToList()));
    }

    /// <summary>
    /// What differs between two sets of reports, matched by their chat message. Ids are new on each parse, so a
    /// parent is compared by its own chat message. Superseded is not compared: the live passes also supersede by
    /// kills, once a minute, and a re-parse leaves that to the next live pass.
    /// </summary>
    private static List<string> Differences(Snapshot a, Snapshot b)
    {
        string Shape(Snapshot s, IntelReport r) =>
            $"kind {r.Kind} in {r.SystemId} from {r.FromSystemId} heading {r.HeadingSystemId} count {r.PlayerCount} flags {r.Flags} " +
            $"nv {r.NoVisual} ships {r.Ships} note {r.Note} by {r.ReporterName} in {r.ChannelName} " +
            $"parent {(r.ParentReportId is { } p && s.ById.TryGetValue(p, out var pr) ? pr.ChatMessageId : 0)} " +
            $"pilots {string.Join(",", s.Pilots[r.ChatMessageId])}";

        var diffs = new List<string>();
        foreach (var key in a.Reports.Keys.Union(b.Reports.Keys).Order())
        {
            var x = a.Reports.TryGetValue(key, out var ra) ? Shape(a, ra) : "none";
            var y = b.Reports.TryGetValue(key, out var rb) ? Shape(b, rb) : "none";
            if (x != y) diffs.Add($"line {key}: {x} / {y}");
        }
        return diffs;
    }
}
