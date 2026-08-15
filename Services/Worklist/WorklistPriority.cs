namespace EveConsole.Services.Worklist;

/// <summary>
/// How the worklist ranks everything against everything else.
///
/// One place on purpose. The bands started out as a magic number per generator, which meant the
/// relative order of, say, a customer order and a stockpile top-up was never actually decided —
/// it just fell out of five separate guesses. It fell out wrong: work serving a pending order
/// scored lowest in the tool.
///
/// The scale, high to low:
///
/// <list type="bullet">
/// <item><b>Order-driven (120)</b> — someone is waiting on it. A customer order outranks
/// everything, because the cost of it slipping is external.</item>
/// <item><b>Silently failing (100)</b> — an order that exists, looks healthy in every list, and
/// buys nothing because it has been outbid. Ranked above a missing order because a missing one
/// announces itself and this one does not.</item>
/// <item><b>Missing (90)</b> — something declared to exist that does not.</item>
/// <item><b>Standing project (85)</b> — declared corp work that is not running.</item>
/// <item><b>Stock-keeping (40–80)</b> — scaled by how empty the shelf is, so a group at 25% of
/// target outranks one at 75%. Capped below the fixed bands: keeping a stockpile full matters,
/// but not more than an order someone is waiting for.</item>
/// <item><b>Housekeeping (30)</b> — real but not urgent, such as an order nearing expiry.</item>
/// </list>
/// </summary>
public static class WorklistPriority
{
    public const int OrderDriven     = 120;
    public const int Outbid          = 100;
    public const int Missing         = 90;
    public const int StandingProject = 85;
    public const int Housekeeping    = 30;

    // ── Hauling ───────────────────────────────────────────────────────────────
    //
    // A haul is worth what the most valuable thing on it is worth. A run that unblocks a job
    // earns the job's urgency even if most of the cargo is a routine top-up, because the trip
    // happens once and the job starts when it lands. The tiers are never used to split a run:
    // one source to one destination is one task whatever is in it.

    /// <summary>Carries material a job is waiting on.</summary>
    public const int HaulUnblocking = 95;

    /// <summary>Tops a station up to its configured level, with nothing waiting.</summary>
    public const int HaulRestock = 45;

    /// <summary>Moves ore, ice or gas to where it can be refined.</summary>
    public const int HaulToRefine = 40;

    /// <summary>Puts spare stock where its group lives. Real, but nothing is waiting.</summary>
    public const int HaulSurplus = 25;

    private const int StockFloor = 40;
    private const int StockRange = 40;   // so a full shelf scores 40 and an empty one 80

    /// <summary>
    /// Stock-keeping urgency from how far below target a group has fallen. At target it scores
    /// the floor; empty scores the top of the band, still below anything a person is waiting on.
    /// </summary>
    public static int ForStock(double percentOfTarget)
    {
        var depleted = Math.Clamp(100 - percentOfTarget, 0, 100) / 100.0;
        return StockFloor + (int)Math.Round(depleted * StockRange);
    }
}
