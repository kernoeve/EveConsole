using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using EveConsole.Data;
using EveConsole.Localization;
using EveConsole.Services;
using EveConsole.Services.Battle;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>One team's card and its lists, kept for the life of a report so that a kill arriving
/// changes the numbers in place rather than rebuilding the screen.</summary>
public sealed class BattleTeamVm : ReactiveObject
{
    public BattleTeamVm(int team, Action<int> remove)
    {
        ShipsView = new DataGridCollectionView(ShipRows);
        FamilyRows.Pin(ShipsView, null, ref _pinningShipSort);
        ShipsView.SortDescriptions.CollectionChanged += (_, _) => FamilyRows.Pin(ShipsView, null, ref _pinningShipSort);
        PilotsView = new DataGridCollectionView(PilotRows);
        FamilyRows.Pin(PilotsView, null, ref _pinningPilotSort);
        PilotsView.SortDescriptions.CollectionChanged += (_, _) => FamilyRows.Pin(PilotsView, null, ref _pinningPilotSort);
        SidesView = new DataGridCollectionView(Sides);
        FamilyRows.Pin(SidesView, null, ref _pinningSort);
        SidesView.SortDescriptions.CollectionChanged += (_, _) => FamilyRows.Pin(SidesView, null, ref _pinningSort);
        Team     = team;
        RemoveCommand = ReactiveCommand.Create(() => remove(Team));
        Title    = string.Format(BattleText.TeamName, (char)('A' + team));
        Swatch   = new SolidColorBrush(Color.Parse(ChartPaint.SeriesHex[team % ChartPaint.MaxSeries]));
    }

    public ReactiveCommand<Unit, Unit> RemoveCommand { get; }

    private bool _canRemove;

    /// <summary>Nobody is on it, and two teams would still be left.</summary>
    public bool CanRemove { get => _canRemove; set => this.RaiseAndSetIfChanged(ref _canRemove, value); }

    public int    Team   { get; }
    public string Title  { get; }
    public IBrush Swatch { get; }

    private BattleTeamStats? _stats;
    public void Update(BattleTeamStats stats)
    {
        _stats = stats;
        foreach (var name in new[] { nameof(Pilots), nameof(IskLost), nameof(Efficiency), nameof(ShipsLost), nameof(PodsLost), nameof(StructuresLost), nameof(DeployablesLost), nameof(Damage), nameof(StructureDamage), nameof(IsOurs) })
            this.RaisePropertyChanged(name);
    }

    public string Pilots          => (_stats?.Pilots ?? 0).ToString("N0");
    public string IskLost         => BattleFormat.Isk(_stats?.IskLost ?? 0);
    public string Efficiency      => (_stats?.Efficiency ?? 0).ToString("P1");
    public string ShipsLost       => (_stats?.ShipsLost ?? 0).ToString("N0");
    public string PodsLost        => (_stats?.PodsLost ?? 0).ToString("N0");
    public string StructuresLost  => (_stats?.StructuresLost ?? 0).ToString("N0");
    public string DeployablesLost => (_stats?.DeployablesLost ?? 0).ToString("N0");
    public string Damage          => BattleFormat.Count(_stats?.DamageDealt ?? 0);
    public string StructureDamage => BattleFormat.Count(_stats?.StructureDamage ?? 0);
    public bool   IsOurs          => _stats?.IsOurs ?? false;

    public BulkObservableCollection<BattleSideVm>  Sides     { get; } = [];

    /// <summary>The sides as the grid shows them: whichever column is sorted, an alliance's
    /// corporations stay under it.</summary>
    public DataGridCollectionView SidesView { get; }
    private bool _pinningSort;

    private IReadOnlyList<BattleSideVm> _alliances = [];

    /// <summary>The team's sides, each followed by its corporations where it is open.</summary>
    public void SetSides(IReadOnlyList<BattleSideVm> sides)
    {
        _alliances = sides;
        Relist();
    }

    private void Relist()
    {
        var rows = FamilyRows.WithChildren<BattleSideVm>(_alliances, s => s.IsExpanded, s => s.Children).ToList();
        for (var i = 0; i < _alliances.Count; i++) _alliances[i].PlaceInList(i);
        if (!Sides.SequenceEqual(rows)) Sides.ResetTo(rows);
    }

    /// <summary>Opens or closes an alliance, one row at a time so the list stays where it was.</summary>
    public void Toggle(BattleSideVm side)
    {
        if (!side.CanExpand) return;
        side.IsExpanded = !side.IsExpanded;
        FamilyRows.Toggle<BattleSideVm>(Sides, side, side.IsExpanded, side.Children);
    }
    public BulkObservableCollection<BattlePilotVm> PilotRows { get; } = [];

    /// <summary>The pilots as the grid shows them: whichever column is sorted, a pilot's hulls stay
    /// under them.</summary>
    public DataGridCollectionView PilotsView { get; }
    private bool _pinningPilotSort;

    private IReadOnlyList<BattlePilotVm> _pilots = [];

    /// <summary>The team's pilots, each followed by their hulls where they are open.</summary>
    public void SetPilots(IReadOnlyList<BattlePilotVm> pilots)
    {
        _pilots = pilots;
        var rows = FamilyRows.WithChildren<BattlePilotVm>(pilots, p => p.IsExpanded, p => p.Children).ToList();
        for (var i = 0; i < pilots.Count; i++) pilots[i].PlaceInList(i);
        if (!PilotRows.SequenceEqual(rows)) PilotRows.ResetTo(rows);
    }

    /// <summary>Opens or closes a pilot, one row at a time so the list stays where it was.</summary>
    public void Toggle(BattlePilotVm pilot)
    {
        if (!pilot.CanExpand) return;
        pilot.IsExpanded = !pilot.IsExpanded;
        FamilyRows.Toggle<BattlePilotVm>(PilotRows, pilot, pilot.IsExpanded, pilot.Children);
    }
    public BulkObservableCollection<BattleShipVm>  ShipRows  { get; } = [];

    /// <summary>The hulls as the grid shows them: whichever column is sorted, who flew a hull stays
    /// under it.</summary>
    public DataGridCollectionView ShipsView { get; }
    private bool _pinningShipSort;

    /// <summary>The team's hulls, each followed by whatever is open beneath it.</summary>
    public void SetShips(IReadOnlyList<BattleShipVm> ships)
    {
        var rows = new List<BattleShipVm>();
        for (var i = 0; i < ships.Count; i++)
        {
            ships[i].PlaceInList(i);
            ships[i].NumberFamily();
            rows.Add(ships[i]);
            rows.AddRange(ships[i].ShowingBelow());
        }
        if (!ShipRows.SequenceEqual(rows)) ShipRows.ResetTo(rows);
    }

    /// <summary>Opens or closes a hull or an alliance under it, one row at a time so the list stays
    /// where it was. Closing a hull takes an alliance open under it too, and it comes back open.</summary>
    public void Toggle(BattleShipVm ship)
    {
        if (!ship.CanExpand) return;
        if (ship.IsExpanded)
        {
            var going = ship.ShowingBelow().ToList();
            ship.IsExpanded = false;
            foreach (var row in going) ShipRows.Remove(row);
        }
        else
        {
            ship.IsExpanded = true;
            var at = ShipRows.IndexOf(ship);
            foreach (var row in ship.ShowingBelow()) ShipRows.Insert(++at, row);
        }
    }
}

/// <summary>ISK and large counts, as a battle report shows them.</summary>
public static class BattleFormat
{
    public static string Isk(double v) => IndustryOppsReport.Isk(v);

    public static string Count(long v) => v switch
    {
        >= 1_000_000_000 => $"{v / 1_000_000_000d:N2}B",
        >= 1_000_000     => $"{v / 1_000_000d:N2}M",
        >= 10_000        => $"{v / 1_000d:N1}K",
        _                => v.ToString("N0"),
    };
}

/// <summary>
/// The pictures on a battle's rows, fetched the first time a row asks for one. A row only asks when
/// it is drawn, so a fight of thousands of pilots fetches what is on the screen and no more, and the
/// image cache hands every row in the same hull the same picture.
/// </summary>
public static class BattleImages
{
    public static string? Corporation(long id) => id > 0 ? $"https://images.evetech.net/corporations/{id}/logo?size=32" : null;

    public static string? Alliance(long id) => id > 0 ? $"https://images.evetech.net/alliances/{id}/logo?size=32" : null;

    public static string? Ship(int typeId) => typeId > 0 ? $"https://images.evetech.net/types/{typeId}/icon?size=32" : null;

    /// <summary>A pilot's portrait; a structure, with no pilot, its owner's logo.</summary>
    public static string? Person(long characterId, long corporationId) =>
        characterId > 0 ? $"https://images.evetech.net/characters/{characterId}/portrait?size=32"
        : corporationId > 0 ? $"https://images.evetech.net/corporations/{corporationId}/logo?size=32"
        : null;

    public static void Load(string? url, Action<Bitmap?> set)
    {
        if (url is not null) _ = LoadAsync(url, set);
    }

    private static async Task LoadAsync(string url, Action<Bitmap?> set)
    {
        var bmp = await EveImageCache.GetAsync(url).ConfigureAwait(false);
        Dispatcher.UIThread.Post(() => set(bmp));
    }
}

/// <summary>A picture on a row, fetched the first time it is drawn.</summary>
public sealed class BattlePicture(string? url) : ReactiveObject
{
    private Bitmap? _image;
    private bool _asked;

