using System.Collections.ObjectModel;
using EveConsole.Services;
using System.Reactive;
using Avalonia.Collections;
using Avalonia.Threading;
using EveConsole.Services.Worklist;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>One line in a summary-panel section: a label and its figure.</summary>
public sealed record SummaryStatVm(string Label, string Value);

/// <summary>One row on the worklist.</summary>
public class WorklistRowVm : ReactiveObject
{
    private readonly WorklistItem _item;

    public WorklistRowVm(WorklistItem item, int sequence)
    {
        _item    = item;
        Sequence = sequence;
    }

    /// <summary>
    /// This row's place in the default priority order, numbered from one.
    ///
    /// <para>Sorting a column throws that order away, and the ranking is the tool's actual output —
    /// it is not recoverable by sorting on anything else, because it is a blend of readiness,
    /// priority, character and title. Carrying it as a column means sorting on it gets you back.</para>
    /// </summary>
    public int Sequence { get; }

    public string Key           => _item.Key;
    public string Title         => _item.Title;
    public string Detail        => _item.Detail;
    public string SourceName    => _item.Source;
    public string CharacterName => _item.CharacterName.Length > 0 ? _item.CharacterName : "—";
    public string LocationName  => _item.LocationName;

    // ── Links ─────────────────────────────────────────────────────────────────
    //
    // Every id was already on the item for routing, so nothing new is fetched. A zero means the
    // generator could not route the task — an unrouted item still shows, its name just is not a
    // link. ⚠️ The em dash CharacterName falls back to is not a name, so HasCharacterLink tests
    // the id rather than the displayed text.
    public bool HasCharacterLink => _item.CharacterId > 0 && _item.CharacterName.Length > 0;
    public bool HasLocationLink  => _item.LocationId  > 0 && _item.LocationName.Length  > 0;
    public bool HasItemLink      => _item.TypeId      > 0 && _item.TypeName.Length      > 0;

    public void OpenCharacter() =>
        EntityNavigator.Instance.Entity(EntityKind.Pilot, _item.CharacterId);

    /// <summary>⚠️ Station versus structure by int range: SdeStations keys on an int, so an id
    /// above that range cannot be a station. Worklist locations carry no discriminator.</summary>
    public void OpenLocation() => OpenPlace(_item.LocationId);
    public void OpenDestination() => OpenPlace(_item.DestinationId);

    private static void OpenPlace(long id)
    {
        if (id <= 0) return;
        if (id <= int.MaxValue) EntityNavigator.Instance.Entity(EntityKind.Station, id);
        else                    EntityNavigator.Instance.Structure(id);
    }

    public void OpenItem() => EntityNavigator.Instance.Item(_item.TypeId);
    public int    TypeId        => _item.TypeId;
    public int    Priority      => _item.Priority;
    public bool   IsSnoozed     => _item.IsSnoozed;
    public double Value         => _item.Value;
    public double VolumeRaw     => _item.Volume;
    public IndustryPool? Pool   => _item.Pool;

    /// <summary>The kind of doing, as its own scannable column.</summary>
    public string KindText => _item.Kind switch
    {
        WorklistKind.Buy         => "Buy",
        WorklistKind.Haul        => "Haul",
        WorklistKind.Refine      => "Refine",
        WorklistKind.Decompress  => "Decompress",
        WorklistKind.Job         => "Job",
        WorklistKind.AssetSafety => "Asset Safety",
        WorklistKind.SkillQueue  => "Skill Queue",
        _                        => "Corp Project",
    };

    /// <summary>
    /// What kind of work this is, at a glance, before the row is read.
    ///
    /// <para>⚠️ Drawn rather than fetched. EVE's image server serves types, characters and corps —
    /// there is no endpoint for "manufacturing" or "reaction", and the client's own activity icons
    /// are CCP's art rather than ours to redistribute. These are simple shapes on the same 16-unit
    /// grid, which also means they stay crisp at row height and take their colour from the theme
    /// instead of arriving as a fixed-colour bitmap.</para>
    ///
    /// <para>A job is split by the slot pool it occupies, because "run a reaction" and "copy a
    /// blueprint" are different errands in different places — the distinction the group header
    /// alone cannot make once the list is sorted by anything else.</para>
    /// </summary>
    public string KindGlyph => (_item.Kind, _item.Pool) switch
    {
        // Cart: something to acquire.
        (WorklistKind.Buy, _) =>
            "M2,3 H4.5 L6.5,10.5 H13 L14.5,5.5 H5.5 M7,13 A1,1 0 1,0 7,12.9 M12,13 A1,1 0 1,0 12,12.9",

        // Arrow between two points: something to move.
        (WorklistKind.Haul, _) =>
            "M2,8 H11 M8.5,5 L12,8 L8.5,11 M13.5,4 V12",

        // A rock breaking into pieces: reprocessing.
        (WorklistKind.Refine, _) =>
            "M8,1.5 L13,4.5 V10 L8,13.5 L3,10 V4.5 Z M3,4.5 L8,7.5 L13,4.5 M8,7.5 V13.5",

        // Opening outward: decompression.
        (WorklistKind.Decompress, _) =>
            "M4,7 H2 M12,7 H14 M4,9.5 H2 M12,9.5 H14 M5.5,3.5 L8,1.5 L10.5,3.5 " +
            "M5,6 H11 V11 H5 Z",

        // Factory roofline.
        (WorklistKind.Job, IndustryPool.Manufacturing) =>
            "M2,13 V6 L6,8.5 V6 L10,8.5 V6 L14,8.5 V13 Z",

        // ⚠️ Atom, not a flask. The flask is what EVE draws for science, so using it here read as
        // "copying" on every reaction row — the two got swapped on first writing. In the client's
        // facility list the reaction icon is the last of the activity marks and is the round one.
        (WorklistKind.Job, IndustryPool.Reaction) =>
            "M8,6.75 A1.25,1.25 0 1,0 8,9.25 A1.25,1.25 0 1,0 8,6.75 " +
            "M4.3,11.7 A5.2,2.4 45 1,1 11.7,4.3 A5.2,2.4 45 1,1 4.3,11.7 " +
            "M4.3,4.3 A5.2,2.4 -45 1,1 11.7,11.7 A5.2,2.4 -45 1,1 4.3,4.3",

        // Flask: copying and invention, which share the science slots — and which is what the
        // client marks those activities with.
        (WorklistKind.Job, IndustryPool.Science) =>
            "M6.5,2 V6 L3,12.5 A1,1 0 0,0 4,14 H12 A1,1 0 0,0 13,12.5 L9.5,6 V2 Z M5.5,2 H10.5",

        // Gear, for a job whose pool is not known.
        (WorklistKind.Job, _) =>
            "M8,5.5 A2.5,2.5 0 1,0 8,10.5 A2.5,2.5 0 1,0 8,5.5 M8,1.5 V3.5 M8,12.5 V14.5 " +
            "M1.5,8 H3.5 M12.5,8 H14.5 M3.5,3.5 L5,5 M11,11 L12.5,12.5 M12.5,3.5 L11,5 M5,11 L3.5,12.5",

        // Flag: a corp project.
        (WorklistKind.CorpProject, _) =>
            "M4,2 V14 M4,3 H13 L10.5,6 L13,9 H4",

        // Shield: asset safety.
        (WorklistKind.AssetSafety, _) =>
            "M8,2 L13.5,4 V8 C13.5,11 11,13.2 8,14 C5,13.2 2.5,11 2.5,8 V4 Z",

        // Rising bars: a skill queue.
        _ => "M3,13 V9.5 M6.5,13 V7 M10,13 V4.5 M13.5,13 V2",
    };

