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

    public SettingsViewModel(
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
        TtsService?                   tts     = null,
        SpeechInputService?           speech  = null,
        GlobalHotkeyService?          hotkey  = null)
    {
        SlackVm        = slackVm;
        GameLogVm      = gameLogVm;
        ChatLogVm      = chatLogVm;
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
    }
}
