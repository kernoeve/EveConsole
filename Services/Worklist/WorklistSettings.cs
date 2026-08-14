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
    /// Which Indy Park industry work is planned against. Zero means unset, and the generator
    /// stays silent rather than guessing — the park decides facilities and rigs, and guessing
    /// wrong produces confidently wrong material figures.
    /// </summary>
    public int IndustryParkId => (int)prefs.GetLong(IndustryParkKey, 0);

    public Task SetIndustryParkAsync(int parkId) =>
        prefs.SetAsync(IndustryParkKey, parkId.ToString());

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

    public double MaxJobDaysManufacturing => Days(MaxJobDaysMfgKey);
    public double MaxJobDaysReaction      => Days(MaxJobDaysRxnKey);

    public double MaxJobDaysFor(IndustryPool pool) =>
        pool == IndustryPool.Reaction ? MaxJobDaysReaction : MaxJobDaysManufacturing;

    public Task SetMaxJobDaysAsync(string key, double days) =>
        prefs.SetAsync(key, days.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));

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