    /// <summary>
    /// What the glyph column sorts on: the same distinction the glyph draws, so rows showing the
    /// same icon land together.
    ///
    /// <para><see cref="KindRank"/> alone would scatter the three job icons, since it cannot see
    /// the pool. Kind leads so the order stays the enum's — buy, haul, job, and so on — with jobs
    /// sub-ordered manufacturing, reaction, science.</para>
    /// </summary>
    public int KindSort => (int)_item.Kind * 10 + _item.Pool switch
    {
        IndustryPool.Manufacturing => 1,
        IndustryPool.Reaction      => 2,
        IndustryPool.Science       => 3,
        _                          => 0,
    };

    /// <summary>Names the glyph, since a shape at row height can only hint.</summary>
    public string KindGlyphTip => (_item.Kind, _item.Pool) switch
    {
        (WorklistKind.Buy,  _)                        => "Buy",
        (WorklistKind.Haul, _)                        => "Haul",
        (WorklistKind.Refine, _)                      => "Reprocess ore",
        (WorklistKind.Decompress, _)                  => "Decompress gas",
        (WorklistKind.Job, IndustryPool.Manufacturing) => "Manufacturing job",
        (WorklistKind.Job, IndustryPool.Reaction)      => "Reaction job",
        (WorklistKind.Job, IndustryPool.Science)       => "Science job — copying or invention",
        (WorklistKind.Job, _)                          => "Industry job",
        (WorklistKind.CorpProject, _)                  => "Corp project",
        (WorklistKind.AssetSafety, _)                  => "Asset safety",
        _                                              => "Skill queue",
    };

    /// <summary>
    /// The print the job was planned against — "ME9 TE20" — or blank.
    ///
    /// <para>One string rather than two labels: the row's panel spaces its children eight pixels
    /// apart, which would read as two separate facts when the pair is how a blueprint is described
    /// everywhere else.</para>
    ///
    /// <para>⚠️ Manufacturing only. A reaction formula has no efficiency to speak of and a copy or
    /// invention job does not consume by it, so both figures would be a meaningless "ME0 TE0" on
    /// every one of those rows.</para>
    /// </summary>
    public string BlueprintMeText =>
        _item.Pool == IndustryPool.Manufacturing && _item.BlueprintMe is { } me
            ? $"ME{me} TE{_item.BlueprintTe ?? 0}"
            : "";

    public bool HasBlueprintMe => BlueprintMeText.Length > 0;

    /// <summary>Only a haul has a far end.</summary>
    public string DestinationName => _item.DestinationName;

    /// <summary>
    /// Rounded to whole cubic metres above a thousand — nobody loads a hauler to the decimal, and
    /// the column is read to judge how many trips it is.
    /// </summary>
    public string VolumeText => _item.Volume <= 0 ? ""
        : _item.Volume >= 1000 ? $"{_item.Volume:N0} m³"
        : $"{_item.Volume:N1} m³";

    /// <summary>The manifest, shown by expanding the row. Empty for single-item tasks.</summary>
    public IReadOnlyList<WorklistLine> Lines => _item.Lines;
    public bool HasLines => _item.Lines.Count > 0;

    /// <summary>
    /// Whether the manifest is showing. Collapsed by default and toggled by the row's own +/−,
    /// rather than following selection: a reader clicks a row to work on it as often as to look
    /// inside it, and having the manifest open and close underneath that click moves the list.
    /// </summary>
    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            this.RaiseAndSetIfChanged(ref _isExpanded, value);
            this.RaisePropertyChanged(nameof(ExpandGlyph));
        }
    }

    public string ExpandGlyph => _isExpanded ? "−" : "+";

    public string ReadinessText => _item.Readiness switch
    {
        WorklistReadiness.Ready   => "Ready",
        WorklistReadiness.Blocked => "Blocked",
        _                         => "Waiting",
    };

    // Which shape the row draws in. A DataGrid has one column set for every row, so per-type
    // presentation has to happen inside the cell — one panel per kind, only one of them visible.
    // Selection by kind rather than by CLR type because every row is the same class; splitting the
    // view model per kind would mean four near-identical types to keep in step.
    public bool IsHaul  => _item.Kind == WorklistKind.Haul;
    public bool IsJob   => _item.Kind == WorklistKind.Job;
    public bool IsBuy   => _item.Kind == WorklistKind.Buy;
    /// <summary>Reprocessing and decompressing share a shape: what to do, and the one station to
    /// do it at.</summary>
    public bool IsRefining => _item.Kind is WorklistKind.Refine or WorklistKind.Decompress;

    public bool IsOther => !IsHaul && !IsJob && !IsBuy && !IsRefining;

    /// <summary>Source and destination as one phrase, since a haul is the pairing rather than two
    /// independent facts.</summary>
    /// <summary>
    /// What the Task cell actually reads as, for sorting on it.
    ///
    /// <para>⚠️ The cell shows a different thing per kind — a haul shows its route, everything else
    /// its title — so sorting on Title alone rearranged haul rows by text the user could not see.
    /// Sorting has to follow what is on screen or it looks random.</para>
    /// </summary>
    public string TaskSortText => IsHaul ? RouteText : Title;

    public string RouteText => $"{_item.LocationName}  →  {_item.DestinationName}";

    /// <summary>What the task is worth — the goods hauled, the purchase, the job's output.
    ///
    /// <para>Blank rather than zero when unpriced. Science jobs are the case that matters: a
    /// blueprint copy has no stored value per ME level, so the honest answer is nothing at all
    /// rather than a confident 0 ISK.</para></summary>
    public string ValueText => _item.Value > 0 ? StationNeedRowVm.Isk(_item.Value) : "";

    /// <summary>
    /// Everything the row knows that is not worth a line of its own, gathered for one tooltip.
    ///
    /// <para>The detail runs to a paragraph on a buy — what it feeds, what is on hand, what is on
    /// order — which is genuinely useful and genuinely not wanted on 141 rows at once. Behind a
    /// marker it costs a hover for the reader who wants it and nothing for the reader who does
    /// not.</para>
    /// </summary>
    public string DetailTip
    {
        get
        {
            var parts = new List<string>(3);
            if (!string.IsNullOrWhiteSpace(_item.Detail)) parts.Add(_item.Detail);
            if (HasNote)   parts.Add(Note);
            if (IsSnoozed) parts.Add(SnoozeText);
            return string.Join("\n\n", parts);
        }
    }

    public bool HasDetailTip => DetailTip.Length > 0;

    /// <summary>
    /// Where this row's task sits in the group order — the declaration order of
    /// <see cref="WorklistKind"/>, which is the one place to change it.
    ///
    /// <para>Sorted on rather than <see cref="KindText"/> so the groups do not fall into
    /// alphabetical order, which would put Buy before Haul for no reason a reader could name.</para>
    /// </summary>
    public int KindRank => (int)_item.Kind;

    /// <summary>
    /// The row's leading edge bar.
    ///
    /// <para>⚠️ Ready is deliberately dim — near the row background rather than the green
    /// <see cref="ReadinessColor"/> uses for text. Ready is the overwhelming majority and the
    /// normal case, and a full-strength bar on all of it would restate the noise the State column
    /// was removed for. Blocked and waiting keep their full colour, so the eye lands on the rows
    /// that want something, and the rail still reads as continuous down the list.</para>
    /// </summary>
    public string ReadinessBarColor => _item.Readiness switch
    {
        WorklistReadiness.Ready   => "#24402c",
        WorklistReadiness.Blocked => "#c85a5a",
        _                         => "#c8a84b",
    };

    public string ReadinessColor => _item.Readiness switch
    {
        WorklistReadiness.Ready   => "#5aa469",
        WorklistReadiness.Blocked => "#c85a5a",
        _                         => "#c8a84b",
    };

    /// <summary>Blocked items say what is in the way; the rest carry their own detail.</summary>
    public string Note => _item.BlockedBy.Length > 0 ? _item.BlockedBy : "";

    public bool HasNote => Note.Length > 0;

    /// <summary>How long this has been asking to be done — the thing a regenerated list would
    /// otherwise lose on every refresh.</summary>
    public string AgeText => _item.FirstSeenAt is { } f ? Ago(f) : "";

    /// <summary>How stale the data behind the suggestion is. Shown so the player does not act
    /// on an hour-old order book without knowing it.</summary>
    public string DataAgeText => _item.DataAsOf is { } d ? $"data {Ago(d)}" : "";

    public string SnoozeText => _item.SnoozedUntil is { } s && s > DateTimeOffset.UtcNow
        ? $"snoozed until {s.ToLocalTime():d MMM HH:mm}"
        : "";

    private static string Ago(DateTimeOffset t)
    {
        var d = DateTimeOffset.UtcNow - t;
        if (d.TotalMinutes <  1) return "just now";
        if (d.TotalHours   <  1) return $"{(int)d.TotalMinutes}m ago";
        if (d.TotalDays    <  1) return $"{(int)d.TotalHours}h ago";
        return $"{(int)d.TotalDays}d ago";
    }
}

