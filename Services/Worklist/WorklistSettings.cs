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
}
