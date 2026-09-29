using EveConsole.Localization;

namespace EveConsole.Services;

/// <summary>
/// The status bar's word on each background process, in one place so the client doing the work
/// and the clients watching it print the same thing. The worker publishes these lines on its
/// activity board once a second (see <see cref="WorkerActivityService"/>), and a client that is
/// not the worker shows them as they arrive rather than reading its own idle services.
///
/// <para>⚠️ Published as finished text, so a client that is not the worker shows these lines in the
/// worker's interface language rather than its own. Accepted for now.</para>
/// </summary>
public static class BackgroundStatus
{
    /// <summary>What the bar says after the process's name, and whether the process is busy —
    /// which is what colours the label.</summary>
    public sealed record Line(string Text, bool Running);

    public static readonly Line Idle = new(DataText.StateIdle, false);

    /// <summary>"3 active, 12 queued": calls holding an HTTP slot, and calls waiting for one.</summary>
    public static Line EsiCalls(int active, int queued) =>
        active + queued == 0 ? Idle : new(string.Format(DataText.BarEsiActiveQueued, active, queued), true);

    /// <summary>"processing 1,234 items": the types still to refresh across every region.</summary>
    public static Line PriceHistory(bool sweeping, int queued) =>
        !sweeping ? Idle : new(queued > 0
            ? Plurals.Format(DataText.ResourceManager, nameof(DataText.BarProcessingItemsOther), queued)
            : DataText.BarProcessing, true);

    /// <summary>"120 of 340 contracts": item pulls done of the pass under way.</summary>
    public static Line ContractItems(bool sweeping, int done, int total) =>
        !sweeping ? Idle : new(total > 0
            ? Plurals.Format(DataText.ResourceManager, nameof(DataText.BarContractsProgressOther), total, done)
            : DataText.BarStarting, true);

    /// <summary>"12 of 300 corporations": the sweep's progress through the NPC corporations.</summary>
    public static Line LpStore(bool sweeping, int done, int total) =>
        !sweeping ? Idle : new(total > 0
            ? Plurals.Format(DataText.ResourceManager, nameof(DataText.BarCorporationsProgressOther), total, done)
            : DataText.BarStarting, true);

    /// <summary>The ESI detail fetch on its own, for its row in the Killmails tab: what it is doing,
    /// or how much is left to do.</summary>
    public static Line KillmailFetch(bool fetching, int done, int total, int backlog)
    {
        if (fetching) return Fetching(done, total, backlog);
        return new(backlog > 0
            ? Plurals.Format(DataText.ResourceManager, nameof(DataText.KillmailsWithoutDetailsOther), backlog)
            : DataText.KillmailsAllDetailed, false);
    }

    /// <summary>The ESI detail fetch first, since it is the slow one: "fetching 37 of 200, 4,812 to go";
    /// else a zKillboard daily backfill under way; else idle.</summary>
    public static Line Killmails(bool fetching, int done, int total, int backlog,
                                 bool backfilling, int backfillDone, int backfillTotal)
    {
        if (fetching) return Fetching(done, total, backlog);
        if (backfilling)
            return new(backfillTotal > 0
                ? string.Format(DataText.BarBackfillProgress, backfillDone, backfillTotal)
                : DataText.BarBackfill, true);
        return Idle;
    }

    /// <summary>The detail fetch under way, for both lines above.</summary>
    private static Line Fetching(int done, int total, int backlog) =>
        new(total <= 0      ? DataText.BarFetching
          : backlog > total ? string.Format(DataText.BarFetchingProgressMore, done, total, backlog - total)
          :                   string.Format(DataText.BarFetchingProgress, done, total), true);
}

/// <summary>
/// Reads the five lines off the services in this process. The client holding the worker lease
/// publishes what this says; every client shows it for its own status bar when it is the worker.
/// </summary>
public sealed class BackgroundStatusSampler(
    Api.EsiClient             esi,
    MarketHistoryService      history,
    ContractsService          contracts,
    LpStoreService            lpStore,
    KillMailService           killMails,
    ZkillboardBackfillService zkbBackfill)
{
    public BackgroundStatus.Line EsiCalls()      => BackgroundStatus.EsiCalls(esi.ActiveCalls, esi.QueuedCalls);
    public BackgroundStatus.Line PriceHistory()  => BackgroundStatus.PriceHistory(history.IsSweeping, history.SweepStatuses.Sum(s => s.Queue));
    public BackgroundStatus.Line ContractItems() => BackgroundStatus.ContractItems(contracts.IsSweepingItems, contracts.ItemsDone, contracts.ItemsTotal);
    public BackgroundStatus.Line LpStore()       => BackgroundStatus.LpStore(lpStore.IsSweeping, lpStore.CorpsDone, lpStore.CorpsTotal);
    public BackgroundStatus.Line Killmails()     => BackgroundStatus.Killmails(killMails.IsFetching, killMails.FetchDone, killMails.FetchTotal, killMails.Backlog,
                                                                              zkbBackfill.IsImporting, zkbBackfill.ProgressCurrent, zkbBackfill.ProgressTotal);
    public BackgroundStatus.Line KillmailFetch() => BackgroundStatus.KillmailFetch(killMails.IsFetching, killMails.FetchDone, killMails.FetchTotal, killMails.Backlog);

    /// <summary>The five, keyed as the activity board carries them.</summary>
    public IEnumerable<(string Key, BackgroundStatus.Line Line)> All() =>
    [
        (WorkerActivityService.BarEsiCalls,      EsiCalls()),
        (WorkerActivityService.BarPriceHistory,  PriceHistory()),
        (WorkerActivityService.BarContractItems, ContractItems()),
        (WorkerActivityService.BarLpStore,       LpStore()),
        (WorkerActivityService.BarKillmails,     Killmails()),
        (WorkerActivityService.KillMailFetch,    KillmailFetch()),
    ];
}
