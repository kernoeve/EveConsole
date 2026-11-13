using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Irc;

public sealed partial class IrcService
{
    private async Task ProcessInboxAsync(IrcSession session, IrcClient client, ChannelReader<IrcMessage> inbox, CancellationToken ct)
    {
        await foreach (var m in inbox.ReadAllAsync(ct))
        {
            try { await ProcessAsync(session, client, m, ct); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // One line that could not be taken must not stop every line after it.
                errors.Log(nameof(IrcService), "incoming " + m.Command, e);
            }
        }
    }

    internal async Task ProcessAsync(IrcSession session, IrcClient client, IrcMessage m, CancellationToken ct)
    {
        // ⚠️ History replays joins, parts and the like as well as messages (event-playback, asked for so they come as
        // events rather than as lines from a "HistServ"). Taken as live, an old JOIN of this person would say a channel
        // was joined: only what was said is read from history.
        // ⚠️ History batches only: a netsplit's batch of QUITs is live, and CHATHISTORY TARGETS arrives in a batch of its own.
        var history = IsHistory(m);
        if (history && m.Command is not ("PRIVMSG" or "NOTICE" or "TAGMSG")) return;

        var changed = false;
        switch (m.Command)
        {
            case "JOIN": changed = await OnJoinAsync(session, client, m, ct); break;
            case "PART": changed = OnLeave(session, client, m[0], m.Nick, null); break;
            case "KICK": changed = OnLeave(session, client, m[0], m[1], (m.Nick, m[2])); break;
            case "QUIT": changed = OnQuit(session, m.Nick); break;
            case "NICK": changed = OnNick(session, m.Nick, m[0]); break;
            case "353": OnNames(session, m); break;
            case "366": changed = OnNamesEnd(session, client, m[1], ct); break;
            case "MODE": changed = OnMode(session, client, m); break;
            case "TOPIC": changed = OnTopic(session, m[0], m.Last, m.Nick); break;
            case "332": changed = OnTopic(session, m[1], m.Last, null); break;
            case "331": changed = OnTopic(session, m[1], "", null); break;
            case "333": session.Locked(s => { if (s.ChannelMap.GetValueOrDefault(s.Fold(m[1])) is { } c) c.TopicBy = m[2].Split('!')[0]; }); break;
            case "AWAY": changed = OnAway(session, m.Nick, m.Params.Count > 0 ? m.Last : null); break;
            case "ACCOUNT": changed = ForEachMember(session, m.Nick, x => x.Account = m[0] == "*" ? "" : m[0]); break;
            case "CHGHOST": changed = ForEachMember(session, m.Nick, x => x.UserHost = m[0] + "@" + m[1]); break;
            case "730": changed = OnMonitor(session, m.Last, true); break;
            case "731": changed = OnMonitor(session, m.Last, false); break;
            case "INVITE" when session.Fold(m[0]) == session.Fold(client.Nick) && m[1].Length > 0:
                session.Locked(s => { if (s.InviteMap.Count < MostInvites) s.InviteMap[s.Fold(m[1])] = new IrcInvite(m[1], m.Nick); });
                changed = true;
                break;
            case "367":   // me #channel mask [setter [time]]
                session.Locked(s =>
                {
                    if (s.BansComing.GetValueOrDefault(s.Fold(m[1])) is { Bans.Count: < 1000 } coming)
                        coming.Bans.Add(new IrcBan(m[2], m.Params.Count > 3 ? m[3].Split('!')[0] : ""));
                });
                break;
            case "368":
                var bans = session.Locked(s => s.BansComing.Remove(s.Fold(m[1]), out var coming) ? coming : default);
                bans.Done?.TrySetResult(bans.Bans);
                break;
            case "321": session.Locked(s => s.ListComing = []); break;
            case "322":
                session.Locked(s =>
                {
                    if (s.ListComing is { Count: < 20000 } list)
                        list.Add(new IrcListEntry(m[1], int.TryParse(m[2], out var n) ? n : 0, StripFormatting(m.Params.Count > 3 ? m.Last : "")));
                });
                break;
            case "323":
                var (listed, done) = session.Locked(s => ((IReadOnlyList<IrcListEntry>)(s.ListComing ?? []), s.ListDone));
                session.Locked(s => { s.ListComing = null; s.ListDone = null; });
                done?.TrySetResult(listed);
                break;
            case "PRIVMSG" or "NOTICE": await OnSaidAsync(session, client, m, ct); break;
            case "TAGMSG": await OnTagsAsync(session, client, m, history, ct); break;
            case "CHATHISTORY" when m[0] == "TARGETS": await OnHistoryTargetAsync(session, client, m, ct); break;
            case "NICK" or "001": break;

            // Refusals: shown where they were asked for.
            case "403" or "405" or "471" or "473" or "474" or "475" or "476" or "477" or "489":
                changed = OnJoinRefused(session, m[1], m.Last);
                break;
            case "401" or "404" or "406" or "441" or "442" or "443" or "481" or "482":
                session.LastRefusal = (m.Params.Count > 2 ? m[1] + ": " : "") + m.Last;
                changed = true;
                break;
            case "FAIL":
                session.LastRefusal = m.Last;
                changed = true;
                break;
        }
        if (changed) SessionChanged?.Invoke(session);
    }