    public Bitmap? Image
    {
        get
        {
            if (!_asked)
            {
                _asked = true;
                BattleImages.Load(url, v => { _image = v; this.RaisePropertyChanged(); });
            }
            return _image;
        }
    }
}

/// <summary>Who a name belongs to, shared by every row that shows it, and filled in as names arrive.</summary>
public sealed class BattleNames
{
    private readonly Dictionary<long, string> _names = [];
    public IReadOnlyDictionary<long, string> All => _names;
    public bool Has(long id) => _names.ContainsKey(id);
    public void Add(IReadOnlyDictionary<long, string> found) { foreach (var (id, name) in found) _names[id] = name; }

    /// <summary>A player's name as ESI gives it, or the id until it has come back.</summary>
    public string Of(long id) => id <= 0 ? "" : _names.TryGetValue(id, out var n) ? n : id.ToString(CultureInfo.InvariantCulture);

    /// <summary>A corporation, in the interface language where it is an NPC's.</summary>
    public string Corporation(long id) => id <= 0 ? "" : SdeNames.NpcCorporation(id, Of(id));
}

/// <summary>One alliance, corporation or lone pilot on a team, with the arrows that move it.</summary>
public sealed class BattleSideVm : ReactiveObject, IFamilyRow
{
    private readonly BattleNames _names;

    public BattleSideVm(BattleSideRow row, BattleNames names, Action<long, int> move, BattleSideVm? parent = null)
    {
        _parent = parent;
        SideId = row.SideId;
        Kind   = row.Kind;
        _row   = row;
        _names = names;
        MoveLeftCommand  = ReactiveCommand.Create(() => move(SideId, _row.Team - 1));
        MoveRightCommand = ReactiveCommand.Create(() => move(SideId, _row.Team + 1));
        OpenCommand      = ReactiveCommand.Create(Open);
    }

    public long           SideId { get; }
    public BattleSideKind Kind   { get; }

    private readonly BattleSideVm? _parent;
    private BattleSideVm Family => _parent ?? this;
    private int _familyOrder, _childOrder;

    public bool IsChild => _parent is not null;
    public int FamilyOrder => Family._familyOrder;
    public int ChildOrder => _childOrder;

    /// <summary>Its corporations, when it is an alliance.</summary>
    public IReadOnlyList<BattleSideVm> Children { get; private set; } = [];
    public bool CanExpand => Children.Count > 0;

    private bool _isExpanded;
    public bool IsExpanded { get => _isExpanded; set => this.RaiseAndSetIfChanged(ref _isExpanded, value); }
    public Thickness NameMargin => IsChild ? new Thickness(18, 0, 0, 0) : new Thickness(4, 0, 0, 0);

    public void SetChildren(IReadOnlyList<BattleSideVm> children)
    {
        Children = children;
        for (var i = 0; i < children.Count; i++) children[i]._childOrder = i + 1;
        this.RaisePropertyChanged(nameof(CanExpand));
    }

    public void PlaceInList(int order) => _familyOrder = order;

    // What the columns sort by. ⚠️ The alliance's numbers on its corporations too, or a sort by any
    // column would scatter them through the list; each family then keeps its own order.
    public string SortName   => Family.Name;
    public int    SortPilots => Family.Pilots;
    public int    SortKills  => Family.Kills;
    public int    SortLost   => Family.Lost;
    public double SortIsk    => Family.IskLost;
    public long   SortDamage => Family.Damage;

    private BattleSideRow _row;

    public string Name => Kind == BattleSideKind.Corporation ? _names.Corporation(SideId) : _names.Of(SideId);
    public int    Pilots   => _row.Pilots;
    public double IskLost  => _row.IskLost;
    public int    Lost     => _row.ShipsLost;
    public long   Damage   => _row.Damage;
    public int    Kills    => _row.Kills;
    public bool   IsOurs   => _row.IsOurs;
    public string IskText    => BattleFormat.Isk(_row.IskLost);
    public string DamageText => BattleFormat.Count(_row.Damage);
    public FontWeight NameWeight => IsOurs ? FontWeight.SemiBold : FontWeight.Normal;

    private Bitmap? _logo;
    private bool _askedLogo;

    /// <summary>Fetched when first drawn: a fight's corporations run to thousands.</summary>
    public Bitmap? Logo
    {
        get
        {
            if (!_askedLogo)
            {
                _askedLogo = true;
                BattleImages.Load(Kind switch
                {
                    BattleSideKind.Alliance    => BattleImages.Alliance(SideId),
                    BattleSideKind.Corporation => BattleImages.Corporation(SideId),
                    _                          => BattleImages.Person(SideId, 0),
                }, v => { _logo = v; this.RaisePropertyChanged(); });
            }
            return _logo;
        }
    }

    private bool _canLeft, _canRight;
    public bool CanMoveLeft  { get => _canLeft;  private set => this.RaiseAndSetIfChanged(ref _canLeft,  value); }
    public bool CanMoveRight { get => _canRight; private set => this.RaiseAndSetIfChanged(ref _canRight, value); }

    public ReactiveCommand<Unit, Unit> MoveLeftCommand  { get; }
    public ReactiveCommand<Unit, Unit> MoveRightCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenCommand      { get; }

    public void Update(BattleSideRow row, int teamCount)
    {
        _row = row;
        CanMoveLeft  = !IsChild && row.Team > 0;
        CanMoveRight = !IsChild && row.Team < teamCount - 1;
        foreach (var name in new[] { nameof(Pilots), nameof(IskLost), nameof(Lost), nameof(Damage), nameof(Kills), nameof(IsOurs), nameof(IskText), nameof(DamageText), nameof(NameWeight), nameof(SortPilots), nameof(SortKills), nameof(SortLost), nameof(SortIsk), nameof(SortDamage) })
            this.RaisePropertyChanged(name);
    }

    public void NamesChanged()
    {
        this.RaisePropertyChanged(nameof(Name));
        this.RaisePropertyChanged(nameof(SortName));
    }

    public void OpenDetails() => Open();

    private void Open() => EntityNavigator.Instance.Entity(Kind switch
    {
        BattleSideKind.Alliance    => EntityKind.Alliance,
        BattleSideKind.Corporation => EntityKind.PlayerCorp,
        _                          => EntityKind.Pilot,
    }, SideId);
}

/// <summary>
/// One pilot on a team, or under them, one of the hulls they had: flown, lost, or both. A pilot with
/// more than one opens into a line for each, their own line carrying the totals.
/// </summary>
public sealed class BattlePilotVm : ReactiveObject, IFamilyRow
{
    private readonly BattleNames _names;
    private readonly Func<int, string> _shipName;
    private readonly Func<BattlePilotRow, bool, IReadOnlyList<BattleKillRow>> _losses;
    private readonly BattlePilotVm? _parent;

    public BattlePilotVm(BattlePilotRow row, BattleNames names, Func<int, string> shipName,
                         Func<BattlePilotRow, bool, IReadOnlyList<BattleKillRow>> losses, BattlePilotVm? parent = null)
    {
        _row = row;
        _names = names;
        _shipName = shipName;
        _losses = losses;
        _parent = parent;
        CorpLogo     = new BattlePicture(parent is null ? BattleImages.Corporation(row.CorporationId) : null);
        AllianceLogo = new BattlePicture(parent is null ? BattleImages.Alliance(row.AllianceId) : null);
        OpenCommand  = ReactiveCommand.Create(Open);
    }

    public BattlePicture CorpLogo { get; }
    public BattlePicture AllianceLogo { get; }
    public ReactiveCommand<Unit, Unit> OpenCommand { get; }

    // ── The family ───────────────────────────────────────────────────────

    private BattlePilotVm Family => _parent ?? this;
    private int _familyOrder, _childOrder;

    public bool IsChild => _parent is not null;
    public int FamilyOrder => Family._familyOrder;
    public int ChildOrder => _childOrder;
    public void PlaceInList(int order) => _familyOrder = order;

    /// <summary>The hulls behind this line, when there is more than one.</summary>
    public IReadOnlyList<BattlePilotVm> Children { get; private set; } = [];
    public bool CanExpand => Children.Count > 0;

    private bool _isExpanded;
    public bool IsExpanded { get => _isExpanded; set => this.RaiseAndSetIfChanged(ref _isExpanded, value); }

    public void SetChildren(IReadOnlyList<BattlePilotVm> children)
    {
        var changed = children.Count != Children.Count;
        Children = children;
        for (var i = 0; i < children.Count; i++) children[i]._childOrder = i + 1;
        if (children.Count == 0) IsExpanded = false;
        if (!changed) return;
        this.RaisePropertyChanged(nameof(CanExpand));
        this.RaisePropertyChanged(nameof(Ship));
    }

    public bool ShowsPilot => !IsChild;
    public Thickness ShipMargin => IsChild ? new Thickness(16, 0, 4, 0) : new Thickness(4, 0);

    // What the columns sort by. ⚠️ The pilot's numbers on their hulls too, or a sort by any column
    // would scatter a family through the list; it then keeps its own order inside.
    public string SortPilot    => Family.Pilot;
    public string SortShip     => Family.Ship;
    public long   SortDamage   => Family.Damage;
    public int    SortKills    => Family.Kills;
    public int    SortLost     => Family.Lost;
    public double SortIsk      => Family.IskLost;
    public string SortCorp     => Family.Corp;
    public string SortAlliance => Family.Alliance;

    // ── The line ─────────────────────────────────────────────────────────

    private BattlePilotRow _row;

    /// <summary>The kills this line lost, oldest first, to open: a pilot's line, across all they flew;
    /// a hull's, in that hull.</summary>
    public IReadOnlyList<BattleKillRow> Losses() => _losses(_row, !IsChild);

