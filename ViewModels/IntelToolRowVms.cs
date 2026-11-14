using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Media;
using Avalonia.Threading;
using EveConsole.Data;
using EveConsole.Localization;
using EveConsole.Models;
using EveConsole.Services;
using EveConsole.Services.Intel;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>
/// One pilot on the Intel tool's Pilots tab: where they were last seen, how far that is from the top bar's
/// origin, how long ago, in what, how it is known, and who they fly for. A row the watch list marks reads
/// bold (Always) or dimmed (Ignore).
/// </summary>
public sealed class IntelPilotListRowVm : ReactiveObject
{
    private readonly DateTimeOffset _seenAt;

    public IntelPilotListRowVm(IntelLastSeen r, IReadOnlyDictionary<long, string> names, IntelSnapshot snapshot,
                               int? jumps, bool viaWormhole, long? nearest, bool pending, IntelRowActions actions, DateTimeOffset now)
    {
        var culture = CultureInfo.CurrentCulture;
        Actions = actions;
        CharacterId = r.CharacterId;
        var corp = r.CorporationId ?? 0;
        var alliance = r.AllianceId ?? 0;
        Name = !string.IsNullOrWhiteSpace(r.Name) ? r.Name : names.GetValueOrDefault(r.CharacterId) ?? r.CharacterId.ToString(CultureInfo.InvariantCulture);
        Watch = snapshot.WatchOf(r.CharacterId, corp, alliance, r.ShipTypeId);
        // Bold on the name; an ignored pilot's whole row is dimmed, not the name a second time.
        Pilot = IntelPictureVm.Pilot(r.CharacterId, Name, Watch == IntelWatchMark.Always ? IntelWatchMark.Always : IntelWatchMark.None);

        var standings = snapshot.Standings;
        var tier = standings.TierFor(nearest, r.CharacterId, corp, alliance);
        TierBrush = IntelTierColors.Of(tier);
        StandingsTip = IntelWords.TierName(tier) + " · " + IntelWords.StandingsTip(standings, standings.EffectiveViewer(nearest));

        SystemId = r.SystemId;
        SystemName = snapshot.SystemName(r.SystemId);
        Jumps = jumps;
        JumpsText = IntelWords.Jumps(jumps, pending);
        Band = jumps is null && pending ? IntelJumpBand.Unknown : IntelWords.Band(jumps);
        var from = nearest is long o ? standings.Own(o)?.Name ?? snapshot.Own.FirstOrDefault(x => x.CharacterId == o)?.Name : null;
        JumpsTip = IntelWords.JumpsTip(jumps, viaWormhole, from);

        _seenAt = IntelSnapshotBuilder.TryParse(r.SeenAt, out var at) ? at : now;
        TimeText = _seenAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

        Hull = r.ShipTypeId is > 0 and int type ? IntelPictureVm.Ship(type, SdeNames.Type(type, r.ShipName ?? type.ToString(CultureInfo.InvariantCulture)))
             : r.ShipName is { Length: > 0 } said ? IntelPictureVm.SaidHull(snapshot, said)
             : null;

        KillMailId = r.Source is IntelLastSeenSource.KillAttacker or IntelLastSeenSource.KillVictim ? r.KillMailId ?? 0 : 0;
        SourceText = r.Source switch
        {
            IntelLastSeenSource.LocalPaste   => IntelText.PilotSourceLocal,
            IntelLastSeenSource.KillAttacker => IntelText.PilotSourceAttacker,
            IntelLastSeenSource.KillVictim   => IntelText.PilotSourceVictim,
            _                                => IntelText.PilotSourceReport,
        };
        if (r.NoVisual) SourceText += " · " + IntelText.PilotNoVisual;
        SourceTip = KillMailId > 0 ? IntelText.TipOpenKill : IntelText.TipOpenSystem;

        Corporation = corp > 0 ? IntelPictureVm.Corporation(corp, names.GetValueOrDefault(corp) ?? string.Format(culture, MapText.CorporationNumbered, corp)) : null;
        Alliance = alliance > 0 ? IntelPictureVm.Alliance(alliance, names.GetValueOrDefault(alliance) ?? string.Format(culture, MapText.AllianceNumbered, alliance)) : null;

        WatchSet = snapshot.Watch;
        Signature = string.Join("|", CharacterId, Name, r.SeenAt, SystemId, JumpsText, tier, Watch, Hull?.Text, Hull?.Id, SourceText,
                                Corporation?.Text, Alliance?.Text, StandingsTip);
        Tick(now);
    }

    public IntelRowActions Actions { get; }
    public string Signature { get; }
    public long CharacterId { get; }
    public string Name { get; }
    public IntelPictureVm Pilot { get; }

    public IntelWatchMark Watch { get; }
    public IntelWatchSet WatchSet { get; }
    public FontWeight NameWeight => Watch == IntelWatchMark.Always ? FontWeight.ExtraBold : FontWeight.SemiBold;
    public double Opacity => Watch == IntelWatchMark.Ignored ? IntelEventRowVm.IgnoredOpacity : 1.0;

