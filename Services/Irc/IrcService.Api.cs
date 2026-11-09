using System.Globalization;
using System.Text.Json;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Irc;

public sealed partial class IrcService
{
    private static IrcClient Connected(IrcSession session) =>
        session is { State: IrcState.Connected, Client: { } client } ? client : throw new InvalidOperationException("Not connected.");

    // ── Saying things ─────────────────────────────────────────────────────────

    /// <summary>
    /// Sends what was typed to a channel or person. A line starting with a slash is a command, as in any IRC client:
    /// /me, /nick, /join, /part, /topic, /msg, /notice, /away, /back, /quote.
    /// </summary>
    /// <param name="target">The channel, or the person's nickname, as the server should see it.</param>
    /// <returns>What to tell the person, if anything (an unknown command); blank when it went.</returns>
    public async Task<string> SendAsync(IrcSession session, string target, string text, string replyTo = "", CancellationToken ct = default)
    {
        if (!ActsHere) return await AskWorkerAsync(session.Server.Id, "send", new CommandArgs(target, text, ReplyTo: replyTo), ct);
        var client = Connected(session);
        text = text.TrimEnd();
        if (text.Length == 0) return "";

        if (text.StartsWith('/') && !text.StartsWith("//"))
        {
            var space = text.IndexOf(' ');
            var verb = (space < 0 ? text[1..] : text[1..space]).ToLowerInvariant();
            var rest = space < 0 ? "" : text[(space + 1)..].Trim();
            switch (verb)
            {
                case "me":
                    await SayAsync(session, client, target, rest, action: true, replyTo, ct);
                    return "";
                case "nick" when rest.Length > 0:
                    await client.NickAsync(rest.Split(' ')[0], ct);
                    return "";
                case "join" when rest.Length > 0:
                    var parts = rest.Split(' ', 2);
                    await JoinChannelAsync(session, parts[0], parts.Length > 1 ? parts[1] : "", null, ct);
                    return "";
                case "part" or "leave":
                    await LeaveChannelAsync(session, rest.Length > 0 && session.IsChannelName(rest.Split(' ')[0]) ? rest.Split(' ')[0] : target, ct);
                    return "";
                case "topic" when session.IsChannelName(target):
                    await client.TopicAsync(target, rest, ct);
                    return "";
                case "msg" or "query" when rest.Contains(' '):
                    var to = rest[..rest.IndexOf(' ')];
                    await SayAsync(session, client, to, rest[(rest.IndexOf(' ') + 1)..], action: false, "", ct);
                    return "";
                case "notice" when rest.Contains(' '):
                    await client.NoticeAsync(rest[..rest.IndexOf(' ')], rest[(rest.IndexOf(' ') + 1)..], ct);
                    return "";
                case "away":
                    await SetAwayAsync(session, rest.Length > 0 ? rest : "Away", ct);
                    return "";
                case "back":
                    await SetAwayAsync(session, "", ct);
                    return "";
                case "quote" or "raw" when IrcMessage.Parse(rest) is { } raw:
                    await client.SendRawAsync(raw, ct);
                    return "";
                default:
                    return string.Format(CultureInfo.CurrentCulture, Localization.IrcText.UnknownCommand, "/" + verb);
            }
        }

        // "//text" sends a line that starts with a slash.
        if (text.StartsWith("//")) text = text[1..];
        await SayAsync(session, client, target, text, action: false, replyTo, ct);
        return "";
    }

    private async Task SayAsync(IrcSession session, IrcClient client, string target, string text, bool action, string replyTo, CancellationToken ct)
    {
        var sent = await client.SayAsync(target, text, action, replyTo, ct);

        // With echo-message the server's copy is what is saved, with its id and time; without, it is saved now.
        if (client.HasEcho) return;
        var isChannel = client.Support.IsChannel(target);
        var conversation = session.Fold(target);
        foreach (var piece in sent)
            await StoreAsync(new IrcStoredMessage
            {
                ServerId = session.Server.Id, Conversation = conversation, IsChannel = isChannel,
                Sender = client.Nick, Body = piece, Kind = action ? IrcMessageKind.Action : IrcMessageKind.Said,
                SentAt = Stamp(DateTimeOffset.UtcNow), IsOutgoing = true, PeerName = isChannel ? "" : target,
                ReplyId = replyTo, DedupeKey = "n:" + Guid.NewGuid().ToString("N"),
            }, fromHistory: false, ct);
    }

