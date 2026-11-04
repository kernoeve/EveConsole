using System.Reactive;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Reactive.Linq;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using EveConsole.Models;
using EveConsole.Services;
using ReactiveUI;
using Avalonia.Media;
using EveConsole.Localization;

namespace EveConsole.ViewModels;

// ── List row VM ───────────────────────────────────────────────────────────────
public class KillmailListRowVm : ReactiveObject
{
    private readonly int  _victimShipTypeId;
    private readonly long _victimCharId;
    private readonly long _victimCorpId;
    private readonly long _victimAllianceId;
    private readonly long _fbCharId;
    private readonly long _fbCorpId;
    private readonly long _fbAllianceId;

    public int            KillMailId        { get; }
    public DateTimeOffset TimeRaw           { get; }
    public string         DateText          { get; }
    public string         TimeText          { get; }
    public string         TotalIskText      { get; }
    public string         ShipName          { get; }
    public int            SystemId          { get; }
    public string         SystemName        { get; }
    public string         ConstellationName { get; }
    public int            RegionId          { get; }
    public string         RegionName        { get; }
    public string         SecurityText      { get; }
    public string         SecurityColor     { get; }

    /// <summary>True security, on hover. The headline is the rounded band; this is the detail
    /// behind it — and the only place a null-sec system's real depth is visible.</summary>
    public string         SecurityTip       { get; }
    public string         VictimName        { get; }
    public string         VictimCorp        { get; }
    public string         VictimAlliance    { get; }
    public string         FbName            { get; }
    public string         FbCorp            { get; }
    public string         FbAlliance        { get; }

    private Bitmap? _shipRender;
    private Bitmap? _victimLogo;
    private Bitmap? _fbLogo;
    public Bitmap? ShipRender  { get => _shipRender;  private set => this.RaiseAndSetIfChanged(ref _shipRender,  value); }
    public Bitmap? VictimLogo  { get => _victimLogo;  private set => this.RaiseAndSetIfChanged(ref _victimLogo,  value); }
    public Bitmap? FbLogo      { get => _fbLogo;      private set => this.RaiseAndSetIfChanged(ref _fbLogo,      value); }

    public KillmailListRowVm(KillmailListRow r)
    {
        KillMailId        = r.KillMailId;
        TimeRaw           = r.KillMailTime;
        DateText          = r.KillMailTime.UtcDateTime.ToString("yyyy-MM-dd");
        TimeText          = r.KillMailTime.UtcDateTime.ToString("HH:mm");
        TotalIskText      = r.TotalIsk > 0 ? FmtIsk(r.TotalIsk) : "";

        // The hull and the places in the interface language, and a corporation where it is an
        // NPC's — a rat's, or a pilot's starter corporation. Only for reading: every link on the
        // row goes by id. Pilots, their corporations and alliances stay as ESI names them.
        ShipName          = SdeNames.Type(r.VictimShipTypeId, r.ShipName);
        SystemId          = r.SystemId;
        SystemName        = SdeNames.SolarSystem(r.SystemId, r.SystemName);
        ConstellationName = SdeNames.Constellation(r.ConstellationId, r.ConstellationName);
        RegionId          = r.RegionId;
        RegionName        = SdeNames.Region(r.RegionId, r.RegionName);
        VictimName        = r.VictimName;
        VictimCorp        = SdeNames.NpcCorporation(r.VictimCorpId, r.VictimCorp);
        VictimAlliance    = r.VictimAlliance;
        FbName            = r.FbName;
        FbCorp            = SdeNames.NpcCorporation(r.FbCorpId, r.FbCorp);
        FbAlliance        = r.FbAlliance;
        _victimShipTypeId = r.VictimShipTypeId;
        _victimCharId     = r.VictimCharId;
        _victimCorpId     = r.VictimCorpId;
        _victimAllianceId = r.VictimAllianceId;
        _fbCharId         = r.FbCharId;
        _fbCorpId         = r.FbCorpId;
        _fbAllianceId     = r.FbAllianceId;

        var sec       = r.SecurityStatus;
        SecurityText  = EveConsole.Services.SecurityColors.Text(sec);
        SecurityColor = EveConsole.Services.SecurityColors.Hex(sec);
        SecurityTip   = EveConsole.Services.SecurityColors.Tip(sec);

        OpenVictimCommand         = ReactiveCommand.Create(OpenVictim);
        OpenVictimCorpCommand     = ReactiveCommand.Create(OpenVictimCorp);
        OpenVictimAllianceCommand = ReactiveCommand.Create(OpenVictimAlliance);
        OpenFbCommand             = ReactiveCommand.Create(OpenFb);
        OpenFbCorpCommand         = ReactiveCommand.Create(OpenFbCorp);
        OpenFbAllianceCommand     = ReactiveCommand.Create(OpenFbAlliance);
        OpenRegionCommand         = ReactiveCommand.Create(() => Nav.Region(RegionId));
        OpenSystemCommand         = ReactiveCommand.Create(OpenSystem);
    }

