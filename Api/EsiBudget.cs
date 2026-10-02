using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace EveConsole.Api;

/// <summary>How hard the governor is holding background work back. Ordered: each is stricter.</summary>
public enum EsiGovernorLevel
{
    /// <summary>Plenty of error budget: background work at full speed.</summary>
    Normal,
    /// <summary>Error budget under <see cref="EsiBudget.SpacedBelow"/>: background calls spaced out.</summary>
    Spaced,
    /// <summary>Under <see cref="EsiBudget.OneAtATimeBelow"/>: background work one call at a time.</summary>
    OneAtATime,
    /// <summary>Under <see cref="EsiBudget.StoppedBelow"/>: background calls wait for the error window to reset.</summary>
    Stopped,
}

/// <summary>One rate-limit group as ESI last described it.</summary>
public sealed record EsiGroupState(
    string Group, int? Tokens, TimeSpan? Window, int? Remaining, int? Used, DateTimeOffset SeenAt,
    IReadOnlyList<string> Routes, int Refusals, DateTimeOffset? BlockedUntil, bool Paced)
{
    /// <summary>What is left, as ESI last said — or the whole allowance once a window has passed
    /// since then. ⚠️ Not refilled in between: the bucket is a floating window (a token comes back
    /// a window after it was spent), so how fast it refills depends on when it was spent, which
    /// the app does not see. Calls on the group keep the figure current, and when nothing calls
    /// it, nothing needs it.</summary>
    public int? CurrentRemaining(DateTimeOffset now) =>
        Window is { } w && now - SeenAt >= w ? Tokens : Remaining;
}

/// <summary>Errors one route has drawn: the last hour, since the app started, and the last one.</summary>
public sealed record EsiRouteErrors(string Route, int Status, int LastMinute, int LastHour, long SinceStart, DateTimeOffset LastAt);

/// <summary>Everything the ESI limits panel shows, at one moment.</summary>
public sealed record EsiBudgetSnapshot(
    int? ErrorRemain, int? ErrorLimit, DateTimeOffset? ErrorResetAt,
    int OursThisWindow, int? OthersThisWindow,
    int CallsLastMinute, int ErrorsLastMinute, int ErrorsLastHour,
    int Refused420, int Refused429,
    EsiGovernorLevel Level, IReadOnlyList<EsiGroupState> Groups, IReadOnlyList<EsiRouteErrors> Errors);

/// <summary>
/// ESI's limits as the responses describe them, for every call the app makes, and the governor
/// that holds background work back as they run low.
///
/// <para><b>The error limit</b> is ESI's per-IP budget of failed calls (4xx and 5xx) per window of
/// about a minute — X-ESI-Error-Limit-Remain and -Reset on every response. Going over it gets every
/// call refused with 420 until the window resets. ⚠️ It is the CONNECTION's, not the app's: every
/// client behind the same public address draws on it, so the remaining figure falls for errors
/// this app never made. The governor reads ESI's figure, not its own count, for that reason.</para>
///
/// <para><b>Rate-limit groups</b> are token buckets ESI puts on some routes, announced with
/// X-Ratelimit-Group, -Limit ("150/15m"), -Remaining and -Used — Used being what that one call
/// cost (2 tokens for a success, measured). A bucket emptied answers 429 with Retry-After. ⚠️ The
/// two are separate: a route with a bucket sends no error-limit headers, and one without a bucket
/// (public contract items, for one) is under the error limit only — measured 2026-10-02.</para>
///
/// <para><b>The governor</b> only ever slows the background lane (<see cref="EsiClient.Background"/>):
/// a call the user is waiting on is never held back by it. Below <see cref="SpacedBelow"/> errors
/// left, background calls are spaced out; below <see cref="OneAtATimeBelow"/> they go one at a time;
/// below <see cref="StoppedBelow"/> they wait for the window to reset — where the client already
/// stands everything down. A bucket under a fifth full is paced at its own refill rate.</para>
/// </summary>
public sealed class EsiBudget
{
    /// <summary>The one budget: ESI's limits are the connection's, so there is one record of them.</summary>
    public static EsiBudget Shared { get; } = new();

