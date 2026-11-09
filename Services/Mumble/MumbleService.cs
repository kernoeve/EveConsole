using System.Globalization;
using System.Text;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Mumble;

public enum MumbleState { Offline, Connecting, Connected, Reconnecting, Failed }

public enum MumbleProblem { None, Certificate, Password, Username, Removed, Other }

/// <summary>What pressing a server's keys does.</summary>
/// <summary>
/// How someone is talking. Direct is to this client alone, and only ever heard from someone else.
/// </summary>
public enum MumbleTalk { None, Channel, Shout, Direct }

/// <summary>A line of a server's text chat, in memory for as long as the app runs.</summary>
/// <summary>How one saved conversation stands.</summary>
public sealed record MumbleDirectSummary(int ServerId, string PeerKey, string PeerName, string LastText, string LastAt, int Unread);

/// <summary>A line of channel chat, which lasts the session. Private messages go to the Messages window.</summary>
public sealed record MumbleChatLine(DateTimeOffset When, string Sender, string Text, bool IsOutgoing)
{
    /// <summary>The channel it was said in.</summary>
    public string Channel { get; init; } = "";

    /// <summary>It went to that channel and the ones under it.</summary>
    public bool IsTree { get; init; }
}

/// <summary>Somebody talking right now, for the overlay.</summary>
public sealed record MumbleTalker(int ServerId, string Server, uint Session, string Name, bool IsSelf, MumbleTalk Mode);

/// <summary>What is selected in a server's channel tree, for the selection key: a person by session, or a channel by id.</summary>
public sealed record MumblePick(bool IsPerson, uint Id);

/// <summary>One server's connection, kept up for as long as someone wants it.</summary>
public sealed class MumbleSession(MumbleServer server)
{
    public MumbleServer Server { get; set; } = server;
    public MumbleState  State  { get; internal set; }
    public MumbleProblem Problem { get; internal set; }
    public string Detail { get; internal set; } = "";

    public string CertificateFingerprint { get; internal set; } = "";
    public string CertificateSubject     { get; internal set; } = "";
    public DateTimeOffset? NextAttempt   { get; internal set; }

    public MumbleClient? Client { get; internal set; }

    internal CancellationTokenSource? Run { get; set; }
    internal Task? Loop { get; set; }
    internal Dictionary<string, double> Volumes { get; set; } = [];

    /// <summary>Who is not heard, and whose words are dropped, on this client alone. ⚠️ Read on the connection's thread.</summary>
    internal System.Collections.Concurrent.ConcurrentDictionary<string, bool> LocalMuted { get; set; } = [];
    internal System.Collections.Concurrent.ConcurrentDictionary<string, bool> Ignored { get; set; } = [];
    internal bool MicFailed { get; set; }
    internal Task? VoiceSending { get; set; }
    /// <summary>One for each channel target with a key, for as long as the server is connected.</summary>
    internal List<GlobalHotkeyService> KeyHooks { get; } = [];

    /// <summary>The voice target number each packet names while talking. Set before <see cref="Talking"/> says so.</summary>
    public int TalkSlot;

    /// <summary>What this connection has told the server each saved target holds, so that it is only told what changed.</summary>
    internal Dictionary<int, byte[]> SentTargets { get; } = [];

    private MumblePick? _pick;
    private MumbleClient? _pickedOn;

    /// <summary>
    /// What was last selected in the channel tree, on this connection. ⚠️ None on a new one: people's sessions are
    /// numbered afresh, and an old number could be someone else.
    /// </summary>
    public MumblePick? Picked => _pickedOn is { } on && ReferenceEquals(on, Client) ? _pick : null;

    internal void SetPick(MumblePick pick, MumbleClient on)
    {
        _pick = pick;
        _pickedOn = on;
    }

    /// <summary>
    /// ⚠️ One telling of the selection at a time, each worked out as it goes: two at once could reach the server in the
    /// other order, leaving it with the older one while this side believes the newer.
    /// </summary>
    internal SemaphoreSlim PickSending { get; } = new(1, 1);

    /// <summary>The selection key is down. Telling the server can take a moment, and it may be let go before then.</summary>
    internal volatile bool PickHeld;

    private MumbleServer? _targetsOf;
    private List<MumbleTalkTarget> _targets = [];

    /// <summary>This server's targets. Kept, as what reads them runs on every change the server reports.</summary>
    internal List<MumbleTalkTarget> Targets
    {
        get
        {
            lock (SentTargets)
            {
                if (!ReferenceEquals(_targetsOf, Server))
                {
                    _targets = MumbleTargets.Read(Server);
                    _targetsOf = Server;
                }
                return _targets;
            }
        }
    }
    internal MumbleEncoder? Encoder { get; set; }

    /// <summary>What is being said right now, if anything, and what its last frame must be.</summary>
    internal volatile int Talking;

    /// <summary>Talking because the microphone heard a voice, not because a key is held.</summary>
    internal volatile bool TalkingByVoice;

    public MumbleTalk TalkingNow => (MumbleTalk)Talking;

    private readonly List<MumbleChatLine> _chat = [];

    public IReadOnlyList<MumbleChatLine> Chat { get { lock (_chat) return [.. _chat]; } }

    internal void AddChat(MumbleChatLine line)
    {
        lock (_chat)
        {
            _chat.Add(line);
            if (_chat.Count > MumbleService.MaxChatLines) _chat.RemoveAt(0);
        }
    }

    public double VolumeOf(MumbleUser user) => Volumes.GetValueOrDefault(MumbleService.UserKey(user), 1.0);
    public bool IsLocalMuted(MumbleUser user) => LocalMuted.ContainsKey(MumbleService.UserKey(user));
    public bool IsIgnored(MumbleUser user) => Ignored.ContainsKey(MumbleService.UserKey(user));
}

/// <summary>
/// The Mumble servers: keeping each connected as asked, routing the microphone to whichever server's key is
/// held, and everyone being heard to the speakers.
///
/// <para>⚠️ Desktop only, for the same reason as Jabber: a worker logging in as the owner would fight
/// their real client for the same seat, and has no one to listen.</para>
/// </summary>
public sealed class MumbleService(IDbContextFactory<AppDbContext> dbFactory, AppErrorLogger errors, AvatarService? avatars = null)
{
    public const int MaxChatLines = 5000;
    public const string InputDevicePref = "mumble.input_device";
    public const string OutputDevicePref = "mumble.output_device";
    public const string OutputVolumePref = "mumble.output_volume";
    public const string MicGainPref = "mumble.mic_gain";
    public const string AutoLevelPref = "mumble.auto_level";
    public const string EchoCancelPref = "mumble.echo_cancel";
    public const string NoiseReducePref = "mumble.noise_reduce";
    public const string VoiceLevelPref = "mumble.voice_level";
    public const string TalkTonePref = "mumble.talk_tone";

    /// <summary>A tone on pressing and letting go of a talk key.</summary>
    public bool TalkTone { get; set; }

    /// <summary>Where voice activation starts out: over a quiet room, under a voice at a normal distance.</summary>
    public const double DefaultVoiceLevelDb = -40;

    /// <summary>What is kept from just before a voice is noticed, so the start of the first word goes too.</summary>
    private const int PreRollFrames = 5;

    // Plain talk needs no saved target: the server sends it to the channel and everything linked to it, which is
    // what the standard client's Push to Talk does, and Shout is that. Whisper has to name the channel to stop there.
    private const int PlainTalk = 0;
    private const int ChannelOnlyTarget = 1;

    /// <summary>How long the microphone stays open after the last key lets go, so a quick re-press loses nothing.</summary>
    private static readonly TimeSpan MicLinger = TimeSpan.FromSeconds(5);

    private static readonly int[] BackoffSeconds = [5, 10, 30, 60, 120, 300];
    private static readonly TimeSpan StableAfter = TimeSpan.FromMinutes(2);

    private readonly Dictionary<int, MumbleSession> _sessions = [];
    private readonly object _gate = new();
    private readonly List<short> _micBuffer = new(MumbleEncoder.FrameSamples * 2);
    private readonly Channel<VoiceWork> _frames = Channel.CreateBounded<VoiceWork>(new BoundedChannelOptions(50)
        { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    /// <summary>
    /// Key presses, in the order they happened. ⚠️ Never handled on the keyboard hook's own thread: Windows gives it a
    /// few hundred milliseconds, opening a microphone can take longer, and a hook that runs late misses the release
    /// or is removed without a word, leaving someone transmitting.
    /// </summary>
    private readonly Channel<Action> _keyEvents = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });
    private Task? _keyWorker;