    // Links live on the row rather than on a host view model. The same row renders in the
    // Killmail Browser, Corp Activity, the map's system page and the entity viewers, and a
    // link that only worked in one of them would be worse than none.
    private static EveConsole.Services.EntityNavigator Nav => EveConsole.Services.EntityNavigator.Instance;

    public ReactiveCommand<Unit, Unit> OpenVictimCommand         { get; }
    public ReactiveCommand<Unit, Unit> OpenVictimCorpCommand     { get; }
    public ReactiveCommand<Unit, Unit> OpenVictimAllianceCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenFbCommand             { get; }
    public ReactiveCommand<Unit, Unit> OpenFbCorpCommand         { get; }
    public ReactiveCommand<Unit, Unit> OpenFbAllianceCommand     { get; }
    public ReactiveCommand<Unit, Unit> OpenRegionCommand         { get; }
    public ReactiveCommand<Unit, Unit> OpenSystemCommand         { get; }

    /// <summary>A name is only a link when there is something behind it to open — rows built
    /// from a killmail with no resolved system or region fall back to plain text.</summary>
    public bool HasSystemLink => SystemId > 0 && SystemName.Length > 0;
    public bool HasRegionLink => RegionId > 0 && RegionName.Length > 0;

    public void OpenVictim()         => Nav.Entity(EveConsole.Services.EntityKind.Pilot,      _victimCharId);
    public void OpenVictimCorp()     => Nav.Entity(EveConsole.Services.EntityKind.PlayerCorp, _victimCorpId);
    public void OpenVictimAlliance() => Nav.Entity(EveConsole.Services.EntityKind.Alliance,   _victimAllianceId);
    public void OpenFb()             => Nav.Entity(EveConsole.Services.EntityKind.Pilot,      _fbCharId);
    public void OpenFbCorp()         => Nav.Entity(EveConsole.Services.EntityKind.PlayerCorp, _fbCorpId);
    public void OpenFbAlliance()     => Nav.Entity(EveConsole.Services.EntityKind.Alliance,   _fbAllianceId);
    public void OpenSystem()         => Nav.System(SystemId);
    public void OpenKillmail()       => Nav.Killmail(KillMailId);

    public Task LoadImagesAsync() => Task.WhenAll(
        _victimShipTypeId > 0
            ? LoadAsync($"https://images.evetech.net/types/{_victimShipTypeId}/render?size=64",  v => ShipRender = v)
            : Task.CompletedTask,
        _victimAllianceId > 0
            ? LoadAsync($"https://images.evetech.net/alliances/{_victimAllianceId}/logo?size=32", v => VictimLogo = v)
            : _victimCorpId > 0
                ? LoadAsync($"https://images.evetech.net/corporations/{_victimCorpId}/logo?size=32", v => VictimLogo = v)
                : Task.CompletedTask,
        _fbAllianceId > 0
            ? LoadAsync($"https://images.evetech.net/alliances/{_fbAllianceId}/logo?size=32", v => FbLogo = v)
            : _fbCorpId > 0
                ? LoadAsync($"https://images.evetech.net/corporations/{_fbCorpId}/logo?size=32", v => FbLogo = v)
                : Task.CompletedTask
    );

    private static async Task LoadAsync(string url, Action<Bitmap?> set)
    {
        var bmp = await EveImageCache.GetAsync(url);
        Dispatcher.UIThread.Post(() => set(bmp));
    }

    private static string FmtIsk(double v) => v switch
    {
        >= 1_000_000_000 => $"{v / 1_000_000_000:F2}B",
        >= 1_000_000     => $"{v / 1_000_000:F2}M",
        >= 1_000         => $"{v / 1_000:F1}K",
        _                => $"{v:F0}",
    };
}

// ── Detail VMs ────────────────────────────────────────────────────────────────
public class KillmailItemVm : ReactiveObject
{
    private readonly int    _typeId;
    private readonly string _iconVariant;

    public int    TypeId        => _typeId;
    public string TypeName      { get; }
    public string QtyDestroyed  { get; }
    public string QtyDropped    { get; }
    public string EstValueText  { get; }
    public bool   HasDestroyed  { get; }
    public bool   HasDropped    { get; }

    private Bitmap? _icon;
    public Bitmap? Icon { get => _icon; private set => this.RaiseAndSetIfChanged(ref _icon, value); }

