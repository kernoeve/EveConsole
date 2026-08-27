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
/// <item><b>Asset safety (300)</b> — a game-enforced deadline with money on the other side of
/// it. Above even the order band, because every other kind of work merely slips when it is
/// left; this one expires.</item>
/// <item><b>Order-driven (120)</b> — someone is waiting on it. A customer order outranks
/// everything, because the cost of it slipping is external.</item>
/// <item><b>Silently failing (100)</b> — an order that exists, looks healthy in every list, and
/// buys nothing because it has been outbid. Ranked above a missing order because a missing one
/// announces itself and this one does not.</item>
/// <item><b>Missing (90)</b> — something declared to exist that does not.</item>
/// <item><b>Standing project (85)</b> — declared corp work that is not running.</item>
/// <item><b>Final products (81–84)</b> — the ships the operation actually sells or flies,
/// from a rule flagged final. A whole band above every other kind of stock-keeping, and below
/// everything operational, so a hull is never queued behind a routine top-up but never ahead of
/// an order either.</item>
/// <item><b>Stock-keeping (40–80)</b> — scaled by how empty the shelf is, so a group at 25% of
/// target outranks one at 75%. Capped below the fixed bands: keeping a stockpile full matters,
/// but not more than an order someone is waiting for.</item>
/// <item><b>Housekeeping (30)</b> — real but not urgent, such as an order nearing expiry.</item>
/// </list>
/// </summary>
public static class WorklistPriority
{
    /// <summary>
    /// Items sitting in asset safety that can be acted on.
    ///
    /// <para>Deliberately above <see cref="OrderBandTop"/>, which is otherwise the ceiling. The
    /// order band is wide because orders compete with each other; this does not compete with
    /// anything. A job that waits a day is a job done a day later, but asset safety runs on the
    /// game's clock: miss the window and the items are delivered wherever the game chooses, at
    /// the game's fee. There is no version of "later" that costs nothing.</para>
    /// </summary>
    public const int AssetSafety     = 300;

    public const int OrderDriven     = 120;
    public const int Outbid          = 100;

    /// <summary>
    /// The band customer-order work occupies, one step per order.
    ///
    /// <para>Orders are not equal to each other. The one due first, or hand-marked to jump the
    /// queue, has to outrank the one due next month — and so does everything it needs, all the way
    /// down the tree and including the hauls that feed it. Encoding the order's rank in the number
    /// gets that for free: the demand service already carries a parent's priority down to its
    /// children, and a haul takes the priority of its most valuable cargo.</para>
    ///
    /// <para>Rank 0 is the most urgent order. The band is wide enough for a hundred of them before
    /// it would touch <see cref="Outbid"/>, and the floor stops it ever doing so.</para>
    /// </summary>
    public const int OrderBandTop = 220;

    /// <summary>Where work for the order at <paramref name="rank"/> sits. Rank 0 is first.</summary>
    public static int ForOrder(int rank) => Math.Max(OrderDriven, OrderBandTop - rank);

    /// <summary>
    /// Ore to reprocess, and compressed gas to decompress.
    ///
    /// <para>High for two reasons that rarely meet. It is material other work is already waiting
    /// on — a job short of Tritanium while the Veldspar sits in the same hangar is blocked by a
    /// wrapper, not by a shortage — and it is among the cheapest things on the list to do: no
    /// trip, no ISK, one action where the material already is. Work that unblocks other work and
    /// costs nothing should not queue behind a stockpile top-up.</para>
    ///
    /// <para>Below <see cref="OrderDriven"/> all the same. The order is the point; this only ever
    /// serves it.</para>
    /// </summary>
    public const int Refining        = 110;

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

    // Final products sit in their own band directly above stock-keeping. Narrow on purpose:
    // every one of them has to outrank every ordinary top-up, and none of them may reach
    // StandingProject (85) or anything above it.
    private const int FinalFloor = 81;
    private const int FinalRange = 3;    // 81 when nearly full, 84 when empty

    /// <summary>
    /// Stock-keeping urgency from how far below target a group has fallen. At target it scores
    /// the floor; empty scores the top of the band, still below anything a person is waiting on.
    /// </summary>
    public static int ForStock(double percentOfTarget)
    {
        var depleted = Math.Clamp(100 - percentOfTarget, 0, 100) / 100.0;
        return StockFloor + (int)Math.Round(depleted * StockRange);
    }

    /// <summary>
    /// The same measure for an item a rule has flagged as a final product.
    ///
    /// <para>⚠️ A band, not a bonus. A titan wanted for the shelf is stock-keeping like any
    /// other, and scored like any other it lands wherever its coverage puts it — a hull at 90%
    /// of target scored 44 and sat near the bottom of the list, behind intermediates being made
    /// to top up shelves that block nothing. Priority is the outer sort, so no tie-break below
    /// it can rescue that.</para>
    ///
    /// <para>Being the thing the operation actually sells is worth more than how full its shelf
    /// happens to be, so every final product outranks every ordinary top-up regardless of
    /// coverage, and coverage only orders them among themselves. Children inherit a parent's
    /// priority, so the components a hull is waiting on rise with it — which is the other half
    /// of the problem: they were low enough to lose to work blocking nothing at all.</para>
    ///
    /// <para>Still below everything operational, and far below an order. An item somebody is
    /// waiting on outranks a hull built to fill a shelf, which is the whole point of the
    /// ordering.</para>
    /// </summary>
    public static int ForFinalStock(double percentOfTarget)
    {
        var depleted = Math.Clamp(100 - percentOfTarget, 0, 100) / 100.0;
        return FinalFloor + (int)Math.Round(depleted * FinalRange);
    }
}
