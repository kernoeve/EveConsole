using System.Text.RegularExpressions;
using EveConsole.Data;
using EveConsole.Localization;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>
/// Reading and writing a courier contract's cargo, entered by hand — see <see cref="CourierCargo"/>
/// — and reading it out of text pasted from the game.
/// </summary>
public static partial class CourierCargoStore
{
    /// <summary>One line read out of pasted text: the name as written, and how many.</summary>
    public sealed record PastedLine(string Name, long Quantity);

    /// <summary>
    /// Lines of "item, quantity" out of text copied from the game or typed by hand.
    ///
    /// <para>The game's inventory and contract windows copy a row as tab-separated columns —
    /// name, then quantity (empty for a single unassembled item), then group, volume and the
    /// rest, which are ignored. Typed, a line may be "Tritanium 1000", "Tritanium x1000",
    /// "1000 Tritanium" or "1000x Tritanium". A quantity is read for its digits alone, so
    /// "1,000", "1.000" and "1 000" are each a thousand whatever the client's language; a line
    /// with none is one. The same item twice is one line with both quantities.</para>
    /// </summary>
    public static List<PastedLine> Parse(string text)
    {
        var lines = new List<PastedLine>();
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\r');
            if (line.Length == 0) continue;

            string name; long qty;
            if (line.Contains('\t'))
            {
                var parts = line.Split('\t');
                name = parts[0].Trim();
                qty  = parts.Length > 1 ? Digits(parts[1]) ?? 1 : 1;
            }
            else if (NameThenQuantity().Match(line) is { Success: true } a)
                (name, qty) = (a.Groups["name"].Value.Trim(), Digits(a.Groups["qty"].Value) ?? 1);
            else if (QuantityThenName().Match(line) is { Success: true } b)
                (name, qty) = (b.Groups["name"].Value.Trim(), Digits(b.Groups["qty"].Value) ?? 1);
            else
                (name, qty) = (line, 1);

            if (name.Length > 0 && qty > 0) lines.Add(new PastedLine(name, qty));
        }
        return lines
            .GroupBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new PastedLine(g.First().Name, g.Sum(l => l.Quantity)))
            .ToList();
    }

    /// <summary>"Name 1,000", "Name x 1000", "Name ×1000" — the quantity last.</summary>
    [GeneratedRegex(@"^(?<name>.+?)\s*(?:\s|[x×]\s*)(?<qty>\d[\d,.  ']*)$")]
    private static partial Regex NameThenQuantity();

    /// <summary>"1,000 Name", "1000x Name" — the quantity first.</summary>
    [GeneratedRegex(@"^(?<qty>\d[\d,.  ']*?)\s*[x×]?\s+(?<name>\D.*)$")]
    private static partial Regex QuantityThenName();

    private static long? Digits(string s)
    {
        var d = new string(s.Where(char.IsAsciiDigit).ToArray());
        return d.Length is > 0 and <= 15 ? long.Parse(d) : null;
    }

    /// <summary>
    /// The item each name is: by its English name or by the name the screen shows, ignoring case.
    /// A name that is neither is left out — the caller says which.
    /// </summary>
    public static async Task<Dictionary<string, int>> ResolveAsync(
        AppDbContext db, IEnumerable<string> names, CancellationToken ct = default)
    {
        var wanted = names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var lower  = wanted.Select(n => n.ToLowerInvariant()).ToList();
        var found  = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // English first, a published item before an unpublished one of the same name.
        foreach (var t in (await db.SdeTypes.AsNoTracking()
                     .Where(t => lower.Contains(t.Name.ToLower()))
                     .Select(t => new { t.TypeId, t.Name, t.Published })
                     .ToListAsync(ct))
                 .OrderByDescending(t => t.Published).ThenBy(t => t.TypeId))
            found.TryAdd(t.Name, t.TypeId);

        // Then as the client shows it in another language.
        await SdeNames.EnsureLoadedAsync(ct);
        foreach (var name in wanted.Where(n => !found.ContainsKey(n)))
        {
            var ids = SdeNames.Find(SdeNameKind.Type, name).Select(id => (int)id).ToList();
            if (ids.Count == 0) continue;
            var english = await db.SdeTypes.AsNoTracking().Where(t => ids.Contains(t.TypeId))
                .Select(t => new { t.TypeId, t.Name, t.Published }).ToListAsync(ct);
            if (english.Where(t => string.Equals(SdeNames.Type(t.TypeId, t.Name), name, StringComparison.OrdinalIgnoreCase))
                       .OrderByDescending(t => t.Published).ThenBy(t => t.TypeId)
                       .FirstOrDefault() is { } hit)
                found[name] = hit.TypeId;
        }
        return found;
    }

    /// <summary>A contract's cargo as entered, with any tick waiting for it attached first.</summary>
    public static async Task<List<CourierCargo>> ForContractAsync(
        IDbContextFactory<AppDbContext> dbFactory, int contractId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await CourierCargoLag.AttachAsync(db, ct);
        return await db.CourierCargo.AsNoTracking().Where(c => c.ContractId == contractId)
            .OrderBy(c => c.Id).ToListAsync(ct);
    }

    /// <summary>
    /// Puts lines on a contract: added to what is there, the same item's quantities summed, or in
    /// place of all of it.
    /// </summary>
    public static async Task AddAsync(
        IDbContextFactory<AppDbContext> dbFactory, ContractRecord contract,
        IReadOnlyList<(int TypeId, long Quantity)> lines, bool replace, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var have = await db.CourierCargo.Where(c => c.ContractId == contract.ContractId).ToListAsync(ct);
        if (replace)
        {
            db.CourierCargo.RemoveRange(have);
            have = [];
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var (typeId, qty) in lines.GroupBy(l => l.TypeId).Select(g => (g.Key, g.Sum(l => l.Quantity))))
        {
            if (have.FirstOrDefault(c => c.TypeId == typeId) is { } row) row.Quantity += qty;
            else db.CourierCargo.Add(new CourierCargo
            {
                ContractId = contract.ContractId,
                FromId     = contract.StartLocationId ?? 0,
                ToId       = contract.EndLocationId ?? 0,
                TypeId     = typeId,
                Quantity   = qty,
                OwnerType  = contract.ForCorporation ? "corporation" : "character",
                OwnerId    = contract.ForCorporation ? contract.IssuerCorporationId : contract.IssuerId,
                AddedAt    = now,
            });
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>One line's quantity; 0 or less takes the line off.</summary>
    public static async Task SetQuantityAsync(
        IDbContextFactory<AppDbContext> dbFactory, long id, long quantity, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.CourierCargo.FirstOrDefaultAsync(c => c.Id == id, ct) is not { } row) return;
        if (quantity <= 0) db.CourierCargo.Remove(row);
        else row.Quantity = quantity;
        await db.SaveChangesAsync(ct);
    }
}
