using System.Globalization;
using System.Reactive;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using EveConsole.Localization;
using EveConsole.Monitoring;
using EveConsole.Services;
using EveConsole.Services.Intel;
using ReactiveUI;

namespace EveConsole.ViewModels;

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
/// The standings tiers' colours. Fixed, not themed: they carry the meaning players know from the
/// game, and are drawn as a stripe or a dot rather than as text, so they read on any theme.
/// </summary>
public static class IntelTierColors
{
    public static readonly IBrush Red       = new ImmutableSolidColorBrush(Color.FromRgb(0xD3, 0x2F, 0x2F));
    public static readonly IBrush Orange    = new ImmutableSolidColorBrush(Color.FromRgb(0xF2, 0x8C, 0x28));
    public static readonly IBrush Neutral   = new ImmutableSolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xA6));
    public static readonly IBrush LightBlue = new ImmutableSolidColorBrush(Color.FromRgb(0x6C, 0xB4, 0xF5));
    public static readonly IBrush Blue      = new ImmutableSolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xE0));
    public static readonly IBrush Own       = new ImmutableSolidColorBrush(Color.FromRgb(0x8E, 0x7C, 0xC3));

    public static IBrush Of(StandingTier tier) => tier switch
    {
        StandingTier.Red       => Red,
        StandingTier.Orange    => Orange,
        StandingTier.LightBlue => LightBlue,
        StandingTier.Blue      => Blue,
        StandingTier.Own       => Own,
        _                      => Neutral,
    };

    /// <summary>The tier's colour as a value, for drawing code (the Universe Map's marks).</summary>
    public static Color Colour(StandingTier tier) => ((ISolidColorBrush)Of(tier)).Color;
}

/// <summary>How far away, in the bands every intel view colours jumps by. Display only.</summary>
public enum IntelJumpBand { Unknown, Here, Near, Mid, Normal, Far }

/// <summary>The words every intel view shares: ages, jumps, and a row's band.</summary>
public static class IntelWords
{
    /// <summary>0 here, 1–2 near, 3–5 mid, 6–10 normal, past 10 far.</summary>
    public static IntelJumpBand Band(int? jumps) => jumps switch
    {
        null   => IntelJumpBand.Unknown,
        0      => IntelJumpBand.Here,
        <= 2   => IntelJumpBand.Near,
        <= 5   => IntelJumpBand.Mid,
        <= 10  => IntelJumpBand.Normal,
        _      => IntelJumpBand.Far,
    };

    /// <summary>"now", "45 s", "3 min", "1 h 5 min".</summary>
    public static string Age(DateTimeOffset at, DateTimeOffset now)
    {
        var seconds = (int)Math.Max(0, (now - at).TotalSeconds);
        var culture = CultureInfo.CurrentCulture;
        return seconds < 10   ? IntelText.AgeNow
             : seconds < 60   ? string.Format(culture, IntelText.AgeSeconds, seconds)
             : seconds < 3600 ? string.Format(culture, IntelText.AgeMinutes, seconds / 60)
             : string.Format(culture, IntelText.AgeHours, seconds / 3600, seconds / 60 % 60);
    }

    /// <summary>The age as a compact badge: "0:53" and "4:10" under ten minutes, then "12m", then "2h".</summary>
    public static string CompactAge(DateTimeOffset at, DateTimeOffset now)
    {
        var seconds = (int)Math.Max(0, (now - at).TotalSeconds);
        var culture = CultureInfo.CurrentCulture;
        return seconds < 600  ? (seconds / 60).ToString(culture) + ":" + (seconds % 60).ToString("00", culture)
             : seconds < 3600 ? string.Format(culture, IntelText.AgeCompactMinutes, seconds / 60)
             : string.Format(culture, IntelText.AgeCompactHours, seconds / 3600);
    }

    /// <summary>A time in EVE time (UTC), as the age badges' tooltips give it.</summary>
    public static string EveTime(DateTimeOffset at) => at.UtcDateTime.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>An age that may be days: as <see cref="Age"/> within the hour, then "5 h", then "3 d".</summary>
    public static string LongAge(DateTimeOffset at, DateTimeOffset now)
    {
        var span = now - at;
        var culture = CultureInfo.CurrentCulture;
        return span < TimeSpan.FromHours(1) ? Age(at, now)
             : span < TimeSpan.FromHours(48) ? string.Format(culture, IntelText.AgeHoursOnly, (int)span.TotalHours)
             : string.Format(culture, IntelText.AgeDays, (int)span.TotalDays);
    }

    /// <summary>The jumps as the badge shows them: a number, "…" while the walk is not ready, or
    /// nothing when there is nowhere to count from.</summary>
    public static string Jumps(int? jumps, bool pending) =>
        jumps is int j ? j.ToString(CultureInfo.CurrentCulture) : pending ? "…" : "";

    /// <summary>"3 jumps from Pilot A", and "through Thera or Turnur" when the shortest way uses a hole.</summary>
    public static string? JumpsTip(int? jumps, bool viaWormhole, string? from)
    {
        if (jumps is not int j || from is null) return null;
        var culture = CultureInfo.CurrentCulture;
        return viaWormhole ? string.Format(culture, IntelText.JumpsFromViaHole, j, from)
                           : string.Format(culture, IntelText.JumpsFrom, j, from);
    }

