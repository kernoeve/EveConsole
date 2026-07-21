using System.Text.Json;
using EveCortex.Agent.Providers;
using EveCortex.Agent.Tools;
using EveCortex.Agent.Tools.Actions;
using EveCortex.Agent.Tools.Data;
using EveCortex.Services;
using ReactiveUI;

namespace EveCortex.Agent;

public sealed class AgentService : ReactiveObject
{
    private static readonly string SettingsPath = Path.Combine(
        AppConfig.AppDataDir, "agent-settings.json");

    private static readonly JsonSerializerOptions _jsonOpts = new() { WriteIndented = true };

    private AgentSettings _settings = new();

    public AgentSettings Settings => _settings;

    public IAgentProvider? Provider { get; private set; }

    public IReadOnlyList<IAgentTool>? Tools { get; private set; }

    // ── Action events / callbacks (wired by MainWindow in TryStartup) ──────────
    public event Action<string>? WindowOpenRequested;
    public event Action?         DataRefreshRequested;

    // Called by AgentPanelViewModel for client-side navigation intent detection.
    public void RequestWindowOpen(string name) => WindowOpenRequested?.Invoke(name);

    // Targeted navigation/filter callbacks — set by MainWindow after startup.
    public Action<int, string>?                       NavigateItemCallback    { get; set; }
    public Action<string?, string?, string?>?          FilterAssetsCallback   { get; set; }
    public Action<string?, string?, string?, string?>? FilterIndustryCallback { get; set; }
    public Action<string>?                             SelectCharacterCallback { get; set; }

    // (tab, marketSource, historyRegion) -> status message. Set by MainWindow.
    public Func<string?, string?, string?, string>?    ConfigureItemBrowserCallback { get; set; }

    // Tab screenshot callback: (tabName) → (pngBytes, description). Set by MainWindow.
    public Func<string, Task<(byte[]? image, string description)>>? CaptureTabCallback { get; set; }

    // ── UI context provider (set by MainWindow, called before each StreamAsync) ──
    public Func<string?>? ContextProvider { get; set; }

    public static string BuildSystemPrompt(AgentSettings settings)
    {
        var name = string.IsNullOrWhiteSpace(settings.AgentName) ? AgentSettings.DefaultAgentName : settings.AgentName.Trim();

        var verbosityInstruction = settings.Verbosity switch
        {
            VerbositySetting.Concise  =>
                "## Response length\n" +
                $"Be brief and direct. Aim for 1-3 sentences. Use a bullet list only when listing 4+ distinct items. " +
                $"Never pad answers. If the answer is one word, give one word.",
            VerbositySetting.Detailed =>
                "## Response length\n" +
                $"Be thorough. Provide context, reasoning, and relevant background. Walk through multi-step answers step by step. " +
                $"Elaborate on implications when they may not be obvious to the capsuleer.",
            _ => // Balanced
                "## Response length\n" +
                $"Be concise but complete. Aim for 2-5 sentences. Use bullets only when the answer is inherently a list. " +
                $"Never pad — stop when the answer is complete.",
        };

        return $"""
            You are {name}, an AI companion integrated into Eve Cortex — a local capsuleer management application for EVE Online.

            You have comprehensive knowledge of EVE Online: industry, market dynamics, ship fittings, sovereignty warfare, PvP, exploration, missions, skills, implants, the player-driven economy, lore, and the complex political landscape of New Eden.

            You are also an expert on the Eve Cortex application itself. The reference below describes every tool — its purpose, how to use it, and the concepts behind it. When the capsuleer asks what a tool does, what they are looking at, or how to accomplish something in Eve Cortex, answer from this understanding and guide them concretely. Do NOT default to taking a screenshot and narrating what you see — screenshots are only for reading specific current on-screen values you cannot obtain from the data tools.

            {AppKnowledge.Guide}

            ## Data freshness — IMPORTANT
            Eve Cortex automatically polls ESI in the background. All data is kept current. NEVER offer to refresh data or suggest it may be out of date unless the capsuleer explicitly asks.

            ## Tool usage — IMPORTANT
            You have direct access to local data through built-in tools. Use them proactively. When asked about assets, jobs, characters, or market prices — call the relevant tool.

            Tool-specific guidance:
            - query_database: Primary tool for any data question — skills, assets, wallet, industry, market, fittings, standings, LP. Compact SELECT queries, explicit LIMIT. Join SdeTypes on TypeId/SkillId for names.
            - get_character_info: Quick character summary — corporation, SP, wallet, training queue.
            - get_industry_jobs: Use status "in_progress" for active jobs; filter by owner_name for character/corp.
            - capture_tab: Only when you must see specific current on-screen values (a chart, a rendered layout) that the data tools cannot give you — NOT to explain what a tool is for. Pass 'current' for the active tab.
            - set_industry_filter, set_asset_filter: Apply visual filters in the Industry or Assets tab.
            - navigate_to_item: Open a specific item in the Item Browser.
            - open_window: ALWAYS call this when the capsuleer asks to open, switch to, or navigate to any tool. Never just say you opened it — call the tool so the UI actually switches.

            {verbosityInstruction}

            ## Tone and format
            You are displayed in a narrow side panel. Prefer plain text over markdown. Format ISK values with commas and two decimal places (e.g. 1,234,567.89 ISK).

            Speak as {name}: calm, knowledgeable, slightly formal, with subtle warmth. You may address the capsuleer respectfully. Occasionally reference the broader state of New Eden to add colour, but keep the focus on what is useful to the capsuleer right now.
            """;
    }

