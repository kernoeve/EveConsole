using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using EveConsole.Controls;
using EveConsole.Models;
using EveConsole.Services;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using ReactiveUI;
using EveConsole.Localization;

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
    public long   Key       { get; } = r.StructureId;
    /// <summary>What a refresh compares: a hub whose owner, ADM, state or window moved is redrawn.</summary>
    public string Signature { get; } = $"{r.Owner}|{r.Adm}|{r.State}|{r.Window}";
    public string TypeName { get; } = SdeNames.Type(r.TypeId, r.TypeName);
    public string Owner    { get; } = r.Owner;
    public string Adm      { get; } = r.Adm is { } a ? $"{a:F1}" : "—";
    public string State    { get; } = r.State switch
    {
        SystemViewService.SovStructureState.Vulnerable   => MapText.SovStateVulnerable,
        SystemViewService.SovStructureState.Invulnerable => MapText.SovStateInvulnerable,
        _                                                => MapText.SovStateUnknown,
    };
    public string Window   { get; } = r.Window;
    public string StateColor { get; } = r.State switch
    {
        SystemViewService.SovStructureState.Vulnerable   => "#e06a4a",
        SystemViewService.SovStructureState.Invulnerable => "#5fbf7a",
        _                                                => "#8a8a9a",
    };

    protected override string? IconUrl => $"https://images.evetech.net/types/{r.TypeId}/icon?size=32";

    public long? AllianceId { get; } = r.AllianceId;

    // The hull type and the alliance holding it. Both ids were already on the row.
    public bool HasTypeLink  => r.TypeId > 0 && TypeName.Length > 0;
    public bool HasOwnerLink => r.AllianceId is > 0 && Owner.Length > 0;

    public void OpenType()  => EntityNavigator.Instance.Item(r.TypeId);
    public void OpenOwner() => EntityNavigator.Instance.Entity(EntityKind.Alliance, r.AllianceId ?? 0);
}

public class CelestialVm(SystemViewService.CelestialRow r) : IconRowVm
{
    public string Name     { get; } = r.Name;   // English: celestial names are not translated yet
    public string TypeName { get; } = SdeNames.Type(r.TypeId, r.TypeName);
    public bool   IsPlanet { get; } = r.Kind == 0;

    protected override string? IconUrl => $"https://images.evetech.net/types/{r.TypeId}/icon?size=32";
}

public class SysStructureVm : IconRowVm
{
    private readonly SystemViewService.StructureRow _r;

    /// <summary>Station id for an NPC station, location id for a player structure — which of the
    /// two decides where clicking it goes.</summary>
    public long   Id          { get; }
    public bool   IsNpc       { get; }
    public int    TypeId      { get; }
    public string Name        { get; }
    public string TypeName    { get; }
    public string Corporation { get; }
    public string Alliance    { get; }
    public string Location    { get; }
    public string Owner       { get; }
    public string Kind        { get; }
    public string KindColor   { get; }

    public bool HasCorporation { get; }
    public bool HasAlliance    { get; }

    public SysStructureVm(SystemViewService.StructureRow r)
    {
        _r          = r;
        Id          = r.StructureId;
        IsNpc       = r.IsNpc;
        TypeId      = r.TypeId;
        // An NPC station in the interface language; a player structure is its owner's name. The
        // service's row keeps the English, which it places stations by.
        Name        = r.IsNpc ? SdeNames.Station(r.StructureId, r.Name) : r.Name;
        TypeName    = SdeNames.Type(r.TypeId, r.TypeName);
        // An NPC station's owner is an NPC corporation; a player corporation simply has no
        // other name, and comes back as it is.
        Corporation = SdeNames.NpcCorporation(r.CorporationId, r.Corporation);
        Alliance    = r.Alliance;
        Location    = r.Location;
        Owner       = r.Owner;
        Kind        = r.IsNpc ? MapText.StructureKindNpc : MapText.StructureKindPlayer;
        KindColor   = r.IsNpc ? "#6a7f99" : "#c8a84b";

        // An id without a name is a link to a blank page, and a name without an id is a link that
        // cannot go anywhere. Both are needed before one is offered.
        HasCorporation = r.CorporationId > 0 && r.Corporation.Length > 0;
        HasAlliance    = r.AllianceId    > 0 && r.Alliance.Length    > 0;

        OpenCommand = ReactiveCommand.Create(() =>
        {
            if (IsNpc) Nav.Entity(EntityKind.Station, Id);
            else       Nav.Structure(Id);
        });

        // ⚠️ An NPC station's owner is an NPC corporation, a player structure's is a player one.
        // They live in different tools, so the same column routes to different places by kind.
        OpenCorpCommand = ReactiveCommand.Create(() =>
            Nav.Entity(IsNpc ? EntityKind.NpcCorp : EntityKind.PlayerCorp, _r.CorporationId));

        OpenAllianceCommand = ReactiveCommand.Create(() =>
            Nav.Entity(EntityKind.Alliance, _r.AllianceId));

        OpenTypeCommand = ReactiveCommand.Create(() => Nav.Item(TypeId));
    }

    private static EntityNavigator Nav => EntityNavigator.Instance;

    public ReactiveCommand<Unit, Unit> OpenCommand         { get; }
    public ReactiveCommand<Unit, Unit> OpenCorpCommand     { get; }
    public ReactiveCommand<Unit, Unit> OpenAllianceCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenTypeCommand     { get; }

    protected override string? IconUrl =>
        TypeId > 0 ? $"https://images.evetech.net/types/{TypeId}/icon?size=32" : null;
}

/// <summary>One line of the celestial tree, indented by depth.</summary>
public class CelestialNodeVm(SystemViewService.CelestialNode n) : IconRowVm
{
    // An NPC station docked at a celestial in the interface language — the service's node keeps the
    // English, which it places stations by. Planets, moons, belts and gates are not translated yet,
    // and structures are player-named. Their types are.
    public string    Name      { get; } = n.IsNpc ? SdeNames.Station(n.LocationId, n.Name) : n.Name;
    public string    TypeName  { get; } = SdeNames.Type(n.TypeId, n.TypeName);
    public string    Kind      { get; } = n.Kind;
    public string    Owner     { get; } = n.Owner;
    public string    Corporation { get; } = SdeNames.NpcCorporation(n.CorporationId, n.Corporation);
    public string    Alliance    { get; } = n.Alliance;

    // Only the docked rows carry an owner or a viewer of their own; a planet is a place.
    public bool HasLocation    { get; } = n.LocationId    > 0;
    public bool HasCorporation { get; } = n.CorporationId > 0 && n.Corporation.Length > 0;
    public bool HasAlliance    { get; } = n.AllianceId    > 0 && n.Alliance.Length    > 0;
    public bool HasType        { get; } = n.TypeId        > 0;

