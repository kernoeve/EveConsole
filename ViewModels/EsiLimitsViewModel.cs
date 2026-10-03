using Avalonia.Media;
using EveConsole.Api;
using EveConsole.Localization;
using EveConsole.Services;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>One rate-limit group on the ESI limits tab.</summary>
public sealed class EsiGroupRowVm
{
    public string Group      { get; init; } = "";
    public string LimitText  { get; init; } = "";
    public string LeftText   { get; init; } = "";
    /// <summary>How full the bucket is, 0–100, for the bar.</summary>
    public double LeftPercent { get; init; }
    public IBrush LeftBrush  { get; init; } = Palette.Good;
    public string StateText  { get; init; } = "";
    /// <summary>Whose allowance the Left figure is — the lowest of the group's.</summary>
    public string LowestText { get; init; } = "";
    /// <summary>Of that bucket, what this app spent in the window, and what went elsewhere.</summary>
    public string SpentHereText      { get; init; } = "";
    public string SpentElsewhereText { get; init; } = "";
    public string RoutesText { get; init; } = "";
    public int    Refusals   { get; init; }
}

/// <summary>Errors one route drew, on the ESI limits tab.</summary>
public sealed class EsiErrorRowVm
{
    public string Route      { get; init; } = "";
    public int    Status     { get; init; }
    public int    LastMinute { get; init; }
    public int    LastHour   { get; init; }
    public long   SinceStart { get; init; }
    public string LastAtText { get; init; } = "";
}

/// <summary>
/// The Background Processes tool's ESI limits tab: the error budget this connection has left, the
/// rate-limit groups the app has met, its errors by route, and what the governor is doing about
/// it — all from <see cref="EsiBudget.Shared"/>, refreshed on the tool's tick.
/// </summary>
public sealed class EsiLimitsViewModel : ReactiveObject
{
    public BulkObservableCollection<EsiGroupRowVm> Groups { get; } = [];
    public BulkObservableCollection<EsiErrorRowVm> Errors { get; } = [];

    private string _budgetText = "";
    public string BudgetText { get => _budgetText; private set => this.RaiseAndSetIfChanged(ref _budgetText, value); }

    private double _budgetPercent = 100;
    public double BudgetPercent { get => _budgetPercent; private set => this.RaiseAndSetIfChanged(ref _budgetPercent, value); }

    private IBrush _budgetBrush = Palette.Good;
    public IBrush BudgetBrush { get => _budgetBrush; private set => this.RaiseAndSetIfChanged(ref _budgetBrush, value); }

    private string _windowText = "";
    public string WindowText { get => _windowText; private set => this.RaiseAndSetIfChanged(ref _windowText, value); }

    private string _callsText = "";
    public string CallsText { get => _callsText; private set => this.RaiseAndSetIfChanged(ref _callsText, value); }

    private string _governorText = "";
    public string GovernorText { get => _governorText; private set => this.RaiseAndSetIfChanged(ref _governorText, value); }

    private IBrush _governorBrush = Palette.Good;
    public IBrush GovernorBrush { get => _governorBrush; private set => this.RaiseAndSetIfChanged(ref _governorBrush, value); }

    private bool _noGroups = true;
    public bool NoGroups { get => _noGroups; private set => this.RaiseAndSetIfChanged(ref _noGroups, value); }

    private bool _noErrors = true;
    public bool NoErrors { get => _noErrors; private set => this.RaiseAndSetIfChanged(ref _noErrors, value); }

    private string _lastKey = "";

    /// <summary>Character names by id, for whose bucket is lowest. Set by the tool when it loads
    /// its characters; an id it does not know is shown as one.</summary>
    public IReadOnlyDictionary<long, string> CharacterNames { get; set; } = new Dictionary<long, string>();