    public IBrush TierBrush { get; }
    public string StandingsTip { get; }

    public int SystemId { get; }
    public string SystemName { get; }
    /// <summary>Jumps from the top bar's origin, for sorting; null while not known.</summary>
    public int? Jumps { get; }
    public string JumpsText { get; }
    public bool HasJumps => JumpsText.Length > 0;
    public string? JumpsTip { get; }
    public IntelJumpBand Band { get; }
    public bool IsHere => Band == IntelJumpBand.Here;
    public bool IsNear => Band == IntelJumpBand.Near;
    public bool IsMid => Band == IntelJumpBand.Mid;
    public bool IsFar => Band == IntelJumpBand.Far;
    public bool IsPending => Band == IntelJumpBand.Unknown && HasJumps;

    /// <summary>When, in EVE time, with the date: the tab goes back sixty days.</summary>
    public string TimeText { get; }
    public DateTimeOffset SeenAt => _seenAt;

    private string _ageText = "";
    public string AgeText { get => _ageText; private set => this.RaiseAndSetIfChanged(ref _ageText, value); }

    public IntelPictureVm? Hull { get; }
    public bool HasHull => Hull is not null;

    public string SourceText { get; }
    public string SourceTip { get; }
    public int KillMailId { get; }

    public IntelPictureVm? Corporation { get; }
    public IntelPictureVm? Alliance { get; }
    public bool HasCorporation => Corporation is not null;
    public bool HasAlliance => Alliance is not null;

    public void Tick(DateTimeOffset now) => AgeText = IntelWords.LongAge(_seenAt, now);

    public Task LoadPicturesAsync() => Task.WhenAll(new[] { Pilot, Hull, Corporation, Alliance }
        .Where(p => p is not null).Select(p => p!.LoadAsync()));

    public void OpenPilot() => EntityNavigator.Instance.Entity(EntityKind.Pilot, CharacterId);
    public void OpenSystem() => EntityNavigator.Instance.System(SystemId);
    public void ShowOnMap() => Actions.ShowOnMap?.Invoke(SystemId);
    public bool CanShowOnMap => Actions.ShowOnMap is not null;

    /// <summary>How they were seen: the kill in Battle Report, or the system's page for a report or a paste.</summary>
    public void OpenSource()
    {
        if (KillMailId > 0) EntityNavigator.Instance.BattleReport(KillMailId);
        else OpenSystem();
    }

    public ReactiveCommand<Unit, Unit> OpenPilotCommand => _openPilot ??= ReactiveCommand.Create(OpenPilot);
    private ReactiveCommand<Unit, Unit>? _openPilot;
    public ReactiveCommand<Unit, Unit> OpenSystemCommand => _openSystem ??= ReactiveCommand.Create(OpenSystem);
    private ReactiveCommand<Unit, Unit>? _openSystem;
    public ReactiveCommand<Unit, Unit> OpenSourceCommand => _openSource ??= ReactiveCommand.Create(OpenSource);
    private ReactiveCommand<Unit, Unit>? _openSource;
}

/// <summary>A column header that sorts its grid: clicked, by its column; clicked again, the other way round. The
/// arrow shows which column sorts and which way. What the header template binds to; each grid's headers are an
/// <see cref="IntelSortHeaderVm{TColumn}"/> of its own columns.</summary>
public abstract class IntelSortHeaderVm : ReactiveObject
{
    protected IntelSortHeaderVm(string text, Action sort)
    {
        Text = text;
        SortCommand = ReactiveCommand.Create(sort);
    }

    public string Text { get; }
    public ReactiveCommand<Unit, Unit> SortCommand { get; }

    private bool _isUp, _isDown;
    /// <summary>Sorted by this column, A to Z (or nearest, or oldest) first.</summary>
    public bool IsUp { get => _isUp; private set => this.RaiseAndSetIfChanged(ref _isUp, value); }
    /// <summary>Sorted by this column, Z to A (or farthest, or newest) first.</summary>
    public bool IsDown { get => _isDown; private set => this.RaiseAndSetIfChanged(ref _isDown, value); }
    public bool IsSorted => IsUp || IsDown;

    protected void ShowArrow(bool sorted, bool descending)
    {
        IsUp = sorted && !descending;
        IsDown = sorted && descending;
        this.RaisePropertyChanged(nameof(IsSorted));
    }
}

/// <summary>A header of one grid's columns: the Pilots tab's, the Camps tab's, the Watch list's.</summary>
public sealed class IntelSortHeaderVm<TColumn> : IntelSortHeaderVm where TColumn : struct, Enum
{
    public IntelSortHeaderVm(TColumn column, string text, Action<TColumn> sort) : base(text, () => sort(column)) => Column = column;

    public TColumn Column { get; }