    private static EntityNavigator Nav => EntityNavigator.Instance;

    /// <summary>An NPC station opens in the entity browser; a player structure has its own tool.</summary>
    public ReactiveCommand<Unit, Unit> OpenCommand { get; } = ReactiveCommand.Create(() =>
    {
        if (n.IsNpc) EntityNavigator.Instance.Entity(EntityKind.Station, n.LocationId);
        else         EntityNavigator.Instance.Structure(n.LocationId);
    });

    public ReactiveCommand<Unit, Unit> OpenCorpCommand { get; } = ReactiveCommand.Create(() =>
        EntityNavigator.Instance.Entity(
            n.IsNpc ? EntityKind.NpcCorp : EntityKind.PlayerCorp, n.CorporationId));

    public ReactiveCommand<Unit, Unit> OpenAllianceCommand { get; } = ReactiveCommand.Create(() =>
        EntityNavigator.Instance.Entity(EntityKind.Alliance, n.AllianceId));

    public ReactiveCommand<Unit, Unit> OpenTypeCommand { get; } = ReactiveCommand.Create(() =>
        EntityNavigator.Instance.Item(n.TypeId));
    public string    Power     { get; } = n.Power > 0 ? n.Power.ToString("N0") : "";
    public string    Workforce { get; } = n.Workforce > 0 ? n.Workforce.ToString("N0") : "";
    /// <summary>Reagent yield per hour, named by planet type — Lava gives Magmatic Gas, Ice
    /// gives Superionic Ice. Blank on every other planet, which carries none.</summary>
    public string    Reagent   { get; } = n.ReagentPerHour <= 0 ? "" : n.Reagent switch
    {
        SystemViewService.PlanetReagent.MagmaticGas   => string.Format(MapText.ReagentMagmaticGasPerHour, n.ReagentPerHour),
        SystemViewService.PlanetReagent.SuperionicIce => string.Format(MapText.ReagentSuperionicIcePerHour, n.ReagentPerHour),
        _                                             => "",
    };
    public string    ReagentColor { get; } = n.Reagent == SystemViewService.PlanetReagent.SuperionicIce ? "#7fc8e8" : "#e08a4a";
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

public class AgentVm
{
    public string Location    { get; }
    public string Name        { get; }
    public string Corporation { get; }
    public string Division    { get; }
    public string AgentType   { get; }
    public string Level       { get; }
    public string Locator     { get; }

    public bool HasCorporation { get; }
    public bool HasStation     { get; }

    public AgentVm(SystemViewService.AgentRow a)
    {
        Location    = SdeNames.Station(a.StationId, a.Location);
        Name        = SdeNames.Agent(a.AgentId, a.Name);
        Corporation = SdeNames.NpcCorporation(a.CorporationId, a.Corporation);
        Division    = SdeNames.Get(SdeNameKind.NpcCorporationDivision, a.DivisionId, a.Division);
        AgentType   = a.AgentType;
        Level       = a.Level.ToString();
        Locator     = a.IsLocator ? MapText.AgentLocator : "";

        HasCorporation = a.CorporationId > 0 && a.Corporation.Length > 0;
        HasStation     = a.StationId     > 0 && a.Location.Length    > 0;

        // Agents, their corporations and the stations they sit in are all NPC entities, so all
        // three go to the same tool — the kind is what picks the tab.
        OpenCommand        = ReactiveCommand.Create(() => Nav.Entity(EntityKind.Agent,   a.AgentId));
        OpenCorpCommand    = ReactiveCommand.Create(() => Nav.Entity(EntityKind.NpcCorp, a.CorporationId));
        OpenStationCommand = ReactiveCommand.Create(() => Nav.Entity(EntityKind.Station, a.StationId));
    }

    private static EntityNavigator Nav => EntityNavigator.Instance;

    public ReactiveCommand<Unit, Unit> OpenCommand        { get; }
    public ReactiveCommand<Unit, Unit> OpenCorpCommand    { get; }
    public ReactiveCommand<Unit, Unit> OpenStationCommand { get; }
}

public class SystemEventVm(SystemViewService.SystemEvent e) : IconRowVm
{
    public string When    { get; } = e.When.UtcDateTime.ToString("yyyy-MM-dd HH:mm");
    public string Kind    { get; } = e.Kind switch
    {
        SystemViewService.SystemEventKind.SovereigntyGained => MapText.SovEventGained,
        SystemViewService.SystemEventKind.SovereigntyLost   => MapText.SovEventLost,
        _                                                   => e.Kind.ToString(),
    };
    public string Summary { get; } = e.Summary;
    public string KindColor { get; } = e.Kind switch
    {
        SystemViewService.SystemEventKind.SovereigntyGained => "#5fbf7a",
        SystemViewService.SystemEventKind.SovereigntyLost   => "#e0574a",
        _                                                   => "#8a8a9a",
    };

    protected override string? IconUrl =>
        e.AllianceId is { } a and > 0 ? $"https://images.evetech.net/alliances/{a}/logo?size=32" : null;
}

/// <summary>
/// A character shown with their portrait, corp and alliance. Each image is fetched from the
/// shared cache only when the row is built, so a page of intel costs three small requests per
/// distinct pilot on first view and none thereafter.
/// </summary>
public class IntelFaceVm : ReactiveObject
{
    private readonly long _charId, _corpId, _allianceId;

    public string  Name       { get; }
    public string  Ship       { get; }
    public bool    HasShip    { get; }
    /// <summary>Bound as the command parameter for opening the hull in the Item Browser.</summary>
    public int     ShipTypeId { get; }

    /// <summary>Named so the badges can say who they are. A corp or alliance logo with no name is
    /// a picture of a shape, recognisable only to someone who already knew.</summary>
    public string CorporationName { get; }
    public string AllianceName    { get; }

    public bool HasCorporation { get; }
    public bool HasAlliance    { get; }