    public Task SendTypingAsync(IrcSession session, string target, bool typing, CancellationToken ct = default)
    {
        // A signal alone, as nothing depends on it and a row each key press would be silly.
        if (!ActsHere)
        {
            if (session is { State: IrcState.Connected, HasTags: true }) Tell("typeout", session.Server.Id, conversation: target, on: typing);
            return Task.CompletedTask;
        }
        return session is { State: IrcState.Connected, Client: { } client } && client.HasTags
            ? client.TypingAsync(target, typing ? "active" : "done", ct)
            : Task.CompletedTask;
    }

    /// <summary>Adds this person's reaction to a message, or takes it back if it is theirs already (+draft/unreact).</summary>
    public async Task ReactAsync(IrcSession session, string target, string conversation, string messageId, string emoji, CancellationToken ct = default)
    {
        if (!ActsHere) { await AskWorkerAsync(session.Server.Id, "react", new CommandArgs(target, emoji, ReplyTo: messageId, Conversation: conversation), ct); return; }
        var client = Connected(session);
        if (!client.HasTags || messageId.Length == 0) return;
        var mine = (await ReactionsAsync(session.Server.Id, conversation, [messageId], client.Nick, ct))
                   .GetValueOrDefault(messageId)?.Any(c => c.Mine && c.Emoji == emoji) == true;
        await client.ReactAsync(target, messageId, emoji, takeBack: mine, ct);
        if (await SetReactionAsync(session.Server.Id, conversation, messageId, client.Nick, emoji, add: !mine, ct))
            Reacted(session.Server.Id, conversation);
    }

    public async Task SetAwayAsync(IrcSession session, string message, CancellationToken ct = default)
    {
        session.Server.AwayMessage = message;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            await db.IrcServers.Where(s => s.Id == session.Server.Id).ExecuteUpdateAsync(u => u.SetProperty(s => s.AwayMessage, message), ct);

        if (!ActsHere)
        {
            await AskWorkerAsync(session.Server.Id, "away", new CommandArgs(Text: message), ct);
            return;
        }

        if (session is { State: IrcState.Connected, Client: { } client })
        {
            await client.AwayAsync(message, ct);
            session.IsAway = message.Length > 0;
        }
        SessionChanged?.Invoke(session);
    }

    public Task ChangeNickAsync(IrcSession session, string nick, CancellationToken ct = default) =>
        !ActsHere ? AskWorkerAsync(session.Server.Id, "nick", new CommandArgs(Nick: nick.Trim()), ct) : Connected(session).NickAsync(nick.Trim(), ct);

    // ── Channels ──────────────────────────────────────────────────────────────

    private async Task<IrcChannel> KeepChannelAsync(int serverId, string name, int roomType, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var folded = IrcSupport.Fold(name, "rfc1459");
        var all = await db.IrcChannels.Where(c => c.ServerId == serverId).ToListAsync(ct);
        var row = all.FirstOrDefault(c => IrcSupport.Fold(c.Name, "rfc1459") == folded);
        if (row is null)
        {
            row = new IrcChannel { ServerId = serverId, Name = name, RoomType = roomType };
            db.IrcChannels.Add(row);
        }
        else
        {
            row.Left = false;
            row.AutoJoin = true;
        }
        await db.SaveChangesAsync(ct);
        if (row.RoomType == JabberRoomType.Intel) RoomTypesChanged();
        return row;
    }

    /// <summary>Joins a channel and keeps it, with its key if it has one.</summary>
    /// <param name="roomType">What it is for, or null to leave a kept channel's as it was.</param>
    public async Task<IrcChannel> JoinChannelAsync(IrcSession session, string name, string key, int? roomType, CancellationToken ct = default)
    {
        name = name.Trim();
        if (!session.IsChannelName(name)) name = "#" + name.TrimStart('#');

        var row = await KeepChannelAsync(session.Server.Id, name, roomType ?? JabberRoomType.General, ct);
        if (roomType is { } type && row.RoomType != type) await SetRoomTypeAsync(session, row.Name, type, ct);
        if (key.Length > 0) await Task.Run(() => AppConfig.SetServicePassword(ChannelKeyReference(row.Id), key), ct);

        session.Locked(s =>
        {
            var folded = s.Fold(name);
            if (s.ChannelMap.TryGetValue(folded, out var have)) { have.Channel = row; have.Failure = ""; }
            else s.ChannelMap[folded] = new IrcChannelState(row);
            s.InviteMap.Remove(folded);
        });

        // ⚠️ The channel and its key are already saved, so the key is never part of what is asked.
        if (!ActsHere) await AskWorkerAsync(session.Server.Id, "join", new CommandArgs(name), ct);
        else if (session is { State: IrcState.Connected, Client: { } client })
        {
            var saved = key.Length > 0 ? key : await Task.Run(() => AppConfig.GetServicePassword(ChannelKeyReference(row.Id)), ct) ?? "";
            await client.JoinAsync(name, saved, ct);
        }
        SessionChanged?.Invoke(session);
        return row;
    }

