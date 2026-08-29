using System;
using System.Collections.Generic;

namespace EveConsole.Models;

/// <summary>How a task decides it is due.</summary>
public static class ScheduleKind
{
    /// <summary>Every N minutes, measured from when it last ran.</summary>
    public const string Interval = "interval";

    /// <summary>At a time of day, on chosen days of the week.</summary>
    public const string Weekly = "weekly";

    /// <summary>At a time of day, on one day of the month.</summary>
    public const string Monthly = "monthly";

    /// <summary>At a time of day, on one day of one month.</summary>
    public const string Yearly = "yearly";
}

/// <summary>What a task does when it fires.</summary>
public static class ScheduledTaskType
{
    public const string SlackPost = "slack_post";
}

/// <summary>
/// A task the app runs on a schedule.
///
/// <para>⚠️ Two different ideas of "due", and they are not interchangeable. An INTERVAL task is
/// measured from its own last run: if the app was closed for a day, one run is owed on startup,
/// not one per period missed. A CLOCK task — weekly, monthly, yearly — is due at a stated time,
/// and a period that passed while the app was closed is simply gone.</para>
///
/// <para>⚠️ Times are EVE time (UTC). The whole app is built around it, the header shows it, and
/// it has no daylight saving to argue about.</para>
/// </summary>
public class ScheduledTask
{
    public int    Id      { get; set; }
    public string Name    { get; set; } = "";
    public bool   Enabled { get; set; } = true;

    /// <summary>One of <see cref="ScheduleKind"/>.</summary>
    public string Kind { get; set; } = ScheduleKind.Weekly;

    /// <summary>Interval tasks only: how many minutes between runs. Hours are entered as a
    /// multiple of sixty rather than a second unit, so there is one number to compare.</summary>
    public int IntervalMinutes { get; set; } = 60;

    /// <summary>
    /// Weekly tasks: which days, as a bitmask with Sunday in bit 0.
    ///
    /// <para>There is no separate daily kind. Daily is weekly with every day ticked, and offering
    /// both would be two ways to say one thing — and two places to fix anything about it.</para>
    /// </summary>
    public int DaysOfWeek { get; set; } = 127;

    /// <summary>Clock tasks: minutes past midnight, EVE time.</summary>
    public int TimeOfDayMinutes { get; set; }

    /// <summary>Monthly and yearly: which day of the month.</summary>
    public int DayOfMonth { get; set; } = 1;

    /// <summary>Yearly only: which month.</summary>
    public int MonthOfYear { get; set; } = 1;

    /// <summary>
    /// Monthly and yearly: run only if the app is up on the day itself.
    ///
    /// <para>⚠️ Not offered on interval or weekly. Those fire on a clock a polling loop only
    /// checks periodically, so "was it the exact moment" is a question about the loop rather than
    /// about the schedule. A month is long enough for catching up to be a real choice.</para>
    /// </summary>
    public bool SkipIfMissed { get; set; }

    /// <summary>One of <see cref="ScheduledTaskType"/>.</summary>
    public string TaskType { get; set; } = ScheduledTaskType.SlackPost;

    /// <summary>The task type's own settings, as JSON.</summary>
    public string Config { get; set; } = "";

    public DateTimeOffset? LastRunUtc { get; set; }
    public string          LastResult { get; set; } = "";
}

/// <summary>
/// Decides whether a task is due, and nothing else.
///
/// <para>Separated from the runner so the rules can be read — and reasoned about — without a
/// timer, a database or a Slack client in the way.</para>
/// </summary>
public static class ScheduleDue
{
    /// <summary>
    /// Whether <paramref name="task"/> should run at <paramref name="now"/> (EVE time).
    /// </summary>
    public static bool IsDue(ScheduledTask task, DateTime now)
    {
        if (!task.Enabled) return false;

        var last = task.LastRunUtc?.UtcDateTime;

        return task.Kind switch
        {
            ScheduleKind.Interval => IntervalDue(task, last, now),
            ScheduleKind.Weekly   => WeeklyDue(task, last, now),
            ScheduleKind.Monthly  => MonthlyDue(task, last, now),
            ScheduleKind.Yearly   => YearlyDue(task, last, now),
            _                     => false,
        };
    }

