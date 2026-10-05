using EveConsole.Models;
using EveConsole.Localization;

namespace EveConsole.Services.Worklist;

/// <summary>
/// What an inventory rule is asking for, and how to say it.
///
/// Shared by the Buy and Build generators because they ask the same question of a rule and only
/// differ in what they do with the answer. They were computing it separately, and predictably
/// diverged: the Buy side learned to mention a fill target above 100% after "5,607 of 10,000,
/// short 5,393" read as an arithmetic error, and the Build side kept quietly showing
/// "0 of 1 — build 2".
/// </summary>
public sealed record InvRuleShortfall(long Target, long Have, long Wanted, long Shortfall, double Percent)
{
    /// <summary>True when stock has fallen far enough for the rule to fire.</summary>
    /// <param name="claimed">Units of this item a customer order will take before the shelf sees
    /// any of it. ⚠️ Without this the same hull answers two claims: the order is judged covered
    /// because the stock exists, and the shelf is judged full because the same stock exists,
    /// so nothing is ever queued to replace what the order is about to carry away.</param>
    public static InvRuleShortfall? For(WorklistInvRule rule, InvLevelGroup group,
                                        InvLevelItem item, InvAvailability? avail,
                                        long claimed = 0)
    {
        var target = (long)item.TargetQuantity * Math.Max(1, group.Multiplier);
        if (target <= 0) return null;

        // Stock is what exists: on hand plus in production. Buy orders are deliberately not
        // counted here — the group's include flags describe what the Inventory Levels tool
        // displays, and whether an order is already placed is a separate question the Buy
        // generator answers for itself.
        //
        // What an order has claimed comes off first: it is spoken for, and a threshold judged
        // on stock that is already promised declines to top up a shelf that is about to empty.
        var have = Math.Max(0, (avail?.Assets ?? 0) + (avail?.IndustryJobs ?? 0) - claimed);
        if (have >= target * (rule.ThresholdPercent / 100.0)) return null;

        var wanted = FillLevel(target, rule.FillTargetPercent);

        return new InvRuleShortfall(target, have, wanted, wanted - have,
                                    target > 0 ? have * 100.0 / target : 0);
    }

    /// <summary>
    /// The stock a rule fills a level of <paramref name="target"/> up to, at
    /// <paramref name="fillPercent"/>. Every generator asks this question; this is the one answer.
    /// </summary>
    /// <remarks>
    /// <para>⚠️ Above 100% the percentage is rounded DOWN. Rounded up, it doubled a small level:
    /// a level of 1 at 110% is 1.1, and the ceiling made it 2 — a second Enhanced Neurolink
    /// Protection Cell built to sit on the shelf, and its materials bought, for a "10% margin"
    /// on one unit. Rounded down a fill never adds more than its percentage does, and never
    /// drops below the level itself: 1 → 1, 10 → 11, 60 → 66.</para>
    ///
    /// <para>Below 100% it is rounded up, as before: filling to 80% of 1 still means 1.</para>
    ///
    /// <para>Rounded to six places first: 100 × 1.15 is 114.99999999999999 in binary, and a
    /// floor of that would quietly ask for one fewer than the rule says.</para>
    /// </remarks>
    public static long FillLevel(long target, double fillPercent)
    {
        var raw = Math.Round(target * (fillPercent / 100.0), 6);
        return fillPercent >= 100
            ? Math.Max(target, (long)Math.Floor(raw))
            : (long)Math.Ceiling(raw);
    }

    /// <summary>"stock 5,607 of 10,000 (56.1%)" — the part every row starts with.</summary>
    public string StockText => string.Format(WorklistText.StockOfTarget, Have, Target, Percent);

    /// <summary>
    /// "Filling to 110% (11,000)." — empty at exactly 100%, because then the target already
    /// explains the number and repeating it is noise. Anywhere else it is the missing piece that
    /// makes the shortfall add up. Opens with a space when not empty, since it follows a sentence.
    /// </summary>
    public string FillText(WorklistInvRule rule) =>
        Math.Abs(rule.FillTargetPercent - 100) < 0.05
            ? ""
            : " " + string.Format(WorklistText.FillingTo, rule.FillTargetPercent, Wanted);
}