    // ── Who is where ──────────────────────────────────────────────────────────

    private async Task<bool> OnJoinAsync(IrcSession session, IrcClient client, IrcMessage m, CancellationToken ct)
    {
        var channel = m[0];
        var key = session.Fold(channel);
        var nick = m.Nick;

        if (client.IsFromMe(m))
        {
            // Left before this came: out again, and not kept.
            if (session.Locked(s => s.Leaving.Remove(key)))
            {
                await client.PartAsync(channel, "", ct);
                return false;
            }

            // Joined: kept, if it was not already (a server can put someone in a channel, and an invitation accepted is one).
            var state = session.Locked(s => s.ChannelMap.GetValueOrDefault(key));
            if (state is null)
            {
                var saved = await KeepChannelAsync(session.Server.Id, channel, JabberRoomType.General, ct);
                state = session.Locked(s => s.ChannelMap.TryGetValue(key, out var have) ? have : s.ChannelMap[key] = new IrcChannelState(saved));
            }
            session.Locked(s =>
            {
                state.Joined = true;
                state.ServerName = channel;
                state.Failure = "";
                state.Members.Clear();
                s.InviteMap.Remove(key);
            });
            return true;
        }

        return session.Locked(s =>
        {
            if (s.ChannelMap.GetValueOrDefault(key) is not { } c) return false;
            var member = new IrcMember(nick) { UserHost = m.UserHost };
            if (client.Has("extended-join") && m.Params.Count >= 2) member.Account = m[1] == "*" ? "" : m[1];
            c.Members[s.Fold(nick)] = member;
            if (s.PeopleMap.GetValueOrDefault(s.Fold(nick)) is { } person) person.Online = true;
            return true;
        });
    }

    private bool OnLeave(IrcSession session, IrcClient client, string channel, string nick, (string By, string Reason)? kicked)
    {
        var key = session.Fold(channel);
        var me = session.Fold(nick) == session.Fold(client.Nick);
        return session.Locked(s =>
        {
            if (s.ChannelMap.GetValueOrDefault(key) is not { } c) return false;
            if (me)
            {
                c.Joined = false;
                c.Members.Clear();
                if (kicked is { } k) c.Failure = k.Reason.Length > 0 ? $"Kicked by {k.By}: {k.Reason}" : $"Kicked by {k.By}";
            }
            else c.Members.Remove(s.Fold(nick));
            return true;
        });
    }

    private static bool OnQuit(IrcSession session, string nick) => session.Locked(s =>
    {
        var key = s.Fold(nick);
        foreach (var c in s.ChannelMap.Values) c.Members.Remove(key);
        if (s.PeopleMap.GetValueOrDefault(key) is { } p) p.Online = false;
        return true;
    });

    private static bool OnNick(IrcSession session, string from, string to) => session.Locked(s =>
    {
        var oldKey = s.Fold(from);
        var newKey = s.Fold(to);
        if (oldKey == s.Fold(s.Nick) || s.Nick.Length == 0) { if (oldKey == s.Fold(s.Nick)) s.Nick = to; }
        foreach (var c in s.ChannelMap.Values)
            if (c.Members.Remove(oldKey, out var member)) { member.Nick = to; c.Members[newKey] = member; }
        return true;
    });