/// <summary>
/// One station's need for one item on the Station Needs grid.
///
/// <para>Formatted text alongside the raw value each column sorts on, so the grid orders by
/// quantity rather than by the digits of a thousands-separated string.</para>
/// </summary>
/// <summary>One line of "what is asking for this", under a need.</summary>
public sealed class NeedDriverRowVm(NeedDriver d)
{
    public string Driver => d.Label;
    public string Kind   => d.Kind;
    public long   QtyRaw => d.Qty;
    public string Qty    => d.Qty.ToString("N0");

    public bool HasLink => d.DriverTypeId > 0;
    public void Open()  { if (d.DriverTypeId > 0) EntityNavigator.Instance.Item(d.DriverTypeId); }
}

public sealed class StationNeedRowVm(StationNeed n) : ReactiveObject
{
    private bool _isExpanded;

    /// <summary>Whether the "asked for by" panel is open. Lives on the item, not the row, so the
    /// glyph stays right when the grid recycles rows during a scroll.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set { this.RaiseAndSetIfChanged(ref _isExpanded, value); this.RaisePropertyChanged(nameof(Glyph)); }
    }

    public string Glyph => !HasDrivers ? "" : _isExpanded ? "▾" : "▸";

    public string Station => n.StationName;
    public string Item    => n.TypeName;

    // Both names on a need point somewhere; the ids were already on the record.
    public bool HasStationLink => n.StationId > 0 && n.StationName.Length > 0;
    public bool HasItemLink    => n.TypeId    > 0 && n.TypeName.Length    > 0;

    /// <summary>⚠️ Station versus structure by int range — SdeStations keys on an int, so an id
    /// above that range cannot be a station. A station need carries no discriminator.</summary>
    public void OpenStation()
    {
        if (n.StationId <= 0) return;
        if (n.StationId <= int.MaxValue)
            EntityNavigator.Instance.Entity(EntityKind.Station, n.StationId);
        else
            EntityNavigator.Instance.Structure(n.StationId);
    }

    public void OpenItem() => EntityNavigator.Instance.Item(n.TypeId);

    public long   TotalRaw  => n.Total;
    public string Total     => n.Total.ToString("N0");
    public long   OnHandRaw => n.OnHand;
    public string OnHand    => n.OnHand.ToString("N0");

    public long   ShortRaw  => n.Shortfall;
    public string Short     => n.Shortfall > 0 ? n.Shortfall.ToString("N0") : "";
    /// <summary>Red only where the station is actually short; a covered need is not a problem.</summary>
    public string ShortColor => n.Shortfall > 0 ? "#c85a5a" : "#555566";

    // Priced and sized on the shortfall, so the columns answer "what does closing this cost, and
    // what does it take to carry" rather than restating stock already sitting there.
    public double ShortValueRaw  => n.ShortfallValue;
    public string ShortValue     => n.Shortfall > 0 ? Isk(n.ShortfallValue) : "";
    public double ShortVolumeRaw => n.ShortfallVolume;
    public string ShortVolume    => n.Shortfall > 0 ? $"{n.ShortfallVolume:N0} m³" : "";

    /// <summary>
    /// What is asking for this here — the jobs behind the number, largest share first.
    ///
    /// <para>Shown as row details rather than a column: it is a list of variable length, and the
    /// question it answers ("what is this FOR") is one people ask of a single row, not one they
    /// scan a grid for.</para>
    /// </summary>
    public IReadOnlyList<NeedDriverRowVm> Drivers { get; } =
        n.Why.Select(d => new NeedDriverRowVm(d)).ToList();

    /// <summary>False when nothing itemised this need, so the row shows no expander.</summary>
    public bool HasDrivers => n.Why.Count > 0;

    public long   OrderJobsRaw => n.OrderJobs;
    public long   JobsRaw      => n.Jobs;
    public long   InvLevelsRaw => n.InventoryLevels;
    public long   StnLevelsRaw => n.StationLevels;

    // Blank rather than "0": a column of zeroes is noise, and what the reader is scanning for is
    // which of the four is carrying the number.
    public string OrderJobs => Some(n.OrderJobs);
    public string Jobs      => Some(n.Jobs);
    public string InvLevels => Some(n.InventoryLevels);
    public string StnLevels => Some(n.StationLevels);

    private static string Some(long v) => v > 0 ? v.ToString("N0") : "";

    internal static string Isk(double v) => v switch
    {
        >= 1_000_000_000_000 => $"{v / 1_000_000_000_000:N2}T",
        >= 1_000_000_000     => $"{v / 1_000_000_000:N2}B",
        >= 1_000_000         => $"{v / 1_000_000:N1}M",
        >= 1_000             => $"{v / 1_000:N0}k",
        _                    => v.ToString("N0"),
    };
}

