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

/// <summary>
/// One block in the message being composed.
///
/// <para>⚠️ Carries its own parameters rather than reading them off the screen. A block is saved
/// into a task that runs at 00:01 with nobody watching, so "which corp" and "which month" have to
/// be part of the block itself.</para>
/// </summary>
public sealed class MessageBlockVm : ReactiveObject
{
    public MessageBlockVm(MessageBlock model, IReadOnlyList<CorpChoice> corps)
    {
        Corps = corps;

        _type       = model.Type;
        _text       = model.Text;
        _monthsBack = model.MonthsBack;
        _corp       = corps.FirstOrDefault(c => c.Id == model.CorpId) ?? corps.FirstOrDefault();

        foreach (var (key, title) in ScheduledBlockRenderer.Top10Categories)
            Categories.Add(new CategoryChoice(key, title) { Selected = model.Categories.Contains(key) });
    }

    public IReadOnlyList<CorpChoice> Corps { get; }

    public ObservableCollection<CategoryChoice> Categories { get; } = [];

    private string _type;
    public string Type
    {
        get => _type;
        set
        {
            this.RaiseAndSetIfChanged(ref _type, value);
            this.RaisePropertyChanged(nameof(IsText));
            this.RaisePropertyChanged(nameof(IsCorp));
            this.RaisePropertyChanged(nameof(IsTop10));
            this.RaisePropertyChanged(nameof(Heading));
        }
    }

    public bool IsText  => Type == MessageBlock.TypeText;
    public bool IsCorp  => Type is MessageBlock.TypeTop10 or MessageBlock.TypeMonthly;
    public bool IsTop10 => Type == MessageBlock.TypeTop10;

    public string Heading => Type switch
    {
        MessageBlock.TypeTop10   => "TOP 10 LISTS",
        MessageBlock.TypeMonthly => "MONTHLY SUMMARY",
        _                        => "TEXT",
    };

    private string _text;
    public string Text { get => _text; set => this.RaiseAndSetIfChanged(ref _text, value); }

    private CorpChoice? _corp;
    public CorpChoice? Corp { get => _corp; set => this.RaiseAndSetIfChanged(ref _corp, value); }

    /// <summary>0 is the month in progress, 1 last month, and so on.</summary>
    private int _monthsBack;
    public int MonthsBack
    {
        get => _monthsBack;
        set
        {
            this.RaiseAndSetIfChanged(ref _monthsBack, Math.Clamp(value, 0, 60));
            this.RaisePropertyChanged(nameof(MonthsBackText));
        }
    }

    /// <summary>Says which month that actually is, so nobody has to count backwards.</summary>
    public string MonthsBackText
    {
        get
        {
            var when = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1)
                           .AddMonths(-Math.Max(0, MonthsBack));

            return MonthsBack == 0
                ? $"the month in progress ({when:MMMM yyyy} right now)"
                : $"{MonthsBack} month(s) back ({when:MMMM yyyy} right now)";
        }
    }

    public MessageBlock ToModel() => new()
    {
        Type       = Type,
        Text       = Text,
        CorpId     = Corp?.Id ?? 0,
        MonthsBack = MonthsBack,
        Categories = [.. Categories.Where(c => c.Selected).Select(c => c.Key)],
    };
}

/// <summary>A corp the app has a token for, plus its id.</summary>
public sealed class CorpChoice(long id, string name)
{
    public long   Id   { get; } = id;
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

/// <summary>One of the four ways a task can repeat, under a name worth reading.</summary>
public sealed class KindChoice(string key, string label)
{
    public string Key   { get; } = key;
    public string Label { get; } = label;
    public override string ToString() => Label;
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
/// The Scheduler tool: a list of tasks, and an editor with the schedule on one side and the
/// message being composed on the other.
/// </summary>
public sealed class SchedulerViewModel : ReactiveObject
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly SchedulerService                _scheduler;
    private readonly ScheduledBlockRenderer          _renderer;
    private readonly SlackService                    _slack;