    public bool HasLoss => _row.Lost > 0;

    private Bitmap? _portrait, _shipIcon;
    private bool _askedPortrait, _askedShip;

    public Bitmap? Portrait
    {
        get
        {
            if (!_askedPortrait && !IsChild)
            {
                _askedPortrait = true;
                BattleImages.Load(BattleImages.Person(_row.CharacterId, _row.CorporationId), v => { _portrait = v; this.RaisePropertyChanged(); });
            }
            return _portrait;
        }
    }

    public Bitmap? ShipIcon
    {
        get
        {
            if (!_askedShip)
            {
                _askedShip = true;
                BattleImages.Load(BattleImages.Ship(_row.ShipTypeId), v => { _shipIcon = v; this.RaisePropertyChanged(); });
            }
            return _shipIcon;
        }
    }

    public long CharacterId => _row.CharacterId;
    public string Pilot    => IsChild ? "" : _row.CharacterId > 0 ? _names.Of(_row.CharacterId) : HullName;
    private string HullName => _shipName(_row.ShipTypeId) is { Length: > 0 } name ? name : BattleText.UnknownShip;

    /// <summary>The hull; on a pilot with several, the main one and how many more.</summary>
    public string Ship     => Children.Count > 1 ? $"{HullName} +{Children.Count - 1}" : HullName;
    public string Corp     => IsChild ? "" : _names.Corporation(_row.CorporationId);
    public string Alliance => IsChild ? "" : _names.Of(_row.AllianceId);
    public long   Damage   => _row.Damage;
    public int    Kills    => _row.Kills;
    public int    FinalBlows => _row.FinalBlows;
    public int    Lost     => _row.Lost;
    public double IskLost  => _row.IskLost;
    public bool   IsOurs   => _row.IsOurs;
    public string DamageText => _row.Damage > 0 ? BattleFormat.Count(_row.Damage) : "";
    public string IskText    => _row.IskLost > 0 ? BattleFormat.Isk(_row.IskLost) : "";
    public string LostText   => _row.Lost > 0 ? _row.Lost.ToString("N0") : "";
    public FontWeight NameWeight => IsOurs ? FontWeight.SemiBold : FontWeight.Normal;

    /// <summary>A loss shows at a glance: tinted on the pilot's line, and under it on the hull lost,
    /// the hulls they did not lose left clear.</summary>
    public IBrush RowTint => _row.Lost > 0 ? Palette.BadSurface : Brushes.Transparent;

    public void Update(BattlePilotRow row)
    {
        if (row == _row) return;
        var newHull = row.ShipTypeId != _row.ShipTypeId;
        _row = row;
        if (newHull)
        {
            _askedShip = false;
            this.RaisePropertyChanged(nameof(ShipIcon));
            this.RaisePropertyChanged(nameof(Ship));
        }
        foreach (var name in new[] { nameof(Corp), nameof(Alliance), nameof(Damage), nameof(Kills), nameof(FinalBlows), nameof(Lost), nameof(IskLost), nameof(IsOurs), nameof(DamageText), nameof(IskText), nameof(LostText), nameof(NameWeight), nameof(RowTint), nameof(HasLoss), nameof(SortDamage), nameof(SortKills), nameof(SortLost), nameof(SortIsk) })
            this.RaisePropertyChanged(name);
    }

    public void NamesChanged()
    {
        foreach (var name in new[] { nameof(Pilot), nameof(Ship), nameof(Corp), nameof(Alliance), nameof(SortPilot), nameof(SortShip), nameof(SortCorp), nameof(SortAlliance) })
            this.RaisePropertyChanged(name);
    }

    /// <summary>The pilot, in the entity viewer; a structure, its owner.</summary>
    public void Open()
    {
        if (_row.CharacterId > 0) EntityNavigator.Instance.Entity(EntityKind.Pilot, _row.CharacterId);
        else EntityNavigator.Instance.Entity(EntityLinks.KindOf(_row.CorporationId), _row.CorporationId);
    }
}

/// <summary>
/// One hull type on a team, or under it, who flew that hull: an alliance or a corporation with none,
/// and under an alliance, its corporations. Every level has its own numbers for the hull.
/// </summary>
public sealed class BattleShipVm : ReactiveObject, IFamilyRow
{
    private readonly Func<int, BattleShipType?> _type;
    private readonly BattleNames _names;
    private readonly BattleShipVm? _parent;
    private readonly int _shipTypeId;
    private readonly long _flyerId;
    private readonly BattleSideKind _kind;

    /// <summary>A hull type.</summary>
    public BattleShipVm(BattleShipRow row, Func<int, BattleShipType?> type, BattleNames names)
    {
        _type = type;
        _names = names;
        _shipTypeId = row.ShipTypeId;
        OpenCommand = ReactiveCommand.Create(Open);
        Update(row.Pilots, row.Lost, row.IskLost, row.Damage);
    }

    /// <summary>Who flew it, under the hull or under their alliance.</summary>
    public BattleShipVm(BattleShipFlyer flyer, BattleShipVm parent)
    {
        _type = parent._type;
        _names = parent._names;
        _parent = parent;
        _shipTypeId = parent._shipTypeId;
        _flyerId = flyer.Id;
        _kind = flyer.Kind;
        Level = parent.Level + 1;
        OpenCommand = ReactiveCommand.Create(Open);
        Update(flyer.Pilots, flyer.Lost, flyer.IskLost, flyer.Damage);
    }

    public ReactiveCommand<Unit, Unit> OpenCommand { get; }

    // ── The family ───────────────────────────────────────────────────────

    /// <summary>0 for the hull, 1 for who flew it, 2 for an alliance's corporations.</summary>
    public int Level { get; }

    private BattleShipVm Root => _parent?.Root ?? this;
    private int _familyOrder, _childOrder;

    public bool IsChild => _parent is not null;
    public int FamilyOrder => Root._familyOrder;
    public int ChildOrder => _childOrder;
    public void PlaceInList(int order) => _familyOrder = order;

    public IReadOnlyList<BattleShipVm> Children { get; private set; } = [];
    public bool CanExpand => Children.Count > 0;

    private bool _isExpanded;
    public bool IsExpanded { get => _isExpanded; set => this.RaiseAndSetIfChanged(ref _isExpanded, value); }

    public Thickness Indent => new(4 + Level * 16, 0, 0, 0);

    public void SetChildren(IReadOnlyList<BattleShipVm> children)
    {
        var changed = children.Count != Children.Count;
        Children = children;
        if (changed) this.RaisePropertyChanged(nameof(CanExpand));
    }

    /// <summary>The rows under this one now showing, in the order they read: an alliance left open
    /// keeps its corporations.</summary>
    public IEnumerable<BattleShipVm> ShowingBelow()
    {
        if (!IsExpanded) yield break;
        foreach (var child in Children)
        {
            yield return child;
            foreach (var below in child.ShowingBelow()) yield return below;
        }
    }

    /// <summary>Numbers this hull's whole family in reading order, shown or not, so a row put in
    /// later sorts into the right place.</summary>
    public void NumberFamily()
    {
        var next = 0;
        Walk(this);

        void Walk(BattleShipVm row)
        {
            row._childOrder = next++;
            foreach (var child in row.Children) Walk(child);
        }
    }

    // What the columns sort by. ⚠️ The hull's numbers on all its family, or a sort would scatter them.
    public string SortName   => Root.Name;
    public string SortGroup  => Root.Group;
    public int    SortPilots => Root.Pilots;
    public int    SortLost   => Root.Lost;
    public double SortIsk    => Root.IskLost;
    public long   SortDamage => Root.Damage;

    // ── The line ─────────────────────────────────────────────────────────

    private Bitmap? _icon;
    private bool _askedIcon;

    /// <summary>The hull's icon, or who flew it's logo. Fetched when first drawn.</summary>
    public Bitmap? Icon
    {
        get
        {
            if (!_askedIcon)
            {
                _askedIcon = true;
                BattleImages.Load(!IsChild ? BattleImages.Ship(_shipTypeId)
                    : _kind == BattleSideKind.Alliance ? BattleImages.Alliance(_flyerId)
                    : BattleImages.Corporation(_flyerId), v => { _icon = v; this.RaisePropertyChanged(); });
            }
            return _icon;
        }
    }

    public int    ShipTypeId => _shipTypeId;
    public string Name => IsChild
        ? (_kind == BattleSideKind.Alliance ? _names.Of(_flyerId) : _names.Corporation(_flyerId))
        : _type(_shipTypeId) is { } t ? SdeNames.Type(t.TypeId, t.Name) : _shipTypeId.ToString(CultureInfo.InvariantCulture);
    public string Group => !IsChild && _type(_shipTypeId) is { } t ? SdeNames.Group(t.GroupId, t.GroupName) : "";

    public int    Pilots  { get; private set; }
    public int    Lost    { get; private set; }
    public double IskLost { get; private set; }
    public long   Damage  { get; private set; }
    public string IskText    => IskLost > 0 ? BattleFormat.Isk(IskLost) : "";
    public string LostText   => Lost > 0 ? Lost.ToString("N0") : "";
    public string DamageText => Damage > 0 ? BattleFormat.Count(Damage) : "";

    public void Update(int pilots, int lost, double iskLost, long damage)
    {
        if (pilots == Pilots && lost == Lost && iskLost == IskLost && damage == Damage) return;
        (Pilots, Lost, IskLost, Damage) = (pilots, lost, iskLost, damage);
        foreach (var name in new[] { nameof(Pilots), nameof(Lost), nameof(IskLost), nameof(Damage), nameof(IskText), nameof(LostText), nameof(DamageText), nameof(SortPilots), nameof(SortLost), nameof(SortIsk), nameof(SortDamage) })
            this.RaisePropertyChanged(name);
    }

