using System.Globalization;
using System.Reactive;
using Avalonia.Media;
using EveConsole.Localization;
using EveConsole.Monitoring;
using EveConsole.Services;
using EveConsole.Services.Intel;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>What a small flag icon on an intel line stands for. The word is its tooltip.</summary>
public enum IntelFlagKind { Kill, Camp, NoVisual, Clear, Cyno, Bubbles, Other }

/// <summary>
/// A flag as a small icon rather than a word: a kill, a camp, no visual, a clear, a cyno, bubbles, or the rest
/// said (a spike, a hot drop…) as one mark. The word, and what it says of the system, is the tooltip.
/// </summary>
public sealed class IntelFlagVm(IntelFlagKind kind, string tip)
{
    public IntelFlagKind Kind { get; } = kind;
    public string Tip { get; } = tip;

    public bool IsKill     => Kind == IntelFlagKind.Kill;
    public bool IsCamp     => Kind == IntelFlagKind.Camp;
    public bool IsNoVisual => Kind == IntelFlagKind.NoVisual;
    public bool IsClear    => Kind == IntelFlagKind.Clear;
    public bool IsCyno     => Kind == IntelFlagKind.Cyno;
    public bool IsBubbles  => Kind == IntelFlagKind.Bubbles;
    public bool IsOther    => Kind == IntelFlagKind.Other;

    /// <summary>The icon, drawn in a 12-pixel square: shapes of this app's own, stroked in the flag's theme colour.</summary>
    public Geometry Data => StreamGeometry.Parse(Kind switch
    {
        IntelFlagKind.Kill     => "M6,2 A4,4 0 1 1 5.99,2 Z M6,0 V3.5 M6,8.5 V12 M0,6 H3.5 M8.5,6 H12",
        IntelFlagKind.Camp     => "M1,11 V5 A5,4 0 0 1 11,5 V11 M4,11 V6.5 A2,2 0 0 1 8,6.5 V11",
        IntelFlagKind.NoVisual => "M0.5,6 Q6,0.5 11.5,6 Q6,11.5 0.5,6 Z M6,4.5 A1.5,1.5 0 1 1 5.99,4.5 Z M1.5,10.5 L10.5,1.5",
        IntelFlagKind.Clear    => "M1.5,6.5 L4.8,9.8 L10.5,2.5",
        IntelFlagKind.Cyno     => "M6,0.5 L9.5,6 L6,11.5 L2.5,6 Z M6,3.5 V8.5",
        IntelFlagKind.Bubbles  => "M6,1 A5,5 0 1 1 5.99,1 Z M6,4 A2,2 0 1 1 5.99,4 Z",
        _                      => "M6,1 A5,5 0 1 1 5.99,1 Z M6,3.2 V7 M6,8.6 V9",
    });

