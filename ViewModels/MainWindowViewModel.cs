using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Threading;
using EveConsole.Agent;
using EveConsole.Data;
using EveConsole.Api;
using EveConsole.Auth;
using EveConsole.Models;
using EveConsole.Monitoring;
using EveConsole.Localization;
using EveConsole.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;
using Avalonia.Media;

namespace EveConsole.ViewModels;

/// <summary>One line of the online-characters hover in the header.</summary>
public sealed record OnlineCharacterVm(string Name, string Location, string Ship, bool IsDocked);

public class MainWindowViewModel : ReactiveObject
{
    public OverviewViewModel              OverviewVm             { get; }
    public AlertSettingsViewModel         AlertSettingsVm        { get; }
    public CharacterViewModel             CharacterVm            { get; }
    public SdeViewModel                   SdeVm                  { get; }
    public UpdateViewModel                UpdateVm               { get; }

    // ── Which database this window is talking to ──────────────────────────────

    /// <summary>"PostgreSQL" or "SQLite", for the title bar.</summary>
    public string DbEngineLabel => DbEngine.DisplayName;

    /// <summary>Drives the icon's colour, so the two are told apart before the word is read.</summary>
    public bool DbIsPostgres => DbEngine.IsPostgres;

    private string _dbEngineTip = "";
    /// <summary>
    /// Where the data actually lives, on hover.
    ///
    /// <para>⚠️ Refreshed every ten minutes, not filled once. The size is the part worth hovering
    /// for and it is the part that moves — an import, a copy, a month of polling — so a figure fixed
    /// at startup goes on quoting what the database was when the window opened, which on a client
    /// left running for days is simply wrong.</para>
    ///
    /// <para>Still not on hover: a tooltip is no reason to touch the database every time a pointer
    /// crosses it. Ten minutes is often enough to never be far out and rare enough to cost nothing
    /// — the SQLite figure is a file length, the PostgreSQL one a single pg_database_size call.</para>
    /// </summary>
    public string DbEngineTip
    {
        get => _dbEngineTip;
        private set => this.RaiseAndSetIfChanged(ref _dbEngineTip, value);
    }

    private async Task LoadDbEngineTipAsync()
    {
        try
        {
            if (DbEngine.IsPostgres)
            {
                var cs = AppConfig.GetPostgresConnection() ?? "";
                var b  = new Npgsql.NpgsqlConnectionStringBuilder(cs);

                await using var conn = new Npgsql.NpgsqlConnection(AppDb.PostgresConnectionString(cs));
                await conn.OpenAsync();
                await using var cmd = new Npgsql.NpgsqlCommand(
                    "SELECT pg_size_pretty(pg_database_size(current_database()))", conn);
                var size = (await cmd.ExecuteScalarAsync())?.ToString() ?? ShellText.DbSizeUnknown;

                DbEngineTip = string.Format(ShellText.TipDbPostgres, b.Host, b.Database, size);
            }
            else
            {
                var path = AppConfig.GetDbPath();
                var size = File.Exists(path)
                    ? $"{new FileInfo(path).Length / 1024d / 1024d:N0} MB"
                    : ShellText.DbFileNotFound;
                DbEngineTip = string.Format(ShellText.TipDbSqlite, path, size);
            }
        }
        catch (Exception ex)
        {
            // The label still names the engine; only the detail is missing.
            DbEngineTip = string.Format(ShellText.TipDbUnreadable, DbEngine.DisplayName, ex.Message.Split('\n')[0]);
        }
    }

    // ── Which client is doing the background work ─────────────────────────────

    private readonly WorkerLease    _workerLease;
    private readonly AlarmMuteState _mute;
    private readonly DispatcherTimer _workerTimer;

    private string _workerOwner = "…";
    /// <summary>
    /// Who holds the lease, in as few words as the bar has room for: <c>this client</c>, the
    /// other client's host name, or <c>none</c>.
    /// </summary>
    public string WorkerOwner
    {
        get => _workerOwner;
        private set => this.RaiseAndSetIfChanged(ref _workerOwner, value);
    }

    private WorkerOwnership _workerState = WorkerOwnership.Unknown;
    /// <summary>Drives the colour, so "nobody is polling" reads before the word does.</summary>
    public WorkerOwnership WorkerState
    {
        get => _workerState;
        private set => this.RaiseAndSetIfChanged(ref _workerState, value);
    }

    private string _workerTip = ShellText.TipWorkerChecking;
    /// <summary>Host, pid and version of the holder, on hover.</summary>
    public string WorkerTip
    {
        get => _workerTip;
        private set => this.RaiseAndSetIfChanged(ref _workerTip, value);
    }

    /// <summary>⚠️ The lease raises its events from its own loop, never the UI thread.</summary>
    private void OnLeaseChanged() => Dispatcher.UIThread.Post(() => _ = RefreshWorkerAsync());

    /// <summary>
    /// Re-reads who holds the lease.
    ///
    /// <para>⚠️ Polled as well as event-driven. Gained and Lost describe THIS process, which is
    /// only half the question — another client taking over, or dying, changes the answer with
    /// nothing here to notice it.</para>
    /// </summary>
    private async Task RefreshWorkerAsync()
    {
        // One process, no contest, nothing to report but itself.
        if (!DbEngine.IsPostgres)
        {
            WorkerOwner = ShellText.WorkerThisClient;
            WorkerState = WorkerOwnership.Mine;
            WorkerTip   = string.Format(ShellText.TipWorkerSqlite, ShellText.WorkerHeadlineMine,
                                        Environment.MachineName, Environment.ProcessId, AppVersion.Number);
            return;
        }

        var s = await WorkerLease.ReadStatusAsync();

        // ⚠️ IsHolder decides "mine", not a host name match. Two clients on one machine report the
        // same host, and just after a handover the row can still name the previous holder — the
        // lock is the only thing that actually knows.
        if (_workerLease.IsHolder)
        {
            WorkerOwner = ShellText.WorkerThisClient;
            WorkerState = WorkerOwnership.Mine;
            WorkerTip   = s is null
                ? ShellText.WorkerHeadlineMine
                : Describe(ShellText.WorkerHeadlineMine, s);
            return;
        }

        if (s is null)
        {
            WorkerOwner = ShellText.WorkerNone;
            WorkerState = WorkerOwnership.None;
            WorkerTip   = ShellText.TipWorkerUnclaimed;
            return;
        }

        if (!WorkerLease.IsLive(s))
        {
            WorkerOwner = ShellText.WorkerNone;
            WorkerState = WorkerOwnership.None;
            WorkerTip   = Describe(ShellText.WorkerHeadlineStale, s);
            return;
        }

        WorkerOwner = s.HostName;
        WorkerState = WorkerOwnership.Other;
        WorkerTip   = Describe(ShellText.WorkerHeadlineOther, s);
    }

    private static string Describe(string headline, BackgroundWorkerStatus s) =>
        string.Format(ShellText.TipWorkerDetails, headline,
                      s.Headless ? string.Format(ShellText.WorkerHostHeadless, s.HostName) : s.HostName,
                      s.ProcessId, s.Version, s.LeaseTakenUtc.ToLocalTime(),
                      Ago(DateTimeOffset.UtcNow - s.HeartbeatUtc));

    private static string Ago(TimeSpan t) =>
        t < TimeSpan.FromMinutes(1) ? string.Format(ShellText.AgoSeconds, Math.Max(0, (int)t.TotalSeconds))
      : t < TimeSpan.FromHours(1)   ? string.Format(ShellText.AgoMinutes, (int)t.TotalMinutes)
      :                               string.Format(ShellText.AgoHours, (int)t.TotalHours);

    // ── Whether this machine stays quiet for alarms ───────────────────────────

    /// <summary>
    /// Silences the alarm actions that interrupt someone here — sound, dialog, and the agent
    /// speaking — without touching what gets recorded.
    ///
    /// <para>⚠️ Backed by the shared <see cref="AlarmMuteState"/>, not a field of its own. The
    /// Alarms tool has its own button for this, and two copies would let one of them go on saying
    /// "on" after the other muted — with the beacon un-struck and the operator believing they are
    /// silent when they are not.</para>
    /// </summary>
    public bool AlarmsMuted
    {
        get => _mute.Muted;
        set => _mute.Muted = value;
    }

    /// <summary>The action a click would take, for a menu item or a button that toggles.</summary>
    public string AlarmsMuteMenuText => _mute.ToggleText;

    private void OnMuteChanged()
    {
        this.RaisePropertyChanged(nameof(AlarmsMuted));
        this.RaisePropertyChanged(nameof(AlarmsMuteMenuText));
        RefreshAlarmsTip();
    }

