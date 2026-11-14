// The Intel tool's Paste tab, headless, as the user meets it: the "Save:" standings chips (blues left out by
// default, the choice kept on this PC), the preview listing everyone with the tiers left out folded to a line each
// and the headline counting only what is saved, an all-blue Local shown as a clear, the Clear button by the box, and
// Pasted by following the system (the first online character's system and that same character by default, never a
// logged-off one). Also the mixed paste the headless picture of the tab shows. Every name and id here is invented.
using System.Collections.ObjectModel;
using System.Globalization;
using EveConsole.Models;
using EveConsole.Services;
using EveConsole.Services.Intel;
using EveConsole.ViewModels;

namespace EveConsole.Tools.IntelCheck
{
    internal static class PasteTabChecks
    {
        private static readonly DateTimeOffset Now = new(2026, 11, 13, 18, 0, 0, TimeSpan.Zero);
        private const int SysB = 39_920_001, SysC = 39_920_002;
        private const long Offline = 93_920_001, Bravo = 93_920_002, Charlie = 93_920_003, MyCorp = 98_920_001, MyAlliance = 99_920_001;
        private const long NeutralA = 94_920_001, NeutralB = 94_920_002, FirstBlue = 94_921_001, FoeCorp = 98_920_009;

        private static readonly StandingTier[] AllFive =
            [StandingTier.Red, StandingTier.Orange, StandingTier.Neutral, StandingTier.LightBlue, StandingTier.Blue];

        /// <summary>Bravo online in QZ-X77, Charlie online in XQ1-Z2; the first character by name is logged off.</summary>
        private static IntelSnapshot Scene() => IntelSnapshotBuilder.Build(new IntelSnapshotInput
        {
            Now = Now, Settings = new IntelSettingsValues(),
            SystemNames = new Dictionary<int, string> { [SysB] = "QZ-X77", [SysC] = "XQ1-Z2" },
            Own =
            [
                new OwnPosition(Bravo, "Pilot Qzx Bravo", SysB, Now.AddMinutes(-20), OwnPositionSource.ChatLog, MyCorp, MyAlliance),
                new OwnPosition(Charlie, "Pilot Qzx Charlie", SysC, Now.AddMinutes(-10), OwnPositionSource.ChatLog, MyCorp, MyAlliance),
            ],
        }, new IntelCampRegister(), 1);

        /// <summary>A Local of 91 names: 88 corp-mates (blue), the character pasting, two neutrals.</summary>
        private static IntelPastePreview Busy(int system, string systemName, long viewer)
        {
            var pilots = new List<IntelPastePilot>();
            for (var i = 0; i < 88; i++)
                pilots.Add(new("Qzx Mate " + i.ToString("00", CultureInfo.InvariantCulture), FirstBlue + i, MyCorp, MyAlliance, StandingTier.Blue));
            pilots.Insert(20, new("Qzx Neutral Alpha", NeutralA, FoeCorp, 0, StandingTier.Neutral));
            pilots.Insert(50, new("Pilot Qzx Bravo", viewer, MyCorp, MyAlliance, StandingTier.Own));
            pilots.Insert(70, new("Qzx Neutral Bravo", NeutralB, FoeCorp, 0, StandingTier.Neutral));
            return Preview(system, systemName, viewer, pilots, []);
        }

        private static IntelPastePreview Preview(int system, string systemName, long viewer, List<IntelPastePilot> pilots, List<string> unresolved,
                                                 Func<long, string?>? nameOf = null)
        {
            var names = pilots.Select(p => p.Name).Concat(unresolved).ToList();
            return new IntelPastePreview
            {
                Kind = IntelPasteKind.Local, Text = string.Join("\n", names), SystemId = system, SystemName = systemName,
                ViewerCharacterId = viewer, Names = names, Pilots = pilots, Unresolved = unresolved,
                Groups = IntelPasteService.Group(pilots, unresolved, nameOf ?? (_ => null)),
            };
        }