    /// <summary>The system's flags as icons, most pressing first: a kill (with when), a camp (with its gate), no
    /// visual, a clear, a cyno, bubbles, then the rest said as one mark.</summary>
    public static List<IntelFlagVm> Of(IntelSystemRow row, IntelSystemHostiles hostiles, IntelSnapshot snapshot)
    {
        var culture = CultureInfo.CurrentCulture;
        var flags = new List<IntelFlagVm>();
        if (hostiles.Kills > 0 && hostiles.NewestKill is { } newest)
            flags.Add(new(IntelFlagKind.Kill, hostiles.Kills == 1
                ? string.Format(culture, IntelText.FlagKill, IntelWords.EveTime(newest))
                : string.Format(culture, IntelText.FlagKills, hostiles.Kills, IntelWords.EveTime(newest))));

        var said = row.Facts.Aggregate(0, (f, x) => f | (int)x.Flag);
        var camp = row.Camps.FirstOrDefault();
        if (camp is not null || (said & (int)IntelRules.IntelFlags.Camp) != 0)
        {
            var gate = CampGate(row, snapshot);
            var words = IntelWords.Flags(0, camp);
            var tip = gate is null ? IntelText.FlagCamp : string.Format(culture, IntelText.FlagCampOn, gate);
            flags.Add(new(IntelFlagKind.Camp, words.Length == 0 ? tip : tip + " · " + words));
        }
        if (row.State == IntelSystemState.NoVisual || (row.Unnamed > 0 && row.UnnamedNoVisual) || hostiles.Pilots.Any(p => p.NoVisual))
            flags.Add(new(IntelFlagKind.NoVisual, IntelText.FlagNoVisual));
        if (row.State == IntelSystemState.Clear)
            flags.Add(new(IntelFlagKind.Clear, IntelText.FlagClear));
        if ((said & (int)IntelRules.IntelFlags.Cyno) != 0)
            flags.Add(new(IntelFlagKind.Cyno, MapText.IntelFlagCyno));
        if ((said & (int)IntelRules.IntelFlags.Bubbles) != 0 || camp?.Bubble == true)
            flags.Add(new(IntelFlagKind.Bubbles, MapText.IntelFlagBubbles));
        var rest = said & ~(int)(IntelRules.IntelFlags.Camp | IntelRules.IntelFlags.Cyno | IntelRules.IntelFlags.Bubbles);
        if (IntelDisplay.Flags(rest) is { Count: > 0 } others)
            flags.Add(new(IntelFlagKind.Other, string.Join(", ", others)));
        return flags;
    }

    /// <summary>The camped gate's name: the camp's own, else the gate a report named.</summary>
    public static string? CampGate(IntelSystemRow row, IntelSnapshot snapshot) =>
        row.Camps.FirstOrDefault() is { } camp ? snapshot.SystemName(camp.GateSystemId)
        : row.Gate is { Length: > 0 } said ? said : null;
}

/// <summary>
/// One piece of "who": a group's logo with how many fly for it, or a pilot's portrait (with a smaller logo of their
/// alliance or corporation). The name beside it where there is room; always in its tooltip.
/// </summary>
public sealed class IntelWhoTokenVm(IntelPictureVm picture, string name, string count, string tip, IntelPictureVm? badge = null)
{
    public IntelPictureVm Picture { get; } = picture;
    public string Name { get; } = name;
    public string CountText { get; } = count;
    public bool HasCount => CountText.Length > 0;
    /// <summary>"×4", after the name where there is room for it (the Board); empty for a pilot.</summary>
    public string TimesText => HasCount ? "×" + CountText : "";
    /// <summary>The token's own name and count; the first token's is the whole breakout, as it is the one never dropped.</summary>
    public string Tip { get; internal set; } = tip;

    /// <summary>A single pilot's alliance (or corporation) logo, small, on their portrait.</summary>
    public IntelPictureVm? Badge { get; } = badge;
    public bool HasBadge => Badge is not null;
    public bool IsPilot => Picture.IsPilot;

    public IEnumerable<IntelPictureVm> PicturesToLoad => Badge is null ? [Picture] : [Picture, Badge];
}

