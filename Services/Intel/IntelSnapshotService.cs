using EveConsole.Data;
using EveConsole.Localization;
using EveConsole.Models;
using EveConsole.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Intel;

/// <summary>
/// The one shared read side of intel: every few seconds, the database read into one
/// <see cref="IntelSnapshot"/> that the panels, the Intel tool, the map and the alarms all take.
///
/// <para><b>When it reads.</b> Every five seconds while anything is subscribed (a panel, the tool,
/// the map) or <see cref="KeepWarm"/> is set (the worker, while an intel alarm is on), and at once
/// when intel is written anywhere (<see cref="IntelSignals.Changed"/>, which reaches this PC too).
/// Idle otherwise. Readers never wait: they take <see cref="Current"/>.</para>
///
/// <para><b>What it reads.</b> Reports and clears in the window, all of them (they change: a later
/// report supersedes one). Where each pilot was last seen, incrementally: only the rows written since
/// the last read, by <c>WrittenAt</c>, with a full reload every five minutes as a safety net; a kill
/// stored late with an old time is still among them. Kills from an in-memory cache: each pass asks
/// for the kill ids in the window and loads only the ones it has not seen, because a kill never
/// changes. Own characters' positions, the newer of ESI's location and the chat log's
/// "Channel changed to Local" line. The settings, fresh, every minute.</para>
///
/// <para>⚠️ Every PC builds its own from the shared database, so every PC shows the same intel. The
/// distances for each own character are walked here, off the UI thread, so a panel finds them cached.</para>
/// </summary>
public sealed class IntelSnapshotService : IDisposable
{
    public static readonly TimeSpan RefreshEvery    = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan SettingsEvery   = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan FullReloadEvery = TimeSpan.FromMinutes(5);

    /// <summary>How far before the last read the next incremental read starts: room for clocks on
    /// two PCs a little apart, and a write committing just after the read.</summary>
    public static readonly TimeSpan WrittenSlack = TimeSpan.FromSeconds(30);

    /// <summary>How far back the chat logs are read for own positions on the first pass.</summary>
    private static readonly TimeSpan ChatPositionsBack = TimeSpan.FromHours(12);

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IntelStandingService            _standings;
    private readonly IntelDistanceService?           _distances;
    private readonly CorpActivityService?            _names;
    private readonly AppPreferencesService?          _prefs;
    private readonly AppErrorLogger?                 _errors;
    private readonly IntelService?                   _intel;

    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private readonly IntelCampRegister _register = new();
    private readonly object _loopLock = new();
    private int _subscribers;
    private bool _keepWarm;
    private Task? _loop;
    private long _version;
    private string? _lastError;

    // What one pass keeps for the next.
    private IntelSettingsValues? _settings;
    private DateTimeOffset _settingsAt = DateTimeOffset.MinValue;
    private readonly Dictionary<long, IntelLastSeen> _lastSeen = [];
    private DateTimeOffset _lastSeenReadAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastSeenFullAt = DateTimeOffset.MinValue;
    private readonly Dictionary<int, IntelKill> _kills = [];
    private readonly Dictionary<int, int> _groupOfType = [];
    private readonly Dictionary<int, string> _typeNames = [];
    private readonly Dictionary<long, string> _pilotNames = [];
    private readonly Dictionary<long, (long, long)> _affiliations = [];
    private DateTimeOffset _affiliationsAt = DateTimeOffset.MinValue;
    private SdeData? _sde;
    private long _chatAfterId = -1;
    private readonly Dictionary<long, (int SystemId, DateTimeOffset At)> _chatPositions = [];
    private readonly Dictionary<long, (int SystemId, DateTimeOffset Since)> _ownSince = [];
    private Task _resolving = Task.CompletedTask;