    public void NamesChanged()
    {
        foreach (var name in new[] { nameof(Name), nameof(Group), nameof(SortName), nameof(SortGroup) })
            this.RaisePropertyChanged(name);
    }

    /// <summary>The hull, in the Item Browser; who flew it, in the entity viewer.</summary>
    public void Open()
    {
        if (!IsChild) EntityNavigator.Instance.Item(_shipTypeId);
        else if (_kind == BattleSideKind.Alliance) EntityNavigator.Instance.Entity(EntityKind.Alliance, _flyerId);
        else EntityNavigator.Instance.Entity(EntityLinks.KindOf(_flyerId), _flyerId);
    }
}

/// <summary>One loss on the timeline.</summary>
public sealed class BattleKillVm : ReactiveObject
{
    private readonly BattleNames _names;
    private readonly Func<int, string> _shipName;
    private readonly Func<int, string> _systemName;
    private readonly IBrush _none;
    private readonly IReadOnlyList<BattleTeamVm> _teams;

    public BattleKillVm(BattleKillRow row, BattleNames names, Func<int, string> shipName, Func<int, string> systemName,
                        IReadOnlyList<BattleTeamVm> teams, bool arrivedLive)
    {
        _row = row;
        _names = names;
        _shipName = shipName;
        _systemName = systemName;
        _teams = teams;
        _none = Brushes.Transparent;
        ArrivedAt = arrivedLive ? DateTimeOffset.UtcNow : null;
        IsNew = arrivedLive;

        OpenVictimCommand    = ReactiveCommand.Create(OpenVictim);
        OpenFinalBlowCommand = ReactiveCommand.Create(OpenFinalBlow);
        VictimCorpLogo     = new BattlePicture(BattleImages.Corporation(row.VictimCorpId));
        VictimAllianceLogo = new BattlePicture(BattleImages.Alliance(row.VictimAllianceId));
        FinalBlowPortrait  = new BattlePicture(BattleImages.Person(row.FinalBlowCharId, row.FinalBlowCorpId));
        FinalBlowCorpLogo  = new BattlePicture(BattleImages.Corporation(row.FinalBlowCorpId));
        FinalBlowAllianceLogo = new BattlePicture(BattleImages.Alliance(row.FinalBlowAllianceId));
    }

    public ReactiveCommand<Unit, Unit> OpenVictimCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenFinalBlowCommand { get; }
    public BattlePicture VictimCorpLogo { get; }
    public BattlePicture VictimAllianceLogo { get; }
    public BattlePicture FinalBlowPortrait { get; }
    public BattlePicture FinalBlowCorpLogo { get; }
    public BattlePicture FinalBlowAllianceLogo { get; }

    /// <summary>Who landed the last hit: a pilot's name, or for a rat its corporation.</summary>
    public string FinalBlow => _row.FinalBlowCharId > 0 ? _names.Of(_row.FinalBlowCharId) : _names.Corporation(_row.FinalBlowCorpId);
    public string FinalBlowCorp => _names.Corporation(_row.FinalBlowCorpId);
    public string FinalBlowAlliance => _names.Of(_row.FinalBlowAllianceId);

    private BattleKillRow _row;

    private Bitmap? _portrait, _shipIcon;
    private bool _askedPortrait, _askedShip;

    public Bitmap? Portrait
    {
        get
        {
            if (!_askedPortrait)
            {
                _askedPortrait = true;
                BattleImages.Load(BattleImages.Person(_row.VictimCharId, _row.VictimCorpId), v => { _portrait = v; this.RaisePropertyChanged(); });
            }
            return _portrait;
        }
    }

    public Bitmap? ShipIcon
    {
        get
        {
            if (!_askedShip)
            {
                _askedShip = true;
                BattleImages.Load(BattleImages.Ship(_row.ShipTypeId), v => { _shipIcon = v; this.RaisePropertyChanged(); });
            }
            return _shipIcon;
        }
    }

    public int      KillMailId => _row.KillMailId;
    public DateTimeOffset TimeRaw => _row.Time;
    public string   Time     => _row.Time.UtcDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    public string   System   => _systemName(_row.SystemId);
    public string   Ship     => _shipName(_row.ShipTypeId);
    public string   Victim   => _row.VictimCharId > 0 ? _names.Of(_row.VictimCharId) : _names.Corporation(_row.VictimCorpId);
    public string   Corp     => _names.Corporation(_row.VictimCorpId);
    public string   Alliance => _names.Of(_row.VictimAllianceId);
    public double   Value    => _row.Value;
    public string   ValueText => BattleFormat.Isk(_row.Value);
    public int      Attackers => _row.Attackers;
    public string   TeamName => _row.Team >= 0 && _row.Team < _teams.Count ? _teams[_row.Team].Title : "";
    public IBrush   TeamBrush => _row.Team >= 0 && _row.Team < _teams.Count ? _teams[_row.Team].Swatch : _none;

    /// <summary>When a kill came in while the report was open, for the marker beside it.</summary>
    public DateTimeOffset? ArrivedAt { get; }

    private bool _isNew;
    public bool IsNew { get => _isNew; set => this.RaiseAndSetIfChanged(ref _isNew, value); }

    public void Update(BattleKillRow row)
    {
        if (row == _row) return;
        _row = row;
        this.RaisePropertyChanged(nameof(TeamName));
        this.RaisePropertyChanged(nameof(TeamBrush));
    }

    public void NamesChanged()
    {
        foreach (var name in new[] { nameof(Ship), nameof(Victim), nameof(Corp), nameof(Alliance), nameof(System), nameof(FinalBlow), nameof(FinalBlowCorp), nameof(FinalBlowAlliance) })
            this.RaisePropertyChanged(name);
    }

    public void Open() => EntityNavigator.Instance.Killmail(_row.KillMailId);

    /// <summary>The victim or the attacker, in the entity viewer. A structure or a rat has no pilot,
    /// so its corporation.</summary>
    private static void OpenPerson(long character, long corporation)
    {
        if (character > 0) EntityNavigator.Instance.Entity(EntityKind.Pilot, character);
        else EntityNavigator.Instance.Entity(EntityLinks.KindOf(corporation), corporation);
    }

    public void OpenVictim() => OpenPerson(_row.VictimCharId, _row.VictimCorpId);
    public void OpenFinalBlow() => OpenPerson(_row.FinalBlowCharId, _row.FinalBlowCorpId);
}

/// <summary>A system or a whole constellation found by the search box: picking it adds its systems.</summary>
public sealed class BattleSearchResult
{
    public string Label { get; init; } = "";
    public IReadOnlyList<SystemSearchResult> Systems { get; init; } = [];
    public bool IsConstellation { get; init; }
}

/// <summary>One system in the battle being defined, with its own start and end. A blank end, or one
/// yet to come, is what makes it eligible for watching live.</summary>
public sealed class BattleSystemRowVm : ReactiveObject
{
    public BattleSystemRowVm(int systemId, string name, string region, string from, string to, Action<BattleSystemRowVm> remove)
    {
        SystemId  = systemId;
        Name      = name;
        Label     = region.Length > 0 ? $"{name}  ·  {region}" : name;
        _fromText = from;
        _toText   = to;
        RemoveCommand = ReactiveCommand.Create(() => remove(this));
    }

    public int    SystemId { get; }
    public string Name     { get; }
    public string Label    { get; }

    private string _fromText, _toText;
    public string FromText { get => _fromText; set => this.RaiseAndSetIfChanged(ref _fromText, value); }
    public string ToText
    {
        get => _toText;
        set { this.RaiseAndSetIfChanged(ref _toText, value); this.RaisePropertyChanged(nameof(CanWatch)); }
    }

    /// <summary>Marks a row whose end leaves room for more kills.</summary>
    public bool CanWatch =>
        string.IsNullOrWhiteSpace(ToText) || BattleSessionViewModel.ParseTime(ToText) is not { } end || end > DateTimeOffset.UtcNow;

    private string _killsText = "";
    public string KillsText { get => _killsText; private set => this.RaiseAndSetIfChanged(ref _killsText, value); }
    public void SetKills(int count) => KillsText = string.Format(BattleText.RowKills, count);

    public ReactiveCommand<Unit, Unit> RemoveCommand { get; }
}

/// <summary>
/// One battle open in the Battle Report tool: the systems it spans, each over its own time, and
/// the report of them read as one fight from the kills in this database. Where any of it is still
/// open it can be watched live, and every number, list and the chart move as kills arrive, with
/// nothing to refresh, whichever battle is on the screen.
/// </summary>
public sealed class BattleSessionViewModel : ReactiveObject
{
    /// <summary>How often a live report looks for kills. zKillboard's stream at its own pace asks
    /// no more often than every 7 seconds, so faster would mostly find nothing; a mail reaching
    /// the database shows within this.</summary>
    public static readonly TimeSpan LivePollInterval = TimeSpan.FromSeconds(4);

    private static readonly TimeSpan NewMarkerFor = TimeSpan.FromSeconds(20);

    private readonly BattleReportService _service;
    private readonly Func<IReadOnlyList<long>, Task<Dictionary<long, string>>> _nameLookup;
    private readonly ZkillboardSettings  _zkb;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly DispatcherTimer _ticker;
    private readonly Func<BattleSessionViewModel, Task> _saved;