    public ApiActivityViewModel           ActivityVm             { get; }
    public EsiExplorerViewModel           ExplorerVm             { get; }
    public ErrorLogViewModel              ErrorLogVm             { get; }
    public AgentUsageViewModel            AgentUsageVm           { get; }
    public GameLogViewerViewModel         GameLogViewerVm        { get; }
    public ChatLogViewerViewModel         ChatLogViewerVm        { get; }
    public AssetBrowserViewModel          AssetBrowserVm         { get; }
    public IndustryBrowserViewModel       IndustryBrowserVm      { get; }
    public CharacterViewerViewModel       CharacterViewerVm      { get; }
    public ItemBrowserViewModel           ItemBrowserVm          { get; }
    public NetWorthViewModel              NetWorthVm             { get; }
    public IncomeExpenseViewModel         IncomeExpenseVm        { get; }
    public TradeOpportunitiesViewModel    TradeOpportunitiesVm   { get; }
    public IndustryOpportunitiesViewModel IndustryOpportunitiesVm { get; }
    public IndyParksViewModel             IndyParksVm            { get; }
    public ProductionCalculatorViewModel  ProductionCalcVm       { get; }
    public PriceOverrideViewModel         PriceOverrideVm        { get; }
    public StructureBrowserViewModel      StructureBrowserVm     { get; }
    public PlanetaryIndustryViewModel     PlanetaryIndustryVm    { get; }
    /// <summary>Planetary Industry's one door to the data, for the Settings window's PI boxes.</summary>
    public EveConsole.Services.Pi.PiService Pi                   { get; }
    public MapToolViewModel                MapVm                  { get; }
    public AlarmsViewModel                AlarmsVm               { get; }
    public SchedulerViewModel             SchedulerVm            { get; }
    public JumpPlannerViewModel           JumpPlannerVm          { get; }
    public AlarmActionRunner              AlarmActions           { get; }
    public WalletViewModel                WalletVm               { get; }
    public ContractsViewModel             ContractsVm            { get; }
    public NotificationsViewModel         NotificationsVm        { get; }
    public MarketViewerViewModel          MarketViewerVm         { get; }
    public SalesTrackerViewModel          SalesTrackerVm         { get; }
    public SaleListingViewModel           SaleListingBuildVm     { get; }
    public SaleListingViewModel           SaleListingMarketVm    { get; }
    public OrderTrackerViewModel          OrderTrackerVm         { get; }
    public StandingBuyOrdersViewModel     StandingBuyOrdersVm    { get; }
    public WorklistViewModel              WorklistVm             { get; }
    public LpMarketValuesViewModel        LpMarketValuesVm       { get; }
    public ItemValuationViewModel         ItemValuationVm        { get; }
    public PlayerEntitiesViewModel        PlayerEntitiesVm       { get; }
    public NpcEntitiesViewModel           NpcEntitiesVm          { get; }
    public MarketSettingsViewModel        MarketVm               { get; }
    public TimerSettingsViewModel         TimerVm                { get; }
    public AgentPanelViewModel            AgentVm                { get; }
    public MarketLevelViewModel           MarketLevelVm          { get; }
    public InvLevelViewModel              InvLevelVm             { get; }
    public SalePostingViewModel           SalePostingVm          { get; }
    public StoresViewModel                StoresVm               { get; }
    public CorpActivityViewModel          CorpActivityVm         { get; }
    public KillmailBrowserViewModel       KillmailBrowserVm      { get; }
    public EveMailViewModel               EveMailVm              { get; }
    public PriceHistorySettingsViewModel  PriceHistorySettingsVm { get; }
    public PollingSettingsViewModel       PollingSettingsVm      { get; }
    public CorpTop10SettingsViewModel     CorpTop10SettingsVm    { get; }
    public SlackSettingsViewModel         SlackSettingsVm        { get; }
    public DiscordSettingsViewModel       DiscordSettingsVm      { get; }
    public GameLogSettingsViewModel       GameLogSettingsVm      { get; }
    public ChatLogSettingsViewModel       ChatLogSettingsVm      { get; }
    public ZkillboardSettingsViewModel    ZkbSettingsVm          { get; }
    public MapStatsSettingsViewModel      MapStatsSettingsVm     { get; }
    public SlackService                   Slack                  { get; }
    public TtsService                     TtsService             { get; }
    public SpeechInputService             SpeechInputService     { get; }
    public GlobalHotkeyService            HotkeyService          { get; }
    public AppPreferencesService          AppPrefs               { get; }
    public DatabaseBackupService          DbBackup               { get; }

    public EveMailService MailSvc { get; }

    private readonly EsiPollingService  _pollingService;
    private readonly BuildCostService   _buildCostService;

    private string _eveTimeText = "";
    public string EveTimeText
    {
        get => _eveTimeText;
        private set => this.RaiseAndSetIfChanged(ref _eveTimeText, value);
    }

