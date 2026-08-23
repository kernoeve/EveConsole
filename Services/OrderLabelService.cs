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

        var onOrders = await db.OrderLabels.AsNoTracking()
            .Select(l => l.Label).Distinct().ToListAsync(ct);

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
        await ReplaceAsync(db, orderId, labels, ct);
        await db.SaveChangesAsync(ct);
    }

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
        var existing = await db.OrderLabels.AsNoTracking()
            .Select(l => l.Label).Distinct().ToListAsync(ct);

        return existing.FirstOrDefault(e => string.Equals(e, label, StringComparison.OrdinalIgnoreCase))
               ?? label;
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