    public BattleSessionViewModel(BattleReportService service, Func<IReadOnlyList<long>, Task<Dictionary<long, string>>> nameLookup,
                                  ZkillboardSettings zkb, IDbContextFactory<AppDbContext> dbFactory,
                                  Func<BattleSessionViewModel, Task> saved, Action<BattleSessionViewModel> close)
    {
        _service    = service;
        _nameLookup = nameLookup;
        _zkb        = zkb;
        _dbFactory  = dbFactory;
        _saved      = saved;

        _fromText = FormatTime(DateTimeOffset.UtcNow.AddHours(-1));

        RunCommand      = ReactiveCommand.CreateFromTask(RunAsync);
        ApplyTimesCommand = ReactiveCommand.Create(ApplyTimes);
        SaveCommand     = ReactiveCommand.CreateFromTask(SaveAsync);
        CloseCommand    = ReactiveCommand.Create(() => close(this));
        AddTeamCommand  = ReactiveCommand.CreateFromTask(AddTeamAsync);
        FromKillCommand = ReactiveCommand.CreateFromTask(FromKillTextAsync);

        RunCommand.ThrownExceptions.Subscribe(ex => Fail(ex));
        AddTeamCommand.ThrownExceptions.Subscribe(ex => Fail(ex));
        FromKillCommand.ThrownExceptions.Subscribe(ex => Fail(ex));
        SaveCommand.ThrownExceptions.Subscribe(ex => Fail(ex));

        this.WhenAnyValue(x => x.SearchText)
            .Throttle(TimeSpan.FromMilliseconds(250))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(t => _ = SearchAsync(t ?? ""));

        // Once a second on the screen only: how long ago the last kill was, and the markers fading.
        _ticker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _ticker.Tick += (_, _) => Tick();

        ChartPaint.TrackAxesOf(this);

        Systems.CollectionChanged += (_, _) => RaiseTitle();

        // A name typed into a saved battle is kept once typing stops.
        this.WhenAnyValue(x => x.Name).Skip(1)
            .Throttle(TimeSpan.FromMilliseconds(600))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(__ => { _ = KeepAsync(); });
    }

    private const string TimeFormat = "yyyy-MM-dd HH:mm";

    // ── Which battle this is ─────────────────────────────────────────────────

    private string _name = "";
    public string Name
    {
        get => _name;
        set { this.RaiseAndSetIfChanged(ref _name, value); RaiseTitle(); }
    }

