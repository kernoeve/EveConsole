// How an intel channel is named on screen: "EVE Chat - <channel>" for a channel of the game's chat logs, "<server> - <room>"
// for a Jabber room or an IRC channel marked Intel, the server by what its settings call it (else its host); two servers
// called the same keep apart by their hosts; the Sources choice lists the game's channels, then each server's, then the kills
// and the pastes; and a choice saved under the stored names still holds after a server is renamed. Every name here is invented.
using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using EveConsole.Localization;
using EveConsole.Models;
using EveConsole.Monitoring;
using EveConsole.Services;
using EveConsole.Services.Intel;
using EveConsole.ViewModels;

namespace EveConsole.Tools.IntelCheck
{
    internal static class ChannelLabelChecks
    {
        // The stored names, as the parser keeps them.
        public const string Game = "qzx-intel", GameB = "Qzx Home Watch";
        public static readonly string Scouts = IntelRooms.JabberPrefix + "qzx-scouts";
        public static readonly string Watch  = IntelRooms.IrcPrefix + "#qzx-watch";
        public static readonly string Relay  = IntelRooms.IrcPrefix + "#qzx-relay";

        private static readonly JabberAccount CommsJabber = new() { Id = 1, Name = "Qzx Comms", Address = "scout@qzx-one.invalid", Host = "chat.qzx-one.invalid" };
        private static readonly IrcServer CommsIrc = new() { Id = 1, Name = "Qzx Comms", Host = "irc.qzx-two.invalid", Nickname = "ScoutQzx" };
        private static readonly IrcServer Qzxnet = new() { Id = 2, Name = "Qzxnet", Host = "irc.qzxnet.invalid", Nickname = "ScoutQzx" };

        /// <summary>One Jabber login and two IRC networks, two of them called "Qzx Comms".</summary>
        public static IntelChannelLabels Labels(string commsIrcName = "Qzx Comms", string qzxnetName = "Qzxnet") => IntelChannelLabels.Build(
            [CommsJabber],
            [new JabberRoom { AccountId = 1, RoomJid = "qzx-scouts@rooms.qzx-one.invalid", Name = "qzx-scouts", RoomType = JabberRoomType.Intel }],
            [CommsIrc.With(commsIrcName), Qzxnet.With(qzxnetName)],
            [new IrcChannel { ServerId = 1, Name = "#qzx-watch", RoomType = JabberRoomType.Intel },
             new IrcChannel { ServerId = 2, Name = "#qzx-relay", RoomType = JabberRoomType.Intel }]);

        private static IrcServer With(this IrcServer s, string name) => new() { Id = s.Id, Name = name, Host = s.Host, Nickname = s.Nickname };

        /// <summary>A snapshot with the rooms above and two game channels known, labelled.</summary>
        public static IntelSnapshot WithRooms(IntelSnapshot snapshot, IntelChannelLabels? labels = null) =>
            snapshot with { Channels = [Relay, Game, Watch, GameB, Scouts], ChannelLabels = labels ?? Labels() };

        public static void Run(Checks c)
        {
            var culture = CultureInfo.CurrentUICulture;
            var format = CultureInfo.CurrentCulture;
            CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture = new CultureInfo("en-US");
            try { Kinds(c); SameName(c); Order(c); Lines(c); }
            finally { CultureInfo.CurrentUICulture = culture; CultureInfo.CurrentCulture = format; }
        }

        private static void Kinds(Checks c)
        {
            var labels = Labels();
            c.Check("labels: a game chat channel is EVE Chat - <channel>", labels.Label(Game) == "EVE Chat - qzx-intel", labels.Label(Game));
            c.Check("labels: with no servers known too", IntelChannelLabels.Empty.Label(GameB) == "EVE Chat - Qzx Home Watch",
                IntelChannelLabels.Empty.Label(GameB));
            c.Check("labels: an IRC channel is <network> - <channel>", labels.Label(Relay) == "Qzxnet - #qzx-relay", labels.Label(Relay));
            c.Check("labels: blank stays blank", labels.Label("") == "" && labels.Label(null) == "");

            // A network with no name goes by its host; a Jabber login with neither name nor host by its address's domain.
            var bare = IntelChannelLabels.Build(
                [new JabberAccount { Id = 7, Address = "scout@qzx-three.invalid" }],
                [new JabberRoom { AccountId = 7, RoomJid = "qzx-pickets@rooms.qzx-three.invalid", Name = "", RoomType = JabberRoomType.Intel }],
                [new IrcServer { Id = 8, Host = "irc.qzx-four.invalid" }],
                [new IrcChannel { ServerId = 8, Name = "#qzx-gate", RoomType = JabberRoomType.Intel }]);
            var pickets = IntelRooms.JabberPrefix + "qzx-pickets";
            c.Check("labels: a Jabber login with no name goes by its address's domain",
                bare.Label(pickets) == "qzx-three.invalid - qzx-pickets", bare.Label(pickets));
            c.Check("labels: an IRC network with no name goes by its host",
                bare.Label(IntelRooms.IrcPrefix + "#qzx-gate") == "irc.qzx-four.invalid - #qzx-gate", bare.Label(IntelRooms.IrcPrefix + "#qzx-gate"));

            // A room whose login is gone still says what it was.
            c.Check("labels: a room no login keeps any more says Jabber or IRC",
                labels.Label(IntelRooms.JabberPrefix + "qzx-old") == "Jabber - qzx-old" && labels.Label(IntelRooms.IrcPrefix + "#qzx-old") == "IRC - #qzx-old",
                labels.Label(IntelRooms.JabberPrefix + "qzx-old") + " / " + labels.Label(IntelRooms.IrcPrefix + "#qzx-old"));
        }