    /// <summary>Leaves a channel, and stops keeping it. What was said there stays saved until it ages out.</summary>
    public async Task LeaveChannelAsync(IrcSession session, string name, CancellationToken ct = default)
    {
        if (!ActsHere) { await AskWorkerAsync(session.Server.Id, "leave", new CommandArgs(name), ct); return; }
        var folded = session.Fold(name);
        var state = session.Locked(s => s.ChannelMap.GetValueOrDefault(folded));
        if (state is null) return;

        if (session is { State: IrcState.Connected, Client: { } client } && state.Joined)
            await client.PartAsync(state.ServerName, "", ct);
        else session.Locked(s => { if (s.Leaving.Count < 100) s.Leaving.Add(folded); });

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            await db.IrcChannels.Where(c => c.Id == state.Channel.Id).ExecuteUpdateAsync(u => u.SetProperty(c => c.Left, true), ct);

        session.Locked(s => s.ChannelMap.Remove(folded));
        if (state.Channel.RoomType == JabberRoomType.Intel) RoomTypesChanged();
        SessionChanged?.Invoke(session);
        UnreadChanged?.Invoke();
        Tell("unread");
    }

    /// <summary>Joins again, after a refusal or being put out.</summary>
    public async Task RejoinAsync(IrcSession session, string name, CancellationToken ct = default)
    {
        if (!ActsHere) { await AskWorkerAsync(session.Server.Id, "rejoin", new CommandArgs(name), ct); return; }
        var state = session.Locked(s => s.ChannelMap.GetValueOrDefault(s.Fold(name)));
        if (state is null || session is not { State: IrcState.Connected, Client: { } client }) return;
        var key = await Task.Run(() => AppConfig.GetServicePassword(ChannelKeyReference(state.Channel.Id)), ct) ?? "";
        await client.JoinAsync(state.ServerName, key, ct);
    }

    public async Task AnswerInviteAsync(IrcSession session, string channel, bool accept, CancellationToken ct = default)
    {
        if (!ActsHere)
        {
            await AskWorkerAsync(session.Server.Id, "invite", new CommandArgs(channel, On: accept), ct);
            session.Locked(s => s.InviteMap.Remove(s.Fold(channel)));
            SessionChanged?.Invoke(session);
            return;
        }
        if (accept) await JoinChannelAsync(session, channel, "", null, ct);
        session.Locked(s => s.InviteMap.Remove(s.Fold(channel)));
        SessionChanged?.Invoke(session);
    }

    public async Task SetAlertModeAsync(IrcSession session, string channel, int mode, CancellationToken ct = default)
    {
        var state = session.Locked(s => s.ChannelMap.GetValueOrDefault(s.Fold(channel)));
        if (state is null) return;
        state.Channel.AlertMode = mode;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.IrcChannels.Where(c => c.Id == state.Channel.Id).ExecuteUpdateAsync(u => u.SetProperty(c => c.AlertMode, mode), ct);
        if (!ActsHere) await PostToWorkerAsync(session.Server.Id, "channel", new CommandArgs(channel), ct);
        SessionChanged?.Invoke(session);
    }

    public async Task SetRoomTypeAsync(IrcSession session, string channel, int type, CancellationToken ct = default)
    {
        var state = session.Locked(s => s.ChannelMap.GetValueOrDefault(s.Fold(channel)));
        if (state is null) return;
        state.Channel.RoomType = type;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.IrcChannels.Where(c => c.Id == state.Channel.Id).ExecuteUpdateAsync(u => u.SetProperty(c => c.RoomType, type), ct);
        if (ActsHere) RoomTypesChanged();
        else
        {
            IntelRoomsChanged?.Invoke();
            await PostToWorkerAsync(session.Server.Id, "channel", new CommandArgs(channel), ct);
        }
        SessionChanged?.Invoke(session);
    }