    /// <summary>The arrow on this header when <paramref name="sorted"/> is its column, the way round given; none otherwise.</summary>
    public void Show(TColumn sorted, bool descending) => ShowArrow(EqualityComparer<TColumn>.Default.Equals(sorted, Column), descending);

    /// <summary>One header per column, in the enum's order, each with its arrow as the grid sorts now.</summary>
    public static IReadOnlyList<IntelSortHeaderVm<TColumn>> For(Func<TColumn, string> name, Action<TColumn> sort, TColumn sorted, bool descending)
    {
        var heads = Enum.GetValues<TColumn>().Select(c => new IntelSortHeaderVm<TColumn>(c, name(c), sort)).ToList();
        foreach (var h in heads) h.Show(sorted, descending);
        return heads;
    }
}

/// <summary>How a grid's sort is kept on this PC: "Column,asc" or "Column,desc" under a key of the view's own.</summary>
public static class IntelGridSort
{
    /// <summary>The kept sort, or <paramref name="fallback"/> when none is kept, it cannot be read, or its column is gone.</summary>
    public static (TColumn Column, bool Descending) Load<TColumn>(string key, (TColumn Column, bool Descending) fallback) where TColumn : struct, Enum
    {
        string? kept;
        try { kept = UiState.Get(key); }
        catch { return fallback; }
        return kept?.Split(',') is [var column, var direction] && Enum.TryParse<TColumn>(column, out var c) && Enum.IsDefined(c)
            ? (c, direction == "desc") : fallback;
    }

    public static void Save<TColumn>(string key, TColumn column, bool descending) where TColumn : struct, Enum
    {
        try { UiState.Set(key, column + "," + (descending ? "desc" : "asc")); }
        catch { /* A view's state is a nicety: a setting that cannot be kept is simply not kept. */ }
    }
}

/// <summary>
/// One gate camp on the Intel tool's Camps tab: where, on which gate, how far, active or ended, how many
/// kills and when the last, who is on it and in what, what they fit, and how it is known.
/// </summary>
public sealed class IntelCampRowVm : ReactiveObject
{
    private const int MaxHulls = 3;
    private readonly DateTimeOffset _lastAt, _endsAt;

    public IntelCampRowVm(IntelCampRow row, IntelSnapshot snapshot, bool pending, IntelRowActions actions, DateTimeOffset now)
    {
        var culture = CultureInfo.CurrentCulture;
        Actions = actions;
        Key = row.Key;
        SystemId = row.SystemId;
        SystemName = snapshot.SystemName(row.SystemId);
        var camp = row.Camp;
        GateText = camp is not null ? string.Format(culture, IntelText.CampGate, snapshot.SystemName(camp.GateSystemId))
                 : row.ReportedGate is { Length: > 0 } said ? string.Format(culture, IntelText.CampGate, said)
                 : "";
        Jumps = row.Jumps;
        JumpsText = IntelWords.Jumps(row.Jumps, pending);
        Band = row.Jumps is null && pending ? IntelJumpBand.Unknown : IntelWords.Band(row.Jumps);
        var from = row.NearestOrigin is long o ? snapshot.Standings.Own(o)?.Name ?? snapshot.Own.FirstOrDefault(x => x.CharacterId == o)?.Name : null;
        JumpsTip = IntelWords.JumpsTip(row.Jumps, row.ViaWormhole, from);
        TierBrush = IntelTierColors.Of(row.Tier);
        StandingsTip = IntelWords.StandingsTip(snapshot.Standings, row.StandingsFrom);
        Watch = row.Watch;

        _lastAt = row.LastAt;
        _endsAt = row.EndsAt;
        LastKillId = camp?.KillMailIds.LastOrDefault() ?? 0;
        KillCount = camp?.KillMailIds.Count;
        AttackerCount = camp is null || camp.Attackers.Count == 0 ? null : camp.Attackers.Count;
        KillsText = camp is null ? "–" : Plurals.Format(IntelText.ResourceManager, nameof(IntelText.CampKillsOther), camp.KillMailIds.Count);
        AttackersText = camp is null || camp.Attackers.Count == 0 ? ""
            : Plurals.Format(IntelText.ResourceManager, nameof(IntelText.CampAttackersOther), camp.Attackers.Count);

        // The hulls on it, most flown first, each with its icon; names from the camp's own line.
        if (camp is not null)
        {
            var named = snapshot.Feed.Where(e => e.CampKey == camp.Key).SelectMany(e => e.Pilots)
                .Where(p => p.ShipTypeId is > 0 && !string.IsNullOrEmpty(p.Ship))
                .GroupBy(p => p.ShipTypeId!.Value).ToDictionary(g => g.Key, g => g.First().Ship!);
            var hulls = camp.Attackers.Where(a => a.ShipTypeId is > 0).GroupBy(a => a.ShipTypeId!.Value)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
                .Select(g => IntelPictureVm.Ship(g.Key, named.TryGetValue(g.Key, out var n) ? n : SdeNames.Type(g.Key, g.Key.ToString(CultureInfo.InvariantCulture)), g.Count()))
                .ToList();
            Hulls = [.. hulls.Take(MaxHulls)];
            HullsMore = hulls.Count > MaxHulls ? "+" + (hulls.Count - MaxHulls).ToString(culture) : "";
        }
        else
        {
            Hulls = [];
            HullsMore = "";
        }
        FlagsText = camp is null ? "" : IntelWords.Flags(0, camp);
        SourceText = camp is null ? IntelText.CampSourceReported : row.Reported ? IntelText.CampSourceBoth : IntelText.CampSourceKills;

        Signature = string.Join("|", Key, JumpsText, row.Tier, Watch, GateText, KillsText, AttackersText, HullsMore, FlagsText, SourceText,
                                row.LastAt.ToUnixTimeSeconds(), string.Join(",", Hulls.Select(h => h.Id + ":" + h.Text)));
        Tick(now);
    }