    /// <summary>
    /// ⚠️ ONE run is owed however long the app was closed. An interval says how long to wait
    /// between runs, not how many runs a stretch of time contains — firing five times because
    /// five periods elapsed would post five identical messages to Slack.
    /// </summary>
    private static bool IntervalDue(ScheduledTask task, DateTime? last, DateTime now)
    {
        var every = Math.Max(1, task.IntervalMinutes);

        // Never run: due now. Otherwise it has waited long enough or it has not.
        return last is null || now >= last.Value.AddMinutes(every);
    }

    /// <summary>
    /// Due on a ticked day, once the time has passed, and not already run today.
    ///
    /// <para>⚠️ A day missed entirely is gone. There is no catching up on Tuesday for a Monday
    /// that never happened: the schedule says when, and the answer to "when" has passed.</para>
    /// </summary>
    private static bool WeeklyDue(ScheduledTask task, DateTime? last, DateTime now)
    {
        if ((task.DaysOfWeek & (1 << (int)now.DayOfWeek)) == 0) return false;
        if (now.TimeOfDay < TimeSpan.FromMinutes(task.TimeOfDayMinutes)) return false;

        return last is null || last.Value.Date < now.Date;
    }

    /// <summary>
    /// Due once in the month, from the stated day and time onward.
    ///
    /// <para>⚠️ Reached the same month, not the same day, unless SkipIfMissed says otherwise. An
    /// app closed on the 1st should still post the monthly summary when it opens on the 3rd —
    /// that is the whole reason the option to skip is an option.</para>
    ///
    /// <para>⚠️ Clamped to the length of the month. A task set for the 31st would otherwise never
    /// run in February, and silently: nothing would be wrong, it simply would not happen.</para>
    /// </summary>
    private static bool MonthlyDue(ScheduledTask task, DateTime? last, DateTime now)
    {
        var day  = Math.Clamp(task.DayOfMonth, 1, DateTime.DaysInMonth(now.Year, now.Month));
        var when = new DateTime(now.Year, now.Month, day)
                       .AddMinutes(task.TimeOfDayMinutes);

        if (now < when) return false;
        if (task.SkipIfMissed && now.Date != when.Date) return false;

        // Already run this month?
        return last is null
            || last.Value.Year != now.Year
            || last.Value.Month != now.Month;
    }

    /// <summary>
    /// Due once in the year, from the stated day and time onward.
    ///
    /// <para>⚠️ Clamped like the monthly case: the 29th of February exists in one year out of
    /// four, and a task set for it should still run in the other three.</para>
    /// </summary>
    private static bool YearlyDue(ScheduledTask task, DateTime? last, DateTime now)
    {
        var month = Math.Clamp(task.MonthOfYear, 1, 12);
        var day   = Math.Clamp(task.DayOfMonth, 1, DateTime.DaysInMonth(now.Year, month));
        var when  = new DateTime(now.Year, month, day).AddMinutes(task.TimeOfDayMinutes);

        if (now < when) return false;
        if (task.SkipIfMissed && now.Date != when.Date) return false;

        return last is null || last.Value.Year != now.Year;
    }

    /// <summary>A plain-English description of the schedule, for the list.</summary>
    public static string Describe(ScheduledTask t)
    {
        var at = $"{t.TimeOfDayMinutes / 60:00}:{t.TimeOfDayMinutes % 60:00}";

        return t.Kind switch
        {
            ScheduleKind.Interval => t.IntervalMinutes % 60 == 0 && t.IntervalMinutes >= 60
                                        ? $"Every {t.IntervalMinutes / 60} hour(s)"
                                        : $"Every {t.IntervalMinutes} minute(s)",
            ScheduleKind.Weekly   => $"{DayNames(t.DaysOfWeek)} at {at}",
            ScheduleKind.Monthly  => $"Day {t.DayOfMonth} of each month at {at}",
            ScheduleKind.Yearly   => $"{MonthName(t.MonthOfYear)} {t.DayOfMonth} each year at {at}",
            _                     => "—",
        };
    }

    private static string DayNames(int mask)
    {
        if (mask == 127) return "Every day";

        string[] names = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
        var picked = new List<string>();
        for (var i = 0; i < 7; i++)
            if ((mask & (1 << i)) != 0) picked.Add(names[i]);

        return picked.Count == 0 ? "No days" : string.Join(", ", picked);
    }

    private static string MonthName(int month) =>
        System.Globalization.CultureInfo.InvariantCulture.DateTimeFormat
              .GetMonthName(Math.Clamp(month, 1, 12));
}