/// <summary>
/// The worklist: what to do next, and whether it can be done now.
///
/// Rows are rebuilt from live data on every refresh rather than tracked, so an item disappears
/// once the thing it asked for has happened. Detection is off polled ESI data, which means a
/// refresh only reflects the last poll — hence the per-row data age.
/// </summary>
public class WorklistViewModel : ReactiveObject
{
    private readonly WorklistService _service;

    public BulkObservableCollection<WorklistRowVm> Rows { get; } = [];

    /// <summary>
    /// What the grid actually binds to: <see cref="Rows"/> grouped by task.
    ///
    /// <para>Task takes three values and the list is already ordered by it, so as a column it was
    /// a heading repeated on every row. As a group header it says the same thing once and carries
    /// the count with it.</para>
    ///
    /// <para>⚠️ A view over the same collection, not a copy. <see cref="ApplyFilters"/> keeps
    /// clearing and refilling <see cref="Rows"/>, and the view follows — so filtering, the
    /// summary and the status line all still read from one list. Building a second grouped list
    /// here would be a second thing to keep in step, which is the failure this codebase keeps
    /// running into.</para>
    /// </summary>
    public DataGridCollectionView RowsView { get; }

    // ── Station Needs ─────────────────────────────────────────────────────────
    //
    // What each station wants and which demand is asking for it. The worklist says what to do;
    // this says why, which is the question asked when a suggestion looks wrong.

    public ObservableCollection<StationNeedRowVm> Needs { get; } = [];

    /// <summary>
    /// The needs grouped by station, the way the task grid groups by kind.
    ///
    /// <para>A station's wants are read together — you are deciding what one trip carries — and a
    /// flat list ordered by shortfall scattered each station's rows through the whole grid. The
    /// group header names the station once instead of every row repeating it.</para>
    ///
    /// <para>⚠️ The station sort has to stay first in SortDescriptions or the groups themselves
    /// reorder when a column is sorted. <see cref="PinNeedsGroupOrder"/> re-pins it, the same fix
    /// the task grid needed.</para>
    /// </summary>
    public DataGridCollectionView NeedsView { get; }

    /// <summary>The same needs with the item as the heading and the stations beneath it.</summary>
    public DataGridCollectionView ItemNeedsView { get; }

    private bool _needsLoading;
    public bool NeedsLoading { get => _needsLoading; private set => this.RaiseAndSetIfChanged(ref _needsLoading, value); }

    private string _needsStatus = "Open this tab to work out what every station wants.";
    public string NeedsStatus { get => _needsStatus; private set => this.RaiseAndSetIfChanged(ref _needsStatus, value); }

    public ReactiveCommand<Unit, Unit> RefreshNeedsCommand { get; private set; } = null!;

    /// <summary>
    /// Which top-level tab is showing. Watched only so Station Needs can fill itself the first
    /// time it is opened: working it out costs the same gather the worklist does, which is worth
    /// paying when the tab is looked at and not worth paying at startup for everyone who never
    /// opens it.
    /// </summary>
    private int _outerTabIndex;
    public int OuterTabIndex
    {
        get => _outerTabIndex;
        set
        {
            this.RaiseAndSetIfChanged(ref _outerTabIndex, value);
            if (value == StationNeedsTab && Needs.Count == 0 && !NeedsLoading) _ = LoadNeedsAsync();
        }
    }

    private const int StationNeedsTab = 1;

    /// <summary>Selects the Station Needs tab — what the Overview's link to it needs, so that
    /// following it lands on the report rather than on whatever tab was last open.</summary>
    public void ShowStationNeedsTab() => OuterTabIndex = StationNeedsTab;

    /// <summary>
    /// A tab the tool should show once its view exists.
    ///
    /// <para>⚠️ Not just setting <see cref="OuterTabIndex"/>. Opening the tool from the Overview
    /// creates the WorklistView, whose TabControl binds SelectedIndex two-way and writes its own
    /// default (0) back over anything set beforehand — and posting the change afterwards is a race
    /// against view creation that was lost twice. The view applies this on load instead, after its
    /// own binding is established, and clears it so it fires once.</para>
    /// </summary>
    public int? RequestedTab { get; set; }

    /// <summary>Applied by the view once it is loaded. Returns the tab to show, if one was asked
    /// for, and forgets it.</summary>
    public int? TakeRequestedTab()
    {
        var t = RequestedTab;
        RequestedTab = null;
        return t;
    }

    /// <summary>Selects the Station Needs tab — what the Overview's link to it needs, so that
    /// following it lands on the report rather than on whatever tab was last open.</summary>
    public void RequestStationNeedsTab() => RequestedTab = StationNeedsTab;

    /// <summary>
    /// Loads the station-needs report if it has not been loaded yet.
    ///
    /// <para>⚠️ Needs is normally filled only when the user opens that tab, which left the
    /// Overview's Station Needs section permanently empty — nothing on the Overview opens a tab.
    /// </para>
    /// </summary>
    public Task EnsureNeedsLoadedAsync()
        => Needs.Count > 0 || NeedsLoading ? Task.CompletedTask : LoadNeedsAsync();

