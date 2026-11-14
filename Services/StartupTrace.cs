using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia.Threading;

namespace EveConsole.Services;

/// <summary>
/// The first minutes of a run, written to a file of their own: when each startup step happened,
/// how late the UI thread was, and what was being allocated, by type and by thread.
///
/// <para>Built for a start that took minutes with the UI frozen in garbage collection: the stall
/// monitor said tens of gigabytes went by and the UI thread ran nothing named for most of it, which
/// says THAT something allocates without saying WHAT. The runtime's own allocation sampling (an
/// event every 100 KB or so, naming the type and the thread) answers that from inside the process,
/// with no profiler to install. Off after <see cref="Duration"/>, and only while
/// <see cref="PerfDiagnostics.StartupTrace"/> is on.</para>
///
/// <para>The file is in the system's temp folder, named EveConsole-startup- and the time the app
/// started.</para>
/// </summary>
public static class StartupTrace
{
    /// <summary>How long after starting it keeps recording.</summary>
    public static TimeSpan Duration { get; set; } = TimeSpan.FromMinutes(4);

    private static readonly TimeSpan Window = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Ping = TimeSpan.FromMilliseconds(200);
    private const int ReportLagAboveMs = 300;
    private const int TopPerWindow = 10;

    private static readonly object Gate = new();
    private static StreamWriter? _out;
    private static AllocationListener? _listener;
    private static DatabaseWatch? _database;
    private static Timer? _ping, _report;
    private static long _started;
    private static ulong _uiThread;
    private static volatile bool _pinging;
    private static int _worstLagInWindow;

    // The window's starting figures, for its differences.
    private static long _allocAtWindow;
    private static TimeSpan _pauseAtWindow;
    private static int _gen0AtWindow, _gen2AtWindow;

    /// <summary>The file being written, once started.</summary>
    public static string? FilePath { get; private set; }

