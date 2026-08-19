using EveConsole.Services;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>
/// One retention section on the tab: the checkbox, the day count, the button and the outcome.
///
/// <para>A shared type rather than three sets of near-identical properties — each section differs
/// only in which rule it edits and which purge it calls, and copying the plumbing three times is
/// how the fourth one ends up subtly different.</para>
/// </summary>
public class RetentionSectionVm : ReactiveObject
{
    private readonly RetentionRule _rule;
    private readonly Func<int, Task<int>> _purge;
    private readonly string _noun;
    private readonly bool _loading;

    /// <param name="noun">Plural, lower case — used in "Removed 1,234 killmails older than…".</param>
    public RetentionSectionVm(RetentionRule rule, Func<int, Task<int>> purge, string noun)
    {
        _loading = true;
        _rule    = rule;
        _purge   = purge;
        _noun    = noun;

        _enabled = rule.Enabled;
        _days    = rule.Days;
        RefreshLastRun();

        _loading = false;
    }

    private bool _enabled;
    public bool Enabled
    {
        get => _enabled;
        set
        {
            this.RaiseAndSetIfChanged(ref _enabled, value);
            if (!_loading) _rule.Enabled = value;
        }
    }

    private int _days;
    public int Days
    {
        get => _days;
        set
        {
            var clamped = Math.Max(_rule.MinimumDays, value);
            this.RaiseAndSetIfChanged(ref _days, clamped);
            if (!_loading) _rule.Days = clamped;
        }
    }

    public int MinimumDays => _rule.MinimumDays;

    /// <summary>
    /// When this rule last purged, so the automatic sweep is visible rather than a matter of
    /// faith. Rules run on their own 24-hour clock measured from here, not from app start.
    /// </summary>
    private string _lastRunText = "";
    public string LastRunText
    {
        get => _lastRunText;
        private set => this.RaiseAndSetIfChanged(ref _lastRunText, value);
    }

    private void RefreshLastRun()
        => LastRunText = _rule.LastRunUtc is { } t
            ? $"Last purged {t.ToLocalTime():yyyy-MM-dd HH:mm}"
            : "Has not run yet.";

    private string _status = "";
    public string Status
    {
        get => _status;
        private set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    private bool _isPurging;
    public bool IsPurging
    {
        get => _isPurging;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isPurging, value);
            this.RaisePropertyChanged(nameof(CanPurge));
        }
    }
    public bool CanPurge => !IsPurging;

    /// <summary>
    /// Purges to the configured age now, whether or not the automatic sweep is enabled — the
    /// button exists for someone who wants it done immediately, and refusing because a checkbox is
    /// off would read as a puzzle rather than a safeguard.
    /// </summary>
    public async Task PurgeNowAsync()
    {
        if (IsPurging) return;
        IsPurging = true;
        Status = "Purging…";
        try
        {
            var removed = await _purge(Days);

            // A manual purge satisfies the daily window as much as a scheduled one does — not
            // stamping it would have the background sweep repeat the same work minutes later.
            _rule.MarkRun();
            RefreshLastRun();

            Status = removed == 0
                ? $"Nothing older than {Days:N0} days."
                : $"Removed {removed:N0} {_noun} older than {Days:N0} days. The file will not " +
                  "get smaller until it is compacted — see Database → Shrink Database.";
        }
        catch (Exception ex)
        {
            Status = $"Purge failed: {ex.Message}";
        }
        finally { IsPurging = false; }
    }
}

/// <summary>
/// How long the app keeps data it can afford to forget.
///
/// <para>One tab for every retention decision, rather than a setting hidden beside whatever tool
/// happens to write the table — because the question a user actually asks is "what is my database
/// full of and what can I drop", and that is answered by reading these together. Settings →
/// Database → Storage Breakdown is the companion: it says where the space went, this decides what
/// to do about it.</para>
/// </summary>
public class DataRetentionSettingsViewModel : ReactiveObject
{
    public DataRetentionSettingsViewModel(DataRetentionService retention)
    {
        ErrorLog = new RetentionSectionVm(
            retention.ErrorLog, d => retention.PurgeErrorLogAsync(d), "entries");

        Killmails = new RetentionSectionVm(
            retention.Killmails, d => retention.PurgeKillmailsAsync(d), "killmails");

        PriceHistory = new RetentionSectionVm(
            retention.PriceHistory, d => retention.PurgePriceHistoryAsync(d), "rows");

        GameLog = new RetentionSectionVm(
            retention.GameLog, d => retention.PurgeGameLogAsync(d), "events");

        ChatMessages = new RetentionSectionVm(
            retention.ChatMessages, d => retention.PurgeChatMessagesAsync(d), "messages");
    }

    public RetentionSectionVm ErrorLog     { get; }
    public RetentionSectionVm Killmails    { get; }
    public RetentionSectionVm PriceHistory { get; }
    public RetentionSectionVm GameLog      { get; }
    public RetentionSectionVm ChatMessages { get; }
}