    public AgentService() => Load();

    public void Initialize(string dbConnectionString)
    {
        Tools =
        [
            // ── Generic data access ───────────────────────────────────────────
            new QueryDatabaseTool(dbConnectionString),

            // ── Specialised data query tools ──────────────────────────────────
            new GetAssetsTool(dbConnectionString),
            new GetIndustryJobsTool(dbConnectionString),
            new GetCharacterInfoTool(dbConnectionString),
            new GetMarketPricesTool(dbConnectionString),
            new SearchItemsTool(dbConnectionString),

            // ── UI action tools ───────────────────────────────────────────────
            new OpenWindowTool(name => WindowOpenRequested?.Invoke(name)),
            new RefreshDataTool(() => DataRefreshRequested?.Invoke()),
            new NavigateToItemTool(dbConnectionString,
                (id, name) => NavigateItemCallback?.Invoke(id, name)),
            new ConfigureItemBrowserTool(
                (tab, src, reg) => ConfigureItemBrowserCallback?.Invoke(tab, src, reg)
                                   ?? "Item Browser is not available."),
            new SetAssetFilterTool(
                (loc, ch, item) => FilterAssetsCallback?.Invoke(loc, ch, item)),
            new SetIndustryFilterTool(
                (act, status, search, owner) => FilterIndustryCallback?.Invoke(act, status, search, owner)),
            new SelectCharacterTool(
                name => SelectCharacterCallback?.Invoke(name)),
            new CaptureTabTool(
                tabName => CaptureTabCallback?.Invoke(tabName)
                           ?? Task.FromResult<(byte[]?, string)>((null, ""))),
        ];
        this.RaisePropertyChanged(nameof(Tools));
    }

    public void Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                _settings = JsonSerializer.Deserialize<AgentSettings>(
                    File.ReadAllText(SettingsPath)) ?? new();
        }
        catch { _settings = new(); }

        RebuildProvider();
    }

    public void Configure(AgentSettings settings)
    {
        _settings = settings;
        RebuildProvider();
        Save();
        this.RaisePropertyChanged(nameof(Settings));
        this.RaisePropertyChanged(nameof(Provider));
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(_settings, _jsonOpts));
        }
        catch { /* non-fatal */ }
    }

    private void RebuildProvider()
    {
        Provider = _settings.Provider switch
        {
            AgentProviderType.Claude when !string.IsNullOrWhiteSpace(_settings.ClaudeApiKey)
                => new ClaudeProvider(_settings.ClaudeApiKey, _settings.ClaudeModel),
            _ => null,
        };
    }
}