    /// <summary>Whose standings gave the colours, for the stripe's tooltip.</summary>
    public static string StandingsTip(IntelStandings standings, long? viewerId) =>
        viewerId is long v && standings.Own(v) is { } own
            ? string.Format(CultureInfo.CurrentCulture, IntelText.StandingsAs, own.Name)
            : IntelText.StandingsAnyCharacter;

    /// <summary>A standings tier as the game names it.</summary>
    public static string TierName(StandingTier tier) => tier switch
    {
        StandingTier.Red       => IntelText.TierRed,
        StandingTier.Orange    => IntelText.TierOrange,
        StandingTier.LightBlue => IntelText.TierLightBlue,
        StandingTier.Blue      => IntelText.TierBlue,
        StandingTier.Own       => IntelText.TierOwn,
        _                      => IntelText.TierNeutral,
    };

    /// <summary>The flags as words, most pressing first, and a camp's own.</summary>
    public static string Flags(int flags, GateCamp? camp = null)
    {
        var words = IntelDisplay.Flags(flags).ToList();
        if (camp is not null)
        {
            if (camp.Smartbomb) words.Add(IntelText.CampSmartbombs);
            if (camp.Hic) words.Add(IntelText.CampHic);
            else if (camp.Interdictor) words.Add(IntelText.CampDictor);
            if (camp.Bubble && !words.Contains(MapText.IntelFlagBubbles)) words.Add(MapText.IntelFlagBubbles);
        }
        return string.Join(", ", words.Distinct());
    }
}

/// <summary>What a row's links and menu do beyond opening a page, set by whoever shows the row.</summary>
public sealed class IntelRowActions
{
    /// <summary>Frames the system on the map.</summary>
    public Action<int>? ShowOnMap { get; init; }

    /// <summary>Opens the Intel tool on the system; null while there is no tool to open.</summary>
    public Action<int>? OpenInTool { get; init; }

    /// <summary>After a link opened a page, as from a panel in front of the window it opened in.</summary>
    public Action? AfterNavigate { get; init; }

    /// <summary>The watch list, for "Always alert" and "Ignore" on a pilot; null where it cannot be changed.</summary>
    public IntelWatchList? Watch { get; init; }

    public static readonly IntelRowActions None = new();
}

/// <summary>
/// One thing on a row's second line: a pilot with their portrait, a hull with its icon, or words.
/// A picture is fetched from the shared image cache and never waited for.
/// </summary>
public sealed class IntelPictureVm : ReactiveObject
{
    public enum Kinds { Pilot, Ship, Words, Corporation, Alliance }

    private IntelPictureVm(Kinds kind, long id, string text, string? tip, string? name = null, string count = "")
    {
        Kind = kind;
        Id = id;
        Text = text;
        Tip = tip;
        Name = name ?? text;
        CountText = count;
    }

    public static IntelPictureVm Pilot(long characterId, string name) =>
        new(Kinds.Pilot, characterId, name, IntelText.TipOpenPilot);

    /// <summary>A pilot with what the watch list says of them: bold for Always, dimmed for Ignore.</summary>
    public static IntelPictureVm Pilot(long characterId, string name, IntelWatchMark watch)
    {
        var p = Pilot(characterId, name);
        p.Watch = watch;
        return p;
    }

    public static IntelPictureVm Corporation(long corporationId, string name) =>
        new(Kinds.Corporation, corporationId, name, IntelText.TipOpenCorporation);

    public static IntelPictureVm Alliance(long allianceId, string name) =>
        new(Kinds.Alliance, allianceId, name, IntelText.TipOpenAlliance);

    /// <summary>What the watch list says of a pilot shown: bold for Always, dimmed for Ignore.</summary>
    public IntelWatchMark Watch { get; private set; }
    public bool IsWatchAlways  => Watch == IntelWatchMark.Always;
    public bool IsWatchIgnored => Watch == IntelWatchMark.Ignored;
    public FontWeight TextWeight => IsWatchAlways ? FontWeight.Bold : FontWeight.Normal;
    public double TextOpacity => IsWatchIgnored ? IntelEventRowVm.IgnoredOpacity : 1.0;

    /// <summary>
    /// A pilot with their alliance's logo (or their corporation's, with no alliance) small on their portrait: who
    /// they fly for, at a glance. The group's name is in the portrait's tooltip, under the pilot's.
    /// </summary>
    public static IntelPictureVm Pilot(long characterId, string name, IntelWatchMark watch, IntelPictureVm? badge)
    {
        var p = Pilot(characterId, name, watch);
        p.Badge = badge;
        p.PictureTip = badge is null ? name : name + "\n" + badge.Text;
        return p;
    }

    /// <summary>
    /// A group by its logo and how many fly for it ("×5"): its name only in the tooltip, as a line shows several of
    /// them side by side. <paramref name="tip"/> replaces the tooltip (the first group carries the whole breakout).
    /// </summary>
    public static IntelPictureVm Group(IntelWhoKind kind, long id, string name, int count, string? tip = null)
    {
        var times = "×" + count.ToString(CultureInfo.CurrentCulture);
        var p = new IntelPictureVm(kind == IntelWhoKind.Alliance ? Kinds.Alliance : Kinds.Corporation, id, name,
                                   kind == IntelWhoKind.Alliance ? IntelText.TipOpenAlliance : IntelText.TipOpenCorporation,
                                   name, times)
        {
            IconOnly = true,
        };
        p.PictureTip = tip ?? name + " · " + count.ToString(CultureInfo.CurrentCulture);
        return p;
    }

