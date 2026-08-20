using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Avalonia.Threading;

namespace EveConsole.Services;

public record ActivityEntry(
    DateTimeOffset Timestamp,
    string         OwnerName,
    string         Endpoint,
    bool           Success,
    int            HttpStatus,
    string?        ErrorMessage)
{
    public string StatusDisplay  => Success ? "OK" : (ErrorMessage ?? $"HTTP {HttpStatus}");
    public string StatusColor    => Success ? "#4caf81" : "#e85555";
    public string StatusCodeText => HttpStatus > 0 ? HttpStatus.ToString() : "—";
}

public record InFlightCall(Guid Id, string OwnerName, string Endpoint, DateTimeOffset StartedAt);

public sealed class ActivityCallHandle : IDisposable
{
    private readonly ApiActivityLog _log;
    private readonly Guid           _id;
    private bool _done;

    internal ActivityCallHandle(ApiActivityLog log, Guid id) { _log = log; _id = id; }

    public void Complete(bool success, int statusCode, string? error = null)
    {
        _done = true;
        _log.CompleteCall(_id, success, statusCode, error);
    }

    public void Dispose()
    {
        if (!_done) _log.CancelCall(_id);
    }
}

/// <summary>
/// The record of API calls behind the API Activity window.
///
/// <para>⚠️ Every mutation here is BATCHED onto the UI thread rather than posted per call, and
/// the reason is not tidiness. This log is written from every API call the app makes; a polling
/// cycle produces hundreds in a burst. Posting each one separately meant hundreds of dispatcher
/// operations, each inserting at the front of a ten-thousand-item ObservableCollection — an O(n)
/// array shift plus a CollectionChanged at index 0 that re-indexes the bound view, per call. That
/// froze the UI thread for seconds at a time on every cycle, whether or not the window was even
/// open. Measured stalls were 1.4-6 s.</para>
///
/// <para>So: callers queue, and at most one flush every <see cref="FlushMs"/> applies the whole
/// backlog in a single pass. The collections stay ObservableCollection because the window binds
/// to them directly.</para>
/// </summary>
public class ApiActivityLog
{
    /// <summary>Kept far below the old ten thousand. The window shows recent activity; the tail
    /// was never read, and every entry beyond the visible page cost time on each insert.</summary>
    private const int MaxEntries = 1_000;

    /// <summary>Coalescing window. Long enough that a burst collapses into one UI pass, short
    /// enough that the window still looks live.</summary>
    private const int FlushMs = 250;

    public ObservableCollection<ActivityEntry> Entries       { get; } = [];
    public ObservableCollection<InFlightCall>  InFlightCalls { get; } = [];

    private readonly ConcurrentDictionary<Guid, InFlightCall> _inFlight = new();

    private readonly ConcurrentQueue<ActivityEntry> _newEntries    = new();
    private readonly ConcurrentQueue<InFlightCall>  _startedCalls  = new();
    private readonly ConcurrentQueue<InFlightCall>  _finishedCalls = new();

    /// <summary>0 = no flush pending. Set with Interlocked so a burst schedules exactly one.</summary>
    private int _flushScheduled;

    public ActivityCallHandle StartCall(string ownerName, string endpoint)
    {
        var id   = Guid.NewGuid();
        var call = new InFlightCall(id, ownerName, endpoint, DateTimeOffset.UtcNow);
        _inFlight[id] = call;

        _startedCalls.Enqueue(call);
        ScheduleFlush();
        return new ActivityCallHandle(this, id);
    }

    internal void CompleteCall(Guid id, bool success, int statusCode, string? error)
    {
        if (!_inFlight.TryRemove(id, out var call)) return;

        _finishedCalls.Enqueue(call);
        _newEntries.Enqueue(new ActivityEntry(DateTimeOffset.UtcNow, call.OwnerName, call.Endpoint,
            success, statusCode, error));
        ScheduleFlush();
    }

    internal void CancelCall(Guid id)
    {
        if (!_inFlight.TryRemove(id, out var call)) return;

        _finishedCalls.Enqueue(call);
        ScheduleFlush();
    }

    public void Add(ActivityEntry entry)
    {
        _newEntries.Enqueue(entry);
        ScheduleFlush();
    }

    private void ScheduleFlush()
    {
        if (Interlocked.CompareExchange(ref _flushScheduled, 1, 0) != 0) return;

        // Background priority: real interaction and rendering go first. This is a diagnostic
        // readout and must never be what makes the window feel slow.
        DispatcherTimer.RunOnce(Flush, TimeSpan.FromMilliseconds(FlushMs),
                                DispatcherPriority.Background);
    }

    private void Flush()
    {
        Interlocked.Exchange(ref _flushScheduled, 0);

        while (_startedCalls.TryDequeue(out var started))
            InFlightCalls.Add(started);

        // In-flight is a handful of entries, so IndexOf is not worth engineering around —
        // unlike Entries, it never grows.
        while (_finishedCalls.TryDequeue(out var finished))
        {
            var idx = InFlightCalls.IndexOf(finished);
            if (idx >= 0) InFlightCalls.RemoveAt(idx);
        }

        // The queue yields oldest first; inserting each at the front in that order leaves the
        // newest at index 0, which is the order the window shows. Each shift is bounded by
        // MaxEntries, so a burst costs a bounded number of moves rather than a growing one.
        var added = 0;
        while (_newEntries.TryDequeue(out var entry))
        {
            Entries.Insert(0, entry);
            added++;
        }

        if (added == 0) return;

        while (Entries.Count > MaxEntries)
            Entries.RemoveAt(Entries.Count - 1);
    }
}
