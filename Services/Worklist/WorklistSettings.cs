using EveConsole.Data;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Worklist;

/// <summary>
/// What the worklist is allowed to raise.
///
/// On the existing preferences store rather than a table of its own: these are a handful of
/// flags with sensible defaults, and the app already keeps that kind of setting in
/// AppPreferences (the monitor.* keys work the same way).
///
/// Everything defaults to on. A source that ships silent is a source the player never discovers,
/// and the whole point of the tool is surfacing work that is currently invisible — so the
/// default is "tell me", and turning things off is the deliberate act.
/// </summary>
public class WorklistSettings(AppPreferencesService prefs)
{
    // ── Sources ───────────────────────────────────────────────────────────────

    public const string StandingBuyEnabledKey = "worklist.standing_buy.enabled";

    /// <summary>Whether a whole generator contributes at all.</summary>
    public bool IsSourceEnabled(string generatorId) =>
        prefs.GetBool(SourceKey(generatorId), true);

    public Task SetSourceEnabledAsync(string generatorId, bool enabled) =>
        prefs.SetAsync(SourceKey(generatorId), enabled ? "1" : "0");

    private static string SourceKey(string generatorId) => $"worklist.{generatorId}.enabled";

    public const string CustomerOrdersKey = "worklist.customer_orders.enabled";

    /// <summary>
    /// Whether pending customer orders count as demand.
    ///
    /// <para>Not a generator of its own — it is an input two of them read, so it cannot go through
    /// <see cref="IsSourceEnabled"/>. It replaces the WorklistOrderRules table, which looked like
    /// a rules list but was only ever asked whether any row was enabled: the park and buy location
    /// stored on each rule were written and never read, planning having always used
    /// <see cref="IndustryParkId"/> and <see cref="IndustryBuyLocationId"/> from the Industry tab.
    /// A single flag is what that table actually was.</para>
    /// </summary>
    public bool PlanCustomerOrders => prefs.GetBool(CustomerOrdersKey, true);

    public Task SetPlanCustomerOrdersAsync(bool on) =>
        prefs.SetAsync(CustomerOrdersKey, on ? "1" : "0");

    // ── Standing buy order conditions ─────────────────────────────────────────
    //
    // Separate flags rather than one switch, because they are genuinely different jobs. An
    // outbid order needs a price change; a missing one needs creating; a low one needs topping
    // up. A player who wants to be told about the first two and not the third is not being
    // fussy — that is a real difference in how they trade.

    public bool RaiseMissing   => Cond("missing");
    public bool RaiseOutbid    => Cond("outbid");
    public bool RaiseLow       => Cond("low");
    public bool RaiseExpiring  => Cond("expiring");

    public Task SetConditionAsync(string condition, bool on) =>
        prefs.SetAsync($"worklist.standing_buy.{condition}", on ? "1" : "0");

    private bool Cond(string name) => prefs.GetBool($"worklist.standing_buy.{name}", true);

    // ── Industry ──────────────────────────────────────────────────────────────

    public const string IndustryParkKey = "worklist.industry.park_id";

    /// <summary>
    /// Which Indy Park industry work is planned against. Zero is the &lt;Default&gt; choice: follow
    /// whichever park is flagged default rather than naming one here.
    /// </summary>
    public int IndustryParkId => (int)prefs.GetLong(IndustryParkKey, 0);

    public Task SetIndustryParkAsync(int parkId) =>
        prefs.SetAsync(IndustryParkKey, parkId.ToString());

    /// <summary>
    /// The park to plan against, with &lt;Default&gt; resolved.
    ///
    /// <para>⚠️ Resolved per run, not stored. That is the whole point of the choice: a player who
    /// picks &lt;Default&gt; is saying "follow the default park", so changing which park carries the
    /// star has to move the planning with it. Storing the id at the moment of choosing would
    /// silently pin it to whichever park happened to be default that day.</para>
    ///
    /// <para>Still returns 0 when nothing resolves — no park chosen and none flagged default — and
    /// every caller already treats 0 as "stay silent". The park decides facilities and rigs, and
    /// guessing wrong produces confidently wrong material figures.</para>
    /// </summary>
    public static Task<int> ResolveParkIdAsync(
        AppDbContext db, int configured, CancellationToken ct = default) =>
        configured > 0
            ? Task.FromResult(configured)
            : db.IndyParks.AsNoTracking()
                .Where(p => p.IsDefault)
                .Select(p => p.Id)
                .FirstOrDefaultAsync(ct);

