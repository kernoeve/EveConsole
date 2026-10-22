using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Services;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;
using EveConsole.Localization;

namespace EveConsole.ViewModels;

/// <summary>One task in the left-hand list.</summary>
public sealed class ScheduledTaskRowVm : ReactiveObject
{
    public int    Id       { get; init; }
    public string Name     { get; init; } = "";
    public string Schedule { get; init; } = "";
    public bool   Enabled  { get; init; }

    /// <summary>What happened last time, and when — the two questions asked of any scheduler.</summary>
    public string LastRunText { get; init; } = "";
    public string LastResult  { get; init; } = "";
}

/// <summary>A key and the name it goes by on screen. Every picker in this tool is this shape.</summary>
public sealed class LabelledChoice(string key, string label)
{
    public string Key   { get; } = key;
    public string Label { get; } = label;
    public override string ToString() => Label;
}

/// <summary>
/// Which month a corp block reports on, counted back from now.
///
/// <para>⚠️ Relative, never a fixed month. A task that posts "last month" has to keep meaning last
/// month every time it runs; a stored year and month would say January forever.</para>
/// </summary>
public sealed class MonthBackChoice(int monthsBack, string label)
{
    public int    MonthsBack { get; } = monthsBack;
    public string Label      { get; } = label;
    public override string ToString() => Label;
}

/// <summary>A corp the app has a token for, plus its id.</summary>
public sealed class CorpChoice(long id, string name)
{
    public long   Id   { get; } = id;
    public string Name { get; } = name;
    public override string ToString() => Name;
}

/// <summary>A defined sale posting, plus its id.</summary>
public sealed class PostingChoice(int id, string name)
{
    public int    Id   { get; } = id;
    public string Name { get; } = name;
    public override string ToString() => Name;
}

/// <summary>A market config an Industry Ops section can price at, plus its id.</summary>
public sealed class MarketConfigChoice(int id, string name)
{
    public int    Id   { get; } = id;
    public string Name { get; } = name;
    public override string ToString() => Name;
}

/// <summary>
/// A market group chosen in an Industry Ops section, with its own ✕.
///
/// <para>⚠️ The command is on the chip rather than reached from the template through a
/// <c>$parent</c> cast, which can fail to resolve at run time and take the app down with it.</para>
/// </summary>
public sealed class MarketGroupChip
{
    public MarketGroupChip(int id, string name, Action<MarketGroupChip> remove)
    {
        MarketGroupId = id;
        Name          = name;
        RemoveCommand = ReactiveCommand.Create(() => remove(this));
    }

    public int    MarketGroupId { get; }
    public string Name          { get; }
    public ReactiveCommand<Unit, Unit> RemoveCommand { get; }
}

/// <summary>
/// One column of an Industry Ops section's table: its own name, the header it prints (its own
/// unless renamed), and the commands that remove it or move it — on the row, not reached from
/// the template through a cast.
/// </summary>
public sealed class OppsColumnVm : ReactiveObject
{
    public OppsColumnVm(string key, string name, string header, bool isItem,
                        Action<OppsColumnVm> remove, Action<OppsColumnVm, int> move)
    {
        Key     = key;
        Name    = name;
        _header = header;
        IsItem  = isItem;
        RemoveCommand   = ReactiveCommand.Create(() => remove(this));
        MoveUpCommand   = ReactiveCommand.Create(() => move(this, -1));
        MoveDownCommand = ReactiveCommand.Create(() => move(this, +1));
    }

    public string Key    { get; }
    /// <summary>The column's own name, which is also its header until renamed.</summary>
    public string Name   { get; }
    /// <summary>The item: always first, never removed or moved; only its header can change.</summary>
    public bool   IsItem { get; }
    public bool   CanMove => !IsItem;

    private string _header;
    public string Header { get => _header; set => this.RaiseAndSetIfChanged(ref _header, value); }

    public ReactiveCommand<Unit, Unit> RemoveCommand   { get; }
    public ReactiveCommand<Unit, Unit> MoveUpCommand   { get; }
    public ReactiveCommand<Unit, Unit> MoveDownCommand { get; }
}

/// <summary>One of the five Top 10 lists, ticked or not.</summary>
public sealed class CategoryChoice(string key, string title) : ReactiveObject
{
    public string Key   { get; } = key;
    public string Title { get; } = title;

    private bool _selected;
    public bool Selected { get => _selected; set => this.RaiseAndSetIfChanged(ref _selected, value); }
}

/// <summary>
/// One standing project DEFINITION, ticked or not.
///
/// <para>⚠️ One row per definition, never per system. A project scoped to a region expands into a
/// row per qualifying system when it is reported, and that set moves with sovereignty — picking
/// from it would mean re-picking every week. The definition is the thing that holds still.</para>
/// </summary>
public sealed class ProjectChoice(long id, string label) : ReactiveObject
{
    public long   Id    { get; } = id;
    public string Label { get; } = label;

    private bool _selected = true;
    public bool Selected { get => _selected; set => this.RaiseAndSetIfChanged(ref _selected, value); }
}

/// <summary>A day of the week, ticked or not.</summary>
public sealed class DayChoice(int bit, string name) : ReactiveObject
{
    public int    Bit  { get; } = bit;
    public string Name { get; } = name;

    private bool _selected;
    public bool Selected { get => _selected; set => this.RaiseAndSetIfChanged(ref _selected, value); }
}

/// <summary>
/// One section of the message being composed.
///
/// <para>⚠️ Carries its own parameters rather than reading them off the screen. A section is
/// saved into a task that runs at 00:01 with nobody watching, so "which corp", "which month" and
/// "which projects" have to be part of the section itself.</para>
/// </summary>
public sealed class MessageBlockVm : ReactiveObject
{
    /// <summary>
    /// The month options, shared by every section.
    ///
    /// <para>The current month is first and named as such: "so far this month" is an ordinary
    /// thing to want posted, and a bare 0 in a spinner did not say so.</para>
    /// </summary>
    public static readonly MonthBackChoice[] MonthOptions =
    [
        new(0, AlarmsText.MonthCurrent),
        new(1, AlarmsText.MonthLast),
        .. Enumerable.Range(2, 11).Select(n => new MonthBackChoice(n,
               Plurals.Format(AlarmsText.ResourceManager, nameof(AlarmsText.MonthsBackOther), n))),
    ];

    public static readonly LabelledChoice[] ProjectTypeOptions =
    [
        new(StandingProjectReport.DeliverItem, AlarmsText.ProjectTypeDeliverItem),
        new(StandingProjectReport.DestroyNpc,  AlarmsText.ProjectTypeDestroyNpc),
    ];

    /// <summary>
    /// How much of the list to report.
    ///
    /// <para>All is last and is the default, because it is what these sections did before the
    /// choice existed — a task written yesterday keeps saying what it said yesterday.</para>
    /// </summary>
    public static readonly LabelledChoice[] ProjectFilterOptions =
    [
        new(ProjectFilters.Missing,       AlarmsText.FilterMissingProjects),
        new(ProjectFilters.MissingAndLow, AlarmsText.FilterMissingAndLowProjects),
        new(ProjectFilters.All,           AlarmsText.FilterAllProjects),
    ];

    private readonly Func<long, string, Task<IReadOnlyList<Models.CorpStandingProject>>>? _loadProjects;

    /// <summary>
    /// Exactly the projects to report on.
    ///
    /// <para>⚠️ Kept here rather than read off the tick boxes, so switching project type and back
    /// does not lose the choice — the boxes are rebuilt from this each time.</para>
    /// </summary>
    private readonly HashSet<long> _included;

    /// <summary>
    /// Whether this section has never been saved.
    ///
    /// <para>⚠️ The whole difference between "tick everything by default" and "never add anything
    /// the author did not". A NEW section ticks whatever it finds, because an empty list is not a
    /// choice anybody made. A SAVED one ticks only what is stored, so a project defined later stays
    /// out of a post written before it existed.</para>
    /// </summary>
    private bool _fresh;