/// <summary>
/// "Who" for a system: one pilot by their portrait and alliance; several by their alliances (or corporations), the
/// largest first, each with its count, then "+N" for the rest. The tooltip has the whole breakout.
/// </summary>
public sealed class IntelWhoVm
{
    public IntelWhoVm(IntelSystemHostiles hostiles, int maxTokens)
    {
        var culture = CultureInfo.CurrentCulture;
        var tokens = new List<IntelWhoTokenVm>();
        var more = 0;
        var tip = new List<string>();

        if (hostiles.Single is { } one)
        {
            var name = one.Name ?? one.CharacterId.ToString(CultureInfo.InvariantCulture);
            var badge = GroupPicture(one, hostiles);
            tokens.Add(new IntelWhoTokenVm(IntelPictureVm.Pilot(one.CharacterId, name, one.Watch), name, "",
                badge is null ? name : name + "\n" + badge.Text, badge));
            tip.Add(name);
            if (badge is not null) tip.Add(badge.Text);
        }
        else if (hostiles.Groups.Count > 0)
        {
            foreach (var g in hostiles.Groups.Take(maxTokens))
            {
                var picture = Picture(g);
                var count = g.Count.ToString(culture);
                tokens.Add(new IntelWhoTokenVm(picture, picture.Text, count, picture.Text + " · " + count));
            }
            more = hostiles.Groups.Count - tokens.Count;
            tip.Add(IntelText.WhoTipTitle);
            tip.AddRange(hostiles.Groups.Select(g => Picture(g).Text + " · " + g.Count.ToString(culture)));
            var unknown = hostiles.Pilots.Count(p => p.AllianceId <= 0 && p.CorporationId <= 0);
            if (unknown > 0) tip.Add(string.Format(culture, IntelText.WhoTipUnknown, unknown));
        }
        else if (hostiles.Pilots.Count > 0)
        {
            // Several with no known affiliation; or one named among others counted, whose portrait carries a "1" so the
            // line does not read as one person.
            var counted = hostiles.Pilots.Count == 1 ? 1.ToString(culture) : "";
            foreach (var p in hostiles.Pilots.Take(maxTokens))
            {
                var name = p.Name ?? p.CharacterId.ToString(CultureInfo.InvariantCulture);
                tokens.Add(new IntelWhoTokenVm(IntelPictureVm.Pilot(p.CharacterId, name, p.Watch), name, counted, name));
            }
            more = hostiles.Pilots.Count - tokens.Count;
            tip.AddRange(hostiles.Pilots.Select(p => p.Name ?? p.CharacterId.ToString(CultureInfo.InvariantCulture)));
        }
        if (hostiles.Unnamed > 0 && tip.Count > 0) tip.Add(string.Format(culture, IntelText.PlusUnnamed, hostiles.Unnamed));

        Tokens = tokens;
        MoreText = more > 0 ? "+" + more.ToString(culture) : "";
        Tip = tip.Count == 0 ? null : string.Join("\n", tip);
        // The first token is never dropped from a narrow line, so it carries the whole breakout: the names, the second
        // group and "+N" may all be gone.
        if (Tip is not null && tokens.Count > 0) tokens[0].Tip = Tip;
        Signature = string.Join(",", tokens.Select(t => t.Picture.Kind + ":" + t.Picture.Id + ":" + t.Name + ":" + t.CountText + ":" + t.Badge?.Id)) + "|" + MoreText + "|" + Tip;
    }

    public IReadOnlyList<IntelWhoTokenVm> Tokens { get; }
    public bool HasTokens => Tokens.Count > 0;
    public IntelWhoTokenVm? First => Tokens.Count > 0 ? Tokens[0] : null;
    public IntelWhoTokenVm? Second => Tokens.Count > 1 ? Tokens[1] : null;
    public bool HasSecond => Tokens.Count > 1;
    public string MoreText { get; }
    public bool HasMore => MoreText.Length > 0;
    public string? Tip { get; }
    public string Signature { get; }

    public IEnumerable<IntelPictureVm> PicturesToLoad => Tokens.SelectMany(t => t.PicturesToLoad);

    private static IntelPictureVm Picture(IntelWhoGroup g) => g.Kind == IntelWhoKind.Alliance
        ? IntelPictureVm.Alliance(g.Id, g.Name ?? string.Format(CultureInfo.CurrentCulture, MapText.AllianceNumbered, g.Id))
        : IntelPictureVm.Corporation(g.Id, g.Name ?? string.Format(CultureInfo.CurrentCulture, MapText.CorporationNumbered, g.Id));

