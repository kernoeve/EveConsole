using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using EveConsole.Services;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>A row that carries an EVE image, loaded lazily from the shared cache.</summary>
public abstract class IconRowVm : ReactiveObject
{
    private Bitmap? _icon;
    public Bitmap? Icon
    {
        get => _icon;
        private set => this.RaiseAndSetIfChanged(ref _icon, value);
    }

    protected abstract string? IconUrl { get; }

    public Task LoadIconAsync()
    {
        var url = IconUrl;
        if (string.IsNullOrEmpty(url)) return Task.CompletedTask;
        return EveImageCache.GetAsync(url).ContinueWith(
            t => Dispatcher.UIThread.Post(() => Icon = t.Result), TaskScheduler.Default);
    }
}

public class SovStructureVm(SystemViewService.SovStructureRow r) : IconRowVm
{
    public string TypeName { get; } = r.TypeName;
    public string Owner    { get; } = r.Owner;
    public string Adm      { get; } = r.Adm is { } a ? $"{a:F1}" : "—";
    public string State    { get; } = r.State;
    public string Window   { get; } = r.Window;
    public string StateColor { get; } = r.State switch
    {
        "Vulnerable"   => "#e06a4a",
        "Invulnerable" => "#5fbf7a",
        _              => "#8a8a9a",
    };

    protected override string? IconUrl => $"https://images.evetech.net/types/{r.TypeId}/icon?size=32";

    public long? AllianceId { get; } = r.AllianceId;
}

public class CelestialVm(SystemViewService.CelestialRow r) : IconRowVm
{
    public string Name     { get; } = r.Name;
    public string TypeName { get; } = r.TypeName;
    public bool   IsPlanet { get; } = r.Kind == 0;

    protected override string? IconUrl => $"https://images.evetech.net/types/{r.TypeId}/icon?size=32";
}

public class SysStructureVm(SystemViewService.StructureRow r) : IconRowVm
{
    public string Name        { get; } = r.Name;
    public string TypeName    { get; } = r.TypeName;
    public string Corporation { get; } = r.Corporation;
    public string Alliance    { get; } = r.Alliance;
    public string Location    { get; } = r.Location;
    public string Kind        { get; } = r.IsNpc ? "NPC" : "Player";
    public string KindColor   { get; } = r.IsNpc ? "#6a7f99" : "#c8a84b";

    protected override string? IconUrl =>
        r.TypeId > 0 ? $"https://images.evetech.net/types/{r.TypeId}/icon?size=32" : null;
}

/// <summary>One line of the celestial tree, indented by depth.</summary>
public class CelestialNodeVm(SystemViewService.CelestialNode n) : IconRowVm
{
    public string    Name      { get; } = n.Name;
    public string    TypeName  { get; } = n.TypeName;
    public string    Kind      { get; } = n.Kind;
    public string    Owner     { get; } = n.Owner;
    public string    Power     { get; } = n.Power > 0 ? n.Power.ToString("N0") : "";
    public string    Workforce { get; } = n.Workforce > 0 ? n.Workforce.ToString("N0") : "";
    /// <summary>Reagent yield per hour, named by planet type — Lava gives Magmatic Gas, Ice
    /// gives Sublimated Ice. Blank on every other planet, which carries none.</summary>
    public string    Reagent   { get; } = n.ReagentPerHour > 0 ? $"{n.ReagentPerHour:N0}/h {n.Reagent}" : "";
    public string    ReagentColor { get; } = n.Reagent == "Sublimated Ice" ? "#7fc8e8" : "#e08a4a";
    public Avalonia.Thickness Indent { get; } = new(n.Depth * 22, 0, 0, 0);
    public bool      IsHeading { get; } = n.Kind is "Star" or "Planet" or "Stargate";

    public string NameColor { get; } = n.Kind switch
    {
        "Star"      => "#e0c060",
        "Planet"    => "#d8d8e4",
        "Stargate"  => "#7fb8d8",
        "Structure" => "#c8a84b",
        "Station"   => "#8fb0c8",
        _           => "#9a9aaa",
    };

    protected override string? IconUrl =>
        n.TypeId > 0 ? $"https://images.evetech.net/types/{n.TypeId}/icon?size=32" : null;
}