    public IntelFaceVm(long charId, string name, string? ship, int shipTypeId, long corpId, long allianceId,
                       string corpName = "", string allianceName = "")
    {
        _charId = charId; _corpId = corpId; _allianceId = allianceId;
        ShipTypeId = shipTypeId;
        Name    = name;
        // The hull as the interface names it, once the parser has recognised it; the raw line is
        // still on the row's tooltip.
        Ship    = ship is null ? "" : shipTypeId > 0 ? SdeNames.Type(shipTypeId, ship) : ship;
        HasShip = !string.IsNullOrEmpty(ship);

        // Fall back to the id when the name cache has not caught up: "Corporation 98000000" is
        // still something you can look up, where a blank tooltip is not.
        CorporationName = corpName.Length > 0 ? corpName : corpId     > 0 ? string.Format(MapText.CorporationNumbered, corpId)     : "";
        AllianceName    = allianceName.Length > 0 ? allianceName : allianceId > 0 ? string.Format(MapText.AllianceNumbered, allianceId) : "";

        HasCorporation = corpId     > 0;
        HasAlliance    = allianceId > 0;

        OpenCommand         = ReactiveCommand.Create(() => Nav.Entity(EntityKind.Pilot,      _charId));
        OpenCorpCommand     = ReactiveCommand.Create(() => Nav.Entity(EntityKind.PlayerCorp, _corpId));
        OpenAllianceCommand = ReactiveCommand.Create(() => Nav.Entity(EntityKind.Alliance,   _allianceId));
        OpenShipCommand     = ReactiveCommand.Create(() => Nav.Item(ShipTypeId));
    }

    private static EntityNavigator Nav => EntityNavigator.Instance;

    public ReactiveCommand<Unit, Unit> OpenCommand         { get; }
    public ReactiveCommand<Unit, Unit> OpenCorpCommand     { get; }
    public ReactiveCommand<Unit, Unit> OpenAllianceCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenShipCommand     { get; }

    private Bitmap? _portrait, _corpLogo, _allianceLogo, _shipIcon;
    public Bitmap? Portrait     { get => _portrait;     private set => this.RaiseAndSetIfChanged(ref _portrait, value); }
    public Bitmap? CorpLogo     { get => _corpLogo;     private set => this.RaiseAndSetIfChanged(ref _corpLogo, value); }
    public Bitmap? AllianceLogo { get => _allianceLogo; private set => this.RaiseAndSetIfChanged(ref _allianceLogo, value); }
    public Bitmap? ShipIcon     { get => _shipIcon;     private set => this.RaiseAndSetIfChanged(ref _shipIcon, value); }

    public Task LoadIconsAsync() => Task.WhenAll(
        Fetch(_charId     > 0 ? $"https://images.evetech.net/characters/{_charId}/portrait?size=32"   : null, b => Portrait = b),
        Fetch(_corpId     > 0 ? $"https://images.evetech.net/corporations/{_corpId}/logo?size=32"     : null, b => CorpLogo = b),
        Fetch(_allianceId > 0 ? $"https://images.evetech.net/alliances/{_allianceId}/logo?size=32"    : null, b => AllianceLogo = b),
        Fetch(ShipTypeId  > 0 ? $"https://images.evetech.net/types/{ShipTypeId}/icon?size=32"        : null, b => ShipIcon = b));

    private static async Task Fetch(string? url, Action<Bitmap?> set)
    {
        if (url is null) return;
        var bmp = await EveImageCache.GetAsync(url);
        Dispatcher.UIThread.Post(() => set(bmp));
    }
}

/// <summary>
/// One intel sighting. Superseded reports are shown rather than hidden — a sighting being out
/// of date is not the same as it never having happened, and the run of who came through a
/// system is what the tab is for.
/// </summary>
public class IntelRowVm(SystemViewService.IntelRow r)
{
    /// <summary>Which sighting this is, for a refresh to find it again: reports have no id of
    /// their own here.</summary>
    public string Key       { get; } = $"{r.When.UtcTicks}|{r.ReporterId}|{r.Channel}|{r.Message}";
    /// <summary>What a refresh compares: a report gone obsolete is redrawn.</summary>
    public string Signature { get; } = $"{r.Obsolete}|{r.PlayerCount}|{r.Pilots.Count}";
    public string When     { get; } = r.When.UtcDateTime.ToString("yyyy-MM-dd HH:mm");
    public string Count    { get; } = r.PlayerCount.ToString("N0");
    public string Note     { get; } = r.Note;
    public string Reporter { get; } = r.Reporter;
    public string Channel  { get; } = r.Channel;

    public List<IntelFaceVm> Pilots { get; } =
        [.. r.Pilots.Select(p => new IntelFaceVm(p.CharacterId, p.Name, p.Ship, p.ShipTypeId,
                                                 p.CorporationId, p.AllianceId,
                                                 p.CorporationName, p.AllianceName))];

    /// <summary>The reporter, shown the same way as the pilots they called.</summary>
    public IntelFaceVm ReportedBy { get; } =
        new(r.ReporterId, r.Reporter, null, 0, r.ReporterCorpId, r.ReporterAllianceId,
            r.ReporterCorpName, r.ReporterAllianceName);

    public Task LoadIconsAsync() =>
        Task.WhenAll(Pilots.Select(p => p.LoadIconsAsync()).Append(ReportedBy.LoadIconsAsync()));

    /// <summary>The line exactly as posted, shown on hover. The parse is a best effort, and
    /// seeing the original beside it is how a wrong one gets noticed.</summary>
    public string Message  { get; } = r.Message;

    /// <summary>"No visual": someone is there but the reporter could not see them.</summary>
    public string NoVisual   { get; } = r.NoVisual ? MapText.IntelNoVisual : "";

    public bool   IsStanding { get; } = !r.Obsolete;
    public string Status     { get; } = r.Obsolete ? MapText.IntelSuperseded : MapText.IntelStanding;

    /// <summary>
    /// Whether standing-versus-superseded is worth drawing attention to.
    ///
    /// It says something about a sighting from the last couple of hours — somebody may still be
    /// there. On one from last October it says nothing: everything that old is superseded or
    /// simply stale, and emphasising the handful that happen not to be superseded highlights an
    /// accident of which pilots were seen again rather than anything about the report.
    /// </summary>
    private bool IsRecent { get; } = DateTimeOffset.UtcNow - r.When < TimeSpan.FromHours(2);

    /// <summary>Uniform: the grid is a log of past activity, and dimming most of it made the
    /// few rows nothing had superseded look significant when they were not.</summary>
    public string RowOpacity => "1.0";

    /// <summary>Graded only while the report is current. On historical rows the count is just a
    /// number, and colouring it implied a severity the age had already made moot.</summary>
    public string CountColor => !IsRecent ? "#c8c8d8"
                              : r.PlayerCount >= 10 ? "#d43f2f"
                              : r.PlayerCount >= 4  ? "#e0913c"
                              : "#c8c8d8";
}

public class GateVm(SystemViewService.GateRow r)
{
    public int    SystemId    { get; } = r.SystemId;
    public string Name        { get; } = SdeNames.SolarSystem(r.SystemId, r.Name);
    public string RegionName  { get; } = SdeNames.Region(r.RegionId, r.RegionName);
    public bool   OutOfRegion { get; } = r.OutOfRegion;
    /// <summary>The rounded band — the number that decides high / low / null.</summary>
    public string Security      { get; } = EveConsole.Services.SecurityColors.Text(r.Security);