    private async Task LoadNeedsAsync()
    {
        // Taken off the service's own generator list rather than injected separately, so the
        // report is produced by the very object that plans the hauling.
        var logistics = _service.Generators.OfType<LogisticsGenerator>().FirstOrDefault();
        if (logistics is null)
        {
            NeedsStatus = "The Logistics source is not available.";
            return;
        }

        NeedsLoading = true;
        try
        {
            var rows = await logistics.NeedsAsync();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Needs.Clear();
                foreach (var r in rows.OrderByDescending(r => r.Shortfall).ThenBy(r => r.StationName))
                    Needs.Add(new StationNeedRowVm(r));

                var stations = rows.Select(r => r.StationId).Distinct().Count();
                var short_   = rows.Count(r => r.Shortfall > 0);
                NeedsStatus = rows.Count == 0
                    ? "Nothing is wanted anywhere — no build rules, orders or station levels are asking for material."
                    : $"{rows.Count:N0} need(s) across {stations} station(s); {short_:N0} short. "
                    + "Shortest first. These are wants, not tasks — a small shortfall may sit inside "
                    + "the station-level deadband and raise no haul.";
            });
        }
        catch (Exception ex)
        {
            NeedsStatus = $"Could not work out station needs: {ex.Message}";
        }
        finally { NeedsLoading = false; }
    }

    /// <summary>Market alt configuration, hosted here because it exists only to serve this tool.</summary>
    public WorklistMarketAltsViewModel MarketAltsVm { get; }

    /// <summary>Inventory-level rules: thresholds, stations and fill targets.</summary>
    public WorklistInvRulesViewModel RulesVm { get; }

    /// <summary>Who maintains each corporation's standing projects.</summary>
    public WorklistCorpAltsViewModel CorpAltsVm { get; }

    /// <summary>Which characters run industry, and against which park.</summary>
    public WorklistIndustryViewModel IndustryVm { get; }

    /// <summary>Where each group's stock should live. Distribution, not demand.</summary>
    public WorklistStationLevelsViewModel StationLevelsVm { get; }

    /// <summary>Which sources run, and which conditions each one raises.</summary>
    public ObservableCollection<WorklistToggleVm> Sources    { get; } = [];
    public ObservableCollection<WorklistToggleVm> Conditions { get; } = [];

    public ReactiveCommand<Unit, Unit>   RefreshCommand { get; }
    public ReactiveCommand<string, Unit> SnoozeCommand  { get; }

    public WorklistViewModel(WorklistService service, WorklistMarketAltsViewModel marketAlts,
                             WorklistInvRulesViewModel rules,
                             WorklistCorpAltsViewModel corpAlts,
                             WorklistIndustryViewModel industry,
                             WorklistStationLevelsViewModel stationLevels)
    {
        _service = service;

        RowsView = new DataGridCollectionView(Rows);
        RowsView.GroupDescriptions.Add(new DataGridPathGroupDescription(nameof(WorklistRowVm.KindText)));
        PinGroupOrder();

        // ⚠️ Re-pinned whenever the sort changes, not set once. Groups are built from the view's
        // sorted sequence, so a column sort decided group order too — sorting by volume put
        // whichever group held the largest single row first, and the groups reshuffled on every
        // header click for a reason nothing on screen explained. Clicking a header replaces the
        // sort wholesale, so the group key has to be put back at the head of it each time; after
        // that the user's sort still applies, inside each group.
        RowsView.SortDescriptions.CollectionChanged += (_, _) => PinGroupOrder();

        // Station Needs, grouped the same way and for the same reason.
        NeedsView = new DataGridCollectionView(Needs);
        NeedsView.GroupDescriptions.Add(new DataGridPathGroupDescription(nameof(StationNeedRowVm.Station)));
        PinNeedsGroupOrder();
        NeedsView.SortDescriptions.CollectionChanged += (_, _) => PinNeedsGroupOrder();

        // ⚠️ The same rows, grouped the other way round — not a second query. A need belongs to a
        // station AND to an item; which one is the heading depends on the question being asked
        // ("what does this station want" against "who wants this item"), and building it twice
        // would be two answers that could disagree.
        ItemNeedsView = new DataGridCollectionView(Needs);
        ItemNeedsView.GroupDescriptions.Add(new DataGridPathGroupDescription(nameof(StationNeedRowVm.Item)));
        PinItemNeedsGroupOrder();
        ItemNeedsView.SortDescriptions.CollectionChanged += (_, _) => PinItemNeedsGroupOrder();

        MarketAltsVm  = marketAlts;
        RulesVm  = rules;
        RulesVm.RulesChanged = RefreshAsync;
        CorpAltsVm = corpAlts;
        CorpAltsVm.CorpAltsChanged = RefreshAsync;
        IndustryVm = industry;
        IndustryVm.IndustryChanged = RefreshAsync;
        StationLevelsVm = stationLevels;
        StationLevelsVm.LevelsChanged = RefreshAsync;

        // Assigning a market alt unblocks items, so the list should reflect it without a manual
        // refresh — that gap is exactly what makes config feel like it did not take.
        MarketAltsVm.MarketAltsChanged = RefreshAsync;

        RefreshCommand = ReactiveCommand.CreateFromTask(RefreshAsync);
        RefreshNeedsCommand = ReactiveCommand.CreateFromTask(LoadNeedsAsync);
        ClearFiltersCommand = ReactiveCommand.Create(ClearFilters);
        SnoozeCommand  = ReactiveCommand.CreateFromTask<string>(async key =>
        {
            await _service.SnoozeAsync(key, DateTimeOffset.UtcNow.AddHours(SnoozeHours));
            await RefreshAsync();
        });

        BuildToggles();
        _ = RefreshAsync();
    }

    /// <summary>
    /// The Sources tab. Built from the registered generators rather than a hard-coded list, so
    /// a new generator appears here the moment it is registered.
    /// </summary>
    private void BuildToggles()
    {
        var s = _service.Settings;

        foreach (var g in _service.Generators)
            Sources.Add(new WorklistToggleVm(
                g.DisplayName,
                "Include this source in the worklist.",
                s.IsSourceEnabled(g.Id),
                on => s.SetSourceEnabledAsync(g.Id, on),
                RefreshAsync));

        // Listed here but not a generator: pending customer orders are demand that the industry
        // and material-purchase generators both read. It belongs on this tab because this is where
        // a player looks to answer "what is the worklist built from".
        Sources.Add(new WorklistToggleVm(
            "Customer orders",
            "Plan the pending orders from the Order Tracker, netted against what is already built "
          + "or in production. Uses the park and buy location set on the Industry tab.",
            s.PlanCustomerOrders,
            on => s.SetPlanCustomerOrdersAsync(on),
            RefreshAsync));

        // Standing-buy conditions are separate switches because they are separate jobs: an
        // outbid order needs a price change, a missing one needs creating, a low one topping up.
        Conditions.Add(new WorklistToggleVm("Missing orders",
            "A standing order with no live buy order at its station.",
            s.RaiseMissing,  on => s.SetConditionAsync("missing", on),  RefreshAsync));
        Conditions.Add(new WorklistToggleVm("Outbid",
            "Someone else is bidding higher, so the order is buying nothing.",
            s.RaiseOutbid,   on => s.SetConditionAsync("outbid", on),   RefreshAsync));
        Conditions.Add(new WorklistToggleVm("Running low",
            "Remaining volume has fallen below the top-up threshold.",
            s.RaiseLow,      on => s.SetConditionAsync("low", on),      RefreshAsync));
        Conditions.Add(new WorklistToggleVm("Expiring soon",
            "The order is close to the end of its duration.",
            s.RaiseExpiring, on => s.SetConditionAsync("expiring", on), RefreshAsync));
    }

    /// <summary>How long the snooze button hides an item for.</summary>
    public const int SnoozeHours = 8;

    private bool _showSnoozed;
    public bool ShowSnoozed
    {
        get => _showSnoozed;
        set { this.RaiseAndSetIfChanged(ref _showSnoozed, value); _ = RefreshAsync(); }
    }

    /// <summary>
    /// Blocked and waiting items are hidden unless asked for.
    ///
    /// The list is read to find something to do now, and an item that cannot be started is not
    /// that — it is context. Left in by default they crowd out the actionable rows, and a list
    /// mostly full of things you cannot do stops being read at all. They stay one click away
    /// because the reasons are worth seeing when planning rather than executing.
    /// </summary>
    private bool _showNotReady;
    public bool ShowNotReady
    {
        get => _showNotReady;
        set { this.RaiseAndSetIfChanged(ref _showNotReady, value); _ = RefreshAsync(); }
    }

    // Counts live on the toggles themselves rather than only in the status line's "hidden" tail.
    // The tail says nothing once a toggle is on, which is exactly when the reader wants to know
    // how much of what they are looking at is the part they just revealed.
    //
    // Counted over the whole run, not the filtered rows: these describe what the toggle governs,
    // and a number that moved when an unrelated column filter changed would read as the toggle
    // having done something.

    private string _notReadyLabel = "Show blocked / waiting";
    public string NotReadyLabel
    {
        get => _notReadyLabel;
        private set => this.RaiseAndSetIfChanged(ref _notReadyLabel, value);
    }

    private string _snoozedLabel = "Show snoozed";
    public string SnoozedLabel
    {
        get => _snoozedLabel;
        private set => this.RaiseAndSetIfChanged(ref _snoozedLabel, value);
    }

    // ── Filtering ─────────────────────────────────────────────────────────────
    //
    // Applied over the rows already built rather than by regenerating, so changing a filter is
    // instant. A rebuild takes seconds — every generator re-queries and re-plans — and paying
    // that to hide a few rows would make the filters feel broken.
    //
    // The dropdown options come from the whole unfiltered set and stay put as filters are
    // applied. Options that vanished as you narrowed would make it impossible to widen again
    // without clearing everything first.

    /// <summary>Shown at the top of each dropdown; means no filter on that column.</summary>
    public const string AnyValue = "(any)";

    private List<WorklistRowVm> _pool = [];

    /// <summary>
    /// Every row this run produced, before the column filters — what the Overview's worklist
    /// sections show. Kept separate from <see cref="Rows"/> so a filter typed in the tool does not
    /// silently reshape a dashboard panel that has no filter row to explain it.
    /// </summary>
    public BulkObservableCollection<WorklistRowVm> PoolRows { get; } = [];

    private DateTimeOffset? _lastRefreshUtc;

    /// <summary>
    /// Rebuilds the worklist only if the last run is older than <paramref name="maxAge"/>.
    ///
    /// <para>⚠️ The Overview refreshes every 60 seconds and a worklist run is expensive — it walks
    /// every generator. Refreshing it on that cadence would put the tool's whole cost on a panel
    /// the user may not be looking at, so the sections accept data that is a few minutes old.</para>
    /// </summary>
    public Task RefreshIfStaleAsync(TimeSpan maxAge)
        => _lastRefreshUtc is { } last && DateTimeOffset.UtcNow - last < maxAge
            ? Task.CompletedTask
            : RefreshAsync();
    private string _hiddenTail = "";

    public ObservableCollection<string> StateOptions     { get; } = [];
    public ObservableCollection<string> TaskOptions      { get; } = [];
    public ObservableCollection<string> CharacterOptions { get; } = [];
    public ObservableCollection<string> SourceOptions    { get; } = [];
    public ObservableCollection<string> DestOptions      { get; } = [];

    private string _stateFilter     = AnyValue;
    private string _taskFilter      = AnyValue;
    private string _characterFilter = AnyValue;
    private string _sourceFilter    = AnyValue;
    private string _destFilter      = AnyValue;
    private string _descriptionFilter = "";
    private string _noteFilter        = "";

    public string StateFilter       { get => _stateFilter;       set => SetFilter(ref _stateFilter, value); }
    public string TaskFilter        { get => _taskFilter;        set => SetFilter(ref _taskFilter, value); }
    public string CharacterFilter   { get => _characterFilter;   set => SetFilter(ref _characterFilter, value); }
    public string SourceFilter      { get => _sourceFilter;      set => SetFilter(ref _sourceFilter, value); }
    public string DestFilter        { get => _destFilter;        set => SetFilter(ref _destFilter, value); }
    public string DescriptionFilter { get => _descriptionFilter; set => SetFilter(ref _descriptionFilter, value); }
    public string NoteFilter        { get => _noteFilter;        set => SetFilter(ref _noteFilter, value); }

    public ReactiveCommand<Unit, Unit> ClearFiltersCommand { get; private set; } = null!;

    public bool HasFilters =>
        _stateFilter != AnyValue || _taskFilter != AnyValue || _characterFilter != AnyValue
        || _sourceFilter != AnyValue || _destFilter != AnyValue
        || _descriptionFilter.Length > 0 || _noteFilter.Length > 0;

    private void SetFilter(ref string field, string? value)
    {
        var v = value ?? AnyValue;
        if (field == v) return;
        field = v;
        this.RaisePropertyChanged(nameof(HasFilters));
        ApplyFilters();
    }

    private void RebuildFilterOptions()
    {
        Fill(StateOptions,     _pool.Select(r => r.ReadinessText));
        Fill(TaskOptions,      _pool.Select(r => r.KindText));
        Fill(CharacterOptions, _pool.Select(r => r.CharacterName));
        Fill(SourceOptions,    _pool.Select(r => r.LocationName));
        Fill(DestOptions,      _pool.Select(r => r.DestinationName));

        // A filter naming something the latest refresh no longer contains would hide every row
        // with no way to tell why, so it falls back to unfiltered.
        Keep(ref _stateFilter,     StateOptions,     nameof(StateFilter));
        Keep(ref _taskFilter,      TaskOptions,      nameof(TaskFilter));
        Keep(ref _characterFilter, CharacterOptions, nameof(CharacterFilter));
        Keep(ref _sourceFilter,    SourceOptions,    nameof(SourceFilter));
        Keep(ref _destFilter,      DestOptions,      nameof(DestFilter));

        // ⚠️ Reconciled in place, never cleared and refilled. Clearing removes the item the
        // dropdown has selected, so the control sets SelectedItem to null and the two-way binding
        // writes that straight back into the filter — before the Keep below can protect it. Every
        // refresh dropped the filters, even when the value was about to reappear in the same list,
        // which is why a refresh looked like it was resetting them on purpose.
        void Fill(ObservableCollection<string> target, IEnumerable<string> values)
        {
            var wanted = new List<string> { AnyValue };
            wanted.AddRange(values
                .Select(v => string.IsNullOrWhiteSpace(v) ? "—" : v)
                .Distinct()
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase));

            // What has gone, first — so the pass below only ever inserts.
            var keep = wanted.ToHashSet(StringComparer.Ordinal);
            for (var i = target.Count - 1; i >= 0; i--)
                if (!keep.Contains(target[i]))
                    target.RemoveAt(i);

            // Both sides are in the same order, so one pass lines them up. A value that survives
            // is never touched, and that is what keeps the selection alive.
            for (var i = 0; i < wanted.Count; i++)
                if (i >= target.Count || !string.Equals(target[i], wanted[i], StringComparison.Ordinal))
                    target.Insert(i, wanted[i]);

            while (target.Count > wanted.Count) target.RemoveAt(target.Count - 1);
        }

        void Keep(ref string field, ObservableCollection<string> options, string propertyName)
        {
            if (options.Contains(field)) return;
            field = AnyValue;
            this.RaisePropertyChanged(propertyName);
        }
    }

    /// <summary>Keeps the group-key sort first, so groups always appear in the order
    /// <see cref="WorklistRowVm.KindRank"/> defines whatever else is being sorted on.</summary>
    private bool _pinningGroupOrder;

    private void PinGroupOrder()
    {
        if (_pinningGroupOrder) return;              // the insert below re-raises this
        var sorts = RowsView.SortDescriptions;
        if (sorts.Count > 0 && sorts[0].PropertyPath == nameof(WorklistRowVm.KindRank)) return;

        _pinningGroupOrder = true;
        try   { sorts.Insert(0, DataGridSortDescription.FromPath(nameof(WorklistRowVm.KindRank))); }
        finally { _pinningGroupOrder = false; }
    }

    private bool _pinningNeedsGroupOrder;

    /// <summary>
    /// <see cref="PinGroupOrder"/> for the Station Needs grid.
    ///
    /// <para>Groups here sort on the station name itself rather than a rank: there is no natural
    /// order among stations, and alphabetical is at least the one a reader can predict. Inside a
    /// group the user's own column sort still applies.</para>
    /// </summary>
    private void PinNeedsGroupOrder()
    {
        if (_pinningNeedsGroupOrder) return;
        var sorts = NeedsView.SortDescriptions;
        if (sorts.Count > 0 && sorts[0].PropertyPath == nameof(StationNeedRowVm.Station)) return;

        _pinningNeedsGroupOrder = true;
        try   { sorts.Insert(0, DataGridSortDescription.FromPath(nameof(StationNeedRowVm.Station))); }
        finally { _pinningNeedsGroupOrder = false; }
    }

    private bool _pinningItemNeedsGroupOrder;

    private void PinItemNeedsGroupOrder()
    {
        if (_pinningItemNeedsGroupOrder) return;
        var sorts = ItemNeedsView.SortDescriptions;
        if (sorts.Count > 0 && sorts[0].PropertyPath == nameof(StationNeedRowVm.Item)) return;

        _pinningItemNeedsGroupOrder = true;
        try   { sorts.Insert(0, DataGridSortDescription.FromPath(nameof(StationNeedRowVm.Item))); }
        finally { _pinningItemNeedsGroupOrder = false; }
    }

    private void ApplyFilters()
    {
        // One notification, not one per row: this is the grid's ItemsSource.
        Rows.ResetTo(_pool.Where(Matches));

        UpdateStatus();
        this.RaisePropertyChanged(nameof(HasFilters));
    }

    // ── Summary strip ─────────────────────────────────────────────────────────
    //
    // ⚠️ Counted off the run, not off the rows on screen — hence WorklistItem rather than
    // WorklistRowVm. The whole point of the strip is to say how many items are blocked without
    // having to show the blocked ones, and a count that emptied the moment a filter narrowed the
    // view answered a question nobody was asking. Snoozed items are the one exclusion, matching
    // _readyTotal: those the user has deliberately parked.

    /// <summary>
    /// The strip's chips: how the work divides, and nothing the grid already says.
    ///
    /// <para>Counts only. Every per-kind total — tasks, value, volume — is on that kind's group
    /// header a few pixels below, so the panels that used to carry them were the same figures
    /// printed twice. What is left is the mix (how much of this is buying versus hauling versus
    /// building) and the state split, neither of which a single group header can show.</para>
    ///
    /// <para>⚠️ Ready is deliberately absent from <see cref="StateSummary"/>: it is the headline
    /// figure sitting immediately to its left. One number, one place on screen.</para>
    /// </summary>
    public ObservableCollection<SummaryStatVm> KindSummary  { get; } = [];
    public ObservableCollection<SummaryStatVm> StateSummary { get; } = [];
    /// <summary>
    /// When the last run finished. Absolute rather than "4 minutes ago": a relative label is only
    /// honest while something keeps it ticking, and every row already carries its own data age.
    /// </summary>
    private string _refreshedText = "";
    public string RefreshedText
    {
        get => _refreshedText;
        private set => this.RaiseAndSetIfChanged(ref _refreshedText, value);
    }

    /// <summary>
    /// How the list is built. Was a permanent paragraph above the grid, read once and then taking
    /// three lines forever. Now shown only when the grid is empty — which is the one moment someone
    /// is asking why there is nothing here.
    /// </summary>
    public const string HelpText =
        "Items are rebuilt from live data each refresh — one disappears once the work is done. " +
        "Detection runs off polled ESI data, so each row shows how old the data behind it is. " +
        "The summary above is counted off the whole run, not the filters.";


    private void UpdateSummary(List<WorklistItem> items)
    {
        // Buy and Haul as themselves; Job split into its three pools, because "116 jobs" does not
        // tell you whether the evening is manufacturing or reactions. Empty ones are simply absent
        // rather than shown as zero.
        //
        // ⚠️ Ready items only, so the chips sum to the headline beside them. They used to count
        // every unsnoozed item including blocked and waiting, which made "166 ready now" sit next
        // to chips totalling 496 — two different questions answered in one row, with nothing
        // saying so. How much is blocked is on the checkbox below, where it is acted on.
        var ready = items.Where(i => i.Readiness == WorklistReadiness.Ready).ToList();

        Fill(KindSummary, new[]
            {
                new SummaryStatVm("buy",           ready.Count(i => i.Kind == WorklistKind.Buy).ToString("N0")),
                new SummaryStatVm("haul",          ready.Count(i => i.Kind == WorklistKind.Haul).ToString("N0")),
                new SummaryStatVm("manufacturing", ready.Count(i => i.Pool == IndustryPool.Manufacturing).ToString("N0")),
                new SummaryStatVm("reactions",     ready.Count(i => i.Pool == IndustryPool.Reaction).ToString("N0")),
                new SummaryStatVm("science",       ready.Count(i => i.Pool == IndustryPool.Science).ToString("N0")),
            }
            .Where(s => s.Value != "0"));

        static void Fill(ObservableCollection<SummaryStatVm> into, IEnumerable<SummaryStatVm> from)
        {
            into.Clear();
            foreach (var s in from) into.Add(s);
        }
    }

    private bool Matches(WorklistRowVm r) =>
        Is(_stateFilter,     r.ReadinessText)
        && Is(_taskFilter,      r.KindText)
        && Is(_characterFilter, r.CharacterName)
        && Is(_sourceFilter,    r.LocationName)
        && Is(_destFilter,      r.DestinationName)
        && Has(_descriptionFilter, r.Title)
        // The note cell shows the detail, the blocked reason and the snooze line together, so a
        // search over it has to cover all three or it would miss what the reader can see.
        && Has(_noteFilter, $"{r.Detail} {r.Note} {r.SnoozeText}");

    private static bool Is(string filter, string value) =>
        filter == AnyValue
        || string.Equals(filter, string.IsNullOrWhiteSpace(value) ? "—" : value,
                         StringComparison.OrdinalIgnoreCase);

    private static bool Has(string needle, string haystack) =>
        needle.Length == 0
        || haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private void ClearFilters()
    {
        _stateFilter = _taskFilter = _characterFilter = _sourceFilter = _destFilter = AnyValue;
        _descriptionFilter = _noteFilter = "";

        foreach (var p in new[] { nameof(StateFilter), nameof(TaskFilter), nameof(CharacterFilter),
                                  nameof(SourceFilter), nameof(DestFilter),
                                  nameof(DescriptionFilter), nameof(NoteFilter) })
            this.RaisePropertyChanged(p);

        ApplyFilters();
    }

    /// <summary>
    /// How many items are actually ready, counted off the data rather than off what is on screen.
    ///
    /// <para>⚠️ Not <c>_pool.Count</c>. The pool is what the two checkboxes allow through, so with
    /// blocked and waiting shown it counted those as ready too — 433 ready where 147 were. Nor is
    /// it <c>Rows.Count</c>: filtering to one character does not make the other characters' work
    /// unready, it just stops showing it.</para>
    /// </summary>
    private int _readyTotal;

    /// <summary>The ready count on its own, for the summary strip's headline figure. Same number
    /// as <see cref="Status"/> opens with — one field, shown twice, rather than two counts that
    /// could disagree.</summary>
    private string _readyCountText = "0";
    public string ReadyCountText { get => _readyCountText; private set => this.RaiseAndSetIfChanged(ref _readyCountText, value); }

    private void UpdateStatus()
    {
        ReadyCountText = _readyTotal.ToString("N0");

        var shown = Rows.Count;

        // "Shown" is only worth saying when it differs from the ready count — otherwise the two
        // numbers are the same fact stated twice.
        var shownPart = shown == _readyTotal ? "" : $"  ·  {shown:N0} shown";

        Status = _readyTotal == 0
            ? $"Nothing ready{shownPart}{_hiddenTail}."
            : $"{_readyTotal:N0} ready{shownPart}{_hiddenTail}";
    }

    private bool _isLoading;
    public bool IsLoading { get => _isLoading; private set => this.RaiseAndSetIfChanged(ref _isLoading, value); }

    private string _status = "";
    public string Status { get => _status; private set => this.RaiseAndSetIfChanged(ref _status, value); }

    private string _errors = "";
    public string Errors { get => _errors; private set => this.RaiseAndSetIfChanged(ref _errors, value); }
    public bool HasErrors => Errors.Length > 0;

    public async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            // ⚠️ Task.Run around the whole computation, not just the build. RefreshAsync is
            // called from the UI thread (the Overview's refresh, the toolbar button), and
            // awaiting BuildAsync does NOT get off it: EF Core on SQLite completes its "async"
            // work synchronously, so every generator, every query and every sort below ran
            // inline on the UI thread. That was the freeze — thirteen seconds of worklist build
            // with ten seconds of it holding the UI, once every six minutes.
            //
            // Only the collection updates need the UI thread, and they are the cheap part.
            var (run, unsnoozed, pool, failed) = await Task.Run(async () =>
            {
                var built = await _service.BuildAsync();

                var alive = built.AllItems.Where(i => ShowSnoozed || !i.IsSnoozed).ToList();

                var visible = alive
                    .Where(i => ShowNotReady || i.Readiness == WorklistReadiness.Ready)
                    // Blocked last: the list is read top-down looking for something to do, and an
                    // item that cannot be actioned does not belong at the top of that read.
                    .OrderBy(i => i.Readiness == WorklistReadiness.Blocked ? 1 : 0)
                    .ThenByDescending(i => i.Priority)
                    .ThenBy(i => i.CharacterName)
                    .ThenBy(i => i.Title)
                    .ToList();

                // Numbered here, off the ordered list, so the sequence is the default order itself
                // rather than a second guess at it. Built off-thread with everything else — the
                // rows are plain view models until something binds to them.
                var rows = visible.Select((i, n) => new WorklistRowVm(i, n + 1)).ToList();

                return (built, alive, rows,
                        built.Sections.Where(s => s.Error is not null).ToList());
            });

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _pool = pool;

                // The Overview sections bind here rather than to Rows: they have no filter row of
                // their own, and inheriting whatever the tool happened to be filtered to would make
                // a dashboard panel change behind the user for reasons not visible on it.
                //
                // ⚠️ One Reset, not one notification per row. The Overview rebuilds four more
                // bound collections whenever this changes, so an item-by-item fill here was
                // quadratic — see BulkObservableCollection.
                PoolRows.ResetTo(_pool);
                _lastRefreshUtc = DateTimeOffset.UtcNow;
                RefreshedText   = $"Refreshed {DateTime.Now:HH:mm}";

                RebuildFilterOptions();
                ApplyFilters();

                var snoozed  = run.AllItems.Count(i => i.IsSnoozed);
                var blocked  = unsnoozed.Count(i => i.Readiness == WorklistReadiness.Blocked);
                var waiting  = unsnoozed.Count(i => i.Readiness == WorklistReadiness.Waiting);

                // Counted here, off the data, so neither checkbox nor filter can change it.
                _readyTotal = unsnoozed.Count(i => i.Readiness == WorklistReadiness.Ready);
                UpdateSummary(unsnoozed);

                // Hidden counts are always reported, even when nothing is actionable. "Nothing to
                // do" beside nine blocked items would be a lie of omission — there is plenty to
                // do, none of it right now.
                NotReadyLabel = $"Show blocked / waiting ({blocked + waiting:N0})";
                SnoozedLabel  = $"Show snoozed ({snoozed:N0})";

                var hidden = new List<string>();
                if (!ShowNotReady && blocked > 0) hidden.Add($"{blocked} blocked");
                if (!ShowNotReady && waiting > 0) hidden.Add($"{waiting} waiting");
                if (!ShowSnoozed  && snoozed > 0) hidden.Add($"{snoozed} snoozed");

                _hiddenTail = hidden.Count > 0 ? $" ({string.Join(", ", hidden)} hidden)" : "";
                UpdateStatus();

                Errors = failed.Count == 0
                    ? ""
                    : string.Join("  ·  ", failed.Select(f => $"{f.DisplayName}: {f.Error}"));
                this.RaisePropertyChanged(nameof(HasErrors));
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => Status = $"Error: {ex.Message}");
        }
        finally { IsLoading = false; }
    }
}

/// <summary>
/// One configurable switch on the Sources tab. Writes straight through to preferences and asks
/// the worklist to rebuild, because a setting that needs a separate refresh to take effect reads
/// as a setting that did not save.
/// </summary>
public class WorklistToggleVm : ReactiveObject
{
    private readonly Func<bool, Task> _save;
    private readonly Func<Task>       _refresh;
    private bool _isOn;

    public WorklistToggleVm(string label, string description, bool isOn,
                            Func<bool, Task> save, Func<Task> refresh)
    {
        Label       = label;
        Description = description;
        _isOn       = isOn;
        _save       = save;
        _refresh    = refresh;
    }

    public string Label       { get; }
    public string Description { get; }

    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (_isOn == value) return;
            this.RaiseAndSetIfChanged(ref _isOn, value);
            _ = Apply(value);
        }
    }

    private async Task Apply(bool value)
    {
        await _save(value);
        await _refresh();
    }
}