    public static IntelPictureVm Ship(int typeId, string hull, int count = 1) =>
        new(Kinds.Ship, typeId,
            count > 1 ? string.Format(CultureInfo.CurrentCulture, IntelText.HullTimes, hull, count) : hull,
            typeId > 0 ? IntelText.TipOpenHull : null,
            hull, count > 1 ? "×" + count.ToString(CultureInfo.CurrentCulture) : "");

    /// <summary>A hull said by its English name with no type id beside it (a report's "8× Muninn", a
    /// pasted d-scan's ships as stored): its type found by name, so it shows its icon and its name in
    /// the interface language. A name that is no hull (a class, or words) stays words, with no icon.</summary>
    public static IntelPictureVm SaidHull(IntelSnapshot snapshot, string english, int count = 1)
    {
        var id = snapshot.HullId(english);
        return Ship(id, id > 0 ? SdeNames.Type(id, english) : english, count);
    }

    public static IntelPictureVm Words(string text) => new(Kinds.Words, 0, text, null);

    public Kinds  Kind { get; }
    public long   Id   { get; }
    /// <summary>What it says as one piece: "Hull C ×8", a pilot's or a group's name, or the words.</summary>
    public string Text { get; }
    public string? Tip { get; }

    /// <summary>The name alone, beside the picture: a hull's without its count.</summary>
    public string Name { get; }

    /// <summary>"×8" after a hull flown by several, "×5" after a group's logo; empty otherwise.</summary>
    public string CountText { get; }
    public bool HasCount => CountText.Length > 0;

    /// <summary>A group shown by its logo and count alone: its name is in the tooltip.</summary>
    public bool IconOnly { get; private init; }

    /// <summary>A pilot's alliance (or corporation) logo, small, on their portrait.</summary>
    public IntelPictureVm? Badge { get; private set; }
    public bool HasBadge => Badge is not null;

    private string? _pictureTip;
    /// <summary>The picture's tooltip: the name, a pilot's group under it, a group's count.</summary>
    public string PictureTip { get => _pictureTip ?? Text; private set => _pictureTip = value; }

    /// <summary>The picture and its badge, for whoever loads or gives pictures.</summary>
    public IEnumerable<IntelPictureVm> WithBadge => Badge is null ? [this] : [this, Badge];

    public bool IsPilot => Kind == Kinds.Pilot;
    public bool IsShip  => Kind == Kinds.Ship;
    public bool IsWords => Kind == Kinds.Words;

    /// <summary>A hull's icon or a corporation's or alliance's logo, drawn square; a portrait is round.</summary>
    public bool IsSquare => Kind is Kinds.Ship or Kinds.Corporation or Kinds.Alliance;

    /// <summary>A pilot, a corporation, an alliance, or a hull with a known type: each opens its page.</summary>
    public bool IsLink => Kind != Kinds.Words && Id > 0;

    /// <summary>Room for a portrait or an icon: only when there is one to fetch. A hull named with no
    /// type to show (a class said in chat, never recognised as a hull) is its words alone, never an
    /// empty square.</summary>
    public bool HasPictureSlot => Kind != Kinds.Words && Id > 0;

    private Bitmap? _picture;
    public Bitmap? Picture { get => _picture; private set => this.RaiseAndSetIfChanged(ref _picture, value); }

    public string? PictureUrl => Kind switch
    {
        Kinds.Pilot       when Id > 0 => $"https://images.evetech.net/characters/{Id}/portrait?size=32",
        Kinds.Ship        when Id > 0 => $"https://images.evetech.net/types/{Id}/icon?size=32",
        Kinds.Corporation when Id > 0 => $"https://images.evetech.net/corporations/{Id}/logo?size=32",
        Kinds.Alliance    when Id > 0 => $"https://images.evetech.net/alliances/{Id}/logo?size=32",
        _ => null,
    };

    public async Task LoadAsync()
    {
        if (Badge is { } badge) _ = badge.LoadAsync();
        if (PictureUrl is not { } url || Picture is not null) return;
        var bitmap = await EveImageCache.GetAsync(url).ConfigureAwait(false);
        if (bitmap is not null) Dispatcher.UIThread.Post(() => Picture = bitmap);
    }

    /// <summary>For a check with no network: the picture given rather than fetched.</summary>
    public void Show(Bitmap? picture) => Picture = picture;

    public void Open()
    {
        switch (Kind)
        {
            case Kinds.Pilot:       EntityNavigator.Instance.Entity(EntityKind.Pilot, Id); break;
            case Kinds.Ship:        EntityNavigator.Instance.Item((int)Id); break;
            case Kinds.Corporation: EntityNavigator.Instance.Entity(EntityKind.PlayerCorp, Id); break;
            case Kinds.Alliance:    EntityNavigator.Instance.Entity(EntityKind.Alliance, Id); break;
        }
    }
}

/// <summary>A piece that a line of intel tokens (<see cref="EveConsole.Views.IntelFitPanel"/>) may leave out when narrow: 0 never,
/// 1 first, then 2, and so on.</summary>
public interface IIntelDroppable
{
    int Drop { get; }
}