    /// <summary>Starts recording. On the UI thread, which it notes as the one to name.</summary>
    public static void Begin()
    {
        if (!PerfDiagnostics.StartupTrace || _out is not null) return;
        try
        {
            _started = Stopwatch.GetTimestamp();
            _uiThread = CurrentOsThread();
            FilePath = Path.Combine(Path.GetTempPath(), $"EveConsole-startup-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            _out = new StreamWriter(FilePath, append: false) { AutoFlush = true };
            Line($"EVE Console {AppVersion.Display} startup trace, {DateTime.Now:yyyy-MM-dd HH:mm:ss}, process up {Uptime():N1}s before this. " +
                 $"Times are seconds since the trace began. UI thread = OS thread {_uiThread}.");
            Snapshot();
            _listener = new AllocationListener();
            _database = new DatabaseWatch();
            _ping = new Timer(_ => PingUi(), null, Ping, Ping);
            _report = new Timer(_ => Report(), null, Window, Window);
        }
        catch (Exception e)
        {
            _out = null;
            Debug.WriteLine($"[StartupTrace] could not start: {e.Message}");
        }
    }

    /// <summary>A step of the start, timed from when the trace began.</summary>
    public static void Mark(string what)
    {
        if (_out is null) return;
        Line($"mark  {what}");
    }

    private static string? _stepName;
    private static long _stepAt;

    /// <summary>
    /// Ends the step before and starts <paramref name="name"/>, for a long run of statements: the step
    /// before is written with how long it took, if that was long enough to matter.
    /// </summary>
    public static void Step(string name)
    {
        if (_out is null) return;
        var now = Stopwatch.GetTimestamp();
        if (_stepName is { } before)
        {
            var ms = Stopwatch.GetElapsedTime(_stepAt, now).TotalMilliseconds;
            if (ms >= 20) Line($"step  {before}: {ms:N0} ms on {ThreadName(CurrentOsThread())}");
        }
        _stepName = name;
        _stepAt = now;
    }

    /// <summary>
    /// Makes <paramref name="root"/> and everything it takes, deepest first, each timed on its own:
    /// what a service takes is already made by the time it is, so its time is its own constructor's.
    /// For singletons only in effect, as making them early changes nothing; anything else is made
    /// and thrown away.
    /// </summary>
    public static void TimeServices(IServiceProvider services, Type root)
    {
        if (_out is null) return;
        var seen = TimedServices;
        void Visit(Type type, int depth)
        {
            if (depth > 12 || !seen.Add(type) || type.IsPrimitive || type == typeof(string)
                || type.IsGenericType || typeof(IServiceProvider).IsAssignableFrom(type)
                || typeof(Microsoft.EntityFrameworkCore.DbContext).IsAssignableFrom(type)) return;
            if (type.IsClass && !type.IsAbstract)
                foreach (var p in type.GetConstructors().MaxBy(c => c.GetParameters().Length)?.GetParameters() ?? [])
                    Visit(p.ParameterType, depth + 1);
            Timed("service " + type.Name, () =>
            {
                try { services.GetService(type); }
                catch (Exception) { /* not registered as itself, or not makeable alone: made later as it would be */ }
            });
        }
        Visit(root, 0);
    }

    private static readonly HashSet<Type> TimedServices = [];

    /// <summary>The splash's progress, with each step it names marked as it's reported.</summary>
    public static IProgress<(double, string)> Marking(IProgress<(double, string)> inner) => new MarkingProgress(inner);

    private sealed class MarkingProgress(IProgress<(double, string)> inner) : IProgress<(double, string)>
    {
        public void Report((double, string) value)
        {
            Mark($"splash {value.Item1:F0}% {value.Item2}");
            inner.Report(value);
        }
    }

    /// <summary>Runs a step and writes how long it took, when that's long enough to matter.</summary>
    public static void Timed(string what, Action step)
    {
        if (_out is null) { step(); return; }
        var at = Stopwatch.GetTimestamp();
        try { step(); }
        finally
        {
            var ms = Stopwatch.GetElapsedTime(at).TotalMilliseconds;
            if (ms >= 5) Line($"step  {what}: {ms:N0} ms on {ThreadName(CurrentOsThread())}");
        }
    }

    private static void PingUi()
    {
        if (_pinging || _out is null) return;
        _pinging = true;
        var sent = Stopwatch.GetTimestamp();
        Dispatcher.UIThread.Post(() =>
        {
            _pinging = false;
            var lag = (int)Stopwatch.GetElapsedTime(sent).TotalMilliseconds;
            if (lag > _worstLagInWindow) _worstLagInWindow = lag;
            if (lag >= ReportLagAboveMs)
                Line($"lag   UI thread {lag:N0} ms late. Last named UI work: {UiWork.Last ?? "(none)"}" +
                     (UiWork.MsSinceLast is { } ago ? $", {ago:N0} ms ago" : ""));
        }, DispatcherPriority.Background);
    }

    /// <summary>Every couple of seconds: what was allocated, and by which thread, and what that cost.</summary>
    private static void Report()
    {
        if (_out is null) return;
        if (Stopwatch.GetElapsedTime(_started) > Duration)
        {
            Stop();
            return;
        }

        var allocMb = (GC.GetTotalAllocatedBytes() - _allocAtWindow) / 1048576d;
        var pauseMs = (GC.GetTotalPauseDuration() - _pauseAtWindow).TotalMilliseconds;
        var gen0 = GC.CollectionCount(0) - _gen0AtWindow;
        var gen2 = GC.CollectionCount(2) - _gen2AtWindow;
        ThreadPool.GetAvailableThreads(out var free, out _);
        var worst = Interlocked.Exchange(ref _worstLagInWindow, 0);
        Snapshot();

        var top = _listener?.TakeTop(TopPerWindow) ?? [];
        var database = _database?.Take().ToList() ?? [];
        foreach (var line in database) Line(line);
        // Quiet windows say nothing, so the file is the interesting part of the start.
        if (allocMb < 50 && worst < ReportLagAboveMs && pauseMs < 100) return;

        Line($"2s    allocated {allocMb:N0} MB, GC paused {pauseMs:N0} ms ({gen0} gen0, {gen2} gen2), " +
             $"heap {GC.GetTotalMemory(false) / 1048576d:N0} MB, UI worst lag {worst:N0} ms, " +
             $"pool {free} free / {ThreadPool.PendingWorkItemCount} queued / {ThreadPool.ThreadCount} threads");
        foreach (var (type, thread, bytes) in top)
            Line($"        {bytes / 1048576d,8:N1} MB  {type}  on {ThreadName(thread)}");
    }

    private static void Snapshot()
    {
        _allocAtWindow = GC.GetTotalAllocatedBytes();
        _pauseAtWindow = GC.GetTotalPauseDuration();
        _gen0AtWindow = GC.CollectionCount(0);
        _gen2AtWindow = GC.CollectionCount(2);
    }

    private static void Stop()
    {
        Line("done  trace ends");
        _ping?.Dispose();
        _report?.Dispose();
        _listener?.Dispose();
        _database?.Dispose();
        lock (Gate)
        {
            _out?.Dispose();
            _out = null;
        }
    }

    private static string ThreadName(ulong osThread) => osThread == _uiThread ? "UI thread" : $"thread {osThread}";

    private static void Line(string text)
    {
        var at = Stopwatch.GetElapsedTime(_started).TotalSeconds;
        lock (Gate)
        {
            try { _out?.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{at,8:F3}  {text}")); }
            catch (Exception) { /* a diagnostic that can't write stops being one, not a fault */ }
        }
    }

    private static double Uptime()
    {
        try { return (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalSeconds; }
        catch { return 0; }
    }

    private static ulong CurrentOsThread() => OperatingSystem.IsWindows() ? GetCurrentThreadId() : (ulong)Environment.CurrentManagedThreadId;

    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    /// <summary>
    /// What the database was asked, per window: each statement's count and time (from Npgsql's own
    /// tracing, so raw commands are seen as well as Entity Framework's), and how many entities
    /// Entity Framework was tracking each time it looked for changes or saved. A loop of thousands of
    /// tiny queries, or saves into an ever-growing tracker, show here by name and table.
    /// </summary>
    private sealed class DatabaseWatch : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly ActivityListener _commands;
        private readonly IDisposable _listeners;
        private readonly List<IDisposable> _subscriptions = [];
        private readonly ConcurrentDictionary<string, (int Count, double Ms, ulong Thread)> _statements = new();
        private readonly ConcurrentDictionary<string, (int Count, int MostTracked, int Saved)> _tracking = new();

        public DatabaseWatch()
        {
            _commands = new ActivityListener
            {
                ShouldListenTo = source => source.Name == "Npgsql",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = Command,
            };
            ActivitySource.AddActivityListener(_commands);
            _listeners = DiagnosticListener.AllListeners.Subscribe(this);
        }

        private void Command(Activity a)
        {
            var text = a.GetTagItem("db.query.text") as string ?? a.GetTagItem("db.statement") as string ?? a.DisplayName;
            var key = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (key.Length > 140) key = key[..140] + "…";
            var thread = CurrentOsThread();
            _statements.AddOrUpdate(key, (1, a.Duration.TotalMilliseconds, thread),
                (_, had) => (had.Count + 1, had.Ms + a.Duration.TotalMilliseconds, thread));
        }

        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name == "Microsoft.EntityFrameworkCore")
                lock (_subscriptions)
                    _subscriptions.Add(listener.Subscribe(this, name =>
                        name.EndsWith("DetectChangesStarting", StringComparison.Ordinal)
                        || name.EndsWith("SaveChangesCompleted", StringComparison.Ordinal)));
        }

        public void OnNext(KeyValuePair<string, object?> e)
        {
            if (e.Value is not Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData { Context: { } context }) return;
            // ⚠️ The state manager, not ChangeTracker.Entries(): that detects changes itself, and from
            // inside "detect changes starting" it would call back here for ever.
#pragma warning disable EF1001 // internal API, for a diagnostic only
            var state = Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions
                .GetService<Microsoft.EntityFrameworkCore.ChangeTracking.Internal.IStateManager>(context);
            var tracked = state.Count;
            var kinds = string.Join("+", state.Entries.Take(500).Select(x => x.EntityType.ClrType.Name).Distinct().Take(3));
#pragma warning restore EF1001
            var saved = e.Value is Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesCompletedEventData s ? s.EntitiesSavedCount : 0;
            var key = (saved > 0 ? "save " : "detect changes ") + (kinds.Length > 0 ? kinds : "(nothing tracked)");
            _tracking.AddOrUpdate(key, (1, tracked, saved),
                (_, had) => (had.Count + 1, Math.Max(had.MostTracked, tracked), had.Saved + saved));
        }

        public void OnCompleted() { }
        public void OnError(Exception error) { }

        /// <summary>The window's busiest statements and trackers, and starts the next.</summary>
        public IEnumerable<string> Take()
        {
            var statements = _statements.Keys.Select(k => _statements.TryRemove(k, out var v) ? (k, v) : default)
                .Where(x => x.k is not null).ToList();
            foreach (var (text, v) in statements.OrderByDescending(x => x.v.Count).Take(6).Where(x => x.v.Count >= 25 || x.v.Ms >= 1000))
                yield return $"sql   {v.Count,6:N0} × {v.Ms,8:N0} ms  on {ThreadName(v.Thread)}  {text}";
            foreach (var (text, v) in statements.Where(x => x.v.Count < 25 && x.v.Ms >= 1000).OrderByDescending(x => x.v.Ms).Take(3))
                yield return $"sql   {v.Count,6:N0} × {v.Ms,8:N0} ms  on {ThreadName(v.Thread)}  {text}";
            var tracking = _tracking.Keys.Select(k => _tracking.TryRemove(k, out var v) ? (k, v) : default)
                .Where(x => x.k is not null).ToList();
            foreach (var (what, v) in tracking.OrderByDescending(x => (long)x.v.Count * x.v.MostTracked).Take(4).Where(x => x.v.MostTracked >= 500 || x.v.Count >= 100))
                yield return $"ef    {v.Count,6:N0} × up to {v.MostTracked:N0} tracked, {v.Saved:N0} saved  {what}";
        }

        public void Dispose()
        {
            _commands.Dispose();
            _listeners.Dispose();
            lock (_subscriptions) foreach (var s in _subscriptions) s.Dispose();
        }
    }