    public const int SpacedBelow     = 70;
    public const int OneAtATimeBelow = 50;
    public const int StoppedBelow    = 20;

    private static readonly TimeSpan SpacedGap     = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan OneAtATimeGap = TimeSpan.FromMilliseconds(1000);
    private const double PaceBelowShare = 0.2;

    private readonly object _lock = new();

    // The error window as ESI last described it.
    private int?           _errorRemain;
    private DateTimeOffset _errorResetAt;
    private int            _oursThisWindow;
    private int            _maxSeenRemain = 100;

    // Calls and errors, newest last; trimmed to a minute and an hour.
    private readonly Queue<DateTimeOffset> _calls = new();
    private readonly Queue<(DateTimeOffset At, string Route, int Status)> _errors = new();
    private readonly Dictionary<(string Route, int Status), long> _errorTotals = [];
    private int _refused420, _refused429;

    private sealed class Group
    {
        public int? Tokens; public TimeSpan? Window; public int? Remaining; public int? Used;
        public DateTimeOffset SeenAt; public readonly HashSet<string> Routes = [];
        public int Refusals; public DateTimeOffset? BlockedUntil; public DateTimeOffset NextPaced;
    }
    private readonly Dictionary<string, Group> _groups = [];
    private readonly ConcurrentDictionary<string, string> _routeGroup = new();

    // Background spacing, and the one-at-a-time gate.
    private DateTimeOffset _nextBackground;
    private readonly SemaphoreSlim _single = new(1, 1);

    private EsiGovernorLevel _level;

    /// <summary>Raised when the governor's level changes: from, to, and why — in English, for the
    /// error log. Raised outside the budget's lock, possibly on any thread.</summary>
    public event Action<EsiGovernorLevel, EsiGovernorLevel, string>? LevelChanged;

    /// <summary>For tests: a budget of its own.</summary>
    internal EsiBudget() { }

    public EsiGovernorLevel Level { get { lock (_lock) return LevelAt(DateTimeOffset.UtcNow); } }

    // ── Recording ───────────────────────────────────────────────────────────────