/// <summary>
/// One piece of an intel line as drawn: a picture (a portrait with its group's logo small on it, a logo, a hull's
/// icon), the name beside it, or a count. A name is its own piece so that a narrow line leaves the names out and
/// keeps the pictures and counts: hulls' names go first, then groups', then pilots'. Each name stays in its picture's
/// tooltip. Words ("lost to", "+2") are never left out.
/// </summary>
public sealed class IntelTokenVm : IIntelDroppable
{
    public enum Parts { Picture, Name, Count }

    private IntelTokenVm(IntelPictureVm source, Parts part, int drop)
    {
        Source = source;
        Part = part;
        Drop = drop;
    }

    public IntelPictureVm Source { get; }
    public Parts Part { get; }
    public int Drop { get; }

    public bool IsPicture => Part == Parts.Picture;
    public bool IsName    => Part == Parts.Name;
    public bool IsCount   => Part == Parts.Count;

    /// <summary>The name piece's words: the bare name beside a picture, or the whole words with none.</summary>
    public string Label => Source.HasPictureSlot ? Source.Name : Source.Text;

    /// <summary>The pieces a picture is drawn as, in order: picture, name, count.</summary>
    public static IEnumerable<IntelTokenVm> Of(IntelPictureVm p)
    {
        if (!p.HasPictureSlot)
        {
            yield return new IntelTokenVm(p, Parts.Name, 0);
            yield break;
        }
        yield return new IntelTokenVm(p, Parts.Picture, 0);
        if (!p.IconOnly)
            yield return new IntelTokenVm(p, Parts.Name, p.Kind switch
            {
                IntelPictureVm.Kinds.Ship => 1,
                IntelPictureVm.Kinds.Corporation or IntelPictureVm.Kinds.Alliance => 2,
                _ => 3,
            });
        if (p.HasCount) yield return new IntelTokenVm(p, Parts.Count, 0);
    }
}

/// <summary>
/// One feed line in an intel view: a report, a clear, a paste, a kill or a camp forming, with its
/// distance, its standings colour, who and what, and its age. Shared by the panel, its popup and
/// the Intel tool, so a report reads the same in all of them.
///
/// <para>Age, fade and the fresh highlight move every second (<see cref="Tick"/>) without the row
/// being built again; everything else is fixed for the snapshot it came from.</para>
/// </summary>
public sealed class IntelEventRowVm : ReactiveObject
{
    /// <summary>The pictures a row shows before "+N".</summary>
    public const int MaxPictures = 4;

    private readonly DateTimeOffset _at, _newest;
    private readonly TimeSpan _lifetime;
    private readonly int? _jumps;
    private readonly bool _canBeFresh;

