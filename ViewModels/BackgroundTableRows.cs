using Avalonia.Media;
using EveConsole.Services;
using ReactiveUI;

namespace EveConsole.ViewModels;

// The rows of the Background Processes tables. Each is updated in place as the monitor
// refreshes, so a grid keeps its scroll and sort rather than being rebuilt every ten seconds.

/// <summary>One part of a process — a sweep, a stage of a pipeline — with what it is doing.</summary>
public sealed class StageRowVm(string stage) : ReactiveObject
{
    public string Stage { get; } = stage;

    private string _state = "";
    public string State { get => _state; private set => this.RaiseAndSetIfChanged(ref _state, value); }

    private string _detail = "";
    public string Detail { get => _detail; private set => this.RaiseAndSetIfChanged(ref _detail, value); }

    private string _lastRun = "";
    public string LastRun { get => _lastRun; private set => this.RaiseAndSetIfChanged(ref _lastRun, value); }

    private string _nextRun = "";
    public string NextRun { get => _nextRun; private set => this.RaiseAndSetIfChanged(ref _nextRun, value); }

    private bool _running;
    public bool Running
    {
        get => _running;
        private set { this.RaiseAndSetIfChanged(ref _running, value); this.RaisePropertyChanged(nameof(StateColor)); }
    }

    /// <summary>Busy in green, at rest in the muted text colour.</summary>
    public IBrush StateColor => Running ? Palette.Good : Palette.TextMuted;

    public void Set(string state, bool running, string detail, string lastRun = "", string nextRun = "")
    {
        State = state; Running = running; Detail = detail; LastRun = lastRun; NextRun = nextRun;
    }
}

/// <summary>One source of contracts whose items are pulled: public listings, or the characters'
/// and corporations' own.</summary>
public sealed class ContractSourceRowVm(string source) : ReactiveObject
{
    public string Source { get; } = source;

    private string _total = "";
    public string TotalText { get => _total; private set => this.RaiseAndSetIfChanged(ref _total, value); }

    private string _pulled = "";
    public string PulledText { get => _pulled; private set => this.RaiseAndSetIfChanged(ref _pulled, value); }

    private string _queued = "";
    public string QueuedText { get => _queued; private set => this.RaiseAndSetIfChanged(ref _queued, value); }

    private string _note = "";
    public string Note { get => _note; private set => this.RaiseAndSetIfChanged(ref _note, value); }

    public void Set(string total, string pulled, string queued, string note = "")
    {
        TotalText = total; PulledText = pulled; QueuedText = queued; Note = note;
    }
}

/// <summary>One NPC corporation the LP store sweep has looked at.</summary>
public sealed class LpStoreCorpRowVm(int corporationId, string name) : ReactiveObject
{
    public int    CorporationId { get; } = corporationId;
    public string Name          { get; } = name;

    private string _store = "";
    public string StoreText { get => _store; private set => this.RaiseAndSetIfChanged(ref _store, value); }

    private int _offers;
    public int Offers { get => _offers; private set { this.RaiseAndSetIfChanged(ref _offers, value); this.RaisePropertyChanged(nameof(OffersText)); } }
    public string OffersText => Offers > 0 ? Offers.ToString("N0") : "";

    private DateTime? _checkedAt;
    public DateTime? CheckedAt { get => _checkedAt; private set { this.RaiseAndSetIfChanged(ref _checkedAt, value); this.RaisePropertyChanged(nameof(CheckedText)); } }
    public string CheckedText => CheckedAt is { } t ? DateTime.SpecifyKind(t, DateTimeKind.Utc).ToLocalTime().ToString("d MMM HH:mm") : "";

    public void Set(bool hasStore, int offers, DateTime? checkedAt)
    {
        StoreText = hasStore ? "yes" : "none"; Offers = offers; CheckedAt = checkedAt;
    }
}

/// <summary>One alarm as the loop sees it: its schedule, and what it last did.</summary>
public sealed class AlarmMonitorRowVm(long id, string name) : ReactiveObject
{
    public long   Id   { get; } = id;
    public string Name { get; } = name;

    private string _condition = "";
    public string Condition { get => _condition; private set => this.RaiseAndSetIfChanged(ref _condition, value); }

    private string _enabled = "";
    public string EnabledText { get => _enabled; private set => this.RaiseAndSetIfChanged(ref _enabled, value); }

    private string _every = "";
    public string EveryText { get => _every; private set => this.RaiseAndSetIfChanged(ref _every, value); }

    private DateTimeOffset? _lastChecked;
    public DateTimeOffset? LastChecked { get => _lastChecked; private set { this.RaiseAndSetIfChanged(ref _lastChecked, value); this.RaisePropertyChanged(nameof(LastCheckedText)); } }
    public string LastCheckedText => LastChecked is { } t ? t.ToLocalTime().ToString("d MMM HH:mm:ss") : "";

    private DateTimeOffset? _lastFired;
    public DateTimeOffset? LastFired { get => _lastFired; private set { this.RaiseAndSetIfChanged(ref _lastFired, value); this.RaisePropertyChanged(nameof(LastFiredText)); } }
    public string LastFiredText => LastFired is { } t ? t.ToLocalTime().ToString("d MMM HH:mm:ss") : "never";

    private int _fireCount;
    public int FireCount { get => _fireCount; private set => this.RaiseAndSetIfChanged(ref _fireCount, value); }

    private string _error = "";
    public string ErrorText { get => _error; private set => this.RaiseAndSetIfChanged(ref _error, value); }

    public void Set(string condition, bool enabled, int pollSeconds, DateTimeOffset? lastChecked, DateTimeOffset? lastFired, int fireCount, string? error)
    {
        Condition   = condition;
        EnabledText = enabled ? "yes" : "no";
        EveryText   = pollSeconds >= 3600 && pollSeconds % 3600 == 0 ? $"{pollSeconds / 3600} h"
                    : pollSeconds >= 60   && pollSeconds % 60   == 0 ? $"{pollSeconds / 60} min"
                    : $"{pollSeconds} s";
        LastChecked = lastChecked;
        LastFired   = lastFired;
        FireCount   = fireCount;
        ErrorText   = error ?? "";
    }
}
