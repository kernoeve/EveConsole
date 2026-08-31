using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>
/// Runs scheduled tasks when they come due.
///
/// <para>⚠️ The loop only asks; <see cref="ScheduleDue"/> answers. Keeping the decision out of
/// here is what lets "is this due" be reasoned about on its own — the loop's job is to ask often
/// enough, record what happened, and never die.</para>
///
/// <para>⚠️ Every minute, not every second. A clock task fires the first time the loop looks
/// after its time, so the interval IS the accuracy — a task set for 00:01 runs somewhere in that
/// minute. That is also why "skip if missed" compares dates rather than instants.</para>
/// </summary>
public class SchedulerService(
    IDbContextFactory<AppDbContext> dbFactory,
    ScheduledBlockRenderer          renderer,
    SlackService                    slack,
    AppErrorLogger                  errors)
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);

    private CancellationTokenSource? _cts;

    /// <summary>Raised after any run, so an open Scheduler window can refresh itself.</summary>
    public event Action? TasksChanged;

    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        _ = RunLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // ⚠️ Everything inside is guarded. A task that throws — a Slack outage, a corp with
            // no data, a configuration somebody hand-edited — must not stop the other tasks or
            // end the loop for the session.
            try { await RunDueAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { errors.Log(nameof(SchedulerService), "loop", ex); }

            try { await Task.Delay(Tick, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Runs whatever is due now. Public so the window can force a pass.</summary>
    public async Task RunDueAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var tasks = await db.ScheduledTasks.Where(t => t.Enabled).ToListAsync(ct);

        var dirty = false;

        foreach (var task in tasks)
        {
            if (!ScheduleDue.IsDue(task, now)) continue;

            var (ok, message) = await RunOneAsync(task, now, ct);

            // ⚠️ A failure does not consume the slot. Being due is a yes-or-no question, not a
            // count, so a task that keeps its old LastRun through an outage retries once a minute
            // and posts ONCE when the outage ends — never a run per minute missed. Stamping the
            // failure instead would spend a monthly task's whole month on a bad minute.
            if (ok) task.LastRunUtc = now;

            // Written only when it changes, so a task failing all day is one row write, not one
            // a minute.
            if (task.LastResult != message)
            {
                task.LastResult = message;
                dirty = true;
            }

            if (ok) dirty = true;
        }

        if (!dirty) return;

        await db.SaveChangesAsync(ct);
        TasksChanged?.Invoke();
    }

    /// <summary>
    /// Runs one task, reporting whether it ran and what to record against it.
    ///
    /// <para>⚠️ "Ran" means it got as far as deciding what to send — including deciding there was
    /// nothing to send. A task with no blocks, or whose month is empty, has nothing more to try;
    /// only a refusal or a thrown exception is worth coming back for.</para>
    /// </summary>
    public async Task<(bool Ok, string Message)> RunOneAsync(
        ScheduledTask task, DateTime now, CancellationToken ct = default)
    {
        try
        {
            return task.TaskType switch
            {
                ScheduledTaskType.SlackPost  => await SlackPostAsync(task, now, ct),
                ScheduledTaskType.RaiseAlert => await RaiseAlertAsync(task, now, ct),

                // Nothing about an unrecognised type gets better by waiting.
                _ => (true, $"Unknown task type \"{task.TaskType}\"."),
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            errors.Log(nameof(SchedulerService), $"task {task.Id} \"{task.Name}\"", ex);
            return (false, $"Failed: {ex.Message}");
        }
    }

    private async Task<(bool Ok, string Message)> SlackPostAsync(
        ScheduledTask task, DateTime now, CancellationToken ct)
    {
        var cfg = ScheduledTaskConfig.FromJson(task.Config);

        if (cfg.Blocks.Count == 0) return (true, "Nothing to post: no sections configured.");

        var viaWebhook = cfg.DestinationKind == SlackDestination.KindWebhook;

        var render = await renderer.RenderAsync(cfg.Blocks, now, ct);
        var body   = render.Text.Trim();

        // ⚠️ Charts are drawn BEFORE anything is decided, because whether they came out is part
        // of whether there is anything to post. A task of nothing but charts renders no text at
        // all, and testing the text alone would have declared it empty and stopped.
        var charts  = cfg.Blocks.Where(b => b.IsChart).ToList();
        var drawn   = new List<(byte[] Png, string Title)>();
        var skipped = 0;

        foreach (var b in charts)
        {
            ct.ThrowIfCancellationRequested();

            // No point drawing what cannot be sent. The count is still reported, so a task
            // pointed at a webhook says what it could not do rather than looking complete.
            if (viaWebhook) { skipped++; continue; }

            if (await renderer.RenderChartAsync(b, ct) is { } c) drawn.Add(c);
        }

        if (body.Length == 0 && drawn.Count == 0)
            return (true, WithSkipped("Nothing to post: the sections rendered empty.", skipped));

        // Asked for, and only then: a task whose static text is the point should still go out on a
        // quiet month. A chart that drew counts as something to say. ⚠️ Counts as the run either
        // way — it got as far as deciding what to send, and deciding to send nothing is a
        // decision, not a failure to retry.
        if (cfg.SkipIfNoDynamicContent && !render.AnyDynamicContent && drawn.Count == 0)
            return (true, WithSkipped("Nothing to post: no dynamic section had anything to say.", skipped));

        var posted = 0;

        if (body.Length > 0)
        {
            var res = viaWebhook
                ? await PostWebhookAsync(cfg.DestinationId, body, ct)
                : await slack.PostMessageAsync(cfg.DestinationId, body, ct: ct);

            if (!res.Ok) return (false, $"Slack refused it: {res.Error}");
            posted = body.Length;
        }

        // After the text, so the message reads in the order it was composed: the words, then the
        // pictures under them.
        var failed = new List<string>();

        foreach (var (png, title) in drawn)
        {
            var error = await slack.UploadFileAsync(
                cfg.DestinationId, png,
                filename: $"{title.Replace(' ', '-').ToLowerInvariant()}.png",
                title:    title,
                ct:       ct);

            if (error is not null) failed.Add($"{title}: {error}");
        }

        var what = posted > 0 ? $"Posted {posted:N0} characters." : "Posted.";
        if (drawn.Count > failed.Count) what += $" {drawn.Count - failed.Count} chart(s) uploaded.";
        if (failed.Count > 0)           what += $" {failed.Count} chart(s) failed: {string.Join("; ", failed)}.";

        return (true, WithSkipped(what, skipped));
    }

    /// <summary>
    /// Appends what a webhook could not carry.
    ///
    /// <para>⚠️ Said out loud on every outcome. A chart section pointed at a webhook is dropped
    /// in a way nothing else would reveal, and a run reporting plain success while half of what it
    /// was asked to send never existed is the kind of quiet that costs a month.</para>
    /// </summary>
    private static string WithSkipped(string message, int skipped) =>
        skipped == 0
            ? message
            : $"{message} {skipped} chart section(s) skipped: a webhook cannot carry an image.";

    /// <summary>
    /// Raises the same alert an alarm raises.
    ///
    /// <para>⚠️ AlarmId and AlarmEventId stay zero: no alarm fired, and inventing one would put a
    /// row in the alarm history for something that never had a condition. Both readers — the
    /// Overview and the Alarms tool — show alerts by title and body without joining to an alarm,
    /// and the delete-alerts-for-alarm sweeps key on a real alarm id, which starts at one.</para>
    /// </summary>
    private async Task<(bool Ok, string Message)> RaiseAlertAsync(
        ScheduledTask task, DateTime now, CancellationToken ct)
    {
        var cfg  = ScheduledTaskConfig.FromJson(task.Config);
        var text = cfg.AlertText.Trim();

        if (text.Length == 0) return (true, "Nothing to raise: the alert has no text.");

        // Both readers lead with the title and put the body under it. An unwritten headline falls
        // back to the task's name, which already answers "which task said this".
        var title = cfg.AlertTitle.Trim();
        if (title.Length == 0) title = task.Name.Trim();
        if (title.Length == 0) title = "Scheduled alert";

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.AlarmAlerts.Add(new AlarmAlert
        {
            CreatedAt = new DateTimeOffset(now, TimeSpan.Zero),
            Title     = title,
            Body      = text,
        });
        await db.SaveChangesAsync(ct);

        return (true, "Alert raised.");
    }

    private async Task<SlackPostResult> PostWebhookAsync(string hookId, string body, CancellationToken ct)
    {
        if (!int.TryParse(hookId, out var id)) return new SlackPostResult(false, null, null, "No webhook chosen.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var hook = await db.SlackWebhooks.FindAsync([id], ct);

        return hook is null
            ? new SlackPostResult(false, null, null, "That webhook has been deleted.")
            : await slack.PostWebhookAsync(hook.Url, body, null, ct);
    }
}
