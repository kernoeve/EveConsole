using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using EveConsole.Alarms;
using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>
/// Turns a firing into its effects. The UI-facing ones are callbacks set by MainWindow after
/// startup — the same arrangement <see cref="Agent.AgentService"/> uses for its action tools —
/// so the evaluation loop stays free of view concerns and still works headless.
/// </summary>
public sealed class AlarmActionRunner
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly AlarmSoundService               _sounds;
    private readonly AppErrorLogger                  _errors;

    public AlarmActionRunner(
        IDbContextFactory<AppDbContext> dbFactory,
        AlarmSoundService               sounds,
        AppErrorLogger                  errors)
    {
        _dbFactory = dbFactory;
        _sounds    = sounds;
        _errors    = errors;
    }

    /// <summary>Hands the agent something to tell the user about. Set by MainWindow.</summary>
    public Func<string, Task>? NotifyAgentCallback { get; set; }

    /// <summary>Raises a top-most dialog: (title, message). Set by MainWindow.</summary>
    public Action<string, string>? ShowDialogCallback { get; set; }

    /// <summary>True when the agent is configured well enough for AgentNotify to reach the user.</summary>
    public Func<bool>? AgentAvailable { get; set; }

    public async Task RunAsync(
        Alarm                        alarm,
        IReadOnlyList<AlarmAction>   actions,
        AlarmEvent                   evt,
        IReadOnlyList<AlarmMatch>    matches,
        CancellationToken            ct = default)
    {
        foreach (var action in actions)
        {
            if (ct.IsCancellationRequested) break;

            JsonElement cfg;
            try { cfg = JsonDocument.Parse(action.ConfigJson ?? "{}").RootElement.Clone(); }
            catch { cfg = default; }

            try
            {
                switch (action.Kind)
                {
                    case AlarmActionKind.Sound:
                        await RunSoundAsync(cfg, ct);
                        break;

                    case AlarmActionKind.AgentNotify:
                        await RunAgentNotifyAsync(alarm, evt, matches, cfg, ct);
                        break;

                    case AlarmActionKind.Alert:
                        await RunAlertAsync(alarm, evt, cfg, ct);
                        break;

                    case AlarmActionKind.Dialog:
                        RunDialog(alarm, evt, cfg);
                        break;
                }
            }
            catch (Exception ex)
            {
                // One failed action must not stop the others — a muted sound device should
                // never cost the user the dialog that mattered.
                _errors.Log("AlarmActionRunner", $"{action.Kind} for alarm {alarm.Id}", ex);
            }
        }
    }

    private async Task RunSoundAsync(JsonElement cfg, CancellationToken ct)
    {
        var key    = Str(cfg, "sound") ?? AlarmSoundService.DefaultKey;
        var volume = Int(cfg, "volume") ?? 100;
        await _sounds.PlayAsync(key, volume, ct);
    }

    private async Task RunAgentNotifyAsync(
        Alarm alarm, AlarmEvent evt, IReadOnlyList<AlarmMatch> matches, JsonElement cfg, CancellationToken ct)
    {
        var extra = Str(cfg, "instruction");

        // The detail of each match goes over too, not just the count. Without it the agent can
        // only say "something happened"; with it, it can say which pilot, in which ship, where.
        var detail = new StringBuilder();
        foreach (var m in matches.Take(10))
        {
            detail.Append("- ").Append(m.Summary);
            if (m.Detail is { Count: > 0 })
            {
                detail.Append(" [")
                      .Append(string.Join(", ", m.Detail
                          .Where(kv => kv.Value is not null)
                          .Select(kv => $"{kv.Key}: {kv.Value}")))
                      .Append(']');
            }
            detail.AppendLine();
        }
        if (matches.Count > 10) detail.AppendLine($"- (+{matches.Count - 10} more)");

        var message =
            $"""
             ALARM FIRED — this is the prompt, not a request to look something up.

             Alarm: {alarm.Name}
             Summary: {evt.Summary}
             Matches ({evt.MatchCount}):
             {detail}
             Tell the capsuleer what happened, in one or two sentences, in your own words, using
             the detail above. Do not call any tools to confirm it — the alarm already did the
             checking, and everything you need is in this message. Do not ask a follow-up
             question; just report it.
             {(string.IsNullOrWhiteSpace(extra) ? "" : $"\nStanding instruction from the capsuleer for this alarm: {extra}")}
             """;

        if (NotifyAgentCallback is { } notify && AgentAvailable?.Invoke() != false)
        {
            await notify(message);
            return;
        }

        // No agent to speak through — fall back to a persistent alert rather than firing into
        // the void, since this action is usually the *only* one on an agent-created alarm.
        await WriteAlertAsync(alarm, evt, alarm.Name,
            evt.Summary + "\n\n(The agent is not configured, so this was recorded as an alert.)", ct);
    }

    private Task RunAlertAsync(Alarm alarm, AlarmEvent evt, JsonElement cfg, CancellationToken ct)
    {
        var title = Expand(Str(cfg, "title") ?? alarm.Name, alarm, evt);
        var body  = Expand(Str(cfg, "body")  ?? evt.Summary, alarm, evt);
        return WriteAlertAsync(alarm, evt, title, body, ct);
    }

    private async Task WriteAlertAsync(Alarm alarm, AlarmEvent evt, string title, string body, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.AlarmAlerts.Add(new AlarmAlert
        {
            AlarmId      = alarm.Id,
            AlarmEventId = evt.Id,
            CreatedAt    = evt.FiredAt,
            Title        = title,
            Body         = body,
        });
        await db.SaveChangesAsync(ct);
    }

    private void RunDialog(Alarm alarm, AlarmEvent evt, JsonElement cfg)
    {
        if (ShowDialogCallback is not { } show) return;

        var title   = Expand(Str(cfg, "title")   ?? "Alarm", alarm, evt);
        var message = Expand(Str(cfg, "message") ?? evt.Summary, alarm, evt);

        Dispatcher.UIThread.Post(() =>
        {
            try { show(title, message); } catch { /* a closed window is not an error */ }
        });
    }

    /// <summary>
    /// Substitutes the placeholders a user may put in a dialog or alert message. Unknown
    /// placeholders are left alone rather than blanked, so a typo is visible instead of silent.
    /// </summary>
    private static string Expand(string template, Alarm alarm, AlarmEvent evt) =>
        template
            .Replace("{alarm}",   alarm.Name,                                StringComparison.OrdinalIgnoreCase)
            .Replace("{summary}", evt.Summary,                               StringComparison.OrdinalIgnoreCase)
            .Replace("{count}",   evt.MatchCount.ToString(),                 StringComparison.OrdinalIgnoreCase)
            .Replace("{time}",    evt.FiredAt.ToLocalTime().ToString("HH:mm"), StringComparison.OrdinalIgnoreCase)
            .Replace("{date}",    evt.FiredAt.ToLocalTime().ToString("d MMM yyyy"), StringComparison.OrdinalIgnoreCase);

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object
        && e.TryGetProperty(name, out var p)
        && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

    private static int? Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object
        && e.TryGetProperty(name, out var p)
        && p.ValueKind == JsonValueKind.Number
        && p.TryGetInt32(out var v)
            ? v
            : null;
}