        private static void SameName(Checks c)
        {
            var labels = Labels();
            c.Check("labels: a Jabber room is <server> - <room>, the host added when another server is called the same",
                labels.Label(Scouts) == "Qzx Comms (chat.qzx-one.invalid) - qzx-scouts", labels.Label(Scouts));
            c.Check("labels: and the other server of that name by its own host",
                labels.Label(Watch) == "Qzx Comms (irc.qzx-two.invalid) - #qzx-watch", labels.Label(Watch));
            c.Check("labels: two servers called the same stay distinct", labels.Label(Scouts) != labels.Label(Watch)
                && labels.ServerOf(Scouts) != labels.ServerOf(Watch));

            // Two logins on one server, called the same: the logins tell them apart.
            var twice = IntelChannelLabels.Build(
                [new JabberAccount { Id = 1, Name = "Qzx Comms", Address = "alpha@qzx-one.invalid", Host = "chat.qzx-one.invalid" },
                 new JabberAccount { Id = 2, Name = "Qzx Comms", Address = "bravo@qzx-one.invalid", Host = "chat.qzx-one.invalid" }],
                [new JabberRoom { AccountId = 1, RoomJid = "qzx-a@rooms.qzx-one.invalid", Name = "qzx-a", RoomType = JabberRoomType.Intel },
                 new JabberRoom { AccountId = 2, RoomJid = "qzx-b@rooms.qzx-one.invalid", Name = "qzx-b", RoomType = JabberRoomType.Intel }],
                [], []);
            var (a, b) = (twice.Label(IntelRooms.JabberPrefix + "qzx-a"), twice.Label(IntelRooms.JabberPrefix + "qzx-b"));
            c.Check("labels: two logins on one server called the same, told apart by the login",
                a == "Qzx Comms (alpha@qzx-one.invalid) - qzx-a" && b == "Qzx Comms (bravo@qzx-one.invalid) - qzx-b", a + " / " + b);

            // Renamed apart, the host is no longer needed.
            var renamed = Labels(commsIrcName: "Qzx Relay Net");
            c.Check("labels: a server renamed apart is shown by its name alone",
                renamed.Label(Scouts) == "Qzx Comms - qzx-scouts" && renamed.Label(Watch) == "Qzx Relay Net - #qzx-watch",
                renamed.Label(Scouts) + " / " + renamed.Label(Watch));
        }

        private static void Order(Checks c)
        {
            var list = IntelViewModel.SourceList(WithRooms(IntelSnapshot.Empty));
            var keys = list.Select(s => s.Key).ToList();
            c.Check("sources: the game's channels first, then each server by name, each by its room's name; kills and pastes last",
                keys.SequenceEqual([GameB, Game, Scouts, Watch, Relay, IntelSnapshot.SourceKills, IntelSnapshot.SourcePaste]),
                string.Join(" | ", list.Select(s => s.Label)));
            c.Check("sources: each listed by its label, its key the stored name",
                list.Take(5).All(s => s.Label == Labels().Label(s.Key)) && list[^2].Label == IntelText.SourceKills && list[^1].Label == IntelText.SourcePastes);
        }