    /// <summary>The gates, the groups and the system names: read once, and again after an SDE import.</summary>
    private sealed record SdeData(
        IReadOnlyDictionary<int, IReadOnlyList<IntelGate>> Gates, IntelSdeGroups Groups,
        IReadOnlyDictionary<int, string> SystemNames, IReadOnlyDictionary<string, int> SystemIds);

    public IntelSnapshotService(
        IDbContextFactory<AppDbContext> dbFactory, IntelStandingService standings,
        IntelDistanceService? distances = null, IntelService? intel = null, CorpActivityService? names = null,
        AppPreferencesService? prefs = null, AppErrorLogger? errors = null)
    {
        _dbFactory = dbFactory;
        _standings = standings;
        _distances = distances;
        _names     = names;
        _prefs     = prefs;
        _errors    = errors;
        _intel     = intel;
        if (intel is not null) intel.IntelChanged += Request;
        SdeNames.Changed += SdeChanged;
    }

    public void Dispose()
    {
        if (_intel is not null) _intel.IntelChanged -= Request;
        SdeNames.Changed -= SdeChanged;
    }

    private void SdeChanged() => _sde = null;

    /// <summary>The latest snapshot; empty before the first read. Never null.</summary>
    public IntelSnapshot Current { get; private set; } = IntelSnapshot.Empty;

    /// <summary>Raised after each new snapshot, on a background thread: a view posts to its own.</summary>
    public event Action<IntelSnapshot>? Changed;

    /// <summary>Kept reading while no view is open: set by the worker while an intel alarm is on.</summary>
    public bool KeepWarm
    {
        get => _keepWarm;
        set { _keepWarm = value; if (value) EnsureLoop(); }
    }

    /// <summary>Keeps the snapshot refreshing until the returned handle is disposed.</summary>
    public IDisposable Subscribe()
    {
        Interlocked.Increment(ref _subscribers);
        EnsureLoop();
        Request();
        return new Subscription(this);
    }