    public void Refresh(EsiBudget? budget = null)
    {
        var now = DateTimeOffset.UtcNow;
        var s = (budget ?? EsiBudget.Shared).Snapshot(now);
        var limit = s.ErrorLimit ?? 100;

        if (s.ErrorRemain is int left && s.ErrorResetAt is { } reset)
        {
            BudgetText    = string.Format(DataText.EsiErrorBudgetText, left, limit, Math.Max(0, (int)Math.Ceiling((reset - now).TotalSeconds)));
            BudgetPercent = limit > 0 ? 100.0 * left / limit : 0;
        }
        else
        {
            BudgetText    = DataText.EsiErrorBudgetFull;
            BudgetPercent = 100;
        }
        BudgetBrush = BudgetPercent < EsiBudget.OneAtATimeBelow ? Palette.Bad
                    : BudgetPercent < EsiBudget.SpacedBelow     ? Palette.Warn
                    : Palette.Good;
        WindowText = s.OthersThisWindow is int others && others > 0
            ? string.Format(DataText.EsiWindowSpent, s.OursThisWindow, others)
            : string.Format(DataText.EsiWindowSpentOurs, s.OursThisWindow);
        CallsText = string.Format(DataText.EsiCallsText, s.CallsLastMinute, s.ErrorsLastMinute, s.ErrorsLastHour, s.Refused420, s.Refused429);

        var paced = s.Groups.Count(g => g.PacedBuckets > 0);
        (GovernorText, GovernorBrush) = s.Level switch
        {
            EsiGovernorLevel.Stopped    => (string.Format(DataText.EsiGovernorStopped,    EsiBudget.StoppedBelow),    Palette.Bad),
            EsiGovernorLevel.OneAtATime => (string.Format(DataText.EsiGovernorOneAtATime, EsiBudget.OneAtATimeBelow), Palette.Bad),
            EsiGovernorLevel.Spaced     => (string.Format(DataText.EsiGovernorSpaced,     EsiBudget.SpacedBelow),     Palette.Warn),
            _                           => (DataText.EsiGovernorNormal, paced > 0 ? Palette.Warn : Palette.Good),
        };
        if (paced > 0) GovernorText += " " + string.Format(DataText.EsiGovernorPacedGroups, paced);

        // The lists are rebuilt only when something in them changed, so a selection or scroll in
        // them survives the two-second tick.
        var key = string.Join("|", s.Groups.Select(g => $"{g.Group}:{g.Remaining}:{g.Refusals}:{g.PacedBuckets}:{g.BlockedUntil}:{g.Owner}:{g.Buckets}:{g.SpentHere}:{g.SpentElsewhere}"))
                + "#" + string.Join("|", s.Errors.Select(e => $"{e.Route}:{e.Status}:{e.LastMinute}:{e.LastHour}:{e.SinceStart}"));
        if (key == _lastKey) return;
        _lastKey = key;

        Groups.ResetTo(s.Groups.Select(g =>
        {
            var left  = g.CurrentRemaining(now);
            var share = g.Tokens is int t && t > 0 && left is int e ? 100.0 * e / t : 100;
            return new EsiGroupRowVm
            {
                Group       = g.Group,
                LimitText   = g.Tokens is int n && g.Window is { } w ? string.Format(DataText.EsiLimitPer, n.ToString("N0"), Window(w)) : "",
                LeftText    = left is int l ? l.ToString("N0") : "",
                LeftPercent = share,
                LeftBrush   = share < 20 ? Palette.Bad : share < 50 ? Palette.Warn : Palette.Good,
                StateText   = g.BlockedUntil is { } b ? string.Format(DataText.EsiGroupBlocked, b.ToLocalTime().ToString("HH:mm:ss"))
                            : g.PacedBuckets > 1 ? string.Format(DataText.EsiGroupPacedSome, g.PacedBuckets)
                            : g.PacedBuckets > 0 ? DataText.EsiGroupPaced : "",
                LowestText  = Lowest(g),
                SpentHereText      = g.SpentHere.ToString("N0"),
                SpentElsewhereText = g.SpentElsewhere is int e2 ? e2.ToString("N0") : DataText.EsiSpentElsewhereWatching,
                RoutesText  = string.Join(CommonText.ListSeparator, g.Routes),
                Refusals    = g.Refusals,
            };
        }).ToList());
        Errors.ResetTo(s.Errors.Select(e => new EsiErrorRowVm
        {
            Route      = e.Route,
            Status     = e.Status,
            LastMinute = e.LastMinute,
            LastHour   = e.LastHour,
            SinceStart = e.SinceStart,
            LastAtText = e.LastAt > DateTimeOffset.MinValue ? e.LastAt.ToLocalTime().ToString("HH:mm:ss") : "",
        }).ToList());
        NoGroups = Groups.Count == 0;
        NoErrors = Errors.Count == 0;
    }

    // Whose bucket is lowest, and of how many — one per character, or the connection's own.
    private string Lowest(EsiGroupState g)
    {
        var who = g.Owner == 0 ? DataText.EsiBucketConnection
                : CharacterNames.GetValueOrDefault(g.Owner) ?? string.Format(DataText.EsiBucketCharacter, g.Owner);
        return g.Buckets > 1 ? string.Format(DataText.EsiBucketLowestOf, who, g.Buckets) : who;
    }

    private static string Window(TimeSpan w) =>
        w.TotalHours >= 1 && w.TotalHours == Math.Floor(w.TotalHours) ? string.Format(DataText.EsiWindowHours, (int)w.TotalHours)
        : w.TotalMinutes >= 1 && w.TotalMinutes == Math.Floor(w.TotalMinutes) ? string.Format(DataText.EsiWindowMinutes, (int)w.TotalMinutes)
        : string.Format(DataText.EsiWindowSeconds, (int)w.TotalSeconds);
}
