using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Irc;

/// <summary>What the server says about someone, asked for by name (WHOIS). What it did not say stays blank.</summary>
public sealed class IrcWhois
{
    public string Nick { get; set; } = "";

    /// <summary>False when the server says nobody of that name is online.</summary>
    public bool Found { get; set; } = true;

    public string User { get; set; } = "";
    public string Host { get; set; } = "";
    public string RealName { get; set; } = "";
    public string Server { get; set; } = "";
    public string ServerInfo { get; set; } = "";

    /// <summary>The account they are logged in to. Blank when the server did not say one.</summary>
    public string Account { get; set; } = "";

    /// <summary>Why they are away. Blank when they are not.</summary>
    public string Away { get; set; } = "";

    public bool IsOperator { get; set; }
    public bool IsSecure { get; set; }
    public bool IsBot { get; set; }

    /// <summary>How long since they last said anything, in seconds. -1 when the server did not say.</summary>
    public long IdleSeconds { get; set; } = -1;

    /// <summary>When they connected, as Unix seconds. 0 when the server did not say.</summary>
    public long SignedOnUnix { get; set; }

    /// <summary>The channels the server shows them in, each with its rank mark in front ("@#ops").</summary>
    public List<string> Channels { get; set; } = [];

    /// <summary>Anything else the server said about them, as it said it: which network services know the name, and so on.</summary>
    public List<string> Notes { get; set; } = [];
}

public sealed partial class IrcService
{
    // ── Who someone is (WHOIS) ────────────────────────────────────────────────

    private const int MostWhoisAtOnce = 20;