        /// <summary>
        /// A mixed Local for the picture of the tab: reds of two corporations of one alliance, an orange, a neutral with
        /// no alliance, a name that is no character, two light blues, six corp-mates and the user's own character.
        /// </summary>
        public static IntelPastePreview Mixed(int system, string systemName, long me)
        {
            const long Friends = 99_920_101, FriendCorp = 98_920_101;
            var names = new Dictionary<long, string>(HostileScene.Names) { [Friends] = "Qzx Friendly Coalition", [FriendCorp] = "Qzx Friendly Shipwrights" };
            var pilots = new List<IntelPastePilot>
            {
                new(HostileScene.PilotName(HostileScene.A1), HostileScene.A1, HostileScene.CorpA1, HostileScene.AllyA, StandingTier.Red),
                new(HostileScene.PilotName(HostileScene.A2), HostileScene.A2, HostileScene.CorpA1, HostileScene.AllyA, StandingTier.Red),
                new(HostileScene.PilotName(HostileScene.A3), HostileScene.A3, HostileScene.CorpA2, HostileScene.AllyA, StandingTier.Red),
                new(HostileScene.PilotName(HostileScene.B1), HostileScene.B1, HostileScene.CorpB, HostileScene.AllyB, StandingTier.Orange),
                new(HostileScene.PilotName(HostileScene.C1), HostileScene.C1, HostileScene.CorpC, 0, StandingTier.Neutral),
                new("Qzx Friend Ash", 94_920_101, FriendCorp, Friends, StandingTier.LightBlue),
                new("Qzx Friend Birch", 94_920_102, FriendCorp, Friends, StandingTier.LightBlue),
                new("Pilot Qzx Home", me, HostileScene.MyCorp, HostileScene.MyAlliance, StandingTier.Own),
            };
            for (var i = 0; i < 6; i++)
                pilots.Add(new("Qzx Corp Mate " + (i + 1).ToString(CultureInfo.InvariantCulture), 94_920_201 + i, HostileScene.MyCorp, HostileScene.MyAlliance, StandingTier.Blue));
            return Preview(system, systemName, me, pilots, ["Qzx Nobody Here"], id => names.GetValueOrDefault(id));
        }

