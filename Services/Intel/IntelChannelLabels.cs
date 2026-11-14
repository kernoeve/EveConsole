using System.Globalization;
using EveConsole.Localization;
using EveConsole.Models;
using EveConsole.Monitoring;

namespace EveConsole.Services.Intel;

/// <summary>
/// How an intel channel is named on screen: "EVE Chat - &lt;channel&gt;" for a channel read from the game's chat logs, and
/// "&lt;server&gt; - &lt;room&gt;" for a Jabber room or an IRC channel marked Intel, the server by what the person calls it in its
/// settings (else its host). Two servers called the same get their host after the name, so their rooms stay apart.
///
/// <para>⚠️ A label only. Everything else goes by the name the channel's reports are stored under (<see cref="IntelRooms.ChannelOf(JabberRoom)"/>
/// for a room, the chat log's own name for a game channel): the sources filter and the choice saved from it, the intel
/// channels setting, the parser, the copies across channels. A server renamed changes what is shown, never what is kept.</para>
/// </summary>
public sealed class IntelChannelLabels : IComparer<string>
{
    /// <summary>No servers known: a game channel is still "EVE Chat - …", and a room goes under "Jabber" or "IRC".</summary>
    public static readonly IntelChannelLabels Empty = new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    private const string JabberWord = "Jabber";
    private const string IrcWord    = "IRC";

    /// <summary>The server each room's stored channel name goes under, as shown.</summary>
    private readonly IReadOnlyDictionary<string, string> _servers;

    private IntelChannelLabels(IReadOnlyDictionary<string, string> servers) => _servers = servers;

    /// <summary>The labels for the rooms and channels these logins keep: all of them, so a room no longer marked Intel still
    /// reads right for the reports it left in the window.</summary>
    public static IntelChannelLabels Build(
        IEnumerable<JabberAccount> accounts, IEnumerable<JabberRoom> rooms,
        IEnumerable<IrcServer> servers, IEnumerable<IrcChannel> channels)
    {
        var named = accounts.Select(a => (Key: "j" + a.Id.ToString(CultureInfo.InvariantCulture), Name: NameOf(a), Host: HostOf(a), Login: a.Address.Trim()))
            .Concat(servers.Select(s => (Key: "i" + s.Id.ToString(CultureInfo.InvariantCulture), Name: NameOf(s), Host: s.Host.Trim(),
                                          Login: s.Nickname.Trim().Length > 0 ? s.Nickname.Trim() + "@" + s.Host.Trim() : s.Host.Trim())))
            .ToList();

        // Two servers called the same: each gets its host after the name, or its login where the host is shared too
        // (two logins on one server).
        var shown = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var same in named.GroupBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var list = same.ToList();
            foreach (var n in list)
            {
                if (list.Count == 1) { shown[n.Key] = n.Name; continue; }
                var hostShared = list.Count(o => string.Equals(o.Host, n.Host, StringComparison.OrdinalIgnoreCase)) > 1;
                var tell = hostShared && n.Login.Length > 0 ? n.Login : n.Host;
                shown[n.Key] = tell.Length > 0 && !string.Equals(tell, n.Name, StringComparison.OrdinalIgnoreCase)
                    ? string.Format(CultureInfo.CurrentCulture, IntelText.SourceServerHost, n.Name, tell)
                    : n.Name;
            }
        }

        // The stored name of each room, and the servers it is kept on. ⚠️ Two logins' rooms of the same name are stored
        // under one name, so both servers are named; those marked Intel first, as theirs are the ones read.
        var held = new List<(string Channel, bool Intel, string Server)>();
        foreach (var room in rooms)
            if (shown.TryGetValue("j" + room.AccountId.ToString(CultureInfo.InvariantCulture), out var server))
                held.Add((IntelRooms.ChannelOf(room), room.RoomType == JabberRoomType.Intel && !room.Left, server));
        foreach (var channel in channels)
            if (shown.TryGetValue("i" + channel.ServerId.ToString(CultureInfo.InvariantCulture), out var server))
                held.Add((IntelRooms.ChannelOf(channel), channel.RoomType == JabberRoomType.Intel && !channel.Left, server));

