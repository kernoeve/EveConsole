using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using EveConsole.Services;
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
    public string Name     { get; } = r.Name;
    public string TypeName { get; } = r.TypeName;
    public string Owner    { get; } = r.Owner;
    public string Kind     { get; } = r.IsNpc ? "NPC" : "Player";
    public string KindColor { get; } = r.IsNpc ? "#6a7f99" : "#c8a84b";

    protected override string? IconUrl =>
        r.TypeId > 0 ? $"https://images.evetech.net/types/{r.TypeId}/icon?size=32" : null;
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

    private string _holder = "";
    public string Holder { get => _holder; private set => this.RaiseAndSetIfChanged(ref _holder, value); }

    private Bitmap? _holderLogo;
    public Bitmap? HolderLogo { get => _holderLogo; private set => this.RaiseAndSetIfChanged(ref _holderLogo, value); }

    private string _planetCount = "";
    public string PlanetCount { get => _planetCount; private set => this.RaiseAndSetIfChanged(ref _planetCount, value); }

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
    public ObservableCollection<CelestialVm>      Planets       { get; } = [];
    public ObservableCollection<CelestialVm>      Moons         { get; } = [];
    public ObservableCollection<SysStructureVm>   Structures    { get; } = [];
    public ObservableCollection<KillmailListRowVm> Kills        { get; } = [];
    public ObservableCollection<GateVm>           Gates         { get; } = [];

    private string _historyNote = "";
    public string HistoryNote { get => _historyNote; private set => this.RaiseAndSetIfChanged(ref _historyNote, value); }

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
        var celestials = await _svc.GetCelestialsAsync(systemId);
        var structures = await _svc.GetStructuresAsync(systemId);
        var gates      = await _svc.GetGatesAsync(systemId);
        var since      = await _svc.GetHistoryStartAsync();

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
            Holder = string.IsNullOrEmpty(header.AllianceName)
                ? (string.IsNullOrEmpty(header.CorporationName) ? "Unclaimed" : header.CorporationName)
                : string.IsNullOrEmpty(header.CorporationName)
                    ? header.AllianceName
                    : $"{header.AllianceName}  ·  {header.CorporationName}";

            PlanetCount = header.Planets.ToString("N0");
            MoonCount   = header.Moons.ToString("N0");
            Jumps     = $"{header.Jumps1h:N0} / {header.Jumps24h:N0}";
            ShipKills = $"{header.ShipKills1h:N0} / {header.ShipKills24h:N0}";
            NpcKills  = $"{header.NpcKills1h:N0} / {header.NpcKills24h:N0}";
            PodKills  = $"{header.PodKills1h:N0} / {header.PodKills24h:N0}";

            Fill(SovStructures, sovStructs.Select(s => new SovStructureVm(s)));
            Fill(Events,     events.Select(e => new SystemEventVm(e)));
            Fill(SovChanges, events.Where(e => e.Kind.StartsWith("Sovereignty"))
                                   .Select(e => new SystemEventVm(e)));
            Fill(Planets, celestials.Where(c => c.Kind == 0).Select(c => new CelestialVm(c)));
            Fill(Moons,   celestials.Where(c => c.Kind == 1).Select(c => new CelestialVm(c)));
            Fill(Structures, structures.Select(s => new SysStructureVm(s)));
            Fill(Kills, killPage.Rows.Select(r => new KillmailListRowVm(r)));
            Fill(Gates, gates.Select(g => new GateVm(g)));

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
            Task.WhenAll(Planets.Select(x => x.LoadIconAsync())),
            Task.WhenAll(Structures.Select(x => x.LoadIconAsync())),
            Task.WhenAll(Events.Take(40).Select(x => x.LoadIconAsync())),
            Task.WhenAll(Kills.Select(x => x.LoadImagesAsync())));
    }

    private static void Fill<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var i in items) target.Add(i);
    }
}
