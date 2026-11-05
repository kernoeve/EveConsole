using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EveConsole.Data;
using EveConsole.Localization;
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
    DiscordService                  discord,
    OnlineServices                  online,
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
    ///
    /// <para>The message is shown as it stands and saved as the task's LastResult, in the interface
    /// language of the machine that ran it. That is acceptable: only the Scheduler shows it, and
    /// a later run in another language simply writes its own.</para>
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
                _ => (true, string.Format(AlarmsText.RunUnknownType, task.TaskType)),
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            errors.Log(nameof(SchedulerService), $"task {task.Id} \"{task.Name}\"", ex);
            return (false, string.Format(AlarmsText.RunFailed, ex.Message));
        }
    }

    private async Task<(bool Ok, string Message)> SlackPostAsync(
        ScheduledTask task, DateTime now, CancellationToken ct)
    {
        var cfg = ScheduledTaskConfig.FromJson(task.Config);

        if (cfg.Blocks.Count == 0) return (true, AlarmsText.RunNoSections);

        // Discord is its own path from here: its own markup, its own splitting, and charts it can
        // carry. The Slack path below is untouched by it.
        if (cfg.DestinationKind == SlackDestination.KindDiscord)
            return await DiscordPostAsync(cfg, now, ct);

        var viaWebhook = cfg.DestinationKind == SlackDestination.KindWebhook;

        // A webhook cannot carry a file, only the address of an image. So its pictures go through
        // the online services, which is only possible once this database has signed in.
        var canDraw = !viaWebhook || await online.AccountAsync(ct) is not null;

        // ⚠️ Charts are drawn BEFORE anything is decided, because whether they came out is part
        // of whether there is anything to post. A task of nothing but charts renders no text at
        // all, and testing the text alone would have declared it empty and stopped.
        var plan    = await renderer.PlanPostAsync(cfg.Blocks, now, "Slack", canDraw, viaWebhook, ct);
        var skipped = plan.Skipped;

        if (!plan.AnyWords && !plan.AnyPictures)
            return (true, WithSkipped(AlarmsText.RunRenderedEmpty, skipped));

        // Asked for, and only then: a task whose static text is the point should still go out on a
        // quiet month. A chart that drew counts as something to say. ⚠️ Counts as the run either
        // way — it got as far as deciding what to send, and deciding to send nothing is a
        // decision, not a failure to retry.
        if (cfg.SkipIfNoDynamicContent && !plan.AnyDynamicContent && !plan.AnyPictures)
            return (true, WithSkipped(AlarmsText.RunNothingDynamic, skipped));

        var posted     = 0;
        var characters = 0;
        var anyRich    = false;
        var refused    = "";
        var uploaded   = 0;
        var failed     = new List<string>();
        var total      = plan.Segments.Sum(s => s.Messages.Count > 0 ? s.Messages.Count : SlackMessageSplitter.Split(s.Text).Count);

        string Outcome()
        {
            var what = posted == 0 ? AlarmsText.RunPosted
                     : anyRich     ? string.Format(AlarmsText.RunPostedRich, posted)
                     : posted > 1  ? string.Format(AlarmsText.RunPostedMessages, characters, posted)
                     :               string.Format(AlarmsText.RunPostedCharacters, characters);

            if (refused.Length > 0) what = string.Format(AlarmsText.RunRichRefused, refused) + " " + what;

            // Each outcome is a sentence of its own, after the one before.
            if (uploaded > 0)     what += " " + string.Format(AlarmsText.RunChartsUploaded, uploaded);
            if (failed.Count > 0) what += " " + string.Format(AlarmsText.RunChartsFailed, failed.Count, string.Join("; ", failed));
            return WithSkipped(what, skipped);
        }

        // ⚠️ In parts, never whole. Slack splits a long message by length alone, through the
        // middle of a code block, so a weekly post whose lists had grown arrived as two messages
        // with every table in both broken. The splitter ends each part cleanly.
        Task<SlackPartsResult> PostTextAsync(string text) =>
            SlackMessageSplitter.PostAsync(text, (part, token) => viaWebhook
                ? PostWebhookAsync(cfg.DestinationId, part, token)
                : slack.PostMessageAsync(cfg.DestinationId, part, ct: token), ct);

        foreach (var segment in plan.Segments)
        {
            ct.ThrowIfCancellationRequested();

            var hasWords = segment.Messages.Count > 0 || segment.Text.Length > 0;

            // The same second between posts as a long message's parts.
            if (hasWords && (posted > 0 || uploaded > 0)) await Task.Delay(TimeSpan.FromSeconds(1), ct);

            if (hasWords)
            {
                SlackPartsResult sent;
                if (segment.Messages.Count > 0)
                {
                    sent = await PostRichAsync(segment.Messages, (message, token) => viaWebhook
                        ? PostWebhookAsync(cfg.DestinationId, message.Fallback, token, message.Blocks)
                        : slack.PostMessageAsync(cfg.DestinationId, message.Fallback, blocks: message.Blocks, ct: token), ct);

                    if (sent.Posted == 0 && SlackRichBlocks.RefusedForFormat(sent.Error))
                    {
                        // ⚠️ Slack would not take the format. The numbers are still worth more than
                        // a post that never comes, so they go as text, and the result says why it
                        // looks different.
                        refused = sent.Error ?? "";
                        sent = await PostTextAsync((await renderer.RenderAsync(segment.Sections, now, ct, "Slack", segment.PicturesFollow)).Text);
                    }
                    else if (sent.Posted > 0) anyRich = true;
                }
                else
                {
                    sent = await PostTextAsync(segment.Text);
                }

                posted     += sent.Posted;
                characters += sent.Characters;

                if (!sent.AllPosted)
                {
                    // Nothing reached the channel, so the next pass can try the whole thing again.
                    if (posted == 0 && uploaded == 0) return (false, string.Format(AlarmsText.RunSlackRefused, sent.Error));

                    // ⚠️ Some of it did. Failing the run would post those parts again on every
                    // retry, so the run counts, and says what never arrived. Nothing goes after a
                    // gap: it would read as though nothing were missing.
                    return (true, WithSkipped(anyRich
                        ? string.Format(AlarmsText.RunPostedRichPart, posted, total, sent.Error)
                        : string.Format(AlarmsText.RunPostedPart, posted, total, characters, sent.Error), skipped));
                }
            }

            // Straight after the section they belong to, before the next one starts.
            foreach (var (png, title) in segment.Pictures)
            {
                var error = viaWebhook
                    ? await PostHostedPictureAsync(cfg.DestinationId, png, title, ct)
                    : await slack.UploadFileAsync(
                        cfg.DestinationId, png,
                        filename: $"{title.Replace(' ', '-').ToLowerInvariant()}.png",
                        title:    title,
                        ct:       ct);

                if (error is null) uploaded++;
                else failed.Add($"{title}: {error}");
            }
        }

        return (true, Outcome());
    }

    /// <summary>
    /// Posts rich messages in order, as <see cref="SlackMessageSplitter"/> posts parts: a second
    /// apart, a later one tried twice since the ones before it are already in the channel, and
    /// stopping at the first refused, since anything after a gap would read as nothing missing.
    /// </summary>
    public static async Task<SlackPartsResult> PostRichAsync(
        IReadOnlyList<SlackRichMessage> messages,
        Func<SlackRichMessage, CancellationToken, Task<SlackPostResult>> post,
        CancellationToken ct = default,
        TimeSpan? gap = null)
    {
        var between = gap ?? TimeSpan.FromSeconds(1);

        for (var i = 0; i < messages.Count; i++)
        {
            if (i > 0) await Task.Delay(between, ct);

            var res = await post(messages[i], ct);

            if (!res.Ok && i > 0)
            {
                await Task.Delay(between * 3, ct);
                res = await post(messages[i], ct);
            }

            if (!res.Ok) return new SlackPartsResult(i, messages.Count, 0, res.Error);
        }

        return new SlackPartsResult(messages.Count, messages.Count, 0, null);
    }

    /// <summary>
    /// A post task aimed at a Discord webhook.
    ///
    /// <para>The same decisions as the Slack path — empty, nothing dynamic, partly posted — with
    /// two differences. The text is rendered in Discord's markup, and charts go as image
    /// attachments, since a Discord webhook carries files where a Slack one cannot.</para>
    /// </summary>
    private async Task<(bool Ok, string Message)> DiscordPostAsync(
        ScheduledTaskConfig cfg, DateTime now, CancellationToken ct)
    {
        if (!int.TryParse(cfg.DestinationId, out var hookId))
            return (false, string.Format(AlarmsText.RunDiscordRefused, AlarmsText.RunNoWebhook));

        // ⚠️ Looked up before anything is drawn. A webhook removed since the task was saved is
        // the likeliest failure, and nothing is gained by rendering a month of charts first.
        var hook = await discord.FindAsync(hookId, ct);
        if (hook is null) return (false, string.Format(AlarmsText.RunDiscordRefused, AlarmsText.RunWebhookDeleted));

        var plan = await renderer.PlanPostAsync(cfg.Blocks, now, "Discord", canDraw: true, forWebhook: false, ct);

        if (!plan.AnyWords && !plan.AnyPictures) return (true, AlarmsText.RunRenderedEmpty);

        if (cfg.SkipIfNoDynamicContent && !plan.AnyDynamicContent && !plan.AnyPictures)
            return (true, AlarmsText.RunNothingDynamic);

        var posted     = 0;
        var characters = 0;
        var uploaded   = 0;
        var total      = 0;
        var failed     = new List<string>();

        foreach (var segment in plan.Segments)
        {
            ct.ThrowIfCancellationRequested();

            if (segment.Text.Length > 0)
            {
                var sent = await discord.PostAsync(hook.Url, segment.Text, hook.Name, ct);

                posted     += sent.Posted;
                characters += sent.Characters;
                total      += sent.Total;

                if (!sent.AllPosted)
                {
                    // Nothing reached the channel, so the next pass can try the whole thing again.
                    if (posted == 0 && uploaded == 0) return (false, string.Format(AlarmsText.RunDiscordRefused, sent.Error));

                    // ⚠️ Some of it did: the run counts, as on Slack, so a retry does not repeat it.
                    return (true, string.Format(AlarmsText.RunPostedPartDiscord, posted, total, characters, sent.Error));
                }
            }

            // Straight after the section they belong to. Each is a message of its own with its
            // title above it.
            foreach (var (png, title) in segment.Pictures)
            {
                var res = await discord.UploadAsync(
                    hook.Url, png,
                    filename: $"{title.Replace(' ', '-').ToLowerInvariant()}.png",
                    text:     $"**{title}**",
                    name:     hook.Name,
                    ct:       ct);

                if (res.Ok) uploaded++;
                else failed.Add($"{title}: {res.Error}");
            }
        }

        var what = posted == 0 ? AlarmsText.RunPosted
                 : posted > 1  ? string.Format(AlarmsText.RunPostedMessages, characters, posted)
                 :               string.Format(AlarmsText.RunPostedCharacters, characters);

        if (uploaded > 0)     what += " " + string.Format(AlarmsText.RunChartsUploaded, uploaded);
        if (failed.Count > 0) what += " " + string.Format(AlarmsText.RunChartsFailed, failed.Count, string.Join("; ", failed));

        return (true, what);
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
            : message + " " + string.Format(AlarmsText.RunChartsSkipped, skipped);

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

        if (text.Length == 0) return (true, AlarmsText.RunNoAlertText);

        // Both readers lead with the title and put the body under it. An unwritten headline falls
        // back to the task's name, which already answers "which task said this".
        var title = cfg.AlertTitle.Trim();
        if (title.Length == 0) title = task.Name.Trim();
        if (title.Length == 0) title = AlarmsText.RunAlertTitle;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.AlarmAlerts.Add(new AlarmAlert
        {
            CreatedAt = new DateTimeOffset(now, TimeSpan.Zero),
            Title     = title,
            Body      = text,
        });
        await db.SaveChangesAsync(ct);

        return (true, AlarmsText.RunAlertRaised);
    }

    private async Task<SlackPostResult> PostWebhookAsync(string hookId, string body, CancellationToken ct, object? blocks = null)
    {
        if (!int.TryParse(hookId, out var id)) return new SlackPostResult(false, null, null, AlarmsText.RunNoWebhook);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var hook = await db.SlackWebhooks.FindAsync([id], ct);

        return hook is null
            ? new SlackPostResult(false, null, null, AlarmsText.RunWebhookDeleted)
            : await slack.PostWebhookAsync(hook.Url, body, blocks, ct);
    }

    /// <summary>
    /// One picture through a webhook: uploaded to the online services, then posted by its address.
    /// Returns null on success, or why it failed.
    ///
    /// <para>⚠️ Titled on purpose. Slack shows a size and a collapse arrow beside an image with or
    /// without a title, and the title at least names the chart.</para>
    ///
    /// <para>⚠️ An upload Slack then refuses is deleted again, so a post that keeps failing does
    /// not spend the day's uploads and the storage on images nobody can see. It still counts
    /// against the day: deleting never gives uploads back.</para>
    /// </summary>
    private async Task<string?> PostHostedPictureAsync(string hookId, byte[] png, string title, CancellationToken ct)
    {
        // The same second between posts as the text's parts.
        await Task.Delay(TimeSpan.FromSeconds(1), ct);

        var uploaded = await online.UploadImageAsync(png, ct);

        // Trimmed: the result's sentence that lists these ends with its own full stop.
        if (!uploaded.Ok) return uploaded.Error?.TrimEnd('.', '。');

        object[] blocks =
        [
            new
            {
                type      = "image",
                image_url = uploaded.Url,
                alt_text  = title,
                title     = new { type = "plain_text", text = title },
            },
        ];

        var posted = await PostWebhookAsync(hookId, title, ct, blocks);
        if (posted.Ok) return null;

        if (uploaded.Id is { } id) await online.DeleteImageAsync(id, ct);
        return posted.Error;
    }
}
