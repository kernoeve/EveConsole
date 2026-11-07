namespace EveConsole.Services.ClientLayouts;

/// <summary>A panel on screen and whether it should be showing.</summary>
public readonly record struct PanelState(string MonitorKey, PlacedPanel Panel, bool Show);

/// <summary>Something the user should know about what placement did or didn't do.</summary>
public sealed record ClientLayoutNotice(DateTimeOffset At, PlacementNoticeKind Kind, string? Character);

/// <summary>
/// Keeps this desktop's clients in their places while placement is on: it looks at client windows
/// a few times a second, applies the rules and carries out the moves. Every client runs its own,
/// because clients are on a particular desktop; only one copy per desktop acts (DeviceIdentity).
///
/// <para>The rules live in <see cref="ClientPlacementEngine"/> and are used only on this loop's
/// thread, so callers ask for a reload or an apply rather than touching them.</para>
/// </summary>
public sealed class ClientLayoutService(ClientLayoutStore store, AppErrorLogger errorLogger)
{
    private static readonly TimeSpan Look = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan LookAtMonitors = TimeSpan.FromSeconds(3);
    private const int NoticesKept = 30;

    private readonly object _gate = new();
    private readonly List<ClientLayoutNotice> _notices = [];
    private IReadOnlyList<PanelState> _panels = [];
    private IReadOnlyList<string> _seen = [];
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _wantReload = true;
    private bool _wantApply;
    private string? _lastError;

    /// <summary>Placement is for a desktop with a person at it, and only Windows gives us the windows.</summary>
    public static bool IsSupported => OperatingSystem.IsWindows() && !AppRuntime.IsHeadless;

    /// <summary>Raised off the UI thread whenever which panels should show has changed.</summary>
    public event Action<IReadOnlyList<PanelState>>? PanelsChanged;

    /// <summary>Raised off the UI thread when a notice is added, or the list is cleared.</summary>
    public event Action? NoticesChanged;

    /// <summary>Raised off the UI thread when a new name has been seen in a client window.</summary>
    public event Action? SeenCharactersChanged;

    public IReadOnlyList<ClientLayoutNotice> Notices
    {
        get { lock (_gate) return [.. _notices]; }
    }

    public IReadOnlyList<string> SeenCharacters => _seen;

    public void Start()
    {
        if (!IsSupported || _loop != null) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loop = Task.Run(() => RunAsync(token), token);
    }

    public async Task StopAsync()
    {
        var cts = _cts;
        var loop = _loop;
        _cts = null;
        _loop = null;
        if (cts == null || loop == null) return;
        await cts.CancelAsync().ConfigureAwait(false);
        try { await loop.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        cts.Dispose();
        DeviceIdentity.ReleasePlacement();
        PanelsChanged?.Invoke([]);
    }

    /// <summary>Saved state changed in a way that takes effect at once, such as the on/off switch.</summary>
    public void RequestReload()
    {
        lock (_gate) _wantReload = true;
    }

    /// <summary>The user pressed Apply, and what it applies is already saved.</summary>
    public void RequestApply()
    {
        lock (_gate)
        {
            _wantReload = true;
            _wantApply = true;
            _notices.Clear();
        }
        NoticesChanged?.Invoke();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        ClientPlacementEngine? engine = null;
        DesktopPlan? plan = null;
        AppliedDesktop applied = new();
        IReadOnlyList<DesktopMonitor> monitors = [];
        var monitorsRead = DateTime.MinValue;
        string deviceId = "";
        var seenSaved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                bool reload, apply;
                lock (_gate)
                {
                    reload = _wantReload;
                    apply = _wantApply;
                    _wantReload = _wantApply = false;
                }

                var now = DateTime.UtcNow;
                var replan = false;
                if (reload)
                {
                    var device = await store.ThisDeviceAsync(ct).ConfigureAwait(false);
                    deviceId = device.Id;
                    foreach (var name in ClientLayoutStore.ReadSeenCharacters(device.SeenCharacters)) seenSaved.Add(name);
                    _seen = [.. seenSaved];
                    SeenCharactersChanged?.Invoke();

                    var on = device.PlacementOn && DeviceIdentity.TryHoldPlacement();
                    if (!device.PlacementOn) DeviceIdentity.ReleasePlacement();
                    if (!on) engine = null;
                    else engine ??= new ClientPlacementEngine();
                    applied = AppliedDesktop.Read(device.Applied);
                    replan = true;
                    if (device.PlacementOn && !on)
                        lock (_gate) _wantReload = true;   // another copy holds it; try again next time
                }

                if (engine == null)
                {
                    Publish([]);
                    await Task.Delay(LookAtMonitors, ct).ConfigureAwait(false);
                    continue;
                }

                if (now - monitorsRead >= LookAtMonitors || replan)
                {
                    var current = ClientWindows.FindMonitors();
                    monitorsRead = now;
                    if (!monitors.SequenceEqual(current))
                    {
                        monitors = current;
                        replan = true;
                    }
                }
                if (replan)
                {
                    plan = ClientLayoutPlanner.Build(applied, monitors);
                    engine.UsePlan(plan.Placement);
                }

                var windows = ClientWindows.FindClients();
                var step = apply ? engine.Apply(windows, now) : engine.Step(windows, now);
                foreach (var move in step.Moves) ClientWindows.Move(move);
                AddNotices(step.Notices);

                if (plan != null)
                {
                    Publish([.. plan.Panels.SelectMany(m =>
                    {
                        var show = ClientLayoutPlanner.PanelsShow(m.Start, engine.ClientsOn(m.Bounds));
                        return m.Panels.Select(p => new PanelState(m.Key, p, show && !p.Rect.IsEmpty));
                    })]);
                }

                if (engine.SeenCharacters.Any(n => !seenSaved.Contains(n)))
                {
                    seenSaved.UnionWith(engine.SeenCharacters);
                    _seen = [.. seenSaved];
                    SeenCharactersChanged?.Invoke();
                    await store.SaveSeenCharactersAsync(deviceId, seenSaved, ct).ConfigureAwait(false);
                }
                _lastError = null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Reported once, not twice a second, and the loop carries on: a database that is
                // away for a moment shouldn't end placement until the next restart.
                if (_lastError != ex.Message)
                {
                    _lastError = ex.Message;
                    errorLogger.Log("ClientLayouts", "keeping clients in place", ex);
                }
            }
            await Task.Delay(Look, ct).ConfigureAwait(false);
        }
    }

    private void Publish(IReadOnlyList<PanelState> panels)
    {
        if (_panels.SequenceEqual(panels)) return;
        _panels = panels;
        PanelsChanged?.Invoke(panels);
    }

    private void AddNotices(List<PlacementNotice> notices)
    {
        if (notices.Count == 0) return;
        lock (_gate)
        {
            foreach (var n in notices)
                _notices.Add(new ClientLayoutNotice(DateTimeOffset.Now, n.Kind, n.Character));
            if (_notices.Count > NoticesKept) _notices.RemoveRange(0, _notices.Count - NoticesKept);
        }
        NoticesChanged?.Invoke();
    }
}
