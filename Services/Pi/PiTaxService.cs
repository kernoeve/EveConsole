using System.Globalization;
using EveConsole.Data;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Pi;

/// <summary>
/// The tax rate each planet is charged at: the one learned for it from the journal where there is
/// one, otherwise the Settings default for what collects it there — a customs office or a skyhook.
///
/// <para>The defaults are shared settings (AppPreferences), not per-machine: they are data every
/// client's figures are worked from, the same on all of them.</para>
/// </summary>
public sealed class PiTaxService(AppPreferencesService prefs)
{
    public const string CustomsDefaultKey = "pi.tax.customs_default_pct";
    public const string SkyhookDefaultKey = "pi.tax.skyhook_default_pct";

    /// <summary>
    /// 10 % for both until somebody says otherwise. A customs office's owner sets its rate, and
    /// 10 % is the figure most often met; a skyhook's is set by the sovereignty holder and is
    /// usually lower, so 10 % errs on the side of a charge that turns out smaller than shown.
    /// </summary>
    public const double FallbackPercent = 10;

    public double CustomsDefaultPercent => Percent(CustomsDefaultKey);
    public double SkyhookDefaultPercent => Percent(SkyhookDefaultKey);

    public Task SetCustomsDefaultPercentAsync(double pct) => prefs.SetAsync(CustomsDefaultKey, Format(pct));
    public Task SetSkyhookDefaultPercentAsync(double pct) => prefs.SetAsync(SkyhookDefaultKey, Format(pct));

    /// <summary>The default rate, as a fraction, for what collects the tax.</summary>
    public double DefaultRate(PiChargeKind kind)
        => (kind == PiChargeKind.Skyhook ? SkyhookDefaultPercent : CustomsDefaultPercent) / 100;

    /// <summary>
    /// The rate for each planet: learned, or the default for its system's kind of collector.
    /// </summary>
    public async Task<Dictionary<int, PiChargeRate>> RatesAsync(AppDbContext db,
        IEnumerable<(int PlanetId, int SolarSystemId)> planets, CancellationToken ct = default)
    {
        var list      = planets.Distinct().ToList();
        var planetIds = list.Select(p => p.PlanetId).Distinct().ToList();
        var systemIds = list.Select(p => p.SolarSystemId).Distinct().ToList();

        var learned = await db.PiPlanetTaxRates.AsNoTracking()
            .Where(r => planetIds.Contains(r.PlanetId))
            .ToDictionaryAsync(r => r.PlanetId, ct);
        var systems = await db.SdeSolarSystems.AsNoTracking()
            .Where(s => systemIds.Contains(s.SolarSystemId))
            .Select(s => new { s.SolarSystemId, s.Security, s.FactionId, s.IsWormhole })
            .ToDictionaryAsync(s => s.SolarSystemId, ct);

        var result = new Dictionary<int, PiChargeRate>();
        foreach (var (planetId, systemId) in list)
        {
            // An unknown system (no SDE yet) is taken as customs: the common case, and the
            // cheaper mistake to make in a figure that is only ever an estimate.
            var kind = systems.TryGetValue(systemId, out var s)
                ? PiCharges.KindFor(s.Security, s.FactionId, s.IsWormhole)
                : PiChargeKind.CustomsOffice;

            result[planetId] = learned.TryGetValue(planetId, out var l)
                ? new PiChargeRate(l.Rate, kind, Learned: true, LearnedAt: l.LearnedAt)
                : new PiChargeRate(DefaultRate(kind), kind);
        }
        return result;
    }

    private double Percent(string key)
        => double.TryParse(prefs.Get(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
           && v is >= 0 and <= 100
            ? v
            : FallbackPercent;

    private static string Format(double pct)
        => Math.Clamp(pct, 0, 100).ToString("0.###", CultureInfo.InvariantCulture);
}