    /// <summary>A frame for everyone talking, or the last one for someone who just stopped.</summary>
    private readonly record struct VoiceWork(short[]? Frame, MumbleSession? EndOf, int EndSlot);

    /// <summary>What the protocol lets a message be, past which no real person's text goes.</summary>
    private const int MostChatChars = 20_000;

    private CancellationTokenSource? _micClose;
    private Task? _sender;

    public MumbleMixer Mixer { get; } = new();
    public MumbleAudioDevices? Devices { get; private set; }

    private string _inputDevice = "", _outputDevice = "";
    private double _micGain = 1.0;

    /// <summary>The microphone by name, blank for the system's. Takes effect from the next time it opens.</summary>
    public string InputDevice
    {
        get => _inputDevice;
        set
        {
            _inputDevice = value;
            if (Devices is not { } d) return;
            d.InputDevice = value;
            d.StopInput();
            UpdateOpenMic();
        }
    }

    /// <summary>The speakers by name, blank for the system's. Switches at once if anything is connected.</summary>
    public string OutputDevice
    {
        get => _outputDevice;
        set
        {
            _outputDevice = value;
            if (Devices is not { } d) return;
            d.OutputDevice = value;
            d.StopOutput();
            if (Sessions.Any(s => s.State == MumbleState.Connected)) d.StartOutput();
        }
    }

    public double OutputVolume { get => Mixer.Volume; set => Mixer.Volume = value; }

    public double MicrophoneGain
    {
        get => _micGain;
        set { _micGain = value; if (Devices is { } d) d.MicrophoneGain = value; }
    }

    /// <summary>Levels this person's voice before it is sent, as the standard client does.</summary>
    public bool AutoLevel { get; set; } = true;

    /// <summary>
    /// Takes what the speakers played back out of the microphone. For speakers: a headset has nothing to cancel. ⚠️ Keeps the
    /// microphone open while connected and not muted, as the standard client does, with nothing sent until someone talks:
    /// opened only for a press, it lands at a different place against the speakers each time, and has learned nothing yet.
    /// </summary>
    public bool EchoCancel
    {
        get => _cleanup.CancelEcho;
        set
        {
            lock (_micBuffer)
            {
                _cleanup.CancelEcho = value;
                HookCleanup();
                _cleanup.StartTalk();
            }

            if (value && MumbleEchoCanceller.WhyNot is { } why) errors.Log(nameof(MumbleService), "start echo cancelling", why);
            UpdateOpenMic();
        }
    }

    public bool NoiseReduce
    {
        get => _cleanup.ReduceNoise;
        set
        {
            lock (_micBuffer)
            {
                _cleanup.ReduceNoise = value;
                _cleanup.StartTalk();
            }
        }
    }

    /// <summary>How loud a frame must be for voice activation to count it as talking.</summary>
    public double VoiceLevelDb { get; set; } = DefaultVoiceLevelDb;

    /// <summary>How loud the microphone was just now, while it is open.</summary>
    public double MicLevelDb { get; private set; } = -96;

    private readonly MumbleVoiceCleanup _cleanup = new();
    private readonly MumbleVoiceGate _voiceGate = new();
    private readonly Queue<short[]> _preRoll = new();
    private bool _cleanupHooked;

    private void HookCleanup()
    {
        if (_cleanupHooked) return;
        _cleanupHooked = true;
        Mixer.Mixed += _cleanup.Played;
    }

    /// <summary>Whether any server wants the microphone listening all the time.</summary>
    public bool WantsOpenMic => Sessions.Any(ListensForVoice);

    private static bool ListensForVoice(MumbleSession s) =>
        s is { State: MumbleState.Connected, Server: { VoiceActivated: true, Silent: false } };

    private bool LearnsTheRoom(MumbleSession s) => _cleanup.CancelEcho && s is { State: MumbleState.Connected, Server.Silent: false };

    /// <summary>Whether the microphone stays open whether anyone is talking or not.</summary>
    private bool MicStaysOpen => Sessions.Any(s => ListensForVoice(s) || LearnsTheRoom(s));

    /// <summary>Opens the microphone for what wants it all the time, or lets it go when nothing does any more.</summary>
    private void UpdateOpenMic()
    {
        if (!WantsOpenMic)
        {
            lock (_micBuffer) _voiceGate.Close();
            foreach (var s in Sessions.Where(s => s.TalkingByVoice)) StopByVoice(s);
        }

        if (!MicStaysOpen)
        {
            ReleaseMicIfIdle();
            return;
        }

        _micClose?.Cancel();
        if (Devices is { } devices && !devices.StartInput())
            foreach (var s in Sessions.Where(s => ListensForVoice(s) || LearnsTheRoom(s)))
            {
                s.MicFailed = true;
                s.Detail = devices.LastError ?? "";
                SessionChanged?.Invoke(s);
            }
    }

    private void StopByVoice(MumbleSession session)
    {
        StopTalking(session, MumbleTalk.Shout);
        session.TalkingByVoice = false;
    }

    /// <summary>
    /// Voice activation, for one frame already cleaned up and levelled. ⚠️ Plain talk, as the standard client's voice
    /// activation is, and never over a key being held: letting go of that is what ends that.
    /// </summary>
    private void GateByVoice(short[] frame, double level)
    {
        var was = _voiceGate.IsOpen;
        var open = _voiceGate.Update(level, VoiceLevelDb);

        if (open && !was)
        {
            var started = false;
            foreach (var s in Sessions.Where(s => ListensForVoice(s) && s.Client is not null && s.Encoder is not null && s.TalkingNow == MumbleTalk.None))
            {
                s.TalkingByVoice = true;
                Volatile.Write(ref s.TalkSlot, PlainTalk);
                Interlocked.Exchange(ref s.Talking, (int)MumbleTalk.Shout);
                started = true;
            }
            if (started)
            {
                while (_preRoll.TryDequeue(out var before)) _frames.Writer.TryWrite(new VoiceWork(before, null, PlainTalk));
                TalkersChanged?.Invoke();
            }
        }
        else if (!open && was)
        {
            foreach (var s in Sessions.Where(s => s.TalkingByVoice)) StopByVoice(s);
        }

        if (!open)
        {
            _preRoll.Enqueue(frame);
            while (_preRoll.Count > PreRollFrames) _preRoll.Dequeue();
        }
    }

    private readonly MumbleVoiceLeveler _leveler = new();

    public event Action<MumbleSession>? SessionChanged;
    public event Action<MumbleSession, MumbleChatLine>? ChatReceived;

    /// <summary>The server refused something, in its words if it gave any. Blank if it did not say why.</summary>
    public event Action<MumbleSession, string>? Refused;

    /// <summary>Who is talking changed. On a timer thread.</summary>
    public event Action? TalkersChanged;

    public static string PasswordReference(int id) => $"mumble-{id}";
    public static string CertificateReference(int id) => $"mumble-cert-{id}";

    /// <summary>How many of these, in this channel, can hear it.</summary>
    public static int ListeningIn(IEnumerable<MumbleUser> users, uint channel) => users.Count(u => u.ChannelId == channel && !u.Deaf && !u.SelfDeaf);

    /// <summary>A person's lasting identity on a server: their certificate if they have one.</summary>
    /// <summary>The name pictures are filed under for Mumble: the same as the Messages tool's.</summary>
    public const string AvatarServiceName = "mumble";