public class AgentVm(SystemViewService.AgentRow a)
{
    public string Location    { get; } = a.Location;
    public string Name        { get; } = a.Name;
    public string Corporation { get; } = a.Corporation;
    public string Division    { get; } = a.Division;
    public string AgentType   { get; } = a.AgentType;
    public string Level       { get; } = a.Level.ToString();
    public string Locator     { get; } = a.IsLocator ? "Locator" : "";
}

public class SystemEventVm(SystemViewService.SystemEvent e) : IconRowVm
{
    public string When    { get; } = e.When.UtcDateTime.ToString("yyyy-MM-dd HH:mm");
    public string Kind    { get; } = e.Kind;
    public string Summary { get; } = e.Summary;
    public string KindColor { get; } = e.Kind switch
    {
        "Sovereignty gained" => "#5fbf7a",
        "Sovereignty lost"   => "#e0574a",
        "ADM increased"      => "#7fb8d8",
        "ADM decreased"      => "#e0913c",
        _                    => "#8a8a9a",
    };

    protected override string? IconUrl =>
        e.AllianceId is { } a and > 0 ? $"https://images.evetech.net/alliances/{a}/logo?size=32" : null;
}

public class GateVm(SystemViewService.GateRow r)
{
    public int    SystemId    { get; } = r.SystemId;
    public string Name        { get; } = r.Name;
    public string RegionName  { get; } = r.RegionName;
    public bool   OutOfRegion { get; } = r.OutOfRegion;
    public string Security    { get; } = r.Security.ToString("F2");
    public string SecurityColor { get; } = SecurityBrush(r.Security);

    private static string SecurityBrush(double sec) => Math.Round(sec, 1) switch
    {
        >= 0.5 => "#4fc07a",
        > 0.0  => "#e0913c",
        _      => "#d94848",
    };
}

/// <summary>
/// The system page: a header of general information over tabs that each answer a different
/// question. Modelled on dotlan's layout, which is what players already know.
/// </summary>
public class SystemPageViewModel : ReactiveObject
{
    private readonly SystemViewService     _svc;
    private readonly KillmailBrowserService _kills;

    public SystemPageViewModel(SystemViewService svc, KillmailBrowserService kills)
    {
        _svc   = svc;
        _kills = kills;
    }

    // ── Header ───────────────────────────────────────────────────────────────

    private SystemViewService.SystemHeader? _header;

    private string _name = "";
    public string Name { get => _name; private set => this.RaiseAndSetIfChanged(ref _name, value); }

    private string _region = "";
    public string Region { get => _region; private set => this.RaiseAndSetIfChanged(ref _region, value); }

    private string _constellation = "";
    public string Constellation { get => _constellation; private set => this.RaiseAndSetIfChanged(ref _constellation, value); }

    private string _security = "";
    public string Security { get => _security; private set => this.RaiseAndSetIfChanged(ref _security, value); }

    private string _securityColor = "#8a8a9a";
    public string SecurityColor { get => _securityColor; private set => this.RaiseAndSetIfChanged(ref _securityColor, value); }

    private string _securityClass = "";
    public string SecurityClass { get => _securityClass; private set => this.RaiseAndSetIfChanged(ref _securityClass, value); }

    private string _production = "";
    /// <summary>Power, workforce and reagent yields pooled across the system — the totals
    /// sovereignty upgrades actually draw on, as opposed to the per-planet breakdown.</summary>
    public string Production { get => _production; private set => this.RaiseAndSetIfChanged(ref _production, value); }

    private bool _hasProduction;
    public bool HasProduction { get => _hasProduction; private set => this.RaiseAndSetIfChanged(ref _hasProduction, value); }

    private string _adm = "";
    /// <summary>Current activity defense multiplier, blank where the system has no sovereignty
    /// structure — 0.0 would read as a real reading rather than "not applicable".</summary>
    public string Adm { get => _adm; private set => this.RaiseAndSetIfChanged(ref _adm, value); }

    private bool _hasAdm;
    public bool HasAdm { get => _hasAdm; private set => this.RaiseAndSetIfChanged(ref _hasAdm, value); }

    private string _admColor = "#8a8a9a";
    public string AdmColor { get => _admColor; private set => this.RaiseAndSetIfChanged(ref _admColor, value); }