    public MessageBlockVm(
        MessageBlock                  model,
        IReadOnlyList<CorpChoice>     corps,
        IReadOnlyList<PostingChoice>  postings,
        Func<long, string, Task<IReadOnlyList<Models.CorpStandingProject>>>? loadProjects = null,
        bool                          fresh = false,
        Func<bool>?                   destinationIsWebhook = null,
        IReadOnlyList<MarketConfigChoice>?           markets   = null,
        Func<Task<MarketGroupPickerResult?>>?        pickGroup = null)
    {
        _destinationIsWebhook = destinationIsWebhook ?? (() => false);
        Markets       = markets ?? [];
        _pickGroup    = pickGroup;
        Corps         = corps;
        Postings      = postings;
        _loadProjects = loadProjects;
        _included     = [.. model.IncludedProjectIds];
        _fresh        = fresh;

        _type    = model.Type;
        _text    = model.Text;
        _month   = MonthOptions.FirstOrDefault(m => m.MonthsBack == model.MonthsBack)
                   ?? MonthOptions[1];
        _corp    = corps.FirstOrDefault(c => c.Id == model.CorpId) ?? corps.FirstOrDefault();
        _hideIsk = model.HideIsk;
        _posting = postings.FirstOrDefault(p => p.Id == model.PostingId);
        _projectType = ProjectTypeOptions.FirstOrDefault(t => t.Key == model.ProjectType)
                       ?? ProjectTypeOptions[0];
        _projectFilter = ProjectFilterOptions.FirstOrDefault(f => f.Key == model.ProjectFilter)
                       ?? ProjectFilterOptions[^1];
        // ⚠️ Taken as stored, blank included. A NEW section is filled with the default below;
        // a saved one is not, because by then blank is an answer somebody gave. Filling it in on
        // load was briefly done to protect sections written before blank meant anything, and it
        // made a cleared title come back every time the task was reopened.
        _sectionTitle = model.SectionTitle;
        _showHeaders       = model.ShowHeaders;
        _showIskLeft       = model.ShowIskLeft;
        _showLastCompleted = model.ShowLastCompleted;

        foreach (var (key, title) in ScheduledBlockRenderer.Top10Categories)
            Categories.Add(new CategoryChoice(key, title) { Selected = model.Categories.Contains(key) });

        // Industry Ops. A new section starts from the tool's defaults, never from what the tool
        // last remembered: each section is set up on its own.
        _oppsMode   = OppsModeOptions.FirstOrDefault(o => o.Key == model.OppsMode) ?? OppsModeOptions[0];
        _oppsMarket = Markets.FirstOrDefault(m => m.Id == model.OppsConfigId)
                      ?? (fresh ? Markets.FirstOrDefault() : null);
        _oppsMinIskVol     = Num(model.OppsMinIskVol30d);
        _oppsMinUnitVol    = Num(model.OppsMinUnitVol30d);
        _oppsMinMargin     = Num(model.OppsMinMarginPct);
        _oppsMinMargin30d  = Num(model.OppsMinMargin30dPct);
        int[] meta = fresh && IsOpps ? OppsMetaGroups.Default : [.. model.MetaGroupsOrDefault()];
        OppsMetaPicker = new MultiChoice(OppsMetaGroups.All.Select(g =>
            new CategoryChoice(g.Id.ToString(), g.Label) { Selected = meta.Contains(g.Id) }));
        _oppsBpoOnly       = model.OppsBpoOnly;
        _oppsOwnedOnly     = model.OppsOwnedOnly;
        _oppsExcludedIds   = [.. model.OppsExcludedGroupIds];
        _oppsIncludedIds   = [.. model.OppsIncludedGroupIds];
        // The item first, then the columns in the order saved; a saved header is what was typed.
        string[] columns = fresh && IsOpps ? IndustryOppsReport.DefaultColumns : [IndustryOppsReport.Item, .. model.OppsColumns];
        var known = IndustryOppsReport.Columns.ToDictionary(c => c.Key);
        foreach (var key in columns.Distinct().Where(known.ContainsKey))
            OppsTable.Add(Column(known[key], model.OppsHeaders.GetValueOrDefault(key)));
        AddOppsColumnCommand = ReactiveCommand.Create(AddOppsColumn);
        RefreshAddable();
        _oppsSortBy        = OppsSortOptions.FirstOrDefault(o => o.Key == model.OppsSortBy)
                             ?? OppsSortOptions.First(o => o.Key == IndustryOppsReport.DefaultSort);
        _oppsSortDirection = model.OppsSortDescending ? SortDirectionOptions[0] : SortDirectionOptions[1];
        _oppsRowLimit      = model.OppsRowLimit > 0 ? model.OppsRowLimit.ToString() : "";

        AddOppsExcludedCommand    = ReactiveCommand.CreateFromTask(() => AddOppsGroupAsync(_oppsExcludedIds, OppsExcluded));
        AddOppsIncludedCommand    = ReactiveCommand.CreateFromTask(() => AddOppsGroupAsync(_oppsIncludedIds, OppsIncluded));
        if (_oppsExcludedIds.Count + _oppsIncludedIds.Count > 0) _ = LoadOppsGroupNamesAsync();

        SelectAllCommand  = ReactiveCommand.Create(() => SelectAllProjects(true));
        SelectNoneCommand = ReactiveCommand.Create(() => SelectAllProjects(false));

        // A new section starts with the title it would write for itself, so the box shows what
        // will be posted rather than promising it from behind a watermark.
        if (fresh && IsProjects && _sectionTitle.Length == 0)
            _sectionTitle = DefaultTitle;
        if (fresh && IsOpps && _sectionTitle.Length == 0)
            _sectionTitle = AlarmsText.OppsDefaultTitle;

        if (IsProjects) _ = ReloadProjectsAsync();
    }

    public IReadOnlyList<CorpChoice>     Corps        { get; }
    public IReadOnlyList<PostingChoice>  Postings     { get; }
    public IReadOnlyList<LabelledChoice> ProjectTypes   => ProjectTypeOptions;
    public IReadOnlyList<LabelledChoice> ReportFilters  => ProjectFilterOptions;

    public ObservableCollection<CategoryChoice> Categories { get; } = [];
    public ObservableCollection<ProjectChoice>  Projects   { get; } = [];

    private string _type;
    public string Type
    {
        get => _type;
        set
        {
            this.RaiseAndSetIfChanged(ref _type, value);
            foreach (var n in new[]
                     {
                         nameof(IsText), nameof(NeedsCorp), nameof(NeedsMonth),
                         nameof(IsTop10), nameof(IsSale), nameof(IsProjects), nameof(IsChart), nameof(IsOpps),
                         nameof(Heading), nameof(ShowWebhookChartWarning),
                     })
                this.RaisePropertyChanged(n);

            if (IsProjects) _ = ReloadProjectsAsync();
        }
    }

    public bool IsText     => Type == MessageBlock.TypeText;

    /// <summary>A picture rather than text, and so a section a webhook cannot carry.</summary>
    public bool IsChart    => Type is MessageBlock.TypeIskChart or MessageBlock.TypeActivityChart
                                   or MessageBlock.TypeKillChart or MessageBlock.TypeMiningChart;
    public bool IsTop10    => Type == MessageBlock.TypeTop10;
    public bool IsSale     => Type == MessageBlock.TypeSale;
    public bool IsProjects => Type == MessageBlock.TypeProjects;
    public bool IsOpps     => Type == MessageBlock.TypeIndustryOpps;

    /// <summary>Standing projects need a corp too; only the two report blocks need a month.</summary>
    public bool NeedsCorp  => Type is MessageBlock.TypeTop10 or MessageBlock.TypeMonthly
                                   or MessageBlock.TypeProjects
                                   or MessageBlock.TypeIskChart
                                   or MessageBlock.TypeActivityChart
                                   or MessageBlock.TypeKillChart
                                   or MessageBlock.TypeMiningChart;
    public bool NeedsMonth => Type is MessageBlock.TypeTop10 or MessageBlock.TypeMonthly;

    public string Heading => Type switch
    {
        MessageBlock.TypeTop10    => AlarmsText.HeadingTop10,
        MessageBlock.TypeMonthly  => AlarmsText.HeadingMonthlySummary,
        MessageBlock.TypeSale     => AlarmsText.HeadingSalePosting,
        MessageBlock.TypeProjects => AlarmsText.HeadingStandingProjects,
        MessageBlock.TypeIndustryOpps => AlarmsText.HeadingIndustryOpps,
        MessageBlock.TypeIskChart      => AlarmsText.HeadingIskChart,
        MessageBlock.TypeActivityChart => AlarmsText.HeadingActivityChart,
        MessageBlock.TypeKillChart     => AlarmsText.HeadingKillChart,
        MessageBlock.TypeMiningChart   => AlarmsText.HeadingMiningChart,
        _                         => AlarmsText.HeadingText,
    };

    private string _text;
    public string Text { get => _text; set => this.RaiseAndSetIfChanged(ref _text, value); }

    /// <summary>Top 10: print the share of the total and not what it was worth.</summary>
    private bool _hideIsk;
    public bool HideIsk { get => _hideIsk; set => this.RaiseAndSetIfChanged(ref _hideIsk, value); }

    private CorpChoice? _corp;
    public CorpChoice? Corp
    {
        get => _corp;
        set
        {
            this.RaiseAndSetIfChanged(ref _corp, value);
            if (IsProjects) _ = ReloadProjectsAsync();
        }
    }

    private PostingChoice? _posting;
    public PostingChoice? Posting { get => _posting; set => this.RaiseAndSetIfChanged(ref _posting, value); }

    private LabelledChoice _projectFilter;

    /// <summary>Which rows the section reports. Does not touch the tick list: that chooses which
    /// projects are in scope, this chooses which of them are worth printing.</summary>
    public LabelledChoice ProjectFilter
    {
        get => _projectFilter;
        set
        {
            var followed = FollowsDefault;
            this.RaiseAndSetIfChanged(ref _projectFilter, value ?? ProjectFilterOptions[^1]);
            this.RaisePropertyChanged(nameof(DefaultTitle));
            if (followed) SectionTitle = DefaultTitle;
        }
    }

    /// <summary>
    /// The heading over the table. Empty means the one the section writes for itself.
    ///
    /// <para>Stored empty rather than filled in with today's default, so a section nobody retitled
    /// keeps following the type and filter it actually reports on.</para>
    /// </summary>
    private string _sectionTitle = "";
    public string SectionTitle { get => _sectionTitle; set => this.RaiseAndSetIfChanged(ref _sectionTitle, value); }

    /// <summary>
    /// Whether the box still holds the title this section would write for itself.
    ///
    /// <para>Changing the type or the filter rewrites the box while that is true, so a title
    /// nobody edited keeps describing what is actually being reported. Once it has been changed
    /// — to anything, including blank — it is left alone.</para>
    ///
    /// <para>⚠️ The default in ANY interface language, not only this run's: a section saved in
    /// one language still holds that language's default when it is reopened in another.</para>
    /// </summary>
    private bool FollowsDefault =>
        StandingProjectReport.IsDefaultTitle(SectionTitle,
                                             ProjectType?.Key ?? StandingProjectReport.DestroyNpc,
                                             ProjectFilter?.Key ?? EveConsole.Services.ProjectFilters.All);

    /// <summary>The title this section writes for itself, and what the box is filled with.</summary>
    public string DefaultTitle =>
        StandingProjectReport.DefaultTitle(ProjectType?.Key ?? StandingProjectReport.DestroyNpc,
                                           ProjectFilter?.Key ?? EveConsole.Services.ProjectFilters.All);

    private bool _showHeaders = true;
    public bool ShowHeaders { get => _showHeaders; set => this.RaiseAndSetIfChanged(ref _showHeaders, value); }

    private bool _showIskLeft = true;
    public bool ShowIskLeft { get => _showIskLeft; set => this.RaiseAndSetIfChanged(ref _showIskLeft, value); }

    private bool _showLastCompleted = true;
    public bool ShowLastCompleted
    {
        get => _showLastCompleted;
        set => this.RaiseAndSetIfChanged(ref _showLastCompleted, value);
    }

