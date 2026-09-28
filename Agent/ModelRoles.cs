using System.Text.Json;

namespace EveConsole.Agent;

public enum ModelRoleKind { Conversation, Analyst }

/// <summary>A change of the model answering for a role, and what is to be said about it.</summary>
/// <param name="Announcement">What the capsuleer is told; empty when the role says nothing.</param>
public sealed record ModelChange(ModelRoleKind Role, ModelProfile From, ModelProfile To, bool Returned, string Announcement);

/// <summary>
/// The models behind the agent while it runs: for each role, its model and its fallback, which of
/// the two answers now, and when to go back.
///
/// <para>The same rules as the voices, and for the same reason — a flaky server must not make the
/// agent flip back and forth. A model that stops answering is replaced AT ONCE, before a word of
/// the answer, when it has a fallback; it comes back only between turns, only after passing free
/// checks without a break for the steady time, never sooner than the gap after the last change,
/// and only after one real word from it.</para>
///
/// <para>⚠️ Unlike a voice, nothing is chosen silently at start. A voice that is not there at
/// start is simply not heard; a model that is not there at start and is quietly replaced is a free
/// model replaced by a paid one without anybody being told. So the first turn tries the role's own
/// model, and if it must fall over, it says so.</para>
/// </summary>
public sealed class ModelRoles : IDisposable
{
    /// <summary>One role while the agent runs.</summary>
    public sealed class Seat
    {
        internal Seat(ModelRoleKind kind, ModelProfile primary, IAgentProvider? primaryProvider,
                      ModelProfile? fallback, IAgentProvider? fallbackProvider, bool announce)
        {
            Kind             = kind;
            Primary          = primary;
            PrimaryProvider  = primaryProvider;
            Fallback         = fallback;
            FallbackProvider = fallbackProvider;
            Announce         = announce;
        }

        public ModelRoleKind   Kind             { get; }
        public ModelProfile    Primary          { get; }
        public ModelProfile?   Fallback         { get; }
        public bool            Announce         { get; }
        internal IAgentProvider? PrimaryProvider  { get; }
        internal IAgentProvider? FallbackProvider { get; }

        // Guarded by the owning ModelRoles' lock.
        internal bool            OnFallback;
        internal DateTimeOffset  LastChange = DateTimeOffset.MinValue;
        internal DateTimeOffset? UpSince;
        internal bool            ReturnReady;

        public bool IsOnFallback => OnFallback;

        /// <summary>The model answering for this role now.</summary>
        public ModelProfile Model => OnFallback && Fallback is not null ? Fallback : Primary;

        /// <summary>What answers for it now; null when that model is not set up — no key, no address.</summary>
        public IAgentProvider? Provider => OnFallback ? FallbackProvider : PrimaryProvider;

        /// <summary>Something can answer for this role: its model now, or the fallback it would
        /// fall over to.</summary>
        public bool CanAnswer => Provider is { IsConfigured: true }
                              || (!OnFallback && FallbackProvider is { IsConfigured: true });
    }

    private readonly object _gate = new();
    private string  _signature = "";
    private Seat?   _conversation;
    private Seat?   _analyst;
    private (ModelProfile Model, IAgentProvider? Provider)? _summary;

    private string   _userName        = "Capsuleer";
    private string   _failoverMessage = "";
    private string   _returnMessage   = "";
    private TimeSpan _switchGap       = TimeSpan.FromMinutes(10);
    private TimeSpan _preferredUp     = TimeSpan.FromMinutes(5);

    private readonly CancellationTokenSource _lifetime = new();
    private Task? _watcher;

    /// <summary>Tests replace the clock; everything else uses the real one.</summary>
    internal Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>Where the one real word before a return is costed, like any other call to a model.</summary>
    public AgentTelemetryService? Telemetry { get; set; }

    /// <summary>Where a watcher that failed unexpectedly is reported.</summary>
    public Services.AppErrorLogger? Errors { get; set; }

    public Seat? Conversation { get { lock (_gate) return _conversation; } }

    /// <summary>The same seat as <see cref="Conversation"/> when one model does everything.</summary>
    public Seat? Analyst { get { lock (_gate) return _analyst; } }