        var byChannel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var same in held.GroupBy(h => h.Channel, StringComparer.OrdinalIgnoreCase))
        {
            var read = same.Where(h => h.Intel).ToList();
            byChannel[same.Key] = string.Join(", ", (read.Count > 0 ? read : same.ToList())
                .Select(h => h.Server).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase));
        }
        return new IntelChannelLabels(byChannel);
    }

    /// <summary>A Jabber login's server as the person calls it: its name, else the host it connects to, else its address's domain.</summary>
    public static string NameOf(JabberAccount account) =>
        account.Name.Trim() is { Length: > 0 } name ? name : HostOf(account) is { Length: > 0 } host ? host : account.Address.Trim();

    /// <summary>An IRC network as the person calls it: its name, else its host.</summary>
    public static string NameOf(IrcServer server) =>
        server.Name.Trim() is { Length: > 0 } name ? name : server.Host.Trim();

    /// <summary>Where a Jabber login connects: the host set, else (DNS deciding) its address's domain.</summary>
    private static string HostOf(JabberAccount account) =>
        account.Host.Trim() is { Length: > 0 } host ? host
        : account.Address.Trim() is var address && address.IndexOf('@') is var at and >= 0 ? address[(at + 1)..] : "";

    /// <summary>Whether a stored channel name is a Jabber room's or an IRC channel's, rather than a game chat channel's.</summary>
    public static bool IsRoom(string channel) =>
        channel.StartsWith(IntelRooms.JabberPrefix, StringComparison.Ordinal) || channel.StartsWith(IntelRooms.IrcPrefix, StringComparison.Ordinal);

    /// <summary>The server a room's channel goes under, as shown; null for a game chat channel.</summary>
    public string? ServerOf(string channel) =>
        _servers.TryGetValue(channel, out var server) && server.Length > 0 ? server
        : channel.StartsWith(IntelRooms.JabberPrefix, StringComparison.Ordinal) ? JabberWord
        : channel.StartsWith(IntelRooms.IrcPrefix, StringComparison.Ordinal) ? IrcWord
        : null;

    /// <summary>The room or channel itself, without the prefix its reports are stored under.</summary>
    public static string RoomName(string channel) =>
        channel.StartsWith(IntelRooms.JabberPrefix, StringComparison.Ordinal) ? channel[IntelRooms.JabberPrefix.Length..]
        : channel.StartsWith(IntelRooms.IrcPrefix, StringComparison.Ordinal) ? channel[IntelRooms.IrcPrefix.Length..]
        : channel;

    /// <summary>The channel as shown: "EVE Chat - &lt;channel&gt;" or "&lt;server&gt; - &lt;room&gt;". Blank stays blank.</summary>
    public string Label(string? channel) =>
        string.IsNullOrEmpty(channel) ? ""
        : ServerOf(channel) is { } server ? string.Format(CultureInfo.CurrentCulture, IntelText.SourceServerChannel, server, RoomName(channel))
        : string.Format(CultureInfo.CurrentCulture, IntelText.SourceEveChat, channel);

    /// <summary>The game's chat channels first, then each server's rooms, servers by name; within each, by the room's name.</summary>
    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        var (sx, sy) = (ServerOf(x), ServerOf(y));
        if ((sx is null) != (sy is null)) return sx is null ? -1 : 1;
        var c = sx is null ? 0 : StringComparer.CurrentCultureIgnoreCase.Compare(sx, sy);
        if (c != 0) return c;
        c = StringComparer.CurrentCultureIgnoreCase.Compare(RoomName(x), RoomName(y));
        return c != 0 ? c : StringComparer.Ordinal.Compare(x, y);
    }
}