        /// <summary>The Feed and Board line's source text, its tooltip and the places it was also heard, all by the label.</summary>
        private static void Lines(Checks c)
        {
            var now = HostileScene.Now;
            var report = new IntelReport
            {
                Id = 7_901, ReportedAt = IntelLastSeenWriter.Format(now.AddMinutes(-1)), ChannelName = Watch, ReporterName = "Scout Qzx",
                SystemId = HostileScene.Lone, SystemName = "ZZQ-71", PlayerCount = 2, Kind = IntelReportKind.Sighting,
                Source = IntelReportSource.Chat, Message = "ZZQ-71 +2",
            };
            var copy = new IntelReport
            {
                Id = 7_902, ReportedAt = IntelLastSeenWriter.Format(now.AddMinutes(-0.5)), ChannelName = Game, ReporterName = "Scout Relay",
                SystemId = HostileScene.Lone, SystemName = "ZZQ-71", Kind = IntelReportKind.Copy, ParentReportId = report.Id,
                Source = IntelReportSource.Chat, Message = "ZZQ-71 +2",
            };
            var snapshot = IntelSnapshotBuilder.Build(new IntelSnapshotInput
            {
                Now = now, Settings = new IntelSettingsValues(), Reports = [report, copy], ChannelLabels = Labels(),
                SystemNames = HostileScene.Systems.ToDictionary(s => s.Id, s => s.Name),
            }, new IntelCampRegister(), 1);
            var row = IntelLens.Project(snapshot, new IntelViewer { IncludeExpired = true }, now, _ => null).Feed
                .FirstOrDefault(r => r.Event.ReportId == report.Id);
            c.Check("lines: the report is on the feed", row is not null);
            if (row is null) return;
            var line = new IntelEventRowVm(row, snapshot, now, false);
            c.Check("lines: the source says the label and who", line.SourceText == "Qzx Comms (irc.qzx-two.invalid) - #qzx-watch · Scout Qzx", line.SourceText);
            c.Check("lines: the tooltip says where it was reported by the label",
                line.ToolTip.Contains("in Qzx Comms (irc.qzx-two.invalid) - #qzx-watch", StringComparison.Ordinal), line.ToolTip);
            c.Check("lines: the places it was also heard by their labels",
                line.EchoTip is { } tip && tip.Contains("EVE Chat - qzx-intel · Scout Relay", StringComparison.Ordinal), line.EchoTip ?? "(none)");
            c.Check("lines: the lens still filters by the stored name",
                IntelLens.Project(snapshot, new IntelViewer { IncludeExpired = true, Sources = new HashSet<string> { Watch } }, now, _ => null)
                    .Feed.Any(r => r.Event.ReportId == report.Id));

            var chatter = new IntelChatterLineVm(new IntelChatter(1, now, Relay, "Scout Qzx", "anyone up?"), snapshot.ChannelLabel(Relay), now);
            c.Check("lines: a chatter line says the label", chatter.SourceText == "Qzxnet - #qzx-relay · Scout Qzx", chatter.SourceText);
        }

        /// <summary>
        /// The Sources choice saved under the stored names still holds after a server is renamed: on the tool showing it,
        /// and on a tool opened afresh (what the next start reads). Headless, with the tool grids' setup.
        /// </summary>
        public static void SavedSelection(Checks c)
        {
            var distances = new IntelDistanceService(HostileScene.Gates.ToDictionary(kv => kv.Key, kv => kv.Value), IntelDistanceService.Edges.None);
            var characters = new ObservableCollection<Character> { new() { Id = HostileScene.Me, Name = "Pilot Qzx Home" } };
            var tool = new IntelViewModel(null, distances, null, null, null, characters) { Clock = () => HostileScene.Now };
            try
            {
                tool.Apply(WithRooms(HostileScene.Snapshot()));
                Pump(() => tool.Sources.Count > 0);
                IntelSourceChoice? Choice(IntelViewModel t, string key) => t.Sources.FirstOrDefault(s => s.Key == key);
                if (Choice(tool, Watch) is not { } watch || Choice(tool, Scouts) is not { } scouts)
                {
                    c.Check("sources: the rooms are listed", false, string.Join(" | ", tool.Sources.Select(s => s.Key)));
                    return;
                }
                watch.IsOn = false;
                scouts.IsOn = false;

                // Both servers renamed: the labels change, the keys and what was switched off do not.
                var renamed = Labels(commsIrcName: "Qzx Relay Net", qzxnetName: "Qzxnet Two");
                tool.Apply(WithRooms(HostileScene.Snapshot(), renamed));
                Pump(() => Choice(tool, Watch)?.Label == renamed.Label(Watch));
                c.Check("sources: a renamed server's rooms are shown by the new name",
                    Choice(tool, Watch)?.Label == "Qzx Relay Net - #qzx-watch" && Choice(tool, Relay)?.Label == "Qzxnet Two - #qzx-relay",
                    string.Join(" | ", tool.Sources.Select(s => s.Label)));
                c.Check("sources: what was switched off stays off after the rename",
                    Choice(tool, Watch)?.IsOn == false && Choice(tool, Scouts)?.IsOn == false && Choice(tool, Relay)?.IsOn == true
                    && Choice(tool, Game)?.IsOn == true,
                    string.Join(" | ", tool.Sources.Select(s => s.Label + "=" + s.IsOn)));

                using (var again = new IntelViewModel(null, distances, null, null, null, characters) { Clock = () => HostileScene.Now })
                {
                    again.Apply(WithRooms(HostileScene.Snapshot(), renamed));
                    Pump(() => again.Sources.Count > 0);
                    c.Check("sources: and on a tool opened afresh, from the saved choice",
                        Choice(again, Watch)?.IsOn == false && Choice(again, Scouts)?.IsOn == false && Choice(again, Relay)?.IsOn == true,
                        string.Join(" | ", again.Sources.Select(s => s.Label + "=" + s.IsOn)));
                }

                // Everything back on, for the checks after.
                foreach (var s in tool.Sources.Where(s => !s.IsOn).ToList()) s.IsOn = true;
            }
            finally { tool.Dispose(); }
        }

        private static void Pump(Func<bool> done)
        {
            var until = DateTime.UtcNow.AddSeconds(10);
            while (!done() && DateTime.UtcNow < until)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(15);
            }
            Dispatcher.UIThread.RunJobs();
        }
    }
}
