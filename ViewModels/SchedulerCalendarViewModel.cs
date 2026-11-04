using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Reactive;
using EveConsole.Localization;
using EveConsole.Models;
using ReactiveUI;

namespace EveConsole.ViewModels;

public enum CalendarMode { Month, Week, Day }

/// <summary>
/// A task on the calendar: one run, or several in the same cell.
///
/// <para>⚠️ Runs have a time and no duration, so a chip is a point in a cell rather than a block
/// spanning rows. Several runs of one task become one chip: a task every minute would otherwise
/// put 60 in an hour and 1,440 in a day.</para>
/// </summary>
public sealed class CalendarChip
{
    public CalendarChip(ScheduledTask task, IReadOnlyList<DateTime> runs, Action<int> open)
    {
        var name  = task.Name.Length > 0 ? task.Name : AlarmsText.UnnamedTask;
        var type  = task.TaskType == ScheduledTaskType.RaiseAlert
                        ? AlarmsText.TaskTypeRaiseAlert
                        : AlarmsText.TaskTypeSlackOrDiscordPost;
        var first = runs[0];
        var when  = runs.Count == 1
                        ? first.ToString(CommonText.DateMonthDayYearTime)
                        : $"{first.ToString(CommonText.DateMonthDayYearTime)} – {runs[^1]:HH:mm}";

        Time        = first.ToString("HH:mm");
        Name        = name;
        CountText   = runs.Count > 1 ? $"×{runs.Count:N0}" : "";
        IsAlert     = task.TaskType == ScheduledTaskType.RaiseAlert;
        First       = first;
        Tip         = $"{name}\n{ScheduleDue.Describe(task)}\n{type}\n{when} EVE";
        OpenCommand = ReactiveCommand.Create(() => open(task.Id));
    }

    public DateTime First     { get; }
    public string   Time      { get; }
    public string   Name      { get; }
    public string   CountText { get; }
    public bool     IsAlert   { get; }
    public string   Tip       { get; }
    public ReactiveCommand<Unit, Unit> OpenCommand { get; }
}

/// <summary>One day in the month grid.</summary>
public sealed class CalendarDayCell
{
    public required string DayNumber { get; init; }
    public required bool   IsToday   { get; init; }
    public required bool   IsPast    { get; init; }
    public required bool   IsOutside { get; init; }
    public required IReadOnlyList<CalendarChip> Chips { get; init; }
    public required string MoreText  { get; init; }
    public required ReactiveCommand<Unit, Unit> OpenCommand { get; init; }

    public bool HasMore => MoreText.Length > 0;
}

/// <summary>A day's heading over the week grid.</summary>
public sealed class CalendarDayHeader
{
    public required string Name    { get; init; }
    public required string Number  { get; init; }
    public required bool   IsToday { get; init; }
    public required ReactiveCommand<Unit, Unit> OpenCommand { get; init; }
}

/// <summary>One hour of one day in the week or day grid.</summary>
public sealed class CalendarHourCell
{
    public required bool IsToday { get; init; }
    public required bool IsPast  { get; init; }
    public required IReadOnlyList<CalendarChip> Chips { get; init; }
}

/// <summary>One hour across the week or day grid: its label, then a cell per day.</summary>
public sealed class CalendarHourRow
{
    public required string Label     { get; init; }
    public required bool   IsNowHour { get; init; }
    public required IReadOnlyList<CalendarHourCell> Cells { get; init; }
}

/// <summary>
/// The Scheduler's Calendar tab: what will run, by month, week or day, in EVE time.
///
/// <para>Holds no schedule rules of its own. Every run comes from
/// <see cref="ScheduleDue.Upcoming"/>, so the calendar and the runner cannot disagree.</para>
/// </summary>
public sealed class SchedulerCalendarViewModel : ReactiveObject
{
    /// <summary>A chip's height and the gap under it, as set on it in the view. The month grid
    /// works out from this how many fit in a cell.</summary>
    public const double ChipHeight = 18;

    /// <summary>The day number and the space around a month cell's chips.</summary>
    private const double CellChrome = 24;

    private readonly Action<int> _openTask;

    private IReadOnlyList<ScheduledTask> _tasks = [];
    private double _monthHeight;
    private int    _monthWeeks = 5;
    private int    _chipsPerCell = 4;