    public KillmailItemVm(KillmailItemRow r)
    {
        _typeId      = r.TypeId;
        // Blueprint render variant, same "bp"/"bpc" path-segment trick the Industry Jobs
        // UI uses (Views/EveImageLoader.cs) — driven by Singleton==2 here rather than
        // industry ActivityId, since a killmail item has no activity to check.
        _iconVariant = r.IsBpc ? "bpc" : r.IsBpo ? "bp" : "icon";
        TypeName     = r.TypeName;
        HasDestroyed = r.QtyDestroyed > 0;
        HasDropped   = r.QtyDropped   > 0;
        QtyDestroyed = r.QtyDestroyed > 0 ? string.Format(CorpText.KillQtyDestroyed, r.QtyDestroyed) : "";
        QtyDropped   = r.QtyDropped   > 0 ? string.Format(CorpText.KillQtyDropped, r.QtyDropped) : "";
        EstValueText = r.EstValue > 0 ? FmtIsk(r.EstValue) : "";
    }

    public Task LoadIconAsync()
    {
        if (_typeId <= 0) return Task.CompletedTask;
        var url = $"https://images.evetech.net/types/{_typeId}/{_iconVariant}?size=32";
        return EveImageCache.GetAsync(url).ContinueWith(t =>
        {
            var bmp = t.Result;
            Dispatcher.UIThread.Post(() => Icon = bmp);
        }, TaskScheduler.Default);
    }

    private static string FmtIsk(double v) => v switch
    {
        >= 1_000_000_000 => $"{v / 1_000_000_000:F2}B",
        >= 1_000_000     => $"{v / 1_000_000:F2}M",
        >= 1_000         => $"{v / 1_000:F1}K",
        _                => $"{v:F0}",
    };
}

/// <summary>One sub-group within a slot section. SubGroupName is empty for every slot
/// except Cargo Hold (which is split further by market group) — the View only renders a
/// sub-header when it's non-empty.</summary>
public class KillmailSubGroupVm
{
    public string               SubGroupName { get; }
    public List<KillmailItemVm> Items        { get; }

    public KillmailSubGroupVm(KillmailSubGroupRow r)
    {
        SubGroupName = r.SubGroupName;
        Items        = r.Items.Select(i => new KillmailItemVm(i)).ToList();
    }
}

public class KillmailSlotGroupVm
{
    public string                    GroupName  { get; }
    public List<KillmailSubGroupVm>  SubGroups  { get; }

    public KillmailSlotGroupVm(KillmailSlotGroupRow r)
    {
        GroupName = r.GroupName;
        SubGroups = r.SubGroups.Select(g => new KillmailSubGroupVm(g)).ToList();
    }
}

public class KillmailAttackerVm : ReactiveObject
{
    private readonly long _characterId;
    private readonly long _corporationId;
    private readonly long _allianceId;
    private readonly int  _shipTypeId;
    private readonly int  _weaponTypeId;

    public string CharName     { get; }
    public string CorpName     { get; }
    public string AllianceName { get; }
    public string ShipName     { get; }
    public string WeaponName   { get; }
    public string DamageText   { get; }
    public int    DamageDone   { get; }
    public bool   FinalBlow    { get; }
    public bool   IsTopDamage  { get; set; }
    public string RoleLabel    { get; private set; } = "";
    public IBrush RoleColor    { get; private set; } = Palette.TextFaint;

    private Bitmap? _portrait;
    private Bitmap? _shipIcon;
    private Bitmap? _weaponIcon;
    public Bitmap? Portrait   { get => _portrait;   private set => this.RaiseAndSetIfChanged(ref _portrait,   value); }
    public Bitmap? ShipIcon   { get => _shipIcon;   private set => this.RaiseAndSetIfChanged(ref _shipIcon,   value); }
    public Bitmap? WeaponIcon { get => _weaponIcon; private set => this.RaiseAndSetIfChanged(ref _weaponIcon, value); }

    public KillmailAttackerVm(KillmailAttackerRow r)
    {
        // The ship, the weapon and an NPC's corporation in the interface language; the links and
        // icons go by id. A pilot and their alliance stay as ESI names them.
        CharName      = r.CharName;
        CorpName      = SdeNames.NpcCorporation(r.CorporationId, r.CorpName);
        AllianceName  = r.AllianceName;
        ShipName      = SdeNames.Type(r.ShipTypeId, r.ShipName);
        WeaponName    = SdeNames.Type(r.WeaponTypeId, r.WeaponName);
        DamageDone    = r.DamageDone;
        DamageText    = $"{r.DamageDone:N0}";
        FinalBlow     = r.FinalBlow;
        _characterId  = r.CharacterId;
        _corporationId = r.CorporationId;
        _allianceId    = r.AllianceId;
        _shipTypeId   = r.ShipTypeId;
        _weaponTypeId = r.WeaponTypeId;
        if (r.FinalBlow) { RoleLabel = CorpText.RoleFinalBlow; RoleColor = Palette.Accent; }

        OpenCharCommand     = ReactiveCommand.Create(() => Nav.Entity(EveConsole.Services.EntityKind.Pilot, _characterId));
        OpenCorpCommand     = ReactiveCommand.Create(() => Nav.Entity(EveConsole.Services.EntityKind.PlayerCorp, _corporationId));
        OpenAllianceCommand = ReactiveCommand.Create(() => Nav.Entity(EveConsole.Services.EntityKind.Alliance, _allianceId));
    }