    private string _industryIndex = "";
    public string IndustryIndex { get => _industryIndex; private set => this.RaiseAndSetIfChanged(ref _industryIndex, value); }

    private bool _hasIndustryIndex;
    public bool HasIndustryIndex { get => _hasIndustryIndex; private set => this.RaiseAndSetIfChanged(ref _hasIndustryIndex, value); }

    private string _localPirates = "";
    public string LocalPirates { get => _localPirates; private set => this.RaiseAndSetIfChanged(ref _localPirates, value); }

    private string _holder = "";
    public string Holder { get => _holder; private set => this.RaiseAndSetIfChanged(ref _holder, value); }

    private Bitmap? _holderLogo;
    public Bitmap? HolderLogo { get => _holderLogo; private set => this.RaiseAndSetIfChanged(ref _holderLogo, value); }

    private string _planetCount = "";
    public string PlanetCount { get => _planetCount; private set => this.RaiseAndSetIfChanged(ref _planetCount, value); }

    private string _beltCount = "";
    public string BeltCount { get => _beltCount; private set => this.RaiseAndSetIfChanged(ref _beltCount, value); }

    private string _moonCount = "";
    public string MoonCount { get => _moonCount; private set => this.RaiseAndSetIfChanged(ref _moonCount, value); }

    private string _jumps = "";
    public string Jumps { get => _jumps; private set => this.RaiseAndSetIfChanged(ref _jumps, value); }

    private string _shipKills = "";
    public string ShipKills { get => _shipKills; private set => this.RaiseAndSetIfChanged(ref _shipKills, value); }

    private string _npcKills = "";
    public string NpcKills { get => _npcKills; private set => this.RaiseAndSetIfChanged(ref _npcKills, value); }

    private string _podKills = "";
    public string PodKills { get => _podKills; private set => this.RaiseAndSetIfChanged(ref _podKills, value); }

    // ── Tabs ─────────────────────────────────────────────────────────────────

    public ObservableCollection<SovStructureVm>   SovStructures { get; } = [];
    public ObservableCollection<SystemEventVm>    SovChanges    { get; } = [];
    public ObservableCollection<SystemEventVm>    Events        { get; } = [];
    public ObservableCollection<CelestialNodeVm>  Celestials    { get; } = [];
    public ObservableCollection<SysStructureVm>   Structures    { get; } = [];
    public ObservableCollection<KillmailListRowVm> Kills        { get; } = [];
    public ObservableCollection<GateVm>           Gates         { get; } = [];
    public ObservableCollection<AgentVm>          Agents        { get; } = [];

    private string _historyNote = "";
    public string HistoryNote { get => _historyNote; private set => this.RaiseAndSetIfChanged(ref _historyNote, value); }

    // ── Graphs ───────────────────────────────────────────────────────────────
    // Four separate charts rather than one with four series: jumps run in the thousands while
    // ship and pod kills are single digits, so sharing an axis would flatten the kill lines
    // onto zero.

    private ISeries[] _jumpSeries = [];
    public ISeries[] JumpSeries { get => _jumpSeries; private set => this.RaiseAndSetIfChanged(ref _jumpSeries, value); }

    private ISeries[] _shipKillSeries = [];
    public ISeries[] ShipKillSeries { get => _shipKillSeries; private set => this.RaiseAndSetIfChanged(ref _shipKillSeries, value); }

    private ISeries[] _podKillSeries = [];
    public ISeries[] PodKillSeries { get => _podKillSeries; private set => this.RaiseAndSetIfChanged(ref _podKillSeries, value); }

    private ISeries[] _npcKillSeries = [];
    public ISeries[] NpcKillSeries { get => _npcKillSeries; private set => this.RaiseAndSetIfChanged(ref _npcKillSeries, value); }

    private Axis[] _historyXAxes = [];
    public Axis[] HistoryXAxes { get => _historyXAxes; private set => this.RaiseAndSetIfChanged(ref _historyXAxes, value); }

    private Axis[] _historyYAxes = [];
    public Axis[] HistoryYAxes { get => _historyYAxes; private set => this.RaiseAndSetIfChanged(ref _historyYAxes, value); }

    private string _graphNote = "";
    public string GraphNote { get => _graphNote; private set => this.RaiseAndSetIfChanged(ref _graphNote, value); }