    // ── Where industry buys ───────────────────────────────────────────────────
    //
    // Build rules carry no station of their own — the park decides where a job runs, so the rule
    // has nothing to say about it. But an input the job is short of has to be bought somewhere,
    // and a nullsec build site is not that place. This names the market the shortfalls are
    // ordered at, and the character is whoever is already the market alt there.
    //
    // Blueprints are the exception and are left unassigned on purpose: a BPO or BPC is acquired
    // through contracts, not a market order, so there is no station for the task to belong to.

    public const string IndustryBuyLocationKey     = "worklist.industry.buy_location_id";
    public const string IndustryBuyLocationNameKey = "worklist.industry.buy_location_name";

    public long   IndustryBuyLocationId   => prefs.GetLong(IndustryBuyLocationKey, 0);
    public string IndustryBuyLocationName => prefs.Get(IndustryBuyLocationNameKey) ?? "";

    public async Task SetIndustryBuyLocationAsync(long locationId, string name)
    {
        await prefs.SetAsync(IndustryBuyLocationKey, locationId.ToString());
        await prefs.SetAsync(IndustryBuyLocationNameKey, name);
    }

    // ── How far to look for materials ─────────────────────────────────────────
    //
    // Deciding a job is short of something means deciding what counts as having it. Stock in
    // another region is not stock you can use this week, so counting it would suppress a purchase
    // that genuinely needs making; counting only the build site would call for buying things
    // sitting one structure away. The scope is the line between those, and only the player knows
    // where it sits — for most it is the region they actually operate in.
    //
    // Everywhere is the default because it is the assumption that never invents a purchase. It
    // will under-report, and the setting is how that gets fixed.

    public const string IndustryScopeKey     = "worklist.industry.asset_scope";
    public const string IndustryScopeIdKey   = "worklist.industry.asset_scope_id";
    public const string IndustryScopeNameKey = "worklist.industry.asset_scope_name";

    /// <summary>"Everywhere", "Region" or "System" — the vocabulary InvLevelService already uses,
    /// so the same resolver serves both.</summary>
    public string IndustryScope => prefs.Get(IndustryScopeKey) is { Length: > 0 } s ? s : "Everywhere";

    public long?  IndustryScopeId => prefs.GetLong(IndustryScopeIdKey, 0) is var id && id > 0 ? id : null;
    public string IndustryScopeName => prefs.Get(IndustryScopeNameKey) ?? "";

    /// <summary>How the scope reads in a task's text, e.g. "in Tenerifis".</summary>
    public string IndustryScopeSuffix => IndustryScope == "Everywhere" || IndustryScopeName.Length == 0
        ? "" : $" in {IndustryScopeName}";

    /// <summary>
    /// Whether hangars belonging to corporations that are not the player's own count as material.
    ///
    /// <para>Off, because they are not the player's. Authorising a main in a large alliance corp
    /// hands the app that corp's entire hangar — tens of thousands of rows of other people's
    /// property — and counting it makes every shortfall look filled. Which corporations are the
    /// player's is already recorded: the Corporations tab flags them as personal.</para>
    ///
    /// <para>It is a switch rather than a rule about who is configured, because the answer does
    /// not change with which alts happen to be running jobs. Someone else's stock is someone
    /// else's whoever is asked to build with it.</para>
    /// </summary>
    public const string IncludeNonPersonalCorpsKey = "worklist.industry.include_nonpersonal_corps";

    public bool IncludeNonPersonalCorps => prefs.GetBool(IncludeNonPersonalCorpsKey, false);

    public Task SetIncludeNonPersonalCorpsAsync(bool on) =>
        prefs.SetAsync(IncludeNonPersonalCorpsKey, on ? "1" : "0");

    public async Task SetIndustryScopeAsync(string scope, long? id, string name)
    {
        await prefs.SetAsync(IndustryScopeKey, scope);
        await prefs.SetAsync(IndustryScopeIdKey, (id ?? 0).ToString());
        await prefs.SetAsync(IndustryScopeNameKey, name);
    }

    // ── Job length ────────────────────────────────────────────────────────────
    //
    // How long a single job may be allowed to run. Twenty thousand runs of a component is a
    // legal job and a seven-day one, and the units do not exist until it ends — so a shortfall
    // met by one long job is a shortfall that stays unmet all week. Splitting it into five
    // shorter jobs delivers the first fifth on day two, and lets the work spread across slots
    // and characters instead of parking one slot for the duration.
    //
    // Manufacturing and reactions are set apart because they are run differently: reaction
    // cycles are short and continuous, manufacturing runs long. Zero means no limit, and then
    // only the blueprint's own maximum applies.

    public const string MaxJobDaysMfgKey = "worklist.industry.max_job_days.manufacturing";
    public const string MaxJobDaysRxnKey = "worklist.industry.max_job_days.reaction";
    public const string MaxJobDaysSciKey = "worklist.industry.max_job_days.science";