    public IntelRowActions Actions { get; }
    public string Signature { get; }
    public string Key { get; }

    public int SystemId { get; }
    public string SystemName { get; }
    public string GateText { get; }
    public bool HasGate => GateText.Length > 0;

    /// <summary>For the sort: jumps from the nearest origin, null when not known.</summary>
    public int? Jumps { get; }
    public string JumpsText { get; }
    public bool HasJumps => JumpsText.Length > 0;
    public string? JumpsTip { get; }
    public IntelJumpBand Band { get; }
    public bool IsHere => Band == IntelJumpBand.Here;
    public bool IsNear => Band == IntelJumpBand.Near;
    public bool IsMid => Band == IntelJumpBand.Mid;
    public bool IsFar => Band == IntelJumpBand.Far;
    public bool IsPending => Band == IntelJumpBand.Unknown && HasJumps;

    public IBrush TierBrush { get; }
    public string StandingsTip { get; }

    public IntelWatchMark Watch { get; }
    public FontWeight NameWeight => Watch == IntelWatchMark.Always ? FontWeight.ExtraBold : FontWeight.SemiBold;

    /// <summary>For the sort: the last kill, or the last report of a camp only reported.</summary>
    public DateTimeOffset LastAt => _lastAt;
    /// <summary>For the sort: kills on it; null for a camp only reported.</summary>
    public int? KillCount { get; }
    /// <summary>For the sort: pilots on it; null when none are known.</summary>
    public int? AttackerCount { get; }

    public string KillsText { get; }
    public int LastKillId { get; }
    public bool HasKills => LastKillId > 0;
    public string AttackersText { get; }
    public IReadOnlyList<IntelPictureVm> Hulls { get; }
    public string HullsMore { get; }
    public bool HasHullsMore => HullsMore.Length > 0;
    public string FlagsText { get; }
    public string SourceText { get; }

    private bool _isActive;
    /// <summary>Within the camp lifetime of its last kill; after that, ended, and faded.</summary>
    public bool IsActive
    {
        get => _isActive;
        private set
        {
            if (_isActive == value && StateText.Length > 0) return;
            this.RaiseAndSetIfChanged(ref _isActive, value);
            StateText = value ? IntelText.CampStateActive : IntelText.CampStateEnded;
        }
    }

    private string _stateText = "";
    public string StateText { get => _stateText; private set => this.RaiseAndSetIfChanged(ref _stateText, value); }

    private string _ageText = "";
    public string AgeText { get => _ageText; private set => this.RaiseAndSetIfChanged(ref _ageText, value); }

    private double _opacity = 1;
    public double Opacity { get => _opacity; private set => this.RaiseAndSetIfChanged(ref _opacity, value); }

    public void Tick(DateTimeOffset now)
    {
        IsActive = now < _endsAt;
        AgeText = IntelWords.Age(_lastAt, now);
        Opacity = (IsActive ? IntelLens.Opacity(_lastAt, _endsAt - _lastAt, now) : IntelLens.EndedCampOpacity)
                  * (Watch == IntelWatchMark.Ignored ? IntelEventRowVm.IgnoredOpacity : 1.0);
    }

    public Task LoadPicturesAsync() => Task.WhenAll(Hulls.Select(h => h.LoadAsync()));

    public void OpenSystem() => EntityNavigator.Instance.System(SystemId);
    public void OpenKills() { if (LastKillId > 0) EntityNavigator.Instance.BattleReport(LastKillId); }
    public void ShowOnMap() => Actions.ShowOnMap?.Invoke(SystemId);
    public bool CanShowOnMap => Actions.ShowOnMap is not null;

    public ReactiveCommand<Unit, Unit> OpenSystemCommand => _openSystem ??= ReactiveCommand.Create(OpenSystem);
    private ReactiveCommand<Unit, Unit>? _openSystem;
    public ReactiveCommand<Unit, Unit> OpenKillsCommand => _openKills ??= ReactiveCommand.Create(OpenKills);
    private ReactiveCommand<Unit, Unit>? _openKills;
}