    /// <summary>What its tab says: the name given it, or where and when it was.</summary>
    public string Title
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Name)) return Name.Trim();
            if (Systems.Count == 0) return BattleText.NewBattle;
            var names = Systems.Select(r => r.Name).Distinct().ToList();
            return string.Join(", ", names.Take(2)) + (names.Count > 2 ? " +" + (names.Count - 2) : "");
        }
    }

    private void RaiseTitle() => this.RaisePropertyChanged(nameof(Title));

    private int? _savedId;

    /// <summary>Its row in the saved battles; null until it is saved.</summary>
    public int? SavedId
    {
        get => _savedId;
        set { this.RaiseAndSetIfChanged(ref _savedId, value); this.RaisePropertyChanged(nameof(IsSaved)); }
    }

    public bool IsSaved => SavedId is not null;

    /// <summary>Saves it, so it can be pulled up later. From then on its changes keep themselves.</summary>
    private async Task SaveAsync()
    {
        if (ReadWindows() is null) return;
        await _saved(this);
    }

    /// <summary>Keeps a change to a battle that is already saved. A row left half typed waits for
    /// the next change rather than overwriting what was kept.</summary>
    private async Task KeepAsync()
    {
        if (!IsSaved || TryReadWindows(out _, out _) is false) return;
        try { await _saved(this); }
        catch (Exception ex) { Fail(ex); }
    }

    /// <summary>What is kept of it: the rows as they stand, and how its sides were placed.</summary>
    public BattleDefinition Definition()
    {
        TryReadWindows(out var windows, out _);
        var definition = new BattleDefinition { Windows = windows ?? [], WatchLive = WatchLive };
        if (_engine is { } engine)
            lock (_engineLock)
            {
                definition.TeamCount = engine.TeamCount;
                definition.Moves     = engine.Moves.ToDictionary(kv => kv.Key, kv => kv.Value);
            }
        else if (_restored is { } restored)
        {
            definition.TeamCount = restored.TeamCount;
            definition.Moves     = restored.Moves;
        }
        return definition;
    }

    /// <summary>Which of the report's tabs is showing. Here rather than in the view: the view is
    /// shared between battles, and each keeps its own place.</summary>
    private int _selectedTab;
    public int SelectedTab { get => _selectedTab; set => this.RaiseAndSetIfChanged(ref _selectedTab, value); }

    private bool _isDefining = true;

    /// <summary>The systems and times are showing. Folded away once a report is open, for the room.</summary>
    public bool IsDefining { get => _isDefining; set => this.RaiseAndSetIfChanged(ref _isDefining, value); }

    // ── The form ─────────────────────────────────────────────────────────────

    /// <summary>The systems of the battle, each over its own time. A sov fight can run across a
    /// constellation or several, moving as it goes, and all of it is one battle.</summary>
    public ObservableCollection<BattleSystemRowVm> Systems { get; } = [];
    public ObservableCollection<BattleSearchResult> SearchResults { get; } = [];

    private string _searchText = "";
    public string SearchText { get => _searchText; set => this.RaiseAndSetIfChanged(ref _searchText, value); }

    private bool _showResults;
    public bool ShowResults { get => _showResults; private set => this.RaiseAndSetIfChanged(ref _showResults, value); }

    /// <summary>A search result picked: added, after the click that picked it.</summary>
    public BattleSearchResult? PickedResult
    {
        get => null;
        set { if (value is { } pick) Dispatcher.UIThread.Post(() => AddPlace(pick)); }
    }

    /// <summary>Adds what was picked, with the times in the boxes above. One system may be added
    /// again, for a second stretch of the fight; a whole constellation adds only what is missing.</summary>
    private void AddPlace(BattleSearchResult pick)
    {
        SearchText = "";
        foreach (var s in pick.Systems)
            if (!pick.IsConstellation || Systems.All(r => r.SystemId != s.SystemId))
                Systems.Add(NewRow(s.SystemId, s.Name, s.Region, FromText, ToText));
    }

    private BattleSystemRowVm NewRow(int systemId, string name, string region, string from, string to) =>
        new(systemId, name, region, from, to, row => Systems.Remove(row));

    /// <summary>Gives every row the times in the boxes above.</summary>
    private void ApplyTimes()
    {
        foreach (var row in Systems)
        {
            row.FromText = FromText;
            row.ToText   = ToText;
        }
    }

    private async Task SearchAsync(string text)
    {
        text = text.Trim();
        SearchResults.Clear();
        if (text.Length < 2) { ShowResults = false; return; }

        var systems = await Task.Run(() => SystemIndexReport.SearchAsync(_dbFactory, text));
        var constellations = await Task.Run(() => _service.SearchConstellationsAsync(text));
        if (SearchText.Trim() != text) return;

        // Constellations first: there are few of them, and a system list can run on.
        foreach (var c in constellations)
            SearchResults.Add(new BattleSearchResult
            {
                IsConstellation = true,
                Label = string.Format(BattleText.ConstellationResult, c.Name, c.Region),
                Systems = [.. c.Systems.Select(s => new SystemSearchResult { SystemId = s.SystemId, Name = s.Name, Region = s.Region })],
            });
        foreach (var s in systems)
        {
            var one = new SystemSearchResult { SystemId = s.SystemId, Name = s.Name, Region = s.Region };
            SearchResults.Add(new BattleSearchResult { Label = one.Label, Systems = [one] });
        }
        ShowResults = SearchResults.Count > 0;
    }

    private string _fromText, _toText = "", _killText = "";

    /// <summary>The times a system takes when added, and that "Apply to all" gives every row.</summary>
    public string FromText { get => _fromText; set => this.RaiseAndSetIfChanged(ref _fromText, value); }

    /// <summary>Blank is a fight still going.</summary>
    public string ToText   { get => _toText;   set => this.RaiseAndSetIfChanged(ref _toText,   value); }

    /// <summary>A kill link or id, pasted, to start from the system and time of that kill.</summary>
    public string KillText { get => _killText; set => this.RaiseAndSetIfChanged(ref _killText, value); }

    public ReactiveCommand<Unit, Unit> RunCommand      { get; }
    public ReactiveCommand<Unit, Unit> ApplyTimesCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveCommand     { get; }
    public ReactiveCommand<Unit, Unit> CloseCommand    { get; }
    public ReactiveCommand<Unit, Unit> AddTeamCommand  { get; }
    public ReactiveCommand<Unit, Unit> FromKillCommand { get; }

    internal static DateTimeOffset? ParseTime(string text) =>
        DateTime.TryParseExact(text.Trim(), TimeFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t)
            ? new DateTimeOffset(t, TimeSpan.Zero) : null;

    private static string FormatTime(DateTimeOffset time) => time.UtcDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture);

    /// <summary>The system of a pasted kill, and the hour around it, ready to run.</summary>
    private async Task FromKillTextAsync()
    {
        if (BattleReportService.ParseKillId(KillText) is not { } id) { SetError(BattleText.KillNotRead); return; }
        await OpenFromKillAsync(id);
    }

    /// <summary>A report on the fight around one kill: its system, half an hour either side. An end
    /// still to come leaves it live until then.</summary>
    public async Task OpenFromKillAsync(int killMailId)
    {
        var anchor = await Task.Run(() => _service.KillAnchorAsync(killMailId));
        if (anchor is not { } a) { SetError(string.Format(BattleText.KillNotHeld, killMailId)); return; }

        var names = await Task.Run(() => SystemIndexReport.LookUpAsync(_dbFactory, [a.SystemId]));
        KillText = "";
        FromText = FormatTime(a.Time.AddMinutes(-30));
        ToText   = FormatTime(a.Time.AddMinutes(30));
        Systems.Clear();
        foreach (var s in names) Systems.Add(NewRow(s.SystemId, s.Name, s.Region, FromText, ToText));
        await RunAsync();
    }

    /// <summary>Fills this battle in from a saved one and opens its report.</summary>
    public async Task LoadAsync(SavedBattleRow saved)
    {
        _savedId = saved.Id;
        _name    = saved.Name;
        this.RaisePropertyChanged(nameof(SavedId));
        this.RaisePropertyChanged(nameof(IsSaved));
        this.RaisePropertyChanged(nameof(Name));
        _watchLive = saved.Definition.WatchLive;
        this.RaisePropertyChanged(nameof(WatchLive));
        _restored = saved.Definition;

        var names = (await Task.Run(() => SystemIndexReport.LookUpAsync(_dbFactory, [.. saved.Definition.Windows.Select(w => w.SystemId).Distinct()])))
            .ToDictionary(s => s.SystemId);
        foreach (var w in saved.Definition.Windows)
            if (names.TryGetValue(w.SystemId, out var s))
                Systems.Add(NewRow(s.SystemId, s.Name, s.Region, FormatTime(w.From), w.To is { } to ? FormatTime(to) : ""));
        if (Systems.Count > 0) await RunAsync();
    }

    /// <summary>Fills this battle in from a fight found, and opens its report with the systems and
    /// times left showing, since those are only a suggestion to adjust.</summary>
    public async Task LoadSuggestedAsync(IReadOnlyList<BattleWindow> windows)
    {
        var names = (await Task.Run(() => SystemIndexReport.LookUpAsync(_dbFactory, [.. windows.Select(w => w.SystemId).Distinct()])))
            .ToDictionary(s => s.SystemId);
        foreach (var w in windows)
            if (names.TryGetValue(w.SystemId, out var s))
                Systems.Add(NewRow(s.SystemId, s.Name, s.Region, FormatTime(w.From), w.To is { } to ? FormatTime(to) : ""));
        if (Systems.Count == 0) return;
        FromText = FormatTime(windows.Min(w => w.From));
        await RunAsync();
        IsDefining = true;
    }

    /// <summary>The rows as windows; false with the reason if one cannot be read.</summary>
    private bool TryReadWindows(out List<BattleWindow>? windows, out string error)
    {
        windows = null;
        error = "";
        if (Systems.Count == 0) { error = BattleText.NeedSystem; return false; }

        var read = new List<BattleWindow>(Systems.Count);
        foreach (var row in Systems)
        {
            if (ParseTime(row.FromText) is not { } from) { error = string.Format(BattleText.BadFromTime, row.Name); return false; }
            DateTimeOffset? to = null;
            if (!string.IsNullOrWhiteSpace(row.ToText))
            {
                if (ParseTime(row.ToText) is not { } end) { error = string.Format(BattleText.BadToTime, row.Name); return false; }
                if (end <= from) { error = string.Format(BattleText.EndBeforeStart, row.Name); return false; }
                to = end;
            }
            read.Add(new BattleWindow(row.SystemId, from, to));
        }
        windows = read;
        return true;
    }

    /// <summary>The rows as windows, or null, having said what is wrong.</summary>
    private List<BattleWindow>? ReadWindows()
    {
        if (TryReadWindows(out var windows, out var error)) return windows;
        SetError(error);
        return null;
    }

    // ── Running a report ─────────────────────────────────────────────────────

    private readonly object _engineLock = new();
    private BattleReportEngine? _engine;
    private BattleDefinition? _restored;
    private CancellationTokenSource? _liveCts;
    private int _runId;

    private async Task RunAsync()
    {
        if (ReadWindows() is not { } windows) return;

        StopLive();
        var run = ++_runId;

        IsLoading = true;
        HasReport = false;
        StatusText = BattleText.Reading;
        IsError = false;
        ClearLists();

        var engine = await Task.Run(async () =>
        {
            var (chars, corps) = await _service.OursAsync().ConfigureAwait(false);
            return new BattleReportEngine(chars, corps);
        });

        // The arrangement carries over: from what was saved the first time, then from the report
        // being replaced, so changing a system's times does not undo the sides placed by hand.
        if (_engine is { } previous)
            lock (_engineLock) engine.Restore(previous.TeamCount, previous.Moves.ToDictionary(kv => kv.Key, kv => kv.Value));
        else if (_restored is { } saved)
            engine.Restore(saved.TeamCount, saved.Moves);

        var loaded = await Task.Run(() => _service.LoadNewAsync(windows, new HashSet<int>()));
        if (run != _runId) return;

        var snapshot = await Task.Run(() =>
        {
            lock (_engineLock)
            {
                engine.Add(loaded.Kills);
                return engine.Build();
            }
        });
        if (run != _runId) return;

        _engine  = engine;
        _tooMany = loaded.TooMany;

        // The stretches that can still gain kills. The rest of the battle is already written.
        var now = DateTimeOffset.UtcNow;
        _windows = windows;
        _watching = [.. windows.Where(w => w.IsOpenAt(now))];
        CanWatchLive = _watching.Count > 0;

        await ApplyAsync(snapshot, arrivedLive: false, engine.TeamCount);
        IsLoading = false;
        HasReport = true;
        IsDefining = false;

        if (WatchLive && _watching.Count > 0) StartLive(run);
        UpdateStatus();
        await KeepAsync();
    }

    /// <summary>How long a stretch stays watched past its end: a mail reaches the database some
    /// time after the kill it records, and a fight's last losses should not be the ones missed.</summary>
    private static readonly TimeSpan LateMailGrace = TimeSpan.FromMinutes(10);

    private List<BattleWindow> _watching = [];
    private List<BattleWindow> _windows = [];
    private bool _tooMany;

    /// <summary>A report is reading kills as they arrive.</summary>
    public bool IsLive => _liveCts is not null;

    private bool _watchLive = true;

    /// <summary>This battle's own switch for watching live, where any of it is still open. Turning
    /// it on catches up on whatever came in while it was off.</summary>
    public bool WatchLive
    {
        get => _watchLive;
        set
        {
            if (_watchLive == value) return;
            this.RaiseAndSetIfChanged(ref _watchLive, value);
            if (HasReport)
            {
                if (value) ResumeLive();
                else StopLive();
            }
            _ = KeepAsync();
        }
    }

    private bool _canWatchLive = true;

    /// <summary>Any of the report can still gain kills: an end left blank or yet to come.</summary>
    public bool CanWatchLive { get => _canWatchLive; private set => this.RaiseAndSetIfChanged(ref _canWatchLive, value); }

    private void ResumeLive()
    {
        var now = DateTimeOffset.UtcNow;
        _watching = [.. _windows.Where(w => w.IsOpenAt(now))];
        CanWatchLive = _watching.Count > 0;
        if (CanWatchLive && !IsLive) StartLive(_runId);
        UpdateStatus();
    }

    /// <summary>Ends this battle's reading, for good: its tab has been closed.</summary>
    public void Shut()
    {
        _runId++;
        StopLive();
    }

    private void StartLive(int run)
    {
        _liveCts = new CancellationTokenSource();
        var ct = _liveCts.Token;
        _ticker.Start();
        this.RaisePropertyChanged(nameof(IsLive));
        _ = Task.Run(() => LiveLoopAsync(run, ct), ct);
    }

    private void StopLive()
    {
        // Cancelled and let go, not disposed: the loop may be reading its token at this moment.
        _liveCts?.Cancel();
        _liveCts = null;
        _ticker.Stop();
        this.RaisePropertyChanged(nameof(IsLive));
        foreach (var kill in _fresh) kill.IsNew = false;
        _fresh.Clear();
        if (HasReport) UpdateStatus();
    }

    private async Task LiveLoopAsync(int run, CancellationToken ct)
    {
        var failed = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(LivePollInterval, ct).ConfigureAwait(false);
                if (run != _runId) return;

                var engine = _engine;
                if (engine is null) continue;

                // A stretch with an end drops out once that has passed; with none left, the report
                // has nothing more to wait for.
                var now = DateTimeOffset.UtcNow;
                var open = _watching.Where(w => w.To is not { } end || end + LateMailGrace > now).ToList();
                if (open.Count != _watching.Count)
                {
                    _watching = open;
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (run != _runId) return;
                        if (open.Count == 0) { StopLive(); CanWatchLive = false; }
                        else UpdateStatus();
                    });
                    if (open.Count == 0) return;
                }

                HashSet<int> known;
                lock (_engineLock) known = [.. engine.KnownKillIds];

                var (kills, tooMany) = await _service.LoadNewAsync(open, known, ct).ConfigureAwait(false);
                if (failed) { failed = false; Dispatcher.UIThread.Post(UpdateStatus); }
                if (kills.Count == 0) continue;

                BattleSnapshot snapshot;
                lock (_engineLock)
                {
                    engine.Add(kills);
                    snapshot = engine.Build();
                }

                var teamCount = engine.TeamCount;
                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    if (run != _runId || ct.IsCancellationRequested) return;
                    _tooMany = tooMany;
                    await ApplyAsync(snapshot, arrivedLive: true, teamCount);
                    UpdateStatus();
                });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                // Say so and keep trying: a dropped connection mid-fight should not end the watch.
                failed = true;
                Dispatcher.UIThread.Post(() => { LiveProblem = string.Format(BattleText.LiveReadFailed, ex.Message); UpdateStatus(); });
            }
        }
    }

    private void Fail(Exception ex)
    {
        IsLoading = false;
        SetError(string.Format(BattleText.ReadFailed, ex.Message));
    }

    private void SetError(string text)
    {
        IsError = true;
        StatusText = text;
    }

    // ── Moving sides ─────────────────────────────────────────────────────────

    private async void MoveSide(long side, int team)
    {
        try
        {
            var engine = _engine;
            if (engine is null) return;

            var snapshot = await Task.Run(() =>
            {
                lock (_engineLock)
                {
                    engine.Move(side, team);
                    return engine.Build();
                }
            });
            await ApplyAsync(snapshot, arrivedLive: false, engine.TeamCount);
            await KeepAsync();
        }
        catch (Exception ex) { Fail(ex); }
    }

    private async void RemoveTeam(int team)
    {
        try
        {
            var engine = _engine;
            if (engine is null) return;

            var snapshot = await Task.Run(() =>
            {
                lock (_engineLock)
                {
                    // Looked at again here: a kill may have put a side on it since the button showed.
                    engine.RemoveTeam(team);
                    return engine.Build();
                }
            });
            await ApplyAsync(snapshot, arrivedLive: false, engine.TeamCount);
            await KeepAsync();
        }
        catch (Exception ex) { Fail(ex); }
    }

    private async Task AddTeamAsync()
    {
        var engine = _engine;
        if (engine is null) return;

        var snapshot = await Task.Run(() =>
        {
            lock (_engineLock)
            {
                engine.AddTeam();
                return engine.Build();
            }
        });
        await ApplyAsync(snapshot, arrivedLive: false, engine.TeamCount);
        await KeepAsync();
    }

    // ── What is on the screen ────────────────────────────────────────────────

    public BulkObservableCollection<BattleTeamVm> Teams { get; } = [];
    public BulkObservableCollection<BattleKillVm> Kills { get; } = [];

    private readonly Dictionary<int, BattleTeamVm> _teamVms = [];
    private readonly Dictionary<long, BattleSideVm> _sideVms = [];
    private readonly Dictionary<(long, long), BattleSideVm> _corpVms = [];
    private readonly Dictionary<(int, long, long, int), BattlePilotVm> _pilotVms = [];
    private readonly Dictionary<(int, long, int), BattlePilotVm> _hullVms = [];
    private readonly Dictionary<(int, int), BattleShipVm> _shipVms = [];
    private readonly Dictionary<(int, int, long, long), BattleShipVm> _flyerVms = [];
    private readonly Dictionary<int, BattleKillVm> _killVms = [];
    private readonly Dictionary<int, BattleShipType> _shipTypes = [];
    private readonly Dictionary<int, string> _systemNames = [];
    private readonly BattleNames _names = new();
    private BattleSnapshot? _snapshot;
    private long _appliedVersion;

    private bool _isLoading, _hasReport, _isError;
    private string _statusText = BattleText.StartHint, _liveProblem = "";
    public bool IsLoading { get => _isLoading; private set => this.RaiseAndSetIfChanged(ref _isLoading, value); }
    public bool HasReport { get => _hasReport; private set => this.RaiseAndSetIfChanged(ref _hasReport, value); }
    public bool IsError   { get => _isError;   private set => this.RaiseAndSetIfChanged(ref _isError,   value); }
    public string StatusText { get => _statusText; private set => this.RaiseAndSetIfChanged(ref _statusText, value); }
    private string LiveProblem { get => _liveProblem; set => _liveProblem = value; }

    private string _lastKillText = "";
    public string LastKillText { get => _lastKillText; private set => this.RaiseAndSetIfChanged(ref _lastKillText, value); }

    /// <summary>Where the database's kills come from, when that limits what this report can show.</summary>
    public string CoverageNote => !_zkb.Enabled ? BattleText.CoverageOff
                                : _zkb.Scope == ZkbScope.MineAndCorp ? BattleText.CoverageMine
                                : "";
    public bool HasCoverageNote => CoverageNote.Length > 0;

    private void ClearLists()
    {
        Teams.ResetTo([]);
        Kills.ResetTo([]);
        _teamVms.Clear();
        _sideVms.Clear();
        _corpVms.Clear();
        _pilotVms.Clear();
        _hullVms.Clear();
        _shipVms.Clear();
        _flyerVms.Clear();
        _killVms.Clear();
        _snapshot = null;
        _appliedVersion = 0;
        _fresh.Clear();
        LiveProblem = "";
        LastKillText = "";
        ChartSeries = [];
        HasChart = false;
    }

    /// <summary>Brings the screen to this snapshot. Rows stay the same objects from one to the next,
    /// so a row that only changed its numbers is not rebuilt, and a list is re-laid only when its
    /// rows or their order actually changed.</summary>
    private async Task ApplyAsync(BattleSnapshot snapshot, bool arrivedLive, int teamCount)
    {
        await EnsureLookupsAsync(snapshot);

        // ⚠️ The live loop and a side moved by hand both build snapshots. Whichever was built last
        // is the true one, whichever reaches here first: an older one would put the side back.
        if (snapshot.Version < _appliedVersion) return;
        _appliedVersion = snapshot.Version;
        _snapshot = snapshot;

        var teams = new List<BattleTeamVm>(teamCount);
        foreach (var stats in snapshot.Teams)
        {
            if (!_teamVms.TryGetValue(stats.Team, out var vm)) _teamVms[stats.Team] = vm = new BattleTeamVm(stats.Team, RemoveTeam);
            vm.Update(stats);
            teams.Add(vm);
        }
        if (!teams.SequenceEqual(Teams)) Teams.ResetTo(teams);
        this.RaisePropertyChanged(nameof(TeamColumns));

        foreach (var group in GroupByTeam(teams, snapshot.Sides, s => s.Team))
        {
            var rows = group.Rows.Select(r =>
            {
                if (!_sideVms.TryGetValue(r.SideId, out var vm))
                    _sideVms[r.SideId] = vm = new BattleSideVm(r, _names, MoveSide);
                vm.Update(r, teamCount);
                vm.SetChildren(snapshot.SideCorps.TryGetValue(r.SideId, out var corps)
                    ? [.. corps.Select(c =>
                    {
                        if (!_corpVms.TryGetValue((r.SideId, c.SideId), out var child))
                            _corpVms[(r.SideId, c.SideId)] = child = new BattleSideVm(c, _names, MoveSide, vm);
                        child.Update(c, teamCount);
                        return child;
                    })]
                    : []);
                return vm;
            }).ToList();
            group.Team.SetSides(rows);
            group.Team.CanRemove = teamCount > BattleReportEngine.AutomaticTeams && rows.Count == 0;
        }

        foreach (var group in GroupByTeam(teams, snapshot.PilotFamilies, f => f.Total.Team))
        {
            var rows = group.Rows.Select(family =>
            {
                var r = family.Total;
                var key = (r.Team, r.CharacterId, r.CharacterId > 0 ? 0L : r.CorporationId, r.CharacterId > 0 ? 0 : r.ShipTypeId);
                if (!_pilotVms.TryGetValue(key, out var vm))
                    _pilotVms[key] = vm = new BattlePilotVm(r, _names, ShipName, LossesOf);
                vm.Update(r);
                vm.SetChildren(family.Hulls.Count < 2 ? [] : [.. family.Hulls.Select(h =>
                {
                    if (!_hullVms.TryGetValue((r.Team, r.CharacterId, h.ShipTypeId), out var hull))
                        _hullVms[(r.Team, r.CharacterId, h.ShipTypeId)] = hull = new BattlePilotVm(h, _names, ShipName, LossesOf, vm);
                    hull.Update(h);
                    return hull;
                })]);
                return vm;
            }).ToList();
            group.Team.SetPilots(rows);
        }

        foreach (var group in GroupByTeam(teams, snapshot.Ships, s => s.Team))
        {
            var rows = group.Rows.Select(r =>
            {
                if (!_shipVms.TryGetValue((r.Team, r.ShipTypeId), out var vm))
                    _shipVms[(r.Team, r.ShipTypeId)] = vm = new BattleShipVm(r, ShipType, _names);
                vm.Update(r.Pilots, r.Lost, r.IskLost, r.Damage);

                BattleShipVm Flyer(BattleShipFlyer flyer, BattleShipVm parent, long underId)
                {
                    var key = (r.Team, r.ShipTypeId, underId, flyer.Id);
                    if (!_flyerVms.TryGetValue(key, out var row)) _flyerVms[key] = row = new BattleShipVm(flyer, parent);
                    row.Update(flyer.Pilots, flyer.Lost, flyer.IskLost, flyer.Damage);
                    row.SetChildren([.. flyer.Corps.Select(c => Flyer(c, row, flyer.Id))]);
                    return row;
                }
                vm.SetChildren(snapshot.ShipFlyers.TryGetValue((r.Team, r.ShipTypeId), out var flyers)
                    ? [.. flyers.Select(f => Flyer(f, vm, 0))] : []);
                return vm;
            }).ToList();
            group.Team.SetShips(rows);
        }

        _teamList.Clear();
        _teamList.AddRange(teams);
        var kills = snapshot.Kills.Select(r =>
        {
            if (!_killVms.TryGetValue(r.KillMailId, out var vm))
            {
                _killVms[r.KillMailId] = vm = new BattleKillVm(r, _names, ShipName, SystemName, _teamList, arrivedLive);
                if (arrivedLive) _fresh.Add(vm);
            }
            vm.Update(r);
            return vm;
        }).ToList();
        Sync(Kills, kills);

        var bySystem = snapshot.Kills.GroupBy(k => k.SystemId).ToDictionary(g => g.Key, g => g.Count());
        foreach (var row in Systems) row.SetKills(bySystem.GetValueOrDefault(row.SystemId));

        BuildChart(snapshot, teams);
        _ = ResolveNamesAsync(snapshot);
    }

    private readonly List<BattleTeamVm> _teamList = [];

    /// <summary>The kills a pilot's line lost: that pilot in that hull, or for a structure, its
    /// owner's of that type. Looked up when asked, not kept on every row.</summary>
    private IReadOnlyList<BattleKillRow> LossesOf(BattlePilotRow row, bool anyHull) =>
        _snapshot is not { } snapshot ? []
        : [.. snapshot.Kills
            .Where(k => k.Team == row.Team
                     && (row.CharacterId > 0
                            ? k.VictimCharId == row.CharacterId && (anyHull || k.ShipTypeId == row.ShipTypeId)
                            : k.VictimCharId <= 0 && k.VictimCorpId == row.CorporationId && k.ShipTypeId == row.ShipTypeId))
            .OrderBy(k => k.Time)];

    public int TeamColumns => Math.Max(BattleReportEngine.AutomaticTeams, Teams.Count);

    private static IEnumerable<(BattleTeamVm Team, List<T> Rows)> GroupByTeam<T>(
        List<BattleTeamVm> teams, IEnumerable<T> rows, Func<T, int> team)
    {
        var byTeam = rows.ToLookup(team);
        foreach (var t in teams) yield return (t, byTeam[t.Team].ToList());
    }

    private static void Sync<T>(BulkObservableCollection<T> list, List<T> rows)
    {
        if (!list.SequenceEqual(rows)) list.ResetTo(rows);
    }

    private string ShipName(int typeId) =>
        typeId <= 0 ? ""
        : _shipTypes.TryGetValue(typeId, out var t) ? SdeNames.Type(t.TypeId, t.Name)
        : typeId.ToString(CultureInfo.InvariantCulture);

    private BattleShipType? ShipType(int typeId) => _shipTypes.GetValueOrDefault(typeId);

    private string SystemName(int systemId) => _systemNames.GetValueOrDefault(systemId, "");

    private async Task EnsureLookupsAsync(BattleSnapshot snapshot)
    {
        var missingTypes = snapshot.Pilots.Select(p => p.ShipTypeId)
            .Concat(snapshot.Kills.Select(k => k.ShipTypeId))
            .Where(id => id > 0 && !_shipTypes.ContainsKey(id)).Distinct().ToList();
        if (missingTypes.Count > 0)
            foreach (var (id, type) in await Task.Run(() => _service.ShipTypesAsync(missingTypes)))
                _shipTypes[id] = type;

        var missingSystems = snapshot.Kills.Select(k => k.SystemId).Where(id => !_systemNames.ContainsKey(id)).Distinct().ToList();
        if (missingSystems.Count > 0)
            foreach (var s in await Task.Run(() => SystemIndexReport.LookUpAsync(_dbFactory, missingSystems)))
                _systemNames[s.SystemId] = s.Name;
    }

    private bool _resolving;

    /// <summary>Fills in the names not known yet, then asks the rows to show them. Off the screen's
    /// way: a large fight's pilots can take several calls, and the numbers do not wait for them.</summary>
    private async Task ResolveNamesAsync(BattleSnapshot snapshot)
    {
        if (_resolving) return;

        var ids = snapshot.Pilots.SelectMany(p => new[] { p.CharacterId, p.CorporationId, p.AllianceId })
            .Concat(snapshot.Sides.Select(s => s.SideId))
            .Concat(snapshot.SideCorps.Values.SelectMany(c => c).Select(c => c.SideId))
            .Concat(snapshot.ShipFlyers.Values.SelectMany(f => f).SelectMany(f => f.Corps.Select(c => c.Id).Append(f.Id)))
            .Concat(snapshot.Kills.SelectMany(k => new[] { k.VictimCharId, k.VictimCorpId, k.VictimAllianceId, k.FinalBlowCharId, k.FinalBlowCorpId, k.FinalBlowAllianceId }))
            .Where(id => id > 0 && !_names.Has(id)).Distinct().ToList();
        if (ids.Count == 0) return;

        _resolving = true;
        try
        {
            var found = await Task.Run(() => _nameLookup(ids));
            _names.Add(found);
            foreach (var s in _sideVms.Values)  s.NamesChanged();
            foreach (var c in _corpVms.Values)  c.NamesChanged();
            foreach (var p in _pilotVms.Values) p.NamesChanged();
            foreach (var h in _hullVms.Values)  h.NamesChanged();
            foreach (var s in _shipVms.Values)  s.NamesChanged();
            foreach (var f in _flyerVms.Values) f.NamesChanged();
            foreach (var k in _killVms.Values)  k.NamesChanged();
        }
        catch (Exception ex)
        {
            LiveProblem = string.Format(BattleText.NamesFailed, ex.Message);
            UpdateStatus();
        }
        finally { _resolving = false; }
    }

    private void UpdateStatus()
    {
        if (_snapshot is not { } s) return;
        IsError = false;
        var text = string.Format(BattleText.Summary, s.Kills.Count, s.TotalPilots, BattleFormat.Isk(s.TotalIsk));
        if (s.FirstKill is { } first && s.LastKill is { } last)
            text += "  ·  " + string.Format(BattleText.SummaryTimes,
                first.UtcDateTime.ToString("HH:mm", CultureInfo.InvariantCulture),
                last.UtcDateTime.ToString("HH:mm", CultureInfo.InvariantCulture));
        if (s.Kills.Count == 0) text = BattleText.NoKills;
        if (IsLive) text += "  ·  " + string.Format(BattleText.LiveWatching, _watching.Count, _windows.Count);
        if (_tooMany) text += "  ·  " + string.Format(BattleText.TooMany, BattleReportService.MaxKills);
        if (LiveProblem.Length > 0) text += "  ·  " + LiveProblem;
        StatusText = text;
        Tick();
    }

    private void Tick()
    {
        if (_snapshot?.LastKill is { } last && IsLive)
        {
            var ago = DateTimeOffset.UtcNow - last;
            LastKillText = string.Format(BattleText.LastKillAgo, ago.TotalHours >= 1
                ? $"{(int)ago.TotalHours}:{ago.Minutes:00}:{ago.Seconds:00}"
                : $"{(int)ago.TotalMinutes}:{ago.Seconds:00}");
        }
        else LastKillText = "";

        var now = DateTimeOffset.UtcNow;
        _fresh.RemoveAll(kill =>
        {
            if (kill.ArrivedAt is { } at && now - at <= NewMarkerFor) return false;
            kill.IsNew = false;
            return true;
        });
    }

    /// <summary>The kills still marked as just arrived.</summary>
    private readonly List<BattleKillVm> _fresh = [];

    // ── The chart: each team's losses adding up over the fight ───────────────

    private ISeries[] _chartSeries = [];
    public ISeries[] ChartSeries { get => _chartSeries; private set => this.RaiseAndSetIfChanged(ref _chartSeries, value); }

    private Axis[] _xAxes = [new Axis()];
    public Axis[] XAxes { get => _xAxes; private set => this.RaiseAndSetIfChanged(ref _xAxes, value); }

    private Axis[] _yAxes = [new Axis()];
    public Axis[] YAxes { get => _yAxes; private set => this.RaiseAndSetIfChanged(ref _yAxes, value); }

    private bool _hasChart;
    public bool HasChart { get => _hasChart; private set => this.RaiseAndSetIfChanged(ref _hasChart, value); }

    private void BuildChart(BattleSnapshot snapshot, List<BattleTeamVm> teams)
    {
        if (snapshot.Kills.Count == 0 || snapshot.FirstKill is not { } start)
        {
            HasChart = false;
            ChartSeries = [];
            return;
        }

        var inOrder = snapshot.Kills.OrderBy(k => k.Time).ToList();
        ChartSeries =
        [
            .. teams.Select(team =>
            {
                var total = 0.0;
                var points = new List<ObservablePoint> { new(0, 0) };
                foreach (var k in inOrder.Where(k => k.Team == team.Team))
                {
                    total += k.Value;
                    points.Add(new ObservablePoint((k.Time - start).TotalMinutes, total));
                }
                return (ISeries)new StepLineSeries<ObservablePoint>
                {
                    Name = team.Title, Values = points, GeometrySize = 0, Fill = null,
                    Stroke = ChartPaint.Series(team.Team),
                    AnimationsSpeed = TimeSpan.Zero,
                };
            }),
        ];
        XAxes =
        [
            new Axis
            {
                Labeler = v => start.AddMinutes(v).UtcDateTime.ToString("HH:mm", CultureInfo.InvariantCulture),
                LabelsPaint = ChartPaint.Labels, TextSize = 11, SeparatorsPaint = null, MinLimit = 0,
            },
        ];
        YAxes =
        [
            new Axis
            {
                Labeler = v => BattleFormat.Isk(v), LabelsPaint = ChartPaint.Labels, TextSize = 11,
                SeparatorsPaint = ChartPaint.Separators, MinLimit = 0,
            },
        ];
        HasChart = true;
    }
}