    private static EveConsole.Services.EntityNavigator Nav => EveConsole.Services.EntityNavigator.Instance;

    public ReactiveCommand<Unit, Unit> OpenCharCommand     { get; }
    public ReactiveCommand<Unit, Unit> OpenCorpCommand     { get; }
    public ReactiveCommand<Unit, Unit> OpenAllianceCommand { get; }

    public void MarkTopDamage()
    {
        IsTopDamage = true;
        if (!FinalBlow) { RoleLabel = CorpText.RoleTopDamage; RoleColor = Palette.Good; }
        else            { RoleLabel = CorpText.RoleFinalBlow + "  " + CorpText.RoleTopDamage; }
    }

    public Task LoadImagesAsync() => Task.WhenAll(
        _characterId  > 0 ? LoadAsync(() => Portrait   = null, $"https://images.evetech.net/characters/{_characterId}/portrait?size=64",  v => Portrait   = v) : Task.CompletedTask,
        _shipTypeId   > 0 ? LoadAsync(() => ShipIcon   = null, $"https://images.evetech.net/types/{_shipTypeId}/render?size=32",           v => ShipIcon   = v) : Task.CompletedTask,
        _weaponTypeId > 0 ? LoadAsync(() => WeaponIcon = null, $"https://images.evetech.net/types/{_weaponTypeId}/icon?size=32",           v => WeaponIcon = v) : Task.CompletedTask
    );

    private static async Task LoadAsync(Action clear, string url, Action<Bitmap?> set)
    {
        var bmp = await EveImageCache.GetAsync(url);
        Dispatcher.UIThread.Post(() => set(bmp));
    }
}

public class KillmailDetailVm : ReactiveObject
{
    private readonly long _victimCharId;
    private readonly long _victimCorpId;
    private readonly long _victimAllianceId;
    private readonly int  _victimShipTypeId;

    public int VictimShipTypeId => _victimShipTypeId;

    public string ShipName        { get; }
    public string VictimName      { get; }
    public string VictimCorp      { get; }
    public string VictimAlliance  { get; }
    public string TimeText        { get; }
    public int    SystemId        { get; }
    public string SystemText      { get; }
    public string LocationText    { get; }
    public string DamageTakenText { get; }
    public string DestroyedText   { get; }
    public string DroppedText     { get; }
    public string TotalIskText    { get; }

    private Bitmap? _victimPortrait;
    private Bitmap? _victimCorpLogo;
    private Bitmap? _victimAllianceLogo;
    private Bitmap? _shipRender;
    public Bitmap? VictimPortrait     { get => _victimPortrait;     private set => this.RaiseAndSetIfChanged(ref _victimPortrait,     value); }
    public Bitmap? VictimCorpLogo     { get => _victimCorpLogo;     private set => this.RaiseAndSetIfChanged(ref _victimCorpLogo,     value); }
    public Bitmap? VictimAllianceLogo { get => _victimAllianceLogo; private set => this.RaiseAndSetIfChanged(ref _victimAllianceLogo, value); }
    public Bitmap? ShipRender         { get => _shipRender;         private set => this.RaiseAndSetIfChanged(ref _shipRender,         value); }

    public List<KillmailSlotGroupVm>  SlotGroups { get; }
    public List<KillmailAttackerVm>   Attackers  { get; }

    private static EveConsole.Services.EntityNavigator Nav => EveConsole.Services.EntityNavigator.Instance;

    public ReactiveCommand<Unit, Unit> OpenVictimCommand         { get; }
    public ReactiveCommand<Unit, Unit> OpenVictimCorpCommand     { get; }
    public ReactiveCommand<Unit, Unit> OpenVictimAllianceCommand { get; }