    /// <summary>What one response said: its status, the error window, its route's bucket.</summary>
    public void Record(string path, int status, HttpResponseHeaders headers, DateTimeOffset? now = null)
    {
        int? I(string name) => headers.TryGetValues(name, out var v) && int.TryParse(v.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
        string? S(string name) => headers.TryGetValues(name, out var v) ? v.FirstOrDefault() : null;
        Record(path, status, I("X-Esi-Error-Limit-Remain"), I("X-Esi-Error-Limit-Reset"),
               S("X-Ratelimit-Group"), S("X-Ratelimit-Limit"), I("X-Ratelimit-Remaining"), I("X-Ratelimit-Used"),
               I("Retry-After"), now);
    }

    /// <summary>The same, from values already read — the core, and what the tests drive.</summary>
    internal void Record(string path, int status, int? errorRemain, int? errorReset,
                         string? group, string? limit, int? remaining, int? used, int? retryAfter,
                         DateTimeOffset? now = null)
    {
        var at    = now ?? DateTimeOffset.UtcNow;
        var route = EsiClient.RouteTemplate(path);
        (EsiGovernorLevel From, EsiGovernorLevel To, string Why)? change = null;

        lock (_lock)
        {
            _calls.Enqueue(at);
            var failed = status >= 400;

            // A new window: the reset moved on from the one being counted.
            if (errorReset is int reset)
            {
                var resetAt = at.AddSeconds(reset);
                if (at >= _errorResetAt || resetAt > _errorResetAt.AddSeconds(2)) _oursThisWindow = 0;
                _errorResetAt = resetAt;
            }
            if (failed)
            {
                _oursThisWindow++;
                _errors.Enqueue((at, route, status));
                _errorTotals[(route, status)] = _errorTotals.GetValueOrDefault((route, status)) + 1;
            }
            if (errorRemain is int remain)
            {
                _errorRemain = remain;
                if (remain > _maxSeenRemain) _maxSeenRemain = remain;
            }
            if (status == 420) _refused420++;
            if (status == 429) _refused429++;

            if (group is not null)
            {
                _routeGroup[route] = group;
                if (!_groups.TryGetValue(group, out var g)) _groups[group] = g = new Group();
                g.Routes.Add(route);
                if (ParseLimit(limit) is { } l) { g.Tokens = l.Tokens; g.Window = l.Window; }
                if (remaining is not null) { g.Remaining = remaining; g.SeenAt = at; }
                if (used is not null) g.Used = used;
                if (status == 429)
                {
                    g.Refusals++;
                    g.BlockedUntil = at.AddSeconds((retryAfter ?? 60) + 1);
                }
            }
            Trim(at);

            var before = _level;
            _level = LevelAt(at);
            if (_level != before) change = (before, _level, DescribeLocked(at));
        }
        if (change is { } c) LevelChanged?.Invoke(c.From, c.To, c.Why);
    }

    /// <summary>"150/15m" — tokens per window; null when not that shape.</summary>
    internal static (int Tokens, TimeSpan Window)? ParseLimit(string? limit)
    {
        if (limit is null) return null;
        var m = Regex.Match(limit.Trim(), @"^(?<n>\d+)\s*/\s*(?<w>\d+)\s*(?<u>[smhd])$");
        if (!m.Success) return null;
        var n = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
        var w = int.Parse(m.Groups["w"].Value, CultureInfo.InvariantCulture);
        var window = m.Groups["u"].Value switch
        {
            "s" => TimeSpan.FromSeconds(w), "m" => TimeSpan.FromMinutes(w),
            "h" => TimeSpan.FromHours(w),   _   => TimeSpan.FromDays(w),
        };
        return n > 0 && window > TimeSpan.Zero ? (n, window) : null;
    }

    private void Trim(DateTimeOffset now)
    {
        while (_calls.Count > 0 && now - _calls.Peek() > TimeSpan.FromMinutes(1)) _calls.Dequeue();
        while (_errors.Count > 0 && now - _errors.Peek().At > TimeSpan.FromHours(1)) _errors.Dequeue();
    }

    /// <summary>The error budget's level, by ESI's own figure; Normal once its window has reset.</summary>
    private EsiGovernorLevel LevelAt(DateTimeOffset now)
    {
        if (_errorRemain is not int remain || now >= _errorResetAt) return EsiGovernorLevel.Normal;
        return remain < StoppedBelow    ? EsiGovernorLevel.Stopped
             : remain < OneAtATimeBelow ? EsiGovernorLevel.OneAtATime
             : remain < SpacedBelow     ? EsiGovernorLevel.Spaced
             : EsiGovernorLevel.Normal;
    }

    /// <summary>What the error window looks like and who spent it — for the error log line.</summary>
    private string DescribeLocked(DateTimeOffset now)
    {
        var left  = _errorRemain is int r && now < _errorResetAt ? r : _maxSeenRemain;
        var spent = Math.Max(0, _maxSeenRemain - left);
        var others = Math.Max(0, spent - _oursThisWindow);
        var top = _errors.Where(e => now - e.At <= TimeSpan.FromMinutes(1))
            .GroupBy(e => (e.Route, e.Status)).OrderByDescending(g => g.Count()).Take(5)
            .Select(g => $"{g.Key.Route} {g.Key.Status} ×{g.Count()}");
        var resetIn = Math.Max(0, (int)Math.Ceiling((_errorResetAt - now).TotalSeconds));
        return $"ESI error budget {left}/{_maxSeenRemain}, resets in {resetIn} s; this app's errors this window: {_oursThisWindow}"
             + (others > 0 ? $", from elsewhere on this connection: {others}" : "")
             + $"; in the last minute by route: {string.Join(", ", top.DefaultIfEmpty("none"))}";
    }

    // ── Governing ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Waits for a background call's turn: spaced, one at a time, or until the error window resets,
    /// as the budget calls for — and at its bucket's refill rate when that is low. Dispose what it
    /// returns once the call has its answer: it is the one-at-a-time gate when that is held.
    /// </summary>
    public async Task<IDisposable?> WaitTurnAsync(string path, CancellationToken ct)
    {
        var route = EsiClient.RouteTemplate(path);
        while (true)
        {
            TimeSpan wait;
            EsiGovernorLevel level;
            lock (_lock)
            {
                var now = DateTimeOffset.UtcNow;
                level = LevelAt(now);
                wait = level == EsiGovernorLevel.Stopped ? _errorResetAt - now + TimeSpan.FromSeconds(1) : TimeSpan.Zero;
            }
            if (wait <= TimeSpan.Zero) break;
            await Task.Delay(wait, ct);
        }

        IDisposable? gate = null;
        if (Level >= EsiGovernorLevel.OneAtATime)
        {
            await _single.WaitAsync(ct);
            gate = new Release(_single);
        }

        TimeSpan delay;
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            var level = LevelAt(now);
            var gap = level switch
            {
                EsiGovernorLevel.OneAtATime => OneAtATimeGap,
                EsiGovernorLevel.Spaced     => SpacedGap,
                _                           => TimeSpan.Zero,
            };
            var at = now;
            if (gap > TimeSpan.Zero)
            {
                if (_nextBackground > at) at = _nextBackground;
                _nextBackground = at + gap;
            }

            // The route's bucket, when it is low: one call per token's refill time.
            if (_routeGroup.TryGetValue(route, out var name) && _groups.TryGetValue(name, out var g)
                && PacedLocked(g, now) && g.Tokens is int t && g.Window is { } w)
            {
                // A call's cost in tokens, as ESI said for the last one; the refill of that many.
                var each = TimeSpan.FromTicks(w.Ticks * Math.Max(1, g.Used ?? 1) / t);
                if (g.NextPaced > at) at = g.NextPaced;
                g.NextPaced = at + each;
            }
            delay = at - now;
        }
        if (delay > TimeSpan.Zero)
        {
            try { await Task.Delay(delay, ct); }
            catch { gate?.Dispose(); throw; }
        }
        return gate;
    }