    /// <summary>True security, shown beside the band rather than instead of it.</summary>
    public string SecurityTrue  { get; } = EveConsole.Services.SecurityColors.TrueText(r.Security);
    public string SecurityColor { get; } = EveConsole.Services.SecurityColors.Hex(r.Security);
    public string SecurityTip   { get; } = EveConsole.Services.SecurityColors.Tip(r.Security);

    /// <summary>Where the gate goes. Opening it navigates the map to that system, which is the
    /// same thing clicking the node would do.</summary>
    public bool HasSystemLink => SystemId > 0 && Name.Length > 0;
    public void OpenSystem() => EntityNavigator.Instance.System(SystemId);
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
        // ⚠️ Registered here rather than where the axes are built: several of these replace their
        // axis arrays wholesale on every reload, so anything holding the arrays would restyle the
        // set that was on screen two loads ago. Only a weak reference is kept.
        ChartPaint.TrackAxesOf(this);

        _svc   = svc;
        _kills = kills;

        OpenInItemBrowserCommand = ReactiveCommand.Create<int>(
            typeId => { if (typeId > 0) NavigateToItemAction?.Invoke(typeId); });
        OpenInItemBrowserCommand.ThrownExceptions.Subscribe(_ => { });

        OpenSovAllianceCommand = ReactiveCommand.Create(() =>
            EntityNavigator.Instance.Entity(EntityKind.Alliance, _sovAllianceId));
        OpenSovCorpCommand = ReactiveCommand.Create(() =>
            EntityNavigator.Instance.Entity(EntityKind.PlayerCorp, _sovCorporationId));
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

    /// <summary>True security, shown beside the rounded band rather than instead of it.</summary>
    private string _securityTrue = "";
    public string SecurityTrue { get => _securityTrue; private set => this.RaiseAndSetIfChanged(ref _securityTrue, value); }

    private string _securityTip = "";
    public string SecurityTip { get => _securityTip; private set => this.RaiseAndSetIfChanged(ref _securityTip, value); }

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

    // ── Header links ──────────────────────────────────────────────────────────
    //
    // Region and constellation frame themselves on the map; the pirate faction opens in the NPC
    // entity browser. Every id comes off the header the page already loads.
    private int _regionId, _constellationId, _pirateFactionId;

    public bool HasRegionLink        => _regionId        > 0 && Region.Length        > 0;
    public bool HasConstellationLink => _constellationId > 0 && Constellation.Length > 0;
    public bool HasPirateLink        => _pirateFactionId > 0 && LocalPirates.Length  > 0;

    public void OpenRegion() => EntityNavigator.Instance.Region(_regionId);
    /// <summary>⚠️ By name — the map graph is keyed on constellation name, not id — and by the
    /// English one: <see cref="Constellation"/> is what the page shows.</summary>
    public void OpenConstellation() => EntityNavigator.Instance.Constellation(_header?.Constellation ?? "");
    public void OpenPirates() => EntityNavigator.Instance.Entity(EntityKind.Faction, _pirateFactionId);

    private string _holder = "";
    public string Holder { get => _holder; private set => this.RaiseAndSetIfChanged(ref _holder, value); }

    // The sovereignty holder, split so each half can be clicked. Holder above stays as the one
    // combined string, since it is still what a system with neither reads as ("Unclaimed").
    private string _sovAlliance = "";
    public string SovAlliance { get => _sovAlliance; private set => this.RaiseAndSetIfChanged(ref _sovAlliance, value); }

    private string _sovCorporation = "";
    public string SovCorporation { get => _sovCorporation; private set => this.RaiseAndSetIfChanged(ref _sovCorporation, value); }

    private long _sovAllianceId, _sovCorporationId;

    private bool _hasSovAlliance;
    public bool HasSovAlliance { get => _hasSovAlliance; private set => this.RaiseAndSetIfChanged(ref _hasSovAlliance, value); }

    private bool _hasSovCorporation;
    public bool HasSovCorporation { get => _hasSovCorporation; private set => this.RaiseAndSetIfChanged(ref _hasSovCorporation, value); }

    /// <summary>Shown only when neither half is a link, so "Unclaimed" still appears.</summary>
    public bool HasSovNeither => !_hasSovAlliance && !_hasSovCorporation;

    public ReactiveCommand<Unit, Unit> OpenSovAllianceCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenSovCorpCommand     { get; }

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

    /// <summary>
    /// The same structures as <see cref="Structures"/>, split for the Overview beside the gates.
    ///
    /// <para>Split rather than one list with a Kind column because the two answer different
    /// questions and lead to different places: an NPC station is a fixture of the system and opens
    /// in the entity browser, a player structure is somebody's and opens in the Structure Browser.
    /// The Structures tab keeps the combined view for comparing them.</para>
    /// </summary>
    public ObservableCollection<SysStructureVm> PlayerStructures { get; } = [];
    public ObservableCollection<SysStructureVm> NpcStations      { get; } = [];

    public bool HasPlayerStructures => PlayerStructures.Count > 0;
    public bool HasNpcStations      => NpcStations.Count > 0;

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

    public ObservableCollection<IntelRowVm> Intel { get; } = [];

    private string _intelSummary = "";
    public string IntelSummary { get => _intelSummary; private set => this.RaiseAndSetIfChanged(ref _intelSummary, value); }

    private bool _hasIntel;
    public bool HasIntel { get => _hasIntel; private set => this.RaiseAndSetIfChanged(ref _hasIntel, value); }

    public string IntelNote => MapText.NoIntelNote;

    private string _killsNote = "";
    public string KillsNote { get => _killsNote; private set => this.RaiseAndSetIfChanged(ref _killsNote, value); }

    /// <summary>Raised when a gate is clicked, so the host can navigate.</summary>
    public Func<int, Task>? NavigateToSystem { get; set; }

    public ReactiveCommand<int, Unit>? OpenGateCommand { get; set; }

    /// <summary>Set by the shell so a hull named in intel opens in the Item Browser.</summary>
    public Action<int>? NavigateToItemAction { get; set; }

    public ReactiveCommand<int, Unit> OpenInItemBrowserCommand { get; }

    /// <summary>Opens the killmail the row describes, for the list's double-click.</summary>
    public ReactiveCommand<KillmailListRowVm, Unit> OpenKillCommand { get; } =
        ReactiveCommand.Create<KillmailListRowVm>(k => EntityNavigator.Instance.Killmail(k.KillMailId));