    private void StartEveTimeClock()
    {
        EveTimeText = DateTimeOffset.UtcNow.ToString("HH:mm:ss");
        var timer = new System.Timers.Timer(1000) { AutoReset = true };
        timer.Elapsed += (_, _) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => EveTimeText = DateTimeOffset.UtcNow.ToString("HH:mm:ss"));
        timer.Start();
    }

    // ── Alarm light (shown beside the settings gear) ────────────────────────────

    private int _activeAlarmCount;
    public int ActiveAlarmCount
    {
        get => _activeAlarmCount;
        private set => this.RaiseAndSetIfChanged(ref _activeAlarmCount, value);
    }

    private bool _hasActiveAlarms;
    public bool HasActiveAlarms
    {
        get => _hasActiveAlarms;
        private set => this.RaiseAndSetIfChanged(ref _hasActiveAlarms, value);
    }

    private IBrush _alarmLightColor = Palette.SurfaceRaised;
    public IBrush AlarmLightColor
    {
        get => _alarmLightColor;
        private set => this.RaiseAndSetIfChanged(ref _alarmLightColor, value);
    }

    private IBrush _alarmLightRing = Palette.SurfaceRaised;
    public IBrush AlarmLightRing
    {
        get => _alarmLightRing;
        private set => this.RaiseAndSetIfChanged(ref _alarmLightRing, value);
    }

    /// <summary>The gleam on the dome dims with the lamp — a bright highlight on a dark dome
    /// reads as a lit bulb that is not lit.</summary>
    private double _alarmGleamOpacity = 0.18;
    public double AlarmGleamOpacity
    {
        get => _alarmGleamOpacity;
        private set => this.RaiseAndSetIfChanged(ref _alarmGleamOpacity, value);
    }

    private string _alarmsTip = ShellText.TabAlarms;
    public string AlarmsTip
    {
        get => _alarmsTip;
        private set => this.RaiseAndSetIfChanged(ref _alarmsTip, value);
    }

    /// <summary>
    /// The light follows the alarm loop's own armed count, which it republishes on every tick,
    /// so this needs no timer of its own and no query.
    /// </summary>
    /// <summary>
    /// Lights the beacon from whichever client is actually evaluating alarms.
    ///
    /// <para>⚠️ Not from the local service. Alarms are leader-only, so on a client that is not the
    /// worker AlarmService is never started and its ArmedCount stays nought — the beacon went dark
    /// and the badge vanished while the Alarms tab, which relays the worker, correctly said one was
    /// armed. Two readouts of the same fact, disagreeing, and the more prominent one wrong.</para>
    /// </summary>
    private void SetAlarmLight(int count) => Dispatcher.UIThread.Post(() =>
    {
        ActiveAlarmCount = count;
        HasActiveAlarms  = count > 0;

        AlarmLightColor   = count > 0 ? Palette.BadSurface : Palette.SurfaceRaised;
        AlarmLightRing    = count > 0 ? Palette.Bad : Palette.SurfaceRaised;
        AlarmGleamOpacity = count > 0 ? 0.55 : 0.18;

        _armedCount = count;
        RefreshAlarmsTip();
    });

    private void BindAlarmLight(AlarmService alarms, WorkerActivityService activity)
    {
        // This client's own service — right only while this client is the worker.
        alarms.WhenAnyValue(x => x.ArmedCount)
            .Subscribe(count => { if (_workerLease.IsHolder) SetAlarmLight(count); });

        // And the worker's, for when it is somebody else. Pushed on the same signal the Alarms tab
        // uses, so the beacon and the tab cannot disagree about how many are armed.
        activity.Changed += () =>
        {
            if (_workerLease.IsHolder) return;
            if (activity.Get(WorkerActivityService.Alarms)?.Count is { } armed) SetAlarmLight(armed);
        };
    }

    private int _armedCount;

    /// <summary>
    /// ⚠️ Armed and audible are different questions, and the tooltip answers both. Muted alarms
    /// stay armed and go on being recorded, so the count alone would let somebody read "3 armed"
    /// off a machine that will not make a sound about any of them.
    /// </summary>
    private void RefreshAlarmsTip()
    {
        var armed = _armedCount == 0
            ? ShellText.AlarmsNoneArmed
            : Plurals.Format(ShellText.ResourceManager, nameof(ShellText.AlarmsArmedOther), _armedCount);

        AlarmsTip = armed + "\n\n" + (AlarmsMuted ? ShellText.AlarmsMutedNote : ShellText.AlarmsMuteHint);
    }

    // ── My characters online (shown beside the EVE clock) ───────────────────────

    private string _onlineCharactersText = "";
    public string OnlineCharactersText
    {
        get => _onlineCharactersText;
        private set => this.RaiseAndSetIfChanged(ref _onlineCharactersText, value);
    }

    /// <summary>
    /// Who is online, for the hover: one row each, replaced wholesale on every refresh so the
    /// tooltip's columns re-measure together. Docked rows read green and in-space rows orange
    /// in the view, because that is the one thing worth seeing at a glance.
    /// </summary>
    private IReadOnlyList<OnlineCharacterVm> _onlineCharacters = [];
    public IReadOnlyList<OnlineCharacterVm> OnlineCharacters
    {
        get => _onlineCharacters;
        private set
        {
            this.RaiseAndSetIfChanged(ref _onlineCharacters, value);
            this.RaisePropertyChanged(nameof(HasOnlineCharacters));
        }
    }

    public bool HasOnlineCharacters => OnlineCharacters.Count > 0;

    /// <summary>Green while anyone is online, grey otherwise — same convention as the TQ dot.</summary>
    private IBrush _onlineCharactersColor = Palette.BorderStrong;
    public IBrush OnlineCharactersColor
    {
        get => _onlineCharactersColor;
        private set => this.RaiseAndSetIfChanged(ref _onlineCharactersColor, value);
    }

    /// <summary>
    /// Reads the online/location/ship state the poller keeps in CharacterStatuses. On its own
    /// timer rather than the clock's, because it costs a query — and off the UI thread, since
    /// SQLite has no real async I/O and awaiting it here would freeze the window.
    /// </summary>
    private void StartOnlineCharactersWatch(IDbContextFactory<AppDbContext> dbFactory, AppErrorLogger errorLogger)
    {
        _ = RefreshOnlineCharactersAsync(dbFactory, errorLogger);

        var timer = new System.Timers.Timer(TimeSpan.FromSeconds(30)) { AutoReset = true };
        timer.Elapsed += (_, _) => _ = RefreshOnlineCharactersAsync(dbFactory, errorLogger);
        timer.Start();
    }

    /// <summary>The last failure logged, so one that repeats every thirty seconds is written once.</summary>
    private string? _onlineCharactersError;

    /// <summary>One of your characters as the header reads it: online or not, and for one who
    /// is, where and in what — any of which a poll may not have filled in yet. The names are the
    /// English; the ids are for naming them in the interface language.</summary>
    internal sealed record OnlineCharacterRow(
        string Name, bool Online, bool Docked, string? System, string? Place, string? Hull, string? ShipName,
        int? SolarSystemId = null, long? StationId = null, int? ShipTypeId = null, long CharacterId = 0,
        long CorporationId = 0, long AllianceId = 0);

    /// <summary>
    /// Every character with a status row, with names looked up for the ones online.
    ///
    /// <para>⚠️ A plain join and then a lookup per name, never correlated left joins. Written as
    /// "from x in table.Where(matches s).DefaultIfEmpty()" once per name, this needed SQL's
    /// APPLY, which SQLite does not have: on every SQLite database the query threw, the catch in
    /// the caller swallowed it, and the header showed a dot with no words beside it. Only
    /// PostgreSQL, which has APPLY, ever saw it work.</para>
    /// </summary>
    internal static async Task<List<OnlineCharacterRow>> ReadOnlineCharactersAsync(
        AppDbContext db, CancellationToken ct = default)
    {
        var statuses = await (
            from s in db.CharacterStatuses.AsNoTracking()
            join c in db.Characters.AsNoTracking() on s.CharacterId equals c.Id
            select new
            {
                c.Id, c.Name, s.Online, s.SolarSystemId, s.StationId, s.StructureId, s.ShipTypeId, s.ShipName,
                c.CorporationId, c.AllianceId,
            }).ToListAsync(ct);

        // Names only for who is online — nothing else is shown. A character who has just logged
        // in may not have had a location or ship poll yet, and is still counted.
        var here = statuses.Where(s => s.Online).ToList();
        var systemIds    = here.Where(s => s.SolarSystemId is not null).Select(s => s.SolarSystemId!.Value).Distinct().ToList();
        var shipIds      = here.Where(s => s.ShipTypeId is not null).Select(s => s.ShipTypeId!.Value).Distinct().ToList();
        var stationIds   = here.Where(s => s.StationId is not null).Select(s => (int)s.StationId!.Value).Distinct().ToList();
        var structureIds = here.Where(s => s.StructureId is not null).Select(s => s.StructureId!.Value).Distinct().ToList();

        var systems  = await db.SdeSolarSystems.AsNoTracking().Where(x => systemIds.Contains(x.SolarSystemId))
            .ToDictionaryAsync(x => x.SolarSystemId, x => x.Name, ct);
        var ships    = await db.SdeTypes.AsNoTracking().Where(x => shipIds.Contains(x.TypeId))
            .ToDictionaryAsync(x => x.TypeId, x => x.Name, ct);
        var stations = await db.SdeStations.AsNoTracking().Where(x => stationIds.Contains(x.StationId))
            .ToDictionaryAsync(x => (long)x.StationId, x => x.Name, ct);

        // The docked place from every table that names one — three for player structures — so a
        // pilot in a Keepstar reads as being in it rather than merely in its system. The first
        // table to name it wins, in the order these were always read.
        var structures = new Dictionary<long, string>();
        void Name(IEnumerable<(long Id, string Name)> found)
        {
            foreach (var (id, name) in found)
                if (!string.IsNullOrEmpty(name)) structures.TryAdd(id, name);
        }
        if (structureIds.Count > 0)
        {
            Name((await db.Structures.AsNoTracking().Where(x => structureIds.Contains(x.StructureId))
                .Select(x => new { x.StructureId, x.Name }).ToListAsync(ct)).Select(x => (x.StructureId, x.Name)));
            Name((await db.EsiStructureNames.AsNoTracking().Where(x => structureIds.Contains(x.StructureId))
                .Select(x => new { x.StructureId, x.Name }).ToListAsync(ct)).Select(x => (x.StructureId, x.Name)));
            Name((await db.EsiCorpStructures.AsNoTracking().Where(x => structureIds.Contains(x.StructureId))
                .Select(x => new { x.StructureId, x.Name }).ToListAsync(ct)).Select(x => (x.StructureId, x.Name)));
        }

        return statuses.Select(s => new OnlineCharacterRow(
            s.Name,
            s.Online,
            Docked: s.StationId != null || s.StructureId != null,
            System: s.SolarSystemId is int sys ? systems.GetValueOrDefault(sys) : null,
            Place:  s.StationId is long sta && stations.TryGetValue(sta, out var station) ? station
                  : s.StructureId is long str ? structures.GetValueOrDefault(str) : null,
            Hull:   s.ShipTypeId is int hull ? ships.GetValueOrDefault(hull) : null,
            s.ShipName,
            SolarSystemId: s.SolarSystemId, StationId: s.StationId, ShipTypeId: s.ShipTypeId, CharacterId: s.Id,
            CorporationId: s.CorporationId, AllianceId: s.AllianceId ?? 0)).ToList();
    }

    private async Task RefreshOnlineCharactersAsync(IDbContextFactory<AppDbContext> dbFactory, AppErrorLogger errorLogger)
    {
        try
        {
            // Off the UI thread: SQLite has no real async I/O, and awaiting it here would freeze
            // the window.
            var rows = await Task.Run(async () =>
            {
                await using var db = await dbFactory.CreateDbContextAsync();
                return await ReadOnlineCharactersAsync(db);
            });

            var online = rows.Where(r => r.Online).OrderBy(r => r.Name).ToList();

            // Nobody on is worth saying in words: "0 of 24 Online" makes the reader do the sum.
            var text = online.Count > 0 ? string.Format(ShellText.OnlineOfTotal, online.Count, rows.Count) : ShellText.NoCharactersOnline;

            var list = online.Select(r =>
            {
                // The system, an NPC station and the hull in the interface language. A player
                // structure stays as its owner named it.
                var system = string.IsNullOrWhiteSpace(r.System) ? ShellText.LocationUnknown
                           : SdeNames.SolarSystem(r.SolarSystemId ?? 0, r.System);

                // Docked: the station or structure, which says more than its system does. In
                // space: the system, which is all there is to say.
                var where = !r.Docked                              ? system
                          : !string.IsNullOrWhiteSpace(r.Place)    ? (r.StationId is { } station ? SdeNames.Station(station, r.Place) : r.Place)
                          :                                          string.Format(ShellText.StructureInSystem, system);

                // The hull is what the ship IS; ShipName is what the pilot called it. Show
                // both only when the pilot bothered to rename it — judged on the English hull,
                // never on the translated one shown.
                var hull = string.IsNullOrWhiteSpace(r.Hull) ? r.Hull : SdeNames.Type(r.ShipTypeId ?? 0, r.Hull);
                var ship = string.IsNullOrWhiteSpace(hull) ? ShellText.ShipUnknown : hull;
                if (!string.IsNullOrWhiteSpace(r.ShipName)
                    && !string.Equals(r.ShipName, r.Hull, StringComparison.OrdinalIgnoreCase))
                    ship = $"{hull} \"{r.ShipName}\"";

                return new OnlineCharacterVm(r.Name, where, ship, r.Docked);
            }).ToList();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                OnlineCharactersText  = text;
                OnlineCharacters      = list;
                OnlineCharactersColor = online.Count > 0 ? Palette.Good : Palette.BorderStrong;
            });
            _onlineCharactersError = null;
        }
        catch (Exception ex)
        {
            // A header ornament must never be the thing that takes the window down — but it must
            // not fail in silence either: this one was broken on SQLite with nothing to show for
            // it. Logged once per distinct failure, not every thirty seconds.
            if (ex.Message != _onlineCharactersError)
            {
                _onlineCharactersError = ex.Message;
                errorLogger.Log(nameof(MainWindowViewModel), "online characters", ex);
            }
        }
    }

    /// <summary>Where clicking the EVE clock goes. Read at click time rather than cached,
    /// so a change in Settings takes effect without reopening the window.</summary>
    private UiLinkSettings? _uiLinks;

    /// <summary>Held here because SettingsViewModel is built by hand when the window is
    /// opened rather than resolved from DI.</summary>
    public OtherSettingsViewModel OtherSettingsVm { get; private set; } = null!;
    public DataRetentionSettingsViewModel DataRetentionVm { get; private set; } = null!;

    public string EveTimeUrl    => _uiLinks?.EveTimeUrl ?? UiLinkSettings.EveOnlineTimeUrl;
    public string EveTimeLinkTip => string.Format(ShellText.TipEveTimeLink, EveTimeUrl);

    // ── Theme (shown on the title bar, beside the alarm beacon) ─────────────────

    /// <summary>
    /// The theme's name, for the label that also picks it.
    ///
    /// <para>Read from ThemeService rather than stored, so the bar and the Settings window can
    /// never disagree about what is on — either can change it, and both follow the event.</para>
    /// </summary>
    public string ThemeName => ThemeService.All
        .FirstOrDefault(t => t.Key == ThemeService.Current)?.Name ?? ShellText.ThemeFallback;

    public string ThemeTip => string.Format(ShellText.TipTheme, ThemeName);

    private void OnThemeChanged()
    {
        this.RaisePropertyChanged(nameof(ThemeName));
        this.RaisePropertyChanged(nameof(ThemeTip));
    }

    /// <summary>The UI scale as the status bar shows it, "100%", read from the service like the
    /// theme so the bar and the Settings window can never disagree.</summary>
    public string UiScaleName => UiScaleService.Label;

    public string UiScaleTip => string.Format(ShellText.TipUiScale, UiScaleName);

    private void OnUiScaleChanged()
    {
        this.RaisePropertyChanged(nameof(UiScaleName));
        this.RaisePropertyChanged(nameof(UiScaleTip));
    }

    // ── Tranquility status (shown beside the EVE clock) ─────────────────────────

    private string _serverStatusText = ShellText.ServerOnline;
    public string ServerStatusText { get => _serverStatusText; private set => this.RaiseAndSetIfChanged(ref _serverStatusText, value); }

    private IBrush _serverStatusColor = Palette.Good;
    public IBrush ServerStatusColor { get => _serverStatusColor; private set => this.RaiseAndSetIfChanged(ref _serverStatusColor, value); }

    private string _serverPlayersText = "";
    public string ServerPlayersText { get => _serverPlayersText; private set => this.RaiseAndSetIfChanged(ref _serverPlayersText, value); }

    private string _serverStatusTip = ShellText.TipServerStatus;
    public string ServerStatusTip { get => _serverStatusTip; private set => this.RaiseAndSetIfChanged(ref _serverStatusTip, value); }

    /// <summary>Mirrors EveServerStatusService onto the UI thread. The service raises
    /// changes from its own polling task, so everything here is marshalled explicitly.</summary>
    private void BindServerStatus(EveServerStatusService status)
    {
        void Apply() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            ServerStatusText   = status.StatusText;
            ServerStatusColor  = status.StatusColor;
            ServerPlayersText  = status.PlayersText;
            ServerStatusTip    = !status.IsOnline ? ShellText.TipTranquilityOffline
                               : status.Players > 0
                                   ? Plurals.Format(ShellText.ResourceManager, nameof(ShellText.TipTranquilityOnlinePlayersOther), status.Players)
                                   : ShellText.TipTranquilityOnline;
        });

        status.PropertyChanged += (_, _) => Apply();
        Apply();
    }


    private string _buildCostStatusText = ShellText.BuildCostsNotYet;
    public string BuildCostStatusText
    {
        get => _buildCostStatusText;
        private set => this.RaiseAndSetIfChanged(ref _buildCostStatusText, value);
    }

    // ── Navigation ────────────────────────────────────────────────────────────

    public IReadOnlyList<NavGroup>       NavGroups { get; }
    public ObservableCollection<ToolTab> OpenTabs  { get; } = new();

    private readonly NavItem[] _allNavItems;

    private ToolTab? _selectedTab;
    public ToolTab? SelectedTab
    {
        get => _selectedTab;
        set => this.RaiseAndSetIfChanged(ref _selectedTab, value);
    }

    public ReactiveCommand<string,  Unit> OpenToolCommand { get; }
    public ReactiveCommand<ToolTab, Unit> CloseTabCommand { get; }

    public void OpenTool(string toolId)
    {
        // Tools of their own once, tabs of the map tool now: a saved tab, the agent and old links
        // still name them.
        if (toolId is "jump_planner" or "route_planner")
        {
            OpenTool("universe");
            if (toolId == "jump_planner") MapVm.ShowJumpPlannerTab();
            else                          MapVm.ShowRouteTab();
            return;
        }

        var existing = OpenTabs.FirstOrDefault(t => t.Id == toolId);
        if (existing is not null)
        {
            SelectedTab = existing;
            // Returning to an already-open tab has to refresh too, or an alarm that fired while
            // the tab sat in the background shows nothing until something else triggers a load.
            if (toolId == "alarms")    _ = AlarmsVm.LoadAsync();
            if (toolId == "scheduler") _ = SchedulerVm.LoadAsync();

            // ⚠️ The error log is NOT refreshed here. Returning to a tab already open should not
            // re-read five thousand rows; the list is as old as the moment it was opened, and the
            // Refresh button says so.
            return;
        }

        var (title, vm, canClose) = toolId switch
        {
            "overview"   => (ShellText.NavOverview,       (object)OverviewVm,       false),
            "characters" => (ShellText.NavCharacters,      CharacterViewerVm,        true),
            "assets"     => (ShellText.NavAssets,          AssetBrowserVm,           true),
            "items"      => (ShellText.NavItemBrowser,    ItemBrowserVm,            true),
            "industry"   => (ShellText.NavIndustryJobs,   IndustryBrowserVm,        true),
            "indy_parks" => (ShellText.NavIndyParks,      IndyParksVm,              true),
            "prod_calc"  => (ShellText.NavProductionCalc, ProductionCalcVm,         true),
            "price_overrides" => (ShellText.NavPriceOverrides, PriceOverrideVm,     true),
            "structure_browser" => (ShellText.NavStructureBrowser, StructureBrowserVm, true),
            "planetary_industry" => (ShellText.NavPlanetaryIndustry, PlanetaryIndustryVm, true),
            "universe"        => (ShellText.TabUniverse,        MapVm,             true),
            "alarms"          => (ShellText.TabAlarms,          AlarmsVm,          true),
            "scheduler"       => (ShellText.TabScheduler,       SchedulerVm,       true),
            "trade"           => (ShellText.TabTrade,           TradeOpportunitiesVm,     true),
            "industry_opps"   => (ShellText.TabIndustryOpps,   IndustryOpportunitiesVm,  true),
            "market_levels"   => (ShellText.NavMarketLevels,   MarketLevelVm,            true),
            "inv_levels"      => (ShellText.TabInvLevels,     InvLevelVm,               true),
            "sale_posting"    => (ShellText.NavSalePosting,    SalePostingVm,            true),
            "stores"          => (ShellText.NavStores,          StoresVm,                 true),
            "net_worth"  => (ShellText.NavNetWorth,       NetWorthVm,               true),
            "income_expense" => (ShellText.NavIncomeExpense, IncomeExpenseVm,     true),
            "wallet"         => (ShellText.NavWallet,          WalletVm,          true),
            "contracts"      => (ShellText.NavContracts,       ContractsVm,       true),
            "market_viewer"  => (ShellText.NavMarketOverview, MarketViewerVm,    true),
            "sales_tracker"  => (ShellText.NavSalesTracker,   SalesTrackerVm,    true),
            "sale_list_build"  => (ShellText.TabSaleListingBuild,  SaleListingBuildVm,  true),
            "sale_list_market" => (ShellText.TabSaleListingMarket, SaleListingMarketVm, true),
            "order_tracker"  => (ShellText.NavOrderTracker,   OrderTrackerVm,    true),
            "standing_buy_orders" => (ShellText.NavStandingBuyOrders, StandingBuyOrdersVm, true),
            "worklist"       => (ShellText.NavWorklist,       WorklistVm,        true),
            "lp_market_values" => (ShellText.NavLpMarketValues, LpMarketValuesVm, true),
            "item_valuation"   => (ShellText.NavItemValuation,   ItemValuationVm,  true),
            "player_entities"  => (ShellText.NavPlayerEntities, PlayerEntitiesVm, true),
            "npc_entities"     => (ShellText.NavNpcEntities,    NpcEntitiesVm,    true),
            "corp_activity"  => (ShellText.NavCorpActivity,  CorpActivityVm,    true),
            "killmails"      => (ShellText.NavKillmails,      KillmailBrowserVm, true),
            "eve_mail"       => (ShellText.NavEveMail,       EveMailVm,         true),
            "notifications"  => (ShellText.NavNotifications,  NotificationsVm,   true),
            "background"     => (ShellText.NavBackgroundProcesses, ActivityVm,  true),
            "data"           => (ShellText.NavEsiExplorer,   ExplorerVm,        true),
            "error_log"      => (ShellText.NavErrorLog,      ErrorLogVm,        true),
            "ai_usage"       => (ShellText.NavAiUsage,       AgentUsageVm,      true),
            "game_log"       => (ShellText.NavGameLog,       GameLogViewerVm,   true),
            "chat_log"       => (ShellText.NavChatLog,       ChatLogViewerVm,   true),
            _                => throw new ArgumentException($"Unknown tool: {toolId}")
        };


        // From here the tool is on screen, so its own refresh timer is allowed to run. A latch,
        // not a visibility check — see IPeriodicRefresh.
        if (vm is IPeriodicRefresh periodic) periodic.AutoRefreshEnabled = true;
        var tab = new ToolTab(toolId, title, vm, canClose);
        OpenTabs.Add(tab);
        SelectedTab = tab;

        // Loaded on open rather than at construction — nothing else needs the alarm list, and
        // a fresh read also picks up anything the agent created since the tab was last shown.
        if (toolId == "alarms")    _ = AlarmsVm.LoadAsync();
        if (toolId == "scheduler") _ = SchedulerVm.LoadAsync();

        // ⚠️ Same reasoning, and it mattered more here. The error log used to read at application
        // start, so opening it showed a list from whenever the app was launched — which looks
        // current and is not. Reading on open means closing the tab and opening it again reads
        // afresh, which is what somebody doing that is asking for.
        if (toolId == "error_log") ErrorLogVm.Reload();
        if (toolId == "ai_usage")  AgentUsageVm.Reload();
        if (toolId == "planetary_industry") _ = PlanetaryIndustryVm.RefreshAsync();

        var navItem = _allNavItems.FirstOrDefault(i => i.ToolId == toolId);
        if (navItem is not null) navItem.IsOpen = true;
    }

    /// <summary>Opens the Background Processes tool at the tab named — what a status-bar label does.
    /// The ask goes to the view model first, so a view already showing acts on it at once and one
    /// built by the open finds it waiting.</summary>
    public void OpenBackgroundProcesses(string tab)
    {
        ActivityVm.RequestedTab = tab;
        OpenTool("background");
    }

    /// <summary>
    /// Distinguishes agent-opened tabs, which are many, from tools, which are one each.
    /// </summary>
    public const string AgentTabPrefix = "agent_output:";

    private int _agentTabCounter;

    /// <summary>
    /// Opens a tab holding something the agent produced, and selects it.
    ///
    /// <para>⚠️ Separate from OpenTool, and deliberately so. OpenTool maps a fixed id to a
    /// singleton view model — asking for the Worklist twice returns to the one Worklist. These
    /// are answers to particular questions, so every call gets an id of its own and a tab of its
    /// own; the previous answer stays open beside it.</para>
    ///
    /// <para>They are reachable ONLY this way. There is no nav entry, because there is nothing to
    /// open — an empty one of these would have no content and no reason to exist.</para>
    ///
    /// <para>⚠️ Marshalled to the UI thread by the caller's Dispatcher.Invoke. Agent tools run on
    /// a background thread and OpenTabs is bound to the tab strip.</para>
    /// </summary>
    public string OpenAgentTab(string title, object viewModel)
    {
        var id  = AgentTabPrefix + Interlocked.Increment(ref _agentTabCounter);
        var tab = new ToolTab(id, title, viewModel, canClose: true);
        OpenTabs.Add(tab);
        SelectedTab = tab;
        return id;
    }

    public void CloseTab(ToolTab tab)
    {
        if (!tab.CanClose) return;
        bool wasSelected = SelectedTab == tab;
        OpenTabs.Remove(tab);

        var navItem = _allNavItems.FirstOrDefault(i => i.ToolId == tab.Id);
        if (navItem is not null) navItem.IsOpen = false;

        if (wasSelected)
            SelectedTab = OpenTabs.FirstOrDefault(t => t.Id == "overview") ?? OpenTabs.FirstOrDefault();
    }

    // Called when a tab is detached into a floating window — removes it from the
    // strip but keeps the nav-item dot lit (the tool is still "open").
    public void MarkToolDetached(string toolId)
    {
        var tab = OpenTabs.FirstOrDefault(t => t.Id == toolId);
        if (tab is not null)
        {
            bool wasSelected = SelectedTab == tab;
            OpenTabs.Remove(tab);
            if (wasSelected)
                SelectedTab = OpenTabs.FirstOrDefault(t => t.Id == "overview") ?? OpenTabs.FirstOrDefault();
        }
        var navItem = _allNavItems.FirstOrDefault(i => i.ToolId == toolId);
        if (navItem is not null) navItem.IsOpen = true;
    }

    // Called when a detached window closes — extinguishes the nav-item dot.
    public void MarkToolReattached(string toolId)
    {
        var navItem = _allNavItems.FirstOrDefault(i => i.ToolId == toolId);
        if (navItem is not null) navItem.IsOpen = false;
    }

    // ── Constructor ───────────────────────────────────────────────────────────

    public MainWindowViewModel(
        EsiAuthService                  auth,
        EsiClient                       esi,
        IDbContextFactory<AppDbContext> dbFactory,
        SdeImportService                sdeService,
        HoboImportService               hoboService,
        EsiPollingService               pollingService,
        WorkerLease                     workerLease,
        WorkerActivityService           workerActivity,
        ApiActivityViewModel            activityVm,
        AlarmMuteState                  alarmMute,
        ApiActivityLog                  activityLog,
        MarketPricingService            marketPricing,
        MarketLevelService              marketLevelService,
        InvLevelService                 invLevelService,
        SalePostingService              salePostingService,
        StoreMailService                storeMailService,
        EveConsole.Services.WebStore.WebStoreSyncService webStoreSync,
        EveConsole.Services.WebStore.CloudflareDeployService cloudflareDeploy,

        OrderLabelService               orderLabels,
        BatchAddService                 batchAddService,
        CorpActivityService             corpActivityService,
        CharacterSummaryService         characterSummaryService,
        StandingBuyOrderService         standingBuyOrderService,
        EveConsole.Services.Worklist.WorklistService worklistService,
        EveConsole.Services.Worklist.WorklistMarketAltService worklistMarketAltService,
        EveConsole.Services.Worklist.WorklistCorpAltService worklistCorpAltService,
        EveConsole.Services.Worklist.IndustryAssignmentService industryAssignmentService,
        EveConsole.Services.Worklist.IndustryBlueprintService  industryBlueprintService,
        EveConsole.Services.Worklist.WorklistSettings worklistSettings,
        IndyFacilityCheckService        indyFacilityCheck,
        IndyStructureLinkService        indyStructureLink,
        IndyBulkAddService              indyBulkAdd,
        KillmailBrowserService          killmailBrowserService,
        BuildCostService                buildCostService,
        ProductionCalculatorService     prodCalcService,
        IServiceScopeFactory            scopeFactory,
        TimerSettingsService            timerSettings,
        TimerForceService               timerForce,
        AgentService                    agentService,
        AppErrorLogger                  errorLogger,
        KillMailService                 killMailService,
        EveMailService                  eveMailService,
        TtsService                      ttsService,
        SpeechInputService              speechInputService,
        GlobalHotkeyService             hotkeyService,
        NewsService                     newsService,
        AppPreferencesService           appPrefs,
        DatabaseBackupService           dbBackup,
        CorpTop10ExcludeService         corpTop10Exclude,
        CorpReportTitles                 corpReportTitles,
        MarketHistoryService            historyService,
        ContractsService                contractsService,
        SlackService                    slackService,
        DiscordService                  discordService,
        MonitoringSettings              monitoringSettings,
        GameLogImportService            gameLogImport,
        ChatLogImportService            chatLogImport,
        IntelService                    intelService,
        ZkillboardSettings              zkillboardSettings,
        ZkillboardPollingService        zkbPolling,
        ZkillboardFirehoseService       zkbFirehose,
        ZkillboardBackfillService       zkbBackfill,
        MapStatsSettings                mapStatsSettings,
        MapStatsBackfillService         mapStatsBackfill,
        MapStatsPollingService          mapStatsPolling,
        MapStatsService                 mapStatsService,
        SystemViewService               systemViewService,
        ZkillboardPostService           zkbPost,
        EntityNameBackfillService       entityNames,
        EveServerStatusService          serverStatus,
        UiLinkSettings                  uiLinks,
        DataRetentionService        dataRetention,
        OrderFulfilmentService      orderFulfilment,
        ExportFormatSettings            exportFormat,
        AlarmService                    alarmService,
        AlarmSoundService               alarmSounds,
        AlarmActionRunner               alarmActions,
        JumpPlannerService              jumpPlanner,
        LpStoreService                  lpStoreService,
        LpValueService                  lpValueService,
        SchedulerService                schedulerService,
        ScheduledBlockRenderer          blockRenderer,
        EveScoutService                 eveScout,
        SystemGraph                     systemGraph,
        SovCampaignService              sovCampaigns,
        EveConsole.Services.Pi.PiService piService)
    {
        AlarmActions = alarmActions;
        _uiLinks        = uiLinks;
        OtherSettingsVm = new OtherSettingsViewModel(uiLinks);
        DataRetentionVm = new DataRetentionSettingsViewModel(dataRetention);
        BindServerStatus(serverStatus);
        ThemeService.Changed   += OnThemeChanged;
        UiScaleService.Changed += OnUiScaleChanged;

        Slack             = slackService;
        SlackSettingsVm   = new SlackSettingsViewModel(slackService);
        DiscordSettingsVm = new DiscordSettingsViewModel(discordService);
        GameLogSettingsVm = new GameLogSettingsViewModel(monitoringSettings, gameLogImport);
        ChatLogSettingsVm = new ChatLogSettingsViewModel(monitoringSettings, chatLogImport, intelService);
        ZkbSettingsVm     = new ZkillboardSettingsViewModel(zkillboardSettings, zkbPolling, zkbFirehose, zkbBackfill, zkbPost);
        MapStatsSettingsVm = new MapStatsSettingsViewModel(mapStatsSettings, mapStatsBackfill, mapStatsPolling, mapStatsService, eveScout);
        AlertSettingsVm   = new AlertSettingsViewModel(dbFactory);
        OverviewVm        = new OverviewViewModel(dbFactory.CreateDbContext(), AlertSettingsVm, errorLogger, newsService, appPrefs, corpActivityService, dbFactory, esi, standingBuyOrderService, indyFacilityCheck);
        CharacterVm       = new CharacterViewModel(auth, esi, dbFactory.CreateDbContext(), errorLogger);
        SdeVm             = new SdeViewModel(sdeService, hoboService, dbFactory.CreateDbContext());

        // Not awaited: the engine name is right immediately, and only the hover detail is late.
        _ = LoadDbEngineTipAsync();

        // And kept current, because the size in it grows. See DbEngineTip for why ten minutes
        // rather than on hover.
        Observable.Interval(TimeSpan.FromMinutes(10))
            .ObserveOnUi("Db.TipRefresh")
            .Subscribe(tick => { _ = LoadDbEngineTipAsync(); });

        _mute            = alarmMute;
        alarmMute.Changed += OnMuteChanged;

        // Who is doing the background work, now and as it changes.
        _workerLease        = workerLease;
        workerLease.Gained += OnLeaseChanged;
        workerLease.Lost   += OnLeaseChanged;
        _ = RefreshWorkerAsync();

        // ⚠️ Polled as well as event-driven, at the lease's own tick, because the events say
        // nothing about what another client did.
        _workerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _workerTimer.Tick += (_, _) => _ = RefreshWorkerAsync();
        _workerTimer.Start();
        ActivityVm        = activityVm;
        CharacterViewerVm = new CharacterViewerViewModel(dbFactory.CreateDbContext(), CharacterVm.Characters,
            characterSummaryService);
        NetWorthVm        = new NetWorthViewModel(dbFactory);
        IncomeExpenseVm   = new IncomeExpenseViewModel(dbFactory, errorLogger);
        MarketVm          = new MarketSettingsViewModel(dbFactory.CreateDbContext(), dbFactory, marketPricing, esi, CharacterVm.Characters, buildCostService);
        var fittingsService = new FittingsService(esi, dbFactory);
        MarketLevelVm     = new MarketLevelViewModel(marketLevelService, dbFactory, fittingsService,
            CharacterVm.Characters, CharacterVm.Corporations, batchAddService, prodCalcService);
        // appPrefs is the constructor parameter, not the AppPrefs property — that is not assigned
        // until far below this line, and passing it here handed the view model a null.
        InvLevelVm        = new InvLevelViewModel(invLevelService, dbFactory, appPrefs,
            batchAddService, prodCalcService, fittingsService,
            CharacterVm.Characters, CharacterVm.Corporations);
        SalePostingVm     = new SalePostingViewModel(salePostingService, dbFactory, batchAddService, slackService, exportFormat, discordService);
        StoresVm          = new StoresViewModel(dbFactory, salePostingService, storeMailService, orderLabels, errorLogger, webStoreSync, workerLease, cloudflareDeploy);

        CorpActivityVm    = new CorpActivityViewModel(corpActivityService, CharacterVm.Corporations, corpTop10Exclude, corpReportTitles, slackService, exportFormat, errorLogger, discordService);
        KillmailBrowserVm = new KillmailBrowserViewModel(killmailBrowserService);
        MailSvc           = eveMailService;
        EveMailVm         = new EveMailViewModel(eveMailService, CharacterVm.Characters);
        CorpActivityVm.RequestOpenKillmail = killMailId =>
        {
            OpenTool("killmails");
            KillmailBrowserVm.SelectById(killMailId);
        };

        OverviewVm.NavigateToCharacterSkills = characterName =>
        {
            OpenTool("characters");
            CharacterViewerVm.ShowSkillsFor(characterName);
        };
        OverviewVm.NavigateToStandingProjects = () =>
        {
            OpenTool("corp_activity");
            CorpActivityVm.ShowStandingProjectsTab();
        };
        OverviewVm.NavigateToStandingBuyOrders = () => OpenTool("standing_buy_orders");
        OverviewVm.NavigateToIndustryJobs      = () => OpenTool("industry");
        // ContractsVm is built further down; the lambda runs long after, so the flow analysis is the only thing objecting.
        OverviewVm.NavigateToActiveContracts   = () => { OpenTool("contracts"); ContractsVm!.ShowActivePersonal(); };
        OverviewVm.NavigateToOrderTracker      = () => OpenTool("order_tracker");
        OverviewVm.RequestOpenKillmail = killMailId =>
        {
            OpenTool("killmails");
            KillmailBrowserVm.SelectById(killMailId);
        };
        OverviewVm.OpenToolRequested = OpenTool;
        TimerVm           = new TimerSettingsViewModel(pollingService, timerSettings, timerForce);
        _pollingService   = pollingService;
        _buildCostService = buildCostService;

        PriceHistorySettingsVm = new PriceHistorySettingsViewModel(dbFactory.CreateDbContext());
        PollingSettingsVm      = new PollingSettingsViewModel(appPrefs);
        CorpTop10SettingsVm    = new CorpTop10SettingsViewModel(corpTop10Exclude, corpReportTitles);
        ItemBrowserVm          = new ItemBrowserViewModel(dbFactory.CreateDbContext(), historyService, dbFactory, appPrefs);
        IndyParksVm            = new IndyParksViewModel(dbFactory, corpActivityService, errorLogger,
                                                        indyStructureLink, indyBulkAdd, pollingService);
        WalletVm               = new WalletViewModel(dbFactory, errorLogger);
        ContractsVm            = new ContractsViewModel(dbFactory, esi, errorLogger, contractsService);
        NotificationsVm        = new NotificationsViewModel(dbFactory, esi, errorLogger);
        MarketViewerVm         = new MarketViewerViewModel(dbFactory, errorLogger);
        SalesTrackerVm         = new SalesTrackerViewModel(dbFactory, errorLogger, corpActivityService, orderLabels);
        SaleListingBuildVm     = new SaleListingViewModel(dbFactory, errorLogger, corpActivityService, SaleCostBasis.BuildCost);
        SaleListingMarketVm    = new SaleListingViewModel(dbFactory, errorLogger, corpActivityService, SaleCostBasis.MarketValue);
        OverviewVm.SaleListingBuild  = SaleListingBuildVm;   // let the Overview embed them as sections
        OverviewVm.SaleListingMarket = SaleListingMarketVm;
        OverviewVm.IncomeExpense     = IncomeExpenseVm;
        SaleListingBuildVm.OpenSalesTracker  = () => OpenTool("sales_tracker");
        SaleListingMarketVm.OpenSalesTracker = () => OpenTool("sales_tracker");
        // Built here rather than further down: the order tracker's buyer picker needs it, and a
        // service constructed after its first consumer is a null nobody notices until the box is
        // typed into.
        var entityBrowser      = new EntityBrowserService(dbFactory, esi);

        OrderTrackerVm         = new OrderTrackerViewModel(dbFactory, orderLabels, entityBrowser, errorLogger, orderFulfilment);
        StandingBuyOrdersVm    = new StandingBuyOrdersViewModel(standingBuyOrderService, corpActivityService);
        WorklistVm             = new WorklistViewModel(worklistService,
                                     new WorklistMarketAltsViewModel(worklistMarketAltService, corpActivityService, dbFactory),
                                     new WorklistInvRulesViewModel(dbFactory, corpActivityService, worklistMarketAltService),
                                     new WorklistCorpAltsViewModel(dbFactory, worklistCorpAltService),
                                     new WorklistIndustryViewModel(dbFactory, industryAssignmentService, worklistSettings, errorLogger, corpActivityService, worklistMarketAltService),
                                     new WorklistStationLevelsViewModel(dbFactory, corpActivityService, worklistSettings),
                                     new WorklistFinalProductsViewModel(dbFactory, appPrefs, errorLogger),
                                     new EveConsole.Services.Worklist.BottleneckService(dbFactory, industryAssignmentService,
                                                           industryBlueprintService, worklistSettings),
                                     new EveConsole.Services.Worklist.ItemContentionService(dbFactory, worklistSettings),
                                     new EveConsole.Services.Worklist.HaulPressureService(dbFactory, worklistSettings));

        // ⚠️ After construction, not with the other Overview wiring above — WorklistVm does not
        // exist until this line, so assigning it earlier set null and left every worklist section
        // on the Overview permanently blank.
        OverviewVm.Worklist = WorklistVm;

        // Adding, renaming or deleting an inventory group changes what the Worklist's group
        // pickers should offer. They load once, so without this a new group is missing until a
        // restart and a renamed one keeps its old label — making a rule look like it points at a
        // group that no longer exists.
        InvLevelVm.GroupsChanged = async () =>
        {
            await WorklistVm.RulesVm.LoadAsync();
            await WorklistVm.StationLevelsVm.LoadAsync();
        };

        LpMarketValuesVm       = new LpMarketValuesViewModel(dbFactory, lpValueService);
        ItemValuationVm        = new ItemValuationViewModel(dbFactory);
        PlayerEntitiesVm       = new PlayerEntitiesViewModel(entityBrowser, killmailBrowserService);
        NpcEntitiesVm          = new NpcEntitiesViewModel(entityBrowser, killmailBrowserService);
        ProductionCalcVm       = new ProductionCalculatorViewModel(dbFactory, prodCalcService, appPrefs);
        PriceOverrideVm        = new PriceOverrideViewModel(new PriceOverrideService(dbFactory), buildCostService);
        StructureBrowserVm     = new StructureBrowserViewModel(
                                     dbFactory, pollingService, esi, new FittingOptionService(dbFactory),
                                     appPrefs, indyStructureLink);
        Pi                     = piService;
        PlanetaryIndustryVm    = new PlanetaryIndustryViewModel(piService, errorLogger);
        // The Overview's PI alerts open the colony they are about, or the list for several.
        OverviewVm.Pi                     = piService;
        OverviewVm.NavigateToPi           = (character, planet) => { OpenTool("planetary_industry"); PlanetaryIndustryVm.ShowColony(character, planet); };
        OverviewVm.NavigateToPiCharacters = () => { OpenTool("planetary_industry"); PlanetaryIndustryVm.ShowCharacters(); };
        var universeMapService = new UniverseMapService(dbFactory);
        // Each system tab gets a page of its own, wired to the Item Browser like the rest.
        SystemPageViewModel NewSystemPage() => new(systemViewService, killmailBrowserService)
        {
            NavigateToItemAction = typeId =>
            {
                OpenTool("items");
                _ = ItemBrowserVm.NavigateToItemCommand.Execute(typeId).Subscribe();
            },
        };
        // The jump planner and the route planner are tabs of the map tool.
        JumpPlannerVm          = new JumpPlannerViewModel(jumpPlanner);
        var jumpBridges        = new JumpBridgeService(dbFactory, esi.GetSovSystemsAsync, corpActivityService);
        MapVm                  = new MapToolViewModel(
            universeMapService, mapStatsService, appPrefs, NewSystemPage,
            new LiveIntelService(dbFactory, corpActivityService), errorLogger, jumpBridges,
            new RoutePlannerService(dbFactory, systemGraph, jumpBridges, esi, eveScout),
            JumpPlannerVm, dbFactory, eveScout, sovCampaigns);
        AlarmsVm               = new AlarmsViewModel(dbFactory, alarmService, alarmSounds, alarmMute);
        SchedulerVm            = new SchedulerViewModel(dbFactory, schedulerService, blockRenderer, slackService, discordService,
                                                        corpActivityService, salePostingService, errorLogger);
        // Parks are added, deleted and renamed in Indy Parks but chosen in the Production
        // Calculator and the Worklist's Industry tab, which each fill their dropdown only once.
        IndyParksVm.ParksChanged += () =>
        {
            _ = ProductionCalcVm.LoadParksAsync();
            _ = WorklistVm.IndustryVm.ReloadParksAsync();
        };

        // One wiring for every killmail row in the app — browser, corp activity, system
        // page, entity viewers.
        EntityNavigator.Instance.OpenEntity = (kind, id) =>
        {
            var player = kind is EntityKind.Pilot or EntityKind.PlayerCorp or EntityKind.Alliance;
            OpenTool(player ? "player_entities" : "npc_entities");
            if (player) PlayerEntitiesVm.Open(kind, id);
            else        NpcEntitiesVm.Open(kind, id);
        };
        EntityNavigator.Instance.OpenSystem   = id => { OpenTool("universe"); MapVm.OpenSystem(id); };
        EntityNavigator.Instance.OpenItem     = id => { OpenTool("items"); _ = ItemBrowserVm.NavigateToItemCommand.Execute(id).Subscribe(); };
        EntityNavigator.Instance.OpenKillmail = id => { OpenTool("killmails"); KillmailBrowserVm.SelectById(id); };
        EntityNavigator.Instance.OpenStructure = id => { OpenTool("structure_browser"); StructureBrowserVm.Open(id); };
        EntityNavigator.Instance.OpenContract  = id => { OpenTool("contracts"); ContractsVm.SelectById(id); };
        EntityNavigator.Instance.OpenNotification = id => { OpenTool("notifications"); NotificationsVm.ShowNotification(id); };
        // A region or constellation is territory you zoom to on a map: the map tab used last,
        // framed on it, or a new map tab if none is open.
        EntityNavigator.Instance.OpenRegion   = id => { OpenTool("universe"); _ = MapVm.FocusRegionAsync(id); };
        EntityNavigator.Instance.OpenConstellation =
            name => { OpenTool("universe"); _ = MapVm.FocusConstellationAsync(name); };

        // Resolve the overlay here rather than in the agent tool: this is the list's home, so
        // an overlay added to the map is reachable by name without touching the tool.
        EntityNavigator.Instance.SetOverlay = text =>
        {
            var wanted = (text ?? "").Trim();
            OpenTool("universe");
            var map  = MapVm.ShowUniverseTab().Map;
            var mode = map.OverlayModes.FirstOrDefault(m =>
                           m.Key.Equals(wanted, StringComparison.OrdinalIgnoreCase) ||
                           m.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                    ?? map.OverlayModes.FirstOrDefault(m =>
                           m.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase));
            if (mode is null) return "";

            map.SelectedOverlay = mode;
            return $"Map overlay set to {mode.Name}.";
        };

        Action<int> showSystem = systemId =>
        {
            OpenTool("universe");
            MapVm.OpenSystem(systemId);
        };
        PlayerEntitiesVm.NavigateToSystem = showSystem;
        NpcEntitiesVm.NavigateToSystem    = showSystem;

        NpcEntitiesVm.NavigateToItem = typeId =>
        {
            OpenTool("items");
            _ = ItemBrowserVm.NavigateToItemCommand.Execute(typeId).Subscribe();
        };
        PlayerEntitiesVm.NavigateToNpc = (kind, id) =>
        {
            OpenTool("npc_entities");
            NpcEntitiesVm.Open(kind, id);
        };
        ProductionCalcVm.NavigateToItemAction = typeId =>
        {
            OpenTool("items");
            _ = ItemBrowserVm.NavigateToItemCommand.Execute(typeId).Subscribe();
        };
        CharacterViewerVm.NavigateToItemAction = typeId =>
        {
            OpenTool("items");
            _ = ItemBrowserVm.NavigateToItemCommand.Execute(typeId).Subscribe();
        };
        LpMarketValuesVm.NavigateToItemAction = typeId =>
        {
            OpenTool("items");
            _ = ItemBrowserVm.NavigateToItemCommand.Execute(typeId).Subscribe();
        };
        ItemValuationVm.NavigateToItemAction = typeId =>
        {
            OpenTool("items");
            _ = ItemBrowserVm.NavigateToItemCommand.Execute(typeId).Subscribe();
        };
        KillmailBrowserVm.NavigateToItemAction = typeId =>
        {
            OpenTool("items");
            _ = ItemBrowserVm.NavigateToItemCommand.Execute(typeId).Subscribe();
        };

        KillmailBrowserVm.NavigateToSystemAction = systemId =>
        {
            OpenTool("universe");
            MapVm.OpenSystem(systemId);
        };

        using var tmpDb      = dbFactory.CreateDbContext();
        var connString       = tmpDb.Database.GetConnectionString()!;
        ExplorerVm           = new EsiExplorerViewModel(connString);
        ErrorLogVm           = new ErrorLogViewModel(dbFactory, errorLogger);
        AgentUsageVm         = new AgentUsageViewModel(dbFactory, errorLogger);
        // A model a paid service has added gets a rate row of its own when the tool is opened.
        AgentUsageVm.SyncListedRates = ct => agentService.Telemetry is { } telemetry
            ? EveConsole.Agent.ListedRates.SyncAsync(agentService.Settings, telemetry, ct)
            : Task.FromResult(0);
        GameLogViewerVm      = new GameLogViewerViewModel(dbFactory, errorLogger);
        ChatLogViewerVm      = new ChatLogViewerViewModel(dbFactory, errorLogger, monitoringSettings);
        AssetBrowserVm       = new AssetBrowserViewModel(connString);
        IndustryBrowserVm    = new IndustryBrowserViewModel(connString, indyFacilityCheck);
        TradeOpportunitiesVm = new TradeOpportunitiesViewModel(connString, historyService, batchAddService);
        IndustryOpportunitiesVm = new IndustryOpportunitiesViewModel(connString, historyService, batchAddService);

        agentService.AlarmToolFactory =
            () => new EveConsole.Agent.Tools.Actions.ManageAlarmsTool(
                dbFactory, alarmService.Registry, alarmService);
        // Set before Initialize — that is where the tool list is built.
        agentService.EntityBrowser = entityBrowser;
        agentService.MapService    = universeMapService;
        agentService.Esi           = esi;
        // The preferences were loaded during startup, before this view model exists; the
        // capsuleer's shared settings are laid over the local file here, before the first prompt
        // is built from them.
        agentService.ApplyShared();
        agentService.Initialize(connString);
        TtsService         = ttsService;
        SpeechInputService = speechInputService;
        HotkeyService      = hotkeyService;
        AppPrefs           = appPrefs;
        UpdateVm           = new UpdateViewModel(appPrefs, errorLogger);
        DbBackup           = dbBackup;

        var s = agentService.Settings;
        ttsService.Configure(s);
        speechInputService.Configure(s.SpeechInputProvider, s.OpenAiApiKey,
                                     s.WhisperLocalModel, s.MicrophoneDeviceName, s.WhisperLanguage,
                                     s.OpenAiTranscriptionModel ?? "");

        AgentVm = new AgentPanelViewModel(agentService, ttsService, speechInputService, hotkeyService);

        StartEveTimeClock();
        StartOnlineCharactersWatch(dbFactory, errorLogger);
        BindAlarmLight(alarmService, workerActivity);

        // The status bar's lines on the background processes, once a second whatever tab is
        // open — cheap, since each is an in-memory read here or the board a worker elsewhere
        // publishes — and at once whenever that worker signals a change (the view model listens).
        Observable.Interval(TimeSpan.FromSeconds(1))
            .ObserveOnUi("MainWindow.BackgroundStatus")
            .Subscribe(_ => ActivityVm.SyncStatusBar());
        ActivityVm.SyncStatusBar();

        // BuildCostService.StatusText is set from a background thread — poll it via a timer.
        Observable.Interval(TimeSpan.FromSeconds(3))
            .ObserveOnUi("MainWindow.BuildCostStatus")
            .Subscribe(_ => BuildCostStatusText = _buildCostService.StatusText);

        // ── Navigation setup ──────────────────────────────────────────────────

        NavGroup[] groups =
        [
            new(ShellText.NavGroupGeneral,
            [
                new NavItem("overview",    ShellText.NavOverview),
                new NavItem("worklist",    ShellText.NavWorklist),
                new NavItem("characters",  ShellText.NavCharacters),
            ]),
            new(ShellText.NavGroupAssets,
            [
                new NavItem("assets",     ShellText.NavAssets),
                new NavItem("items",      ShellText.NavItemBrowser),
                new NavItem("inv_levels", ShellText.NavInventoryLevels),
            ]),
            new(ShellText.NavGroupStructures,
            [
                new NavItem("structure_browser", ShellText.NavStructureBrowser),
                new NavItem("universe",          ShellText.NavUniverseMap),
            ]),
            new(ShellText.NavGroupIndustry,
            [
                new NavItem("industry",      ShellText.NavIndustryJobs),
                new NavItem("indy_parks",    ShellText.NavIndyParks),
                new NavItem("prod_calc",     ShellText.NavProductionCalc),
                new NavItem("planetary_industry", ShellText.NavPlanetaryIndustry),
                new NavItem("price_overrides", ShellText.NavPriceOverrides),
                new NavItem("industry_opps", ShellText.NavIndustryOpportunities),
            ]),
            new(ShellText.NavGroupMarket,
            [
                new NavItem("market_viewer", ShellText.NavMarketOverview),
                new NavItem("item_valuation", ShellText.NavItemValuation),
                new NavItem("lp_market_values", ShellText.NavLpMarketValues),
                new NavItem("market_levels", ShellText.NavMarketLevels),
                new NavItem("contracts",     ShellText.NavContracts),
                new NavItem("trade",         ShellText.NavTradeOpportunities),
                new NavItem("standing_buy_orders", ShellText.NavStandingBuyOrders),
                new NavItem("order_tracker", ShellText.NavOrderTracker),
                new NavItem("sales_tracker", ShellText.NavSalesTracker),
                new NavItem("sale_posting",  ShellText.NavSalePosting),
                new NavItem("stores",        ShellText.NavStores),
            ]),
            new(ShellText.NavGroupFinance,
            [
                new NavItem("net_worth",     ShellText.NavNetWorth),
                new NavItem("income_expense",ShellText.NavIncomeExpense),
                new NavItem("wallet",        ShellText.NavWallet),
            ]),
            new(ShellText.NavGroupCorp,
            [
                new NavItem("corp_activity", ShellText.NavCorpActivity),
                new NavItem("killmails",     ShellText.NavKillmails),
                new NavItem("player_entities", ShellText.NavPlayerEntities),
                new NavItem("npc_entities",    ShellText.NavNpcEntities),
            ]),
            new(ShellText.NavGroupCommunication,
            [
                new NavItem("eve_mail", ShellText.NavEveMail),
                new NavItem("notifications", ShellText.NavNotifications),
            ]),
            new(ShellText.NavGroupData,
            [
                // Alarms is reached from the alarm light beside the settings gear, not from
                // here — it is a status indicator first and a tool second.
                new NavItem("background", ShellText.NavBackgroundProcesses),
                new NavItem("data", ShellText.NavEsiExplorer),
                new NavItem("error_log", ShellText.NavErrorLog),
                new NavItem("ai_usage",  ShellText.NavAiUsage),
                new NavItem("game_log", ShellText.NavGameLog),
                new NavItem("chat_log", ShellText.NavChatLog),
            ]),
        ];

        NavGroups    = groups;
        _allNavItems = groups.SelectMany(g => g.Items).ToArray();

        OpenToolCommand = ReactiveCommand.Create<string>(OpenTool);
        CloseTabCommand = ReactiveCommand.Create<ToolTab>(CloseTab);

        OpenTool("overview");
    }

    public Task ForceResolveNamesAsync() =>
        _pollingService.ForceResolveStructureNamesAsync();
}

/// <summary>Who is doing the background work, as far as the title bar cares.</summary>
public enum WorkerOwnership
{
    /// <summary>Not yet read. Shown as neither running nor missing — saying "none" before
    /// looking would raise an alarm about a worker that is very likely fine.</summary>
    Unknown,
    Mine,
    Other,
    None,
}