    private static void OnNames(IrcSession session, IrcMessage m)
    {
        // 353 me = #channel :@alice +bob carol!user@host
        var channel = m[2];
        session.Locked(s =>
        {
            if (s.ChannelMap.GetValueOrDefault(s.Fold(channel)) is not { } c) return;
            c.NamesComing ??= new Dictionary<string, IrcMember>(StringComparer.Ordinal);
            var support = s.Support;
            foreach (var entry in m.Last.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var at = 0;
                var ranks = new StringBuilder();
                while (at < entry.Length && support.RankOfSymbol(entry[at]) is var rank and >= 0)
                {
                    ranks.Append(support.Ranks[rank].Mode);
                    at++;
                }
                var rest = entry[at..];
                var bang = rest.IndexOf('!');
                var nick = bang < 0 ? rest : rest[..bang];
                if (nick.Length == 0 || c.NamesComing.Count >= 50000) continue;
                c.NamesComing[s.Fold(nick)] = new IrcMember(nick) { Ranks = SortRanks(support, ranks.ToString()), UserHost = bang < 0 ? "" : rest[(bang + 1)..] };
            }
        });
    }

    private bool OnNamesEnd(IrcSession session, IrcClient client, string channel, CancellationToken ct)
    {
        var askHistory = false;
        var changed = session.Locked(s =>
        {
            if (s.ChannelMap.GetValueOrDefault(s.Fold(channel)) is not { } c || c.NamesComing is null) return false;
            c.Members.Clear();
            foreach (var (k, v) in c.NamesComing) c.Members[k] = v;
            c.NamesComing = null;
            foreach (var key in c.Members.Keys)
                if (s.PeopleMap.GetValueOrDefault(key) is { } p) p.Online = true;
            if (c.Joined && !c.HistoryAsked) { c.HistoryAsked = true; askHistory = true; }
            return true;
        });

        // Joined and listed: now what was said while away, where the server keeps it.
        if (askHistory && client.HasChatHistory) _ = CatchUpAsync(session, client, channel, ct);
        return changed;
    }

    private static string SortRanks(IrcSupport support, string modes) =>
        new([.. modes.Distinct().Where(m => support.RankOfMode(m) >= 0).OrderBy(support.RankOfMode)]);

    private static bool OnMode(IrcSession session, IrcClient client, IrcMessage m)
    {
        var target = m[0];
        var support = client.Support;
        if (!support.IsChannel(target)) return false;

        return session.Locked(s =>
        {
            if (s.ChannelMap.GetValueOrDefault(s.Fold(target)) is not { } c) return false;
            var adding = true;
            var next = 2;
            foreach (var ch in m[1])
            {
                if (ch is '+' or '-') { adding = ch == '+'; continue; }
                var value = support.TakesValue(ch, adding) && next < m.Params.Count ? m.Params[next++] : "";
                if (support.RankOfMode(ch) < 0 || value.Length == 0) continue;
                if (c.Members.GetValueOrDefault(s.Fold(value)) is not { } member) continue;
                member.Ranks = adding ? SortRanks(support, member.Ranks + ch) : member.Ranks.Replace(ch.ToString(), "");
            }
            return true;
        });
    }

    private static bool OnTopic(IrcSession session, string channel, string topic, string? by) => session.Locked(s =>
    {
        if (s.ChannelMap.GetValueOrDefault(s.Fold(channel)) is not { } c) return false;
        c.Topic = StripFormatting(topic);
        if (by is not null) c.TopicBy = by;
        return true;
    });

    private static bool OnAway(IrcSession session, string nick, string? message) => ForEachMember(session, nick, member =>
    {
        member.Away = message is not null;
        member.AwayMessage = message ?? "";
    }, person =>
    {
        person.Away = message is not null;
        person.AwayMessage = message ?? "";
    });

    private static bool ForEachMember(IrcSession session, string nick, Action<IrcMember> act, Action<IrcPerson>? person = null) => session.Locked(s =>
    {
        var key = s.Fold(nick);
        foreach (var c in s.ChannelMap.Values)
            if (c.Members.GetValueOrDefault(key) is { } member) act(member);
        if (person is not null && s.PeopleMap.GetValueOrDefault(key) is { } p) person(p);
        return true;
    });

    private static bool OnMonitor(IrcSession session, string list, bool online) => session.Locked(s =>
    {
        foreach (var entry in list.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var nick = entry.Split('!')[0];
            var key = s.Fold(nick);
            if (!s.PeopleMap.TryGetValue(key, out var p)) s.PeopleMap[key] = p = new IrcPerson(nick);
            p.Online = online;
            if (!online) p.Away = false;
        }
        return true;
    });

    private static bool OnJoinRefused(IrcSession session, string channel, string why) => session.Locked(s =>
    {
        if (s.ChannelMap.GetValueOrDefault(s.Fold(channel)) is not { } c) { s.LastRefusal = $"{channel}: {why}"; return true; }
        c.Joined = false;
        c.Failure = why;
        return true;
    });

