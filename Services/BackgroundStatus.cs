namespace EveConsole.Services;

/// <summary>
/// The status bar's word on each background process, in one place so the client doing the work
/// and the clients watching it print the same thing. The worker publishes these lines on its
/// activity board once a second (see <see cref="WorkerActivityService"/>), and a client that is
/// not the worker shows them as they arrive rather than reading its own idle services.
/// </summary>
public static class BackgroundStatus
{
    /// <summary>What the bar says after the process's name, and whether the process is busy —
    /// which is what colours the label.</summary>
    public sealed record Line(string Text, bool Running);

    public static readonly Line Idle = new("Idle", false);

    /// <summary>"3 active, 12 queued": calls holding an HTTP slot, and calls waiting for one.</summary>
    public static Line EsiCalls(int active, int queued) =>
        active + queued == 0 ? Idle : new($"{active:N0} active, {queued:N0} queued", true);

    /// <summary>"processing 1,234 items": the types still to refresh across every region.</summary>
    public static Line PriceHistory(bool sweeping, int queued) =>
        !sweeping ? Idle : new(queued > 0 ? $"processing {queued:N0} items" : "processing", true);

    /// <summary>"120 of 340 contracts": item pulls done of the pass under way.</summary>
    public static Line ContractItems(bool sweeping, int done, int total) =>
        !sweeping ? Idle : new(total > 0 ? $"{done:N0} of {total:N0} contracts" : "starting", true);

    /// <summary>"12 of 300 corporations": the sweep's progress through the NPC corporations.</summary>
    public static Line LpStore(bool sweeping, int done, int total) =>
        !sweeping ? Idle : new(total > 0 ? $"{done:N0} of {total:N0} corporations" : "starting", true);

    /// <summary>The ESI detail fetch on its own, for its row in the Killmails tab: what it is doing,
    /// or how much is left to do.</summary>
    public static Line KillmailFetch(bool fetching, int done, int total, int backlog)
    {
        if (fetching)
        {
            var toGo = backlog > total ? $", {backlog - total:N0} more to go" : "";
            return new(total > 0 ? $"fetching {done:N0} of {total:N0}{toGo}" : "fetching", true);
        }
        return new(backlog > 0 ? $"{backlog:N0} kill mails without details yet — the next poll fetches up to 200"
                               : "every kill mail the app knows has its details", false);
    }

    /// <summary>The ESI detail fetch first, since it is the slow one: "fetching 37 of 200, 4,812 to go";
    /// else a zKillboard daily backfill under way; else idle.</summary>
    public static Line Killmails(bool fetching, int done, int total, int backlog,
                                 bool backfilling, int backfillDone, int backfillTotal)
    {
        if (fetching)
        {
            var toGo = backlog > total ? $", {backlog - total:N0} more to go" : "";
            return new(total > 0 ? $"fetching {done:N0} of {total:N0}{toGo}" : "fetching", true);
        }
        if (backfilling)
            return new(backfillTotal > 0 ? $"backfill {backfillDone:N0} of {backfillTotal:N0}" : "backfill", true);
        return Idle;
    }
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
