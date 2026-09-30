using EveConsole.Data;
using EveConsole.Localization;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Fitting;

/// <summary>Unit prices for a fit's items, on the same basis the app values assets with.
/// <paramref name="Basis"/> says which, for showing: the price type and the market.</summary>
public sealed record FitPrices(IReadOnlyDictionary<int, double> ByType, string Basis)
{
    public static readonly FitPrices None = new(new Dictionary<int, double>(), "");
    public double? Of(int typeId) => ByType.TryGetValue(typeId, out var p) && p > 0 ? p : null;
}

public static class FitPricing
{
    /// <summary>
    /// Prices for <paramref name="typeIds"/> at the market and price type chosen for asset values
    /// in Settings → Market — the basis net worth and killmail values use, so a fit's value agrees
    /// with what the same items are worth everywhere else in the app.
    /// </summary>
    public static async Task<FitPrices> LoadAsync(IDbContextFactory<AppDbContext> dbFactory, IEnumerable<int> typeIds, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var settings = await db.MarketDefaultSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);
        if (settings?.AssetValueConfigId is not int configId) return FitPrices.None;
        var priceType = settings.AssetValuePriceType;

        var ids = typeIds.Distinct().ToList();
        var byType = await db.MarketItemPrices.AsNoTracking()
            .Where(p => p.ConfigId == configId && ids.Contains(p.TypeId))
            .ToDictionaryAsync(p => p.TypeId, p => priceType switch
            {
                MarketPriceType.Buy  => p.BuyPrice,
                MarketPriceType.Sell => p.SellPrice,
                _                    => p.Midpoint,
            }, ct);
        // The market as its source is named — the user's to rename.
        var market = await db.MarketPricingConfigs.AsNoTracking().Where(c => c.Id == configId).Select(c => c.LocationName).FirstOrDefaultAsync(ct);
        var label  = MarketPriceType.Label(priceType);
        return new FitPrices(byType, market is null ? label : string.Format(FittingText.PriceBasisWithMarket, label, market));
    }
}
