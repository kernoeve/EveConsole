using EveConsole.Models;

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
    public static InvRuleShortfall? For(WorklistInvRule rule, InvLevelGroup group,
                                        InvLevelItem item, InvAvailability? avail)
    {
        var target = (long)item.TargetQuantity * Math.Max(1, group.Multiplier);
        if (target <= 0) return null;

        // Stock is what exists: on hand plus in production. Buy orders are deliberately not
        // counted here — the group's include flags describe what the Inventory Levels tool
        // displays, and whether an order is already placed is a separate question the Buy
        // generator answers for itself.
        var have = (avail?.Assets ?? 0) + (avail?.IndustryJobs ?? 0);
        if (have >= target * (rule.ThresholdPercent / 100.0)) return null;

        var wanted = (long)Math.Ceiling(target * (rule.FillTargetPercent / 100.0));

        return new InvRuleShortfall(target, have, wanted, wanted - have,
                                    target > 0 ? have * 100.0 / target : 0);
    }

    /// <summary>"stock 5,607 of 10,000 (56.1%)" — the part every row starts with.</summary>
    public string StockText => $"stock {Have:N0} of {Target:N0} ({Percent:0.#}%)";

    /// <summary>
    /// "Filling to 110% (11,000)." — empty at exactly 100%, because then the target already
    /// explains the number and repeating it is noise. Anywhere else it is the missing piece that
    /// makes the shortfall add up.
    /// </summary>
    public string FillText(WorklistInvRule rule) =>
        Math.Abs(rule.FillTargetPercent - 100) < 0.05
            ? ""
            : $" Filling to {rule.FillTargetPercent:0.#}% ({Wanted:N0}).";
}