    public KillmailDetailVm(KillmailDetailData d)
    {
        _victimCharId     = d.VictimCharId;
        _victimCorpId     = d.VictimCorpId;
        _victimAllianceId = d.VictimAllianceId;
        _victimShipTypeId = d.VictimShipTypeId;

        // The hull, the place and an NPC victim corporation in the interface language. The
        // nearest-celestial line stays as it is: planets, moons and gates keep their English.
        var system     = SdeNames.SolarSystem(d.SystemId, d.SystemName);
        var region     = SdeNames.Region(d.RegionId, d.RegionName);
        ShipName       = SdeNames.Type(d.VictimShipTypeId, d.ShipName);
        VictimName     = d.VictimName;
        VictimCorp     = SdeNames.NpcCorporation(d.VictimCorpId, d.VictimCorp);
        VictimAlliance = d.VictimAlliance;
        TimeText       = d.KillMailTime.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss");
        SystemId       = d.SystemId;
        SystemText     = string.IsNullOrEmpty(d.RegionName) ? system : $"{system}  ({region})";
        LocationText   = d.LocationText;
        DamageTakenText= string.Format(CorpText.DamageTaken, d.VictimDamageTaken);
        DestroyedText  = FmtIsk(d.DestroyedIsk);
        DroppedText    = FmtIsk(d.DroppedIsk);
        TotalIskText   = FmtIsk(d.DestroyedIsk + d.DroppedIsk);
        SlotGroups = d.SlotGroups.Select(g => new KillmailSlotGroupVm(g)).ToList();
        Attackers  = d.Attackers.Select(a => new KillmailAttackerVm(a)).ToList();

        OpenVictimCommand         = ReactiveCommand.Create(() => Nav.Entity(EveConsole.Services.EntityKind.Pilot, _victimCharId));
        OpenVictimCorpCommand     = ReactiveCommand.Create(() => Nav.Entity(EveConsole.Services.EntityKind.PlayerCorp, _victimCorpId));
        OpenVictimAllianceCommand = ReactiveCommand.Create(() => Nav.Entity(EveConsole.Services.EntityKind.Alliance, _victimAllianceId));

        // Mark the highest-damage attacker (may differ from final blow)
        Attackers.OrderByDescending(a => a.DamageDone).FirstOrDefault()?.MarkTopDamage();

        // Load header portrait/logo/ship render, attacker portraits/ship/weapon icons,
        // and item icons asynchronously.
        _ = LoadHeaderImagesAsync();
        _ = Task.WhenAll(Attackers.Select(a => a.LoadImagesAsync()));
        _ = Task.WhenAll(SlotGroups.SelectMany(g => g.SubGroups).SelectMany(sg => sg.Items).Select(i => i.LoadIconAsync()));
    }

    // Portrait is the same height as the ship render (64px); corp/alliance are shown
    // separately (not one-wins-the-other) at half that height (32px) each.
    private Task LoadHeaderImagesAsync() => Task.WhenAll(
        _victimCharId > 0
            ? LoadAsync($"https://images.evetech.net/characters/{_victimCharId}/portrait?size=64", v => VictimPortrait = v)
            : Task.CompletedTask,
        _victimCorpId > 0
            ? LoadAsync($"https://images.evetech.net/corporations/{_victimCorpId}/logo?size=32", v => VictimCorpLogo = v)
            : Task.CompletedTask,
        _victimAllianceId > 0
            ? LoadAsync($"https://images.evetech.net/alliances/{_victimAllianceId}/logo?size=32", v => VictimAllianceLogo = v)
            : Task.CompletedTask,
        _victimShipTypeId > 0
            ? LoadAsync($"https://images.evetech.net/types/{_victimShipTypeId}/render?size=64", v => ShipRender = v)
            : Task.CompletedTask
    );

    private static async Task LoadAsync(string url, Action<Bitmap?> set)
    {
        var bmp = await EveImageCache.GetAsync(url);
        Dispatcher.UIThread.Post(() => set(bmp));
    }

    private static string FmtIsk(double v) => v switch
    {
        >= 1_000_000_000 => $"{v / 1_000_000_000:F2}B ISK",
        >= 1_000_000     => $"{v / 1_000_000:F2}M ISK",
        >= 1_000         => $"{v / 1_000:F1}K ISK",
        _                => $"{v:F0} ISK",
    };
}

/// <summary>One entry in the Personal tab's character list; id 0 is "All Characters".</summary>
public sealed class KillmailCharacterOption
{
    public long   Id              { get; }
    public string Name            { get; }
    public bool   IsAllCharacters => Id == 0;

    public KillmailCharacterOption(Character c) { Id = c.Id; Name = c.Name; }

    private KillmailCharacterOption() { Id = 0; Name = CommsText.AllCharacters; }
    public static readonly KillmailCharacterOption All = new();

    public override string ToString() => Name;
}

// ── Main ViewModel ────────────────────────────────────────────────────────────
public class KillmailBrowserViewModel : ReactiveObject
{
    public const int PersonalTab = 0;
    public const int AllTab      = 1;

    private readonly KillmailBrowserService          _service;
    private readonly ObservableCollection<Character> _sourceChars;