    /// <summary>A pilot's alliance, or their corporation when they have none: its logo and name.</summary>
    public static IntelPictureVm? GroupPicture(IntelHostile p, IntelSystemHostiles hostiles) =>
        p.AllianceId > 0 ? Picture(new IntelWhoGroup(IntelWhoKind.Alliance, p.AllianceId, Named(hostiles, IntelWhoKind.Alliance, p.AllianceId), 1))
        : p.CorporationId > 0 ? Picture(new IntelWhoGroup(IntelWhoKind.Corporation, p.CorporationId, Named(hostiles, IntelWhoKind.Corporation, p.CorporationId), 1))
        : null;

    private static string? Named(IntelSystemHostiles hostiles, IntelWhoKind kind, long id) =>
        hostiles.Groups.FirstOrDefault(g => g.Kind == kind && g.Id == id)?.Name;
}

/// <summary>
/// One hostile pilot in a system's details: their portrait and name, their alliance (or corporation) logo and name,
/// their hull's icon and name, how long since they were seen, and a kill mark when they were on a kill here.
/// </summary>
public sealed class IntelHostilePilotVm : ReactiveObject
{
    private readonly DateTimeOffset _seenAt;

    public IntelHostilePilotVm(IntelHostile p, IntelSystemHostiles hostiles, IntelSnapshot snapshot, IntelRowActions actions, DateTimeOffset now)
    {
        var culture = CultureInfo.CurrentCulture;
        Actions = actions;
        CharacterId = p.CharacterId;
        Name = p.Name ?? p.CharacterId.ToString(CultureInfo.InvariantCulture);
        Pilot = IntelPictureVm.Pilot(p.CharacterId, Name, p.Watch);
        Group = IntelWhoVm.GroupPicture(p, hostiles);
        // Both, where they differ, in the logo's tooltip: the alliance shown, the corporation under it.
        var corp = p.CorporationId > 0 ? snapshot.EntityName(p.CorporationId) ?? string.Format(culture, MapText.CorporationNumbered, p.CorporationId) : null;
        GroupTip = Group is null ? null : p.AllianceId > 0 && corp is not null ? Group.Text + "\n" + corp : Group.Text;
        Hull = p.Ship is { Length: > 0 } ship
            ? p.ShipTypeId is > 0 and int type ? IntelPictureVm.Ship(type, ship) : IntelPictureVm.SaidHull(snapshot, ship)
            : null;
        HullTip = Hull is null ? null : Hull.Text + "\n" + IntelText.TipOpenHull;
        OnKill = p.OnKill;
        NoVisual = p.NoVisual;
        TierBrush = IntelTierColors.Of(p.Tier);
        _seenAt = p.SeenAt;
        TimeTip = string.Format(culture, IntelText.TipSeenAt, IntelWords.EveTime(p.SeenAt));
        WatchSet = snapshot.Watch;
        Watch = p.Watch;
        Signature = string.Join("|", CharacterId, Name, Group?.Id, Group?.Text, Hull?.Id, Hull?.Text, OnKill, NoVisual, p.Tier, p.Watch,
                                p.SeenAt.ToUnixTimeSeconds());
        Tick(now);
    }

    public IntelRowActions Actions { get; }
    public string Signature { get; }
    public long CharacterId { get; }
    public string Name { get; }
    public IntelPictureVm Pilot { get; }
    public IntelPictureVm? Group { get; }
    public bool HasGroup => Group is not null;
    public string? GroupTip { get; }
    public IntelPictureVm? Hull { get; }
    public bool HasHull => Hull is not null;
    /// <summary>The hull's whole name (trimmed on a narrow line), and what clicking it does.</summary>
    public string? HullTip { get; }
    public bool OnKill { get; }
    public bool NoVisual { get; }
    public IBrush TierBrush { get; }
    public string TimeTip { get; }
    public IntelWatchSet WatchSet { get; }
    public IntelWatchMark Watch { get; }
    public double Opacity => Watch == IntelWatchMark.Ignored ? IntelEventRowVm.IgnoredOpacity : 1.0;

    private string _ageText = "";
    public string AgeText { get => _ageText; private set => this.RaiseAndSetIfChanged(ref _ageText, value); }