        public static void Run(Checks c)
        {
            UiState.Set(IntelPasteVm.KeySaveTiers, null);
            var characters = new ObservableCollection<Character>
            {
                new() { Id = Offline, Name = "Aaron Qzx Offline" }, new() { Id = Bravo, Name = "Pilot Qzx Bravo" }, new() { Id = Charlie, Name = "Pilot Qzx Charlie" },
            };
            var tool = new IntelViewModel(null, null, null, null, null, characters) { Clock = () => Now };
            var again = (IntelViewModel?)null;
            try
            {
                var paste = tool.Paste;

                // ── The chips' default: blues left out ──
                c.Check("paste tab: by default the Save chips keep Red, Orange and Neutral, and leave Light blue and Blue out",
                    paste.SaveTiers.SetEquals(StandingTiers.Hostile) && paste is { SaveRed: true, SaveOrange: true, SaveNeutral: true, SaveLightBlue: false, SaveBlue: false },
                    string.Join(",", paste.SaveTiers));

                // ── The default system and Pasted by: the same online character ──
                tool.Apply(Scene());
                c.Check("paste tab: the default system is the first online character's, and Pasted by is that same character, not the logged-off one first by name",
                    paste.SelectedSystem?.Value == SysB && paste.SelectedCharacter?.Value == Bravo,
                    $"system {paste.SelectedSystem?.Label}, by {paste.SelectedCharacter?.Label}");

                paste.SelectedSystem = paste.SystemChoices.First(x => x.Value == SysC);
                c.Check("paste tab: choosing another character's system moves Pasted by to that character",
                    paste.SelectedCharacter?.Value == Charlie, paste.SelectedCharacter?.Label ?? "none");

                // ── Clear: the box and the preview go, the choices stay ──
                paste.Text = "Qzx Neutral Alpha\nQzx Mate 01";
                paste.Show(Busy(SysC, "XQ1-Z2", Charlie));
                var shownBefore = paste.HasPreview && paste.Groups.Count > 0;
                paste.ClearCommand.Execute().Subscribe(_ => { }, _ => { });
                c.Check("paste tab: Clear empties the box and the preview, and keeps the system and Pasted by",
                    shownBefore && paste.Text.Length == 0 && !paste.HasPreview && paste.Groups.Count == 0 && paste.PlacesText.Length == 0
                    && paste.DetectedText.Length == 0 && paste.SelectedSystem?.Value == SysC && paste.SelectedCharacter?.Value == Charlie,
                    $"text '{paste.Text}', preview {paste.HasPreview}, system {paste.SelectedSystem?.Label}, by {paste.SelectedCharacter?.Label}");

                // ── The preview of a busy Local: everyone listed, only the kept counted ──
                paste.Show(Busy(SysB, "QZ-X77", Bravo));
                var outLines = paste.Groups.Where(g => g.IsLeftOut).Select(g => g.Name + ":" + g.CountText).ToList();
                c.Check("paste tab: a 91-name Local with two neutrals says it places 2, and 89 left out (blue and yours)",
                    paste.PlacesText == "Places 2 pilots in QZ-X77" && paste.LeftOutText == "89 left out (Blue, Yours)"
                    && paste.CountsText.StartsWith("91 names", StringComparison.Ordinal) && !paste.IsClearPaste,
                    $"'{paste.PlacesText}' / '{paste.LeftOutText}' / '{paste.CountsText}'");
                c.Check("paste tab: the tiers left out fold to one dimmed line each with their count, the kept ones list their pilots",
                    outLines.SequenceEqual(["Blue:88", "Yours:1"]) && paste.Groups.Where(g => !g.IsLeftOut).Sum(g => g.Corps.Sum(x => x.Pilots.Count)) == 2
                    && paste.Groups.Where(g => g.IsLeftOut).All(g => g.Corps.Count == 0 && g.RowOpacity < 1),
                    string.Join(" | ", paste.Groups.Select(g => $"{g.Name}:{g.CountText}:{(g.IsLeftOut ? "out" : "in")}")));
                c.Check("paste tab: what Save would store is the two", paste.Shown is { PlayerCount: 2, IsClear: false } s0 && s0.Kept.Count == 2);

                paste.SaveBlue = true;
                c.Check("paste tab: switching Blue on recounts at once: 90 placed, only your own left out",
                    paste.PlacesText == "Places 90 pilots in QZ-X77" && paste.LeftOutText == "1 left out (Yours)" && paste.Shown?.PlayerCount == 90,
                    $"'{paste.PlacesText}' / '{paste.LeftOutText}'");
                paste.SaveBlue = false;

                // ── An all-blue Local: a clear ──
                var blues = Busy(SysB, "QZ-X77", Bravo);
                paste.Show(blues with { Pilots = [.. blues.Pilots.Where(p => p.Tier != StandingTier.Neutral)], Names = [.. blues.Names.Where(n => !n.StartsWith("Qzx Neutral", StringComparison.Ordinal))] });
                c.Check("paste tab: an all-blue Local says it saves the system as clear",
                    paste.IsClearPaste && paste.PlacesText.StartsWith("Saves QZ-X77 as clear", StringComparison.Ordinal) && paste.Shown is { IsClear: true, CanSave: true }
                    && paste.LeftOutText == "89 left out (Blue, Yours)",
                    $"'{paste.PlacesText}' clear {paste.IsClearPaste}");

                // ── Pasted by picked by hand stays ──
                paste.Clear();
                paste.CharacterPicked();
                paste.SelectedCharacter = paste.CharacterChoices.First(x => x.Value == Bravo);
                paste.SelectedSystem = paste.SystemChoices.First(x => x.Value == SysB);
                paste.SelectedSystem = paste.SystemChoices.First(x => x.Value == SysC);
                c.Check("paste tab: Pasted by picked by hand does not follow the system", paste.SelectedCharacter?.Value == Bravo, paste.SelectedCharacter?.Label ?? "none");

                // ── Nobody online: as before, the first character there is ──
                var nobody = new IntelViewModel(null, null, null, null, null, characters) { Clock = () => Now };
                nobody.Apply(IntelSnapshotBuilder.Build(new IntelSnapshotInput { Now = Now, Settings = new IntelSettingsValues() }, new IntelCampRegister(), 1));
                c.Check("paste tab: with nobody online, Pasted by is the first character as before", nobody.Paste.SelectedCharacter?.Value == Offline,
                    nobody.Paste.SelectedCharacter?.Label ?? "none");
                nobody.Dispose();

                // ── The choice is kept on this PC ──
                paste.SaveRed = false;
                paste.SaveLightBlue = true;
                var saved = UiState.Get(IntelPasteVm.KeySaveTiers);
                again = new IntelViewModel(null, null, null, null, null, characters) { Clock = () => Now };
                c.Check("paste tab: the Save chips are kept on this PC and read back by a new tool",
                    saved == "1,2,3" && again.Paste.SaveTiers.SetEquals([StandingTier.Orange, StandingTier.Neutral, StandingTier.LightBlue])
                    && again.Paste is { SaveRed: false, SaveLightBlue: true, SaveBlue: false },
                    $"saved '{saved}', read {string.Join(",", again.Paste.SaveTiers)}");
                again.Dispose();
                foreach (var t in AllFive) SetSave(paste, t, false);
                again = new IntelViewModel(null, null, null, null, null, characters) { Clock = () => Now };
                c.Check("paste tab: every chip off is kept as such, not read back as the default",
                    again.Paste.SaveTiers.Count == 0, UiState.Get(IntelPasteVm.KeySaveTiers) ?? "null");
            }
            finally
            {
                again?.Dispose();
                tool.Dispose();
                UiState.Set(IntelPasteVm.KeySaveTiers, null);
            }
        }

        private static void SetSave(IntelPasteVm paste, StandingTier tier, bool on)
        {
            switch (tier)
            {
                case StandingTier.Red:       paste.SaveRed = on; break;
                case StandingTier.Orange:    paste.SaveOrange = on; break;
                case StandingTier.Neutral:   paste.SaveNeutral = on; break;
                case StandingTier.LightBlue: paste.SaveLightBlue = on; break;
                case StandingTier.Blue:      paste.SaveBlue = on; break;
            }
        }
    }
}
