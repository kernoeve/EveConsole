// Pasted Local and d-scan: what a paste is taken for, the names a Local holds, a d-scan's ships and deployables by
// their SDE groups, how a Local's pilots are grouped, "replaces N", and the paste stored on a database: a Local
// clears the system and places everyone, blues and our own raw, unresolved names counted as unnamed; a d-scan adds
// an unnamed count and never clears; neither has a chat message, and two in a row both store. Every name and id
// here is invented.
using System.Globalization;
using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Monitoring;
using EveConsole.Services.Intel;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Tools.IntelCheck
{
    internal static class PasteChecks
    {
        private const int Here = 31_200_001, There = 31_200_002;
        private const long Me = 93_200_001, MyCorp = 98_200_001;
        private const long Foe = 92_200_001, Mate = 92_200_002, Earlier = 92_200_003, Stays = 92_200_004, FoeCorp = 98_200_009;

        // Invented SDE groups and types: a hull, a capsule, a bubble, a cyno, a wreck.
        private const int ShipCat = 6, DeployCat = 22, CelestialCat = 2;
        private const int HullGroup = 9_001, PodGroup = 9_002, BubbleGroup = 9_003, CynoGroup = 9_004, WreckGroup = 9_005;
        private const int HullA = 70_301, HullB = 70_302, Pod = 70_303, Bubble = 70_304, Cyno = 70_305, Wreck = 70_306;

        private static string Iso(DateTimeOffset at) => IntelLastSeenWriter.Format(at);

        private static IntelSdeGroups Groups() => new(
            [(HullGroup, ShipCat, "Frigate"), (PodGroup, ShipCat, IntelSdeGroups.CapsuleName),
             (BubbleGroup, DeployCat, IntelSdeGroups.MobileDisruptorName), (CynoGroup, CelestialCat, IntelSdeGroups.CynoFieldName),
             (WreckGroup, CelestialCat, "Wreck")],
            [(ShipCat, IntelSdeGroups.ShipCategoryName), (DeployCat, "Deployable"), (CelestialCat, "Celestial")]);

        private static readonly Dictionary<int, DscanType> Types = new()
        {
            [HullA] = new(HullA, "Hull Alpha", HullGroup), [HullB] = new(HullB, "Hull Bravo", HullGroup),
            [Pod] = new(Pod, "Capsule", PodGroup), [Bubble] = new(Bubble, "Bubble Maker", BubbleGroup),
            [Cyno] = new(Cyno, "Cyno Beacon", CynoGroup), [Wreck] = new(Wreck, "Hull Alpha Wreck", WreckGroup),
        };

        // ── What a paste is ──────────────────────────────────────────────────────────────────────────────────

        public static void Classification(Checks c)
        {
            c.Check("paste: nothing is empty", IntelPasteService.Classify("  \r\n \n") == IntelPasteKind.Empty);
            c.Check("paste: names one per line are a Local",
                IntelPasteService.Classify("Daxx Orlen\r\nShen Arak\r\nO'Brien-Vale\r\nxX Unknown Xx\r\n") == IntelPasteKind.Local);
            c.Check("paste: a d-scan with type ids is a d-scan",
                IntelPasteService.Classify($"{HullA}\tSomeone's Hull Alpha\tHull Alpha\t1,204 km\n{Pod}\tCapsule\tCapsule\t-") == IntelPasteKind.Dscan);
            c.Check("paste: a d-scan of three columns is a d-scan",
                IntelPasteService.Classify("Someone's Hull Alpha\tHull Alpha\t2.1 AU\nCapsule\tCapsule\t-") == IntelPasteKind.Dscan);
            c.Check("paste: a line with one tab among names is neither",
                IntelPasteService.Classify("Daxx Orlen\nShen\tArak") == IntelPasteKind.Unknown);
            c.Check("paste: chat is neither", IntelPasteService.Classify("QZ-X77 +3 on the gate, anyone around?") == IntelPasteKind.Unknown);
            c.Check("paste: a name of 2 characters is no name, of 3 is", !IntelPasteService.IsNameLike("Ab") && IntelPasteService.IsNameLike("Abc"));
            c.Check("paste: a name of 37 characters is one, of 38 is not",
                IntelPasteService.IsNameLike(new string('a', 37)) && !IntelPasteService.IsNameLike(new string('a', 38)));
            c.Check("paste: a name may not start with a hyphen", !IntelPasteService.IsNameLike("-Abc"));

            var names = IntelPasteService.LocalNames("Daxx Orlen\r\n\r\n  Shen Arak  \ndaxx orlen\nMira Halcyon\n");
            c.Check("paste: a Local's names, trimmed, blank lines skipped, each once",
                names.SequenceEqual(["Daxx Orlen", "Shen Arak", "Mira Halcyon"]), string.Join("|", names));

            var lines = IntelPasteService.DscanLines($"{HullA}\tA's ship\tHull Alpha\t10 km\nB's ship\tHull Bravo\t2 AU\nbroken line");
            c.Check("paste: d-scan lines, with the type id when given",
                lines.Count == 2 && lines[0] is { TypeId: HullA, TypeName: "Hull Alpha", Distance: "10 km" }
                && lines[1] is { TypeId: null, Name: "B's ship", TypeName: "Hull Bravo" },
                string.Join("|", lines));
        }

        // ── A d-scan's ships and deployables ─────────────────────────────────────────────────────────────────

        public static void Dscan(Checks c)
        {
            var text = string.Join("\n",
                $"{HullA}\tOne\tHull Alpha\t10 km", $"{HullA}\tTwo\tHull Alpha\t12 km", $"{HullB}\tThree\tHull Bravo\t1 AU",
                $"{Pod}\tCapsule\tCapsule\t-", $"{Bubble}\tBubble\tBubble Maker\t30 km", $"{Cyno}\tCyno\tCyno Beacon\t2 AU",
                $"{Wreck}\tWreck\tHull Alpha Wreck\t5 km", "99999999\tOdd\tSomething New\t5 km",
                "Four\tHull Bravo\t3 AU");
            var lines = IntelPasteService.DscanLines(text);
            DscanType? TypeOf(DscanLine l) =>
                l.TypeId is int id && Types.TryGetValue(id, out var t) ? t
                : Types.Values.FirstOrDefault(t => t.Name == l.TypeName);
            var d = IntelPasteService.Summarise(lines, TypeOf, Groups());

            c.Check("d-scan: ships and capsules count, by hull, most first",
                d.Ships == 5 && d.Hulls.Count == 3 && d.Hulls[0] is { TypeId: HullA, Count: 2 } && d.Hulls.Any(h => h is { TypeId: HullB, Count: 2 })
                && d.Hulls.Any(h => h is { TypeId: Pod, Count: 1 }),
                string.Join(",", d.Hulls.Select(h => $"{h.Name}×{h.Count}")));
            c.Check("d-scan: a three-column line is found by its type's name", d.Hulls.First(h => h.TypeId == HullB).Count == 2);
            c.Check("d-scan: a mobile warp disruptor says bubbles, a cynosural field a cyno",
                d.Bubbles == 1 && d.Cynos == 1 && d.Flags == ((int)IntelRules.IntelFlags.Bubbles | (int)IntelRules.IntelFlags.Cyno));
            c.Check("d-scan: a wreck is no ship, and a type the SDE lacks is counted unknown", d.Unknown == 1 && d.Ships == 5);
            c.Check("d-scan: the hulls as a report stores them",
                d.ShipsText == "2× Hull Alpha, 2× Hull Bravo, Capsule", d.ShipsText ?? "null");

            var none = IntelPasteService.Summarise(lines, TypeOf, IntelSdeGroups.Empty);
            c.Check("d-scan: with no SDE groups found, nothing is a ship and no flag is set", none.Ships == 0 && none.Flags == 0);
        }

        // ── The preview's groups and "replaces N" ────────────────────────────────────────────────────────────

        public static void Preview(Checks c)
        {
            var pilots = new List<IntelPastePilot>
            {
                new("Neutral One", 1, 11, 0, StandingTier.Neutral), new("Red One", 2, 12, 101, StandingTier.Red),
                new("Red Two", 3, 12, 101, StandingTier.Red), new("Red Three", 4, 13, 101, StandingTier.Red),
                new("Mine", 5, 14, 0, StandingTier.Own), new("Friend", 6, 15, 102, StandingTier.Blue),
            };
            var groups = IntelPasteService.Group(pilots, ["Nobody Atall"], id => id == 101 ? "Foe Alliance" : id == 12 ? "Foe Corp" : null);
            c.Check("paste groups: worst tier first, then the rest, names that are nobody after, our own last",
                groups.Select(g => g.Kind + ":" + g.Tier).SequenceEqual(
                    ["Alliance:Red", "NoAlliance:Neutral", "Alliance:Blue", "Unresolved:Neutral", "Own:Own"]),
                string.Join(",", groups.Select(g => g.Kind + ":" + g.Tier)));
            c.Check("paste groups: an alliance's corporations, the biggest first, each with its pilots",
                groups[0] is { Name: "Foe Alliance", Count: 3 } g0 && g0.Corps[0] is { CorporationId: 12, Name: "Foe Corp" } && g0.Corps[0].Pilots.Count == 2);

            var now = new DateTimeOffset(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
            var snapshot = IntelSnapshotBuilder.Build(new IntelSnapshotInput
            {
                Now = now, Settings = new IntelSettingsValues(),
                Reports = [new IntelReport { Id = 1, ReportedAt = Iso(now.AddMinutes(-2)), SystemId = Here, PlayerCount = 3, ChannelName = "intelcheck" },
                           new IntelReport { Id = 2, ReportedAt = Iso(now.AddMinutes(-40)), SystemId = There, PlayerCount = 1, ChannelName = "intelcheck" }],
                ReportPilots = [new IntelReportCharacter { IntelReportId = 1, CharacterId = Earlier, CharacterName = "Earlier" }],
                LastSeen = [new IntelLastSeen { CharacterId = Earlier, SystemId = Here, SeenAt = Iso(now.AddMinutes(-2)), ReportId = 1, WrittenAt = Iso(now) }],
            }, new IntelCampRegister(), 1);
            c.Check("paste: a Local replaces the pilots and the unnamed count there now", IntelPasteService.ReplacedIn(snapshot, Here, now) == 3,
                IntelPasteService.ReplacedIn(snapshot, Here, now).ToString(CultureInfo.InvariantCulture));
            c.Check("paste: nothing expired is counted as replaced", IntelPasteService.ReplacedIn(snapshot, There, now) == 0);
        }

        // ── Stored, on a database ────────────────────────────────────────────────────────────────────────────

        public static async Task DatabaseAsync(Checks c, string engine, DbContextOptions<AppDbContext> options)
        {
            var now = DateTimeOffset.UtcNow;
            await using (var db = new AppDbContext(options))
            {
                await db.IntelReportCharacters.ExecuteDeleteAsync();
                await db.IntelReports.ExecuteDeleteAsync();
                await db.IntelLastSeens.ExecuteDeleteAsync();
                await db.CharacterAffiliations.ExecuteDeleteAsync();
                await db.CharacterStatuses.ExecuteDeleteAsync();
                await db.Characters.ExecuteDeleteAsync();
                await db.AppPreferences.Where(p => p.Key.StartsWith("intel.")).ExecuteDeleteAsync();
                await db.SdeSolarSystems.ExecuteDeleteAsync();
                await db.SdeTypes.Where(t => t.TypeId >= HullA && t.TypeId <= Wreck).ExecuteDeleteAsync();
                await db.SdeGroups.Where(g => g.GroupId >= HullGroup && g.GroupId <= WreckGroup).ExecuteDeleteAsync();
                await db.SdeCategories.Where(x => x.CategoryId == ShipCat || x.CategoryId == DeployCat || x.CategoryId == CelestialCat).ExecuteDeleteAsync();

                db.SdeSolarSystems.Add(new SdeSolarSystem { SolarSystemId = Here, Name = "QZ-X77", RegionId = 1, ConstellationId = 1 });
                db.SdeSolarSystems.Add(new SdeSolarSystem { SolarSystemId = There, Name = "XQ1-Z2", RegionId = 1, ConstellationId = 1 });
                db.SdeCategories.AddRange(new SdeCategory { CategoryId = ShipCat, Name = IntelSdeGroups.ShipCategoryName },
                                          new SdeCategory { CategoryId = DeployCat, Name = "Deployable" },
                                          new SdeCategory { CategoryId = CelestialCat, Name = "Celestial" });
                foreach (var (id, cat, name) in new[] { (HullGroup, ShipCat, "Frigate"), (PodGroup, ShipCat, IntelSdeGroups.CapsuleName),
                             (BubbleGroup, DeployCat, IntelSdeGroups.MobileDisruptorName), (CynoGroup, CelestialCat, IntelSdeGroups.CynoFieldName),
                             (WreckGroup, CelestialCat, "Wreck") })
                    db.SdeGroups.Add(new SdeGroup { GroupId = id, CategoryId = cat, Name = name });
                foreach (var t in Types.Values) db.SdeTypes.Add(new SdeType { TypeId = t.TypeId, GroupId = t.GroupId, Name = t.Name });
                db.Characters.Add(new Character { Id = Me, Name = "Pilot Mine", CorporationId = (int)MyCorp });
                db.CharacterAffiliations.Add(new CharacterAffiliation { CharacterId = Foe, CorporationId = FoeCorp, PulledAt = now });
                db.CharacterAffiliations.Add(new CharacterAffiliation { CharacterId = Mate, CorporationId = MyCorp, PulledAt = now });

                // Reported earlier: one named pilot and two more in QZ-X77; one named pilot in XQ1-Z2.
                var here = new IntelReport { ReportedAt = Iso(now.AddMinutes(-3)), ChannelName = "intelcheck", ReporterName = "Scout", SystemId = Here,
                                             SystemName = "QZ-X77", PlayerCount = 3, Message = "QZ-X77 Earlier +2", ChatMessageId = 7_001 };
                var there = new IntelReport { ReportedAt = Iso(now.AddMinutes(-3)), ChannelName = "intelcheck", ReporterName = "Scout", SystemId = There,
                                              SystemName = "XQ1-Z2", PlayerCount = 1, Message = "XQ1-Z2 Stays", ChatMessageId = 7_002 };
                db.IntelReports.AddRange(here, there);
                await db.SaveChangesAsync();
                db.IntelReportCharacters.Add(new IntelReportCharacter { IntelReportId = here.Id, CharacterId = Earlier, CharacterName = "Earlier" });
                db.IntelReportCharacters.Add(new IntelReportCharacter { IntelReportId = there.Id, CharacterId = Stays, CharacterName = "Stays" });
                await db.SaveChangesAsync();
                await IntelLastSeenWriter.UpsertAsync(db,
                [
                    new LastSeenRow { CharacterId = Earlier, SeenAt = Iso(now.AddMinutes(-3)), Source = IntelLastSeenSource.Report, SystemId = Here, ReportId = here.Id, Name = "Earlier" },
                    new LastSeenRow { CharacterId = Stays, SeenAt = Iso(now.AddMinutes(-3)), Source = IntelLastSeenSource.Report, SystemId = There, ReportId = there.Id, Name = "Stays" },
                ]);
            }

            var factory = new Factory(options);
            var snapshots = new IntelSnapshotService(factory, new IntelStandingService(factory), prefs: Prefs.Over(options));
            var resolved = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
            {
                ["Foe Pilot"] = Foe, ["Corp Mate"] = Mate, ["Pilot Mine"] = Me,
            };
            var asked = 0;
            var paste = new IntelPasteService(factory,
                (names, _) => { asked++; return Task.FromResult<IReadOnlyDictionary<string, long>>(names.Where(resolved.ContainsKey).ToDictionary(n => n, n => resolved[n])); },
                snapshots: snapshots);

            var before = await snapshots.RefreshNowAsync();
            var local = await paste.PreviewAsync("Foe Pilot\nCorp Mate\nPilot Mine\nNobody Atall\n", Me, Here, before, now);
            c.Check($"{engine} paste: a Local previews its names, the characters among them and the rest",
                local is { Kind: IntelPasteKind.Local, PlayerCount: 4 } && local.Pilots.Count == 3 && local.Unresolved.SequenceEqual(["Nobody Atall"]) && asked == 1,
                $"{local.Kind} {local.Pilots.Count} {string.Join(",", local.Unresolved)}");
            c.Check($"{engine} paste: the preview says it replaces the three reported there", local.Replaces == 3, local.Replaces.ToString(CultureInfo.InvariantCulture));
            c.Check($"{engine} paste: our own character is Own, a corp-mate Blue, the rest Neutral, as the viewer sees them",
                local.Pilots.Single(p => p.CharacterId == Me).Tier == StandingTier.Own
                && local.Pilots.Single(p => p.CharacterId == Mate).Tier == StandingTier.Blue
                && local.Pilots.Single(p => p.CharacterId == Foe) is { Tier: StandingTier.Neutral, CorporationId: FoeCorp },
                string.Join(",", local.Pilots.Select(p => $"{p.Name}:{p.Tier}:{p.CorporationId}")));
            await using (var db = new AppDbContext(options))
                c.Check($"{engine} paste: a preview writes nothing", await db.IntelReports.CountAsync() == 2);

            var localId = await paste.SaveAsync(local, now);
            await using (var db = new AppDbContext(options))
            {
                var report = await db.IntelReports.AsNoTracking().SingleAsync(r => r.Id == localId);
                c.Check($"{engine} paste: a Local is stored as a report of its own, with no chat message",
                    report is { Source: IntelReportSource.LocalPaste, Kind: IntelReportKind.Sighting, ChatMessageId: 0, PlayerCount: 4, ChannelName: "",
                                ReporterCharacterId: Me, ReporterName: "Pilot Mine", SystemName: "QZ-X77" },
                    $"{report.Source} {report.ChatMessageId} {report.PlayerCount} {report.ReporterName}");
                var stored = await db.IntelReportCharacters.AsNoTracking().Where(p => p.IntelReportId == localId).Select(p => p.CharacterId).ToListAsync();
                c.Check($"{engine} paste: every character on it is stored, blues and our own too", stored.Order().SequenceEqual(new[] { Foe, Mate, Me }.Order()));
                c.Check($"{engine} paste: the earlier report of the system is taken out, the other system's stays",
                    await db.IntelReports.AsNoTracking().Where(r => r.SystemId == Here && r.Id != localId).AllAsync(r => r.Obsolete)
                    && await db.IntelReports.AsNoTracking().Where(r => r.SystemId == There).AllAsync(r => !r.Obsolete));
                var seen = await db.IntelLastSeens.AsNoTracking().Where(s => s.CharacterId == Foe || s.CharacterId == Mate || s.CharacterId == Me).ToListAsync();
                c.Check($"{engine} paste: each pilot is last seen there, from a pasted Local",
                    seen.Count == 3 && seen.All(s => s.SystemId == Here && s.Source == IntelLastSeenSource.LocalPaste && s.ReportId == localId));
            }

            var after = await snapshots.RefreshNowAsync();
            var sys = after.Systems.GetValueOrDefault(Here);
            c.Check($"{engine} paste: the system now holds the pasted pilots and not the earlier one",
                sys is not null && sys.PilotIds.Order().SequenceEqual(new[] { Foe, Mate, Me }.Order()) && !sys.PilotIds.Contains(Earlier),
                sys is null ? "no system" : string.Join(",", sys.PilotIds));
            c.Check($"{engine} paste: the unresolved name is counted as one unnamed pilot", sys?.Unnamed?.Count == 1, sys?.Unnamed?.Count.ToString(CultureInfo.InvariantCulture) ?? "none");
            var view = IntelLens.Project(after, new IntelViewer { StandingsCharacterId = Me }, now, _ => null);
            var row = view.Systems.FirstOrDefault(r => r.SystemId == Here);
            c.Check($"{engine} paste: the viewer sees the hostile and the unnamed, not the blue or their own",
                row is not null && row.Pilots.Select(p => p.Pilot.CharacterId).SequenceEqual([Foe]) && row.Unnamed == 1,
                row is null ? "no row" : string.Join(",", row.Pilots.Select(p => p.Pilot.CharacterId)) + " +" + row.Unnamed);
            c.Check($"{engine} paste: the Local is a line of the feed, from the pastes",
                after.Feed.Any(e => e.ReportId == localId && e.Kind == IntelEventKind.LocalPaste && e.SourceKey == IntelSnapshot.SourcePaste));

            // A d-scan of the other system: an unnamed count of ships, bubbles and a cyno, and nobody taken out.
            var text = string.Join("\n", $"{HullA}\tOne\tHull Alpha\t10 km", $"{HullA}\tTwo\tHull Alpha\t12 km", $"{Pod}\tCapsule\tCapsule\t-",
                                   $"{Bubble}\tBubble\tBubble Maker\t30 km", $"{Cyno}\tCyno\tCyno Beacon\t2 AU", "Three\tHull Bravo\t1 AU");
            var dscan = await paste.PreviewAsync(text, Me, There, after, now);
            c.Check($"{engine} paste: a d-scan previews its ships by type id and by name",
                dscan is { Kind: IntelPasteKind.Dscan, PlayerCount: 4 } && dscan.Dscan.Bubbles == 1 && dscan.Dscan.Cynos == 1,
                $"{dscan.Kind} {dscan.Dscan.Ships} b{dscan.Dscan.Bubbles} c{dscan.Dscan.Cynos} u{dscan.Dscan.Unknown}");
            var dscanId = await paste.SaveAsync(dscan, now.AddSeconds(1));
            await using (var db = new AppDbContext(options))
            {
                var report = await db.IntelReports.AsNoTracking().SingleAsync(r => r.Id == dscanId);
                c.Check($"{engine} paste: a second paste stores too, with no chat message either",
                    report is { Source: IntelReportSource.DscanPaste, ChatMessageId: 0, PlayerCount: 4 }
                    && report.Flags == ((int)IntelRules.IntelFlags.Bubbles | (int)IntelRules.IntelFlags.Cyno)
                    && report.Ships == "2× Hull Alpha, Capsule, Hull Bravo",
                    $"{report.Source} {report.Flags} {report.Ships}");
                c.Check($"{engine} paste: a d-scan names nobody and takes nobody out",
                    !await db.IntelReportCharacters.AnyAsync(p => p.IntelReportId == dscanId)
                    && await db.IntelReports.AsNoTracking().Where(r => r.SystemId == There && r.Id != dscanId).AllAsync(r => !r.Obsolete));
            }
            var last = await snapshots.RefreshNowAsync();
            var thereNow = last.Systems.GetValueOrDefault(There);
            c.Check($"{engine} paste: the d-scan's count less the pilot already placed there is unnamed, and the pilot stays",
                thereNow is not null && thereNow.PilotIds.Contains(Stays) && thereNow.Unnamed?.Count == 3
                && thereNow.Facts.Any(f => f.Flag == IntelRules.IntelFlags.Bubbles) && thereNow.Facts.Any(f => f.Flag == IntelRules.IntelFlags.Cyno),
                thereNow is null ? "none" : $"{string.Join(",", thereNow.PilotIds)} +{thereNow.Unnamed?.Count} {string.Join(",", thereNow.Facts.Select(f => f.Flag))}");

            var recent = await paste.RecentAsync();
            c.Check($"{engine} paste: the recent pastes, newest first", recent.Count == 2 && recent[0].ReportId == dscanId && recent[1].ReportId == localId);

            var unknown = await paste.PreviewAsync("not\ta paste", Me, Here, last, now);
            c.Check($"{engine} paste: something else cannot be saved", !unknown.CanSave);
            var noSystem = await paste.PreviewAsync("Foe Pilot", Me, 0, last, now);
            c.Check($"{engine} paste: a Local with no system chosen cannot be saved", !noSystem.CanSave);
            snapshots.Dispose();
        }
    }
}