    /// <summary>Every channel the server lists. A big network lists thousands, so this can take a while.</summary>
    public async Task<IReadOnlyList<IrcListEntry>> ListChannelsAsync(IrcSession session, CancellationToken ct = default)
    {
        if (!ActsHere)
            return JsonSerializer.Deserialize<List<IrcListEntry>>(await AskWorkerAsync(session.Server.Id, "list", new CommandArgs(), ct)) ?? [];
        var client = Connected(session);
        var done = new TaskCompletionSource<IReadOnlyList<IrcListEntry>>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Locked(s => { s.ListDone = done; s.ListComing = []; });
        await client.ListAsync(ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        await using (timeout.Token.Register(() => done.TrySetResult(session.Locked(s => (IReadOnlyList<IrcListEntry>)(s.ListComing ?? [])))))
            return await done.Task;
    }

    // ── A channel operator's ──────────────────────────────────────────────────

    /// <summary>The ranks this connection holds in a channel, highest first, as mode letters.</summary>
    public static string MyRanks(IrcSession session, string channel) => session.Locked(s =>
        s.ChannelMap.GetValueOrDefault(s.Fold(channel))?.Members.GetValueOrDefault(s.Fold(s.Nick))?.Ranks ?? "");

    /// <summary>Whether a rank letter is at or above half-operator, which is what kicking and setting modes take.</summary>
    public static bool IsOperator(IrcSession session, string ranks)
    {
        var support = session.Support;
        var halfop = support.RankOfMode('h');
        var op = support.RankOfMode('o');
        var bar = halfop >= 0 ? halfop : op;
        return bar >= 0 && ranks.Any(r => support.RankOfMode(r) is var rank and >= 0 && rank <= bar);
    }

    public Task SetTopicAsync(IrcSession session, string channel, string topic, CancellationToken ct = default) =>
        !ActsHere ? AskWorkerAsync(session.Server.Id, "topic", new CommandArgs(channel, topic), ct) : Connected(session).TopicAsync(channel, topic, ct);

    public Task KickAsync(IrcSession session, string channel, string nick, string reason, CancellationToken ct = default) =>
        !ActsHere ? AskWorkerAsync(session.Server.Id, "kick", new CommandArgs(channel, reason, nick), ct) : Connected(session).KickAsync(channel, nick, reason, ct);

    /// <summary>Bans by host where the server has told it, else by nickname, and puts them out.</summary>
    public async Task BanAsync(IrcSession session, string channel, IrcMember member, string reason, CancellationToken ct = default)
    {
        if (!ActsHere) { await AskWorkerAsync(session.Server.Id, "ban", new CommandArgs(channel, reason, member.Nick, member.UserHost), ct); return; }
        var client = Connected(session);
        var at = member.UserHost.IndexOf('@');
        var mask = at >= 0 ? "*!*@" + member.UserHost[(at + 1)..] : member.Nick + "!*@*";
        await client.ModeAsync(channel, "+b", mask);
        await client.KickAsync(channel, member.Nick, reason, ct);
    }

    /// <summary>Gives or takes a rank (o, h, v...).</summary>
    public Task SetRankAsync(IrcSession session, string channel, string nick, char mode, bool give) =>
        !ActsHere ? AskWorkerAsync(session.Server.Id, "rank", new CommandArgs(channel, Nick: nick, Mode: mode, On: give))
                  : Connected(session).ModeAsync(channel, (give ? "+" : "-") + mode, nick);

    /// <summary>Who a channel bans. Empty when it will not say (not an operator), as the server's refusal says why.</summary>
    public async Task<IReadOnlyList<IrcBan>> BansAsync(IrcSession session, string channel, CancellationToken ct = default)
    {
        if (!ActsHere)
            return JsonSerializer.Deserialize<List<IrcBan>>(await AskWorkerAsync(session.Server.Id, "bans", new CommandArgs(channel), ct)) ?? [];
        var client = Connected(session);
        var done = new TaskCompletionSource<IReadOnlyList<IrcBan>>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Locked(s => s.BansComing[s.Fold(channel)] = ([], done));
        await client.ModeAsync(channel, "+b");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await using (timeout.Token.Register(() =>
        {
            session.Locked(s => s.BansComing.Remove(s.Fold(channel)));
            done.TrySetResult([]);
        }))
            return await done.Task;
    }

    public Task LiftBanAsync(IrcSession session, string channel, string mask, CancellationToken ct = default) =>
        !ActsHere ? AskWorkerAsync(session.Server.Id, "liftban", new CommandArgs(channel, mask), ct) : Connected(session).ModeAsync(channel, "-b", mask);

    public Task InviteAsync(IrcSession session, string nick, string channel, CancellationToken ct = default) =>
        !ActsHere ? AskWorkerAsync(session.Server.Id, "inviteto", new CommandArgs(channel, Nick: nick), ct) : Connected(session).InviteAsync(nick, channel, ct);

    // ── Reading what was saved ────────────────────────────────────────────────

    public async Task<List<IrcChannel>> SavedChannelsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.IrcChannels.AsNoTracking().Where(c => !c.Left).ToListAsync(ct);
    }

