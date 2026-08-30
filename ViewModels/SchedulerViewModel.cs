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
        new(0, "Current month"),
        new(1, "Last month"),
        .. Enumerable.Range(2, 11).Select(n => new MonthBackChoice(n, $"{n} months back")),
    ];

    public static readonly LabelledChoice[] ProjectTypeOptions =
    [
        new(StandingProjectReport.DeliverItem, "Deliver item"),
        new(StandingProjectReport.DestroyNpc,  "Destroy NPC"),
    ];

    /// <summary>
    /// How much of the list to report.
    ///
    /// <para>All is last and is the default, because it is what these sections did before the
    /// choice existed — a task written yesterday keeps saying what it said yesterday.</para>
    /// </summary>
    public static readonly LabelledChoice[] ProjectFilterOptions =
    [
        new(ProjectFilters.Missing,       "Missing projects"),
        new(ProjectFilters.MissingAndLow, "Missing and low projects"),
        new(ProjectFilters.All,           "All projects"),
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
        bool                          fresh = false)
    {
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
        _sectionTitle = model.SectionTitle;
        _showHeaders  = model.ShowHeaders;

        foreach (var (key, title) in ScheduledBlockRenderer.Top10Categories)
            Categories.Add(new CategoryChoice(key, title) { Selected = model.Categories.Contains(key) });

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
                         nameof(IsTop10), nameof(IsSale), nameof(IsProjects), nameof(Heading),
                     })
                this.RaisePropertyChanged(n);

            if (IsProjects) _ = ReloadProjectsAsync();
        }
    }

    public bool IsText     => Type == MessageBlock.TypeText;
    public bool IsTop10    => Type == MessageBlock.TypeTop10;
    public bool IsSale     => Type == MessageBlock.TypeSale;
    public bool IsProjects => Type == MessageBlock.TypeProjects;

    /// <summary>Standing projects need a corp too; only the two report blocks need a month.</summary>
    public bool NeedsCorp  => Type is MessageBlock.TypeTop10 or MessageBlock.TypeMonthly
                                   or MessageBlock.TypeProjects;
    public bool NeedsMonth => Type is MessageBlock.TypeTop10 or MessageBlock.TypeMonthly;

    public string Heading => Type switch
    {
        MessageBlock.TypeTop10    => "TOP 10 LISTS",
        MessageBlock.TypeMonthly  => "MONTHLY SUMMARY",
        MessageBlock.TypeSale     => "SALE POSTING",
        MessageBlock.TypeProjects => "STANDING PROJECTS",
        _                         => "TEXT",
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
            this.RaiseAndSetIfChanged(ref _projectFilter, value ?? ProjectFilterOptions[^1]);
            this.RaisePropertyChanged(nameof(DefaultTitle));
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

    /// <summary>What the title box shows when it is empty, and what the post then prints.</summary>
    public string DefaultTitle =>
        StandingProjectReport.DefaultTitle(ProjectType?.Key ?? StandingProjectReport.DestroyNpc,
                                           ProjectFilter?.Key ?? EveConsole.Services.ProjectFilters.All);

    private bool _showHeaders;
    public bool ShowHeaders { get => _showHeaders; set => this.RaiseAndSetIfChanged(ref _showHeaders, value); }

    private LabelledChoice _projectType;
    public LabelledChoice ProjectType
    {
        get => _projectType;
        set
        {
            this.RaiseAndSetIfChanged(ref _projectType, value ?? ProjectTypeOptions[0]);
            this.RaisePropertyChanged(nameof(DefaultTitle));
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
                ? $"In progress — {when:MMMM yyyy} as things stand."
                : $"{when:MMMM yyyy} as things stand.";
        }
    }

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

        List<Models.CorpStandingProject> list;
        try { list = [.. await _loadProjects(Corp.Id, ProjectType.Key)]; }
        catch (Exception ex) { ProjectsNote = $"Could not read the projects: {ex.Message}"; return; }

        if (list.Count == 0)
        {
            ProjectsNote = $"No {ProjectType.Label.ToLowerInvariant()} projects are defined for this corp.";
            return;
        }

        foreach (var p in list)
        {
            var choice = new ProjectChoice(p.Id, StandingProjectReport.Describe(p))
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
        IncludedProjectIds = [.. _included],
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
    private readonly CorpActivityService             _corp;
    private readonly SalePostingService              _sales;
    private readonly AppErrorLogger                  _errors;

    public SchedulerViewModel(
        IDbContextFactory<AppDbContext> dbFactory,
        SchedulerService                scheduler,
        ScheduledBlockRenderer          renderer,
        SlackService                    slack,
        CorpActivityService             corp,
        SalePostingService              sales,
        AppErrorLogger                  errors)
    {
        _dbFactory = dbFactory;
        _scheduler = scheduler;
        _renderer  = renderer;
        _slack     = slack;
        _corp      = corp;
        _sales     = sales;
        _errors    = errors;

        NewCommand     = ReactiveCommand.Create(NewTask);
        SaveCommand    = ReactiveCommand.CreateFromTask(SaveAsync);
        DeleteCommand  = ReactiveCommand.CreateFromTask(DeleteAsync);
        RefreshCommand = ReactiveCommand.CreateFromTask(RefreshAsync);
        RunNowCommand  = ReactiveCommand.CreateFromTask(RunNowAsync);
        PreviewCommand = ReactiveCommand.CreateFromTask(PreviewAsync);

        AddSectionCommand = ReactiveCommand.Create(AddSection);

        MoveUpCommand   = ReactiveCommand.Create<MessageBlockVm>(b => Move(b, -1));
        MoveDownCommand = ReactiveCommand.Create<MessageBlockVm>(b => Move(b, +1));
        RemoveCommand   = ReactiveCommand.Create<MessageBlockVm>(b => Blocks.Remove(b));

        for (var i = 0; i < 7; i++)
            Days.Add(new DayChoice(i, ((DayOfWeek)i).ToString()[..3]) { Selected = true });

        PickKind(ScheduleKind.Weekly);
        PickTaskType(ScheduledTaskType.SlackPost);
        SelectedSectionType = SectionTypes[0];

        // ⚠️ A command that throws otherwise fails in silence: ReactiveUI routes the exception
        // here and nowhere else, so the button just appears not to work. Every failure now says
        // so on the line beside the buttons.
        foreach (var cmd in new IHandleObservableErrors[]
                 {
                     NewCommand, SaveCommand, DeleteCommand, RefreshCommand, RunNowCommand,
                     PreviewCommand, AddSectionCommand,
                     MoveUpCommand, MoveDownCommand, RemoveCommand,
                 })
        {
            cmd.ThrownExceptions.Subscribe(ex =>
            {
                StatusText = $"Failed: {ex.Message}";
                _errors.Log(nameof(SchedulerViewModel), "command", ex);
            });
        }

        this.WhenAnyValue(x => x.SelectedTask)
            .Where(t => t is not null)
            .Subscribe(t => _ = LoadEditorAsync(t!.Id));

        // A task that fires while the tool is open should show its new last-run without the
        // user going looking for Refresh.
        scheduler.TasksChanged += OnTasksChanged;
    }

    private void OnTasksChanged() => Dispatcher.UIThread.Post(() => _ = LoadAsync());

    // ── Lists ────────────────────────────────────────────────────────────────

    public ObservableCollection<ScheduledTaskRowVm> Tasks       { get; } = [];
    public ObservableCollection<MessageBlockVm>     Blocks      { get; } = [];
    public ObservableCollection<SlackDestination>   Destinations { get; } = [];
    public ObservableCollection<DayChoice>          Days        { get; } = [];
    public ObservableCollection<CorpChoice>         Corps       { get; } = [];
    public ObservableCollection<PostingChoice>      Postings    { get; } = [];

    public List<LabelledChoice> TaskTypes { get; } =
    [
        new(ScheduledTaskType.SlackPost,  "Post to Slack"),
        new(ScheduledTaskType.RaiseAlert, "Raise an alert"),
    ];

    /// <summary>What a message section can be. One list, one Add button.</summary>
    public List<LabelledChoice> SectionTypes { get; } =
    [
        new(MessageBlock.TypeText,     "Text"),
        new(MessageBlock.TypeTop10,    "Corp Top 10"),
        new(MessageBlock.TypeMonthly,  "Monthly Summary"),
        new(MessageBlock.TypeSale,     "Sale Posting"),
        new(MessageBlock.TypeProjects, "Standing Projects"),
    ];

    private LabelledChoice? _selectedSectionType;
    public LabelledChoice? SelectedSectionType
    {
        get => _selectedSectionType;
        set => this.RaiseAndSetIfChanged(ref _selectedSectionType, value);
    }

    public List<LabelledChoice> Kinds { get; } =
    [
        new(ScheduleKind.Interval, "Every so often"),
        new(ScheduleKind.Weekly,   "Days of the week"),
        new(ScheduleKind.Monthly,  "Once a month"),
        new(ScheduleKind.Yearly,   "Once a year"),
    ];

    /// <summary>Calendar month names, for a yearly task's chosen month.</summary>
    public List<string> MonthNames { get; } =
        [.. Enumerable.Range(1, 12).Select(m =>
            System.Globalization.CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(m))];

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
            "Measured from the last run. Closed for longer than the interval, it runs once when it "
          + "opens — not once per period missed.",

        ScheduleKind.Weekly =>
            "Every day ticked is a daily task. A day that passes while the app is closed is not "
          + "made up later, so just after midnight beats just before it.",

        ScheduleKind.Monthly =>
            "Runs once in the month, from that day and time onward. A day past the end of a short "
          + "month runs on its last day, so the 31st still happens in February.",

        ScheduleKind.Yearly =>
            "Runs once in the year, from that day and time onward. 29 February falls back to the "
          + "28th in the three years out of four that lack it.",

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

    private string _monthOfYear = "January";
    public string MonthOfYear { get => _monthOfYear; set => this.RaiseAndSetIfChanged(ref _monthOfYear, value); }

    private bool _skipIfMissed;
    public bool SkipIfMissed { get => _skipIfMissed; set => this.RaiseAndSetIfChanged(ref _skipIfMissed, value); }

    private SlackDestination? _destination;
    public SlackDestination? Destination { get => _destination; set => this.RaiseAndSetIfChanged(ref _destination, value); }

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
    public ReactiveCommand<Unit, Unit> DeleteCommand  { get; }
    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<Unit, Unit> RunNowCommand  { get; }
    public ReactiveCommand<Unit, Unit> PreviewCommand { get; }

    public ReactiveCommand<Unit, Unit> AddSectionCommand { get; }

    public ReactiveCommand<MessageBlockVm, Unit> MoveUpCommand   { get; }
    public ReactiveCommand<MessageBlockVm, Unit> MoveDownCommand { get; }
    public ReactiveCommand<MessageBlockVm, Unit> RemoveCommand   { get; }

    // ── Loading ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Reloads everything, the Slack channel list included.
    ///
    /// <para>⚠️ Clears nothing itself. Both lists are bound to combo boxes in the open editor, and
    /// a Clear pushes null through those bindings — which reads as the user picking nothing, and
    /// loses the choice before there is a new list to restore it from.</para>
    /// </summary>
    public async Task RefreshAsync()
    {
        await LoadDestinationsAsync();
        await LoadAsync();
    }

    public async Task LoadAsync()
    {
        await LoadCorpsAsync();
        await LoadPostingsAsync();

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
                Name        = t.Name.Length > 0 ? t.Name : "(unnamed)",
                Schedule    = ScheduleDue.Describe(t),
                Enabled     = t.Enabled,
                LastRunText = t.LastRunUtc is null
                                  ? "Never run"
                                  : $"Last run {t.LastRunUtc.Value.UtcDateTime:yyyy-MM-dd HH:mm} EVE",
                LastResult  = t.LastResult,
            });

        // Re-select by id rather than by object: the rows above are new instances, so the old
        // selection would otherwise clear itself and take the open editor with it.
        if (keep != 0)
            _selectedTask = Tasks.FirstOrDefault(r => r.Id == keep);
        this.RaisePropertyChanged(nameof(SelectedTask));

        StatusText = $"{Tasks.Count} task(s).";
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
        catch (Exception ex) { _errors.Log(nameof(SchedulerViewModel), "postings", ex); return; }

        foreach (var p in rows)
            if (Postings.All(x => x.Id != p.Id)) Postings.Add(new PostingChoice(p.Id, p.Name));
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
    /// "Webhook: " prefix. One list, because from a task's point of view they are one choice.
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
                SlackDestination.KindWebhook, w.Id.ToString(), "Webhook: " + w.Name, w.Url));

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
        StatusText       = "New task.";
    }

    // ── Blocks ───────────────────────────────────────────────────────────────

    private MessageBlockVm NewBlock(MessageBlock model, bool fresh) =>
        new(model, Corps, Postings, LoadStandingProjectsAsync, fresh);

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

    private ScheduledTask? Collect()
    {
        if (string.IsNullOrWhiteSpace(Name)) { StatusText = "Give the task a name."; return null; }

        var minutes = ParsedTime();
        if (HasClock && minutes is null) { StatusText = "Time of day must be HH:mm, EVE time."; return null; }

        if (IsWeekly && Days.All(d => !d.Selected)) { StatusText = "Pick at least one day."; return null; }

        // Each type asks for what it actually needs. An alert with a headline and nothing else is
        // a perfectly good reminder; a Slack post with nothing to say is not a post.
        if (IsSlackPost)
        {
            if (Destination is null)
            {
                StatusText = Destinations.Count == 0
                    ? "No channels or webhooks yet — add one under Settings, Slack."
                    : "Pick where to post it.";
                return null;
            }
            if (Blocks.Count == 0) { StatusText = "Add something to say."; return null; }

            // ⚠️ A section missing its own parameter renders to nothing, and a message that
            // silently came out one section short is the hardest kind of wrong to notice. Refused
            // here instead, while the section is still in front of whoever built it.
            if (IncompleteSection() is { } complaint) { StatusText = complaint; return null; }
        }
        else if (IsRaiseAlert && string.IsNullOrWhiteSpace(AlertText))
        {
            StatusText = "Write what the alert should say.";
            return null;
        }

        var cfg = new ScheduledTaskConfig
        {
            DestinationKind = Destination?.Kind ?? "",
            DestinationId   = Destination?.Id   ?? "",
            SkipIfNoDynamicContent = SkipIfNoDynamicContent,
            AlertTitle      = AlertTitle.Trim(),
            AlertText       = AlertText.Trim(),
            Blocks          = [.. Blocks.Select(b => b.ToModel())],
        };

        return new ScheduledTask
        {
            Id               = EditingId,
            Name             = Name.Trim(),
            Enabled          = Enabled,
            Kind             = Kind,
            IntervalMinutes  = IntervalInHours ? IntervalValue * 60 : IntervalValue,
            DaysOfWeek       = Days.Where(d => d.Selected).Sum(d => 1 << d.Bit),
            TimeOfDayMinutes = minutes ?? 0,
            DayOfMonth       = DayOfMonth,
            MonthOfYear      = MonthNames.IndexOf(MonthOfYear) + 1,

            // ⚠️ Only stored where it is offered. Left set from a previous kind it would sit in
            // the row unseen and change what a monthly task does the day somebody switched to it.
            SkipIfMissed     = CanSkipIfMissed && SkipIfMissed,

            TaskType         = TaskType,
            Config           = cfg.ToJson(),
        };
    }

    /// <summary>What is missing from the first section that is missing something, or null.</summary>
    private string? IncompleteSection()
    {
        for (var i = 0; i < Blocks.Count; i++)
        {
            var b   = Blocks[i];
            var who = $"Section {i + 1} ({b.Heading.ToLowerInvariant()})";

            if (b.NeedsCorp && b.Corp is null)                      return $"{who} needs a corp.";
            if (b.IsSale    && b.Posting is null)                   return $"{who} needs a posting.";
            if (b.IsTop10   && b.Categories.All(c => !c.Selected))  return $"{who} needs at least one list.";
            if (b.IsText    && string.IsNullOrWhiteSpace(b.Text))   return $"{who} is empty.";

            // ⚠️ Not guarded on Projects.Count. With the list stored as inclusions, a section
            // saved before its projects finished loading would store an empty list and quietly
            // post nothing — so "none ticked" and "none loaded" are both refused here.
            if (b.IsProjects && b.Projects.All(pr => !pr.Selected))
                return $"{who} needs at least one project ticked.";
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

        await LoadAsync();

        SelectedTask = Tasks.FirstOrDefault(t => t.Id == id);
        EditingId    = id;
        StatusText   = "Saved.";
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
        StatusText = "Deleted.";
    }

    /// <summary>
    /// Renders what the task would say, without sending it.
    ///
    /// <para>Renders from the editor rather than from the saved row, so a block being fiddled
    /// with can be seen before it is committed to a schedule.</para>
    /// </summary>
    private async Task PreviewAsync()
    {
        if (Blocks.Count == 0) { StatusText = "Nothing to render: this task has no blocks."; return; }

        StatusText = "Rendering…";
        try
        {
            var render = await _renderer.RenderAsync(
                [.. Blocks.Select(b => b.ToModel())], DateTime.UtcNow);

            PreviewText = render.Text.Length > 0 ? render.Text : "(the sections rendered empty)";

            // The preview is also where you find out the switch would have held the post back,
            // rather than finding out from a channel that stayed quiet.
            StatusText = SkipIfNoDynamicContent && !render.AnyDynamicContent
                ? "Rendered, but no dynamic section had anything to say — this would not post."
                : $"{render.Text.Length:N0} characters.";
        }
        catch (Exception ex)
        {
            PreviewText = "";
            StatusText  = $"Preview failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Runs the SAVED task now, for real.
    ///
    /// <para>It posts to the real channel and raises a real alert, so it counts as the run: the
    /// last-run stamp moves, and a daily task run by hand at noon does not go again at midnight.
    /// Preview is the button for trying something out without any of that.</para>
    /// </summary>
    private async Task RunNowAsync()
    {
        if (EditingId == 0) { StatusText = "Save it first."; return; }

        var id  = EditingId;
        var now = DateTime.UtcNow;
        StatusText = "Running…";

        var task = await Task.Run(async () =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            return await db.ScheduledTasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id);
        });

        if (task is null) { StatusText = "That task is gone."; return; }

        var (ok, message) = await _scheduler.RunOneAsync(task, now);

        // Stamped on the same rule the scheduler uses: a run that got as far as deciding what to
        // send counts, a refusal does not.
        if (ok)
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