    public IntelEventRowVm(IntelFeedRow row, IntelSnapshot snapshot, DateTimeOffset now, bool pending,
                           IntelRowActions? actions = null)
    {
        var e = row.Event;
        var culture = CultureInfo.CurrentCulture;
        Actions = actions ?? IntelRowActions.None;
        Key = e.Key;
        SystemId = e.SystemId;
        SystemName = snapshot.SystemName(e.SystemId);
        _at = e.At;
        var camp = e.CampKey is { } key ? snapshot.Camps.FirstOrDefault(c => c.Key == key) : null;
        // A camp is as new as its last kill; a report as its last follow-up.
        _newest = row.FollowUps.Count > 0 ? row.FollowUps.Max(f => f.Event.At) : e.At;
        if (e.Kind == IntelEventKind.CampFormed && camp is not null && camp.LastKill > _newest) _newest = camp.LastKill;
        if (_newest < e.At) _newest = e.At;
        _lifetime = e.Lifetime;
        _jumps = row.Jumps;
        _canBeFresh = e.Kind != IntelEventKind.Clear;

        JumpsText = IntelWords.Jumps(row.Jumps, pending);
        Band = row.Jumps is null && pending ? IntelJumpBand.Unknown : IntelWords.Band(row.Jumps);
        var origin = row.NearestOrigin is long o ? snapshot.Standings.Own(o)?.Name
                   ?? snapshot.Own.FirstOrDefault(x => x.CharacterId == o)?.Name : null;
        JumpsTip = IntelWords.JumpsTip(row.Jumps, row.ViaWormhole, origin);

        IsClear = e.Kind == IntelEventKind.Clear;
        TierBrush = IsClear ? null : IntelTierColors.Of(row.Tier);
        StandingsTip = IsClear ? null : IntelWords.StandingsTip(snapshot.Standings, row.StandingsFrom);

        var gateName = e.NearGate is { } gate ? snapshot.SystemName(gate.DestinationSystemId) : null;
        var pictures = new List<IntelPictureVm>();
        var more = 0;
        string tail;
        string? detail = null;

        switch (e.Kind)
        {
            case IntelEventKind.Clear:
                State = IntelRowState.Clear;
                StateText = IntelText.StateClear;
                tail = "";
                detail = e.Reporter is { Length: > 0 } who ? string.Format(culture, IntelText.ClearBy, who) : null;
                break;

            case IntelEventKind.Kill:
            {
                // Who lost what to whom: the victim's portrait (their group's logo on it) and name, their hull, then
                // the attackers by group with counts, then what the attackers flew. The victim is no hostile: never
                // counted among them.
                State = IntelRowState.Kill;
                StateText = gateName is null ? IntelText.StateKill : IntelText.StateGateKill;
                if (e.VictimId > 0)
                    pictures.Add(IntelPictureVm.Pilot(e.VictimId, e.VictimName ?? e.VictimId.ToString(CultureInfo.InvariantCulture),
                                                      IntelWatchMark.None, GroupBadge(e.VictimCorporationId, e.VictimAllianceId, snapshot)));
                if (e.VictimShipTypeId > 0)
                    pictures.Add(IntelPictureVm.Ship(e.VictimShipTypeId, e.VictimShip ?? "?"));
                pictures.Add(IntelPictureVm.Words(IntelText.KillLostTo));
                AddGroups(pictures, row.Pilots, snapshot, IntelText.KillAttackersTip);
                var hulls = Hulls(row.Pilots.Select(p => p.Pilot).ToList(), e.TopHullTypeIds);
                pictures.AddRange(hulls.Take(MaxPictures - 1));
                more = Math.Max(0, hulls.Count - (MaxPictures - 1));
                tail = gateName is null ? "" : string.Format(culture, IntelText.NearGate, gateName);
                break;
            }

            case IntelEventKind.CampFormed:
            {
                State = IntelRowState.Camp;
                StateText = string.Format(culture, IntelText.StateCamp, camp?.KillMailIds.Count ?? 0);
                // Who is camping, by group with counts, then what they fly.
                AddGroups(pictures, row.Pilots, snapshot, IntelText.WhoTipTitle);
                var hulls = Hulls(row.Pilots.Select(p => p.Pilot).ToList(), e.TopHullTypeIds);
                pictures.AddRange(hulls.Take(MaxPictures));
                more = Math.Max(0, hulls.Count - MaxPictures);
                var flags = IntelWords.Flags(0, camp);
                tail = gateName is null ? flags
                     : flags.Length == 0 ? string.Format(culture, IntelText.NearGate, gateName)
                     : string.Format(culture, IntelText.NearGate, gateName) + " · " + flags;
                break;
            }

            default:
            {
                var count = row.Pilots.Count + e.Count;
                (State, StateText) = e.Kind switch
                {
                    IntelEventKind.LocalPaste => (IntelRowState.Hostile, string.Format(culture, IntelText.StateLocal, count)),
                    IntelEventKind.Dscan      => (IntelRowState.Hostile, string.Format(culture, IntelText.StateDscan, count)),
                    _ when count == 0         => (IntelRowState.Flags, IntelWords.Flags(e.Flags) is { Length: > 0 } f ? f : IntelText.StateReported),
                    _ when e.NoVisual         => (IntelRowState.NoVisual, string.Format(culture, IntelText.StateNoVisual, count)),
                    _                         => (IntelRowState.Hostile, string.Format(culture, IntelText.StateHostile, count)),
                };

                // Each named pilot with their portrait (their group's logo small on it) and name, then their hull with
                // its icon, as far as there is room.
                var shown = 0;
                foreach (var named in row.Pilots.OrderBy(p => p.Tier))
                {
                    var p = named.Pilot;
                    var needs = p.Ship is { Length: > 0 } ? 2 : 1;
                    if (pictures.Count + needs > MaxPictures) break;
                    pictures.Add(IntelPictureVm.Pilot(p.CharacterId, p.Name ?? p.CharacterId.ToString(CultureInfo.InvariantCulture), named.Watch,
                                                      GroupBadge(p.CorporationId, p.AllianceId, snapshot)));
                    if (p.Ship is { Length: > 0 } ship)
                        pictures.Add(p.ShipTypeId is > 0 and int type ? IntelPictureVm.Ship(type, ship) : IntelPictureVm.SaidHull(snapshot, ship));
                    shown++;
                }
                // Hulls said with nobody to fly them: "Muninn ×8".
                if (shown == row.Pilots.Count)
                    foreach (var (n, hull) in IntelDisplay.ParseShips(e.Ships))
                    {
                        // Past the room: counted in "+N unnamed" when pilots were named too, else in "+N".
                        if (pictures.Count >= MaxPictures) { if (row.Pilots.Count == 0) more += n; continue; }
                        pictures.Add(IntelPictureVm.SaidHull(snapshot, hull, n));
                    }
                more += row.Pilots.Count - shown;
                if (e.Count > 0 && row.Pilots.Count > 0)
                    pictures.Add(IntelPictureVm.Words(string.Format(culture, IntelText.PlusUnnamed, e.Count)));

                tail = count == 0 ? "" : IntelWords.Flags(e.Flags);
                if (pictures.Count == 0 && !string.IsNullOrWhiteSpace(e.Message)) detail = Said(e.Message!, snapshot, e.SystemId);
                if (e.Gate is { Length: > 0 } said && count > 0)
                    tail = tail.Length == 0 ? string.Format(culture, MapText.IntelOnGate, said)
                                            : tail + " · " + string.Format(culture, MapText.IntelOnGate, said);
                break;
            }
        }

        _superseded = e.Superseded;
        Kind = e.Kind;
        KindText = e.Kind switch
        {
            IntelEventKind.FollowUp   => IntelText.KindFollowUp,
            IntelEventKind.Clear      => IntelText.KindClear,
            IntelEventKind.LocalPaste => IntelText.KindLocalPaste,
            IntelEventKind.Dscan      => IntelText.KindDscan,
            IntelEventKind.Kill       => IntelText.KindKill,
            IntelEventKind.CampFormed => IntelText.KindCamp,
            _                         => IntelText.KindReport,
        };
        // EVE time is UTC.
        TimeText = e.At.UtcDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        SourceText = e.Kind switch
        {
            IntelEventKind.Kill or IntelEventKind.CampFormed => IntelText.SourceKillboard,
            IntelEventKind.LocalPaste or IntelEventKind.Dscan =>
                e.Reporter is { Length: > 0 } paster ? string.Format(culture, IntelText.SourcePasteBy, paster) : IntelText.SourcePaste,
            _ => e.Reporter is { Length: > 0 } reporter && e.Channel is { Length: > 0 } channel
                    ? string.Format(culture, IntelText.SourceChannelBy, channel, reporter)
                    : e.Channel ?? "",
        };
        ReporterId = e.Kind is IntelEventKind.Kill or IntelEventKind.CampFormed ? 0 : e.ReporterId ?? 0;
        // Where they came from or are heading: the solid and the dashed arrow on the map, in words.
        var direction = new List<string>();
        if (e.FromSystemId is int fromSystem && fromSystem != e.SystemId)
            direction.Add(string.Format(culture, IntelText.CameFrom, snapshot.SystemName(fromSystem)));
        if (e.HeadingSystemId is int heading && heading != e.SystemId)
            direction.Add(string.Format(culture, IntelText.HeadingTo, snapshot.SystemName(heading)));
        DirectionText = string.Join(" · ", direction);

        Pictures = pictures;
        Tokens = [.. pictures.SelectMany(IntelTokenVm.Of)];
        MoreText = more > 0 ? "+" + more.ToString(culture) : "";
        TailText = tail;
        DetailText = detail ?? "";
        FollowUpText = row.FollowUps.Count > 0 ? string.Format(culture, IntelText.FollowUps, row.FollowUps.Count) : "";
        ToolTip = TipOf(row, culture);
        Watch = row.Watch;
        WatchSet = snapshot.Watch;
        Signature = string.Join("|", Key, JumpsText, row.Tier, StateText, TailText, DetailText, MoreText, FollowUpText, e.Superseded, Watch,
                                string.Join(",", pictures.Select(p => p.Kind + ":" + p.Id + ":" + p.Text + ":" + p.Watch + ":" + p.Badge?.Id
                                                                      + ":" + p.CountText + ":" + p.PictureTip)));
        Tick(now, snapshot.Settings.FreshSeconds);
    }