    /// <summary>A page of a conversation, oldest first, ending before a message (or at the newest).</summary>
    public async Task<List<IrcStoredMessage>> LoadConversationAsync(int serverId, string conversation, long beforeId = long.MaxValue, int limit = 200, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var page = await db.IrcMessages.AsNoTracking()
                           .Where(m => m.ServerId == serverId && m.Conversation == conversation && m.Id < beforeId)
                           .OrderByDescending(m => m.Id).Take(limit).ToListAsync(ct);
        page.Reverse();
        return page;
    }

    public async Task<List<IrcUnread>> UnreadAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.IrcMessages.AsNoTracking().Where(m => !m.IsRead)
                       .GroupBy(m => new { m.ServerId, m.Conversation })
                       .Select(g => new IrcUnread(g.Key.ServerId, g.Key.Conversation, g.Count(), g.Count(m => m.IsAlert), g.Count(m => m.IsMention)))
                       .ToListAsync(ct);
    }

    public async Task MarkReadAsync(int serverId, string conversation, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var changed = await db.IrcMessages.Where(m => m.ServerId == serverId && m.Conversation == conversation && !m.IsRead)
                              .ExecuteUpdateAsync(u => u.SetProperty(m => m.IsRead, true), ct);
        if (changed > 0) { UnreadChanged?.Invoke(); Tell("unread"); }
    }

    /// <summary>Every direct conversation, with its last line and unread counts.</summary>
    public async Task<List<IrcDirectSummary>> DirectSummariesAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var lasts = await db.IrcMessages.AsNoTracking().Where(m => !m.IsChannel)
                            .GroupBy(m => new { m.ServerId, m.Conversation })
                            .Select(g => new { g.Key.ServerId, g.Key.Conversation, LastId = g.Max(m => m.Id), Unread = g.Count(m => !m.IsRead), Alerts = g.Count(m => !m.IsRead && m.IsAlert) })
                            .ToListAsync(ct);
        var ids = lasts.Select(l => l.LastId).ToList();
        var rows = await db.IrcMessages.AsNoTracking().Where(m => ids.Contains(m.Id)).ToDictionaryAsync(m => m.Id, ct);
        return [.. lasts.Where(l => rows.ContainsKey(l.LastId)).Select(l =>
        {
            var row = rows[l.LastId];
            return new IrcDirectSummary(l.ServerId, l.Conversation, row.PeerName.Length > 0 ? row.PeerName : l.Conversation, row.Body, row.SentAt, l.Unread, l.Alerts);
        })];
    }

    /// <summary>The reactions to some of a conversation's messages, by message id.</summary>
    public async Task<Dictionary<string, List<Jabber.JabberReactionCount>>> ReactionsAsync(int serverId, string conversation, IReadOnlyCollection<string> targets, string myNick, CancellationToken ct = default)
    {
        var wanted = targets.Where(t => t.Length > 0).ToList();
        if (wanted.Count == 0) return [];
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.IrcReactions.AsNoTracking()
                           .Where(r => r.ServerId == serverId && r.Conversation == conversation && wanted.Contains(r.Target))
                           .OrderBy(r => r.UpdatedAt).ToListAsync(ct);
        return rows.GroupBy(r => r.Target).ToDictionary(g => g.Key, g => g
            .SelectMany(r => r.Emojis.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(e => (Emoji: e, r.Sender)))
            .GroupBy(x => x.Emoji)
            .Select(e => new Jabber.JabberReactionCount(e.Key, e.Count(), [.. e.Select(x => x.Sender)],
                e.Any(x => string.Equals(x.Sender, myNick, StringComparison.OrdinalIgnoreCase))))
            .ToList());
    }
}