    private LabelledChoice _projectType;
    public LabelledChoice ProjectType
    {
        get => _projectType;
        set
        {
            var followed = FollowsDefault;
            this.RaiseAndSetIfChanged(ref _projectType, value ?? ProjectTypeOptions[0]);
            this.RaisePropertyChanged(nameof(DefaultTitle));
            if (followed) SectionTitle = DefaultTitle;
            _ = ReloadProjectsAsync();
        }
    }

    public IReadOnlyList<MonthBackChoice> Months => MonthOptions;

    private MonthBackChoice _month;
    public MonthBackChoice Month
    {
        get => _month;
        set
        {
            this.RaiseAndSetIfChanged(ref _month, value ?? MonthOptions[1]);
            this.RaisePropertyChanged(nameof(MonthsBackText));
        }
    }

    public int MonthsBack => Month?.MonthsBack ?? 1;

    /// <summary>Names the month it resolves to today, so nobody has to count backwards.</summary>
    public string MonthsBackText
    {
        get
        {
            var when = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1)
                           .AddMonths(-Math.Max(0, MonthsBack));

            return MonthsBack == 0
                ? string.Format(AlarmsText.MonthInProgress, when)
                : string.Format(AlarmsText.MonthAsThingsStand, when);
        }
    }

    /// <summary>
    /// Ticks or clears every project at once.
    ///
    /// <para>Sets the boxes rather than the set behind them: each box's own subscription keeps the
    /// stored list in step, so there is one path in and out of it.</para>
    /// </summary>
    public void SelectAllProjects(bool on)
    {
        foreach (var p in Projects) p.Selected = on;
    }

    public ReactiveCommand<Unit, Unit> SelectAllCommand  { get; }
    public ReactiveCommand<Unit, Unit> SelectNoneCommand { get; }

    /// <summary>
    /// True while this section draws a chart and the task is aimed at a webhook.
    ///
    /// <para>⚠️ Read off the task's destination, which lives a level up. The section is where
    /// somebody is looking when they add a chart, so the section is where the warning has to be
    /// — finding out at 00:01 from a status line is finding out too late.</para>
    /// </summary>
    public bool ShowWebhookChartWarning => IsChart && _destinationIsWebhook();

    private readonly Func<bool> _destinationIsWebhook;

    /// <summary>Called by the editor when the destination changes, since it is not this
    /// section's own property to watch.</summary>
    public void DestinationChanged() => this.RaisePropertyChanged(nameof(ShowWebhookChartWarning));

    private string _projectsNote = "";
    public string ProjectsNote { get => _projectsNote; private set => this.RaiseAndSetIfChanged(ref _projectsNote, value); }

    /// <summary>
    /// Rebuilds the tick list for the chosen corp and project type.
    ///
    /// <para>A new section ticks whatever it finds; a saved one ticks only what it stored. That is
    /// the whole rule — nothing joins a saved post on its own.</para>
    /// </summary>
    private async Task ReloadProjectsAsync()
    {
        Projects.Clear();
        ProjectsNote = "";

        if (_loadProjects is null || Corp is null || !IsProjects) return;

        // The labels name items and places as the interface does, so those names are in first.
        await SdeNames.EnsureLoadedAsync();

        List<Models.CorpStandingProject> list;
        try { list = [.. await _loadProjects(Corp.Id, ProjectType.Key)]; }
        catch (Exception ex) { ProjectsNote = string.Format(AlarmsText.ErrReadProjects, ex.Message); return; }

        if (list.Count == 0)
        {
            // One sentence per type: the type's label, lower-cased into the middle of a sentence,
            // only reads right in English.
            ProjectsNote = ProjectType.Key == StandingProjectReport.DeliverItem
                ? AlarmsText.NoDeliverProjects
                : AlarmsText.NoDestroyNpcProjects;
            return;
        }

        foreach (var p in list)
        {
            // Shown in the interface language; the section keeps the definition's id.
            var choice = new ProjectChoice(p.Id, StandingProjectReport.DescribeShown(p))
            {
                Selected = _fresh || _included.Contains(p.Id),
            };

            // The set is the record, not the boxes: the boxes are thrown away and rebuilt every
            // time the corp or the type changes. Subscribing fires once with the current value,
            // which is what seeds a fresh section's list.
            choice.WhenAnyValue(x => x.Selected)
                  .Subscribe(on => { if (on) _included.Add(choice.Id); else _included.Remove(choice.Id); });

            Projects.Add(choice);
        }
    }

    // ── Industry Ops ─────────────────────────────────────────────────────────

    public static readonly LabelledChoice[] OppsModeOptions =
    [
        new(nameof(IndustryMode.BuildAndSellOrder),      IndustryText.ModeBuildSellOrder),
        new(nameof(IndustryMode.BuildAndSellToBuyOrder), IndustryText.ModeBuildSellToBuyOrder),
    ];

    /// <summary>Any column, the item included, can be the sort.</summary>
    public static readonly LabelledChoice[] OppsSortOptions =
        [.. IndustryOppsReport.Columns.Select(c => new LabelledChoice(c.Key, c.Header))];

    public static readonly LabelledChoice[] SortDirectionOptions =
    [
        new("desc", AlarmsText.SortHighestFirst),
        new("asc",  AlarmsText.SortLowestFirst),
    ];

    public IReadOnlyList<LabelledChoice>     OppsModes          => OppsModeOptions;
    public IReadOnlyList<LabelledChoice>     OppsSorts          => OppsSortOptions;
    public IReadOnlyList<LabelledChoice>     SortDirections     => SortDirectionOptions;
    public IReadOnlyList<MarketConfigChoice> Markets            { get; }

    private LabelledChoice _oppsMode;
    public LabelledChoice OppsMode { get => _oppsMode; set => this.RaiseAndSetIfChanged(ref _oppsMode, value ?? OppsModeOptions[0]); }

    private MarketConfigChoice? _oppsMarket;
    public MarketConfigChoice? OppsMarket { get => _oppsMarket; set => this.RaiseAndSetIfChanged(ref _oppsMarket, value); }

    // The four boxes hold what was typed; a blank box is no filter. Checked on Save.
    private string _oppsMinIskVol, _oppsMinUnitVol, _oppsMinMargin, _oppsMinMargin30d;
    public string OppsMinIskVol    { get => _oppsMinIskVol;    set => this.RaiseAndSetIfChanged(ref _oppsMinIskVol, value); }
    public string OppsMinUnitVol   { get => _oppsMinUnitVol;   set => this.RaiseAndSetIfChanged(ref _oppsMinUnitVol, value); }
    public string OppsMinMargin    { get => _oppsMinMargin;    set => this.RaiseAndSetIfChanged(ref _oppsMinMargin, value); }
    public string OppsMinMargin30d { get => _oppsMinMargin30d; set => this.RaiseAndSetIfChanged(ref _oppsMinMargin30d, value); }

    private bool _oppsBpoOnly, _oppsOwnedOnly;

    /// <summary>The meta groups, ticked or not, as one dropdown. A new section starts as the tool does.</summary>
    public MultiChoice OppsMetaPicker { get; }
    public bool OppsBpoOnly     { get => _oppsBpoOnly;     set => this.RaiseAndSetIfChanged(ref _oppsBpoOnly, value); }
    public bool OppsOwnedOnly   { get => _oppsOwnedOnly;   set => this.RaiseAndSetIfChanged(ref _oppsOwnedOnly, value); }

    /// <summary>
    /// The groups chosen, by id. ⚠️ The ids are the record, the chips beside them are only how
    /// they are shown: the names arrive a moment after the section is built, and reading the
    /// chips then would make a freshly opened task look edited.
    /// </summary>
    private readonly List<int> _oppsExcludedIds, _oppsIncludedIds;
    public ObservableCollection<MarketGroupChip> OppsExcluded { get; } = [];
    public ObservableCollection<MarketGroupChip> OppsIncluded { get; } = [];

    public ReactiveCommand<Unit, Unit> AddOppsExcludedCommand { get; }
    public ReactiveCommand<Unit, Unit> AddOppsIncludedCommand { get; }

    private readonly Func<Task<MarketGroupPickerResult?>>? _pickGroup;

    private async Task AddOppsGroupAsync(List<int> ids, ObservableCollection<MarketGroupChip> shown)
    {
        if (_pickGroup is null || await _pickGroup() is not { } pick) return;
        if (ids.Contains(pick.MarketGroupId)) return;
        ids.Add(pick.MarketGroupId);
        shown.Add(Chip(ids, shown, pick.MarketGroupId, SdeNames.MarketGroup(pick.MarketGroupId, pick.GroupName)));
    }

    /// <summary>A chip whose ✕ takes the group out of both the ids and the chips.</summary>
    private static MarketGroupChip Chip(List<int> ids, ObservableCollection<MarketGroupChip> shown, int id, string name) =>
        new(id, name, chip => { ids.Remove(chip.MarketGroupId); shown.Remove(chip); });

    private async Task LoadOppsGroupNamesAsync()
    {
        Dictionary<int, string> names;
        try { names = await Task.Run(() => IndustryOpportunitiesService.MarketGroupNamesAsync([.. _oppsExcludedIds, .. _oppsIncludedIds])); }
        catch { names = []; }
        // A group the SDE no longer has is still shown, by number, so it can be removed.
        foreach (var id in _oppsExcludedIds) OppsExcluded.Add(Chip(_oppsExcludedIds, OppsExcluded, id, names.GetValueOrDefault(id, $"#{id}")));
        foreach (var id in _oppsIncludedIds) OppsIncluded.Add(Chip(_oppsIncludedIds, OppsIncluded, id, names.GetValueOrDefault(id, $"#{id}")));
    }

    /// <summary>The table's columns, the item first, in the order printed.</summary>
    public ObservableCollection<OppsColumnVm> OppsTable { get; } = [];

    /// <summary>The columns not in the table yet, for the Add dropdown.</summary>
    public ObservableCollection<LabelledChoice> OppsAddable { get; } = [];

    private LabelledChoice? _oppsToAdd;
    public LabelledChoice? OppsToAdd { get => _oppsToAdd; set => this.RaiseAndSetIfChanged(ref _oppsToAdd, value); }

    public ReactiveCommand<Unit, Unit> AddOppsColumnCommand { get; }

    private OppsColumnVm Column(IndustryOppsReport.Column c, string? header) => new(
        c.Key, c.Header, header is { Length: > 0 } ? header : c.Header, c.Key == IndustryOppsReport.Item,
        col => { OppsTable.Remove(col); RefreshAddable(); },
        (col, by) =>
        {
            // The item holds the first place: nothing moves above it.
            var i = OppsTable.IndexOf(col);
            var j = i + by;
            if (i < 0 || j < 1 || j >= OppsTable.Count) return;
            OppsTable.Move(i, j);
        });

    private void AddOppsColumn()
    {
        if (OppsToAdd is not { } pick) return;
        var c = IndustryOppsReport.Columns.First(x => x.Key == pick.Key);
        OppsTable.Add(Column(c, null));
        RefreshAddable();
    }

    /// <summary>The dropdown offers what is not in the table, in the grid's order, the first picked.</summary>
    private void RefreshAddable()
    {
        OppsAddable.Clear();
        foreach (var c in IndustryOppsReport.Columns.Where(c => OppsTable.All(t => t.Key != c.Key)))
            OppsAddable.Add(new LabelledChoice(c.Key, c.Header));
        OppsToAdd = OppsAddable.FirstOrDefault();
    }

    private LabelledChoice _oppsSortBy;
    public LabelledChoice OppsSortBy { get => _oppsSortBy; set => this.RaiseAndSetIfChanged(ref _oppsSortBy, value ?? OppsSortOptions[0]); }

    private LabelledChoice _oppsSortDirection;
    public LabelledChoice OppsSortDirection { get => _oppsSortDirection; set => this.RaiseAndSetIfChanged(ref _oppsSortDirection, value ?? SortDirectionOptions[0]); }

    /// <summary>How many rows, as typed. Blank is all of them.</summary>
    private string _oppsRowLimit;
    public string OppsRowLimit { get => _oppsRowLimit; set => this.RaiseAndSetIfChanged(ref _oppsRowLimit, value); }

    private static string Num(double? v) => v?.ToString("0.##########") ?? "";

    /// <summary>A box's number: null when blank, and when it is not a number of 0 or more
    /// (which <see cref="OppsBadField"/> reports). "15%" reads as 15.</summary>
    private static double? Parse(string text) =>
        double.TryParse(text.Trim().TrimEnd('%'), out var v) && v >= 0 ? v : null;

    /// <summary>The label of the first box holding something other than a number of 0 or more
    /// (or, for Rows, a whole one), or null when all of them are fine.</summary>
    public string? OppsBadField
    {
        get
        {
            foreach (var (text, label) in new[]
                     {
                         (OppsMinIskVol,    IndustryText.Min30dIskVol),
                         (OppsMinUnitVol,   IndustryText.Min30dUnitVol),
                         (OppsMinMargin,    IndustryText.MinMarginPct),
                         (OppsMinMargin30d, IndustryText.MinMargin30dPct),
                     })
                if (text.Trim().Length > 0 && Parse(text) is null) return label;

            if (OppsRowLimit.Trim().Length > 0 && !(int.TryParse(OppsRowLimit.Trim(), out var n) && n >= 0))
                return AlarmsText.RowLimitLabel;
            return null;
        }
    }

    /// <summary>
    /// This section is now on disk, so stop defaulting new projects to ticked.
    ///
    /// <para>⚠️ Without this, switching project type after a save would re-tick everything under
    /// the new type — which is exactly the surprise the stored list exists to prevent.</para>
    /// </summary>
    public void MarkSaved() => _fresh = false;

    public MessageBlock ToModel() => new()
    {
        Type               = Type,
        Text               = Text,
        CorpId             = Corp?.Id ?? 0,
        MonthsBack         = MonthsBack,
        Categories         = [.. Categories.Where(c => c.Selected).Select(c => c.Key)],
        HideIsk            = HideIsk,
        PostingId          = Posting?.Id ?? 0,
        ProjectType        = ProjectType?.Key ?? StandingProjectReport.DestroyNpc,
        ProjectFilter      = ProjectFilter?.Key ?? EveConsole.Services.ProjectFilters.All,
        SectionTitle       = SectionTitle.Trim(),
        ShowHeaders        = ShowHeaders,
        ShowIskLeft        = ShowIskLeft,
        ShowLastCompleted  = ShowLastCompleted,
        // Sorted, so two identical selections always render the same JSON. The unsaved-changes
        // check compares that text, and a HashSet's order is not a promise.
        IncludedProjectIds = [.. _included.OrderBy(x => x)],

        OppsMode             = OppsMode.Key,
        OppsConfigId         = OppsMarket?.Id ?? 0,
        OppsMinIskVol30d     = Parse(OppsMinIskVol),
        OppsMinUnitVol30d    = Parse(OppsMinUnitVol),
        OppsMinMarginPct     = Parse(OppsMinMargin),
        OppsMinMargin30dPct  = Parse(OppsMinMargin30d),
        OppsMetaGroups       = [.. OppsMetaPicker.SelectedKeys.Select(int.Parse)],
        OppsBpoOnly          = OppsBpoOnly,
        OppsOwnedOnly        = OppsOwnedOnly,
        OppsExcludedGroupIds = [.. _oppsExcludedIds],
        OppsIncludedGroupIds = [.. _oppsIncludedIds],
        OppsColumns          = [.. OppsTable.Where(c => !c.IsItem).Select(c => c.Key)],
        // Only headers changed from the column's own: one left alone follows the interface
        // language, and a blank box means the column's own header too.
        OppsHeaders          = OppsTable
            .Where(c => c.Header.Trim().Length > 0 && c.Header.Trim() != c.Name)
            .ToDictionary(c => c.Key, c => c.Header.Trim()),
        OppsSortBy           = OppsSortBy.Key,
        OppsSortDescending   = OppsSortDirection.Key == "desc",
        OppsRowLimit         = int.TryParse(OppsRowLimit.Trim(), out var limit) && limit > 0 ? limit : 0,
    };
}