    // Filters — all run server-side in KillmailBrowserService.GetListAsync, not against
    // whatever page happens to already be loaded (with 100K+ rows total and one page
    // loaded at a time, a client-side filter would silently miss anything outside the
    // loaded window). Changing any of them re-queries from scratch via the debounced
    // subscription set up in the constructor, rather than firing on every keystroke.
    // Corp used to be a dropdown of only our own tracked corps (joined through
    // EsiKillMailRefs) — that could only ever show "my corp"'s kills by construction, so
    // it's now a free-text search like Character/Ship/System, matching victim or
    // final-blow-attacker corp by name (any corp, not just tracked ones).
    private DateTime? _filterFrom, _filterThru;
    private string    _filterChar = "", _filterCorp = "", _filterShip = "", _filterSystem = "";

    public DateTime? FilterFrom
    {
        get => _filterFrom;
        set => this.RaiseAndSetIfChanged(ref _filterFrom, value);
    }
    public DateTime? FilterThru
    {
        get => _filterThru;
        set => this.RaiseAndSetIfChanged(ref _filterThru, value);
    }
    public string FilterChar
    {
        get => _filterChar;
        set => this.RaiseAndSetIfChanged(ref _filterChar, value);
    }
    public string FilterCorp
    {
        get => _filterCorp;
        set => this.RaiseAndSetIfChanged(ref _filterCorp, value);
    }
    public string FilterShip
    {
        get => _filterShip;
        set => this.RaiseAndSetIfChanged(ref _filterShip, value);
    }
    public string FilterSystem
    {
        get => _filterSystem;
        set => this.RaiseAndSetIfChanged(ref _filterSystem, value);
    }

    // Personal lists the kills our own characters were on; All searches every kill by the
    // character and corp boxes. Dates, ship and system/region apply to both.
    private int _selectedTab = PersonalTab;
    public int SelectedTab
    {
        get => _selectedTab;
        set => this.RaiseAndSetIfChanged(ref _selectedTab, value);
    }

    public ObservableCollection<KillmailCharacterOption> Characters { get; } = [];

    private KillmailCharacterOption? _selectedCharacter = KillmailCharacterOption.All;
    public KillmailCharacterOption? SelectedCharacter
    {
        get => _selectedCharacter;
        set => this.RaiseAndSetIfChanged(ref _selectedCharacter, value);
    }

    // Fires when characters arrive or leave, so "All Characters" re-queries through the same
    // debounce as the filters — the list loads before the characters do.
    private readonly System.Reactive.Subjects.Subject<Unit> _charactersChanged = new();

    /// <summary>Set while the view model changes filters itself and reloads on its own.</summary>
    private bool _quiet;

    // List — populated directly from whatever the server already filtered/paged, no
    // client-side re-filtering step.
    public ObservableCollection<KillmailListRowVm> KillmailRows { get; } = [];