    /// <summary>
    /// Asks the server who someone is. Null when it did not answer in time. ⚠️ The answer comes as many lines, each by a
    /// number, and ends with 318; one asked twice at once shares the one question.
    /// </summary>
    public async Task<IrcWhois?> WhoisAsync(IrcSession session, string nick, CancellationToken ct = default)
    {
        nick = nick.Trim();
        if (nick.Length == 0 || nick.Contains(' ') || nick.Contains(',') || session.IsChannelName(nick)) return null;
        if (!ActsHere)
        {
            var json = await AskWorkerAsync(session.Server.Id, "whois", new CommandArgs(Nick: nick), ct);
            return json.Length > 0 ? JsonSerializer.Deserialize<IrcWhois>(json) : null;
        }

        var client = Connected(session);
        var key = session.Fold(nick);
        var (done, ask) = session.Locked(s =>
        {
            if (s.WhoisComing.TryGetValue(key, out var have)) return (have.Done, false);
            if (s.WhoisComing.Count >= MostWhoisAtOnce) return ((TaskCompletionSource<IrcWhois?>?)null, false);
            var fresh = new TaskCompletionSource<IrcWhois?>(TaskCreationOptions.RunContinuationsAsynchronously);
            s.WhoisComing[key] = (new IrcWhois { Nick = nick }, fresh, false);
            return (fresh, true);
        });
        if (done is null) return null;
        if (ask) await client.WhoisAsync(nick, ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await using (timeout.Token.Register(() =>
        {
            // What came, if anything did: a server that never sends the end still said something.
            var said = session.Locked(s => s.WhoisComing.Remove(key, out var coming) && coming.Heard ? coming.Info : null);
            done.TrySetResult(said);
        }))
            return await done.Task;
    }

    /// <summary>One line of an answer to WHOIS: true when it was one, as nothing else needs it then.</summary>
    private static bool OnWhoisLine(IrcSession session, IrcMessage m)
    {
        if (m.Params.Count < 2) return false;
        var key = session.Fold(m[1]);

        if (m.Command == "318")
        {
            var ended = session.Locked(s => s.WhoisComing.Remove(key, out var coming) ? coming : default);
            ended.Done?.TrySetResult(ended.Info);
            return ended.Done is not null;
        }

        // Nobody of that name: said at once, rather than waiting for the end that may or may not follow.
        if (m.Command is "401" or "402")
        {
            var gone = session.Locked(s => s.WhoisComing.Remove(key, out var coming) ? coming : default);
            if (gone.Done is null) return false;
            gone.Info.Found = false;
            gone.Done.TrySetResult(gone.Info);
            return true;
        }

        return session.Locked(s =>
        {
            if (!s.WhoisComing.TryGetValue(key, out var coming)) return false;
            var w = coming.Info;
            var rest = m.Params.Count > 2 ? m.Params.Skip(2).ToList() : [];
            switch (m.Command)
            {
                case "311" when rest.Count >= 2:   // user host * :real name
                    w.Nick = m[1]; w.User = rest[0]; w.Host = rest[1]; w.RealName = StripFormatting(m.Last);
                    break;
                case "312" when rest.Count >= 1:   // server :about it
                    w.Server = rest[0]; w.ServerInfo = rest.Count > 1 ? m.Last : "";
                    break;
                case "313": w.IsOperator = true; break;
                case "317" when rest.Count >= 1:   // idle signon :seconds idle, signon time
                    if (long.TryParse(rest[0], NumberStyles.None, CultureInfo.InvariantCulture, out var idle)) w.IdleSeconds = idle;
                    if (rest.Count > 2 && long.TryParse(rest[1], NumberStyles.None, CultureInfo.InvariantCulture, out var on)) w.SignedOnUnix = on;
                    break;
                case "319":
                    foreach (var c in m.Last.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        if (w.Channels.Count < 200) w.Channels.Add(c);
                    break;
                case "330" when rest.Count >= 1: w.Account = rest[0]; break;
                case "301": w.Away = StripFormatting(m.Last); break;
                case "671": w.IsSecure = true; break;
                case "335": w.IsBot = true; break;
                case "307" or "320" or "276" or "338" or "378" or "379":
                    if (w.Notes.Count < 20) w.Notes.Add(StripFormatting(string.Join(' ', rest)));
                    break;
                default: return false;
            }
            s.WhoisComing[key] = coming with { Heard = true };
            return true;
        });
    }

    /// <summary>What WHOIS said, on one line, for a typed /whois.</summary>
    public static string WhoisLine(IrcWhois? w, string nick)
    {
        if (w is null) return Localization.IrcText.WhoisNoAnswer;
        if (!w.Found) return string.Format(CultureInfo.CurrentCulture, Localization.IrcText.WhoisNotOnline, nick);
        var parts = WhoisRows(w).Select(r => r.Label + ": " + r.Value);
        return w.Nick + " · " + string.Join(" · ", parts);
    }

    /// <summary>What WHOIS said as rows of a label and its words, only those it said.</summary>
    public static IReadOnlyList<(string Label, string Value)> WhoisRows(IrcWhois w)
    {
        var rows = new List<(string, string)>();
        if (w.RealName.Length > 0) rows.Add((Localization.IrcText.WhoisRealName, w.RealName));
        rows.Add((Localization.IrcText.WhoisAccount, w.Account.Length > 0 ? w.Account : Localization.IrcText.WhoisNoAccount));
        if (w.Away.Length > 0) rows.Add((Localization.IrcText.WhoisAway, w.Away));
        if (w.IdleSeconds >= 0) rows.Add((Localization.IrcText.WhoisIdle, Span(TimeSpan.FromSeconds(w.IdleSeconds))));
        if (w.SignedOnUnix > 0)
            rows.Add((Localization.IrcText.WhoisSince, DateTimeOffset.FromUnixTimeSeconds(w.SignedOnUnix).ToLocalTime().ToString("g", CultureInfo.CurrentCulture)));
        if (w.Channels.Count > 0) rows.Add((Localization.IrcText.WhoisChannels, string.Join(' ', w.Channels)));
        if (w.User.Length > 0 || w.Host.Length > 0) rows.Add((Localization.IrcText.WhoisAddress, $"{w.User}@{w.Host}"));
        if (w.Server.Length > 0) rows.Add((Localization.IrcText.WhoisServer, w.ServerInfo.Length > 0 ? $"{w.Server} ({w.ServerInfo})" : w.Server));
        if (w.IsSecure) rows.Add((Localization.IrcText.WhoisSecure, Localization.IrcText.WhoisYes));
        if (w.IsOperator) rows.Add((Localization.IrcText.WhoisOperator, Localization.IrcText.WhoisYes));
        if (w.IsBot) rows.Add((Localization.IrcText.WhoisBot, Localization.IrcText.WhoisYes));
        foreach (var note in w.Notes) rows.Add((Localization.IrcText.WhoisAlso, note));
        return rows;
    }

    /// <summary>A length of time as people say it: seconds, minutes, hours and minutes, or days and hours.</summary>
    public static string Span(TimeSpan span)
    {
        var c = CultureInfo.CurrentCulture;
        if (span.TotalMinutes < 1) return string.Format(c, Localization.IrcText.SpanSeconds, (int)span.TotalSeconds);
        if (span.TotalHours < 1) return string.Format(c, Localization.IrcText.SpanMinutes, (int)span.TotalMinutes);
        if (span.TotalDays < 1) return string.Format(c, Localization.IrcText.SpanHours, (int)span.TotalHours, span.Minutes);
        return string.Format(c, Localization.IrcText.SpanDays, (int)span.TotalDays, span.Hours);
    }

    // ── Encryption the server asks for (STS) ──────────────────────────────────

    /// <summary>
    /// The encrypted port a server has asked to be reached on, where it has asked and the rule still holds. Null when it
    /// has not, or asked for a host other than the one now set. ⚠️ The host as set: someone who changes it has
    /// pointed the server somewhere else, where this rule says nothing.
    /// </summary>
    public static int? StsPortOf(IrcServer server)
    {
        if (server.StsPort is <= 0 or > 65535 || server.StsHost.Length == 0) return null;
        if (!string.Equals(server.StsHost, server.Host.Trim(), StringComparison.OrdinalIgnoreCase)) return null;
        return DateTimeOffset.TryParse(server.StsUntil, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var until) && until > DateTimeOffset.UtcNow
            ? server.StsPort : null;
    }

    /// <summary>
    /// Keeps what an encrypted connection said of encryption: for how long, from now. A duration of nothing takes the
    /// rule back. Said every time it connects, so a rule kept renews itself.
    /// </summary>
    private async Task KeepStsAsync(IrcSession session, IrcClient client, string host, int port, CancellationToken ct)
    {
        if (!client.IsEncrypted || client.StsDuration is not { } duration) return;
        var server = session.Server;
        string stsHost = "", until = "";
        var stsPort = 0;
        if (duration > TimeSpan.Zero)
        {
            stsHost = host;
            stsPort = port;
            until = (DateTimeOffset.UtcNow + duration).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        }
        else if (server.StsHost.Length == 0) return;

        // ⚠️ Renewed every connection, but only written when it moves by a day or more: a write is a "servers" signal to every client.
        if (stsHost == server.StsHost && stsPort == server.StsPort && until.Length > 0
            && DateTimeOffset.TryParse(server.StsUntil, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var had)
            && (DateTimeOffset.Parse(until, CultureInfo.InvariantCulture) - had).Duration() < TimeSpan.FromDays(1)) return;

        server.StsHost = stsHost; server.StsPort = stsPort; server.StsUntil = until;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var id = server.Id;
        await db.IrcServers.Where(s => s.Id == id).ExecuteUpdateAsync(u => u
            .SetProperty(s => s.StsHost, stsHost).SetProperty(s => s.StsPort, stsPort).SetProperty(s => s.StsUntil, until), ct);
        Tell("servers");
        SessionChanged?.Invoke(session);
    }

    // ── Pictures (draft/metadata-2 "avatar") ──────────────────────────────────

    /// <summary>The longest picture link taken: a link is a few hundred characters at most.</summary>
    public const int LongestPictureLink = 400;

    /// <summary>Whether a picture link is one this client would show: http or https, and not too long. Blank is fine.</summary>
    public static bool IsPictureLink(string url) =>
        url.Length == 0
        || url.Length <= LongestPictureLink && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && !url.Contains(' ');

    /// <summary>The link to an EVE character's portrait on the game's own image server.</summary>
    public static string EvePortraitLink(long characterId) =>
        $"https://images.evetech.net/characters/{characterId.ToString(CultureInfo.InvariantCulture)}/portrait?size=128";

    /// <summary>The EVE characters added to this app, by name, to offer their portraits.</summary>
    public Task<List<(long Id, string Name)>> EveCharactersAsync(CancellationToken ct = default) => OwnPicture.EveCharactersAsync(dbFactory, ct);

    /// <summary>
    /// Sets this person's picture on a server, as a web link, kept so it is set again on every connection. Blank takes
    /// it away. Where the server keeps no pictures it is kept all the same, for a server that later does.
    /// </summary>
    public async Task SetAvatarUrlAsync(IrcSession session, string url, CancellationToken ct = default)
    {
        url = url.Trim();
        if (!IsPictureLink(url)) throw new ArgumentException(Localization.IrcText.PictureLinkBad);

        session.Server.AvatarUrl = url;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var id = session.Server.Id;
            await db.IrcServers.Where(s => s.Id == id).ExecuteUpdateAsync(u => u.SetProperty(s => s.AvatarUrl, url), ct);
        }

        if (!ActsHere) { await AskWorkerAsync(session.Server.Id, "avatar", new CommandArgs(Text: url), ct); return; }
        if (session is { State: IrcState.Connected, Client: { } client }) await SendAvatarAsync(session, client, url, ct);
        else if (url.Length == 0) NotePicture(session, ["me"], "");
        else NotePicture(session, ["me"], url);
    }

    private async Task SendAvatarAsync(IrcSession session, IrcClient client, string url, CancellationToken ct)
    {
        // Shown here at once, whatever the server makes of it: it is this person's own choice.
        NotePicture(session, ["me"], url);
        if (client.HasMetadata) await client.SetAvatarAsync(url, ct);
    }

    /// <summary>After logging in: pictures asked to be told of, this person's own set, and those of people written with.</summary>
    private async Task PicturesAfterLoginAsync(IrcSession session, IrcClient client, IReadOnlyList<string> peers, CancellationToken ct)
    {
        if (!client.HasMetadata) return;
        await client.SubscribeMetadataAsync(ct);
        if (session.Server.AvatarUrl.Length > 0) await SendAvatarAsync(session, client, session.Server.AvatarUrl, ct);
        else await client.AskAvatarAsync(client.Nick, ct);

        // ⚠️ A few only: each is a line at the sending pace, and what someone types waits behind them.
        foreach (var peer in peers.Where(p => p.Length > 0).Take(MostPeerPictures)) await client.AskAvatarAsync(peer, ct);
    }

    private const int MostPeerPictures = 10;

    /// <summary>How often a picture is asked for, one person at a time. ⚠️ Slow on purpose: see AskPicturesSlowlyAsync.</summary>
    internal static TimeSpan PictureAskEvery { get; set; } = TimeSpan.FromSeconds(2);

    private const int MostPicturesAsked = 5000;
    private const int MostPicturesWaiting = 300;

    /// <summary>Someone whose picture to ask for, once per connection, unless the server has told it already.</summary>
    private static void AskPictureLater(IrcSession session, IrcClient client, string nick)
    {
        if (!client.HasMetadata || nick.Length == 0) return;
        session.Locked(s =>
        {
            if (s.PicturesToAsk.Count < MostPicturesWaiting && s.PicturesAsked.Count < MostPicturesAsked && s.PicturesAsked.Add(s.Fold(nick)))
                s.PicturesToAsk.Enqueue(nick);
        });
    }

    /// <summary>
    /// Asks for the pictures of people who joined, one every <see cref="PictureAskEvery"/>, for as long as the connection
    /// lasts. ⚠️ Each ask is a line at the sending pace, which what someone types shares: a busy channel full of people
    /// coming and going must not make their words wait.
    /// </summary>
    private static async Task AskPicturesSlowlyAsync(IrcSession session, IrcClient client, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(PictureAskEvery, ct); }
            catch (OperationCanceledException) { return; }

            var next = session.Locked(s => s.PicturesToAsk.TryDequeue(out var nick) ? nick : null);
            if (next is null) continue;
            try { await client.AskAvatarAsync(next, ct); }
            catch (Exception e) when (e is IrcException or OperationCanceledException or ObjectDisposedException or IOException) { return; }
        }
    }

    /// <summary>
    /// A value of someone's that the server passed on: METADATA target key visibility [:value], or the answer to asking
    /// (761 me target key visibility :value, 766 me target key :not set). Only pictures are taken, and only people's.
    /// </summary>
    private void OnMetadata(IrcSession session, IrcClient client, string target, string key, string? value)
    {
        if (!key.Equals("avatar", StringComparison.OrdinalIgnoreCase) || target.Length == 0 || session.IsChannelName(target)) return;
        var me = target == "*" || session.Fold(target) == session.Fold(client.Nick);
        // Told already: not asked for again.
        if (!me) session.Locked(s => { if (s.PicturesAsked.Count < MostPicturesAsked) s.PicturesAsked.Add(s.Fold(target)); });
        // ⚠️ Both keys the screens go by: Group Chat by "nick:" and the nickname in lower case, Messages by the folded name.
        string[] persons = me ? ["me"] : ["nick:" + target.ToLowerInvariant(), session.Fold(target)];
        NotePicture(session, persons, value?.Trim() ?? "");
    }

    /// <summary>Tells the pictures what someone's link is now: fetched once per link, and blank forgets theirs.</summary>
    private void NotePicture(IrcSession session, IReadOnlyList<string> persons, string url)
    {
        if (avatars is null) return;
        if (url.Length == 0) { _ = avatars.NoteAsync(AvatarServiceName, session.Server.Id, persons, "", () => Task.FromResult<byte[]?>(null)); return; }
        if (!IsPictureLink(url)) return;

        // The link's own hash says whether it changed: a new link is fetched, the same one is not.
        var hash = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(url)));
        _ = avatars.NoteAsync(AvatarServiceName, session.Server.Id, persons, hash, () => WebPictures.FetchAsync(url, CancellationToken.None));
    }
}