    /// <summary>Whether data questions go to a model of their own.</summary>
    public bool IsSplit
    {
        get { lock (_gate) return _conversation is not null && _analyst is not null && !ReferenceEquals(_conversation, _analyst); }
    }

    /// <summary>
    /// The model that compacts the history: its own when one is chosen, otherwise whatever answers
    /// the conversation now. <c>OwnModel</c> says which, so the caller can give it the prompt its
    /// cache already holds.
    /// </summary>
    public (ModelProfile Model, IAgentProvider? Provider, bool OwnModel)? Summary
    {
        get
        {
            lock (_gate)
            {
                if (_summary is { } s) return (s.Model, s.Provider, true);
                return _conversation is { } c ? (c.Model, c.Provider, false) : null;
            }
        }
    }

    /// <summary>
    /// Takes the settings. The seats — and so a fall-over in progress — are rebuilt only when
    /// something that decides them changed: a Save that touched nothing about the models must not
    /// throw away a fallback that is answering, nor send the agent back to a model that has just
    /// failed. The wording and the timings are taken every time.
    /// </summary>
    public void Configure(AgentSettings s, Func<ModelProfile, IAgentProvider?> build)
    {
        s.NormalizeModels();
        var signature = JsonSerializer.Serialize(new
        {
            s.Models, s.ConversationRole, s.AnalystRole, s.SummaryModelId,
            s.ClaudeApiKey, s.ClaudeCacheTtl, s.OpenAiApiKey,
        });

        lock (_gate)
        {
            _userName        = string.IsNullOrWhiteSpace(s.UserName)
                               || s.UserName.Trim().Equals(AgentSettings.DefaultUserName, StringComparison.OrdinalIgnoreCase)
                                   ? "Capsuleer" : s.UserName.Trim();
            _failoverMessage = s.ModelFailoverMessageText;
            _returnMessage   = s.ModelReturnMessageText;
            _switchGap       = TimeSpan.FromMinutes(Math.Max(0, s.ModelSwitchGapMinutes));
            _preferredUp     = TimeSpan.FromMinutes(Math.Max(0, s.ModelPreferredUpMinutes));

            if (signature != _signature)
            {
                _signature    = signature;
                _conversation = MakeSeat(ModelRoleKind.Conversation, s.ConversationRole, s, build);
                _analyst      = s.RolesSplit ? MakeSeat(ModelRoleKind.Analyst, s.AnalystRole, s, build) : _conversation;
                _summary      = s.ModelById(s.SummaryModelId) is { } model ? (model, build(model)) : null;
            }
        }

        _watcher ??= Task.Run(() => WatchAsync(_lifetime.Token));
    }

    private static Seat? MakeSeat(ModelRoleKind kind, ModelRole role, AgentSettings s, Func<ModelProfile, IAgentProvider?> build)
    {
        if (s.ModelById(role.ModelId) is not { } primary) return null;
        var fallback = s.ModelById(role.FallbackId) is { } f && f.Id != primary.Id ? f : null;
        return new Seat(kind, primary, build(primary), fallback, fallback is null ? null : build(fallback), role.Announce);
    }

    // ── Falling over ───────────────────────────────────────────────────────────

    /// <summary>
    /// The seat's model has stopped answering: its fallback takes over now, if it has one that is
    /// set up. Null when there is nothing to fall over to — already on the fallback, or none.
    /// </summary>
    public ModelChange? FailOver(Seat seat)
    {
        lock (_gate)
        {
            if (seat.OnFallback || seat.Fallback is null || seat.FallbackProvider is not { IsConfigured: true }) return null;
            seat.OnFallback  = true;
            seat.LastChange  = Clock();
            seat.UpSince     = null;
            seat.ReturnReady = false;
            return new ModelChange(seat.Kind, seat.Primary, seat.Fallback, Returned: false,
                                   seat.Announce ? Format(_failoverMessage, seat) : "");
        }
    }

    // ── Coming back ────────────────────────────────────────────────────────────