    private ISeries[] _admSeries = [];
    public ISeries[] AdmSeries { get => _admSeries; private set => this.RaiseAndSetIfChanged(ref _admSeries, value); }

    private Axis[] _admXAxes = [];
    public Axis[] AdmXAxes { get => _admXAxes; private set => this.RaiseAndSetIfChanged(ref _admXAxes, value); }

    private Axis[] _admYAxes = [];
    public Axis[] AdmYAxes { get => _admYAxes; private set => this.RaiseAndSetIfChanged(ref _admYAxes, value); }

    private string _admNote = "";
    public string AdmNote { get => _admNote; private set => this.RaiseAndSetIfChanged(ref _admNote, value); }

    private bool _hasAdmGraph;
    public bool HasAdmGraph { get => _hasAdmGraph; private set => this.RaiseAndSetIfChanged(ref _hasAdmGraph, value); }

    private ISeries[] _indexSeries = [];
    public ISeries[] IndexSeries { get => _indexSeries; private set => this.RaiseAndSetIfChanged(ref _indexSeries, value); }

    private Axis[] _indexXAxes = [];
    public Axis[] IndexXAxes { get => _indexXAxes; private set => this.RaiseAndSetIfChanged(ref _indexXAxes, value); }

    private Axis[] _indexYAxes = [];
    public Axis[] IndexYAxes { get => _indexYAxes; private set => this.RaiseAndSetIfChanged(ref _indexYAxes, value); }

    /// <summary>The chart legend defaults to black-on-white, which is unreadable on this theme.</summary>
    public SolidColorPaint LegendPaint     { get; } = new(SKColor.Parse("#9A9AAE"));
    public SolidColorPaint LegendBackPaint { get; } = new(SKColor.Parse("#101018"));

    private string _indexNote = "";
    public string IndexNote { get => _indexNote; private set => this.RaiseAndSetIfChanged(ref _indexNote, value); }

    private bool _hasIndexGraph;
    public bool HasIndexGraph { get => _hasIndexGraph; private set => this.RaiseAndSetIfChanged(ref _hasIndexGraph, value); }

    // ── Overview sparklines (hourly) ─────────────────────────────────────────

    private ISeries[] _hourJumpSeries = [];
    public ISeries[] HourJumpSeries { get => _hourJumpSeries; private set => this.RaiseAndSetIfChanged(ref _hourJumpSeries, value); }

    private ISeries[] _hourNpcSeries = [];
    public ISeries[] HourNpcSeries { get => _hourNpcSeries; private set => this.RaiseAndSetIfChanged(ref _hourNpcSeries, value); }

    private ISeries[] _hourShipSeries = [];
    public ISeries[] HourShipSeries { get => _hourShipSeries; private set => this.RaiseAndSetIfChanged(ref _hourShipSeries, value); }

    private ISeries[] _hourPodSeries = [];
    public ISeries[] HourPodSeries { get => _hourPodSeries; private set => this.RaiseAndSetIfChanged(ref _hourPodSeries, value); }

    private Axis[] _hourXAxes = [];
    public Axis[] HourXAxes { get => _hourXAxes; private set => this.RaiseAndSetIfChanged(ref _hourXAxes, value); }

    private Axis[] _hourYAxes = [];
    public Axis[] HourYAxes { get => _hourYAxes; private set => this.RaiseAndSetIfChanged(ref _hourYAxes, value); }

    private string _agentNote = "";
    public string AgentNote { get => _agentNote; private set => this.RaiseAndSetIfChanged(ref _agentNote, value); }

    private string _hourNote = "";
    public string HourNote { get => _hourNote; private set => this.RaiseAndSetIfChanged(ref _hourNote, value); }

    public string IntelNote =>
        "Intel channel reports will appear here once chat-log parsing is in place. " +
        "Reports will be recorded against the system they name, with the time and the reporter.";

    private string _killsNote = "";
    public string KillsNote { get => _killsNote; private set => this.RaiseAndSetIfChanged(ref _killsNote, value); }

    /// <summary>Raised when a gate is clicked, so the host can navigate.</summary>
    public Func<int, Task>? NavigateToSystem { get; set; }