    public void Tick(DateTimeOffset now) => AgeText = IntelWords.CompactAge(_seenAt, now);

    public IEnumerable<IntelPictureVm> PicturesToLoad => new[] { Pilot, Group, Hull }.Where(p => p is not null).Select(p => p!);

    public void Open(IntelPictureVm picture)
    {
        picture.Open();
        Actions.AfterNavigate?.Invoke();
    }
}

/// <summary>
/// One system with hostiles in it, as the panel beside a client, its single line and its popup show it: the unit
/// every intel list leads with. Its one line: how far, the system (its region small under it), how many, what they
/// fly, who they are, how long ago, and small flag icons (a kill among them, so killing reads apart from passing
/// through). Opened: the pilots there, then the system's reports, kills and camp lines.
///
/// <para>Age, fade and the fresh highlight move every second (<see cref="Tick"/>); the rest is fixed for the snapshot
/// it came from, and a row reading the same is kept, pictures and all.</para>
/// </summary>
public sealed class IntelSystemLineVm : ReactiveObject
{
    /// <summary>The groups the line shows before "+N", and the further hulls after the first.</summary>
    public const int MaxGroups = 2, MaxOtherHulls = 3;

    private readonly DateTimeOffset _newest, _expires;
    private readonly IReadOnlyList<(DateTimeOffset At, TimeSpan Life)> _items;
    private readonly int? _jumps;
    private readonly bool _canBeFresh;