    // ── Load ─────────────────────────────────────────────────────────────────

    /// <summary>Bumped per load so background icon fetches from a previous system can tell
    /// they are stale and drop their results.</summary>
    private int _loadGeneration;

    public async Task LoadAsync(int systemId)
    {
        var generation = ++_loadGeneration;

        // The rows below take their names once, as they are built.
        await SdeNames.EnsureLoadedAsync();

        var header    = await _svc.GetHeaderAsync(systemId);
        if (header is null) return;
        _systemId = systemId;

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
        var intel      = await _svc.GetIntelAsync(systemId);
        var incursion  = await _svc.GetIncursionAsync(header.ConstellationId);

        // The kill list is the same query and the same row type the Kills tool uses, so the
        // formatting and icons match the rest of the app rather than being reinvented here. By
        // the system's id: the tool's text filter would also bring in every system and region
        // whose name contains this one.
        var killPage = await _kills.GetListAsync(0, 50, solarSystemId: header.SystemId);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            ApplyHeader(header);
            ApplyIncursion(incursion, header.SystemId);

            Fill(SovStructures, sovStructs.Select(s => new SovStructureVm(s)));
            Fill(Events,     events.Select(e => new SystemEventVm(e)));
            Fill(SovChanges, events.Where(e => e.Kind is SystemViewService.SystemEventKind.SovereigntyGained
                                                     or SystemViewService.SystemEventKind.SovereigntyLost)
                                   .Select(e => new SystemEventVm(e)));
            Fill(Celestials, tree.Select(n => new CelestialNodeVm(n)));
            BuildGraphs(history);
            BuildSparklines(hourly);
            BuildAdmGraph(admHist);
            BuildIndexGraph(indexHist);
            Fill(Structures, structures.Select(s => new SysStructureVm(s)));
            Fill(PlayerStructures, structures.Where(s => !s.IsNpc).Select(s => new SysStructureVm(s)));
            // In the order of the names shown; the service sorts by the English.
            Fill(NpcStations,      structures.Where(s =>  s.IsNpc).Select(s => new SysStructureVm(s))
                                             .OrderBy(s => s.Name, StringComparer.CurrentCulture));
            this.RaisePropertyChanged(nameof(HasPlayerStructures));
            this.RaisePropertyChanged(nameof(HasNpcStations));
            Fill(Kills, killPage.Rows.Select(r => new KillmailListRowVm(r)));
            // In the order of the names shown; the service sorts by the English.
            Fill(Gates, gates.Select(g => new GateVm(g)).OrderBy(g => g.Name, StringComparer.CurrentCulture));
            Fill(Agents, agents
                .OrderBy(a => SdeNames.Station(a.StationId, a.Location), StringComparer.CurrentCulture)
                .ThenBy(a => a.Level)
                .ThenBy(a => SdeNames.Agent(a.AgentId, a.Name), StringComparer.CurrentCulture)
                .Select(a => new AgentVm(a)));
            Fill(Intel, intel.Select(i => new IntelRowVm(i)));
            HasIntel = intel.Count > 0;
            IntelSummary = intel.Count == 0
                ? ""
                : string.Format(MapText.IntelSummary, intel.Count, intel.Count(i => !i.Obsolete));
            AgentNote = agents.Count == 0
                ? MapText.AgentsNone
                : string.Format(MapText.AgentsSummary, agents.Count, agents.Select(a => a.Location).Distinct().Count());

            // Stated plainly: the history only reaches back as far as the snapshots, and
            // without saying so an empty list reads as "nothing ever happened here".
            HistoryNote = since is null
                ? MapText.SovHistoryNone
                : string.Format(MapText.SovHistoryNote, since);

            KillsNote = killPage.Rows.Count == 0
                ? MapText.KillsNone
                : string.Format(killPage.HasMore ? MapText.KillsMostRecentMore : MapText.KillsMostRecent,
                                killPage.Rows.Count);
        });

        await ApplyExtrasAsync(generation);

