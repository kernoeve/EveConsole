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

        var ran = false;

        foreach (var task in tasks)
        {
            if (!ScheduleDue.IsDue(task, now)) continue;

            var result = await RunOneAsync(task, now, ct);

            // ⚠️ Stamped whatever happened, success or not. A task that failed and kept its old
            // LastRun would be due again a minute later, and would spend the outage retrying at
            // the top of every minute — for a Slack post, that is a flood once Slack returns.
            task.LastRunUtc = now;
            task.LastResult = result;
            ran = true;
        }

        if (!ran) return;

        await db.SaveChangesAsync(ct);
        TasksChanged?.Invoke();
    }

    /// <summary>Runs one task and returns what to record against it.</summary>
    public async Task<string> RunOneAsync(ScheduledTask task, DateTime now, CancellationToken ct = default)
    {
        try
        {
            return task.TaskType switch
            {
                ScheduledTaskType.SlackPost => await SlackPostAsync(task, now, ct),
                _                           => $"Unknown task type \"{task.TaskType}\".",
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            errors.Log(nameof(SchedulerService), $"task {task.Id} \"{task.Name}\"", ex);
            return $"Failed: {ex.Message}";
        }
    }

    private async Task<string> SlackPostAsync(ScheduledTask task, DateTime now, CancellationToken ct)
    {
        var cfg = SlackPostConfig.FromJson(task.Config);

        if (cfg.Blocks.Count == 0) return "Nothing to post: no blocks configured.";

        var body = await renderer.RenderAsync(cfg.Blocks, now, ct);

        // ⚠️ An empty render is not a failure worth retrying, and not a success either. A Top 10
        // for a month nobody flew in renders to nothing, and posting an empty message would be
        // worse than saying so here.
        if (body.Trim().Length == 0) return "Nothing to post: the blocks rendered empty.";

        var res = cfg.DestinationKind == SlackDestination.KindWebhook
            ? await PostWebhookAsync(cfg.DestinationId, body, ct)
            : await slack.PostMessageAsync(cfg.DestinationId, body, ct: ct);

        return res.Ok ? $"Posted {body.Length:N0} characters." : $"Slack refused it: {res.Error}";
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