    private sealed class Subscription(IntelSnapshotService owner) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) Interlocked.Decrement(ref owner._subscribers);
        }
    }

    /// <summary>A refresh as soon as the loop can; a burst of requests is one refresh.</summary>
    public void Request()
    {
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    /// <summary>The current snapshot when it is newer than <paramref name="maxAge"/>; otherwise a
    /// new one, read now. For a reader that polls on its own schedule (the map's adapter).</summary>
    public async Task<IntelSnapshot> FreshAsync(TimeSpan maxAge, CancellationToken ct = default) =>
        DateTimeOffset.UtcNow - Current.At <= maxAge ? Current : await RefreshNowAsync(ct).ConfigureAwait(false);

    /// <summary>Reads a new snapshot now (one at a time) and returns it. A failed read keeps the
    /// last one and is logged once per distinct failure.</summary>
    public async Task<IntelSnapshot> RefreshNowAsync(CancellationToken ct = default)
    {
        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var snapshot = await BuildAsync(ct).ConfigureAwait(false);
            Current = snapshot;
            _lastError = null;
            try { Changed?.Invoke(snapshot); }
            catch (Exception ex) { _errors?.Log(nameof(IntelSnapshotService), "a snapshot subscriber", ex); }
            await WarmDistancesAsync(snapshot, ct).ConfigureAwait(false);
            return snapshot;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            if (ex.Message != _lastError)
            {
                _lastError = ex.Message;
                _errors?.Log(nameof(IntelSnapshotService), "reading intel", ex);
            }
            return Current;
        }
        finally { _refreshGate.Release(); }
    }

    /// <summary>Hides the kill-feed warning on every PC.</summary>
    public async Task DismissFeedWarningAsync()
    {
        if (_prefs is not null) await _prefs.SetBoolAsync(IntelSettings.KeyFeedWarning, true).ConfigureAwait(false);
        _settingsAt = DateTimeOffset.MinValue;
        Request();
    }

    /// <summary>Shows the kill-feed warning again (Settings → Intel).</summary>
    public async Task ResetFeedWarningAsync()
    {
        if (_prefs is not null) await _prefs.SetBoolAsync(IntelSettings.KeyFeedWarning, false).ConfigureAwait(false);
        _settingsAt = DateTimeOffset.MinValue;
        Request();
    }

    /// <summary>Reads the settings again on the next pass: after Settings → Intel changed one here.</summary>
    public void SettingsChanged()
    {
        _settingsAt = DateTimeOffset.MinValue;
        Request();
    }

    private void EnsureLoop()
    {
        lock (_loopLock)
        {
            if (_loop is { IsCompleted: false }) return;
            _loop = Task.Run(LoopAsync);
        }
    }

    private bool Wanted => Volatile.Read(ref _subscribers) > 0 || _keepWarm;

    private async Task LoopAsync()
    {
        while (true)
        {
            lock (_loopLock)
                if (!Wanted) { _loop = null; return; }

            await RefreshNowAsync().ConfigureAwait(false);
            await _wake.WaitAsync(RefreshEvery).ConfigureAwait(false);
            // A burst while the pass ran is one more pass, not one per request.
            while (_wake.CurrentCount > 0) await _wake.WaitAsync(0).ConfigureAwait(false);
        }
    }

    private async Task WarmDistancesAsync(IntelSnapshot snapshot, CancellationToken ct)
    {
        if (_distances is null) return;
        foreach (var system in snapshot.Own.Select(o => o.SystemId).Distinct())
        {
            try { await _distances.JumpsFromAsync(system, IntelGraphMode.Threat, null, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { _errors?.Log(nameof(IntelSnapshotService), "walking distances", ex); }
        }
    }

    // ── One pass ─────────────────────────────────────────────────────────────

    private async Task<IntelSnapshot> BuildAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var settings = await SettingsAsync(db, now, ct).ConfigureAwait(false);
        var from     = now - settings.Window;
        var fromText = IntelLastSeenWriter.Format(from);
        var sde      = _sde ??= await LoadSdeAsync(db, ct).ConfigureAwait(false);

        // Reports and clears in the window, with their pilots.
        var reports = await db.IntelReports.AsNoTracking()
            .Where(r => string.Compare(r.ReportedAt, fromText) >= 0)
            .ToListAsync(ct).ConfigureAwait(false);
        var reportIds = reports.Select(r => r.Id).ToList();
        var reportPilots = new List<IntelReportCharacter>();
        foreach (var chunk in reportIds.Chunk(1000))
            reportPilots.AddRange(await db.IntelReportCharacters.AsNoTracking()
                .Where(c => chunk.Contains(c.IntelReportId)).ToListAsync(ct).ConfigureAwait(false));

        await ReadLastSeenAsync(db, now, fromText, ct).ConfigureAwait(false);
        await ReadKillsAsync(db, from, ct).ConfigureAwait(false);
        var own = await ReadOwnAsync(db, sde, now, ct).ConfigureAwait(false);
        var officers = await LiveIntelService.OfficersAsync(db, now, ct).ConfigureAwait(false);
        var standings = await _standings.GetAsync(settings.StandingsCharacterId, ct).ConfigureAwait(false);

        var pilotIds = _lastSeen.Keys
            .Concat(reportPilots.Select(p => p.CharacterId))
            .Concat(_kills.Values.SelectMany(k => k.Attackers.Select(a => a.CharacterId).Append(k.VictimId)))
            .Where(id => id > 0).ToHashSet();
        await NamesAsync(db, pilotIds, ct).ConfigureAwait(false);
        await AffiliationsAsync(db, reportPilots.Select(p => p.CharacterId).Where(id => !_lastSeen.ContainsKey(id)).ToHashSet(), now, ct)
            .ConfigureAwait(false);
        var typeIds = _lastSeen.Values.Select(r => r.ShipTypeId ?? 0)
            .Concat(reportPilots.Select(p => p.ShipTypeId ?? 0))
            .Concat(_kills.Values.SelectMany(k => k.Attackers.Select(a => a.ShipTypeId ?? 0).Append(k.VictimShipTypeId)))
            .Where(t => t > 0).ToHashSet();
        await TypeNamesAsync(db, typeIds, ct).ConfigureAwait(false);

        var input = new IntelSnapshotInput
        {
            Now = now, Settings = settings,
            Reports = reports, ReportPilots = reportPilots,
            LastSeen = [.. _lastSeen.Values],
            Kills = [.. _kills.Values],
            Gates = sde.Gates, Groups = sde.Groups, SystemNames = sde.SystemNames,
            Own = own, Names = new Dictionary<long, string>(_pilotNames), TypeNames = new Dictionary<int, string>(_typeNames),
            Affiliations = new Dictionary<long, (long, long)>(_affiliations),
            Officers = officers, Standings = standings,
        };
        return IntelSnapshotBuilder.Build(input, _register, Interlocked.Increment(ref _version));
    }

    private async Task<IntelSettingsValues> SettingsAsync(AppDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        if (_settings is { } held && now - _settingsAt < SettingsEvery) return held;
        var read = await IntelSettings.ReadAsync(db, ct).ConfigureAwait(false);

        // The warning comes back by itself if the full feed is switched on and off again: its
        // dismissal is cleared whenever the full feed is seen on.
        if (read.FeedWarningDismissed && read.ZkbEnabled && read.ZkbScope == ZkbScope.All && _prefs is not null)
        {
            await _prefs.SetBoolAsync(IntelSettings.KeyFeedWarning, false).ConfigureAwait(false);
            read = read with { FeedWarningDismissed = false };
        }
        _settings = read;
        _settingsAt = now;
        return read;
    }

    /// <summary>Last seen: everything in the window every five minutes, and between, only the rows
    /// written since the last read.</summary>
    private async Task ReadLastSeenAsync(AppDbContext db, DateTimeOffset now, string fromText, CancellationToken ct)
    {
        List<IntelLastSeen> rows;
        if (now - _lastSeenFullAt >= FullReloadEvery)
        {
            rows = await db.IntelLastSeens.AsNoTracking()
                .Where(r => string.Compare(r.SeenAt, fromText) >= 0)
                .ToListAsync(ct).ConfigureAwait(false);
            _lastSeen.Clear();
            _lastSeenFullAt = now;
        }
        else
        {
            var sinceText = IntelLastSeenWriter.Format(_lastSeenReadAt - WrittenSlack);
            rows = await db.IntelLastSeens.AsNoTracking()
                .Where(r => string.Compare(r.WrittenAt, sinceText) >= 0)
                .ToListAsync(ct).ConfigureAwait(false);
        }
        _lastSeenReadAt = now;

        foreach (var r in rows) _lastSeen[r.CharacterId] = r;
        foreach (var id in _lastSeen.Where(kv => string.CompareOrdinal(kv.Value.SeenAt, fromText) < 0).Select(kv => kv.Key).ToList())
            _lastSeen.Remove(id);
    }

    /// <summary>The kills in the window: ids every pass, details only for ones not yet held.</summary>
    private async Task ReadKillsAsync(AppDbContext db, DateTimeOffset from, CancellationToken ct)
    {
        // ⚠️ Raw SQL for the time: SQLite cannot translate a DateTimeOffset comparison in LINQ.
        var ids = (await db.Database
            .SqlQueryRaw<int>("""SELECT "KillMailId" AS "Value" FROM "KillMailDetails" WHERE "KillMailTime" >= {0}""", from)
            .ToListAsync(ct).ConfigureAwait(false)).ToHashSet();

        foreach (var gone in _kills.Keys.Where(id => !ids.Contains(id)).ToList()) _kills.Remove(gone);
        var fresh = ids.Where(id => !_kills.ContainsKey(id)).ToList();
        foreach (var chunk in fresh.Chunk(500))
        {
            var details = await db.KillMailDetails.AsNoTracking()
                .Where(d => chunk.Contains(d.KillMailId)).ToListAsync(ct).ConfigureAwait(false);
            var attackers = (await db.KillMailAttackers.AsNoTracking()
                    .Where(a => chunk.Contains(a.KillMailId))
                    .Select(a => new { a.KillMailId, a.CharacterId, a.CorporationId, a.AllianceId, a.ShipTypeId, a.WeaponTypeId })
                    .ToListAsync(ct).ConfigureAwait(false))
                .ToLookup(a => a.KillMailId);

            var types = details.Select(d => d.VictimShipTypeId)
                .Concat(attackers.SelectMany(g => g).SelectMany(a => new[] { a.ShipTypeId ?? 0, a.WeaponTypeId ?? 0 }))
                .Where(t => t > 0).ToHashSet();
            await GroupsOfTypesAsync(db, types, ct).ConfigureAwait(false);
            int? GroupOf(int? type) => type is int t && _groupOfType.TryGetValue(t, out var g) ? g : null;

            foreach (var d in details)
                _kills[d.KillMailId] = new IntelKill(
                    d.KillMailId, d.KillMailTime, d.SolarSystemId,
                    d.VictimCharId, d.VictimCorpId, d.VictimAllianceId ?? 0, d.VictimShipTypeId, GroupOf(d.VictimShipTypeId),
                    d.VictimPosX, d.VictimPosY, d.VictimPosZ,
                    [.. attackers[d.KillMailId].Select(a => new IntelKillAttacker(
                        a.CharacterId ?? 0, a.CorporationId ?? 0, a.AllianceId ?? 0,
                        a.ShipTypeId, GroupOf(a.ShipTypeId), a.WeaponTypeId, GroupOf(a.WeaponTypeId)))]);
        }
    }

    private async Task GroupsOfTypesAsync(AppDbContext db, HashSet<int> types, CancellationToken ct)
    {
        var missing = types.Where(t => !_groupOfType.ContainsKey(t)).ToList();
        foreach (var chunk in missing.Chunk(1000))
            foreach (var t in await db.SdeTypes.AsNoTracking().Where(x => chunk.Contains(x.TypeId))
                         .Select(x => new { x.TypeId, x.GroupId }).ToListAsync(ct).ConfigureAwait(false))
                _groupOfType[t.TypeId] = t.GroupId;
    }

    private async Task TypeNamesAsync(AppDbContext db, HashSet<int> types, CancellationToken ct)
    {
        var missing = types.Where(t => !_typeNames.ContainsKey(t)).ToList();
        foreach (var chunk in missing.Chunk(1000))
            foreach (var t in await db.SdeTypes.AsNoTracking().Where(x => chunk.Contains(x.TypeId))
                         .Select(x => new { x.TypeId, x.Name }).ToListAsync(ct).ConfigureAwait(false))
                _typeNames[t.TypeId] = SdeNames.Type(t.TypeId, t.Name);
    }

    /// <summary>Names from the local caches only; the ones they lack are looked up over ESI in the
    /// background and are there on a later pass. Never ESI on the pass itself: with ESI slow, the
    /// whole picture would stop moving.</summary>
    private async Task NamesAsync(AppDbContext db, HashSet<long> ids, CancellationToken ct)
    {
        var missing = ids.Where(id => !_pilotNames.ContainsKey(id)
                                      && !(_lastSeen.TryGetValue(id, out var r) && !string.IsNullOrEmpty(r.Name))).ToList();
        if (missing.Count == 0) return;
        foreach (var chunk in missing.Chunk(1000))
        {
            foreach (var n in await db.UniverseNames.AsNoTracking().Where(u => chunk.Contains(u.EntityId))
                         .Select(u => new { u.EntityId, u.Name }).ToListAsync(ct).ConfigureAwait(false))
                _pilotNames[n.EntityId] = n.Name;
            foreach (var c in await db.Characters.AsNoTracking().Where(c => chunk.Contains(c.Id))
                         .Select(c => new { c.Id, c.Name }).ToListAsync(ct).ConfigureAwait(false))
                _pilotNames[c.Id] = c.Name;
        }
        ResolveInBackground([.. missing.Where(id => !_pilotNames.ContainsKey(id)).Take(1000)]);
    }

    private void ResolveInBackground(List<long> ids)
    {
        if (ids.Count == 0 || _names is null || !_resolving.IsCompleted) return;
        _resolving = Task.Run(async () =>
        {
            try { await _names.ResolveNamesAsync(ids); }
            catch { /* Names are a nicety here; the next pass asks again. */ }
        });
    }

    /// <summary>Pulled affiliations for report pilots no last-seen row covers, read again with the
    /// full reload.</summary>
    private async Task AffiliationsAsync(AppDbContext db, HashSet<long> ids, DateTimeOffset now, CancellationToken ct)
    {
        if (now - _affiliationsAt >= FullReloadEvery) { _affiliations.Clear(); _affiliationsAt = now; }
        var missing = ids.Where(id => !_affiliations.ContainsKey(id)).ToList();
        foreach (var chunk in missing.Chunk(1000))
        {
            var found = await db.CharacterAffiliations.AsNoTracking().Where(a => chunk.Contains(a.CharacterId))
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var id in chunk) _affiliations[id] = (0, 0);
            foreach (var a in found) _affiliations[a.CharacterId] = (a.CorporationId, a.AllianceId);
        }
    }

    /// <summary>The stargates by system with their positions and destinations, the groups intel
    /// reads, and the system names.</summary>
    private static async Task<SdeData> LoadSdeAsync(AppDbContext db, CancellationToken ct)
    {
        var gates = await (
            from c in db.SdeCelestials.AsNoTracking()
            where c.Kind == 2
            join a in db.SdeStargates.AsNoTracking() on c.ItemId equals (long)a.StargateId
            join b in db.SdeStargates.AsNoTracking() on a.DestinationStargateId equals b.StargateId
            select new { a.StargateId, a.SolarSystemId, Destination = b.SolarSystemId, c.X, c.Y, c.Z })
            .ToListAsync(ct).ConfigureAwait(false);
        var systems = await db.SdeSolarSystems.AsNoTracking()
            .Select(s => new { s.SolarSystemId, s.Name }).ToListAsync(ct).ConfigureAwait(false);

        return new SdeData(
            gates.GroupBy(g => g.SolarSystemId).ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<IntelGate>)[.. g.Select(x => new IntelGate(x.StargateId, x.SolarSystemId, x.Destination, x.X, x.Y, x.Z))]),
            await IntelSdeGroups.LoadAsync(db, ct).ConfigureAwait(false),
            systems.ToDictionary(s => s.SolarSystemId, s => s.Name),
            systems.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                   .ToDictionary(g => g.Key, g => g.First().SolarSystemId, StringComparer.OrdinalIgnoreCase));
    }

    // ── Own characters ───────────────────────────────────────────────────────

    /// <summary>
    /// Every own character that is online, where it is: the newer of ESI's location and the chat
    /// log's last "Channel changed to Local" line. The chat log moves within one tail of the file,
    /// seconds after the jump; ESI's location within its next poll. A Local line newer than the last
    /// online check also counts as online: the character has logged in since.
    /// </summary>
    private async Task<List<OwnPosition>> ReadOwnAsync(AppDbContext db, SdeData sde, DateTimeOffset now, CancellationToken ct)
    {
        await ReadChatPositionsAsync(db, sde, now, ct).ConfigureAwait(false);

        var rows = await MainWindowViewModel.ReadOnlineCharactersAsync(db, ct).ConfigureAwait(false);
        var checks = await db.CharacterStatuses.AsNoTracking()
            .Select(s => new { s.CharacterId, s.LocationCheckedAt, s.OnlineCheckedAt })
            .ToListAsync(ct).ConfigureAwait(false);
        var checkedAt = checks.ToDictionary(c => c.CharacterId);

        var own = new List<OwnPosition>();
        foreach (var r in rows)
        {
            var chat = _chatPositions.TryGetValue(r.CharacterId, out var cp) ? cp : ((int SystemId, DateTimeOffset At)?)null;
            var times = checkedAt.GetValueOrDefault(r.CharacterId);
            var online = r.Online || (chat is { } c && (times?.OnlineCheckedAt is not { } oc || c.At > oc));
            if (!online) continue;

            int system; DateTimeOffset since; OwnPositionSource source;
            var esiAt = times?.LocationCheckedAt ?? DateTimeOffset.MinValue;
            if (chat is { } line && (r.SolarSystemId is null || line.At >= esiAt))
                (system, since, source) = (line.SystemId, line.At, OwnPositionSource.ChatLog);
            else if (r.SolarSystemId is int esi)
            {
                (system, source) = (esi, OwnPositionSource.Esi);
                since = chat is { } same && same.SystemId == esi ? same.At
                      : _ownSince.TryGetValue(r.CharacterId, out var held) && held.SystemId == esi ? held.Since
                      : esiAt == DateTimeOffset.MinValue ? now : esiAt;
            }
            else continue;

            _ownSince[r.CharacterId] = (system, since);
            var hull = r.ShipTypeId is int t && r.Hull is { } h ? SdeNames.Type(t, h) : r.Hull;
            var fromEsi = source == OwnPositionSource.Esi || r.SolarSystemId == system;
            own.Add(new OwnPosition(
                r.CharacterId, r.Name, system, since, source, r.CorporationId, r.AllianceId,
                r.ShipTypeId, hull, r.ShipName,
                // Docked and the place only while ESI agrees on the system: after a jump the chat log
                // knows of, ESI's dock is the old system's.
                fromEsi && r.Docked, fromEsi ? r.Place : null));
        }
        return [.. own.OrderBy(o => o.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>The newest "Channel changed to Local" line per listening character: the last twelve
    /// hours on the first pass, then only lines stored since.</summary>
    private async Task ReadChatPositionsAsync(AppDbContext db, SdeData sde, DateTimeOffset now, CancellationToken ct)
    {
        IQueryable<ChatMessage> q = db.ChatMessages.AsNoTracking()
            .Where(m => m.IsSystemMessage && m.SystemName != null && m.ListenerCharacterId != null);
        if (_chatAfterId < 0)
        {
            // Where the next passes start: the newest line now, whatever it is, so they never go
            // back over the whole table when the last twelve hours held no Local line.
            _chatAfterId = await db.ChatMessages.MaxAsync(m => (int?)m.Id, ct).ConfigureAwait(false) ?? 0;
            var back = IntelLastSeenWriter.Format(now - ChatPositionsBack);
            q = q.Where(m => string.Compare(m.OccurredAt, back) >= 0);
        }
        else
        {
            var after = (int)Math.Min(_chatAfterId, int.MaxValue);
            q = q.Where(m => m.Id > after);
        }

        var lines = await q.Select(m => new { m.Id, m.ListenerCharacterId, m.SystemName, m.OccurredAt })
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var l in lines.OrderBy(l => l.Id))
        {
            if (l.Id > _chatAfterId) _chatAfterId = l.Id;
            if (!sde.SystemIds.TryGetValue(l.SystemName!.Trim(), out var system)) continue;
            if (!IntelSnapshotBuilder.TryParse(l.OccurredAt, out var at)) continue;
            var who = l.ListenerCharacterId!.Value;
            if (!_chatPositions.TryGetValue(who, out var held) || at >= held.At) _chatPositions[who] = (system, at);
        }
    }
}