    public SchedulerViewModel(
        IDbContextFactory<AppDbContext> dbFactory,
        SchedulerService                scheduler,
        ScheduledBlockRenderer          renderer,
        SlackService                    slack)
    {
        _dbFactory = dbFactory;
        _scheduler = scheduler;
        _renderer  = renderer;
        _slack     = slack;

        NewCommand     = ReactiveCommand.Create(NewTask);
        SaveCommand    = ReactiveCommand.CreateFromTask(SaveAsync);
        DeleteCommand  = ReactiveCommand.CreateFromTask(DeleteAsync);
        RefreshCommand = ReactiveCommand.CreateFromTask(RefreshAsync);
        RunNowCommand  = ReactiveCommand.CreateFromTask(RunNowAsync);
        PreviewCommand = ReactiveCommand.CreateFromTask(PreviewAsync);

        AddTextCommand    = ReactiveCommand.Create(() => AddBlock(MessageBlock.TypeText));
        AddTop10Command   = ReactiveCommand.Create(() => AddBlock(MessageBlock.TypeTop10));
        AddMonthlyCommand = ReactiveCommand.Create(() => AddBlock(MessageBlock.TypeMonthly));

        MoveUpCommand   = ReactiveCommand.Create<MessageBlockVm>(b => Move(b, -1));
        MoveDownCommand = ReactiveCommand.Create<MessageBlockVm>(b => Move(b, +1));
        RemoveCommand   = ReactiveCommand.Create<MessageBlockVm>(b => Blocks.Remove(b));

        for (var i = 0; i < 7; i++)
            Days.Add(new DayChoice(i, ((DayOfWeek)i).ToString()[..3]) { Selected = true });

        PickKind(ScheduleKind.Weekly);

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

    public List<KindChoice> Kinds { get; } =
    [
        new(ScheduleKind.Interval, "Every so often"),
        new(ScheduleKind.Weekly,   "Days of the week"),
        new(ScheduleKind.Monthly,  "Once a month"),
        new(ScheduleKind.Yearly,   "Once a year"),
    ];

    public List<string> Months { get; } =
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

    private KindChoice? _selectedKind;
    public KindChoice? SelectedKind
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
        }
    }

    /// <summary>The stored key. Derived from the picker rather than kept beside it, so there is
    /// one thing to set and no pair to fall out of step.</summary>
    public string Kind => SelectedKind?.Key ?? ScheduleKind.Weekly;

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

    public ReactiveCommand<Unit, Unit> AddTextCommand    { get; }
    public ReactiveCommand<Unit, Unit> AddTop10Command   { get; }
    public ReactiveCommand<Unit, Unit> AddMonthlyCommand { get; }

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
        MonthOfYear  = Months[Math.Clamp(task.MonthOfYear, 1, 12) - 1];
        SkipIfMissed = task.SkipIfMissed;

        foreach (var d in Days) d.Selected = (task.DaysOfWeek & (1 << d.Bit)) != 0;

        var cfg = SlackPostConfig.FromJson(task.Config);

        Destination = Destinations.FirstOrDefault(
            d => d.Kind == cfg.DestinationKind && d.Id == cfg.DestinationId);

        Blocks.Clear();
        foreach (var b in cfg.Blocks) Blocks.Add(new MessageBlockVm(b, Corps));

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
        IntervalValue   = 1;
        IntervalInHours = true;
        TimeOfDay    = "00:01";
        DayOfMonth   = 1;
        MonthOfYear  = Months[0];
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

    private void AddBlock(string type)
    {
        Blocks.Add(new MessageBlockVm(
            new MessageBlock { Type = type, MonthsBack = type == MessageBlock.TypeText ? 0 : 1 },
            Corps));
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

        if (Destination is null) { StatusText = "Pick where to post it."; return null; }
        if (Blocks.Count == 0)   { StatusText = "Add something to say."; return null; }

        var cfg = new SlackPostConfig
        {
            DestinationKind = Destination.Kind,
            DestinationId   = Destination.Id,
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
            MonthOfYear      = Months.IndexOf(MonthOfYear) + 1,

            // ⚠️ Only stored where it is offered. Left set from a previous kind it would sit in
            // the row unseen and change what a monthly task does the day somebody switched to it.
            SkipIfMissed     = CanSkipIfMissed && SkipIfMissed,

            TaskType         = ScheduledTaskType.SlackPost,
            Config           = cfg.ToJson(),
        };
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
        if (Blocks.Count == 0) { StatusText = "Add something to say."; return; }

        StatusText = "Rendering…";
        try
        {
            var body = await _renderer.RenderAsync(
                [.. Blocks.Select(b => b.ToModel())], DateTime.UtcNow);

            PreviewText = body.Length > 0 ? body : "(the blocks rendered empty)";
            StatusText  = $"{body.Length:N0} characters.";
        }
        catch (Exception ex)
        {
            PreviewText = "";
            StatusText  = $"Preview failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Runs the SAVED task now, and posts for real.
    ///
    /// <para>⚠️ Does not touch LastRun. A test send that counted as the run would cancel the
    /// scheduled one — the point of testing is to find out whether the real run will work.</para>
    /// </summary>
    private async Task RunNowAsync()
    {
        if (EditingId == 0) { StatusText = "Save it first."; return; }

        var id = EditingId;
        StatusText = "Running…";

        var task = await Task.Run(async () =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            return await db.ScheduledTasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id);
        });

        if (task is null) { StatusText = "That task is gone."; return; }

        var (_, message) = await _scheduler.RunOneAsync(task, DateTime.UtcNow);
        StatusText = message;
    }
}