/// <summary>Something that can go on the watch list, as the add box offers it.</summary>
/// <param name="Name">As stored: a hull's or group's English name, a pilot's, corporation's or alliance's own.</param>
/// <param name="Label">As shown: in the interface language.</param>
public sealed record IntelWatchCandidate(string Kind, long Id, string Name, string Label, string Detail)
{
    public override string ToString() => Label;
}

/// <summary>One entry on the Watch list tab: what it is, with its picture, its mode (changed in place), its
/// note, and when and where it was added.</summary>
public sealed class IntelWatchEntryVm : ReactiveObject
{
    private readonly IntelWatchTabVm _owner;

    public IntelWatchEntryVm(IntelWatchEntry e, IntelWatchTabVm owner)
    {
        _owner = owner;
        Id = e.Id;
        Kind = e.Kind;
        EntityId = e.EntityId;
        Note = e.Note ?? "";
        var label = e.Kind switch
        {
            IntelWatchKind.Hull  => SdeNames.Type((int)e.EntityId, e.Name),
            IntelWatchKind.Group => SdeNames.Group((int)e.EntityId, e.Name),
            _                    => e.Name,
        };
        Picture = e.Kind switch
        {
            IntelWatchKind.Pilot       => IntelPictureVm.Pilot(e.EntityId, label),
            IntelWatchKind.Corporation => IntelPictureVm.Corporation(e.EntityId, label),
            IntelWatchKind.Alliance    => IntelPictureVm.Alliance(e.EntityId, label),
            IntelWatchKind.Hull        => IntelPictureVm.Ship((int)e.EntityId, label),
            _                          => IntelPictureVm.Words(label),
        };
        Label = label;
        AddedAt = e.AddedAt;
        KindText = IntelWatchTabVm.KindName(e.Kind);
        _selectedMode = owner.ModeChoices.FirstOrDefault(m => m.Value == e.Mode) ?? owner.ModeChoices[0];
        AddedText = IntelSnapshotBuilder.TryParse(e.AddedAt, out var at)
            ? string.Format(CultureInfo.CurrentCulture, IntelText.WatchAdded, at.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), e.AddedBy)
            : e.AddedBy;
        Signature = string.Join("|", Id, Kind, EntityId, label, e.Mode, Note, AddedText);
        RemoveCommand = ReactiveCommand.Create(() => owner.Remove(this));
    }

    public string Signature { get; }
    public int Id { get; }
    public string Kind { get; }
    public long EntityId { get; }
    public IntelPictureVm Picture { get; }
    /// <summary>The name as shown, in the interface language for a hull or group: what Name sorts by.</summary>
    public string Label { get; }
    public string KindText { get; }
    public string Note { get; }
    public bool HasNote => Note.Length > 0;
    public string AddedText { get; }
    /// <summary>ISO text, as stored: what Added sorts by.</summary>
    public string AddedAt { get; }

    public IReadOnlyList<IntelChoice<int>> ModeChoices => _owner.ModeChoices;

    private IntelChoice<int> _selectedMode;
    /// <summary>Always alert or Ignore: changed in place, written at once.</summary>
    public IntelChoice<int> SelectedMode
    {
        get => _selectedMode;
        set
        {
            if (value is null || value.Value == _selectedMode.Value) return;
            this.RaiseAndSetIfChanged(ref _selectedMode, value);
            _owner.ChangeMode(this, value.Value);
        }
    }

    public ReactiveCommand<Unit, Unit> RemoveCommand { get; }
}

/// <summary>
/// The Intel tool's Watch list tab: the shared list of pilots, corporations, alliances, hulls and ship groups
/// always alerted for or ignored, and a box to add to it. Every read and write is off the UI thread.
/// </summary>
public sealed class IntelWatchTabVm : ReactiveObject
{
    private readonly IntelWatchList? _list;
    private readonly IDbContextFactory<AppDbContext>? _dbFactory;
    private long _loads;

    public IntelWatchTabVm(IntelWatchList? list, IDbContextFactory<AppDbContext>? dbFactory)
    {
        _list = list;
        _dbFactory = dbFactory;
        KindChoices = [.. IntelWatchKind.All.Select(k => new IntelChoice<string>(k, KindName(k)))];
        ModeChoices =
        [
            new(IntelWatchMode.Always, IntelText.WatchModeAlways),
            new(IntelWatchMode.Ignore, IntelText.WatchModeIgnore),
        ];
        _selectedKind = KindChoices[0];
        _selectedMode = ModeChoices[0];
        AddCommand = ReactiveCommand.Create(Add, this.WhenAnyValue(x => x.SelectedCandidate).Select(c => c is not null && _list is not null));
        (_sort, _sortDescending) = IntelGridSort.Load(KeySort, (WatchColumn.Kind, false));
        Headers = IntelSortHeaderVm<WatchColumn>.For(ColumnName, SortBy, _sort, _sortDescending);

        this.WhenAnyValue(x => x.SearchText).Skip(1).Throttle(TimeSpan.FromMilliseconds(250))
            .SelectMany(t => Observable.FromAsync(() => SuggestAsync(t))).Subscribe();
    }