    private KillmailListRowVm? _selectedKillmail;
    public KillmailListRowVm? SelectedKillmail
    {
        get => _selectedKillmail;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedKillmail, value);
            if (value is not null) _ = LoadDetailAsync(value.KillMailId);
            else Detail = null;
        }
    }

    // Detail
    private KillmailDetailVm? _detail;
    public KillmailDetailVm? Detail
    {
        get => _detail;
        private set => this.RaiseAndSetIfChanged(ref _detail, value);
    }

    // Status
    private bool   _isLoading;
    private string _statusText  = "";
    private IBrush _statusColor = Palette.TextFaint;
    public bool   IsLoading    { get => _isLoading;    private set => this.RaiseAndSetIfChanged(ref _isLoading,    value); }
    public string StatusText   { get => _statusText;   private set => this.RaiseAndSetIfChanged(ref _statusText,   value); }
    public IBrush StatusColor  { get => _statusColor;  private set => this.RaiseAndSetIfChanged(ref _statusColor,  value); }

    public ReactiveUI.ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> RefreshCommand      { get; }
    public ReactiveUI.ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> ClearFiltersCommand { get; }
    public ReactiveUI.ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> LoadMoreCommand     { get; }

    /// <summary>Set by MainWindowViewModel — opens the Item Browser tool and selects the
    /// given type id, same wiring as ProductionCalculatorViewModel/
    /// CharacterViewerViewModel's identical property.</summary>
    public Action<int>? NavigateToItemAction { get; set; }

    /// <summary>Set by the shell so a system name opens that system on the Universe map.</summary>
    public Action<int>? NavigateToSystemAction { get; set; }
    public ReactiveUI.ReactiveCommand<int, System.Reactive.Unit> OpenInItemBrowserCommand { get; }
    public ReactiveUI.ReactiveCommand<int, System.Reactive.Unit> OpenSystemMapCommand     { get; }

    // Paging — the underlying table can now hold 100K+ rows (zKillboard backfill/firehose),
    // so the browser loads a page at a time instead of one hard-capped query.
    private int  _offset;
    private bool _hasMore;
    public bool HasMore { get => _hasMore; private set => this.RaiseAndSetIfChanged(ref _hasMore, value); }

    private bool _isLoadingMore;
    public bool IsLoadingMore { get => _isLoadingMore; private set => this.RaiseAndSetIfChanged(ref _isLoadingMore, value); }

    public KillmailBrowserViewModel(KillmailBrowserService service, ObservableCollection<Character> characters)
    {
        _service     = service;
        _sourceChars = characters;

        // "All Characters" first, everyone else alphabetical however they arrive.
        Characters.Add(KillmailCharacterOption.All);
        foreach (var c in characters) InsertSorted(new KillmailCharacterOption(c));
        characters.CollectionChanged += OnSourceCharsChanged;

        RefreshCommand = ReactiveUI.ReactiveCommand.CreateFromTask(LoadAsync);
        RefreshCommand.ThrownExceptions.Subscribe(_ => { });

        LoadMoreCommand = ReactiveUI.ReactiveCommand.CreateFromTask(LoadMoreAsync,
            this.WhenAnyValue(x => x.HasMore, x => x.IsLoadingMore, (more, loading) => more && !loading));
        LoadMoreCommand.ThrownExceptions.Subscribe(_ => { });

        OpenInItemBrowserCommand = ReactiveUI.ReactiveCommand.Create<int>(typeId => NavigateToItemAction?.Invoke(typeId));
        OpenSystemMapCommand     = ReactiveUI.ReactiveCommand.Create<int>(id => { if (id > 0) NavigateToSystemAction?.Invoke(id); });

        ClearFiltersCommand = ReactiveUI.ReactiveCommand.Create(() =>
        {
            FilterFrom   = null;
            FilterThru   = null;
            FilterChar   = "";
            FilterCorp   = "";
            FilterShip   = "";
            FilterSystem = "";
            SelectedCharacter = KillmailCharacterOption.All;
        });

        // Filter changes re-query the server, so debounce rather than firing on every
        // keystroke — 400ms after the user stops typing/picking dates.
        this.WhenAnyValue(x => x.FilterFrom, x => x.FilterThru, x => x.FilterChar, x => x.FilterCorp, x => x.FilterShip, x => x.FilterSystem)
            .Skip(1)
            .Select(_ => Unit.Default)
            .Merge(this.WhenAnyValue(x => x.SelectedTab, x => x.SelectedCharacter).Skip(1).Select(_ => Unit.Default))
            .Merge(_charactersChanged.Where(_ => _selectedTab == PersonalTab && _selectedCharacter?.IsAllCharacters != false))
            .Where(_ => !_quiet)
            .Throttle(TimeSpan.FromMilliseconds(400), RxApp.TaskpoolScheduler)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => { var task = LoadAsync(); });

        // Default date range: last 30 days
        _filterFrom = DateTime.Today.AddDays(-30);
        _filterThru = DateTime.Today;

        _ = LoadAsync();
    }

    private async Task LoadAsync(CancellationToken ct = default)
    {
        IsLoading   = true;
        StatusColor = Palette.TextFaint;
        StatusText  = CorpText.StatusLoadingKillmails;
        _offset     = 0;
        HasMore     = false;
        try
        {
            // The rows name ships, places and NPC corporations in the interface language, and
            // the filters find those names — so the names are awaited before either.
            await SdeNames.EnsureLoadedAsync(ct);

            var page = await GetPageAsync(ct);

            var rows = page.Rows.Select(r => new KillmailListRowVm(r)).ToList();
            KillmailRows.Clear();
            foreach (var r in rows) KillmailRows.Add(r);
            _offset = rows.Count;
            HasMore = page.HasMore;
            _ = Task.WhenAll(rows.Select(r => r.LoadImagesAsync()));
            UpdateStatusText();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText = string.Format(CommonText.ErrorWithMessage, ex.Message); StatusColor = Palette.Bad; }
        finally { IsLoading = false; }
    }

    /// <summary>Fetches the next page (same filters/order as the current load) and
    /// appends it — never re-sorts, so it stays consistent with what's already
    /// rendered above it (GetListAsync's ORDER BY KillMailTime DESC is the single
    /// source of truth for row order throughout).</summary>
    private async Task LoadMoreAsync(CancellationToken ct = default)
    {
        if (!HasMore || IsLoadingMore) return;

        IsLoadingMore = true;
        try
        {
            var page = await GetPageAsync(ct);

            var newRows = page.Rows.Select(r => new KillmailListRowVm(r)).ToList();
            foreach (var r in newRows) KillmailRows.Add(r);
            _offset += newRows.Count;
            HasMore  = page.HasMore;
            _ = Task.WhenAll(newRows.Select(r => r.LoadImagesAsync()));
            UpdateStatusText();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText = string.Format(CommonText.ErrorWithMessage, ex.Message); StatusColor = Palette.Bad; }
        finally { IsLoadingMore = false; }
    }

    /// <summary>The page at <see cref="_offset"/> for the tab on screen. Personal ignores the
    /// character and corp boxes, which belong to All.</summary>
    private Task<KillmailListPage> GetPageAsync(CancellationToken ct)
    {
        var personal = _selectedTab == PersonalTab;
        IReadOnlyCollection<long>? involved = !personal ? null
            : _selectedCharacter is { IsAllCharacters: false } one ? [one.Id]
            : _sourceChars.Select(c => c.Id).ToList();

        return _service.GetListAsync(
            _offset, KillmailBrowserService.PageSize,
            _filterFrom is { } f ? DateOnly.FromDateTime(f) : null,
            _filterThru is { } t ? DateOnly.FromDateTime(t) : null,
            personal ? null : _filterChar, personal ? null : _filterCorp, _filterShip, _filterSystem,
            matchShownNames: true, ct: ct, involvedCharacterIds: involved);
    }

    /// <summary>Places a character after "All Characters" and before the first name that sorts
    /// after it.</summary>
    private void InsertSorted(KillmailCharacterOption option)
    {
        var at = 1;   // index 0 is always "All Characters"
        while (at < Characters.Count
               && string.Compare(Characters[at].Name, option.Name, StringComparison.OrdinalIgnoreCase) < 0)
            at++;
        Characters.Insert(at, option);
    }

    private void OnSourceCharsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            var keep = _selectedCharacter?.Id ?? 0;
            while (Characters.Count > 1) Characters.RemoveAt(1);
            foreach (var c in _sourceChars) InsertSorted(new KillmailCharacterOption(c));
            SelectedCharacter = Characters.FirstOrDefault(o => o.Id == keep) ?? KillmailCharacterOption.All;
        }

        if (e.NewItems is not null)
            foreach (Character c in e.NewItems)
                if (Characters.All(o => o.Id != c.Id)) InsertSorted(new KillmailCharacterOption(c));

        if (e.OldItems is not null)
            foreach (Character c in e.OldItems)
                if (Characters.FirstOrDefault(o => o.Id == c.Id) is { } opt)
                {
                    if (ReferenceEquals(opt, _selectedCharacter)) SelectedCharacter = KillmailCharacterOption.All;
                    Characters.Remove(opt);
                }

        _charactersChanged.OnNext(Unit.Default);
    }

    private void UpdateStatusText()
    {
        StatusText = HasMore
            ? string.Format(CorpText.KillmailsLoadedMore, KillmailRows.Count)
            : string.Format(CorpText.KillmailsCount, KillmailRows.Count);
    }

    public void SelectById(int killMailId) => _ = SelectByIdAsync(killMailId);

    /// <summary>
    /// Show a kill that arrived from somewhere else — an entity viewer, the map, corp activity.
    /// </summary>
    private async Task SelectByIdAsync(int killMailId)
    {
        var row = KillmailRows.FirstOrDefault(r => r.KillMailId == killMailId);
        if (row is not null) { SelectedKillmail = row; return; }

        // Not on screen, and reloading as-is would not help: the list defaults to the last 30
        // days, while a kill picked out of a pilot's history is usually older than that. Widen
        // the range before looking again — and on All, since a kill opened from a pilot, the map
        // or corp activity is seldom one of ours.
        // ⚠️ Raised quietly: the debounced reload would otherwise run again 400ms after this one,
        // clear the list, and take the selection and the detail pane with it.
        _filterFrom  = null;
        _filterThru  = null;
        _selectedTab = AllTab;
        _quiet = true;
        try
        {
            this.RaisePropertyChanged(nameof(FilterFrom));
            this.RaisePropertyChanged(nameof(FilterThru));
            this.RaisePropertyChanged(nameof(SelectedTab));
        }
        finally { _quiet = false; }
        await LoadAsync();

        row = KillmailRows.FirstOrDefault(r => r.KillMailId == killMailId);
        if (row is not null) { SelectedKillmail = row; return; }

        // Older than the first page of an unfiltered list. The row cannot be highlighted
        // without paging an unknown distance to reach it, but the detail pane is what was
        // actually asked for, so load that on its own.
        await LoadDetailAsync(killMailId);
    }

    private async Task LoadDetailAsync(int killMailId, CancellationToken ct = default)
    {
        try
        {
            // The item lists are worded and ordered in the service, the header here — both in
            // the interface language, so the names come first.
            await SdeNames.EnsureLoadedAsync(ct);
            var data = await _service.GetDetailAsync(killMailId, ct);
            Detail = data is not null ? new KillmailDetailVm(data) : null;
        }
        catch (OperationCanceledException) { }
        catch { Detail = null; }
    }
}