    public IntelRowActions Actions { get; }
    public string Key { get; }

    /// <summary>How strongly a row or a pilot the watch list ignores is drawn, against its own fade.</summary>
    public const double IgnoredOpacity = 0.45;

    /// <summary>What the watch list says of the row: Always (bold) or Ignored (dimmed).</summary>
    public IntelWatchMark Watch { get; }
    public bool IsWatchAlways  => Watch == IntelWatchMark.Always;
    public bool IsWatchIgnored => Watch == IntelWatchMark.Ignored;
    public FontWeight StateWeight => IsWatchAlways ? FontWeight.Bold : FontWeight.Normal;
    public FontWeight NameWeight  => IsWatchAlways ? FontWeight.ExtraBold : FontWeight.SemiBold;

    /// <summary>The watch list as the row was built with, for the pilot menu's ticks.</summary>
    public IntelWatchSet WatchSet { get; }

    public IntelEventKind Kind { get; }

    /// <summary>"Report", "Follow-up", "Kill"…: what the Feed labels the line.</summary>
    public string KindText { get; }

    /// <summary>When it was said, in EVE time (UTC): the Feed shows it under the age.</summary>
    public string TimeText { get; }

    /// <summary>Where it came from: "channel · reporter", "zKillboard", "Paste · who pasted".</summary>
    public string SourceText { get; }

    /// <summary>Who reported or pasted it, for their portrait beside the source; 0 for a kill.</summary>
    public long ReporterId { get; }
    public bool HasReporter => ReporterId > 0;

    /// <summary>"came from X", "heading to Y".</summary>
    public string DirectionText { get; }
    public bool HasDirection => DirectionText.Length > 0;

    private readonly bool _superseded;

    /// <summary>A later line restated or replaced it: the Feed keeps it, faded, and says so.</summary>
    public bool IsSuperseded => _superseded;

    private string _statusText = "";
    /// <summary>"superseded" or "expired", or nothing while it stands.</summary>
    public string StatusText { get => _statusText; private set { this.RaiseAndSetIfChanged(ref _statusText, value); this.RaisePropertyChanged(nameof(HasStatus)); } }
    public bool HasStatus => StatusText.Length > 0;

    /// <summary>What the row shows, less its age: a row built again from a new snapshot that reads
    /// the same keeps the one already on screen, pictures and all.</summary>
    public string Signature { get; }

    public int    SystemId   { get; }
    public string SystemName { get; }

    public string JumpsText { get; }
    public bool   HasJumps  => JumpsText.Length > 0;
    public string? JumpsTip { get; }
    public IntelJumpBand Band { get; }
    public bool IsHere   => Band == IntelJumpBand.Here;
    public bool IsNear   => Band == IntelJumpBand.Near;
    public bool IsMid    => Band == IntelJumpBand.Mid;
    public bool IsFar    => Band == IntelJumpBand.Far;
    public bool IsPending => Band == IntelJumpBand.Unknown && HasJumps;