    public static string KindName(string kind) => kind switch
    {
        IntelWatchKind.Pilot       => IntelText.WatchKindPilot,
        IntelWatchKind.Corporation => IntelText.WatchKindCorporation,
        IntelWatchKind.Alliance    => IntelText.WatchKindAlliance,
        IntelWatchKind.Hull        => IntelText.WatchKindHull,
        _                          => IntelText.WatchKindGroup,
    };

    public bool CanEdit => _list is not null;

    public IReadOnlyList<IntelChoice<string>> KindChoices { get; }
    public IReadOnlyList<IntelChoice<int>> ModeChoices { get; }

    private IntelChoice<string> _selectedKind;
    public IntelChoice<string> SelectedKind
    {
        get => _selectedKind;
        set
        {
            if (value is null || value == _selectedKind) return;
            this.RaiseAndSetIfChanged(ref _selectedKind, value);
            Candidates.Clear();
            SelectedCandidate = null;
            _ = SuggestAsync(SearchText);
        }
    }

    private string _searchText = "";
    public string SearchText { get => _searchText; set => this.RaiseAndSetIfChanged(ref _searchText, value); }

    public ObservableCollection<IntelWatchCandidate> Candidates { get; } = [];

    private IntelWatchCandidate? _selectedCandidate;
    public IntelWatchCandidate? SelectedCandidate
    {
        get => _selectedCandidate;
        set => this.RaiseAndSetIfChanged(ref _selectedCandidate, value);
    }

    private IntelChoice<int> _selectedMode;
    public IntelChoice<int> SelectedMode
    {
        get => _selectedMode;
        set { if (value is not null) this.RaiseAndSetIfChanged(ref _selectedMode, value); }
    }

    private string _noteText = "";
    public string NoteText { get => _noteText; set => this.RaiseAndSetIfChanged(ref _noteText, value); }

    public ReactiveCommand<Unit, Unit> AddCommand { get; }

    public BulkObservableCollection<IntelWatchEntryVm> Entries { get; } = [];

    private string _emptyText = "";
    public string EmptyText { get => _emptyText; private set => this.RaiseAndSetIfChanged(ref _emptyText, value); }

    private string _statusText = "";
    public string StatusText { get => _statusText; private set { this.RaiseAndSetIfChanged(ref _statusText, value); this.RaisePropertyChanged(nameof(HasStatus)); } }
    public bool HasStatus => StatusText.Length > 0;

