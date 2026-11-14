using System.Text.Json;
using EveConsole.Localization;
using EveConsole.Models;

namespace EveConsole.Services.Mumble;

/// <summary>
/// A server's channel targets: who a talk key reaches. Saved on the server's row as JSON, as they are only ever read and
/// written whole.
///
/// <para>A server takes 30 saved voice targets, numbered from 1, and a client says which one each packet of voice is for.
/// Number 0 needs no saving: it is plain talk, the current channel and whatever is linked to it, which is the standard
/// client's Push to Talk.</para>
/// </summary>
public static class MumbleTargets
{
    /// <summary>A target's channel by this name is the root, which has no path of its own.</summary>
    public const string RootPath = "/";

    /// <summary>How many a server has room for, besides plain talk and the one kept for what is selected.</summary>
    public const int MostSaved = 29;

    /// <summary>The server's last, kept for whatever is selected in the channel tree: see <see cref="EntriesOfPick"/>.</summary>
    public const int PickSlot = 30;

    /// <summary>
    /// A server's targets. A server that has never saved any gets the two everyone starts with, on the keys it already
    /// had. ⚠️ An empty list is not that: it means they were all removed, and they stay removed.
    /// </summary>
    public static List<MumbleTalkTarget> Read(MumbleServer server)
    {
        if (server.TalkTargets.Length > 0)
        {
            try
            {
                if (JsonSerializer.Deserialize<List<MumbleTalkTarget>>(server.TalkTargets) is { } saved) return saved;
            }
            catch (JsonException) { /* nothing a person could fix: they get the defaults, as a new server does */ }
        }

        return
        [
            new MumbleTalkTarget { Name = MumbleText.TargetDefaultWhisper, Key = server.WhisperKey, Channels = [new MumbleTargetChannel()] },
            new MumbleTalkTarget { Name = MumbleText.TargetDefaultShout, Key = server.ShoutKey, Channels = [new MumbleTargetChannel { Links = true }] },
        ];
    }

    public static string Write(IEnumerable<MumbleTalkTarget> targets) => JsonSerializer.Serialize(targets);

    /// <summary>Just where this client is and what is linked to it: plain talk says that without saving anything.</summary>
    public static bool IsPlainTalk(MumbleTalkTarget target) =>
        target.Group.Trim().Length == 0 && Names(target).Count == 0
        && target.Channels is [{ Path.Length: 0, Links: true, Subchannels: false }];

    /// <summary>
    /// Each target's number on the server, in order: 0 for plain talk, 1 onward for the rest, and -1 past what a server has
    /// room for. Saved ones are numbered by their place, so that the same list always gets the same numbers.
    /// </summary>
    public static int[] Slots(IReadOnlyList<MumbleTalkTarget> targets)
    {
        var slots = new int[targets.Count];
        var next = 1;
        for (var i = 0; i < targets.Count; i++)
            slots[i] = IsPlainTalk(targets[i]) ? 0 : next <= MostSaved ? next++ : -1;
        return slots;
    }

    /// <summary>Who a target reaches as things stand: channels by id and people by session, which change while connected.</summary>
    public static List<MumbleTargetEntry> EntriesOf(MumbleTalkTarget target, MumbleClient client)
    {
        var entries = new List<MumbleTargetEntry>();
        var group = target.Group.Trim();

        foreach (var channel in target.Channels)
        {
            uint? id = channel.Path switch
            {
                "" => client.Me?.ChannelId,
                RootPath => 0,
                var path => MumbleService.FindChannel(client, path),
            };
            if (id is { } found) entries.Add(new MumbleTargetEntry(found, group, channel.Links, channel.Subchannels, []));
        }

        var names = Names(target);
        if (names.Count > 0)
        {
            var sessions = client.Users.Where(u => names.Contains(u.Name)).Select(u => u.Session).ToList();
            if (sessions.Count > 0) entries.Add(new MumbleTargetEntry(null, "", false, false, sessions));
        }

        return entries;
    }

    /// <summary>
    /// Who the selection key reaches: the person selected, or the channel, with its links and subchannels if asked. Nothing
    /// once they have gone from the server.
    /// </summary>
    public static List<MumbleTargetEntry> EntriesOfPick(MumblePick? pick, bool links, bool subchannels, MumbleClient client) => pick switch
    {
        { IsPerson: true } when client.UserOf(pick.Id) is not null => [new MumbleTargetEntry(null, "", false, false, [pick.Id])],
        { IsPerson: false } when client.ChannelOf(pick.Id) is not null => [new MumbleTargetEntry(pick.Id, "", links, subchannels, [])],
        _ => [],
    };

    private static HashSet<string> Names(MumbleTalkTarget target) =>
        target.People.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
}
