namespace EveConsole.Services;

/// <summary>
/// Destinations for the clickable readouts in the title bar. Typed wrapper over
/// AppPreferencesService, same shape as ZkillboardSettings / MonitoringSettings.
/// </summary>
public sealed class UiLinkSettings(AppPreferencesService prefs)
{
    public const string KeyEveTimeUrl = "ui.eve_time_url";

    /// <summary>Suggested destinations for the EVE clock. The user is not limited to
    /// these — any URL they type is stored verbatim.</summary>
    public const string EveOnlineTimeUrl  = "https://www.eveonlinetime.com/";
    public const string NakamuraLabsUrl   = "https://time.nakamura-labs.com/";

    /// <summary>Fixed, not configurable: CCP's own service-status page is the only
    /// meaningful destination for the Tranquility indicator.</summary>
    public const string ServerStatusUrl = "https://status.eveonline.com/";

    public string EveTimeUrl
    {
        get
        {
            var stored = prefs.Get(KeyEveTimeUrl);
            return string.IsNullOrWhiteSpace(stored) ? EveOnlineTimeUrl : stored.Trim();
        }
        set
        {
            var url = (value ?? "").Trim();
            _ = prefs.SetAsync(KeyEveTimeUrl, string.IsNullOrEmpty(url) ? EveOnlineTimeUrl : url);
        }
    }
}