    /// <summary>Reads the list again, off the UI thread.</summary>
    public void Load()
    {
        if (_list is null) { Show([]); return; }
        var ticket = Interlocked.Increment(ref _loads);
        _ = Task.Run(async () =>
        {
            try
            {
                var rows = await _list.ReadAsync().ConfigureAwait(false);
                Dispatcher.UIThread.Post(() => { if (Interlocked.Read(ref _loads) == ticket) Show(rows); });
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => StatusText = string.Format(CultureInfo.CurrentCulture, IntelText.WatchFailed, ex.Message));
            }
        });
    }

    /// <summary>The entries given rather than read: for a check with invented ones.</summary>
    /// <remarks>An entry reading as one already shown is that one, picture and all: a reading that changed nothing
    /// changes nothing on screen, and a changed entry is replaced alone (before, every read reset the list, and every
    /// row was drawn again).</remarks>
    public void Show(IReadOnlyList<IntelWatchEntry> rows)
    {
        var held = Entries.ToDictionary(e => e.Id);
        var fresh = rows
            .Select(r =>
            {
                var vm = new IntelWatchEntryVm(r, this);
                return held.TryGetValue(r.Id, out var old) && old.Signature == vm.Signature ? old : vm;
            })
            .ToList();
        Entries.SyncTo([.. SortWatch(fresh, _sort, _sortDescending)], e => e.Id, e => e.Signature);
        foreach (var e in fresh.Where(e => !held.TryGetValue(e.Id, out var old) || !ReferenceEquals(old, e)))
            _ = e.Picture.LoadAsync();
        EmptyText = fresh.Count == 0 ? IntelText.WatchEmpty : "";
    }

    // ── The sort ─────────────────────────────────────────────────────────────

    private const string KeySort = "intel.watch_sort";

    /// <summary>A column of the Watch list, each sortable by clicking its header.</summary>
    public enum WatchColumn { Kind, Name, Mode, Note, Added }

    private WatchColumn _sort;
    private bool _sortDescending;

    public WatchColumn Sort => _sort;
    public bool SortDescending => _sortDescending;

    /// <summary>The headers, in <see cref="WatchColumn"/> order: each sorts by its column.</summary>
    public IReadOnlyList<IntelSortHeaderVm<WatchColumn>> Headers { get; }

    // Each header by name: a compiled binding reads a property, not an index into a read-only list.
    public IntelSortHeaderVm HeadKind  => Headers[(int)WatchColumn.Kind];
    public IntelSortHeaderVm HeadName  => Headers[(int)WatchColumn.Name];
    public IntelSortHeaderVm HeadMode  => Headers[(int)WatchColumn.Mode];
    public IntelSortHeaderVm HeadNote  => Headers[(int)WatchColumn.Note];
    public IntelSortHeaderVm HeadAdded => Headers[(int)WatchColumn.Added];

    private static string ColumnName(WatchColumn column) => column switch
    {
        WatchColumn.Kind => IntelText.ColKind,
        WatchColumn.Name => IntelText.ColName,
        WatchColumn.Mode => IntelText.ColMode,
        WatchColumn.Note => IntelText.ColNote,
        _                => IntelText.ColAdded,
    };

    /// <summary>A header clicked: its column, or the other way round when it already sorts by it. The entries shown
    /// are put in the new order; nothing is read again or built again.</summary>
    public void SortBy(WatchColumn column)
    {
        (_sort, _sortDescending) = NextWatchSort(_sort, _sortDescending, column);
        IntelGridSort.Save(KeySort, _sort, _sortDescending);
        foreach (var h in Headers) h.Show(_sort, _sortDescending);
        Entries.SyncTo([.. SortWatch(Entries.ToList(), _sort, _sortDescending)], e => e.Id, e => e.Signature);
    }

    /// <summary>The sort after a header is clicked: the same column turns round; another starts newest first for
    /// when they were added, and A to Z (the list's own kind order for Kind, Always alert before Ignore) for the rest.</summary>
    public static (WatchColumn Column, bool Descending) NextWatchSort(WatchColumn current, bool descending, WatchColumn clicked) =>
        clicked == current ? (current, !descending) : (clicked, clicked == WatchColumn.Added);

    /// <summary>
    /// The entries in a column's order. Kind is the list's own order of kinds (pilots, corporations, alliances, hulls,
    /// groups); a blank note goes last either way round; entries alike in the column keep kind, then name order.
    /// </summary>
    public static IEnumerable<IntelWatchEntryVm> SortWatch(IEnumerable<IntelWatchEntryVm> entries, WatchColumn column, bool descending)
    {
        var text = StringComparer.CurrentCultureIgnoreCase;
        var kinds = IntelWatchKind.All.ToList();
        int KindOrder(IntelWatchEntryVm e) => kinds.IndexOf(e.Kind) is var i and >= 0 ? i : kinds.Count;
        IOrderedEnumerable<IntelWatchEntryVm> By<TKey>(Func<IntelWatchEntryVm, TKey> key, IComparer<TKey>? comparer = null) =>
            descending ? entries.OrderByDescending(key, comparer) : entries.OrderBy(key, comparer);
        var sorted = column switch
        {
            WatchColumn.Kind  => By(KindOrder),
            WatchColumn.Name  => By(e => e.Label, text),
            WatchColumn.Mode  => By(e => e.SelectedMode.Value),
            WatchColumn.Note  => descending
                ? entries.OrderBy(e => e.HasNote ? 0 : 1).ThenByDescending(e => e.Note, text)
                : entries.OrderBy(e => e.HasNote ? 0 : 1).ThenBy(e => e.Note, text),
            _                 => By(e => e.AddedAt, StringComparer.Ordinal),
        };
        return sorted.ThenBy(KindOrder).ThenBy(e => e.Label, text).ThenBy(e => e.Id);
    }

    private void Add()
    {
        if (_list is not { } list || SelectedCandidate is not { } c) return;
        var mode = SelectedMode.Value;
        var note = NoteText;
        SearchText = "";
        SelectedCandidate = null;
        Candidates.Clear();
        NoteText = "";
        Write(() => list.SetAsync(c.Kind, c.Id, c.Name, mode, note));
    }

    internal void ChangeMode(IntelWatchEntryVm entry, int mode)
    {
        if (_list is { } list) Write(() => list.UpdateAsync(entry.Id, mode, entry.Note));
    }

    internal void Remove(IntelWatchEntryVm entry)
    {
        if (_list is { } list) Write(() => list.RemoveAsync(entry.Kind, entry.EntityId));
    }

    /// <summary>A change, off the UI thread; the list is read again after it, here, and the snapshot hears of it.</summary>
    private void Write(Func<Task> change) => _ = Task.Run(async () =>
    {
        try
        {
            await change().ConfigureAwait(false);
            Dispatcher.UIThread.Post(() => StatusText = "");
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => StatusText = string.Format(CultureInfo.CurrentCulture, IntelText.WatchFailed, ex.Message));
        }
        Dispatcher.UIThread.Post(Load);
    });

    /// <summary>What the box offers for the kind chosen: names starting with what was typed, then (from three
    /// characters) containing it; hulls and groups also by their name in the interface language.</summary>
    private async Task SuggestAsync(string? text)
    {
        var term = (text ?? "").Trim();
        if (_dbFactory is null || term.Length < 2 || term == SelectedCandidate?.Label) return;
        var kind = SelectedKind.Value;
        try
        {
            var found = await Task.Run(() => CandidatesAsync(_dbFactory, kind, term)).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (kind != SelectedKind.Value || SearchText.Trim() != term) return;
                Candidates.Clear();
                foreach (var c in found) Candidates.Add(c);
            });
        }
        catch
        {
            // The old suggestions stay; the next keystroke asks again.
        }
    }

    private const int ShipCategoryId = 6;
    private const int MaxCandidates = 30;

    public static async Task<List<IntelWatchCandidate>> CandidatesAsync(IDbContextFactory<AppDbContext> dbFactory, string kind, string term,
                                                                         CancellationToken ct = default)
    {
        var lower = term.ToLowerInvariant();
        var detail = KindName(kind);
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var found = new List<IntelWatchCandidate>();
        switch (kind)
        {
            case IntelWatchKind.Pilot:
            {
                var named = await db.UniverseNames.AsNoTracking()
                    .Where(u => u.Category == "character" && u.Name.ToLower().StartsWith(lower))
                    .Select(u => new { u.EntityId, u.Name }).Take(MaxCandidates).ToListAsync(ct).ConfigureAwait(false);
                var seen = await db.IntelLastSeens.AsNoTracking()
                    .Where(r => r.Name != null && r.Name.ToLower().StartsWith(lower))
                    .Select(r => new { EntityId = r.CharacterId, Name = r.Name! }).Take(MaxCandidates).ToListAsync(ct).ConfigureAwait(false);
                found.AddRange(named.Concat(seen).GroupBy(n => n.EntityId).Select(g => g.First())
                    .Select(n => new IntelWatchCandidate(kind, n.EntityId, n.Name, n.Name, detail)));
                break;
            }
            case IntelWatchKind.Corporation or IntelWatchKind.Alliance:
            {
                var category = kind == IntelWatchKind.Corporation ? "corporation" : "alliance";
                var contains = term.Length >= IntelPilotSearch.ContainsFrom;
                var rows = await db.UniverseNames.AsNoTracking()
                    .Where(u => u.Category == category && (contains ? u.Name.ToLower().Contains(lower) : u.Name.ToLower().StartsWith(lower)))
                    .Select(u => new { u.EntityId, u.Name }).Take(MaxCandidates * 2).ToListAsync(ct).ConfigureAwait(false);
                found.AddRange(rows.Select(n => new IntelWatchCandidate(kind, n.EntityId, n.Name, n.Name, detail)));
                break;
            }
            case IntelWatchKind.Hull:
            {
                await SdeNames.EnsureLoadedAsync(ct).ConfigureAwait(false);
                var shown = SdeNames.Find(SdeNameKind.Type, term).Select(id => (int)id).ToList();
                var rows = await (from t in db.SdeTypes.AsNoTracking()
                                  join g in db.SdeGroups.AsNoTracking() on t.GroupId equals g.GroupId
                                  where g.CategoryId == ShipCategoryId && t.Published
                                        && (t.Name.ToLower().Contains(lower) || shown.Contains(t.TypeId))
                                  select new { t.TypeId, t.Name }).Take(MaxCandidates * 2).ToListAsync(ct).ConfigureAwait(false);
                found.AddRange(rows.Select(t => new IntelWatchCandidate(kind, t.TypeId, t.Name, SdeNames.Type(t.TypeId, t.Name), detail)));
                break;
            }
            default:
            {
                await SdeNames.EnsureLoadedAsync(ct).ConfigureAwait(false);
                var shown = SdeNames.Find(SdeNameKind.Group, term).Select(id => (int)id).ToList();
                var rows = await db.SdeGroups.AsNoTracking()
                    .Where(g => g.CategoryId == ShipCategoryId && g.Published && (g.Name.ToLower().Contains(lower) || shown.Contains(g.GroupId)))
                    .Select(g => new { g.GroupId, g.Name }).Take(MaxCandidates).ToListAsync(ct).ConfigureAwait(false);
                found.AddRange(rows.Select(g => new IntelWatchCandidate(IntelWatchKind.Group, g.GroupId, g.Name, SdeNames.Group(g.GroupId, g.Name), detail)));
                break;
            }
        }
        // Names starting with what was typed first, then the rest, each alphabetical.
        return [.. found
            .OrderBy(c => c.Label.StartsWith(term, StringComparison.CurrentCultureIgnoreCase) ? 0 : 1)
            .ThenBy(c => c.Label, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaxCandidates)];
    }
}