    /// <summary>The standings stripe: the worst tier named; null on a clear, which is drawn as good.</summary>
    public IBrush? TierBrush { get; }
    public string? StandingsTip { get; }

    public IntelRowState State { get; }
    public string StateText { get; }
    public bool IsClear { get; }
    public bool IsHostileState  => State == IntelRowState.Hostile;
    public bool IsNoVisualState => State == IntelRowState.NoVisual;
    public bool IsCampState     => State == IntelRowState.Camp;
    public bool IsKillState     => State == IntelRowState.Kill;
    public bool IsFlagsState    => State == IntelRowState.Flags;

    /// <summary>Who and what, in order: each a picture with its name, a group's logo with its count, or words.</summary>
    public IReadOnlyList<IntelPictureVm> Pictures { get; }

    /// <summary><see cref="Pictures"/> as drawn: each picture, its name and its count as pieces of their own, so a narrow
    /// line leaves names out and keeps the pictures.</summary>
    public IReadOnlyList<IntelTokenVm> Tokens { get; }

    public string MoreText { get; }
    public bool HasMore => MoreText.Length > 0;
    public string TailText { get; }
    public bool HasTail => TailText.Length > 0;
    public string DetailText { get; }
    public bool HasDetail => DetailText.Length > 0;
    public bool HasSecondLine => Pictures.Count > 0 || HasTail || HasDetail || HasMore;
    public string FollowUpText { get; }
    public bool HasFollowUps => FollowUpText.Length > 0;
    public string ToolTip { get; }

    public bool CanOpenInTool => Actions.OpenInTool is not null;
    public bool CanShowOnMap  => Actions.ShowOnMap is not null;

    private string _ageText = "";
    public string AgeText { get => _ageText; private set => this.RaiseAndSetIfChanged(ref _ageText, value); }

    private double _opacity = 1;
    public double Opacity { get => _opacity; private set => this.RaiseAndSetIfChanged(ref _opacity, value); }

    private bool _isFresh;
    /// <summary>News under the fresh time, within five jumps: what an alarm would wake for.
    /// Lights the row; makes no sound.</summary>
    public bool IsFresh { get => _isFresh; private set => this.RaiseAndSetIfChanged(ref _isFresh, value); }

    /// <summary>The newest news on the row, a follow-up's included: what the panel sorts by.</summary>
    public DateTimeOffset NewestAt => _newest;

    /// <summary>Age, fade and freshness at <paramref name="now"/>.</summary>
    public void Tick(DateTimeOffset now, int freshSeconds)
    {
        AgeText = IntelWords.Age(_newest, now);
        Opacity = (_superseded ? IntelLens.ExpiredOpacity : IntelLens.Opacity(_at, _lifetime, now)) * (IsWatchIgnored ? IgnoredOpacity : 1.0);
        StatusText = _superseded ? IntelText.StatusSuperseded : now >= _at + _lifetime ? IntelText.StatusExpired : "";
        IsFresh = _canBeFresh && !IsWatchIgnored && _jumps is int j && j <= IntelViewer.FreshJumps
                  && now - _newest <= TimeSpan.FromSeconds(freshSeconds) && now < _at + _lifetime;
    }

    private Bitmap? _reporterPicture;
    /// <summary>The reporter's portrait, beside the source in the tool's Feed.</summary>
    public Bitmap? ReporterPicture { get => _reporterPicture; private set => this.RaiseAndSetIfChanged(ref _reporterPicture, value); }

    public Task LoadPicturesAsync() => Task.WhenAll(Pictures.Select(p => p.LoadAsync()).Append(LoadReporterAsync()));

    private async Task LoadReporterAsync()
    {
        if (ReporterId <= 0 || ReporterPicture is not null) return;
        var bitmap = await EveImageCache.GetAsync($"https://images.evetech.net/characters/{ReporterId}/portrait?size=32").ConfigureAwait(false);
        if (bitmap is not null) Dispatcher.UIThread.Post(() => ReporterPicture = bitmap);
    }

    /// <summary>For a check with no network: the reporter's picture given rather than fetched.</summary>
    public void ShowReporter(Bitmap? picture) => ReporterPicture = picture;

    public void OpenSystem()
    {
        EntityNavigator.Instance.System(SystemId);
        Actions.AfterNavigate?.Invoke();
    }

    public void ShowOnMap()
    {
        Actions.ShowOnMap?.Invoke(SystemId);
        Actions.AfterNavigate?.Invoke();
    }

    public void OpenInTool() => Actions.OpenInTool?.Invoke(SystemId);

    public void OpenPicture(IntelPictureVm picture)
    {
        picture.Open();
        Actions.AfterNavigate?.Invoke();
    }

    public ReactiveCommand<Unit, Unit> OpenSystemCommand => _openSystem ??= ReactiveCommand.Create(OpenSystem);
    private ReactiveCommand<Unit, Unit>? _openSystem;

    /// <summary>What was said, less the system's name it starts with, which the row already shows.</summary>
    private static string Said(string message, IntelSnapshot snapshot, int systemId)
    {
        var text = message.Trim();
        if (snapshot.SystemNames.TryGetValue(systemId, out var english)
            && text.StartsWith(english, StringComparison.OrdinalIgnoreCase) && text.Length > english.Length
            && !char.IsLetterOrDigit(text[english.Length]))
            text = text[english.Length..].TrimStart(' ', ':', ',', '-', '·');
        return text.Length > 0 ? text : message;
    }