        // Deliberately not awaited. The caller reveals the page as soon as this method returns,
        // so awaiting the icons kept the whole page off screen behind several hundred image
        // requests — on a cold cache that is by far the largest part of opening a system, while
        // the data above is a few hundred milliseconds.
        _ = LoadImagesAsync(header, generation);
    }

    /// <summary>
    /// The header: names, security, sovereignty, indices and the 1 h / 24 h counts. Every setter
    /// raises only on a change, so a refresh that finds nothing new moves nothing. UI thread.
    /// </summary>
    private void ApplyHeader(SystemViewService.SystemHeader header)
    {
        // The header keeps the English (the constellation link finds its constellation by
        // it); these are what the page shows.
        _header = header;
        Name          = SdeNames.SolarSystem(header.SystemId, header.Name);
        Region        = SdeNames.Region(header.RegionId, header.Region);
        Constellation = SdeNames.Constellation(header.ConstellationId, header.Constellation);
        Security      = EveConsole.Services.SecurityColors.Text(header.Security);
        SecurityTrue  = EveConsole.Services.SecurityColors.TrueText(header.Security);
        SecurityColor = EveConsole.Services.SecurityColors.Hex(header.Security);
        SecurityTip   = EveConsole.Services.SecurityColors.Tip(header.Security);
        SecurityClass = header.SecurityClass;
        LocalPirates  = SdeNames.Faction(header.LocalPirateFactionId, header.LocalPirates);

        _regionId        = header.RegionId;
        _constellationId = header.ConstellationId;
        _pirateFactionId = header.LocalPirateFactionId;
        this.RaisePropertyChanged(nameof(HasRegionLink));
        this.RaisePropertyChanged(nameof(HasConstellationLink));
        this.RaisePropertyChanged(nameof(HasPirateLink));

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
        if (header.Power > 0)                bits.Add(string.Format(MapText.ProductionPower, header.Power));
        if (header.Workforce > 0)            bits.Add(string.Format(MapText.ProductionWorkforce, header.Workforce));
        if (header.MagmaticGasPerHour > 0)   bits.Add(string.Format(MapText.ProductionMagmaticGas, header.MagmaticGasPerHour));
        if (header.SuperionicIcePerHour > 0) bits.Add(string.Format(MapText.ProductionSuperionicIce, header.SuperionicIcePerHour));
        Production    = string.Join("  ·  ", bits);
        HasProduction = bits.Count > 0;
        Holder = string.IsNullOrEmpty(header.AllianceName)
            ? (string.IsNullOrEmpty(header.CorporationName) ? MapText.SovUnclaimed : header.CorporationName)
            : string.IsNullOrEmpty(header.CorporationName)
                ? header.AllianceName
                : $"{header.AllianceName}  ·  {header.CorporationName}";

        SovAlliance       = header.AllianceName;
        SovCorporation    = header.CorporationName;
        _sovAllianceId    = header.AllianceId    ?? 0;
        _sovCorporationId = header.CorporationId ?? 0;
        HasSovAlliance    = _sovAllianceId    > 0 && SovAlliance.Length    > 0;
        HasSovCorporation = _sovCorporationId > 0 && SovCorporation.Length > 0;
        this.RaisePropertyChanged(nameof(HasSovNeither));

        PlanetCount = header.Planets.ToString("N0");
        MoonCount   = header.Moons.ToString("N0");
        BeltCount   = header.Belts.ToString("N0");
        Jumps     = $"{header.Jumps1h:N0} / {header.Jumps24h:N0}";
        ShipKills = $"{header.ShipKills1h:N0} / {header.ShipKills24h:N0}";
        NpcKills  = $"{header.NpcKills1h:N0} / {header.NpcKills24h:N0}";
        PodKills  = $"{header.PodKills1h:N0} / {header.PodKills24h:N0}";
    }

    /// <summary>
    /// Fills in icons after the page is already on screen.
    ///
    /// <paramref name="generation"/> guards against a second system being opened while these
    /// are still in flight: without it a slow logo from the previous system could land on top
    /// of the new one.
    /// </summary>
    private async Task LoadImagesAsync(SystemViewService.SystemHeader header, int generation)
    {
        try
        {
            // Snapshot on the UI thread. These are ObservableCollections that opening another
            // system clears and refills, and enumerating one mid-change throws.
            SovStructureVm[]    sov    = [];
            CelestialNodeVm[]   cel    = [];
            SysStructureVm[]    str    = [];
            SystemEventVm[]     evt    = [];
            KillmailListRowVm[] kills  = [];
            IntelRowVm[]        intel  = [];

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                sov   = [.. SovStructures];
                cel   = [.. Celestials.Where(c => c.Icon is null).Take(120)];
                str   = [.. Structures];
                evt   = [.. Events.Take(40)];
                kills = [.. Kills];
                // Every row, not just the first screenful. This was capped at 40 back when all
                // image requests were fired at once and a cap was the only thing stopping a
                // system page launching a thousand of them together — but the cap is precisely
                // what made icons stop partway down the list. EveImageCache now allows twelve at
                // a time and serves waiters in order, so the visible rows still resolve first
                // and the rest fill in behind them rather than never arriving.
                intel = [.. Intel];
            });

            if (generation != _loadGeneration) return;

            var url = header.AllianceId is { } a and > 0
                ? $"https://images.evetech.net/alliances/{a}/logo?size=64"
                : header.CorporationId is { } c and > 0
                    ? $"https://images.evetech.net/corporations/{c}/logo?size=64"
                    : null;

            var logo = url is null ? null : await EveImageCache.GetAsync(url);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation == _loadGeneration) HolderLogo = logo;
            });

            await Task.WhenAll(
                Task.WhenAll(sov  .Select(x => x.LoadIconAsync())),
                Task.WhenAll(cel  .Select(x => x.LoadIconAsync())),
                Task.WhenAll(str  .Select(x => x.LoadIconAsync())),
                Task.WhenAll(evt  .Select(x => x.LoadIconAsync())),
                Task.WhenAll(intel.Select(x => x.LoadIconsAsync())),
                Task.WhenAll(kills.Select(x => x.LoadImagesAsync())));
        }
        catch
        {
            // An icon that will not load is not worth surfacing, and must not reach the
            // unobserved-task handler now that nothing awaits this.
        }
    }

    /// <summary>
    /// The four small hourly charts on the Overview. Bounded by how much hourly detail is kept,
    /// which is one day by default — so the span is stated rather than labelled "48h" when it
    /// may hold less.
    /// </summary>
    private void BuildSparklines(List<SystemViewService.HourPoint> hourly)
    {
        _newestHour = hourly.Count > 0 ? hourly[^1].Hour : null;
        if (hourly.Count == 0)
        {
            HourJumpSeries = HourNpcSeries = HourShipSeries = HourPodSeries = [];
            HourNote = MapText.HourlyNone;
            return;
        }

        var span = (hourly[^1].Hour - hourly[0].Hour).TotalHours + 1;
        HourNote = string.Format(MapText.HourlyNote, span);

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
                LabelsPaint = ChartPaint.FaintLabels,
                TextSize    = 9,
                MinStep     = Math.Max(1, hourly.Count / 6),
                SeparatorsPaint = null,
            },
        ];
        HourYAxes =
        [
            new Axis
            {
                LabelsPaint = ChartPaint.FaintLabels,
                TextSize    = 9,
                MinLimit    = 0,
                SeparatorsPaint = new SolidColorPaint(Palette.Sk("BorderSubtle")) { StrokeThickness = 1 },
            },
        ];
    }

    private void BuildGraphs(List<SystemViewService.HistoryPoint> history)
    {
        if (history.Count == 0)
        {
            JumpSeries = ShipKillSeries = PodKillSeries = NpcKillSeries = [];
            GraphNote  = MapText.GraphNone;
            return;
        }

        GraphNote = string.Format(MapText.GraphNote, history[0].Day, history[^1].Day);

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

        JumpSeries     = Line(MapText.SeriesJumps, history.Select(h => h.Jumps),     "#6FC8F0");
        ShipKillSeries = Line(MapText.ShipKills,   history.Select(h => h.ShipKills), "#FF6A3D");
        PodKillSeries  = Line(MapText.PodKills,    history.Select(h => h.PodKills),  "#F0D040");
        NpcKillSeries  = Line(MapText.NpcKills,    history.Select(h => h.NpcKills),  "#7FD070");

        var labels = history.Select(h => h.Day.ToString("MM-dd")).ToArray();
        HistoryXAxes =
        [
            new Axis
            {
                Labels        = labels,
                LabelsPaint   = ChartPaint.Labels,
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
                LabelsPaint     = ChartPaint.Labels,
                TextSize        = 10,
                MinLimit        = 0,
                SeparatorsPaint = new SolidColorPaint(Palette.Sk("BorderSubtle")) { StrokeThickness = 1 },
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
            AdmNote   = MapText.AdmNone;
            return;
        }

        AdmNote = string.Format(MapText.AdmNote, points[0].Day, points[^1].Day);

        AdmSeries =
        [
            new LineSeries<double>
            {
                Name           = MapText.SeriesAdm,
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
                LabelsPaint     = ChartPaint.Labels,
                TextSize        = 10,
                MinLimit        = 0,
                MaxLimit        = 6,
                MinStep         = 1,
                SeparatorsPaint = new SolidColorPaint(Palette.Sk("BorderSubtle")) { StrokeThickness = 1 },
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

    /// <summary>An activity as the chart legend names it; ESI's own word, capitalised, for one not
    /// listed.</summary>
    private static string PrettyActivity(string a) => a switch
    {
        "manufacturing"                   => MapText.ActivityManufacturing,
        "researching_time_efficiency"     => MapText.ActivityTimeEfficiency,
        "researching_material_efficiency" => MapText.ActivityMaterialEfficiency,
        "copying"                         => MapText.ActivityCopying,
        "invention"                       => MapText.ActivityInvention,
        "reaction"                        => MapText.ActivityReaction,
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
            IndexNote   = MapText.IndexNone;
            return;
        }

        // Each activity is stored independently, so pad against the union of days rather than
        // assuming they all start together — otherwise a late-arriving activity would be drawn
        // shifted left against the others.
        var days = series.SelectMany(s => s.Points.Select(p => p.Day)).Distinct().OrderBy(d => d).ToList();

        IndexNote = string.Format(MapText.IndexNote, days[0], days[^1]);

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
                LabelsPaint     = ChartPaint.Labels,
                TextSize        = 10,
                MinLimit        = 0,
                SeparatorsPaint = new SolidColorPaint(Palette.Sk("BorderSubtle")) { StrokeThickness = 1 },
            },
        ];
    }

    private static Axis DayAxis(string[] labels) => new()
    {
        Labels          = labels,
        LabelsPaint     = ChartPaint.Labels,
        TextSize        = 10,
        MinStep         = Math.Max(1, labels.Length / 10),
        SeparatorsPaint = null,
    };

    // ── Refresh ──────────────────────────────────────────────────────────────

    private int _systemId;
    private int _refreshing;
    private DateTimeOffset? _newestHour;

    /// <summary>
    /// Reads again what moves — the header's counts, intel, recent kills, sovereignty hubs, the
    /// hourly charts when a new hour has come in, and the map's extras (zone, bridges, Thera and
    /// Turnur, who is here now) — and changes only what changed. Lists are merged row by row,
    /// never cleared and refilled: a new kill slides in at the top and nothing else moves. What
    /// does not move (celestials, structures, agents, the long graphs) stays as loaded. The map
    /// tool calls this every 30 s while the page is the tab on screen.
    /// </summary>
    public async Task RefreshAsync()
    {
        if (_systemId == 0 || Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        var generation = _loadGeneration;
        try
        {
            var header     = await _svc.GetHeaderAsync(_systemId);
            if (header is null) return;
            var intel      = await _svc.GetIntelAsync(_systemId);
            var incursion  = await _svc.GetIncursionAsync(header.ConstellationId);
            var sovStructs = await _svc.GetSovStructuresAsync(_systemId);
            var hourly     = await _svc.GetHourlyHistoryAsync(_systemId);
            var killPage   = await _kills.GetListAsync(0, 50, solarSystemId: _systemId);
            if (generation != _loadGeneration) return;

            var added = new List<object>();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ApplyHeader(header);
                ApplyIncursion(incursion, header.SystemId);

                added.AddRange(Merge(Intel, [.. intel.Select(i => new IntelRowVm(i))], r => r.Key, (a, b) => a.Signature == b.Signature));
                HasIntel = intel.Count > 0;
                IntelSummary = intel.Count == 0 ? "" : string.Format(MapText.IntelSummary, intel.Count, intel.Count(i => !i.Obsolete));

                added.AddRange(Merge(Kills, [.. killPage.Rows.Select(r => new KillmailListRowVm(r))], k => k.KillMailId, (_, _) => true));
                KillsNote = killPage.Rows.Count == 0
                    ? MapText.KillsNone
                    : string.Format(killPage.HasMore ? MapText.KillsMostRecentMore : MapText.KillsMostRecent, killPage.Rows.Count);

                added.AddRange(Merge(SovStructures, [.. sovStructs.Select(s => new SovStructureVm(s))], s => s.Key, (a, b) => a.Signature == b.Signature));

                // The four small charts only when an hour has been added: redrawn every time,
                // they would visibly blink.
                var newest = hourly.Count > 0 ? hourly[^1].Hour : (DateTimeOffset?)null;
                if (newest != _newestHour) BuildSparklines(hourly);
            });
            await ApplyExtrasAsync(generation);

            // Icons for the rows that came in; the rest already have theirs.
            await Task.WhenAll(added.Select(r => r switch
            {
                IntelRowVm i        => i.LoadIconsAsync(),
                KillmailListRowVm k => k.LoadImagesAsync(),
                SovStructureVm s    => s.LoadIconAsync(),
                _                   => Task.CompletedTask,
            }));
        }
        catch
        {
            // A refresh that fails leaves the page as it was; the next one tries again.
        }
        finally { Interlocked.Exchange(ref _refreshing, 0); }
    }

    /// <summary>
    /// Brings <paramref name="target"/> to <paramref name="fresh"/>'s rows and order by moving,
    /// inserting, replacing and removing single rows — never clearing — so a list on screen only
    /// moves where something changed. A row whose key is kept and whose content is the same is
    /// left alone, its icons and all. Returns the rows put in, which need their icons.
    /// </summary>
    internal static List<T> Merge<T, TKey>(ObservableCollection<T> target, IReadOnlyList<T> fresh,
                                           Func<T, TKey> key, Func<T, T, bool> same) where TKey : notnull
    {
        var added = new List<T>();
        var wanted = fresh.Select(key).ToHashSet();
        for (var i = target.Count - 1; i >= 0; i--)
            if (!wanted.Contains(key(target[i]))) target.RemoveAt(i);

        for (var i = 0; i < fresh.Count; i++)
        {
            var k = key(fresh[i]);
            if (i < target.Count && EqualityComparer<TKey>.Default.Equals(key(target[i]), k))
            {
                if (!same(target[i], fresh[i])) { target[i] = fresh[i]; added.Add(fresh[i]); }
                continue;
            }
            var at = -1;
            for (var j = i + 1; j < target.Count; j++)
                if (EqualityComparer<TKey>.Default.Equals(key(target[j]), k)) { at = j; break; }
            if (at >= 0)
            {
                target.Move(at, i);
                if (!same(target[i], fresh[i])) { target[i] = fresh[i]; added.Add(fresh[i]); }
            }
            else
            {
                target.Insert(i, fresh[i]);
                added.Add(fresh[i]);
            }
        }
        return added;
    }

    // ── Incursion ────────────────────────────────────────────────────────────

    private string _incursionText = "";
    /// <summary>The incursion in this system's constellation, or "" for none.</summary>
    public string IncursionText { get => _incursionText; private set => this.RaiseAndSetIfChanged(ref _incursionText, value); }

    private string _incursionColor = "#c8543f";
    public string IncursionColor { get => _incursionColor; private set => this.RaiseAndSetIfChanged(ref _incursionColor, value); }

    private void ApplyIncursion(SystemViewService.IncursionInfo? i, int systemId)
    {
        if (i is null) { IncursionText = ""; return; }
        var state = i.State.ToLowerInvariant() switch
        {
            "established" => MapText.IncursionEstablished,
            "mobilizing"  => MapText.IncursionMobilizing,
            "withdrawing" => MapText.IncursionWithdrawing,
            _             => i.State,
        };
        IncursionColor = i.State.ToLowerInvariant() switch
        {
            "established" => "#c8543f",
            "mobilizing"  => "#e0913c",
            _             => "#9a7a5a",
        };
        // The state leads the line, so it takes a capital where the language has them.
        if (state.Length > 0) state = char.ToUpper(state[0], System.Globalization.CultureInfo.CurrentCulture) + state[1..];
        IncursionText = string.Format(MapText.SysIncursionLine, state, i.Influence * 100,
                            SdeNames.Faction(i.FactionId, i.FactionName),
                            i.StagingSystemId == systemId ? MapText.SysIncursionStagingHere
                                                          : SdeNames.SolarSystem(i.StagingSystemId, i.StagingName))
                      + (i.HasBoss ? " · " + MapText.NodeBossUp : "");
    }

    // ── From the map: zone, bridges, Thera and Turnur, who is here now ─────────

    /// <summary>Set by the map tool: what it knows about a system that the system service does
    /// not — its Ansiblex zone, its jump bridges, its Thera and Turnur wormholes, and who is
    /// placed there now. Null outside the map tool.</summary>
    public Func<int, CancellationToken, Task<SystemMapExtras>>? ExtrasSource { get; set; }

    public ObservableCollection<SysBridgeVm> Bridges   { get; } = [];
    public ObservableCollection<SysHoleVm>   Wormholes { get; } = [];

    private string _zoneText = "";
    public string ZoneText { get => _zoneText; private set => this.RaiseAndSetIfChanged(ref _zoneText, value); }

    private string _zoneColor = "#8a8a9a";
    public string ZoneColor { get => _zoneColor; private set => this.RaiseAndSetIfChanged(ref _zoneColor, value); }

    private bool _hasZone;
    public bool HasZone { get => _hasZone; private set => this.RaiseAndSetIfChanged(ref _hasZone, value); }

    private string _hostilesNow = "";
    /// <summary>Hostiles placed here in the last 5 minutes, by intel or killmail; "" for none.</summary>
    public string HostilesNow { get => _hostilesNow; private set => this.RaiseAndSetIfChanged(ref _hostilesNow, value); }

    private string _ownNow = "";
    /// <summary>The capsuleer's characters here now; "" for none.</summary>
    public string OwnNow { get => _ownNow; private set => this.RaiseAndSetIfChanged(ref _ownNow, value); }

    private string _campaign = "";
    /// <summary>The sovereignty campaign here, scheduled or running; "" for none.</summary>
    public string Campaign { get => _campaign; private set => this.RaiseAndSetIfChanged(ref _campaign, value); }

    private string _weather = "";
    /// <summary>The metaliminal storm reported here, as EVE-Scout lists it; "" for none.</summary>
    public string Weather { get => _weather; private set => this.RaiseAndSetIfChanged(ref _weather, value); }

    private bool _hasBridges;
    public bool HasBridges { get => _hasBridges; private set => this.RaiseAndSetIfChanged(ref _hasBridges, value); }

    private bool _hasWormholes;
    public bool HasWormholes { get => _hasWormholes; private set => this.RaiseAndSetIfChanged(ref _hasWormholes, value); }

    private async Task ApplyExtrasAsync(int generation)
    {
        if (ExtrasSource is not { } source || _systemId == 0) return;
        SystemMapExtras extras;
        try { extras = await source(_systemId, CancellationToken.None); }
        catch { return; }
        if (generation != _loadGeneration) return;

        var id = _systemId;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (extras.Zone is { } z)
            {
                HasZone   = true;
                ZoneColor = MapCanvas.ZoneColors[Math.Clamp(z.Zone, 0, MapCanvas.ZoneColors.Length - 1)].ToString();
                ZoneText  = z.Zone == 0
                    ? MapText.SysZoneNone
                    : string.Format(MapText.SysZoneLine, z.Zone, z.DistanceLy ?? 0,
                                    SdeNames.SolarSystem(z.CapitalSystemId, z.CapitalName),
                                    JumpBridgeService.ZoneMultiplier(z.Zone) is var m and > 0
                                        ? string.Format(MapText.SysZoneCost, m) : MapText.SysZoneFree);
            }
            else HasZone = false;

            HostilesNow = extras.Hostiles is { Count: > 0 } h
                ? string.Format(MapText.SysHostilesNow, h.Count,
                    string.Join(", ", h.Pilots.Take(8).Select(p => p.Ship is { Length: > 0 } ship ? $"{p.Name} ({ship})" : p.Name))
                    + (h.Count > 8 ? ", …" : ""))
                : "";
            Campaign = extras.Campaigns is { Count: > 0 } fights
                ? string.Join("  ·  ", fights.Select(c => $"{CampaignText.Line(c, DateTimeOffset.UtcNow)} · {CampaignText.Times(c)}"))
                : "";
            Weather = extras.Storms is { Count: > 0 } storms
                ? string.Join(" · ", storms.Select(MapToolViewModel.StormLine))
                : "";
            OwnNow = extras.Own.Count > 0
                ? string.Format(MapText.SysOwnNow, string.Join(", ", extras.Own.Select(o => o.Name)))
                : "";

            Merge(Bridges, [.. extras.Bridges.Select(b => new SysBridgeVm(b, id))], b => b.OtherSystemId, (a, b) => a.Signature == b.Signature);
            HasBridges = Bridges.Count > 0;
            Merge(Wormholes, [.. extras.Holes.Select(c => new SysHoleVm(c, id))], w => w.Key, (a, b) => a.Signature == b.Signature);
            HasWormholes = Wormholes.Count > 0;
        });
    }

    private static void Fill<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var i in items) target.Add(i);
    }
}