/// <summary>
/// The Scheduler tool: a list of tasks, and an editor with the schedule on one side and the
/// message being composed on the other.
/// </summary>
public sealed class SchedulerViewModel : ReactiveObject
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly SchedulerService                _scheduler;
    private readonly ScheduledBlockRenderer          _renderer;
    private readonly SlackService                    _slack;
    private readonly DiscordService                  _discord;
    private readonly CorpActivityService             _corp;
    private readonly SalePostingService              _sales;
    private readonly AppErrorLogger                  _errors;

    /// <summary>For the market group picker an Industry Ops section opens.</summary>
    public BatchAddService Batch { get; }

    /// <summary>
    /// Opens the market group picker and returns what was chosen. Set by the view, which is the
    /// only thing with a window to hang the dialog off.
    /// </summary>
    public Func<Task<MarketGroupPickerResult?>>? PickMarketGroup { get; set; }

    public SchedulerViewModel(
        IDbContextFactory<AppDbContext> dbFactory,
        SchedulerService                scheduler,
        ScheduledBlockRenderer          renderer,
        SlackService                    slack,
        DiscordService                  discord,
        CorpActivityService             corp,
        SalePostingService              sales,
        AppErrorLogger                  errors,
        BatchAddService                 batch)
    {
        Batch      = batch;
        _dbFactory = dbFactory;
        _scheduler = scheduler;
        _renderer  = renderer;
        _slack     = slack;
        _discord   = discord;
        _corp      = corp;
        _sales     = sales;
        _errors    = errors;

        NewCommand     = ReactiveCommand.CreateFromTask(NewTaskAsync);
        SaveCommand    = ReactiveCommand.CreateFromTask(SaveAsync);
        DiscardCommand = ReactiveCommand.CreateFromTask(DiscardAsync);
        DeleteCommand  = ReactiveCommand.CreateFromTask(DeleteAsync);
        RefreshCommand = ReactiveCommand.CreateFromTask(RefreshAsync);
        RunNowCommand  = ReactiveCommand.CreateFromTask(RunNowAsync);
        PreviewCommand = ReactiveCommand.CreateFromTask(PreviewAsync);

        AddSectionCommand = ReactiveCommand.Create(AddSection);

        MoveUpCommand   = ReactiveCommand.Create<MessageBlockVm>(b => Move(b, -1));
        MoveDownCommand = ReactiveCommand.Create<MessageBlockVm>(b => Move(b, +1));
        RemoveCommand   = ReactiveCommand.Create<MessageBlockVm>(b => Blocks.Remove(b));

        for (var i = 0; i < 7; i++)
            Days.Add(new DayChoice(i, DayNames[i]) { Selected = true });

        PickKind(ScheduleKind.Weekly);
        PickTaskType(ScheduledTaskType.SlackPost);
        SelectedSectionType = SectionTypes[0];

        // ⚠️ A command that throws otherwise fails in silence: ReactiveUI routes the exception
        // here and nowhere else, so the button just appears not to work. Every failure now says
        // so on the line beside the buttons.
        foreach (var cmd in new IHandleObservableErrors[]
                 {
                     NewCommand, SaveCommand, DiscardCommand, DeleteCommand, RefreshCommand, RunNowCommand,
                     PreviewCommand, AddSectionCommand,
                     MoveUpCommand, MoveDownCommand, RemoveCommand,
                 })
        {
            cmd.ThrownExceptions.Subscribe(ex =>
            {
                StatusText = string.Format(AlarmsText.StatusCommandFailed, ex.Message);
                _errors.Log(nameof(SchedulerViewModel), "command", ex);
            });
        }

        this.WhenAnyValue(x => x.SelectedTask)
            .Where(t => t is not null)
            .Subscribe(t => _ = SelectAsync(t!));

        // A task that fires while the tool is open should show its new last-run without the
        // user going looking for Refresh.
        scheduler.TasksChanged += OnTasksChanged;
    }

    private void OnTasksChanged() => Dispatcher.UIThread.Post(() => _ = LoadAsync());

    /// <summary>
    /// Moving to another task, with a stop if the current one has unsaved edits.
    ///
    /// <para>⚠️ Puts the selection back when the answer is no. The list has already moved by the
    /// time this runs — a ListBox selects on click — so declining has to undo it, and the
    /// guard stops that undo from being read as another selection.</para>
    /// </summary>
    private bool _reselecting;

    private async Task SelectAsync(ScheduledTaskRowVm row)
    {
        if (_reselecting) return;

        if (row.Id != EditingId && !await MayDiscardAsync(AlarmsText.DiscardMoveToTask))
        {
            var back = Tasks.FirstOrDefault(t => t.Id == EditingId);
            _reselecting = true;
            try { SelectedTask = back; }
            finally { _reselecting = false; }
            return;
        }

        await LoadEditorAsync(row.Id);
    }

    // ── Lists ────────────────────────────────────────────────────────────────

    public ObservableCollection<ScheduledTaskRowVm> Tasks       { get; } = [];
    public ObservableCollection<MessageBlockVm>     Blocks      { get; } = [];
    public ObservableCollection<SlackDestination>   Destinations { get; } = [];
    public ObservableCollection<DayChoice>          Days        { get; } = [];
    public ObservableCollection<CorpChoice>         Corps       { get; } = [];
    public ObservableCollection<PostingChoice>      Postings    { get; } = [];
    public ObservableCollection<MarketConfigChoice> Markets     { get; } = [];

    public List<LabelledChoice> TaskTypes { get; } =
    [
        new(ScheduledTaskType.SlackPost,  AlarmsText.TaskTypeSlackOrDiscordPost),
        new(ScheduledTaskType.RaiseAlert, AlarmsText.TaskTypeRaiseAlert),
    ];

    /// <summary>What a message section can be. One list, one Add button.</summary>
    public List<LabelledChoice> SectionTypes { get; } =
    [
        new(MessageBlock.TypeText,     AlarmsText.SectionTypeText),
        new(MessageBlock.TypeTop10,    AlarmsText.SectionTypeTop10),
        new(MessageBlock.TypeMonthly,  AlarmsText.SectionTypeMonthly),
        new(MessageBlock.TypeSale,     AlarmsText.SectionTypeSale),
        new(MessageBlock.TypeProjects, AlarmsText.SectionTypeProjects),
        new(MessageBlock.TypeIndustryOpps, AlarmsText.SectionTypeIndustryOpps),
        new(MessageBlock.TypeIskChart,      AlarmsText.SectionTypeIskChart),
        new(MessageBlock.TypeActivityChart, AlarmsText.SectionTypeActivityChart),
        new(MessageBlock.TypeKillChart,     AlarmsText.SectionTypeKillChart),
        new(MessageBlock.TypeMiningChart,   AlarmsText.SectionTypeMiningChart),
    ];

    private LabelledChoice? _selectedSectionType;
    public LabelledChoice? SelectedSectionType
    {
        get => _selectedSectionType;
        set => this.RaiseAndSetIfChanged(ref _selectedSectionType, value);
    }

    public List<LabelledChoice> Kinds { get; } =
    [
        new(ScheduleKind.Interval, AlarmsText.KindInterval),
        new(ScheduleKind.Weekly,   AlarmsText.KindWeekly),
        new(ScheduleKind.Monthly,  AlarmsText.KindMonthly),
        new(ScheduleKind.Yearly,   AlarmsText.KindYearly),
    ];

    /// <summary>
    /// The days of the week as the tick boxes name them, Sunday first — the bit a day is saved
    /// under is its place in this list, as with <see cref="DayOfWeek"/>.
    /// </summary>
    private static readonly string[] DayNames =
    [
        AlarmsText.DaySun, AlarmsText.DayMon, AlarmsText.DayTue, AlarmsText.DayWed,
        AlarmsText.DayThu, AlarmsText.DayFri, AlarmsText.DaySat,
    ];

    /// <summary>
    /// Calendar month names, for a yearly task's chosen month.
    ///
    /// <para>The month is saved as its place in this list (see Snapshot), never as the name, so
    /// the names can be in any language.</para>
    /// </summary>
    public List<string> MonthNames { get; } =
    [
        AlarmsText.MonthJanuary, AlarmsText.MonthFebruary, AlarmsText.MonthMarch,
        AlarmsText.MonthApril,   AlarmsText.MonthMay,      AlarmsText.MonthJune,
        AlarmsText.MonthJuly,    AlarmsText.MonthAugust,   AlarmsText.MonthSeptember,
        AlarmsText.MonthOctober, AlarmsText.MonthNovember, AlarmsText.MonthDecember,
    ];

    private ScheduledTaskRowVm? _selectedTask;
    public ScheduledTaskRowVm? SelectedTask
    {
        get => _selectedTask;
        set => this.RaiseAndSetIfChanged(ref _selectedTask, value);
    }

    // ── Editor ───────────────────────────────────────────────────────────────

    private int _editingId;
    public int EditingId { get => _editingId; private set => this.RaiseAndSetIfChanged(ref _editingId, value); }

    private bool _hasEditor;
    public bool HasEditor { get => _hasEditor; private set => this.RaiseAndSetIfChanged(ref _hasEditor, value); }

    private string _name = "";
    public string Name { get => _name; set => this.RaiseAndSetIfChanged(ref _name, value); }

    private bool _enabled = true;
    public bool Enabled { get => _enabled; set => this.RaiseAndSetIfChanged(ref _enabled, value); }

    private LabelledChoice? _selectedTaskType;
    public LabelledChoice? SelectedTaskType
    {
        get => _selectedTaskType;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedTaskType, value);
            this.RaisePropertyChanged(nameof(TaskType));
            this.RaisePropertyChanged(nameof(IsSlackPost));
            this.RaisePropertyChanged(nameof(IsRaiseAlert));
            this.RaisePropertyChanged(nameof(CanPreview));
        }
    }

    public string TaskType => SelectedTaskType?.Key ?? ScheduledTaskType.SlackPost;

    public bool IsSlackPost  => TaskType == ScheduledTaskType.SlackPost;
    public bool IsRaiseAlert => TaskType == ScheduledTaskType.RaiseAlert;

    /// <summary>Preview renders blocks. An alert is the text you already typed.</summary>
    public bool CanPreview => IsSlackPost;

    private void PickTaskType(string key) =>
        SelectedTaskType = TaskTypes.FirstOrDefault(t => t.Key == key) ?? TaskTypes[0];

    /// <summary>Slack posts: stay silent unless a dynamic section actually said something.</summary>
    private bool _skipIfNoDynamicContent;
    public bool SkipIfNoDynamicContent
    {
        get => _skipIfNoDynamicContent;
        set => this.RaiseAndSetIfChanged(ref _skipIfNoDynamicContent, value);
    }

    /// <summary>Alerts: the headline. Empty falls back to the task's own name.</summary>
    private string _alertTitle = "";
    public string AlertTitle { get => _alertTitle; set => this.RaiseAndSetIfChanged(ref _alertTitle, value); }

    /// <summary>Alerts: what it says, under the headline.</summary>
    private string _alertText = "";
    public string AlertText { get => _alertText; set => this.RaiseAndSetIfChanged(ref _alertText, value); }

    private LabelledChoice? _selectedKind;
    public LabelledChoice? SelectedKind
    {
        get => _selectedKind;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedKind, value);
            this.RaisePropertyChanged(nameof(Kind));
            this.RaisePropertyChanged(nameof(IsInterval));
            this.RaisePropertyChanged(nameof(IsWeekly));
            this.RaisePropertyChanged(nameof(IsMonthly));
            this.RaisePropertyChanged(nameof(IsYearly));
            this.RaisePropertyChanged(nameof(HasClock));
            this.RaisePropertyChanged(nameof(CanSkipIfMissed));
            this.RaisePropertyChanged(nameof(ScheduleHint));
        }
    }

    /// <summary>The stored key. Derived from the picker rather than kept beside it, so there is
    /// one thing to set and no pair to fall out of step.</summary>
    public string Kind => SelectedKind?.Key ?? ScheduleKind.Weekly;

    /// <summary>
    /// What happens when the app was closed at the time — the only part of scheduling anyone
    /// actually asks about.
    ///
    /// <para>One line that changes with the kind, rather than four blocks each showing and hiding.
    /// The schedule fields sit in a row now, and four stacked paragraphs under them would be the
    /// tallest thing on a screen whose real subject is below.</para>
    /// </summary>
    public string ScheduleHint => Kind switch
    {
        ScheduleKind.Interval =>
            AlarmsText.HintInterval,

        ScheduleKind.Weekly =>
            AlarmsText.HintWeekly,

        ScheduleKind.Monthly =>
            AlarmsText.HintMonthly,

        ScheduleKind.Yearly =>
            AlarmsText.HintYearly,

        _ => "",
    };

    private void PickKind(string key) =>
        SelectedKind = Kinds.FirstOrDefault(k => k.Key == key) ?? Kinds[1];

    public bool IsInterval => Kind == ScheduleKind.Interval;
    public bool IsWeekly   => Kind == ScheduleKind.Weekly;
    public bool IsMonthly  => Kind == ScheduleKind.Monthly;
    public bool IsYearly   => Kind == ScheduleKind.Yearly;
    public bool HasClock   => !IsInterval;

    /// <summary>
    /// ⚠️ Monthly and yearly only, and deliberately. On an interval or a weekly task the run
    /// happens whenever the minute-by-minute loop next looks, so "was it done on the day" is a
    /// question about the poll rather than about the schedule.
    /// </summary>
    public bool CanSkipIfMissed => IsMonthly || IsYearly;

    private int _intervalValue = 1;
    public int IntervalValue { get => _intervalValue; set => this.RaiseAndSetIfChanged(ref _intervalValue, Math.Max(1, value)); }

    private bool _intervalInHours = true;
    public bool IntervalInHours
    {
        get => _intervalInHours;
        set
        {
            this.RaiseAndSetIfChanged(ref _intervalInHours, value);
            this.RaisePropertyChanged(nameof(IntervalInMinutes));
        }
    }

    /// <summary>
    /// The other half of the pair.
    ///
    /// <para>Its own property rather than a negated binding, so each radio button writes something
    /// it owns. Unchecking is ignored: in a radio pair the button being turned ON is the one that
    /// carries the choice.</para>
    /// </summary>
    public bool IntervalInMinutes
    {
        get => !_intervalInHours;
        set { if (value) IntervalInHours = false; }
    }

    /// <summary>The time of day, EVE time, as "HH:mm".</summary>
    private string _timeOfDay = "00:01";
    public string TimeOfDay { get => _timeOfDay; set => this.RaiseAndSetIfChanged(ref _timeOfDay, value); }

    private int _dayOfMonth = 1;
    public int DayOfMonth { get => _dayOfMonth; set => this.RaiseAndSetIfChanged(ref _dayOfMonth, Math.Clamp(value, 1, 31)); }

    private string _monthOfYear = AlarmsText.MonthJanuary;
    public string MonthOfYear { get => _monthOfYear; set => this.RaiseAndSetIfChanged(ref _monthOfYear, value); }

    private bool _skipIfMissed;
    public bool SkipIfMissed { get => _skipIfMissed; set => this.RaiseAndSetIfChanged(ref _skipIfMissed, value); }

    private SlackDestination? _destination;
    public SlackDestination? Destination
    {
        get => _destination;
        set
        {
            this.RaiseAndSetIfChanged(ref _destination, value);

            // A chart section's warning depends on this, and a section cannot watch a property
            // that is not its own.
            foreach (var b in Blocks) b.DestinationChanged();
        }
    }

    private string _statusText = "";
    public string StatusText { get => _statusText; private set => this.RaiseAndSetIfChanged(ref _statusText, value); }

    private string _previewText = "";
    public string PreviewText
    {
        get => _previewText;
        private set
        {
            this.RaiseAndSetIfChanged(ref _previewText, value);
            this.RaisePropertyChanged(nameof(HasPreview));
        }
    }

    public bool HasPreview => PreviewText.Length > 0;

    public ReactiveCommand<Unit, Unit> NewCommand     { get; }
    public ReactiveCommand<Unit, Unit> SaveCommand    { get; }
    public ReactiveCommand<Unit, Unit> DiscardCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteCommand  { get; }
    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<Unit, Unit> RunNowCommand  { get; }
    public ReactiveCommand<Unit, Unit> PreviewCommand { get; }

    public ReactiveCommand<Unit, Unit> AddSectionCommand { get; }

    public ReactiveCommand<MessageBlockVm, Unit> MoveUpCommand   { get; }
    public ReactiveCommand<MessageBlockVm, Unit> MoveDownCommand { get; }
    public ReactiveCommand<MessageBlockVm, Unit> RemoveCommand   { get; }

    // ── Loading ──────────────────────────────────────────────────────────────

    private async Task RefreshAsync()
    {
        if (!await MayDiscardAsync(AlarmsText.DiscardRefresh)) return;
        await ReloadAsync();
    }

    /// <summary>
    /// Reloads everything, the Slack channel list included.
    ///
    /// <para>⚠️ Clears nothing itself. Both lists are bound to combo boxes in the open editor, and
    /// a Clear pushes null through those bindings — which reads as the user picking nothing, and
    /// loses the choice before there is a new list to restore it from.</para>
    /// </summary>
    public async Task ReloadAsync()
    {
        await LoadDestinationsAsync();
        await LoadAsync();
    }

    public async Task LoadAsync()
    {
        await LoadCorpsAsync();
        await LoadPostingsAsync();
        await LoadMarketsAsync();

        // ⚠️ Only when there is nothing to show. This also runs on every background tick that
        // changed a task, and asking Slack for its channel list once a minute would be a network
        // round trip to redraw a list that has not moved. Refresh is how you ask for a new one.
        if (Destinations.Count == 0) await LoadDestinationsAsync();

        var rows = await Task.Run(async () =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            return await db.ScheduledTasks.AsNoTracking()
                           .OrderBy(t => t.Name)
                           .ToListAsync();
        });

        var keep = SelectedTask?.Id ?? 0;

        Tasks.Clear();
        foreach (var t in rows)
            Tasks.Add(new ScheduledTaskRowVm
            {
                Id          = t.Id,
                Name        = t.Name.Length > 0 ? t.Name : AlarmsText.UnnamedTask,
                Schedule    = ScheduleDue.Describe(t),
                Enabled     = t.Enabled,
                LastRunText = t.LastRunUtc is null
                                  ? AlarmsText.NeverRun
                                  : string.Format(AlarmsText.LastRun, t.LastRunUtc.Value.UtcDateTime),
                LastResult  = t.LastResult,
            });

        // Re-select by id rather than by object: the rows above are new instances, so the old
        // selection would otherwise clear itself and take the open editor with it.
        if (keep != 0)
            _selectedTask = Tasks.FirstOrDefault(r => r.Id == keep);
        this.RaisePropertyChanged(nameof(SelectedTask));

        StatusText = string.Format(AlarmsText.TaskCount, Tasks.Count);
    }

    /// <summary>
    /// ⚠️ Merged, never replaced. A block's chosen corp is bound to this collection, so a Clear
    /// would push null through that binding and lose the choice. Merging also means a corp added
    /// since the tool was opened turns up without a restart.
    /// </summary>
    private async Task LoadCorpsAsync()
    {
        var corps = await Task.Run(async () =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            return await db.Corporations.AsNoTracking()
                           .OrderBy(c => c.Name)
                           .Select(c => new { c.Id, c.Name })
                           .ToListAsync();
        });

        foreach (var c in corps)
            if (Corps.All(x => x.Id != c.Id)) Corps.Add(new CorpChoice(c.Id, c.Name));
    }

    /// <summary>Merged for the same reason the corps are: a section's chosen posting is bound to
    /// this collection, and a Clear would push null through that binding.</summary>
    private async Task LoadPostingsAsync()
    {
        List<Models.SalePosting> rows;
        try   { rows = await _sales.LoadPostingsAsync(); }
        catch (Exception ex) { _errors.Log(nameof(SchedulerViewModel), "postings", ex);
            StatusText = AppErrorLogger.Line("Error loading postings", ex);
            return; }

        foreach (var p in rows)
            if (Postings.All(x => x.Id != p.Id)) Postings.Add(new PostingChoice(p.Id, p.Name));
    }

    /// <summary>The markets an Industry Ops section can price at. Merged for the same reason the
    /// corps are: a section's chosen market is bound to this collection.</summary>
    private async Task LoadMarketsAsync()
    {
        List<IndustryMarketConfig> rows;
        try   { rows = await Task.Run(IndustryOpportunitiesService.LoadMarketConfigsAsync); }
        catch (Exception ex) { _errors.Log(nameof(SchedulerViewModel), "markets", ex); return; }

        foreach (var m in rows)
            if (Markets.All(x => x.Id != m.ConfigId)) Markets.Add(new MarketConfigChoice(m.ConfigId, m.Name));
    }

    /// <summary>
    /// The standing project DEFINITIONS of one type, for a section's tick list.
    ///
    /// <para>⚠️ Definitions, not the expanded rows. A region-scoped project is one thing to tick
    /// even though it reports as a row per qualifying system.</para>
    /// </summary>
    private async Task<IReadOnlyList<Models.CorpStandingProject>> LoadStandingProjectsAsync(
        long corpId, string projectType)
    {
        // ⚠️ Off the UI thread. This runs from a combo box setter, and EF over SQLite completes
        // synchronously often enough that awaiting it on the UI thread is a stall, not a yield.
        var all = await Task.Run(() => _corp.GetStandingProjectsAsync(corpId));
        return [.. all.Where(p => p.ProjectType == projectType)];
    }

    /// <summary>
    /// The same list the Slack settings offer: workspace channels first, then webhooks under a
    /// "Webhook: " prefix — and after them Discord's webhooks under "Discord: ". One list, because
    /// from a task's point of view they are one choice.
    ///
    /// <para>⚠️ Discord's are their own kind with their own prefix. A Slack webhook and a Discord
    /// one of the same name are different rows in different tables, and only one of them can
    /// carry a chart.</para>
    /// </summary>
    private async Task LoadDestinationsAsync()
    {
        var picked = Destination;
        Destinations.Clear();

        if (_slack.HasToken)
        {
            var (channels, _) = await _slack.ListChannelsAsync();
            foreach (var c in channels.OrderBy(c => c.Name))
                Destinations.Add(new SlackDestination(SlackDestination.KindChannel, c.Id, "#" + c.Name));
        }

        foreach (var w in await _slack.WebhooksAsync())
            Destinations.Add(new SlackDestination(
                SlackDestination.KindWebhook, w.Id.ToString(), string.Format(AlarmsText.WebhookDestination, w.Name), w.Url));

        // ⚠️ No URL carried: a Discord link is a secret, and the task keeps only the row's id.
        foreach (var w in await _discord.WebhooksAsync())
            Destinations.Add(new SlackDestination(
                SlackDestination.KindDiscord, w.Id.ToString(), string.Format(AlarmsText.DiscordDestination, w.Name)));

        if (picked is not null)
            Destination = Destinations.FirstOrDefault(d => d.Kind == picked.Kind && d.Id == picked.Id);
    }

    private int _editorLoadedFor = -1;

    private async Task LoadEditorAsync(int id)
    {
        // ⚠️ The list rebuilds its rows on every background tick, and re-selecting by id looks
        // like a fresh selection from here. Re-reading the editor then would throw away whatever
        // was half-typed into it. Selecting a DIFFERENT task still loads, which is the only time
        // anyone means it to.
        if (id == _editorLoadedFor && HasEditor) return;

        var task = await Task.Run(async () =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            return await db.ScheduledTasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id);
        });

        if (task is null) return;

        EditingId = task.Id;
        Name      = task.Name;
        Enabled   = task.Enabled;
        PickKind(task.Kind);
        PickTaskType(task.TaskType);

        if (task.IntervalMinutes % 60 == 0 && task.IntervalMinutes >= 60)
        {
            IntervalInHours = true;
            IntervalValue   = task.IntervalMinutes / 60;
        }
        else
        {
            IntervalInHours = false;
            IntervalValue   = task.IntervalMinutes;
        }

        TimeOfDay    = $"{task.TimeOfDayMinutes / 60:00}:{task.TimeOfDayMinutes % 60:00}";
        DayOfMonth   = task.DayOfMonth;
        MonthOfYear  = MonthNames[Math.Clamp(task.MonthOfYear, 1, 12) - 1];
        SkipIfMissed = task.SkipIfMissed;

        foreach (var d in Days) d.Selected = (task.DaysOfWeek & (1 << d.Bit)) != 0;

        var cfg = ScheduledTaskConfig.FromJson(task.Config);

        SkipIfNoDynamicContent = cfg.SkipIfNoDynamicContent;
        AlertTitle  = cfg.AlertTitle;
        AlertText   = cfg.AlertText;
        Destination = Destinations.FirstOrDefault(
            d => d.Kind == cfg.DestinationKind && d.Id == cfg.DestinationId);

        Blocks.Clear();
        foreach (var b in cfg.Blocks) Blocks.Add(NewBlock(b, fresh: false));

        PreviewText      = "";
        HasEditor        = true;
        _editorLoadedFor = task.Id;
        MarkClean();
    }

    private async Task NewTaskAsync()
    {
        if (!await MayDiscardAsync(AlarmsText.DiscardNewTask)) return;
        NewTask();
    }

    private void NewTask()
    {
        _selectedTask = null;
        this.RaisePropertyChanged(nameof(SelectedTask));

        EditingId    = 0;
        Name         = "";
        Enabled      = true;
        PickKind(ScheduleKind.Weekly);
        PickTaskType(ScheduledTaskType.SlackPost);
        SkipIfNoDynamicContent = false;
        AlertTitle      = "";
        AlertText       = "";
        IntervalValue   = 1;
        IntervalInHours = true;
        TimeOfDay    = "00:01";
        DayOfMonth   = 1;
        MonthOfYear  = MonthNames[0];
        SkipIfMissed = false;
        Destination  = null;

        foreach (var d in Days) d.Selected = true;

        Blocks.Clear();
        PreviewText      = "";
        HasEditor        = true;
        _editorLoadedFor = -1;
        StatusText       = AlarmsText.StatusNewTask;
        MarkClean();
    }

    // ── Blocks ───────────────────────────────────────────────────────────────

    private MessageBlockVm NewBlock(MessageBlock model, bool fresh) =>
        new(model, Corps, Postings, LoadStandingProjectsAsync, fresh,
            () => Destination?.IsWebhook == true, Markets,
            () => PickMarketGroup?.Invoke() ?? Task.FromResult<MarketGroupPickerResult?>(null));

    private void AddSection()
    {
        var type = SelectedSectionType?.Key ?? MessageBlock.TypeText;
        Blocks.Add(NewBlock(new MessageBlock { Type = type, MonthsBack = 1 }, fresh: true));
    }

    private void Move(MessageBlockVm block, int by)
    {
        var i = Blocks.IndexOf(block);
        var j = i + by;
        if (i < 0 || j < 0 || j >= Blocks.Count) return;
        Blocks.Move(i, j);
    }

    // ── Saving ───────────────────────────────────────────────────────────────

    /// <summary>Minutes past midnight, or null when the box does not hold a time.</summary>
    private int? ParsedTime()
    {
        var parts = TimeOfDay.Split(':');
        if (parts.Length != 2) return null;
        if (!int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var m)) return null;
        if (h is < 0 or > 23 || m is < 0 or > 59) return null;
        return h * 60 + m;
    }

    /// <summary>
    /// The editor as a task, with nothing checked.
    ///
    /// <para>Separated from Collect so the unsaved-changes test can build one from a half-finished
    /// editor. Validation belongs to saving and running, not to asking whether anything changed.</para>
    /// </summary>
    private ScheduledTask Snapshot()
    {
        var cfg = new ScheduledTaskConfig
        {
            DestinationKind        = Destination?.Kind ?? "",
            DestinationId          = Destination?.Id   ?? "",
            SkipIfNoDynamicContent = SkipIfNoDynamicContent,
            AlertTitle             = AlertTitle.Trim(),
            AlertText              = AlertText.Trim(),
            Blocks                 = [.. Blocks.Select(b => b.ToModel())],
        };

        return new ScheduledTask
        {
            Id               = EditingId,
            Name             = Name.Trim(),
            Enabled          = Enabled,
            Kind             = Kind,
            IntervalMinutes  = IntervalInHours ? IntervalValue * 60 : IntervalValue,
            DaysOfWeek       = Days.Where(d => d.Selected).Sum(d => 1 << d.Bit),
            TimeOfDayMinutes = ParsedTime() ?? 0,
            DayOfMonth       = DayOfMonth,
            MonthOfYear      = MonthNames.IndexOf(MonthOfYear) + 1,
            SkipIfMissed     = CanSkipIfMissed && SkipIfMissed,
            TaskType         = TaskType,
            Config           = cfg.ToJson(),
        };
    }

    /// <summary>Everything that distinguishes one saved task from another, as one string.</summary>
    private static string Signature(ScheduledTask t) => string.Join('\u0001',
        t.Name, t.Enabled, t.Kind, t.IntervalMinutes, t.DaysOfWeek, t.TimeOfDayMinutes,
        t.DayOfMonth, t.MonthOfYear, t.SkipIfMissed, t.TaskType, t.Config);

    /// <summary>What was last loaded or saved. Empty while no editor is open.</summary>
    private string _savedSignature = "";

    private void MarkClean() => _savedSignature = Signature(Snapshot());

    /// <summary>Whether the editor holds anything the database does not.</summary>
    public bool IsDirty => HasEditor && Signature(Snapshot()) != _savedSignature;

    /// <summary>
    /// Asks whether to abandon unsaved edits. Set by the view, which is the only thing with a
    /// window to hang a dialog off. Absent, nothing is guarded and nothing blocks.
    /// </summary>
    public Func<string, Task<bool>>? ConfirmDiscard { get; set; }

    /// <summary>True to carry on. Silent when there is nothing to lose.</summary>
    private async Task<bool> MayDiscardAsync(string what)
    {
        if (!IsDirty || ConfirmDiscard is null) return true;

        var name = Name.Trim().Length > 0 ? Name.Trim() : AlarmsText.ThisTask;
        return await ConfirmDiscard(
            string.Format(AlarmsText.UnsavedChanges, name) + "\n\n" + what);
    }

    private ScheduledTask? Collect()
    {
        if (string.IsNullOrWhiteSpace(Name)) { StatusText = AlarmsText.ErrTaskName; return null; }

        var minutes = ParsedTime();
        if (HasClock && minutes is null) { StatusText = AlarmsText.ErrTimeOfDay; return null; }

        if (IsWeekly && Days.All(d => !d.Selected)) { StatusText = AlarmsText.ErrPickDay; return null; }

        // Each type asks for what it actually needs. An alert with a headline and nothing else is
        // a perfectly good reminder; a Slack post with nothing to say is not a post.
        if (IsSlackPost)
        {
            if (Destination is null)
            {
                StatusText = Destinations.Count == 0
                    ? AlarmsText.ErrNoDestinationsAny
                    : AlarmsText.ErrPickDestination;
                return null;
            }
            if (Blocks.Count == 0) { StatusText = AlarmsText.ErrNothingToSay; return null; }

            // ⚠️ A section missing its own parameter renders to nothing, and a message that
            // silently came out one section short is the hardest kind of wrong to notice. Refused
            // here instead, while the section is still in front of whoever built it.
            if (IncompleteSection() is { } complaint) { StatusText = complaint; return null; }
        }
        else if (IsRaiseAlert && string.IsNullOrWhiteSpace(AlertText))
        {
            StatusText = AlarmsText.ErrAlertText;
            return null;
        }

        // ⚠️ SkipIfMissed is stored only where it is offered, and TimeOfDayMinutes only where a
        // clock applies — both handled in Snapshot, which is the single place the editor is
        // turned into a task.
        return Snapshot();
    }

    /// <summary>What is missing from the first section that is missing something, or null.</summary>
    private string? IncompleteSection()
    {
        for (var i = 0; i < Blocks.Count; i++)
        {
            var b       = Blocks[i];
            var n       = i + 1;
            // As the section names itself: lower-casing it suited English, and turned German's
            // capitalised nouns into misspellings.
            var heading = b.Heading;

            // Whole sentences, with the section's number and heading as placeholders.
            if (b.NeedsCorp && b.Corp is null)                      return string.Format(AlarmsText.SectionNeedsCorp, n, heading);
            if (b.IsSale    && b.Posting is null)                   return string.Format(AlarmsText.SectionNeedsPosting, n, heading);
            if (b.IsTop10   && b.Categories.All(c => !c.Selected))  return string.Format(AlarmsText.SectionNeedsList, n, heading);
            if (b.IsText    && string.IsNullOrWhiteSpace(b.Text))   return string.Format(AlarmsText.SectionEmpty, n, heading);
            if (b.IsOpps    && b.OppsMarket is null)                return string.Format(AlarmsText.SectionNeedsMarket, n, heading);
            if (b.IsOpps    && b.OppsBadField is { } field)         return string.Format(AlarmsText.SectionBadNumber, n, heading, field);
            if (b.IsOpps    && b.OppsMetaPicker.AllSelected == false)
                return string.Format(AlarmsText.SectionNeedsMetaGroup, n, heading);

            // ⚠️ Not guarded on Projects.Count. With the list stored as inclusions, a section
            // saved before its projects finished loading would store an empty list and quietly
            // post nothing — so "none ticked" and "none loaded" are both refused here.
            if (b.IsProjects && b.Projects.All(pr => !pr.Selected))
                return string.Format(AlarmsText.SectionNeedsProject, n, heading);
        }

        return null;
    }

    private async Task SaveAsync()
    {
        var edited = Collect();
        if (edited is null) return;

        var id = await Task.Run(async () =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            ScheduledTask row;
            if (edited.Id == 0)
            {
                row = new ScheduledTask();
                db.ScheduledTasks.Add(row);
            }
            else
            {
                row = await db.ScheduledTasks.FirstAsync(t => t.Id == edited.Id);
            }

            row.Name             = edited.Name;
            row.Enabled          = edited.Enabled;
            row.Kind             = edited.Kind;
            row.IntervalMinutes  = edited.IntervalMinutes;
            row.DaysOfWeek       = edited.DaysOfWeek;
            row.TimeOfDayMinutes = edited.TimeOfDayMinutes;
            row.DayOfMonth       = edited.DayOfMonth;
            row.MonthOfYear      = edited.MonthOfYear;
            row.SkipIfMissed     = edited.SkipIfMissed;
            row.TaskType         = edited.TaskType;
            row.Config           = edited.Config;

            await db.SaveChangesAsync();
            return row.Id;
        });

        // On disk now, so a section stops ticking projects it has not been told about.
        foreach (var b in Blocks) b.MarkSaved();

        // ⚠️ Clean, and owning the new id, BEFORE the list is touched. Assigning
        // SelectedTask runs the unsaved-changes guard, which compares the row against
        // EditingId — still 0 on a task saved for the first time, so the guard read its own
        // freshly saved row as a move to a different task and asked whether to discard the
        // changes it had just written.
        EditingId = id;
        MarkClean();

        await LoadAsync();

        SelectedTask = Tasks.FirstOrDefault(t => t.Id == id);
        StatusText   = AlarmsText.StatusSaved;
    }

    /// <summary>
    /// Puts the editor back to what is on disk.
    ///
    /// <para>Does not ask. The button IS the confirmation — an "are you sure" over a control
    /// labelled Discard Changes is a second question about the same decision.</para>
    /// </summary>
    private async Task DiscardAsync()
    {
        if (!IsDirty) { StatusText = AlarmsText.StatusNothingToDiscard; return; }

        // Never saved: there is no row to go back to, so the editor closes rather than reverting
        // to a blank version of a task that does not exist.
        if (EditingId == 0)
        {
            HasEditor        = false;
            _editorLoadedFor = -1;
            _savedSignature  = "";
            StatusText       = AlarmsText.StatusDiscarded;
            return;
        }

        // ⚠️ Cleared first: LoadEditorAsync refuses to re-read the task it already has open,
        // which is what stops a background refresh from throwing away an edit in progress. Here
        // throwing it away is the point.
        var id = EditingId;
        _editorLoadedFor = -1;
        await LoadEditorAsync(id);

        StatusText = AlarmsText.StatusDiscarded;
    }

    private async Task DeleteAsync()
    {
        if (EditingId == 0) { HasEditor = false; return; }

        var id = EditingId;
        await Task.Run(async () =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var row = await db.ScheduledTasks.FirstOrDefaultAsync(t => t.Id == id);
            if (row is null) return;
            db.ScheduledTasks.Remove(row);
            await db.SaveChangesAsync();
        });

        HasEditor        = false;
        EditingId        = 0;
        _editorLoadedFor = -1;
        await LoadAsync();
        StatusText = AlarmsText.StatusDeleted;
    }

    /// <summary>
    /// Renders what the task would say, without sending it.
    ///
    /// <para>Renders from the editor rather than from the saved row, so a block being fiddled
    /// with can be seen before it is committed to a schedule.</para>
    /// </summary>
    private async Task PreviewAsync()
    {
        if (Blocks.Count == 0) { StatusText = AlarmsText.StatusNoBlocks; return; }

        StatusText = AlarmsText.StatusRendering;
        try
        {
            // In the markup the destination will get, so a Discord task previews as Discord.
            var render = await _renderer.RenderAsync(
                [.. Blocks.Select(b => b.ToModel())], DateTime.UtcNow,
                formatName: Destination?.IsDiscord == true ? "Discord" : "Slack");

            // WARN Charts are named, not drawn. They contribute no text, so a preview that
            // showed only the text would call a chart-only message empty and read as broken.
            // Drawing them here would also mean rendering twice for a button that sends nothing.
            var noted = string.Join("\n", Blocks.Where(b => b.IsChart)
                .Select(b => b.ShowWebhookChartWarning
                    ? string.Format(AlarmsText.PreviewChartSkipped, b.Heading)
                    : string.Format(AlarmsText.PreviewChartUploaded, b.Heading)));

            var shown = string.Join("\n\n",
                new[] { render.Text, noted }.Where(t => t.Length > 0));

            PreviewText = shown.Length > 0 ? shown : AlarmsText.PreviewEmpty;

            // The preview is also where you find out the switch would have held the post back,
            // rather than finding out from a channel that stayed quiet.
            StatusText = SkipIfNoDynamicContent && !render.AnyDynamicContent
                ? AlarmsText.StatusWouldNotPost
                : string.Format(AlarmsText.StatusCharacters, render.Text.Length);
        }
        catch (Exception ex)
        {
            PreviewText = "";
            StatusText  = string.Format(AlarmsText.StatusPreviewFailed, ex.Message);
        }
    }

    // ── Export / import ──────────────────────────────────────────────────────

    /// <summary>Every task, as saved, to a JSON file.</summary>
    public async Task ExportAsync(Stream stream)
    {
        var rows = await Task.Run(async () =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            return await db.ScheduledTasks.AsNoTracking().OrderBy(t => t.Name).ToListAsync();
        });

        await AlarmTaskTransfer.WriteTasksAsync(stream, rows,
            (kind, id) => Destinations.FirstOrDefault(d => d.Kind == kind && d.Id == id)?.Label);
        StatusText = string.Format(AlarmsText.StatusTasksExported, rows.Count);
    }

    /// <summary>
    /// Adds the tasks in an export as new tasks. Nothing already here is replaced, and a task
    /// identical to one here is skipped, so importing the same file twice adds nothing.
    ///
    /// <para>⚠️ Switched off, every one. An imported task posts to a channel the moment it comes
    /// due, and where it posts may not mean the same thing here: a webhook's id is a row in the
    /// exporting database. It is matched by name when the id does not fit, and left blank when
    /// neither does — the editor then asks for it on Save.</para>
    /// </summary>
    public async Task ImportAsync(Stream stream)
    {
        var entries = await AlarmTaskTransfer.ReadTasksAsync(stream);
        if (entries is null) { StatusText = AlarmsText.ErrNotATasksExport; return; }

        var added = new List<ScheduledTask>();
        foreach (var e in entries)
        {
            var cfg = ScheduledTaskConfig.FromJson(AlarmTaskTransfer.Text(e.Config));
            if (cfg.DestinationKind.Length > 0
                && !Destinations.Any(d => d.Kind == cfg.DestinationKind && d.Id == cfg.DestinationId))
            {
                var byName = Destinations.FirstOrDefault(d => d.Kind == cfg.DestinationKind && d.Label == e.DestinationLabel);
                cfg.DestinationKind = byName?.Kind ?? "";
                cfg.DestinationId   = byName?.Id   ?? "";
            }

            var task = AlarmTaskTransfer.ToTask(e, cfg);
            task.Enabled = false;
            added.Add(task);
        }

        var (imported, skipped) = await Task.Run(async () =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var here = (await db.ScheduledTasks.AsNoTracking().ToListAsync())
                       .Select(t => (t.Name, t.TaskType, t.Config)).ToHashSet();
            var n = 0;
            foreach (var t in added)
            {
                if (!here.Add((t.Name, t.TaskType, t.Config))) continue;
                db.ScheduledTasks.Add(t);
                n++;
            }
            await db.SaveChangesAsync();
            return (n, added.Count - n);
        });

        await LoadAsync();
        StatusText = string.Format(AlarmsText.StatusTasksImported, imported, skipped);
    }

    /// <summary>
    /// Runs the SAVED task now, for real.
    ///
    /// <para>It posts to the real channel and raises a real alert, so it counts as the run: the
    /// last-run stamp moves, and a daily task run by hand at noon does not go again at midnight.
    /// Preview is the button for trying something out without any of that.</para>
    /// </summary>
    private bool _isRunning;

    /// <summary>
    /// True while a manual run is in flight.
    ///
    /// <para>⚠️ The button disables itself while the command runs, and a render that asks ESI
    /// for sovereignty can take seconds. Without something saying so, a second click lands on a
    /// dead button and the whole thing reads as broken.</para>
    /// </summary>
    public bool IsRunning
    {
        get => _isRunning;
        private set { this.RaiseAndSetIfChanged(ref _isRunning, value); this.RaisePropertyChanged(nameof(RunLabel)); }
    }

    public string RunLabel => IsRunning ? AlarmsText.Running : AlarmsText.RunNow;

    /// <summary>
    /// Runs what is ON SCREEN, for real.
    ///
    /// <para>⚠️ The editor, not the saved row — the same thing Preview renders. Running one
    /// config while looking at another is the kind of difference nobody catches until the wrong
    /// message is already in the channel.</para>
    ///
    /// <para>An unsaved task can be run: there is simply no row to stamp afterwards.</para>
    /// </summary>
    private async Task RunNowAsync()
    {
        var task = Collect();
        if (task is null) return;

        var id  = EditingId;
        var now = DateTime.UtcNow;

        IsRunning  = true;
        StatusText = AlarmsText.Running;

        var (ok, message) = await _scheduler.RunOneAsync(task, now);
        IsRunning = false;

        // Stamped on the same rule the scheduler uses: a run that got as far as deciding what to
        // send counts, a refusal does not. Nothing to stamp on a task that has never been saved.
        if (ok && id != 0)
        {
            await Task.Run(async () =>
            {
                await using var db = await _dbFactory.CreateDbContextAsync();
                var row = await db.ScheduledTasks.FirstOrDefaultAsync(t => t.Id == id);
                if (row is null) return;
                row.LastRunUtc = now;
                row.LastResult = message;
                await db.SaveChangesAsync();
            });
        }

        await LoadAsync();
        StatusText = message;
    }
}