    /// <summary>The groups a line shows by logo and count before "+N".</summary>
    public const int MaxGroups = 2;

    /// <summary>A pilot's alliance logo, or their corporation's when they have none: small, on their portrait.</summary>
    private static IntelPictureVm? GroupBadge(long corporationId, long allianceId, IntelSnapshot snapshot)
    {
        var culture = CultureInfo.CurrentCulture;
        return allianceId > 0 ? IntelPictureVm.Alliance(allianceId, snapshot.EntityName(allianceId) ?? string.Format(culture, MapText.AllianceNumbered, allianceId))
             : corporationId > 0 ? IntelPictureVm.Corporation(corporationId, snapshot.EntityName(corporationId) ?? string.Format(culture, MapText.CorporationNumbered, corporationId))
             : null;
    }

    /// <summary>
    /// Who, by group: the line's pilots by alliance (or corporation, with none), the largest first, each as its logo
    /// and "×n", the first <see cref="MaxGroups"/>, then "+N" groups. The first carries the whole breakout in its
    /// tooltip under <paramref name="title"/>, with the pilots whose group is not known yet. The same grouping as a
    /// system's who (<see cref="IntelHostiles.Groups"/>).
    /// </summary>
    private static void AddGroups(List<IntelPictureVm> pictures, IReadOnlyList<IntelEventPilotRow> pilots, IntelSnapshot snapshot, string title)
    {
        var hostiles = pilots.Select(p => new IntelHostile(p.Pilot.CharacterId, p.Pilot.Name, p.Pilot.ShipTypeId, p.Pilot.Ship,
                                                           p.Pilot.CorporationId, p.Pilot.AllianceId, DateTimeOffset.MinValue, p.Tier, p.Watch,
                                                           false, false)).ToList();
        var groups = IntelHostiles.Groups(hostiles, snapshot);
        if (groups.Count == 0) return;
        var culture = CultureInfo.CurrentCulture;
        string NameOf(IntelWhoGroup g) => g.Name ?? string.Format(culture,
            g.Kind == IntelWhoKind.Alliance ? MapText.AllianceNumbered : MapText.CorporationNumbered, g.Id);

        var tip = new List<string> { title };
        tip.AddRange(groups.Select(g => NameOf(g) + " · " + g.Count.ToString(culture)));
        var unknown = hostiles.Count(p => p.AllianceId <= 0 && p.CorporationId <= 0);
        if (unknown > 0) tip.Add(string.Format(culture, IntelText.WhoTipUnknown, unknown));
        var breakout = string.Join("\n", tip);

        var shown = groups.Take(MaxGroups).ToList();
        for (var i = 0; i < shown.Count; i++)
            pictures.Add(IntelPictureVm.Group(shown[i].Kind, shown[i].Id, NameOf(shown[i]), shown[i].Count, i == 0 ? breakout : null));
        if (groups.Count > shown.Count)
            pictures.Add(IntelPictureVm.Words("+" + (groups.Count - shown.Count).ToString(culture)));
    }

    /// <summary>Hulls with how many flew each, most first: from who was on the kill or the camp, by type.</summary>
    private static List<IntelPictureVm> Hulls(IReadOnlyList<IntelEventPilot> pilots, IReadOnlyList<int>? top)
    {
        var byType = pilots.Where(p => p.ShipTypeId is > 0)
            .GroupBy(p => p.ShipTypeId!.Value)
            .Select(g => (Type: g.Key, Name: g.Select(p => p.Ship).FirstOrDefault(s => !string.IsNullOrEmpty(s)), Count: g.Count()))
            .ToList();
        var order = (top ?? []).ToList();
        return [.. byType
            .OrderBy(h => order.IndexOf(h.Type) is var i and >= 0 ? i : int.MaxValue)
            .ThenByDescending(h => h.Count).ThenBy(h => h.Type)
            .Select(h => IntelPictureVm.Ship(h.Type, h.Name ?? h.Type.ToString(CultureInfo.InvariantCulture), h.Count))];
    }

    /// <summary>The whole line as said, who said it where, and the later lines of its thread.</summary>
    private static string TipOf(IntelFeedRow row, CultureInfo culture)
    {
        var e = row.Event;
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(e.Message)) lines.Add(e.Message!);
        if (e.Kind == IntelEventKind.Kill && e.VictimName is { Length: > 0 } victim)
            lines.Add(string.Format(culture, IntelText.KillVictim, victim, e.AttackerCount));
        if (e.Kind is IntelEventKind.LocalPaste or IntelEventKind.Dscan && e.Reporter is { Length: > 0 } paster)
            lines.Add(string.Format(culture, IntelText.PastedBy, paster));
        else if (e.Reporter is { Length: > 0 } who)
            lines.Add(string.Format(culture, IntelText.ReportedBy, who, e.Channel ?? ""));
        if (row.FollowUps.Count > 0)
        {
            lines.Add(IntelText.LaterInThread);
            lines.AddRange(row.FollowUps.Select(f => "  " + (f.Event.Message ?? "")));
        }
        return string.Join("\n", lines);
    }
}

/// <summary>What a row says about its system, for its colour.</summary>
public enum IntelRowState { Hostile, NoVisual, Flags, Camp, Kill, Clear }
