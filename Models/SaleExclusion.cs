namespace EveConsole.Models;

/// <summary>
/// A sale the user has marked as not for profit — a transfer to an alt, a favour sold at cost,
/// anything whose margin would be meaningless. Excluded everywhere profit is reckoned; still
/// visible in the Sales Tracker grid itself when asked for.
///
/// <para>Keyed on kind as well as id: a market sale is identified by its wallet transaction id
/// and a contract sale by its contract id, and those two sequences are unrelated, so the pair is
/// what makes a row unique.</para>
/// </summary>
public class SaleExclusion
{
    /// <summary>"Market" or "Contract" — matches SaleRowVm.Kind.</summary>
    public string Kind   { get; set; } = "";
    public long   SaleId { get; set; }

    public DateTimeOffset MarkedAt { get; set; }
}