    /// <param name="killsShown">Whether the view shows kills (<see cref="IntelView.KillsShown"/>): the kill marker follows it.</param>
    public IntelSystemLineVm(IntelSystemRow row, IntelSnapshot snapshot, IReadOnlyList<IntelFeedRow> events, string? region,
                             DateTimeOffset now, bool pending, IntelRowActions actions, bool killsShown = true)
    {
        var culture = CultureInfo.CurrentCulture;
        Actions = actions;
        SystemId = row.SystemId;
        SystemName = snapshot.SystemName(row.SystemId);
        RegionText = region ?? snapshot.RegionName(row.SystemId) ?? "";
        JumpsText = IntelWords.Jumps(row.Jumps, pending);
        Band = row.Jumps is null && pending ? IntelJumpBand.Unknown : IntelWords.Band(row.Jumps);
        var origin = row.NearestOrigin is long o ? snapshot.Standings.Own(o)?.Name ?? snapshot.Own.FirstOrDefault(x => x.CharacterId == o)?.Name : null;
        JumpsTip = IntelWords.JumpsTip(row.Jumps, row.ViaWormhole, origin);
        _jumps = row.Jumps;
        IsClear = row.State == IntelSystemState.Clear;
        TierBrush = IsClear ? null : IntelTierColors.Of(row.Tier);
        StandingsTip = IsClear ? null : IntelWords.StandingsTip(snapshot.Standings, row.StandingsFrom);
        _canBeFresh = !IsClear;

        var hostiles = IntelHostiles.Of(row, snapshot, now, killsShown);
        Hostiles = hostiles;
        var count = hostiles.Count;
        CountText = count > 0 ? count.ToString(culture) : "";
        IsNoVisual = row.State == IntelSystemState.NoVisual;
        CountTip = count > 0 ? string.Format(culture, IntelText.TipHostileCount, count, hostiles.Pilots.Count, hostiles.Unnamed) : null;

        // Flying: the most flown hull by icon and name (×n); the next few by icon, named in their tooltips.
        if (hostiles.PrimaryHull is { } primary)
        {
            PrimaryHull = IntelPictureVm.Ship(primary.TypeId, primary.Name, primary.Count);
            PrimaryHullName = primary.Name;
            PrimaryHullCount = primary.Count > 1 ? "×" + primary.Count.ToString(culture) : "";
        }
        OtherHulls = [.. hostiles.Hulls.Skip(1).Take(MaxOtherHulls).Select(h => IntelPictureVm.Ship(h.TypeId, h.Name, h.Count))];
        var past = hostiles.Hulls.Skip(1 + MaxOtherHulls).ToList();
        HullsMore = past.Count > 0 ? "+" + past.Count.ToString(culture) : "";
        HullsMoreTip = past.Count > 0 ? string.Join("\n", past.Select(h => IntelPictureVm.Ship(h.TypeId, h.Name, h.Count).Text)) : null;

        Who = new IntelWhoVm(hostiles, MaxGroups);

        // A camp says which gate beside its icon; every other flag, a clear among them, is its icon alone.
        var gate = row.Camps.Count > 0 || row.Facts.Any(f => f.Flag == IntelRules.IntelFlags.Camp) ? IntelFlagVm.CampGate(row, snapshot) : null;
        GateText = gate is null ? "" : string.Format(culture, IntelText.CampGate, gate);
        Flags = IntelFlagVm.Of(row, hostiles, snapshot);

        Pilots = [.. hostiles.Pilots.Select(p => new IntelHostilePilotVm(p, hostiles, snapshot, actions, now))];
        Events = [.. events.Select(e => new IntelEventRowVm(e, snapshot, now, pending, actions))];

        _newest = row.NewestAt;
        _expires = row.ExpiresAt;
        AgeTip = string.Format(culture, IntelText.TipNewsAt, IntelWords.EveTime(row.NewestAt));
        var items = new List<(DateTimeOffset, TimeSpan)>();
        items.AddRange(row.Pilots.Select(p => (p.Pilot.At, p.Pilot.Lifetime)));
        if (row.Unnamed > 0 && row.Intel.Unnamed is { } u) items.Add((u.At, u.Lifetime));
        items.AddRange(row.Facts.Select(f => (f.At, f.Lifetime)));
        items.AddRange(row.Camps.Select(c => (c.LastKill, c.ExpiresAt - c.LastKill)));
        if (IsClear && row.Intel.Clear is { } clear) items.Add((clear.At, clear.Lifetime));
        _items = items;

        Watch = row.Watch;
        Signature = string.Join("|", SystemId, JumpsText, row.Tier, CountText, GateText, RegionText, Watch, IsNoVisual,
                                PrimaryHull?.Id, PrimaryHull?.Text, HullsMore, string.Join(",", OtherHulls.Select(h => h.Id + ":" + h.Text)),
                                Who.Signature, string.Join(",", Flags.Select(f => f.Kind + ":" + f.Tip)),
                                string.Join(",", Pilots.Select(p => p.Signature)), string.Join(",", Events.Select(e => e.Signature)));
        Tick(now, snapshot.Settings.FreshSeconds);
    }

    public IntelRowActions Actions { get; }
    public string Signature { get; }

    /// <summary>Who, what and the kills, as summed up for every intel list.</summary>
    public IntelSystemHostiles Hostiles { get; }

    public int SystemId { get; }
    public string SystemName { get; }
    public string RegionText { get; }
    public bool HasRegion => RegionText.Length > 0;

    public string JumpsText { get; }
    public bool HasJumps => JumpsText.Length > 0;
    public string? JumpsTip { get; }
    public IntelJumpBand Band { get; }
    public bool IsHere => Band == IntelJumpBand.Here;
    public bool IsNear => Band == IntelJumpBand.Near;
    public bool IsMid => Band == IntelJumpBand.Mid;
    public bool IsFar => Band == IntelJumpBand.Far;
    public bool IsPending => Band == IntelJumpBand.Unknown && HasJumps;

    public IBrush? TierBrush { get; }
    public string? StandingsTip { get; }
    public bool IsClear { get; }

    public IntelWatchMark Watch { get; }
    public bool IsWatchIgnored => Watch == IntelWatchMark.Ignored;
    public FontWeight NameWeight => Watch == IntelWatchMark.Always ? FontWeight.ExtraBold : FontWeight.SemiBold;