    /// <summary>The group is under a fifth of its allowance, as ESI last said within a window.</summary>
    private static bool PacedLocked(Group g, DateTimeOffset now)
    {
        if (g.Tokens is not int t || g.Window is not { } w || g.Remaining is not int r || w <= TimeSpan.Zero) return false;
        return now - g.SeenAt < w && r < t * PaceBelowShare;
    }

    private sealed class Release(SemaphoreSlim gate) : IDisposable
    {
        private int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) gate.Release(); }
    }

    // ── Reading ─────────────────────────────────────────────────────────────────

    public EsiBudgetSnapshot Snapshot(DateTimeOffset? at = null)
    {
        var now = at ?? DateTimeOffset.UtcNow;
        lock (_lock)
        {
            Trim(now);
            var inWindow = _errorRemain is not null && now < _errorResetAt;
            var others = inWindow ? Math.Max(0, _maxSeenRemain - _errorRemain!.Value - _oursThisWindow) : (int?)null;
            var minute = _errors.Where(e => now - e.At <= TimeSpan.FromMinutes(1)).ToList();
            var errors = _errorTotals
                .Select(kv =>
                {
                    var hour = _errors.Where(e => e.Route == kv.Key.Route && e.Status == kv.Key.Status).ToList();
                    return new EsiRouteErrors(kv.Key.Route, kv.Key.Status,
                        minute.Count(e => e.Route == kv.Key.Route && e.Status == kv.Key.Status), hour.Count, kv.Value,
                        hour.Count > 0 ? hour[^1].At : DateTimeOffset.MinValue);
                })
                .OrderByDescending(e => e.LastHour).ThenByDescending(e => e.SinceStart)
                .ToList();
            var groups = _groups.Select(kv => new EsiGroupState(kv.Key, kv.Value.Tokens, kv.Value.Window, kv.Value.Remaining,
                    kv.Value.Used, kv.Value.SeenAt, kv.Value.Routes.Order().ToList(), kv.Value.Refusals,
                    kv.Value.BlockedUntil is { } b && b > now ? b : null, PacedLocked(kv.Value, now)))
                .OrderBy(g => g.Group, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new EsiBudgetSnapshot(
                inWindow ? _errorRemain : null, _maxSeenRemain, inWindow ? _errorResetAt : null,
                inWindow ? _oursThisWindow : 0, others,
                _calls.Count, minute.Count, _errors.Count, _refused420, _refused429,
                LevelAt(now), groups, errors);
        }
    }
}
