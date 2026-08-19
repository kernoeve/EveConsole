using EveConsole.Agent;
using EveConsole.Services;
using ReactiveUI;

namespace EveConsole.ViewModels;

public class SettingsViewModel : ReactiveObject
{
    public CharacterViewModel             CharacterVm     { get; }
    public SdeViewModel                   SdeVm           { get; }
    public MarketSettingsViewModel        MarketVm        { get; }
    public TimerSettingsViewModel         TimerVm         { get; }
    public AgentSettingsViewModel         AgentVm         { get; }
    public PriceHistorySettingsViewModel  PriceHistoryVm  { get; }
    public AlertSettingsViewModel         AlertsVm        { get; }
    public PollingSettingsViewModel       PollingVm       { get; }
    public CorpTop10SettingsViewModel     CorpTop10Vm     { get; }
    public DatabaseSettingsViewModel      DatabaseVm      { get; }
    public UpdateViewModel                UpdateVm        { get; }
    public SlackSettingsViewModel         SlackVm         { get; }
    public GameLogSettingsViewModel       GameLogVm       { get; }
    public ChatLogSettingsViewModel       ChatLogVm       { get; }
    public ZkillboardSettingsViewModel    ZkbVm           { get; }
    public MapStatsSettingsViewModel      MapStatsVm      { get; }
    public OtherSettingsViewModel         OtherVm         { get; }
    public DataRetentionSettingsViewModel RetentionVm     { get; }

    /// <summary>
    /// Shared with the Worklist tool rather than a second instance of its own.
    ///
    /// <para>The Industry tab here edits which characters may be given jobs; the Worklist tool
    /// reads that same list to plan against. Two view models over one table would each hold their
    /// own copy of the grid and neither would see the other's edits until a reload.</para>
    /// </summary>
    public WorklistIndustryViewModel      IndustryVm      { get; }

    public SettingsViewModel(
        WorklistIndustryViewModel     industryVm,
        CharacterViewModel            characterVm,
        SdeViewModel                  sdeVm,
        UpdateViewModel               updateVm,
        MarketSettingsViewModel       marketVm,
        TimerSettingsViewModel        timerVm,
        AgentService                  agentService,
        PriceHistorySettingsViewModel priceHistoryVm,
        AlertSettingsViewModel        alertsVm,
        PollingSettingsViewModel      pollingVm,
        CorpTop10SettingsViewModel    corpTop10Vm,
        DatabaseSettingsViewModel     databaseVm,
        SlackSettingsViewModel        slackVm,
        GameLogSettingsViewModel      gameLogVm,
        ChatLogSettingsViewModel      chatLogVm,
        ZkillboardSettingsViewModel   zkbVm,
        MapStatsSettingsViewModel     mapStatsVm,
        OtherSettingsViewModel        otherVm,
        DataRetentionSettingsViewModel retentionVm,
        TtsService?                   tts     = null,
        SpeechInputService?           speech  = null,
        GlobalHotkeyService?          hotkey  = null)
    {
        SlackVm        = slackVm;
        GameLogVm      = gameLogVm;
        ChatLogVm      = chatLogVm;
        ZkbVm          = zkbVm;
        MapStatsVm     = mapStatsVm;
        CharacterVm    = characterVm;
        SdeVm          = sdeVm;
        UpdateVm       = updateVm;
        MarketVm       = marketVm;
        TimerVm        = timerVm;
        AgentVm        = new AgentSettingsViewModel(agentService, tts, speech, hotkey);
        PriceHistoryVm = priceHistoryVm;
        AlertsVm       = alertsVm;
        PollingVm      = pollingVm;
        CorpTop10Vm    = corpTop10Vm;
        DatabaseVm     = databaseVm;
        OtherVm        = otherVm;
        RetentionVm    = retentionVm;
        IndustryVm     = industryVm;
    }
}