    /// <summary>How many hostiles, named and counted; empty with none.</summary>
    public string CountText { get; }
    public bool HasCount => CountText.Length > 0;
    public string? CountTip { get; }
    /// <summary>Every sighting was "no visual": the count is drawn as reported rather than seen.</summary>
    public bool IsNoVisual { get; }

    public IntelPictureVm? PrimaryHull { get; }
    public bool HasPrimaryHull => PrimaryHull is not null;
    public string PrimaryHullName { get; } = "";
    public string PrimaryHullCount { get; } = "";
    public bool HasPrimaryHullCount => PrimaryHullCount.Length > 0;
    public IReadOnlyList<IntelPictureVm> OtherHulls { get; }
    public bool HasOtherHulls => OtherHulls.Count > 0;
    public string HullsMore { get; }
    public bool HasHullsMore => HullsMore.Length > 0;
    public string? HullsMoreTip { get; }

    public IntelWhoVm Who { get; }

    /// <summary>"QZ-X77 gate" for a camp; empty otherwise.</summary>
    public string GateText { get; }
    public bool HasGate => GateText.Length > 0;

    public IReadOnlyList<IntelFlagVm> Flags { get; }
    public bool HasFlags => Flags.Count > 0;

    /// <summary>The pilots there, newest first: what the row opens to first.</summary>
    public IReadOnlyList<IntelHostilePilotVm> Pilots { get; }
    public bool HasPilots => Pilots.Count > 0;

    /// <summary>The system's reports, kills and camp lines, as every intel view shows a line.</summary>
    public IReadOnlyList<IntelEventRowVm> Events { get; }
    public bool HasEvents => Events.Count > 0;

    public DateTimeOffset NewestAt => _newest;
    public string AgeTip { get; }

    private bool _isOpen;
    public bool IsOpen { get => _isOpen; set => this.RaiseAndSetIfChanged(ref _isOpen, value); }
    public void Toggle() => IsOpen = !IsOpen;

    private string _ageText = "";
    public string AgeText { get => _ageText; private set => this.RaiseAndSetIfChanged(ref _ageText, value); }

    private double _opacity = 1;
    public double Opacity { get => _opacity; private set => this.RaiseAndSetIfChanged(ref _opacity, value); }

    private bool _isFresh;
    /// <summary>News under the fresh time, within five jumps: lights the row. Never a sound.</summary>
    public bool IsFresh { get => _isFresh; private set => this.RaiseAndSetIfChanged(ref _isFresh, value); }

    public void Tick(DateTimeOffset now, int freshSeconds)
    {
        AgeText = IntelWords.CompactAge(_newest, now);
        Opacity = (_items.Count == 0 ? 1 : _items.Max(i => IntelLens.Opacity(i.At, i.Life, now))) * (IsWatchIgnored ? IntelEventRowVm.IgnoredOpacity : 1.0);
        IsFresh = _canBeFresh && !IsWatchIgnored && _jumps is int j && j <= IntelViewer.FreshJumps
                  && now - _newest <= TimeSpan.FromSeconds(freshSeconds) && now < _expires;
        foreach (var p in Pilots) p.Tick(now);
        foreach (var e in Events) e.Tick(now, freshSeconds);
    }

    /// <summary>Every picture the line and its pilots show (the events load their own), for loading or, in a check, giving.</summary>
    public IEnumerable<IntelPictureVm> PicturesToLoad =>
        new[] { PrimaryHull }.Where(p => p is not null).Select(p => p!)
            .Concat(OtherHulls).Concat(Who.PicturesToLoad).Concat(Pilots.SelectMany(p => p.PicturesToLoad));

    public Task LoadPicturesAsync() =>
        Task.WhenAll(PicturesToLoad.Select(p => p.LoadAsync()).Concat(Events.Select(e => e.LoadPicturesAsync())));

    public bool CanShowOnMap => Actions.ShowOnMap is not null;
    public bool CanOpenInTool => Actions.OpenInTool is not null;

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
}
