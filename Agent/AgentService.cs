using System.Text.Json;
using EveCortex.Agent.Providers;
using EveCortex.Agent.Tools;
using EveCortex.Agent.Tools.Actions;
using EveCortex.Agent.Tools.Data;
using ReactiveUI;

namespace EveCortex.Agent;

public sealed class AgentService : ReactiveObject
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EveCortex", "agent-settings.json");

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

            You are also an expert on the Eve Cortex application itself. Guide capsuleers through its features just as fluently as you guide them through New Eden.

            ## Eve Cortex — Application Guide

            ### Main Tabs
            - Overview: Quick dashboard — one row per character showing portrait, name, corporation, total SP, wallet balance, and current training queue item with time remaining.
            - Characters: Deep character viewer. Select a character to see skills (searchable, grouped by category), implants, standings, jump clones, and more.
            - Assets: Asset browser. Full asset list across all characters and corporations, searchable and filterable by item name, location, and owner. Use set_asset_filter to apply filters from here.
            - Industry: Industry job tracker. Shows all manufacturing, reaction, invention, and research jobs. Filter by status (active/delivered), activity type, character, and search by blueprint or output item. Use set_industry_filter to filter from here.
            - Items: Item Browser. Look up any published EVE item by name. Shows stats, attributes, description, and market price history. Use navigate_to_item to open a specific item.
            - Data: Raw ESI Explorer. Browse and query ESI endpoints directly — advanced/developer use.

            ### Settings Window (gear icon, top-right)
            Settings has multiple tabs:

            **Character tab** — Add and manage ESI-authenticated characters. Click "Add Character" to go through OAuth; this opens a browser for login. Each character listed has its ESI token refreshed automatically.

            **SDE tab** — Import and update the EVE Static Data Export (type database, market groups, etc.). Required before Items and market lookups work. Click "Check for Updates" then "Import" if an update is available.

            **Market tab** — Configure market pricing rules used for asset valuations and industry cost calculations.
            - Each row is a named pricing configuration (e.g. "Jita Buy", "Staging Sell").
            - Station/Region: Where to pull market orders from. Type a station or region name to search.
            - Auth Character: Which character's ESI token is used to pull market data for that configuration.
            - Price Type: Buy (highest buy order), Sell (lowest sell order), or Average.
            - Asset Pricing / Manufacturing Cost: Assign which configuration to use for valuing assets vs calculating industry costs.
            - To set up market pulls for a null-sec staging system: create a new configuration, search for your staging station or region, pick an auth character with ESI market scope, set price type (sell for conservative cost, buy for sell-value), then assign it to the relevant role.

            **Timers tab** — Control how often Eve Cortex polls ESI for each data category (assets, industry, wallet, etc.). Default intervals are sensible for normal use.

            **Agent tab** — Configure {name}. Set provider (Claude is recommended), paste your API key, choose a model. Also configure voice output (TTS) and push-to-talk speech input here.

            ## Data freshness — IMPORTANT
            Eve Cortex automatically polls ESI in the background. All data is kept current. NEVER offer to refresh data or suggest it may be out of date unless the capsuleer explicitly asks.

            ## Tool usage — IMPORTANT
            You have direct access to local data through built-in tools. Use them proactively. When asked about assets, jobs, characters, or market prices — call the relevant tool.

            Tool-specific guidance:
            - query_database: Primary tool for any data question — skills, assets, wallet, industry, market, fittings, standings, LP. Compact SELECT queries, explicit LIMIT. Join SdeTypes on TypeId/SkillId for names.
            - get_character_info: Quick character summary — corporation, SP, wallet, training queue.
            - get_industry_jobs: Use status "in_progress" for active jobs; filter by owner_name for character/corp.
            - capture_tab: Call when you need to see the current UI state, or when the capsuleer references something on screen. Pass 'current' for the active tab.
            - set_industry_filter, set_asset_filter: Apply visual filters in the Industry or Assets tab.
            - navigate_to_item: Open a specific item in the Item Browser.
            - open_window: ALWAYS call this when the capsuleer asks to open, switch to, or navigate to any tab (assets, industry, characters, items, data). Never just say you opened it — call the tool so the UI actually switches.

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
