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

public class ApiActivityLog
{
    private const int MaxEntries = 10_000;

    public ObservableCollection<ActivityEntry> Entries       { get; } = [];
    public ObservableCollection<InFlightCall>  InFlightCalls { get; } = [];

    private readonly ConcurrentDictionary<Guid, InFlightCall> _inFlight = new();

    public ActivityCallHandle StartCall(string ownerName, string endpoint)
    {
        var id   = Guid.NewGuid();
        var call = new InFlightCall(id, ownerName, endpoint, DateTimeOffset.UtcNow);
        _inFlight[id] = call;
        Dispatcher.UIThread.Post(() => InFlightCalls.Add(call));
        return new ActivityCallHandle(this, id);
    }

    internal void CompleteCall(Guid id, bool success, int statusCode, string? error)
    {
        if (!_inFlight.TryRemove(id, out var call)) return;
        var entry = new ActivityEntry(DateTimeOffset.UtcNow, call.OwnerName, call.Endpoint,
            success, statusCode, error);
        Dispatcher.UIThread.Post(() =>
        {
            var idx = InFlightCalls.IndexOf(call);
            if (idx >= 0) InFlightCalls.RemoveAt(idx);
            Entries.Insert(0, entry);
            while (Entries.Count > MaxEntries) Entries.RemoveAt(MaxEntries);
        });
    }

    internal void CancelCall(Guid id)
    {
        if (!_inFlight.TryRemove(id, out var call)) return;
        Dispatcher.UIThread.Post(() =>
        {
            var idx = InFlightCalls.IndexOf(call);
            if (idx >= 0) InFlightCalls.RemoveAt(idx);
        });
    }

    public void Add(ActivityEntry entry) =>
        Dispatcher.UIThread.Post(() =>
        {
            Entries.Insert(0, entry);
            while (Entries.Count > MaxEntries) Entries.RemoveAt(MaxEntries);
        });
}