    /// <summary>
    /// People's pictures, by their key, by "name:" and their name for the chat, which only has that, and as "me". One too
    /// big to come with them is asked for. Nothing is fetched twice: the store keeps what it has seen.
    /// </summary>
    private void NoteTextures(MumbleSession session, MumbleClient client)
    {
        if (avatars is null) return;
        var me = client.Session;
        foreach (var user in client.Users)
        {
            if (user.Texture is { } bytes)
            {
                if (user.TextureHash.Length == 0) user.TextureHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(bytes));
                List<string> persons = [UserKey(user), "name:" + user.Name];
                if (user.Session == me) persons.Add("me");
                _ = avatars.NoteAsync(AvatarServiceName, session.Server.Id, persons, user.TextureHash, () => Task.FromResult<byte[]?>(bytes));
            }
            else if (user.HasTextureToAsk) client.AskTexture(user);
        }
    }

    public static string UserKey(MumbleUser user) => user.Hash.Length > 0 ? user.Hash : $"name:{user.Name}";

    /// <summary>Loads the servers and connects the ones set to connect on their own.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (AppRuntime.IsHeadless) return;

        Devices = new MumbleAudioDevices(Mixer) { InputDevice = _inputDevice, OutputDevice = _outputDevice, MicrophoneGain = _micGain };
        Devices.Captured += OnCaptured;
        Devices.InputOpening += OnMicOpening;
        _sender = Task.Run(SendFramesAsync, CancellationToken.None);
        _keyWorker = Task.Run(HandleKeysAsync, CancellationToken.None);
        _chatSaver ??= Task.Run(SaveChatAsync, CancellationToken.None);
        StartTalkerWatch();

        await PurgeOldChatAsync(ct);
        await LoadServersAsync(ct);
    }

    /// <summary>Lists the saved servers, and connects the ones this PC is set to.</summary>
    public async Task LoadServersAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        foreach (var server in await db.MumbleServers.AsNoTracking().OrderBy(s => s.Id).ToListAsync(ct))
        {
            await TakeSharedAutoConnectAsync(db, server, ct);

            var session = Track(server);
            if (AutoConnectsHere(server.Id)) Connect(session);

            // ⚠️ Said even when it stays offline: what lists the servers looked before this read them.
            SessionChanged?.Invoke(session);
        }
    }

    /// <summary>
    /// Whether this PC connects to a server by itself on starting. ⚠️ This PC's own, in its config file: the login and
    /// certificate are shared and a server takes one connection per identity, so if this were shared too, starting a client
    /// on a second PC would throw the first off mid-conversation.
    /// </summary>
    public static bool AutoConnectsHere(int serverId) => UiState.GetBool(UiState.MumbleAutoConnectPrefix + serverId, false);

    public static void SetAutoConnectHere(int serverId, bool on) => UiState.Set(UiState.MumbleAutoConnectPrefix + serverId, on ? "1" : null);

    /// <summary>
    /// A tick from when this was shared goes to the first PC to see it, and leaves the database, so the next PC starts
    /// unticked rather than connecting once and throwing the first off. Claimed in one statement in case two start together.
    /// </summary>
    private static async Task TakeSharedAutoConnectAsync(AppDbContext db, MumbleServer server, CancellationToken ct)
    {
        if (!server.AutoConnect) return;

        var claimed = await db.MumbleServers.Where(s => s.Id == server.Id && s.AutoConnect)
                              .ExecuteUpdateAsync(u => u.SetProperty(s => s.AutoConnect, false), ct);

        // ⚠️ Cleared here too: this object is saved whole later on, which would put it back.
        server.AutoConnect = false;
        if (claimed > 0) SetAutoConnectHere(server.Id, true);
    }

    // ── Private messages, which outlast the app ───────────────────────────────

    private const string StoredDate = "yyyy-MM-dd'T'HH:mm:ss'Z'";
    private const int SaveAttempts = 5;

    /// <summary>Waiting to be saved, in order, so that a busy database never holds up the connection's reader.</summary>
    private readonly Channel<MumbleDirectMessage> _chatSaves = Channel.CreateUnbounded<MumbleDirectMessage>();
    private Task? _chatSaver;   // ⚠️ Not SingleReader: that kind of channel cannot say how much is waiting, which shutting down asks.

    /// <summary>A private message was saved, sent or received.</summary>
    public event Action<MumbleDirectMessage>? DirectStored;

    public event Action? DirectUnreadChanged;

    private void Record(MumbleSession session, MumbleChatLine line)
    {
        session.AddChat(line);
        ChatReceived?.Invoke(session, line);
    }

    private void RecordDirect(int serverId, MumbleUser peer, string sender, string body, bool outgoing) =>
        _chatSaves.Writer.TryWrite(new MumbleDirectMessage
        {
            ServerId = serverId, PeerKey = UserKey(peer), PeerName = peer.Name, Sender = sender, Body = body,
            SentAt = DateTimeOffset.UtcNow.UtcDateTime.ToString(StoredDate, CultureInfo.InvariantCulture),
            IsOutgoing = outgoing, IsRead = outgoing, IsAlert = !outgoing,
        });

    private async Task SaveChatAsync()
    {
        await foreach (var row in _chatSaves.Reader.ReadAllAsync())
        {
            // ⚠️ A busy database usually clears in moments, so it gets several tries. This must never end, or nothing
            // said afterwards would be kept.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await using var db = await dbFactory.CreateDbContextAsync();
                    row.Id = 0;
                    db.MumbleDirectMessages.Add(row);
                    await db.SaveChangesAsync();

                    DirectStored?.Invoke(row);
                    if (!row.IsRead) DirectUnreadChanged?.Invoke();
                    break;
                }
                catch (Exception e)
                {
                    if (attempt >= SaveAttempts)
                    {
                        errors.Log(nameof(MumbleService), "save a private message", e);
                        break;
                    }
                    await Task.Delay(TimeSpan.FromSeconds(attempt * 2));
                }
            }
        }
    }

    /// <summary>Someone on a server right now, by what a conversation with them is filed under. Null if they are not.</summary>
    public MumbleUser? PersonOf(int serverId, string peerKey) =>
        SessionOf(serverId)?.Client?.Users.FirstOrDefault(u => UserKey(u) == peerKey);

    /// <summary>Every saved conversation, with how it stands.</summary>
    public async Task<List<MumbleDirectSummary>> DirectSummariesAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var groups = await db.MumbleDirectMessages.AsNoTracking().Where(m => m.PeerKey != "")
            .GroupBy(m => new { m.ServerId, m.PeerKey })
            .Select(g => new { g.Key.ServerId, g.Key.PeerKey, Last = g.Max(m => m.Id), Unread = g.Count(m => !m.IsRead) })
            .ToListAsync(ct);

        var ids  = groups.Select(g => g.Last).ToList();
        var last = await db.MumbleDirectMessages.AsNoTracking().Where(m => ids.Contains(m.Id)).ToDictionaryAsync(m => m.Id, ct);

        return [.. groups.Select(g => new MumbleDirectSummary(g.ServerId, g.PeerKey, last[g.Last].PeerName, last[g.Last].Body, last[g.Last].SentAt, g.Unread))];
    }

    public async Task<List<MumbleDirectMessage>> LoadDirectAsync(int serverId, string peerKey, long beforeId = long.MaxValue, int limit = 200, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var page = await db.MumbleDirectMessages.AsNoTracking()
            .Where(m => m.ServerId == serverId && m.PeerKey == peerKey && m.Id < beforeId)
            .OrderByDescending(m => m.Id).Take(limit).ToListAsync(ct);
        page.Reverse();
        return page;
    }

    public async Task MarkDirectReadAsync(int serverId, string peerKey, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var changed = await db.MumbleDirectMessages.Where(m => m.ServerId == serverId && m.PeerKey == peerKey && !m.IsRead)
                              .ExecuteUpdateAsync(u => u.SetProperty(m => m.IsRead, true), ct);
        if (changed > 0) DirectUnreadChanged?.Invoke();
    }

    /// <summary>To someone by what their conversation is filed under. ⚠️ They have to be connected: a server holds nothing for later.</summary>
    public Task SendDirectAsync(int serverId, string peerKey, string message, CancellationToken ct = default)
    {
        if (SessionOf(serverId) is not { } session) throw new MumbleException(MumbleFailure.Network, "Not connected.");
        var person = PersonOf(serverId, peerKey) ?? throw new MumbleException(MumbleFailure.Network, "They are not connected.");
        return SendPrivateTextAsync(session, person, message, ct);
    }

    /// <summary>Private messages past the days Jabber's history is kept, by the same setting. Zero keeps them all.</summary>
    private async Task PurgeOldChatAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var saved = await db.AppPreferences.AsNoTracking().Where(p => p.Key == Jabber.JabberService.HistoryDaysPref).Select(p => p.Value).FirstOrDefaultAsync(ct);
            var days  = int.TryParse(saved, out var d) ? d : Jabber.JabberService.DefaultHistoryDays;
            if (days <= 0) return;

            var cutoff = DateTimeOffset.UtcNow.AddDays(-days).UtcDateTime.ToString(StoredDate, CultureInfo.InvariantCulture);
            // ⚠️ Never an unread one: age is no reason to lose what was not seen.
            await db.MumbleDirectMessages.Where(m => m.IsRead && string.Compare(m.SentAt, cutoff) < 0).ExecuteDeleteAsync(ct);
        }
        catch (Exception e) when (e is DbUpdateException or InvalidOperationException)
        {
            errors.Log(nameof(MumbleService), "purge old chat", e);
        }
    }

    public async Task StopAsync()
    {
        MumbleSession[] all;
        lock (_gate) all = [.. _sessions.Values];
        await Task.WhenAll(all.Select(DisconnectAsync));

        // A moment for the last lines said to reach the database.
        for (var waited = 0; _chatSaves.Reader.Count > 0 && waited < 20; waited++) await Task.Delay(100);

        _talkerTimer?.Dispose();
        Devices?.Dispose();
    }

    public IReadOnlyList<MumbleSession> Sessions
    {
        get { lock (_gate) return [.. _sessions.Values.OrderBy(s => s.Server.Id)]; }
    }

    public MumbleSession? SessionOf(int id)
    {
        lock (_gate) return _sessions.GetValueOrDefault(id);
    }

    private MumbleSession Track(MumbleServer server)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(server.Id, out var known)) { known.Server = server; return known; }
            return _sessions[server.Id] = new MumbleSession(server);
        }
    }

    // ── Servers ───────────────────────────────────────────────────────────────

    /// <summary>Saves a server, new or changed. A null password leaves the saved one as it is.</summary>
    public async Task<MumbleSession> SaveServerAsync(MumbleServer server, string? password, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (server.Id == 0) db.MumbleServers.Add(server);
        else db.MumbleServers.Update(server);
        await db.SaveChangesAsync(ct);

        if (password is not null)
        {
            var id = server.Id;
            await Task.Run(() => AppConfig.SetServicePassword(PasswordReference(id), password), ct);
        }

        var keysChanged = SessionOf(server.Id)?.Server is not { } before || before.TalkTargets != server.TalkTargets
                       || before.MuteKey != server.MuteKey || before.DeafenKey != server.DeafenKey
                       || before.PickKey != server.PickKey || before.PickLinks != server.PickLinks || before.PickSubchannels != server.PickSubchannels;
        var session = Track(server);

        // A key changed while held would never be seen to come up. ⚠️ Only then: this runs each time typing pauses in
        // Settings, and correcting a server's name shouldn't cut anyone off.
        if (keysChanged)
        {
            StopTalking(session);
            BindKeys(session);
            if (session.Client is { } client && session.State == MumbleState.Connected) _ = SendTargetsAsync(session, client, CancellationToken.None);
        }
        UpdateOpenMic();
        SessionChanged?.Invoke(session);
        return session;
    }

    /// <summary>Forgets a server and how loud everyone on it was. Its certificate goes too. Permanent.</summary>
    public async Task DeleteServerAsync(int id, CancellationToken ct = default)
    {
        MumbleSession? session;
        lock (_gate) _sessions.Remove(id, out session);
        if (session is not null) await DisconnectAsync(session);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.MumbleUserVolumes.Where(v => v.ServerId == id).ExecuteDeleteAsync(ct);
        await db.MumbleDirectMessages.Where(m => m.ServerId == id).ExecuteDeleteAsync(ct);
        await db.MumbleServers.Where(s => s.Id == id).ExecuteDeleteAsync(ct);

        await Task.Run(() =>
        {
            AppConfig.SetServicePassword(PasswordReference(id), null);
            AppConfig.SetServicePassword(CertificateReference(id), null);
            SetAutoConnectHere(id, false);
        }, ct);
    }

    public async Task TrustCertificateAsync(MumbleSession session, CancellationToken ct = default)
    {
        session.Server.TrustedCertificate = session.CertificateFingerprint;
        await SaveServerAsync(session.Server, null, ct);
        Connect(session);
    }

    /// <summary>
    /// This server's certificate, made the first time it is needed. ⚠️ It is how a server knows a returning
    /// user, including whether they are registered: losing it loses the registered name, so it is kept per
    /// server, protected, and never replaced while it can still be read.
    /// </summary>
    private static X509Certificate2 ClientCertificate(MumbleServer server)
    {
        var reference = CertificateReference(server.Id);
        var saved = AppConfig.GetServicePassword(reference);

        // ⚠️ Saved but not readable right now, as with a locked keychain or a changed database password, is not the same
        // as never saved. Making a new one would throw away the registered name for good, so this waits and tries again.
        if (saved is null && AppConfig.HasServicePassword(reference))
            throw new MumbleException(MumbleFailure.Network, "This server's saved identity cannot be opened. If the database password changed, put the old one back, or remove this server and add it again to start with a new identity.");

        if (saved is not null)
        {
            try { return MumbleClient.LoadIdentity(Convert.FromBase64String(saved)); }
            catch (System.Security.Cryptography.CryptographicException e)
            {
                throw new MumbleException(MumbleFailure.Network, "This server's saved identity cannot be opened right now.", e);
            }
            catch (FormatException) { /* not a certificate at all, so nothing is lost by making one */ }
        }

        var made = MumbleClient.NewClientCertificate(server.Username.Length > 0 ? server.Username : "EVE Console");
        AppConfig.SetServicePassword(reference, Convert.ToBase64String(made.Export(X509ContentType.Pkcs12)));
        return made;
    }

    // ── Connecting ────────────────────────────────────────────────────────────

    public void Connect(MumbleSession session)
    {
        if (AppRuntime.IsHeadless) return;

        // A server still being filled in.
        if (session.Server.Host.Length == 0 || session.Server.Username.Length == 0) return;

        lock (_gate)
        {
            if (session.Loop is { IsCompleted: false }) return;
            session.Run  = new CancellationTokenSource();
            session.Loop = Task.Run(() => KeepConnectedAsync(session, session.Run.Token));
        }
    }

    public async Task DisconnectAsync(MumbleSession session)
    {
        CancellationTokenSource? run;
        Task? loop;
        lock (_gate) { run = session.Run; loop = session.Loop; }

        // ⚠️ Nothing here may throw: this is what the Disconnect button and closing the app wait on, and a failure
        // would leave one stuck on and the other unable to quit.
        try
        {
            if (run is not null) await run.CancelAsync();
            if (session.Client is { } client) await client.DisposeAsync();
            if (loop is not null) await loop.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception e) when (e is not (TimeoutException or OperationCanceledException))
        {
            errors.Log(nameof(MumbleService), "disconnect", e);
        }
        catch (Exception) { /* it is told to stop, and will */ }

        SetState(session, MumbleState.Offline, MumbleProblem.None, "");
    }

    /// <summary>
    /// Connects and reconnects until cancelled. ⚠️ Stops on what trying again cannot fix: a wrong password
    /// retried for hours looks like an attack and can get an address banned.
    /// </summary>
    private async Task KeepConnectedAsync(MumbleSession session, CancellationToken ct)
    {
        var attempt = 0;

        while (!ct.IsCancellationRequested)
        {
            var started = DateTimeOffset.UtcNow;
            SetState(session, attempt == 0 ? MumbleState.Connecting : MumbleState.Reconnecting, MumbleProblem.None, session.Detail);

            try
            {
                await RunOnceAsync(session, ct);
                if (ct.IsCancellationRequested) break;
                session.Detail = "";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (MumbleException e) when (e.Failure == MumbleFailure.Certificate)
            {
                session.CertificateFingerprint = e.CertificateFingerprint ?? "";
                session.CertificateSubject     = e.CertificateSubject ?? "";
                SetState(session, MumbleState.Failed, MumbleProblem.Certificate, e.Message);
                return;
            }
            catch (MumbleException e) when (e.Failure == MumbleFailure.Removed)
            {
                // ⚠️ Not a dropped connection. Going straight back would undo a kick, and two clients on one login would
                // throw each other off for ever. The standard client stays off too.
                SetState(session, MumbleState.Failed, MumbleProblem.Removed, e.Message);
                return;
            }
            catch (MumbleException e) when (e.Failure == MumbleFailure.Rejected
                && e.Reject is MumbleReject.WrongUserPassword or MumbleReject.WrongServerPassword or MumbleReject.NoCertificate
                               or MumbleReject.InvalidUsername or MumbleReject.WrongVersion or MumbleReject.AuthenticatorFail)
            {
                SetState(session, MumbleState.Failed, e.Reject switch
                {
                    MumbleReject.WrongUserPassword or MumbleReject.WrongServerPassword => MumbleProblem.Password,
                    MumbleReject.InvalidUsername => MumbleProblem.Username,
                    _ => MumbleProblem.Other,
                }, e.Message);
                return;
            }
            catch (Exception e)
            {
                // ⚠️ Everything. This loop is the only thing bringing the connection back, and a database that is
                // busy or a value it does not like must not end that without a word.
                if (e is not (MumbleException or IOException or TimeoutException or OperationCanceledException))
                    errors.Log(nameof(MumbleService), "keep connected", e);
                session.Detail = e.Message;
            }
            finally
            {
                try { await EndConnectionAsync(session); }
                catch (Exception e) when (e is not OperationCanceledException) { errors.Log(nameof(MumbleService), "end a connection", e); }
            }

            // Told to stop: the failure that just ended the read loop is that, and not worth retrying.
            if (ct.IsCancellationRequested) break;

            if (DateTimeOffset.UtcNow - started > StableAfter) attempt = 0;

            var wait = TimeSpan.FromSeconds(BackoffSeconds[Math.Min(attempt, BackoffSeconds.Length - 1)]);
            attempt++;

            session.NextAttempt = DateTimeOffset.UtcNow + wait;
            SetState(session, MumbleState.Reconnecting, MumbleProblem.None, session.Detail);

            try { await Task.Delay(wait, ct); }
            catch (OperationCanceledException) { break; }
            finally { session.NextAttempt = null; }
        }
    }

    private async Task EndConnectionAsync(MumbleSession session)
    {
        Interlocked.Exchange(ref session.Talking, (int)MumbleTalk.None);
        ReleaseMicIfIdle();
        UnbindKeys(session);
        Mixer.RemoveServer(session.Server.Id);
        if (session.Client is { } client)
        {
            session.Client = null;
            await client.DisposeAsync();
        }
        TalkersChanged?.Invoke();
    }

    private async Task RunOnceAsync(MumbleSession session, CancellationToken ct)
    {
        var server   = session.Server;
        var password = await Task.Run(() => AppConfig.GetServicePassword(PasswordReference(server.Id)) ?? "", ct);
        var identity = await Task.Run(() => ClientCertificate(server), ct);

        var client = new MumbleClient(new MumbleLogin(
            server.Host, server.Port, server.Username, password, identity, server.TrustedCertificate,
            [.. server.Tokens.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)]));

        client.VoiceReceived += v => OnVoice(session, v);
        client.UserLeft += s => { Mixer.Remove(server.Id, s); TalkersChanged?.Invoke(); };
        client.TextReceived += t => OnText(session, client, t);
        client.Denied += why =>
        {
            Refused?.Invoke(session, why);
            if (why.Length > 0) { session.Detail = why; SessionChanged?.Invoke(session); }
        };

        // ⚠️ Where to return to is read before connecting, and nothing is remembered until that is done:
        // every login starts at the root, which would otherwise overwrite it on the way in.
        var returnTo = server.LastChannel;
        var settled  = false;

        uint lastChannel = uint.MaxValue;
        client.Changed += () =>
        {
            if (client.Me is { } me && me.ChannelId != lastChannel)
            {
                lastChannel = me.ChannelId;
                if (Volatile.Read(ref settled)) _ = RememberChannelAsync(session, client, me.ChannelId);
            }

            // Targets name channels and people by ids, so they follow this client moving, people coming and going, and
            // channels being made.
            _ = SendTargetsAsync(session, client, ct);
            NoteTextures(session, client);
            SessionChanged?.Invoke(session);
        };

        session.Client = client;
        lock (session.SentTargets) session.SentTargets.Clear();
        await client.ConnectAsync(ct);

        session.Encoder = new MumbleEncoder(MumbleEncoder.BitrateFor(client.MaxBandwidth));
        await LoadVolumesAsync(session, ct);

        // Back where they were, and as muted as they were.
        if (FindChannel(client, returnTo) is { } channel && channel != client.Me?.ChannelId)
            await client.JoinChannelAsync(channel, ct);
        Volatile.Write(ref settled, true);

        if (server.Silent)
            await client.SetSelfMuteAsync(server.Silent, server.SelfDeafened, ct);

        Devices?.StartOutput();
        BindKeys(session);

        SetState(session, MumbleState.Connected, MumbleProblem.None, "");
        await client.RunAsync(ct);

        if (!ct.IsCancellationRequested)
            throw new MumbleException(MumbleFailure.Network, "The connection ended.");
    }

    /// <summary>Tells the server any saved target that has changed, whether in Settings or by things moving on the server.</summary>
    private static async Task SendTargetsAsync(MumbleSession session, MumbleClient client, CancellationToken ct)
    {
        var targets = session.Targets;
        var slots = MumbleTargets.Slots(targets);
        for (var i = 0; i < targets.Count; i++)
        {
            if (slots[i] <= 0) continue;

            var message = MumbleClient.VoiceTargetMessage(slots[i], MumbleTargets.EntriesOf(targets[i], client));
            lock (session.SentTargets)
            {
                if (session.SentTargets.TryGetValue(slots[i], out var sent) && sent.AsSpan().SequenceEqual(message)) continue;
                session.SentTargets[slots[i]] = message;
            }

            await SendTargetAsync(client, message, ct);
        }

        await SendPickAsync(session, client, ct);
    }

    /// <summary>
    /// Tells the server who the selection key reaches, if that has changed. Done as the selection changes, not at the press,
    /// so that the first words can't go to what was selected before.
    /// </summary>
    private static async Task SendPickAsync(MumbleSession session, MumbleClient client, CancellationToken ct)
    {
        if (session.Server.PickKey == 0) return;

        await session.PickSending.WaitAsync(ct);
        try
        {
            var message = MumbleClient.VoiceTargetMessage(MumbleTargets.PickSlot,
                MumbleTargets.EntriesOfPick(session.Picked, session.Server.PickLinks, session.Server.PickSubchannels, client));
            lock (session.SentTargets)
            {
                if (session.SentTargets.TryGetValue(MumbleTargets.PickSlot, out var sent) && sent.AsSpan().SequenceEqual(message)) return;
                session.SentTargets[MumbleTargets.PickSlot] = message;
            }

            await SendTargetAsync(client, message, ct);
        }
        finally { session.PickSending.Release(); }
    }

    /// <summary>
    /// Selects a person or a channel for the selection key, as the standard client's tree does: what is clicked or
    /// right-clicked there. Yourself is not something to whisper to.
    /// </summary>
    public void Pick(MumbleSession session, MumblePick pick)
    {
        if (session.State != MumbleState.Connected || session.Client is not { } client) return;
        if (pick.IsPerson && pick.Id == client.Session) return;
        if (session.Picked == pick) return;

        session.SetPick(pick, client);
        _ = SendPickAsync(session, client, CancellationToken.None);
        SessionChanged?.Invoke(session);
    }

    private static async Task SendTargetAsync(MumbleClient client, byte[] message, CancellationToken ct)
    {
        try { await client.SendVoiceTargetAsync(message, ct); }
        catch (Exception e) when (e is MumbleException or OperationCanceledException or IOException) { /* the next connection sets it again */ }
    }

    /// <summary>A channel as its names from the root, which survive a server restart better than its id.</summary>
    public static string PathOf(MumbleClient client, uint channel)
    {
        var names = new List<string>();
        // The client refuses loops, and the count is here so that a mistake there could not hang this.
        var steps = 0;
        for (var c = client.ChannelOf(channel); c?.Parent is { } parent && steps < 1000; c = client.ChannelOf(parent), steps++) names.Insert(0, c.Name);
        return string.Join('/', names);
    }

    public static uint? FindChannel(MumbleClient client, string path)
    {
        if (path.Length == 0) return null;

        uint at = 0;
        foreach (var name in path.Split('/'))
        {
            var next = client.Channels.FirstOrDefault(c => c.Parent == at && c.Name == name);
            if (next is null) return null;
            at = next.Id;
        }
        return at;
    }

    private async Task RememberChannelAsync(MumbleSession session, MumbleClient client, uint channel)
    {
        var path = PathOf(client, channel);
        if (path == session.Server.LastChannel) return;

        session.Server.LastChannel = path;
        var id = session.Server.Id;
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            await db.MumbleServers.Where(s => s.Id == id).ExecuteUpdateAsync(u => u.SetProperty(s => s.LastChannel, path));
        }
        catch (Exception e) when (e is DbUpdateException or InvalidOperationException) { errors.Log(nameof(MumbleService), "remember the channel", e); }
    }

    private void SetState(MumbleSession session, MumbleState state, MumbleProblem problem, string detail)
    {
        session.State   = state;
        session.Problem = problem;
        session.Detail  = detail;
        UpdateOpenMic();
        SessionChanged?.Invoke(session);
    }

    // ── Hearing ───────────────────────────────────────────────────────────────

    private void OnVoice(MumbleSession session, MumbleVoicePacket packet)
    {
        if (session.Server.SelfDeafened || session.Client is not { } client) return;

        // ⚠️ Only from someone the server has said is there. Each new speaker costs a decoder kept until they leave,
        // so sessions made up on the spot would use up memory without limit.
        if (client.UserOf(packet.Session) is not { } user) return;

        var gain = session.VolumeOf(user);
        if (gain <= 0 || session.IsLocalMuted(user)) return;

        Mixer.Receive(session.Server.Id, packet, gain, user.PrioritySpeaker);
    }

    private async Task LoadVolumesAsync(MumbleSession session, CancellationToken ct)
    {
        var id = session.Server.Id;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.MumbleUserVolumes.AsNoTracking().Where(v => v.ServerId == id).ToListAsync(ct);

        session.Volumes    = rows.ToDictionary(v => v.UserKey, v => v.Volume);
        session.LocalMuted = new(rows.Where(v => v.LocalMute).Select(v => KeyValuePair.Create(v.UserKey, true)));
        session.Ignored    = new(rows.Where(v => v.IgnoreText).Select(v => KeyValuePair.Create(v.UserKey, true)));
    }

    /// <summary>Stops hearing someone on this client alone, or starts again, and remembers it.</summary>
    public async Task SetLocalMuteAsync(MumbleSession session, MumbleUser user, bool mute, CancellationToken ct = default)
    {
        if (mute) { session.LocalMuted[UserKey(user)] = true; Mixer.Remove(session.Server.Id, user.Session); }
        else session.LocalMuted.TryRemove(UserKey(user), out _);

        await SavePersonAsync(session, user, row => row.LocalMute = mute, ct);
        SessionChanged?.Invoke(session);
    }

    /// <summary>Stops taking someone's words on this client alone, or starts again, and remembers it.</summary>
    public async Task SetIgnoredAsync(MumbleSession session, MumbleUser user, bool ignore, CancellationToken ct = default)
    {
        if (ignore) session.Ignored[UserKey(user)] = true;
        else session.Ignored.TryRemove(UserKey(user), out _);

        await SavePersonAsync(session, user, row => row.IgnoreText = ignore, ct);
        SessionChanged?.Invoke(session);
    }

    private async Task SavePersonAsync(MumbleSession session, MumbleUser user, Action<MumbleUserVolume> change, CancellationToken ct)
    {
        var key = UserKey(user);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.MumbleUserVolumes.FirstOrDefaultAsync(v => v.ServerId == session.Server.Id && v.UserKey == key, ct);
        if (row is null) db.MumbleUserVolumes.Add(row = new MumbleUserVolume { ServerId = session.Server.Id, UserKey = key, Volume = session.VolumeOf(user) });
        change(row);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Sets how loud someone is for this listener, and remembers it.</summary>
    public async Task SetUserVolumeAsync(MumbleSession session, MumbleUser user, double volume, CancellationToken ct = default)
    {
        var key = UserKey(user);
        var value = Math.Clamp(volume, 0, 3);
        session.Volumes[key] = value;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.MumbleUserVolumes.FirstOrDefaultAsync(v => v.ServerId == session.Server.Id && v.UserKey == key, ct);
        if (row is null) db.MumbleUserVolumes.Add(new MumbleUserVolume { ServerId = session.Server.Id, UserKey = key, Volume = value, LocalMute = session.IsLocalMuted(user), IgnoreText = session.IsIgnored(user) });
        else row.Volume = value;
        await db.SaveChangesAsync(ct);
    }

    // ── Text ──────────────────────────────────────────────────────────────────


    /// <summary>Mumble text is HTML, and anyone can send anything. It is shown as plain words, never rendered.</summary>
    public static string PlainText(string html)
    {
        var withBreaks = html.Replace("<br>", "\n", StringComparison.OrdinalIgnoreCase)
                             .Replace("<br/>", "\n", StringComparison.OrdinalIgnoreCase)
                             .Replace("<br />", "\n", StringComparison.OrdinalIgnoreCase);
        return WebUtility.HtmlDecode(WithoutTags(withBreaks)).Trim();
    }

    /// <summary>
    /// ⚠️ By hand and in one pass, not a regex: megabytes of "&lt;" with no "&gt;" would take a regex minutes, on the
    /// connection's reading thread.
    /// </summary>
    private static string WithoutTags(string html)
    {
        var words = new StringBuilder(html.Length);
        var at = 0;
        while (at < html.Length)
        {
            var open = html.IndexOf('<', at);
            if (open < 0) { words.Append(html, at, html.Length - at); break; }

            words.Append(html, at, open - at);

            var close = html.IndexOf('>', open + 1);
            if (close < 0) { words.Append(html, open, html.Length - open); break; }

            at = close + 1;
        }
        return words.ToString();
    }

    private void OnText(MumbleSession session, MumbleClient client, MumbleTextMessage text)
    {
        if (text.Actor is { } said && client.UserOf(said) is { } author && session.IsIgnored(author)) return;

        var sender = text.Actor is { } actor ? client.UserOf(actor)?.Name ?? "" : "";
        var words = PlainText(text.Message);
        if (words.Length > MostChatChars) words = words[..MostChatChars] + "\u2026";

        if (text.Sessions.Count > 0)
        {
            // Filed under who sent it, so a server's own notices, from nobody in particular, stay in its chat.
            if (text.Actor is { } from && client.UserOf(from) is { } person)
            {
                RecordDirect(session.Server.Id, person, sender, words, false);
                return;
            }
        }

        var isTree = text.Trees.Count > 0;
        uint? where = isTree ? text.Trees[0] : text.Channels.Count > 0 ? text.Channels[0] : null;
        var channel = where is { } id ? client.ChannelOf(id)?.Name ?? "" : "";

        Record(session, new MumbleChatLine(DateTimeOffset.UtcNow, sender, words, false) { Channel = channel, IsTree = isTree });
    }

    /// <summary>To a channel, by default the one this client is in, and if asked the ones under it too.</summary>
    public async Task SendTextAsync(MumbleSession session, string message, uint? channel = null, bool andSubchannels = false, CancellationToken ct = default)
    {
        if (session.Client is not { Me: { } me } client) throw new MumbleException(MumbleFailure.Network, "Not connected.");

        var target = channel ?? me.ChannelId;
        var html = WebUtility.HtmlEncode(message).Replace("\n", "<br>", StringComparison.Ordinal);

        if (andSubchannels) await client.SendTextToTreeAsync(target, html, ct);
        else await client.SendTextToChannelAsync(target, html, ct);

        Record(session, new MumbleChatLine(DateTimeOffset.UtcNow, me.Name, message, true) { Channel = client.ChannelOf(target)?.Name ?? "", IsTree = andSubchannels });
    }

    /// <summary>To one person, wherever on the server they are.</summary>
    public async Task SendPrivateTextAsync(MumbleSession session, MumbleUser to, string message, CancellationToken ct = default)
    {
        if (session.Client is not { Me: { } me } client) throw new MumbleException(MumbleFailure.Network, "Not connected.");

        await client.SendTextToUserAsync(to.Session, WebUtility.HtmlEncode(message).Replace("\n", "<br>", StringComparison.Ordinal), ct);

        RecordDirect(session.Server.Id, to, me.Name, message, true);
    }

    public Task JoinChannelAsync(MumbleSession session, uint channel, CancellationToken ct = default) =>
        session.Client?.JoinChannelAsync(channel, ct) ?? Task.CompletedTask;

    /// <summary>Mute at the person's word. While deafened that means "let me talk", which takes the deafening off with it, as in the standard client.</summary>
    public Task ToggleMuteAsync(MumbleSession session, CancellationToken ct = default) =>
        SetMuteAsync(session, !session.Server.Silent, false, ct);

    /// <summary>Deafen at the person's word, leaving their own mute as it was to come back to.</summary>
    public Task ToggleDeafenAsync(MumbleSession session, CancellationToken ct = default) =>
        SetMuteAsync(session, session.Server.SelfMuted, !session.Server.SelfDeafened, ct);

    public async Task SetMuteAsync(MumbleSession session, bool mute, bool deaf, CancellationToken ct = default)
    {
        session.Server.SelfMuted    = mute;
        session.Server.SelfDeafened = deaf;

        if (mute || deaf) StopTalking(session);
        if (deaf) Mixer.RemoveServer(session.Server.Id);

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var id = session.Server.Id;
            await db.MumbleServers.Where(s => s.Id == id).ExecuteUpdateAsync(u => u
                .SetProperty(s => s.SelfMuted, mute).SetProperty(s => s.SelfDeafened, deaf), ct);
        }

        if (session.Client is { } client && session.State == MumbleState.Connected)
            await client.SetSelfMuteAsync(mute || deaf, deaf, ct);

        UpdateOpenMic();
        SessionChanged?.Invoke(session);
    }

    // ── Talking ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Binds this server's keys, for as long as it is connected. ⚠️ One hook per key: the same keyboard
    /// service push-to-talk for the agent uses, once for each, so each server's keys are its alone.
    /// </summary>
    private void BindKeys(MumbleSession session)
    {
        UnbindKeys(session);
        if (session.State != MumbleState.Connected && session.Client is null) return;

        var targets = session.Targets;
        var slots = MumbleTargets.Slots(targets);
        for (var i = 0; i < targets.Count; i++)
        {
            if (targets[i].Key == 0 || slots[i] < 0) continue;

            var slot = slots[i];
            var mode = slot == PlainTalk ? MumbleTalk.Shout : MumbleTalk.Channel;
            var hook = new GlobalHotkeyService
            {
                OnPress   = () => _keyEvents.Writer.TryWrite(() => StartTalking(session, mode, slot)),
                OnRelease = () => _keyEvents.Writer.TryWrite(() => StopTalkingFrom(session, slot)),
            };
            hook.Configure(targets[i].Key);
            session.KeyHooks.Add(hook);
        }

        if (session.Server.PickKey != 0)
        {
            var hook = new GlobalHotkeyService
            {
                OnPress   = () => _keyEvents.Writer.TryWrite(() => _ = StartPickedAsync(session)),
                OnRelease = () => _keyEvents.Writer.TryWrite(() =>
                {
                    session.PickHeld = false;
                    StopTalkingFrom(session, MumbleTargets.PickSlot);
                }),
            };
            hook.Configure(session.Server.PickKey);
            session.KeyHooks.Add(hook);
        }

        // Mute and deafen go on and off at each press, as the standard client's do.
        BindToggle(session, session.Server.MuteKey, () => ToggleMuteAsync(session));
        BindToggle(session, session.Server.DeafenKey, () => ToggleDeafenAsync(session));
    }

    private void BindToggle(MumbleSession session, int key, Func<Task> toggle)
    {
        if (key == 0) return;

        var hook = new GlobalHotkeyService { OnPress = () => _keyEvents.Writer.TryWrite(() => _ = ToggledAsync(toggle)) };
        hook.Configure(key);
        session.KeyHooks.Add(hook);
    }

    /// <summary>Whispers to what is selected, for as long as the key is held. Nothing selected, or gone since, says nothing.</summary>
    private async Task StartPickedAsync(MumbleSession session)
    {
        session.PickHeld = true;
        if (session.Client is not { } client || session.Picked is not { } pick
            || MumbleTargets.EntriesOfPick(pick, session.Server.PickLinks, session.Server.PickSubchannels, client).Count == 0) return;

        try
        {
            // Told already when it was selected. Again only if the server doesn't hold it, as after it reconnected.
            await SendPickAsync(session, client, CancellationToken.None);
            if (!session.PickHeld) return;
            StartTalking(session, pick.IsPerson ? MumbleTalk.Direct : MumbleTalk.Channel, MumbleTargets.PickSlot);
        }
        catch (Exception e) { errors.Log(nameof(MumbleService), "whisper to the selection", e); }
    }

    private async Task ToggledAsync(Func<Task> toggle)
    {
        try { await toggle(); }
        catch (Exception e) { errors.Log(nameof(MumbleService), "mute or deafen from a key", e); }
    }

    private static void UnbindKeys(MumbleSession session)
    {
        foreach (var hook in session.KeyHooks) hook.Dispose();
        session.KeyHooks.Clear();
    }

    /// <summary>Starts sending the microphone to this server, as plain talk or to the first saved target.</summary>
    public void StartTalking(MumbleSession session, MumbleTalk mode) => StartTalking(session, mode, TargetOf(mode));

    /// <summary>Starts sending the microphone to this server, on this key's terms.</summary>
    /// <param name="slot">The voice target each packet names: see <see cref="MumbleTargets.Slots"/>.</param>
    public void StartTalking(MumbleSession session, MumbleTalk mode, int slot)
    {
        if (session.Client is null || session.Encoder is null || session.Server.Silent) return;

        // A new press starts clean: a few samples left from the last one are not part of what is being said now. ⚠️ Unless
        // echo cancelling is on, which needs every sample of the microphone, in order, to stay in step with the speakers.
        if (Sessions.All(s => s.TalkingNow == MumbleTalk.None))
            lock (_micBuffer)
            {
                if (!_cleanup.CancelEcho) _micBuffer.Clear();
                _leveler.StartTalk();
                if (_cleanup.IsOn) _cleanup.NewPress();
            }

        // A key takes over from the voice: letting go of it, not a pause, is what ends this now.
        session.TalkingByVoice = false;
        Volatile.Write(ref session.TalkSlot, slot);
        var was = (MumbleTalk)Interlocked.Exchange(ref session.Talking, (int)mode);
        if (TalkTone && was == MumbleTalk.None) Mixer.PlayCue(starting: true);
        _micClose?.Cancel();

        if (Devices is { } devices && !devices.StartInput())
        {
            // Saying so, rather than showing them as talking to a room that hears nothing.
            Interlocked.Exchange(ref session.Talking, (int)MumbleTalk.None);
            session.MicFailed = true;
            session.Detail = devices.LastError ?? "";
            SessionChanged?.Invoke(session);
        }
        else if (session.MicFailed)
        {
            session.MicFailed = false;
            session.Detail = "";
            SessionChanged?.Invoke(session);
        }

        TalkersChanged?.Invoke();
    }

    private async Task HandleKeysAsync()
    {
        await foreach (var key in _keyEvents.Reader.ReadAllAsync())
        {
            try { key(); }
            catch (Exception e) { errors.Log(nameof(MumbleService), "handle a key", e); }
        }
    }

    /// <summary>Stops, if this target's key is the one talking: letting go of one while holding another changes nothing.</summary>
    private void StopTalkingFrom(MumbleSession session, int slot)
    {
        if (Volatile.Read(ref session.TalkSlot) == slot) StopTalking(session);
    }

    /// <summary>Stops, if this way of talking is the one going: a voice ending doesn't end a key being held.</summary>
    public void StopTalking(MumbleSession session, MumbleTalk? onlyIf = null)
    {
        if (onlyIf is { } mode && session.TalkingNow != mode) return;

        var byVoice = session.TalkingByVoice;
        var slot = Volatile.Read(ref session.TalkSlot);
        var was = (MumbleTalk)Interlocked.Exchange(ref session.Talking, (int)MumbleTalk.None);
        if (was == MumbleTalk.None) return;

        if (TalkTone && !byVoice) Mixer.PlayCue(starting: false);

        // The last frame says they let go, so the listeners' clients stop at once rather than timing out. It goes
        // through the sender: an encoder has state, and only one thread may ever use it.
        _frames.Writer.TryWrite(new VoiceWork(null, session, slot));

        ReleaseMicIfIdle();
        TalkersChanged?.Invoke();
    }

    /// <summary>
    /// How someone is talking, by what a server says on each packet of their voice. The same marker the standard client
    /// colours people by. ⚠️ Not the same numbers as what is sent: anything sent to a saved target, such as Whisper's own
    /// channel, reaches a listener as 1, and plain talk, which is Shout, as 0.
    /// </summary>
    public static MumbleTalk TalkOf(int target) => target switch
    {
        0 => MumbleTalk.Shout,
        2 => MumbleTalk.Direct,
        _ => MumbleTalk.Channel,
    };

    public static int TargetOf(MumbleTalk mode) => mode == MumbleTalk.Shout ? PlainTalk : ChannelOnlyTarget;

    private void ReleaseMicIfIdle()
    {
        if (Devices is null || MicStaysOpen || Sessions.Any(s => s.TalkingNow != MumbleTalk.None)) return;

        _micClose?.Cancel();
        var closing = _micClose = new CancellationTokenSource();
        _ = Task.Delay(MicLinger, closing.Token).ContinueWith(t =>
        {
            if (t.IsCanceled || MicStaysOpen || Sessions.Any(s => s.TalkingNow != MumbleTalk.None)) return;

            Devices?.StopInput();
            MicLevelDb = -96;   // nothing is being heard now, rather than whatever was last
        }, TaskScheduler.Default);
    }

    private void OnCaptured(short[] samples) => SubmitMicrophone(samples);

    private void OnMicOpening()
    {
        lock (_micBuffer)
        {
            _micBuffer.Clear();
            if (_cleanup.IsOn) _cleanup.StartTalk();
        }
    }

    /// <summary>Takes microphone samples in 20 ms frames. Fast, as it runs on the sound device's thread.</summary>
    public void SubmitMicrophone(ReadOnlySpan<short> samples)
    {
        lock (_micBuffer)
        {
            foreach (var s in samples) _micBuffer.Add(s);

            while (_micBuffer.Count >= MumbleEncoder.FrameSamples)
            {
                var frame = _micBuffer.GetRange(0, MumbleEncoder.FrameSamples).ToArray();
                _micBuffer.RemoveRange(0, MumbleEncoder.FrameSamples);
                if (_cleanup.IsOn) _cleanup.Process(frame);

                // Before levelling, which would turn a quiet room up until it counted as a voice.
                var level = MumbleVoiceGate.LevelOf(frame);
                MicLevelDb = level;

                if (AutoLevel) _leveler.Process(frame, _cleanup.LastFrameWasEcho);
                if (WantsOpenMic) GateByVoice(frame, level);

                // Only once someone is talking: with the microphone open all the time, this would otherwise be most of it.
                if (Sessions.Any(s => s.TalkingNow != MumbleTalk.None))
                    _frames.Writer.TryWrite(new VoiceWork(frame, null, PlainTalk));
            }
        }
    }

    /// <summary>Starts the worker a test drives without any sound hardware.</summary>
    public void StartWithoutDevices()
    {
        _sender ??= Task.Run(SendFramesAsync, CancellationToken.None);
        _keyWorker ??= Task.Run(HandleKeysAsync, CancellationToken.None);
        _chatSaver ??= Task.Run(SaveChatAsync, CancellationToken.None);
        StartTalkerWatch();
    }

    private async Task SendFramesAsync()
    {
        var silence = new short[MumbleEncoder.FrameSamples];

        await foreach (var work in _frames.Reader.ReadAllAsync())
        {
            // ⚠️ Whatever goes wrong costs one frame. Ending this loop would end voice on every server until a restart.
            try
            {
                if (work.EndOf is { Client: { } endClient, Encoder: { } endEncoder } ended)
                    SendOut(ended, endClient, endEncoder.Encode(silence, work.EndSlot, last: true), mustGo: true);
                else if (work.Frame is { } frame)
                    foreach (var session in Sessions)
                    {
                        var mode = session.TalkingNow;
                        if (mode == MumbleTalk.None || session.Client is not { } client || session.Encoder is not { } encoder) continue;
                        SendOut(session, client, encoder.Encode(frame, Volatile.Read(ref session.TalkSlot), last: false), mustGo: false);
                    }
            }
            catch (Exception e) { errors.Log(nameof(MumbleService), "send voice", e); }
        }
    }

    /// <summary>
    /// ⚠️ Never waited on: one server that has stopped reading must not hold up the others. A frame that cannot go
    /// yet is dropped, which is the only useful thing to do with speech that is already late.
    /// </summary>
    private static void SendOut(MumbleSession session, MumbleClient client, MumbleVoicePacket packet, bool mustGo)
    {
        if (!mustGo && session.VoiceSending is { IsCompleted: false }) return;

        // ⚠️ Over UDP it has finished by now. Wrapping that in a continuation made it look unfinished for a moment, and a
        // sound device hands over frames in bursts, so the next one could arrive in that moment and be thrown away.
        var sending = client.SendVoiceAsync(packet, CancellationToken.None);
        session.VoiceSending = sending.IsCompletedSuccessfully
            ? sending
            : sending.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private Timer? _talkerTimer;
    private int _talkerTicks;
    private string _lastTalkers = "";

    private void StartTalkerWatch()
    {
        _talkerTimer ??= new Timer(_ =>
        {
            // Twice a second: a headset unplugged stops its stream without saying so.
            if (++_talkerTicks % 5 == 0) Devices?.KeepOutputAlive();

            var now = string.Join('|', Talkers().Select(t => $"{t.ServerId}:{t.Session}:{t.Mode}"));
            if (now == _lastTalkers) return;
            _lastTalkers = now;
            TalkersChanged?.Invoke();
        }, null, 100, 100);
    }

    /// <summary>Whether someone is muted and deafened as things stand, or null if they are no longer there.</summary>
    public (bool Muted, bool Deaf)? StateOf(int serverId, uint session) =>
        SessionOf(serverId)?.Client?.UserOf(session) is { } user ? (user.Mute || user.SelfMute || user.Suppress, user.Deaf || user.SelfDeaf) : null;

    /// <summary>Everyone talking right now, across every server, with this client first where it is.</summary>
    public IReadOnlyList<MumbleTalker> Talkers()
    {
        var result = new List<MumbleTalker>();

        foreach (var session in Sessions)
            if (session.TalkingNow != MumbleTalk.None && session.Client?.Me is { } me)
                result.Add(new MumbleTalker(session.Server.Id, Label(session), me.Session, me.Name, true, session.TalkingNow));

        foreach (var (server, user) in Mixer.TalkingNow())
            if (SessionOf(server) is { Client: { } client } session && client.UserOf(user) is { } who)
                result.Add(new MumbleTalker(server, Label(session), user, who.Name, false, TalkOf(Mixer.TargetOf(server, user))));

        return result;

        static string Label(MumbleSession s) => s.Server.Name.Length > 0 ? s.Server.Name : s.Server.Host;
    }

    /// <summary>
    /// A key another server uses, or the same as this one's other: keys must be unique, or one press would
    /// talk on two servers at once. Null when there is no clash.
    /// </summary>
    public static string? KeyClash(MumbleServer server, IEnumerable<MumbleServer> others)
    {
        var keys = KeysOf(server);
        if (keys.Count != keys.Distinct().Count()) return server.Name;

        foreach (var other in others)
        {
            if (other.Id == server.Id) continue;
            if (KeysOf(other).Intersect(keys).Any()) return other.Name.Length > 0 ? other.Name : other.Host;
        }
        return null;
    }

    private static List<int> KeysOf(MumbleServer server) =>
        [.. MumbleTargets.Read(server).Select(t => t.Key).Append(server.MuteKey).Append(server.DeafenKey).Append(server.PickKey).Where(k => k != 0)];
}