    public double MaxJobDaysManufacturing => Days(MaxJobDaysMfgKey);
    public double MaxJobDaysReaction      => Days(MaxJobDaysRxnKey);

    /// <summary>
    /// Copying and invention, which run to their own rhythm again. An invention attempt on a
    /// capital blueprint is over eight days on its own, so a limit set for manufacturing would
    /// chop the batch into single attempts and hand out a slot for each.
    /// </summary>
    public double MaxJobDaysScience => Days(MaxJobDaysSciKey);

    public double MaxJobDaysFor(IndustryPool pool) => pool switch
    {
        IndustryPool.Reaction => MaxJobDaysReaction,
        IndustryPool.Science  => MaxJobDaysScience,
        _                     => MaxJobDaysManufacturing,
    };

    // Where copying and invention happen is not a setting. Indy Parks already assigns Blueprint
    // Copying and Blueprint Invention to a structure, the same way it assigns every manufacturing
    // category, and InventionService reads that. Asking a second time here would let the two
    // disagree, and the park is the one that also knows the lab's rigs.

    // ── Decryptors ────────────────────────────────────────────────────────────
    //
    // Held as names rather than type ids because that is what the player thinks in, and the eight
    // generic decryptors have not changed name in a decade. An unrecognised name reads as none,
    // which costs a decryptor's worth of runs and never invents something that cannot be invented.

    public const string ShipDecryptorKey  = "worklist.industry.decryptor.ship";
    public const string OtherDecryptorKey = "worklist.industry.decryptor.other";

    /// <summary>
    /// Ships default to Parity: +3 runs on a blueprint that would otherwise carry one is the
    /// difference between four hulls per success and one, and the 50% it adds to the odds pays for
    /// the decryptor several times over on anything hull-sized.
    /// </summary>
    public string ShipDecryptor => prefs.Get(ShipDecryptorKey) ?? "Parity Decryptor";

    /// <summary>
    /// Everything else defaults to none. A module or a round of ammunition already invents at ten
    /// runs a success, so the marginal runs are worth less than the decryptor, and buying one per
    /// attempt on a line that runs hundreds of attempts is real money.
    /// </summary>
    public string OtherDecryptor => prefs.Get(OtherDecryptorKey) ?? "";

    public Task SetDecryptorAsync(string key, string name) => prefs.SetAsync(key, name);

    public Task SetMaxJobDaysAsync(string key, double days) =>
        prefs.SetAsync(key, days.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));

    // ── Station level deadbands ───────────────────────────────────────────────
    //
    // A level held to the unit is a level that generates a haul every time anything is consumed.
    // Ten units short of a thousand is not worth a trip, and a task that appears, gets ignored,
    // and reappears next refresh trains the reader to ignore the list.
    //
    // Two separate bands because they guard opposite directions and a player may not want them
    // symmetric: being under at a build site costs a stalled job, while being over costs only
    // space. Both are a percentage of the level and both are hysteresis — the trip is triggered
    // at the edge of the band but fills or drains all the way back to the level, so crossing it
    // once does not leave the station sitting permanently at the trigger point.

    public const string RestockBandKey = "worklist.station_levels.restock_band_pct";
    public const string SurplusBandKey = "worklist.station_levels.surplus_band_pct";

    /// <summary>How far below its level a station must fall before it is worth restocking.</summary>
    public double RestockBandPercent => Band(RestockBandKey);

    /// <summary>How far above its level a station must rise before the excess is swept away.</summary>
    public double SurplusBandPercent => Band(SurplusBandKey);

    public Task SetBandAsync(string key, double percent) =>
        prefs.SetAsync(key, Math.Clamp(percent, 0, 100)
            .ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Ten percent by default, and never negative — a negative band would mean acting
    /// before there is anything to act on.</summary>
    private double Band(string key) =>
        prefs.Get(key) is { Length: > 0 } s
        && double.TryParse(s, System.Globalization.NumberStyles.Float,
                           System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? Math.Clamp(v, 0, 100)
            : 10.0;

    /// <summary>
    /// Seven days by default: a week is the span most industrialists already plan around, and it
    /// is short enough that a split actually happens on the long component runs that prompted
    /// this. Negative values are read as no limit rather than rejected, since the only sane
    /// reading of "less than nothing" here is "do not cap".
    /// </summary>
    private double Days(string key)
    {
        var raw = prefs.Get(key);
        if (string.IsNullOrEmpty(raw)) return 7.0;
        return double.TryParse(raw, System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out var d) && d > 0
            ? d : 0.0;
    }
}