    // ── What is said ──────────────────────────────────────────────────────────

    private async Task OnSaidAsync(IrcSession session, IrcClient client, IrcMessage m, CancellationToken ct)
    {
        var target = m[0];
        var text = m.Last;
        var kind = m.Command == "NOTICE" ? IrcMessageKind.Notice : IrcMessageKind.Said;

        if (IrcClient.IsCtcp(text, out var ctcp, out var rest))
        {
            if (ctcp != "ACTION") return;   // other CTCP is answered by the client, and is no message
            text = rest;
            kind = IrcMessageKind.Action;
        }

        // A notice from the server itself (no nickname of a person), or before logging in: not a conversation. Nor the
        // server's history service retelling events, where a server sends those as lines.
        if (m.Nick.Length == 0 || m.Prefix.IndexOf('!') < 0 && m.Nick.Contains('.')) return;
        if (m.Nick.Equals("HistServ", StringComparison.OrdinalIgnoreCase)) return;
        if (target == "*") return;

        var fromMe = client.IsFromMe(m);

        // ⚠️ A password this person said to a service (NickServ IDENTIFY), come back by echo: not kept as a message.
        if (fromMe && IsService(target) && CarriesSecret(text)) return;

        var isChannel = client.Support.IsChannel(target.TrimStart(client.Support.Ranks.Select(r => r.Symbol).ToArray()));
        if (isChannel) target = target.TrimStart(client.Support.Ranks.Select(r => r.Symbol).ToArray());

        // A direct message is filed under the other person: the sender, or, for one of this person's own, who it went to.
        var peer = isChannel ? "" : fromMe ? target : m.Nick;
        var conversation = isChannel ? session.Fold(target) : session.Fold(peer);
        var history = IsHistory(m);

        var channel = isChannel ? session.Locked(s => s.ChannelMap.GetValueOrDefault(conversation)?.Channel) : null;
        if (isChannel && channel is null) return;   // a channel not kept: nothing to file it under

        var body = StripFormatting(text);
        var at = IrcClient.TimeOf(m);
        var mention = isChannel && !fromMe && NamesMe(session, body);
        var alerts = !fromMe && (isChannel
            ? ChannelAlerts(session.Server, session.Nick, channel, body)
            : kind != IrcMessageKind.Notice || !IsService(m.Nick));

        var stored = new IrcStoredMessage
        {
            ServerId = session.Server.Id,
            Conversation = conversation,
            IsChannel = isChannel,
            Sender = m.Nick,
            Body = body.Length > 4000 ? body[..4000] : body,
            Kind = kind,
            SentAt = Stamp(at),
            IsOutgoing = fromMe,
            IsMention = mention,
            IsAlert = alerts,
            MessageId = m.Tag("msgid") ?? "",
            ReplyId = m.Tag("+draft/reply") ?? m.Tag("+reply") ?? "",
            PeerName = peer,
            DedupeKey = DedupeKey(conversation, m.Nick, body, m.Tag("msgid"), m.Tag("time") is not null ? at : null),
        };

        var saved = await StoreAsync(stored, history, ct);
        if (saved is null) return;

        if (!isChannel && peer.Length > 0)
            session.Locked(s => { if (s.PeopleMap.Count < MostPeople) s.PeopleMap.TryAdd(s.Fold(peer), new IrcPerson(peer) { Online = !history }); });

        // Someone who said something has stopped typing.
        if (!fromMe) Typed(session.Server.Id, conversation, m.Nick, false);

        if (channel?.RoomType == JabberRoomType.Intel) await FeedIntelAsync(channel, saved, ct);
    }

    /// <summary>NickServ, ChanServ and the like: their notices answer what was asked, and are no alarm.</summary>
    private static bool IsService(string nick) => nick.EndsWith("Serv", StringComparison.OrdinalIgnoreCase);

    private bool NamesMe(IrcSession session, string body) =>
        session.Nick.Length > 0 && Jabber.JabberService.ContainsWord(body, session.Nick)
        || session.Server.Nickname.Length > 0 && Jabber.JabberService.ContainsWord(body, session.Server.Nickname);

