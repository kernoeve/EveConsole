using System.Text.Json;
using EveConsole.Agent.Providers;
using EveConsole.Agent.Tools;
using EveConsole.Agent.Tools.Actions;
using EveConsole.Agent.Tools.Data;
using EveConsole.Services;
using ReactiveUI;

namespace EveConsole.Agent;

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

    /// <summary>Opens an entity viewer. Set alongside <see cref="EntityBrowser"/>.</summary>
    public Action<Services.EntityKind, long, string>? NavigateEntityCallback  { get; set; }

    /// <summary>
    /// Supplied by the host so the entity tool can resolve names the same way the viewers
    /// do. Absent in contexts that have no database, in which case the tool is not offered.
    /// </summary>
    public Services.EntityBrowserService?             EntityBrowser           { get; set; }

    /// <summary>Resolves system and region names for the map tool. Same contract as
    /// <see cref="EntityBrowser"/> — absent means the tool is not offered.</summary>
    public Services.UniverseMapService?               MapService              { get; set; }

    /// <summary>The ESI client, for the esi_call tool. Same contract — absent means not offered.</summary>
    public Api.EsiClient?                             Esi                     { get; set; }
    public Action<string?, string?, string?>?          FilterAssetsCallback   { get; set; }
    public Action<string?, string?, string?, string?>? FilterIndustryCallback { get; set; }
    public Action<string>?                             SelectCharacterCallback { get; set; }

    // (tab, marketSource, historyRegion) -> status message. Set by MainWindow.
    public Func<string?, string?, string?, string>?    ConfigureItemBrowserCallback { get; set; }

    // Tab screenshot callback: (tabName) → (pngBytes, description). Set by MainWindow.
    public Func<string, Task<(byte[]? image, string description)>>? CaptureTabCallback { get; set; }

    /// <summary>
    /// Opens an agent-authored grid tab: (title, caption, columns, rows) -> status message.
    ///
    /// <para>⚠️ Unlike every other tab callback, each invocation opens a NEW tab rather than
    /// returning to an existing one. These are answers, and a second answer must not overwrite
    /// the first.</para>
    /// </summary>
    public Func<string, string, string[], List<string[]>, string>? ShowTableCallback { get; set; }

    /// <summary>Opens an agent-authored document tab: (title, markdown) -> status message.</summary>
    public Func<string, string, string>? ShowDocumentCallback { get; set; }

    // ── UI context provider (set by MainWindow, called before each StreamAsync) ──
    public Func<string?>? ContextProvider { get; set; }

    /// <param name="tableIndex">
    /// Every table name, from <see cref="AgentSchema.Index"/>. Roughly 1k tokens and worth every
    /// one of them: without it the agent knows only the tables somebody thought to write down, and
    /// treats a question whose answer lives anywhere else as a question with no data behind it.
    /// Sits inside the cached prefix, so it is paid for in full once and at a tenth after that.
    /// </param>
    public static string BuildSystemPrompt(AgentSettings settings, string? tableIndex = null)
    {
        var name = string.IsNullOrWhiteSpace(settings.AgentName) ? AgentSettings.DefaultAgentName : settings.AgentName.Trim();

        // ── The person, and their own standing instructions ─────────────────
        //
        // The instructions below say "the capsuleer" throughout, as a role. When a name is set,
        // one line up front binds the role to the person; the instructions are not rewritten.
        var userName = string.IsNullOrWhiteSpace(settings.UserName) ? AgentSettings.DefaultUserName : settings.UserName.Trim();
        var personal = userName.Equals(AgentSettings.DefaultUserName, StringComparison.OrdinalIgnoreCase)
            ? ""
            : $"The capsuleer you are talking to is {userName}. Address them by that name, and wherever " +
              $"these instructions say \"the capsuleer\" they mean {userName}.";

        // ⚠️ Last, and declared to win. Everything above is the application's general guidance;
        // this is what THIS person has said about how they want to be understood — "when I say
        // home I mean Jita 4-4" — and it has to beat the general case or it is
        // useless. Inside the cached prefix, so a change costs one re-cache, not one per turn.
        var guidance = string.IsNullOrWhiteSpace(settings.UserGuidance)
            ? ""
            : $"## {userName}'s standing instructions — these OVERRIDE everything above\n" +
              "Follow these even where they contradict the guidance above. When one says what a word " +
              "or a name means, that is what it means every time it is said. To add or change one, " +
              "use update_guidance.\n\n" + settings.UserGuidance.Trim();

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
            You are {name}, an AI companion integrated into EVE Console — a local capsuleer management application for EVE Online.
            {personal}

            You have comprehensive knowledge of EVE Online: industry, market dynamics, ship fittings, sovereignty warfare, PvP, exploration, missions, skills, implants, the player-driven economy, lore, and the complex political landscape of New Eden.

            You are also an expert on the EVE Console application itself. The reference below describes every tool — its purpose, how to use it, and the concepts behind it. When the capsuleer asks what a tool does, what they are looking at, or how to accomplish something in EVE Console, answer from this understanding and guide them concretely. Do NOT default to taking a screenshot and narrating what you see — screenshots are only for reading specific current on-screen values you cannot obtain from the data tools.

            {AppKnowledge.Guide}

            {tableIndex}

            {AgentDataNotes.Notes}

            ## Answering questions about the capsuleer's data
            Nearly every question about what the capsuleer HAS, OWNS, IS DOING or HAS DONE is a
            database question, and the database is far larger than the notes on the query_database
            tool describe. Before concluding that something cannot be answered, look for it: the
            table index above is the complete list, and describe_tables gives you the columns.
            "I do not have that data" is almost always wrong — say it only after looking.

            Work in this order:
            1. Find candidate tables in the index above.
            2. describe_tables on the few you intend to use. Do not guess column names — an
               invented one does not reliably fail, it can come back as a column full of its own
               name, which reads like data.
            3. Then write the query.

            ## Valuing things — there is ONE right way, use it every time
            ⚠️ The app already holds a value for essentially every item. Never send the capsuleer
            to zKillboard, Fuzzwork or any other site for a number this database can produce, and
            never invent your own valuation — the same question must give the same answer twice.

            THE MARKET VALUE. This is the default and the answer unless asked for something else:

              MarketDefaultSettings (one row) names the source and the side:
                AssetValueConfigId   -> which MarketPricingConfigs row
                AssetValuePriceType  -> 'Sell', 'Buy' or 'Midpoint'
              MarketItemPrices holds the stored daily price per item:
                columns are ConfigId, TypeId, BuyPrice, SellPrice, Midpoint, FromMarketData

            So: join MarketItemPrices on TypeId, take the column AssetValuePriceType names, and

            ⚠️ ALWAYS filter ConfigId to AssetValueConfigId. There are several configured price
            sources and MarketItemPrices holds a row PER SOURCE per item. An unfiltered join
            returns one row per source and multiplies every SUM by however many exist — this has
            already produced totals several times too large, which looked plausible and were not.

            ⚠️ Use these stored prices. Do NOT compute from MarketRawOrders — that is the raw order
            book, it is far slower, and it bypasses the percentile filtering, the buy/sell/midpoint
            choice and the build-cost gap fill that make the stored number the app's own answer.

            ⚠️ The stored price ALREADY includes the "% over build cost" fill for items with no
            market. Rows carrying FromMarketData = false are that fill. Do not add a markup
            yourself and do not exclude those rows as though they were missing.

            FALLBACK. Where an item has no market price at all, use ContractPrices.BestPrice.
            Say when you have fallen back to it.

            OTHER VALUATIONS, only when the question asks for them: BUILD COST is what it costs the
            capsuleer to MAKE something, which is not its market price; contract pricing covers
            blueprint copies per run and by ME.

            ## "Value" of a kill
            Unqualified, the value of a killmail is the WHOLE loss: the ship hull plus everything
            destroyed and everything dropped. Not the hull alone, and not the contents alone —
            asking the same question twice and summing different parts of it is how one region's
            24 hours came back as 300B, then 20B, then 202B.

            Hull-only, dropped-only or destroyed-only are answers to questions that SAY so. If the
            capsuleer just says "value", give the total.

            ## Data freshness — IMPORTANT
            EVE Console automatically polls ESI in the background. All data is kept current. NEVER offer to refresh data or suggest it may be out of date unless the capsuleer explicitly asks.

            ## Tool usage — IMPORTANT
            You have direct access to local data through built-in tools. Use them proactively. When asked about assets, jobs, characters, or market prices — call the relevant tool.

            Between tool calls, write at most one short sentence about what you are doing, and
            often nothing. Never narrate a query that failed or how you fixed it — fix it and
            retry. Everything you write here is shown in the chat and read aloud; "the issue was
            with the CTE referencing kd.KillMailId" is not something anyone wants to hear.

            Tool-specific guidance:
            - query_database: Primary tool for any data question — skills, assets, wallet, industry, market, fittings, standings, LP. Compact SELECT queries, explicit LIMIT. Join SdeTypes on TypeId/SkillId for names.
            - get_character_info: Quick character summary — corporation, SP, wallet, training queue.
            - get_industry_jobs: Use status "in_progress" for active jobs; filter by owner_name for character/corp.
            - capture_tab: Only when you must see specific current on-screen values (a chart, a rendered layout) that the data tools cannot give you — NOT to explain what a tool is for. Pass 'current' for the active tab.
            - set_industry_filter, set_asset_filter: Apply visual filters in the Industry or Assets tab.
            - navigate_to_item: Open a specific item in the Item Browser.
            - open_window: ALWAYS call this when the capsuleer asks to open, switch to, or navigate to any tool. Never just say you opened it — call the tool so the UI actually switches.
            - manage_alarms: Whenever the capsuleer asks to be TOLD or ALERTED when something happens, set up an alarm with this rather than answering once. An alarm keeps working after this conversation ends; an intention to watch does not.
            - esi_call: For what the database does not hold — anything CURRENT about people outside the capsuleer's own corporations. "Are they still in the corp", "where did they go", public details of a stranger: get the ids from the database, then ask ESI. Never for data the database already has.
            - set_destination: ALWAYS call this when the capsuleer asks to set a destination, route, or autopilot to a system — "set destination Jita", "take me to Amarr". Never just say it is done. If it tells you several characters are online, ask which one; do not pick.
            - update_guidance: When the capsuleer tells you what a word means, who someone is, or how to behave FROM NOW ON — "when I say home I mean…", "remember that…", "my main is…" — record it with this so it holds in every conversation, then confirm briefly. Never for one-off requests or things you found out yourself.

            ## Where a long answer goes — IMPORTANT
            You are in a narrow side panel whose contents are carried in the history of every later
            turn and, when speech is on, read out loud. A listing belongs in a tab, not in the chat.

            - show_query: the answer is a list of records that a query can produce in its finished
              form. The rows go straight from the database to the tab and never enter your
              context, so a thousand rows cost you no more than ten. This is the DEFAULT for a
              listing. Write the query to produce the final table — aliases as headers, names
              joined in, ISK and dates formatted, ordered for the reader — because you will not
              see the rows to fix them afterwards. For a summary figure, run a separate small
              query_database.
            - show_table: the rows need shaping you can only do by hand — merging results from
              several queries, adding a column you computed, annotating. Use it even for five
              rows rather than typing them into the chat, but keep it to a few dozen: every row
              is written into the call, and a long one is cut off before it can open.
            - show_document: the answer is a report rather than a reply — sections, an analysis, a
              plan, a comparison, anything worth keeping or re-reading.

            Each call opens a NEW tab, so you may open several in one turn and nothing is lost.

            ⚠️ Having opened one, do NOT then write its contents into your reply as well. That
            undoes the entire point. Say what you found in a sentence or two, name the tab, and
            stop — "Six of them are still in the corp; they are in the Capital Buyers tab."

            Answer in the chat when the answer is short and conversational: one figure, a yes or
            no, a name, a sentence of explanation. A single value is not a table.

            ## When an alarm fires
            You will sometimes receive a message beginning "ALARM FIRED". That is an alarm the capsuleer set up reaching you — it is the prompt itself, not a request to investigate. Report what it says in a sentence or two, using the detail supplied. Do not call tools to verify it, and do not ask what they would like you to do about it.

            {verbosityInstruction}

            ## Tone and format
            You are displayed in a narrow side panel. Prefer plain text over markdown.

            ## Time
            Each of the capsuleer's messages begins with [the moment it was sent, EVE time], and the
            current app state says what time it is now. Use the two: a question from five weeks ago
            is not the same conversation as one from five minutes ago, and "since we last spoke"
            has an answer. Do not put a timestamp on your own replies.

            ## ISK figures
            Round to a short form by default: 382.9B, 1.2M, 45.7K. That is what a capsuleer says
            out loud and it is what your answer is often read aloud as.

            Give the exact figure ONLY when asked for it, or when the precision is the point —
            a wallet balance being reconciled, a contract price being matched. Never give both:
            "382,885,953,507.99 ISK (~382.9 billion)" is the short form with a long number read
            out in front of it, which is the worst of the two.

            ## System names
            Write null-security system names exactly as they appear — C-FD0D, Y-ORBJ, 6-IAFR. Do not spell them out in your reply; when spoken aloud they are expanded for you.
            They are said character by character, with the hyphen pronounced "tac": C-FD0D is "C tac F D zero D", 6-IAFR is "six tac I A F R". Use that form only if the capsuleer asks how a name is pronounced, or if you are spelling one out on purpose.

            Speak as {name}: calm, knowledgeable, slightly formal, with subtle warmth. You may address the capsuleer respectfully. Occasionally reference the broader state of New Eden to add colour, but keep the focus on what is useful to the capsuleer right now.

            {guidance}
            """;
    }

    public AgentService() => Load();

    /// <summary>
    /// Supplies the alarm tool. Set before <see cref="Initialize"/> — without it the agent
    /// simply has no alarm tool, rather than a broken one.
    /// </summary>
    public Func<Tools.IAgentTool>? AlarmToolFactory { get; set; }

    /// <summary>
    /// Where tool calls and token usage are recorded. Set before <see cref="Initialize"/>; absent
    /// means the agent runs unmeasured rather than broken.
    /// </summary>
    public AgentTelemetryService? Telemetry { get; set; }

    /// <summary>
    /// The database as generated from the entity model. Set before <see cref="Initialize"/>;
    /// without it the agent keeps the query tool but loses discovery, which is the old behaviour
    /// rather than a broken one.
    /// </summary>
    public AgentSchema? Schema { get; set; }

    /// <summary>
    /// Raised with a tool's name as it starts, so the panel can say what is happening rather than
    /// showing nothing while the agent works through six round trips.
    /// </summary>
    public Action<string>? ToolActivity { get; set; }

    public void Initialize(string dbConnectionString)
    {
        Tools =
        [
            // ── Generic data access ───────────────────────────────────────────
            new QueryDatabaseTool(dbConnectionString, Schema),

            // ── Specialised data query tools — WITHDRAWN ──────────────────────
            //
            // ⚠️ Not offered any more. GetAssets, GetIndustryJobs, GetCharacterInfo,
            // GetMarketPrices and SearchItems are thin wrappers around one or two fixed SELECTs,
            // and they were doing harm rather than saving effort.
            //
            // Measured: asked how many titans the capsuleer owned, the agent called get_assets
            // with a name search, got back exactly 50 rows — its LIMIT, reported as a bare array
            // with nothing to say it had been cut off — then searched four hulls individually and
            // added the totals together. The answer was far too high and nothing in the result
            // could have revealed it. Asked to check again it wrote SQL, which was right, because
            // query_database reports truncation and lets the database do the counting.
            //
            // They also cost tokens on every single turn: each one's schema ships in every
            // request whether or not it is used. With the table index and describe_tables the
            // agent can reach the same data and more, so the trade no longer holds.
            //
            // The files are kept rather than deleted — the queries in them are a decent record of
            // how these questions were meant to be answered, and the telemetry will show whether
            // anything actually regresses without them.

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

            // ── Output tools ──────────────────────────────────────────────────
            //
            // Where a long answer goes instead of into the conversation. Rows and reports left in
            // the chat are carried in the history of every later turn, and are read aloud when
            // speech is on — which for a fifty-row listing is unusable.
            new ShowTableTool(
                (title, caption, columns, rows) =>
                    ShowTableCallback?.Invoke(title, caption, columns, rows)
                    ?? "Tabs are not available, so the table could not be shown."),
            new ShowDocumentTool(
                (title, markdown) => ShowDocumentCallback?.Invoke(title, markdown)
                                     ?? "Tabs are not available, so the document could not be shown."),
            // What the capsuleer has told the agent to remember about how to understand them.
            new UpdateGuidanceTool(this),
            // The rows go from the database straight to the grid and never through the model —
            // the same grid show_table opens, fed from the other side.
            new ShowQueryTool(
                (title, caption, columns, rows) =>
                    ShowTableCallback?.Invoke(title, caption, columns, rows)
                    ?? "Tabs are not available, so the table could not be shown.",
                Schema),
        ];

        if (EntityBrowser is { } entities)
            Tools = [.. Tools, new NavigateToEntityTool(entities,
                (kind, id, name) => NavigateEntityCallback?.Invoke(kind, id, name))];

        if (MapService is { } mapService)
            Tools = [.. Tools, new OpenMapTool(mapService), new SetMapOverlayTool()];

        // Direct ESI access, for what the database does not hold — where a stranger is NOW; and
        // the one in-game action so far, kept as its own tool rather than a POST the caller allows.
        if (Esi is { } esi)
            Tools = [.. Tools, new EsiCallTool(esi), new SetDestinationTool(esi)];

        // Discovery. Offered only when the schema was built — a describe_tables with nothing
        // behind it would be a tool that always answers "I do not know".
        if (Schema is { } schema)
            Tools = [.. Tools, new DescribeTablesTool(schema)];

        if (AlarmToolFactory?.Invoke() is { } alarmTool)
            Tools = [.. Tools, alarmTool];

        // ⚠️ Last, after every conditional tool above has been added. Wrapping earlier would
        // leave the map, entity and alarm tools unmeasured — and they would look simply unused
        // in the telemetry rather than uninstrumented, which is the more misleading of the two.
        if (Telemetry is { } telemetry)
            Tools = [.. Tools.Select(t =>
                (IAgentTool)new TelemetryToolDecorator(t, telemetry, name => ToolActivity?.Invoke(name)))];

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

    /// <summary>
    /// Replaces the capsuleer's standing instructions and publishes the change.
    ///
    /// <para>⚠️ Published as a NEW Settings object. WhenAnyValue ignores a notification whose value
    /// is the same reference, so mutating the current one in place and raising would reach nobody
    /// — and the open Settings tab, which rebuilds its whole object on Save, would then write the
    /// old text straight back over the agent's change.</para>
    /// </summary>
    public void UpdateGuidance(string guidance)
    {
        var next = _settings.Clone();
        next.UserGuidance = guidance;
        _settings = next;
        Save();
        this.RaisePropertyChanged(nameof(Settings));
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
