using EveConsole.Models;
using EveConsole.Services;

namespace EveConsole.ViewModels;

/// <summary>
/// One outstanding order, as the read-only summaries show it.
///
/// <para>Shared by the Overview's Orders section and the Stores tab, so the same order cannot be
/// described two different ways on two screens. An order is edited in the Order Tracker and
/// nowhere else; both callers of this are views onto it.</para>
/// </summary>
public class OrderSummaryRowVm(TrackedOrder o, string itemName)
{
    /// <summary>⚠️ The Order Tracker's format, off the same field, deliberately. Two screens
    /// showing the same order under different dates is a bug report waiting to happen, and the
    /// tracker is the one people check against.</summary>
    public string Created  => o.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd");

    /// <summary>⚠️ Sorting key for Created, because the column only shows the DATE. Several
    /// orders placed on the same day are indistinguishable to the displayed text, so ordering
    /// on it shuffles them arbitrarily; this keeps the real sequence. The Order Tracker keys
    /// its own created column the same way.</summary>
    public long CreatedSort => o.CreatedAt.UtcTicks;

    /// <summary>What the buyer quotes back. Empty on an order entered by hand.</summary>
    public string Ref      => o.OrderRef;

    public string Item     => itemName.Length > 0 ? itemName : $"Type {o.TypeId}";
    public int    Units    => o.Units;
    public string UnitsText => o.Units.ToString("N0");
    public string Buyer    => o.Buyer.Length > 0 ? o.Buyer : "";

    /// <summary>Blank when nobody has estimated one yet — which is itself worth seeing, since an
    /// order with no date is one the buyer has been told nothing about.</summary>
    public string EstDate  => o.EstimatedDate is { Length: > 0 } d ? d : "";

    public string Status   => o.Status.Length > 0
                            ? char.ToUpper(o.Status[0]) + o.Status[1..]
                            : o.Status;

    /// <summary>Where the units are expected to come from. The column that actually moves while
    /// an order is open — Status reads "Pending" on every row of an active-only list.</summary>
    public string Source   => o.FulfilmentSource switch
    {
        OrderFulfilmentService.SourceStock    => "Stock",
        OrderFulfilmentService.SourceJob      => "In production",
        OrderFulfilmentService.SourceContract => "Contracted",
        _                                     => "Unsourced",
    };

    /// <summary>An open order with nothing behind it: the row to look at.</summary>
    public bool IsUnsourced => o.FulfilmentSource.Length == 0;

    /// <summary>⚠️ Sorts undated orders last rather than first. An empty date string sorts as
    /// earliest, which put the orders nobody had answered at the top pretending to be the most
    /// urgent. Used as the primary key wherever these rows are ordered by promise date.</summary>
    public string EstSortKey => EstDate.Length > 0 ? EstDate : "9999-99-99";
}
