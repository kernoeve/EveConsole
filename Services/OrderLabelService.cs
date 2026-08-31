using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>
/// Tags on orders: what they are, which orders carry them, and what the pickers offer.
///
/// <para>Labels are free text somebody typed. There is no table of permitted values and no
/// creating one before use — typing a new label into a picker IS creating it, and the list of
/// choices is whatever is in use plus whatever the stores are configured to apply.</para>
///
/// <para><b>⚠️ Compared without case.</b> "BNI First Capital" and "bni first capital" are the
/// same tag to everyone except a database, and two spellings of one label split a report in half
/// silently. The stored form is whatever was typed first; every comparison ignores case.</para>
/// </summary>
public class OrderLabelService(IDbContextFactory<AppDbContext> dbFactory)
{
    /// <summary>Every label in use, plus any a store is set to apply, sorted for a picker.</summary>
    public async Task<List<string>> AllAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var onOrders = (await db.OrderLabels.AsNoTracking()
                .Select(l => l.Label).Distinct().ToListAsync(ct))
            .Concat(await db.SaleLabels.AsNoTracking()
                .Select(l => l.Label).Distinct().ToListAsync(ct))
            .ToList();

        // ⚠️ Store settings too. A label configured on a shop that has taken no orders yet exists
        // as an intention and nothing else, and leaving it out of the picker would mean the one
        // place it is guaranteed to matter could not offer it.
        var onStores = (await db.Stores.AsNoTracking()
                .Where(s => s.OrderLabels != "")
                .Select(s => s.OrderLabels).ToListAsync(ct))
            .SelectMany(Split);

