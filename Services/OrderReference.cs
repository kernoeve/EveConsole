using EveConsole.Data;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>
/// The short code that identifies one order.
///
/// <para><b>⚠️ Unique across every order, not just within a store.</b> A buyer quotes one of
/// these back to ask about an order or to cancel it, and the lookup finds it by code alone — so
/// two orders sharing a code, even in different shops, would be two answers to one question.</para>
///
/// <para>Several rows can share one: an order for three things is three rows and one code,
/// because that is what the person placed. Orders entered by hand are one row each and so get
/// one code each.</para>
/// </summary>
public static class OrderReference
{
    /// <summary>
    /// The characters a code is drawn from.
    ///
    /// <para>⚠️ Chosen so nothing in one can be misread when it is typed back: no O against 0,
    /// no I or 1, no S against 5. A code is read off one mail and typed into another, and every
    /// pair that looks alike is a cancellation that finds nothing.</para>
    /// </summary>
    private const string Alphabet = "ABCDEFGHJKLMNPQRTUVWXYZ2346789";

    private const int Length = 6;

    /// <summary>A code no order is using.</summary>
    public static async Task<string> NewAsync(AppDbContext db, CancellationToken ct = default)
    {
        var used = await db.TrackedOrders.Where(o => o.OrderRef != "")
            .Select(o => o.OrderRef).Distinct().ToListAsync(ct);
        var taken = used.ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var attempt = 0; attempt < 50; attempt++)
        {
            var candidate = string.Concat(Enumerable.Range(0, Length)
                .Select(_ => Alphabet[Random.Shared.Next(Alphabet.Length)]));
            if (taken.Add(candidate)) return candidate;
        }

        // Fifty collisions against thirty characters to the sixth means something is wrong
        // rather than unlucky. A timestamp is ugly, but it is unique and the order still gets one.
        return "T" + DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()[^5..];
    }
}