    private async Task WatchAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
            catch (OperationCanceledException) { return; }
            try { await CheckAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { Errors?.Log("ModelRoles", "Model watcher", ex); }
        }
    }

    /// <summary>
    /// One round of the watch: for each role on its fallback, whether its own model is up — free
    /// checks only — since when, and whether it has been up long enough, and long enough after the
    /// last change, to go back.
    /// </summary>
    internal async Task CheckAsync(CancellationToken ct = default)
    {
        Seat[] waiting;
        lock (_gate) waiting = Seats().Where(s => s.OnFallback).ToArray();

        foreach (var seat in waiting)
        {
            var up = seat.PrimaryProvider is { } provider && await SafeAvailableAsync(provider, ct);
            lock (_gate)
            {
                if (!seat.OnFallback) continue;
                var now = Clock();
                if (!up) { seat.UpSince = null; seat.ReturnReady = false; continue; }   // a failed check starts the clock again
                seat.UpSince   ??= now;
                seat.ReturnReady = now - seat.UpSince.Value >= _preferredUp && now - seat.LastChange >= _switchGap;
            }
        }
    }

    private static async Task<bool> SafeAvailableAsync(IAgentProvider provider, CancellationToken ct)
    {
        try   { return await provider.IsAvailableAsync(ct); }
        catch { return false; }
    }

    /// <summary>
    /// Between turns: every role whose own model is back and has stayed up goes back to it — after
    /// one real word from it — and the changes are returned for the caller to announce. Called
    /// before each turn, so a return never lands mid-answer.
    /// </summary>
    public async Task<IReadOnlyList<ModelChange>> ApplyPendingReturnsAsync(CancellationToken ct = default)
    {
        Seat[] ready;
        lock (_gate) ready = Seats().Where(s => s.OnFallback && s.ReturnReady).ToArray();
        if (ready.Length == 0) return [];

        var changes = new List<ModelChange>();
        foreach (var seat in ready)
        {
            // ⚠️ One real word first. The model list says a server is up, not that the model will
            // load — Ollama lists a model it then cannot fit beside whatever else holds the GPU —
            // and a return that failed on its first question would announce itself twice in one
            // turn. A word costs nothing on a model of our own and a few tokens on a paid one.
            var confirmed = await ConfirmAsync(seat, ct);
            lock (_gate)
            {
                if (!seat.OnFallback || !seat.ReturnReady || seat.Fallback is null) continue;
                if (!confirmed) { seat.UpSince = null; seat.ReturnReady = false; continue; }
                seat.OnFallback  = false;
                seat.LastChange  = Clock();
                seat.UpSince     = null;
                seat.ReturnReady = false;
                changes.Add(new ModelChange(seat.Kind, seat.Fallback, seat.Primary, Returned: true,
                                            seat.Announce ? Format(_returnMessage, seat) : ""));
            }
        }
        return changes;
    }

    private async Task<bool> ConfirmAsync(Seat seat, CancellationToken ct)
    {
        if (seat.PrimaryProvider is not { IsConfigured: true } provider) return false;
        var telemetry = Telemetry;
        telemetry?.Begin("model-check", provider.ProviderName, seat.Primary.ModelName, 0);
        var failure = "";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));   // a local model may have to load first
            await foreach (var chunk in provider.StreamAsync(
                               "Reply with the single word: ready.",
                               [new AgentMessage(MessageRole.User, "Are you there?")],
                               tools: null, onUsage: u => telemetry?.Usage(u), ct: timeout.Token).ConfigureAwait(false))
                if (chunk.Trim().Length > 0) return true;
            failure = "no answer";
            return false;
        }
        catch (Exception ex) { failure = ex.Message; return false; }
        finally { telemetry?.Complete(0, failure); }
    }

    // ── Words ──────────────────────────────────────────────────────────────────

    private string Format(string template, Seat seat) =>
        (template ?? "").Replace("{user}",     _userName, StringComparison.OrdinalIgnoreCase)
                        .Replace("{purpose}",  seat.Kind == ModelRoleKind.Analyst ? "data access" : "our conversation",
                                 StringComparison.OrdinalIgnoreCase)
                        .Replace("{primary}",  seat.Primary.Label, StringComparison.OrdinalIgnoreCase)
                        .Replace("{fallback}", seat.Fallback?.Label ?? "", StringComparison.OrdinalIgnoreCase)
                        .Trim();

    /// <summary>The distinct seats — one when a single model does everything. Under the lock.</summary>
    private IEnumerable<Seat> Seats()
    {
        if (_conversation is { } c) yield return c;
        if (_analyst is { } a && !ReferenceEquals(a, _conversation)) yield return a;
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
