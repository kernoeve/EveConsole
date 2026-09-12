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
    private readonly ClientSignals                   _signals;
    private readonly AlarmMuteState                  _mute;

    public AlarmActionRunner(
        IDbContextFactory<AppDbContext> dbFactory,
        AlarmSoundService               sounds,
        AppErrorLogger                  errors,
        ClientSignals                   signals,
        AlarmMuteState                  mute)
    {
        _dbFactory = dbFactory;
        _sounds    = sounds;
        _errors    = errors;
        _signals   = signals;
        _mute      = mute;
    }

    /// <summary>Hands the agent something to tell the user about. Set by MainWindow.</summary>
    public Func<string, Task>? NotifyAgentCallback { get; set; }

    /// <summary>Has the agent say a text exactly as given, at once, in its own voice — no model.</summary>
    public Func<string, Task>? AnnounceCallback { get; set; }

    /// <summary>Raises a top-most dialog: (title, message). Set by MainWindow.</summary>
    public Action<string, string>? ShowDialogCallback { get; set; }

    /// <summary>True when the agent is configured well enough for AgentNotify to reach the user.</summary>
    public Func<bool>? AgentAvailable { get; set; }

    /// <summary>
    /// Turns a firing into its effects. Runs on the client holding the worker lease.
    ///
    /// <para>⚠️ Only the Alert action happens here. The other three — a sound, a dialog, the agent
    /// speaking — happen at a person's machine, and the worker may be headless or on a server in
    /// another room. So it does the one thing that is a database write, resolves the wording for
    /// the rest while it still has the matches in hand, and publishes. Every client, this one
    /// included, then performs whatever it is willing to.</para>
    /// </summary>
    /// <param name="defaults">
    /// Title and body supplied by the condition, used wherever the user has not written their
    /// own. Resolved by the caller, which is what holds the registry.
    /// </param>
    public async Task RunAsync(
        Alarm                        alarm,
        IReadOnlyList<AlarmAction>   actions,
        AlarmEvent                   evt,
        IReadOnlyList<AlarmMatch>    matches,
        (string Title, string Body)  defaults,
        string?                      announcement = null,
        CancellationToken            ct = default)
    {
        var signal = new AlarmSignal { AlarmId = alarm.Id, Name = alarm.Name };

        // Whether anything other than the agent was asked for. Decides, below, if silence from the
        // agent would lose the firing altogether.
        var somethingElse = false;

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
                    case AlarmActionKind.Alert:
                        // A row, so it reaches every client whether or not it was listening,
                        // muted, or even running.
                        await RunAlertAsync(alarm, evt, cfg, defaults, ct);
                        somethingElse = true;
                        break;

                    case AlarmActionKind.Sound:
                        signal.SoundKey    = Str(cfg, "sound")  ?? AlarmSoundService.DefaultKey;
                        signal.SoundVolume = Int(cfg, "volume") ?? 100;
                        somethingElse      = true;
                        break;

                    case AlarmActionKind.Dialog:
                        signal.DialogTitle = Expand(Str(cfg, "title")   ?? defaults.Title, alarm, evt);
                        signal.DialogBody  = Expand(Str(cfg, "message") ?? defaults.Body,  alarm, evt);
                        somethingElse      = true;
                        break;

                    case AlarmActionKind.AgentNotify:
                        // ⚠️ A condition that composed its own announcement is quoted, not
                        // paraphrased, and spoken without a model round trip: for intel the
                        // difference is several seconds, and the order of the facts is the point.
                        // A standing instruction on the alarm still goes through the model, with
                        // the announcement as the text it must say first.
                        if (announcement is not null && string.IsNullOrWhiteSpace(Str(cfg, "instruction")))
                            signal.SpeakText = announcement;
                        else
                            signal.AgentText = ComposeAgentPrompt(alarm, evt, matches, cfg, announcement);
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

        if ((signal.AgentText is not null || signal.SpeakText is not null) && !somethingElse)
        {
            signal.AgentOnly     = true;
            signal.FallbackTitle = alarm.Name;
            signal.FallbackBody  = evt.Summary
                                 + "\n\n(The agent is not configured, so this was recorded as an alert.)";
        }

        // ⚠️ The fallback is decided here rather than by each client, and only by a worker that
        // cannot speak it itself. Left to the clients, every one of them without an agent would
        // write its own copy of the same alert. This keeps a single client behaving exactly as it
        // did before — it is both worker and speaker, so it asks itself — while a headless worker
        // errs towards recording a warning that may also get spoken elsewhere. An extra row in a
        // list you can dismiss is the right way to be wrong about this.
        if (signal.AgentOnly && !CanSpeak())
        {
            await WriteAlertAsync(alarm, evt, signal.FallbackTitle!, signal.FallbackBody!, ct);
            return;
        }

        if (signal.SoundKey is null && signal.DialogTitle is null && signal.AgentText is null && signal.SpeakText is null) return;

        await _signals.PublishAsync(JsonSerializer.Serialize(signal), ct);
    }

    /// <summary>
    /// Performs the parts of a firing that belong to this machine.
    ///
    /// <para>Reached from <see cref="ClientSignals"/>, on every client including the worker's own
    /// — which is why nothing here asks whether this process raised the alarm.</para>
    /// </summary>
    public async Task HandleSignalAsync(string payload, CancellationToken ct = default)
    {
        AlarmSignal? s;
        try { s = JsonSerializer.Deserialize<AlarmSignal>(payload); }
        catch (Exception ex)
        {
            _errors.Log("AlarmActionRunner", "reading an alarm signal", ex);
            return;
        }

        if (s is null || s.Kind != AlarmSignal.SignalKind) return;

        // ⚠️ Checked here, on the receiving side, and never on the worker. Muting is a fact about
        // this machine — one client can be quiet while another is not — and a worker that filtered
        // on its own setting would silence everybody's.
        if (_mute.Muted) return;

        if (s.SoundKey is not null)
        {
            try { await _sounds.PlayAsync(s.SoundKey, s.SoundVolume, ct); }
            catch (Exception ex) { _errors.Log("AlarmActionRunner", $"sound for alarm {s.AlarmId}", ex); }
        }

        if (s.DialogTitle is not null && ShowDialogCallback is { } show)
        {
            Dispatcher.UIThread.Post(() =>
            {
                try { show(s.DialogTitle, s.DialogBody ?? ""); }
                catch { /* a closed window is not an error */ }
            });
        }

        if (s.SpeakText is not null && CanSpeak() && AnnounceCallback is { } announce)
        {
            try { await announce(s.SpeakText); }
            catch (Exception ex) { _errors.Log("AlarmActionRunner", $"announcement for alarm {s.AlarmId}", ex); }
        }

        if (s.AgentText is not null && CanSpeak())
        {
            try { await NotifyAgentCallback!(s.AgentText); }
            catch (Exception ex) { _errors.Log("AlarmActionRunner", $"agent notify for alarm {s.AlarmId}", ex); }
        }
    }

    /// <summary>Whether this process can actually get the agent to say something.</summary>
    private bool CanSpeak() => NotifyAgentCallback is not null && AgentAvailable?.Invoke() != false;

    private string ComposeAgentPrompt(
        Alarm alarm, AlarmEvent evt, IReadOnlyList<AlarmMatch> matches, JsonElement cfg, string? announcement = null)
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
             {(announcement is null
                 ? "Tell the capsuleer what happened, in one or two sentences, in your own words, using\n" +
                   "the detail above."
                 : "Say exactly this, first and word for word, adding nothing before it: " + announcement + "\n" +
                   "Every fact is in that sentence and in the order it matters; do not reorder it, pad it,\n" +
                   "or say who reported it.")} Do not call any tools to confirm it — the alarm already did the
             checking, and everything you need is in this message. Do not ask a follow-up
             question; just report it.
             {(string.IsNullOrWhiteSpace(extra) ? "" : $"\nStanding instruction from the capsuleer for this alarm: {extra}")}
             """;

        return message;
    }

    private Task RunAlertAsync(
        Alarm alarm, AlarmEvent evt, JsonElement cfg, (string Title, string Body) defaults, CancellationToken ct)
    {
        // Absent config means "use the default", which is what the editor writes when the
        // capsuleer leaves the box ticked.
        var title = Expand(Str(cfg, "title") ?? defaults.Title, alarm, evt);
        var body  = Expand(Str(cfg, "body")  ?? defaults.Body,  alarm, evt);
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