    public SchedulerCalendarViewModel(Action<int> openTask)
    {
        _openTask = openTask;
        _anchor   = Today;

        PreviousCommand  = ReactiveCommand.Create(() => Step(-1));
        NextCommand      = ReactiveCommand.Create(() => Step(+1));
        TodayCommand     = ReactiveCommand.Create(() => { Anchor = Today; });
        ShowMonthCommand = ReactiveCommand.Create(() => { Mode = CalendarMode.Month; });
        ShowWeekCommand  = ReactiveCommand.Create(() => { Mode = CalendarMode.Week; });
        ShowDayCommand   = ReactiveCommand.Create(() => { Mode = CalendarMode.Day; });

        Rebuild();
    }

    public ReactiveCommand<Unit, Unit> PreviousCommand  { get; }
    public ReactiveCommand<Unit, Unit> NextCommand      { get; }
    public ReactiveCommand<Unit, Unit> TodayCommand     { get; }
    public ReactiveCommand<Unit, Unit> ShowMonthCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowWeekCommand  { get; }
    public ReactiveCommand<Unit, Unit> ShowDayCommand   { get; }

    /// <summary>Today in EVE time, which is what the whole Scheduler runs on.</summary>
    private static DateTime Today => DateTime.UtcNow.Date;

    private CalendarMode _mode = CalendarMode.Month;
    public CalendarMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value) return;
            this.RaiseAndSetIfChanged(ref _mode, value);
            this.RaisePropertyChanged(nameof(IsMonth));
            this.RaisePropertyChanged(nameof(IsWeek));
            this.RaisePropertyChanged(nameof(IsDay));
            this.RaisePropertyChanged(nameof(IsHourGrid));
            Rebuild();
        }
    }

    public bool IsMonth    => Mode == CalendarMode.Month;
    public bool IsWeek     => Mode == CalendarMode.Week;
    public bool IsDay      => Mode == CalendarMode.Day;
    public bool IsHourGrid => Mode != CalendarMode.Month;

    private DateTime _anchor;

    /// <summary>The day the view is built around: any day of the month or week on show.</summary>
    public DateTime Anchor
    {
        get => _anchor;
        set
        {
            this.RaiseAndSetIfChanged(ref _anchor, value.Date);
            Rebuild();
        }
    }

    private string _title = "";
    public string Title { get => _title; private set => this.RaiseAndSetIfChanged(ref _title, value); }

    public ObservableCollection<string>            WeekdayNames { get; } = [];
    public ObservableCollection<CalendarDayCell>   MonthCells   { get; } = [];
    public ObservableCollection<CalendarDayHeader> DayHeaders   { get; } = [];
    public ObservableCollection<CalendarHourRow>   HourRows     { get; } = [];

    /// <summary>The tasks as they stand now. Called whenever the Scheduler's list is read.</summary>
    public void SetTasks(IReadOnlyList<ScheduledTask> tasks)
    {
        _tasks = tasks;
        Rebuild();
    }

    /// <summary>The month grid's height, from the view: how many chips fit depends on it.</summary>
    public void SetMonthHeight(double height)
    {
        _monthHeight = height;
        if (UpdateChipsPerCell() && IsMonth) Rebuild();
    }

    /// <summary>True when the number changed.</summary>
    private bool UpdateChipsPerCell()
    {
        if (_monthHeight <= 0) return false;

        var fit = (int)Math.Floor((_monthHeight / _monthWeeks - CellChrome) / ChipHeight);
        fit = Math.Max(1, fit);
        if (fit == _chipsPerCell) return false;

        _chipsPerCell = fit;
        return true;
    }

    private void Step(int by) => Anchor = Mode switch
    {
        CalendarMode.Month => new DateTime(Anchor.Year, Anchor.Month, 1).AddMonths(by),
        CalendarMode.Week  => Anchor.AddDays(7 * by),
        _                  => Anchor.AddDays(by),
    };

    private void OpenDay(DateTime day)
    {
        _anchor = day;
        this.RaisePropertyChanged(nameof(Anchor));
        if (IsDay) Rebuild();
        else Mode = CalendarMode.Day;
    }

    /// <summary>Redraws from the clock and the tasks: also what the view calls once a minute, so
    /// runs that have passed go and the day rolls over.</summary>
    public void Rebuild()
    {
        switch (Mode)
        {
            case CalendarMode.Month: BuildMonth(); break;
            case CalendarMode.Week:
                var start = StartOfWeek(Anchor);
                Title = $"{start.ToString(CommonText.DateMonthDay)} – {start.AddDays(6).ToString(CommonText.DateMonthDayYear)}";
                BuildHours(start, 7);
                break;
            default:
                Title = Anchor.ToString(AlarmsText.CalendarDayTitlePattern);
                BuildHours(Anchor, 1);
                break;
        }
    }

    /// <summary>Weeks start where this machine's regional format says they do.</summary>
    private static DateTime StartOfWeek(DateTime day)
    {
        var first = (int)CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        return day.AddDays(-(((int)day.DayOfWeek - first + 7) % 7));
    }

    /// <summary>Every enabled task's runs between two moments, as (task, run), in time order.</summary>
    private List<(ScheduledTask Task, DateTime Run)> RunsBetween(DateTime now, DateTime from, DateTime to) =>
        [.. _tasks.SelectMany(t => ScheduleDue.Upcoming(t, now, from, to).Select(run => (t, run)))
                  .OrderBy(r => r.run)];

    private CalendarChip[] Chips(IEnumerable<(ScheduledTask Task, DateTime Run)> runs) =>
        [.. runs.GroupBy(r => r.Task.Id)
                .Select(g => new CalendarChip(g.First().Task, [.. g.Select(r => r.Run)], _openTask))
                .OrderBy(c => c.First)
                .ThenBy(c => c.Name, StringComparer.CurrentCulture)];

    private void BuildMonth()
    {
        var now   = DateTime.UtcNow;
        var month = new DateTime(Anchor.Year, Anchor.Month, 1);
        var start = StartOfWeek(month);
        var weeks = (int)Math.Ceiling(((month - start).Days + DateTime.DaysInMonth(month.Year, month.Month)) / 7.0);
        var end   = start.AddDays(7 * weeks);

        Title = month.ToString(CommonText.DateMonthYear);

        _monthWeeks = weeks;
        UpdateChipsPerCell();

        var byDay = RunsBetween(now, start, end).ToLookup(r => r.Run.Date);

        Replace(WeekdayNames, Enumerable.Range(0, 7).Select(i => ShortDayName(start.AddDays(i))));
        Replace(MonthCells, Enumerable.Range(0, 7 * weeks).Select(i =>
        {
            var day   = start.AddDays(i);
            var chips = Chips(byDay[day]);

            // A "+3 more" line takes the room of one chip, so it costs one when it is needed.
            var shown = chips.Length <= _chipsPerCell ? chips.Length : Math.Max(0, _chipsPerCell - 1);

            return new CalendarDayCell
            {
                DayNumber   = day.Day.ToString(),
                IsToday     = day == now.Date,
                IsPast      = day < now.Date,
                IsOutside   = day.Month != month.Month,
                Chips       = chips[..shown],
                MoreText    = shown < chips.Length
                                  ? string.Format(AlarmsText.CalendarMoreItems, chips.Length - shown)
                                  : "",
                OpenCommand = ReactiveCommand.Create(() => OpenDay(day)),
            };
        }));
    }

    private void BuildHours(DateTime start, int days)
    {
        var now    = DateTime.UtcNow;
        var byHour = RunsBetween(now, start, start.AddDays(days))
            .ToLookup(r => r.Run.Date.AddHours(r.Run.Hour));

        Replace(DayHeaders, Enumerable.Range(0, days).Select(i =>
        {
            var day = start.AddDays(i);
            return new CalendarDayHeader
            {
                Name        = ShortDayName(day),
                Number      = day.Day.ToString(),
                IsToday     = day == now.Date,
                OpenCommand = ReactiveCommand.Create(() => OpenDay(day)),
            };
        }));

        var showsToday = now.Date >= start && now.Date < start.AddDays(days);

        Replace(HourRows, Enumerable.Range(0, 24).Select(hour => new CalendarHourRow
        {
            Label     = $"{hour:00}:00",
            IsNowHour = showsToday && hour == now.Hour,
            Cells     = [.. Enumerable.Range(0, days).Select(i =>
            {
                var day = start.AddDays(i);
                return new CalendarHourCell
                {
                    IsToday = day == now.Date,
                    IsPast  = day < now.Date,
                    Chips   = Chips(byHour[day.AddHours(hour)]),
                };
            })],
        }));
    }

    /// <summary>The tick boxes' short day names, Sunday first as <see cref="DayOfWeek"/> is.</summary>
    private static string ShortDayName(DateTime day) => day.DayOfWeek switch
    {
        DayOfWeek.Sunday    => AlarmsText.DaySun,
        DayOfWeek.Monday    => AlarmsText.DayMon,
        DayOfWeek.Tuesday   => AlarmsText.DayTue,
        DayOfWeek.Wednesday => AlarmsText.DayWed,
        DayOfWeek.Thursday  => AlarmsText.DayThu,
        DayOfWeek.Friday    => AlarmsText.DayFri,
        _                   => AlarmsText.DaySat,
    };

    private static void Replace<T>(ObservableCollection<T> list, IEnumerable<T> items)
    {
        list.Clear();
        foreach (var item in items) list.Add(item);
    }
}