    public ReactiveCommand<int, Unit>? OpenGateCommand { get; set; }

    // ── Load ─────────────────────────────────────────────────────────────────

    public async Task LoadAsync(int systemId)
    {
        var header    = await _svc.GetHeaderAsync(systemId);
        if (header is null) return;

        var sovStructs = await _svc.GetSovStructuresAsync(systemId);
        var events     = await _svc.GetEventsAsync(systemId);
        var tree       = await _svc.GetCelestialTreeAsync(systemId);
        var structures = await _svc.GetStructuresAsync(systemId);
        var gates      = await _svc.GetGatesAsync(systemId);
        var since      = await _svc.GetHistoryStartAsync();
        var history    = await _svc.GetHistoryAsync(systemId);
        var hourly     = await _svc.GetHourlyHistoryAsync(systemId);
        var admHist    = await _svc.GetAdmHistoryAsync(systemId);
        var indexHist  = await _svc.GetIndustryHistoryAsync(systemId);
        var agents     = await _svc.GetAgentsAsync(systemId);

        // The kill list is the same query and the same row type the Kills tool uses, so the
        // formatting and icons match the rest of the app rather than being reinvented here.
        var killPage = await _kills.GetListAsync(0, 50, systemFilter: header.Name);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _header = header;
            Name          = header.Name;
            Region        = header.Region;
            Constellation = header.Constellation;
            Security      = header.Security.ToString("F2");
            SecurityColor = Math.Round(header.Security, 1) switch
            {
                >= 0.5 => "#4fc07a",
                > 0.0  => "#e0913c",
                _      => "#d94848",
            };
            SecurityClass = header.SecurityClass;
            LocalPirates  = header.LocalPirates;

            HasAdm = header.Adm is not null;
            Adm    = header.Adm is { } adm ? adm.ToString("F1") : "";
            // The same reading the sovereignty overlay uses: 6 is fully defended, 1 undefended.
            AdmColor = header.Adm switch
            {
                >= 5.0 => "#4fc07a",
                >= 3.0 => "#e0913c",
                not null => "#d94848",
                _        => "#8a8a9a",
            };
            // Cost indices are fractions in the API; players talk in percent. All six are shown
            // rather than manufacturing alone — a system can be cheap to build in and expensive
            // to invent in, and only one of those was visible before.
            HasIndustryIndex = header.Industry.Count > 0;
            IndustryIndex    = string.Join("   ",
                header.Industry.Select(i => $"{i.ShortName} {i.Index * 100:F2}%"));

            var bits = new List<string>();
            if (header.Power > 0)                bits.Add($"{header.Power:N0} power");
            if (header.Workforce > 0)            bits.Add($"{header.Workforce:N0} workforce");
            if (header.MagmaticGasPerHour > 0)   bits.Add($"{header.MagmaticGasPerHour:N0}/h magmatic gas");
            if (header.SublimatedIcePerHour > 0) bits.Add($"{header.SublimatedIcePerHour:N0}/h sublimated ice");
            Production    = string.Join("  ·  ", bits);
            HasProduction = bits.Count > 0;
            Holder = string.IsNullOrEmpty(header.AllianceName)
                ? (string.IsNullOrEmpty(header.CorporationName) ? "Unclaimed" : header.CorporationName)
                : string.IsNullOrEmpty(header.CorporationName)
                    ? header.AllianceName
                    : $"{header.AllianceName}  ·  {header.CorporationName}";

            PlanetCount = header.Planets.ToString("N0");
            MoonCount   = header.Moons.ToString("N0");
            BeltCount   = header.Belts.ToString("N0");
            Jumps     = $"{header.Jumps1h:N0} / {header.Jumps24h:N0}";
            ShipKills = $"{header.ShipKills1h:N0} / {header.ShipKills24h:N0}";
            NpcKills  = $"{header.NpcKills1h:N0} / {header.NpcKills24h:N0}";
            PodKills  = $"{header.PodKills1h:N0} / {header.PodKills24h:N0}";

            Fill(SovStructures, sovStructs.Select(s => new SovStructureVm(s)));
            Fill(Events,     events.Select(e => new SystemEventVm(e)));
            Fill(SovChanges, events.Where(e => e.Kind.StartsWith("Sovereignty"))
                                   .Select(e => new SystemEventVm(e)));
            Fill(Celestials, tree.Select(n => new CelestialNodeVm(n)));
            BuildGraphs(history);
            BuildSparklines(hourly);
            BuildAdmGraph(admHist);
            BuildIndexGraph(indexHist);
            Fill(Structures, structures.Select(s => new SysStructureVm(s)));
            Fill(Kills, killPage.Rows.Select(r => new KillmailListRowVm(r)));
            Fill(Gates, gates.Select(g => new GateVm(g)));
            Fill(Agents, agents.Select(a => new AgentVm(a)));
            AgentNote = agents.Count == 0
                ? "No agents in this system."
                : $"{agents.Count} agents across {agents.Select(a => a.Location).Distinct().Count()} stations.";

            // Stated plainly: the history only reaches back as far as the snapshots, and
            // without saying so an empty list reads as "nothing ever happened here".
            HistoryNote = since is null
                ? "No sovereignty history stored yet."
                : $"Derived from stored snapshots since {since:yyyy-MM-dd}. Earlier changes were " +
                  "before this app began recording and cannot be recovered.";

            KillsNote = killPage.Rows.Count == 0
                ? "No killmails stored for this system."
                : $"{killPage.Rows.Count} most recent" + (killPage.HasMore ? " (more exist)" : "");
        });

        await LoadImagesAsync(header);
    }

    private async Task LoadImagesAsync(SystemViewService.SystemHeader header)
    {
        if (header.AllianceId is { } a and > 0)
        {
            var bmp = await EveImageCache.GetAsync($"https://images.evetech.net/alliances/{a}/logo?size=64");
            await Dispatcher.UIThread.InvokeAsync(() => HolderLogo = bmp);
        }
        else if (header.CorporationId is { } c and > 0)
        {
            var bmp = await EveImageCache.GetAsync($"https://images.evetech.net/corporations/{c}/logo?size=64");
            await Dispatcher.UIThread.InvokeAsync(() => HolderLogo = bmp);
        }
        else
        {
            await Dispatcher.UIThread.InvokeAsync(() => HolderLogo = null);
        }

        // Icons are fetched after the lists are on screen so the page appears immediately and
        // fills in, rather than waiting on several hundred image requests.
        await Task.WhenAll(
            Task.WhenAll(SovStructures.Select(x => x.LoadIconAsync())),
            Task.WhenAll(Celestials.Where(c => c.Icon is null).Take(120).Select(x => x.LoadIconAsync())),
            Task.WhenAll(Structures.Select(x => x.LoadIconAsync())),
            Task.WhenAll(Events.Take(40).Select(x => x.LoadIconAsync())),
            Task.WhenAll(Kills.Select(x => x.LoadImagesAsync())));
    }

    /// <summary>
    /// The four small hourly charts on the Overview. Bounded by how much hourly detail is kept,
    /// which is one day by default — so the span is stated rather than labelled "48h" when it
    /// may hold less.
    /// </summary>
    private void BuildSparklines(List<SystemViewService.HourPoint> hourly)
    {
        if (hourly.Count == 0)
        {
            HourJumpSeries = HourNpcSeries = HourShipSeries = HourPodSeries = [];
            HourNote = "No hourly history stored yet.";
            return;
        }

        var span = (hourly[^1].Hour - hourly[0].Hour).TotalHours + 1;
        HourNote = $"Last {span:F0} hours, hour by hour. " +
                   "Raise \"keep hourly detail\" in Settings for a longer span.";

        static ISeries[] Spark(IEnumerable<int> values, string hex) =>
        [
            new LineSeries<int>
            {
                Values         = values.ToArray(),
                GeometrySize   = 0,
                LineSmoothness = 0.2,
                Stroke         = new SolidColorPaint(SKColor.Parse(hex)) { StrokeThickness = 1.6f },
                Fill           = new SolidColorPaint(SKColor.Parse(hex).WithAlpha(40)),
            },
        ];

        HourJumpSeries = Spark(hourly.Select(h => h.Jumps),     "#6FC8F0");
        HourNpcSeries  = Spark(hourly.Select(h => h.NpcKills),  "#7FD070");
        HourShipSeries = Spark(hourly.Select(h => h.ShipKills), "#FF6A3D");
        HourPodSeries  = Spark(hourly.Select(h => h.PodKills),  "#F0D040");

        // Hours back from now, like dotlan's "42h 36h … 0h", rather than wall-clock stamps
        // that would be unreadable at this size.
        var newest = hourly[^1].Hour;
        HourXAxes =
        [
            new Axis
            {
                Labels      = hourly.Select(h => $"{(newest - h.Hour).TotalHours:F0}h").ToArray(),
                LabelsPaint = new SolidColorPaint(SKColor.Parse("#55556A")),
                TextSize    = 9,
                MinStep     = Math.Max(1, hourly.Count / 6),
                SeparatorsPaint = null,
            },
        ];
        HourYAxes =
        [
            new Axis
            {
                LabelsPaint = new SolidColorPaint(SKColor.Parse("#55556A")),
                TextSize    = 9,
                MinLimit    = 0,
                SeparatorsPaint = new SolidColorPaint(SKColor.Parse("#1A1A24")) { StrokeThickness = 1 },
            },
        ];
    }

    private void BuildGraphs(List<SystemViewService.HistoryPoint> history)
    {
        if (history.Count == 0)
        {
            JumpSeries = ShipKillSeries = PodKillSeries = NpcKillSeries = [];
            GraphNote  = "No activity history stored for this system yet.";
            return;
        }

        GraphNote = $"Daily totals, {history[0].Day:yyyy-MM-dd} to {history[^1].Day:yyyy-MM-dd}. " +
                    "Each point is a full UTC day; the last may be partial.";

        static ISeries[] Line(string name, IEnumerable<int> values, string hex) =>
        [
            new LineSeries<int>
            {
                Name           = name,
                Values         = values.ToArray(),
                GeometrySize   = 0,
                LineSmoothness = 0.3,
                Stroke         = new SolidColorPaint(SKColor.Parse(hex)) { StrokeThickness = 2 },
                Fill           = new SolidColorPaint(SKColor.Parse(hex).WithAlpha(36)),
            },
        ];

        JumpSeries     = Line("Jumps",      history.Select(h => h.Jumps),     "#6FC8F0");
        ShipKillSeries = Line("Ship kills", history.Select(h => h.ShipKills), "#FF6A3D");
        PodKillSeries  = Line("Pod kills",  history.Select(h => h.PodKills),  "#F0D040");
        NpcKillSeries  = Line("NPC kills",  history.Select(h => h.NpcKills),  "#7FD070");

        var labels = history.Select(h => h.Day.ToString("MM-dd")).ToArray();
        HistoryXAxes =
        [
            new Axis
            {
                Labels        = labels,
                LabelsPaint   = new SolidColorPaint(SKColor.Parse("#6A6A7C")),
                TextSize      = 10,
                // A month of daily labels will not fit, so only every few days are drawn.
                MinStep       = Math.Max(1, labels.Length / 10),
                SeparatorsPaint = null,
            },
        ];
        HistoryYAxes =
        [
            new Axis
            {
                LabelsPaint     = new SolidColorPaint(SKColor.Parse("#6A6A7C")),
                TextSize        = 10,
                MinLimit        = 0,
                SeparatorsPaint = new SolidColorPaint(SKColor.Parse("#1E1E2A")) { StrokeThickness = 1 },
            },
        ];
    }

    /// <summary>
    /// ADM over time. Its own chart with a fixed 0–6 axis: the value only ever moves between 1
    /// and 6, and an autoscaled axis would turn the routine drift between 5.8 and 6.0 into a
    /// dramatic-looking collapse.
    /// </summary>
    private void BuildAdmGraph(List<SystemViewService.AdmPoint> points)
    {
        HasAdmGraph = points.Count > 0;
        if (!HasAdmGraph)
        {
            AdmSeries = [];
            AdmNote   = "No ADM history: this system holds no sovereignty structure.";
            return;
        }

        AdmNote = $"Daily peak ADM, {points[0].Day:yyyy-MM-dd} to {points[^1].Day:yyyy-MM-dd}.";

        AdmSeries =
        [
            new LineSeries<double>
            {
                Name           = "ADM",
                Values         = points.Select(p => p.Adm).ToArray(),
                GeometrySize   = 0,
                LineSmoothness = 0.3,
                Stroke         = new SolidColorPaint(SKColor.Parse("#7FD070")) { StrokeThickness = 2 },
                Fill           = new SolidColorPaint(SKColor.Parse("#7FD070").WithAlpha(36)),
            },
        ];

        var labels = points.Select(p => p.Day.ToString("MM-dd")).ToArray();
        AdmXAxes   = [DayAxis(labels)];
        AdmYAxes   =
        [
            new Axis
            {
                LabelsPaint     = new SolidColorPaint(SKColor.Parse("#6A6A7C")),
                TextSize        = 10,
                MinLimit        = 0,
                MaxLimit        = 6,
                MinStep         = 1,
                SeparatorsPaint = new SolidColorPaint(SKColor.Parse("#1E1E2A")) { StrokeThickness = 1 },
            },
        ];
    }

    /// <summary>Colours per activity, so the same activity keeps its colour between systems.</summary>
    private static readonly Dictionary<string, string> IndexColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["manufacturing"]           = "#6FC8F0",
        ["researching_time_efficiency"]     = "#7FD070",
        ["researching_material_efficiency"] = "#F0D040",
        ["copying"]                 = "#C89AE0",
        ["invention"]               = "#FF6A3D",
        ["reaction"]                = "#E06A9A",
    };

    private static string PrettyActivity(string a) => a switch
    {
        "researching_time_efficiency"     => "Time efficiency",
        "researching_material_efficiency" => "Material efficiency",
        _ => char.ToUpperInvariant(a[0]) + a[1..].Replace('_', ' '),
    };

    /// <summary>
    /// All six cost indices on one chart — unlike jumps versus kills these share a scale, and
    /// seeing them together is the point: a system's manufacturing index rising while reactions
    /// stay flat says something the separate charts would not.
    /// </summary>
    private void BuildIndexGraph(List<SystemViewService.IndexSeries> series)
    {
        HasIndexGraph = series.Count > 0 && series.Any(s => s.Points.Count > 0);
        if (!HasIndexGraph)
        {
            IndexSeries = [];
            IndexNote   = "No industry cost index history stored for this system yet.";
            return;
        }

        // Each activity is stored independently, so pad against the union of days rather than
        // assuming they all start together — otherwise a late-arriving activity would be drawn
        // shifted left against the others.
        var days = series.SelectMany(s => s.Points.Select(p => p.Day)).Distinct().OrderBy(d => d).ToList();

        IndexNote = $"Daily cost index, {days[0]:yyyy-MM-dd} to {days[^1]:yyyy-MM-dd}. " +
                    "Shown as a percentage, as the industry window does.";

        IndexSeries = series.Select(s =>
        {
            var byDay = s.Points.ToDictionary(p => p.Day, p => p.Index);
            var hex   = IndexColors.GetValueOrDefault(s.Activity, "#8A8A9A");
            return (ISeries)new LineSeries<double?>
            {
                Name           = PrettyActivity(s.Activity),
                Values         = days.Select(d => byDay.TryGetValue(d, out var v) ? v * 100 : (double?)null).ToArray(),
                GeometrySize   = 0,
                LineSmoothness = 0.3,
                Stroke         = new SolidColorPaint(SKColor.Parse(hex)) { StrokeThickness = 2 },
                Fill           = null,
            };
        }).ToArray();

        IndexXAxes = [DayAxis(days.Select(d => d.ToString("MM-dd")).ToArray())];
        IndexYAxes =
        [
            new Axis
            {
                Labeler         = v => $"{v:0.##}%",
                LabelsPaint     = new SolidColorPaint(SKColor.Parse("#6A6A7C")),
                TextSize        = 10,
                MinLimit        = 0,
                SeparatorsPaint = new SolidColorPaint(SKColor.Parse("#1E1E2A")) { StrokeThickness = 1 },
            },
        ];
    }

    private static Axis DayAxis(string[] labels) => new()
    {
        Labels          = labels,
        LabelsPaint     = new SolidColorPaint(SKColor.Parse("#6A6A7C")),
        TextSize        = 10,
        MinStep         = Math.Max(1, labels.Length / 10),
        SeparatorsPaint = null,
    };

    private static void Fill<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var i in items) target.Add(i);
    }
}