    /// <summary>Whether a channel message calls for attention, by that channel's setting.</summary>
    public static bool ChannelAlerts(IrcServer server, string nick, IrcChannel? channel, string body)
    {
        var mode = channel?.AlertMode ?? JabberRoomAlert.Mentions;
        if (mode == JabberRoomAlert.All) return true;
        if (mode == JabberRoomAlert.None) return false;
        if (nick.Length > 0 && Jabber.JabberService.ContainsWord(body, nick)) return true;
        if (server.Nickname.Length > 0 && Jabber.JabberService.ContainsWord(body, server.Nickname)) return true;
        return server.AlertWords.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                     .Any(word => Jabber.JabberService.ContainsWord(body, word));
    }

    /// <summary>UTC, sortable, to the millisecond: two lines in the same second still sort.</summary>
    public static string Stamp(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    /// <summary>
    /// What makes two arrivals the same message: the server's id where it gives one, else the words and the server's
    /// time. Without either a line cannot be told from a repeat, and it is kept.
    /// </summary>
    public static string DedupeKey(string conversation, string sender, string body, string? msgid, DateTimeOffset? serverTime)
    {
        if (msgid is { Length: > 0 }) return $"m:{conversation}:{msgid}";
        if (serverTime is not { } at) return "n:" + Guid.NewGuid().ToString("N");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{conversation}|{sender}|{body}|{Stamp(at)}"));
        return "h:" + Convert.ToHexString(hash)[..32];
    }

    /// <summary>
    /// Colour, bold, italic and the like (mIRC codes): taken out, since the text is shown as text. A message that is
    /// all codes keeps its words.
    /// </summary>
    public static string StripFormatting(string text)
    {
        if (text.IndexOfAny(['\x02', '\x03', '\x04', '\x0F', '\x11', '\x16', '\x1D', '\x1E', '\x1F']) < 0) return text;

        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '\x02' or '\x0F' or '\x11' or '\x16' or '\x1D' or '\x1E' or '\x1F':
                    continue;
                case '\x03':
                    // Colour: up to two digits, and optionally a comma and up to two more.
                    var n = 0;
                    while (n < 2 && i + 1 < text.Length && char.IsAsciiDigit(text[i + 1])) { i++; n++; }
                    if (n > 0 && i + 2 < text.Length && text[i + 1] == ',' && char.IsAsciiDigit(text[i + 2]))
                    {
                        i++;
                        n = 0;
                        while (n < 2 && i + 1 < text.Length && char.IsAsciiDigit(text[i + 1])) { i++; n++; }
                    }
                    continue;
                case '\x04':
                    // Hex colour: six hex digits, optionally a comma and six more.
                    var h = 0;
                    while (h < 6 && i + 1 < text.Length && char.IsAsciiHexDigit(text[i + 1])) { i++; h++; }
                    if (h == 6 && i + 7 < text.Length && text[i + 1] == ',') i += 7;
                    continue;
                default:
                    sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Typing and reactions. ⚠️ A reaction adds and an unreact takes away, each the same however often it comes: history
    /// replays them on every reconnect, and a reaction that flipped on each one came and went with every catch-up.
    /// </summary>
    private async Task OnTagsAsync(IrcSession session, IrcClient client, IrcMessage m, bool history, CancellationToken ct)
    {
        var target = m[0];
        if (target.Length == 0) return;
        var fromMe = client.IsFromMe(m);
        var isChannel = client.Support.IsChannel(target);
        var conversation = isChannel ? session.Fold(target) : session.Fold(fromMe ? target : m.Nick);

        // Typing is only news now: never from history.
        if (!history && !fromMe && m.Tag("+typing") is { } typing)
            Typed(session.Server.Id, conversation, m.Nick, typing == "active");

        if ((m.Tag("+draft/reply") ?? m.Tag("+reply")) is not { Length: > 0 } msgid) return;
        var add = (m.Tag("+draft/react") ?? m.Tag("+react")) is { Length: > 0 and <= 64 } added ? added : null;
        var remove = m.Tag("+draft/unreact") is { Length: > 0 and <= 64 } taken ? taken : null;
        if (add is null && remove is null) return;

        // This person's own come back from the server too; saved under their name as when sent.
        if (await SetReactionAsync(session.Server.Id, conversation, msgid, m.Nick, add ?? remove!, add is not null, ct))
            Reacted(session.Server.Id, conversation);
    }

    private void Typed(int server, string conversation, string nick, bool typing)
    {
        TypingChanged?.Invoke(server, conversation, nick, typing);
        Tell("typing", server, conversation: conversation, nick: nick, on: typing);
    }

    private void Reacted(int server, string conversation)
    {
        ReactionsChanged?.Invoke(server, conversation);
        Tell("react", server, conversation: conversation);
    }

    private static bool IsHistory(IrcMessage m) => IrcClient.BatchOf(m) is "chathistory" or "draft/chathistory" or "znc.in/playback";

    private const int MostInvites = 100;
    private const int MostPeople = 5000;

    // ── Catching up ───────────────────────────────────────────────────────────

    private async Task CatchUpAsync(IrcSession session, IrcClient client, string target, CancellationToken ct)
    {
        try
        {
            var conversation = session.Fold(target);
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var last = await db.IrcMessages.AsNoTracking()
                               .Where(x => x.ServerId == session.Server.Id && x.Conversation == conversation)
                               .OrderByDescending(x => x.Id).Select(x => x.SentAt).FirstOrDefaultAsync(ct);

            if (last is { Length: > 0 } && DateTimeOffset.TryParse(last, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var after))
                await client.HistoryAfterAsync(target, after.AddSeconds(-1), 500, ct);
            else
                await client.HistoryLatestAsync(target, 50, ct);
        }
        catch (Exception e) when (e is IrcException or OperationCanceledException or ObjectDisposedException or InvalidOperationException) { }
    }

    private async Task OnHistoryTargetAsync(IrcSession session, IrcClient client, IrcMessage m, CancellationToken ct)
    {
        // CHATHISTORY TARGETS <target> <timestamp>: a conversation with news since then. Channels catch up on joining.
        var target = m[1];
        if (target.Length == 0 || client.Support.IsChannel(target)) return;
        await CatchUpAsync(session, client, target, ct);
    }

    /// <summary>Hands an intel channel's message to the intel parser. A failure here is the parser's loss, not the message's: that is saved.</summary>
    private async Task FeedIntelAsync(IrcChannel channel, IrcStoredMessage row, CancellationToken ct)
    {
        var lines = Monitoring.IntelRooms.ToChatLines(channel, row);
        if (lines.Count == 0) return;
        try
        {
            // ⚠️ Through ChatMessageWrites: the intel watermark reads by id, so ids must commit in order.
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            db.ChatMessages.AddRange(lines);
            await EveConsole.Data.ChatMessageWrites.SaveAsync(db, ct);
            IntelLinesSaved?.Invoke();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            errors.Log(nameof(IrcService), "saving a channel's intel", e);
        }
    }

    // ── Saving ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Saves a message unless it is one already saved. A conversation's history the first time it is caught up is
    /// saved as read: it is the past, not news.
    /// </summary>
    internal async Task<IrcStoredMessage?> StoreAsync(IrcStoredMessage m, bool fromHistory, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.IrcMessages.AnyAsync(x => x.ServerId == m.ServerId && x.DedupeKey == m.DedupeKey, ct)) return null;

        if (m.IsOutgoing) m.IsRead = true;
        else if (fromHistory && !await db.IrcMessages.AnyAsync(x => x.ServerId == m.ServerId && x.Conversation == m.Conversation, ct))
        {
            m.IsRead = true;
            m.IsAlert = false;
        }

        db.IrcMessages.Add(m);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { return null; }   // the same message, saved by another of the owner's machines just now

        var sound = m is { IsAlert: true, IsRead: false } && !fromHistory;
        MessageStored?.Invoke(m);
        if (!m.IsRead) UnreadChanged?.Invoke();
        if (sound) AlertArrived?.Invoke(m);
        TellMessageSaved(m.Id, sound);
        return m;
    }

    /// <summary>Puts an emoji in someone's reactions to a message, or takes it out. True when that changed anything.</summary>
    internal async Task<bool> SetReactionAsync(int serverId, string conversation, string target, string sender, string emoji, bool add, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.IrcReactions.FirstOrDefaultAsync(r => r.ServerId == serverId && r.Conversation == conversation && r.Target == target && r.Sender == sender, ct);
        var now = DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        if (row is null)
        {
            if (!add) return false;
            db.IrcReactions.Add(new IrcReaction { ServerId = serverId, Conversation = conversation, Target = target, Sender = sender, Emojis = emoji, UpdatedAt = now });
        }
        else
        {
            var set = row.Emojis.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (add == set.Contains(emoji)) return false;
            if (add) { if (set.Count >= 20) return false; set.Add(emoji); }
            else set.Remove(emoji);
            if (set.Count == 0) db.IrcReactions.Remove(row);
            else { row.Emojis = string.Join(' ', set); row.UpdatedAt = now; }
        }
        try { await db.SaveChangesAsync(ct); return true; }
        catch (DbUpdateException) { return false; }
    }
}