        return onOrders.Concat(onStores)
            .Where(l => l.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(l => l, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The labels on each of the given orders.</summary>
    public async Task<Dictionary<int, List<string>>> ForOrdersAsync(
        IReadOnlyCollection<int> orderIds, CancellationToken ct = default)
    {
        if (orderIds.Count == 0) return [];

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        return (await db.OrderLabels.AsNoTracking()
                .Where(l => orderIds.Contains(l.OrderId))
                .ToListAsync(ct))
            .GroupBy(l => l.OrderId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(l => l.Label).OrderBy(l => l, StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>Replaces one order's labels with exactly these.</summary>
    public async Task SetAsync(int orderId, IEnumerable<string> labels, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var before = await db.OrderLabels.AsNoTracking()
            .Where(l => l.OrderId == orderId).Select(l => l.Label).ToListAsync(ct);

        await ReplaceAsync(db, orderId, labels, ct);
        await db.SaveChangesAsync(ct);

        var after = await db.OrderLabels.AsNoTracking()
            .Where(l => l.OrderId == orderId).Select(l => l.Label).ToListAsync(ct);

        // ⚠️ The DIFFERENCE is what travels, not the new set. This is a replace, and replaying a
        // replace onto the sale would make one order's box the whole contract's labels — wiping
        // anything a sibling order or the sale itself carried and this box never showed.
        var contracts = await ContractsOfOrdersAsync(db, [orderId], ct);
        if (contracts.Count == 0) return;

        foreach (var gained in after.Except(before, StringComparer.OrdinalIgnoreCase))
            await SpreadAsync(db, contracts, gained, add: true, ct);

        foreach (var lost in before.Except(after, StringComparer.OrdinalIgnoreCase))
            await SpreadAsync(db, contracts, lost, add: false, ct);
    }

    /// <summary>Takes one label off several orders, and off any sale sharing their contract.</summary>
    public async Task RemoveAsync(IReadOnlyCollection<int> orderIds, string label, CancellationToken ct = default)
    {
        var clean = Clean(label);
        if (clean.Length == 0 || orderIds.Count == 0) return;

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        db.OrderLabels.RemoveRange(await db.OrderLabels
            .Where(l => l.Label == clean && orderIds.Contains(l.OrderId)).ToListAsync(ct));
        await db.SaveChangesAsync(ct);

        await SpreadAsync(db, await ContractsOfOrdersAsync(db, orderIds, ct), clean, add: false, ct);
    }

    /// <summary>The contracts a set of orders is linked to.</summary>
    private static async Task<List<long>> ContractsOfOrdersAsync(
        AppDbContext db, IReadOnlyCollection<int> orderIds, CancellationToken ct) =>
        await db.TrackedOrders.AsNoTracking()
            .Where(o => orderIds.Contains(o.Id) && o.LinkedContractId != null)
            .Select(o => (long)o.LinkedContractId!.Value)
            .Distinct().ToListAsync(ct);

    /// <summary>
    /// Adds one label to several orders at once, skipping any that already carry it.
    ///
    /// <para>For the grid's right-click, where the point is tagging a selection in one action.</para>
    /// </summary>
    public async Task AddAsync(IReadOnlyCollection<int> orderIds, string label, CancellationToken ct = default)
    {
        var clean = Clean(label);
        if (clean.Length == 0 || orderIds.Count == 0) return;

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // ⚠️ Existing spellings win. Adding "bni first capital" where "BNI First Capital" is
        // already in use must not create a second tag that reads the same and counts separately.
        clean = await CanonicalAsync(db, clean, ct);

        var already = await db.OrderLabels.AsNoTracking()
            .Where(l => orderIds.Contains(l.OrderId) && l.Label == clean)
            .Select(l => l.OrderId).ToListAsync(ct);

        var missing = orderIds.Except(already).ToList();
        if (missing.Count == 0) return;

        db.OrderLabels.AddRange(missing.Select(id => new OrderLabel { OrderId = id, Label = clean }));
        await db.SaveChangesAsync(ct);

        await SpreadAsync(db, await ContractsOfOrdersAsync(db, orderIds, ct), clean, add: true, ct);
    }

    /// <summary>Applies a store's configured labels to a set of orders it has just taken.</summary>
    public async Task ApplyStoreLabelsAsync(
        AppDbContext db, string? storeLabels, IEnumerable<int> orderIds, CancellationToken ct = default)
    {
        var labels = Split(storeLabels ?? "").ToList();
        if (labels.Count == 0) return;

        foreach (var id in orderIds)
            foreach (var label in labels)
                db.OrderLabels.Add(new OrderLabel { OrderId = id, Label = label });

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Order ids carrying a label, or every labelled order when none is named.</summary>
    public async Task<HashSet<int>> OrdersWithAsync(string? label, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var q = db.OrderLabels.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(label))
        {
            var clean = Clean(label);
            q = q.Where(l => l.Label == clean);
        }

        return (await q.Select(l => l.OrderId).Distinct().ToListAsync(ct)).ToHashSet();
    }

    /// <summary>Splits a stored comma-separated setting into labels.</summary>
    public static IEnumerable<string> Split(string? text) =>
        (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(Clean)
                    .Where(l => l.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>⚠️ Commas removed, not just trimmed. A label containing one would split into two
    /// the next time a store setting round-tripped through a comma-separated field.</summary>
    public static string Clean(string? label) =>
        (label ?? "").Replace(",", " ").Trim();

    private static async Task<string> CanonicalAsync(AppDbContext db, string label, CancellationToken ct)
    {
        // Both sides, because they are one list of labels kept in two tables — a spelling already
        // in use on a sale must win over a new one typed on an order, or the two halves of the
        // same tag drift apart in exactly the place they are supposed to agree.
        var existing = (await db.OrderLabels.AsNoTracking()
                .Select(l => l.Label).Distinct().ToListAsync(ct))
            .Concat(await db.SaleLabels.AsNoTracking()
                .Select(l => l.Label).Distinct().ToListAsync(ct));

        return existing.FirstOrDefault(e => string.Equals(e, label, StringComparison.OrdinalIgnoreCase))
               ?? label;
    }

    // ── Sales ─────────────────────────────────────────────────────────────────
    //
    // A sale and an order are the same delivery seen from two sides, and the thing that joins
    // them is the contract: an order names one in LinkedContractId, and a contract sale IS one.
    // A label put on either belongs to both.
    //
    // ⚠️ Two tables and a reconciliation rather than one shared table, because the two sides
    // exist independently and either can come first. An order taken through a programme is
    // labelled the day it is placed; the contract that fulfils it may not exist for a week, and
    // the sale not until it is accepted. Anything that assumed the link was there at labelling
    // time would lose exactly the case the labels are for.

    /// <summary>"Contract" — the one sale kind that can share a label with an order.</summary>
    private const string ContractKind = "Contract";

    /// <summary>The labels on each of the given sales.</summary>
    public async Task<Dictionary<(string Kind, long SaleId), List<string>>> ForSalesAsync(
        IReadOnlyCollection<(string Kind, long SaleId)> sales, CancellationToken ct = default)
    {
        if (sales.Count == 0) return [];

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // ⚠️ Read whole and filtered in memory. The key is a pair, and a composite IN over a few
        // thousand of them is not something SQLite does well — while this table holds one row per
        // label actually in use, which is small.
        var wanted = sales.ToHashSet();

        return (await db.SaleLabels.AsNoTracking().ToListAsync(ct))
            .Where(l => wanted.Contains((l.Kind, l.SaleId)))
            .GroupBy(l => (l.Kind, l.SaleId))
            .ToDictionary(
                g => g.Key,
                g => g.Select(l => l.Label).OrderBy(l => l, StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>Adds one label to several sales, and to any order sharing their contract.</summary>
    public async Task AddToSalesAsync(
        IReadOnlyCollection<(string Kind, long SaleId)> sales, string label, CancellationToken ct = default)
    {
        var clean = Clean(label);
        if (clean.Length == 0 || sales.Count == 0) return;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        clean = await CanonicalAsync(db, clean, ct);

        var have = (await db.SaleLabels.AsNoTracking().Where(l => l.Label == clean).ToListAsync(ct))
            .Select(l => (l.Kind, l.SaleId)).ToHashSet();

        foreach (var (kind, id) in sales.Distinct())
            if (have.Add((kind, id)))
                db.SaleLabels.Add(new SaleLabel { Kind = kind, SaleId = id, Label = clean });

        await db.SaveChangesAsync(ct);
        await SpreadAsync(db, ContractsOf(sales), clean, add: true, ct);
    }

    /// <summary>Removes one label from several sales, and from any order sharing their contract.</summary>
    public async Task RemoveFromSalesAsync(
        IReadOnlyCollection<(string Kind, long SaleId)> sales, string label, CancellationToken ct = default)
    {
        var clean = Clean(label);
        if (clean.Length == 0 || sales.Count == 0) return;

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var wanted = sales.ToHashSet();
        var doomed = (await db.SaleLabels.Where(l => l.Label == clean).ToListAsync(ct))
            .Where(l => wanted.Contains((l.Kind, l.SaleId))).ToList();

        db.SaleLabels.RemoveRange(doomed);
        await db.SaveChangesAsync(ct);
        await SpreadAsync(db, ContractsOf(sales), clean, add: false, ct);
    }

    /// <summary>
    /// Brings orders and sales into agreement wherever they share a contract.
    ///
    /// <para><b>⚠️ A union, deliberately.</b> This runs when a link is noticed, not when somebody
    /// presses something, and at that moment neither side is the authority — the order was
    /// labelled before the contract existed, and the sale may have been labelled since. Both sets
    /// are real, so both are kept.</para>
    ///
    /// <para>The consequence worth knowing: a label removed from one side while the two were not
    /// yet linked comes back when they are. Removals made after the link propagate immediately
    /// and are not undone by this, because by then both sides already agree.</para>
    ///
    /// <para>Cheap and idempotent — two small tables read, only what is missing written — so it
    /// runs on every load of either tracker rather than trying to detect the moment a link
    /// appears. The link is made by the contract matcher, on its own schedule.</para>
    /// </summary>
    public async Task SyncByContractAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var linked = await db.TrackedOrders.AsNoTracking()
            .Where(o => o.LinkedContractId != null)
            .Select(o => new { o.Id, ContractId = (long)o.LinkedContractId!.Value })
            .ToListAsync(ct);
        if (linked.Count == 0) return;

        var orderIds    = linked.Select(o => o.Id).ToHashSet();
        var orderLabels = (await db.OrderLabels.AsNoTracking().ToListAsync(ct))
                          .Where(l => orderIds.Contains(l.OrderId)).ToList();
        var saleLabels  = await db.SaleLabels.AsNoTracking()
                          .Where(l => l.Kind == ContractKind).ToListAsync(ct);

        var byContract  = linked.GroupBy(o => o.ContractId)
                                .ToDictionary(g => g.Key, g => g.Select(o => o.Id).ToList());
        var haveOnOrder = orderLabels.Select(l => (l.OrderId, l.Label)).ToHashSet();
        var haveOnSale  = saleLabels.Select(l => (l.SaleId, l.Label)).ToHashSet();

        var added = false;
        foreach (var (contractId, ids) in byContract)
        {
            var union = orderLabels.Where(l => ids.Contains(l.OrderId)).Select(l => l.Label)
                .Concat(saleLabels.Where(l => l.SaleId == contractId).Select(l => l.Label))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (union.Count == 0) continue;

            foreach (var label in union)
            {
                foreach (var id in ids)
                    if (haveOnOrder.Add((id, label)))
                    {
                        db.OrderLabels.Add(new OrderLabel { OrderId = id, Label = label });
                        added = true;
                    }

                if (haveOnSale.Add((contractId, label)))
                {
                    db.SaleLabels.Add(new SaleLabel { Kind = ContractKind, SaleId = contractId, Label = label });
                    added = true;
                }
            }
        }

        if (added) await db.SaveChangesAsync(ct);
    }

    /// <summary>The contract ids among a set of sales. Market sales have none and are skipped.</summary>
    private static List<long> ContractsOf(IEnumerable<(string Kind, long SaleId)> sales) =>
        sales.Where(s => s.Kind == ContractKind).Select(s => s.SaleId).Distinct().ToList();

    /// <summary>
    /// Carries one label's addition or removal across everything sharing a contract.
    ///
    /// <para>Called from both directions — a label put on an order reaches its sale, one put on a
    /// sale reaches its orders — so the two sides never disagree about a contract they both
    /// already know about.</para>
    /// </summary>
    private static async Task SpreadAsync(
        AppDbContext db, IReadOnlyCollection<long> contractIds, string label, bool add, CancellationToken ct)
    {
        if (contractIds.Count == 0) return;

        var orderIds = await db.TrackedOrders.AsNoTracking()
            .Where(o => o.LinkedContractId != null && contractIds.Contains((long)o.LinkedContractId!.Value))
            .Select(o => o.Id).ToListAsync(ct);

        if (add)
        {
            var already = await db.OrderLabels.AsNoTracking()
                .Where(l => l.Label == label && orderIds.Contains(l.OrderId))
                .Select(l => l.OrderId).ToListAsync(ct);

            foreach (var id in orderIds.Except(already))
                db.OrderLabels.Add(new OrderLabel { OrderId = id, Label = label });

            var onSales = (await db.SaleLabels.AsNoTracking()
                    .Where(l => l.Label == label && l.Kind == ContractKind)
                    .Select(l => l.SaleId).ToListAsync(ct)).ToHashSet();

            foreach (var cid in contractIds.Where(c => !onSales.Contains(c)))
                db.SaleLabels.Add(new SaleLabel { Kind = ContractKind, SaleId = cid, Label = label });
        }
        else
        {
            db.OrderLabels.RemoveRange(await db.OrderLabels
                .Where(l => l.Label == label && orderIds.Contains(l.OrderId)).ToListAsync(ct));

            db.SaleLabels.RemoveRange(await db.SaleLabels
                .Where(l => l.Label == label && l.Kind == ContractKind && contractIds.Contains(l.SaleId))
                .ToListAsync(ct));
        }

        await db.SaveChangesAsync(ct);
    }

    private static async Task ReplaceAsync(
        AppDbContext db, int orderId, IEnumerable<string> labels, CancellationToken ct)
    {
        await db.OrderLabels.Where(l => l.OrderId == orderId).ExecuteDeleteAsync(ct);

        var wanted = labels.Select(Clean).Where(l => l.Length > 0)
                           .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        foreach (var label in wanted)
            db.OrderLabels.Add(new OrderLabel
            {
                OrderId = orderId,
                Label   = await CanonicalAsync(db, label, ct),
            });
    }
}