    /// <summary>
    /// The runtime's allocation sampling: about every 100 KB a thread allocates, an event names the
    /// type it was allocating then and the thread. Summed per window, by type and thread.
    /// </summary>
    private sealed class AllocationListener : EventListener
    {
        private const EventKeywords GcKeyword = (EventKeywords)0x1;
        private readonly ConcurrentDictionary<(string Type, ulong Thread), long> _bytes = new();

        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "Microsoft-Windows-DotNETRuntime")
                EnableEvents(source, EventLevel.Verbose, GcKeyword);
        }

        protected override void OnEventWritten(EventWrittenEventArgs e)
        {
            if (e.EventName is null || !e.EventName.StartsWith("GCAllocationTick", StringComparison.Ordinal) || e.Payload is null) return;
            var names = e.PayloadNames;
            if (names is null) return;
            string? type = null;
            long amount = 0;
            for (var i = 0; i < names.Count; i++)
            {
                switch (names[i])
                {
                    case "TypeName": type = e.Payload[i] as string; break;
                    case "AllocationAmount64": amount = Convert.ToInt64(e.Payload[i], CultureInfo.InvariantCulture); break;
                    case "AllocationAmount" when amount == 0: amount = Convert.ToInt64(e.Payload[i], CultureInfo.InvariantCulture); break;
                }
            }
            _bytes.AddOrUpdate((type ?? "?", (ulong)e.OSThreadId), amount, (_, had) => had + amount);
        }

        /// <summary>The window's biggest, and starts the next.</summary>
        public List<(string Type, ulong Thread, long Bytes)> TakeTop(int count)
        {
            var taken = new List<(string, ulong, long)>();
            foreach (var key in _bytes.Keys)
                if (_bytes.TryRemove(key, out var bytes)) taken.Add((key.Type, key.Thread, bytes));
            return [.. taken.OrderByDescending(t => t.Item3).Take(count)];
        }
    }
}
