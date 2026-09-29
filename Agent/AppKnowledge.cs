namespace EveConsole.Agent;

/// <summary>
/// In-depth reference describing every EVE Console tool — its purpose, how to use it,
/// and the key concepts behind it. Injected into the agent's system prompt so the
/// agent can explain and guide the capsuleer from real understanding rather than by
/// describing a screenshot. Keep this current when tools are added or changed.
/// </summary>
public static class AppKnowledge
{
    public const string Guide = """
        # EVE Console — Tool Reference

        EVE Console's data lives in a local database (SQLite, or a shared PostgreSQL server) kept
        current by background ESI polling. The left sidebar opens tools as tabs, grouped into
        General, Assets, Structures / Navigation, Industry, Market / Trade, Finance, Corp /
        Interactions, Communication and Data / Logs; the gear icon (top-right) opens Settings.
        Quoted names are the exact labels of buttons, columns and menu items, right-click menus
        included — cite them when the capsuleer asks how to do something.

        ## How data flows (under the hood)
        - Build costs: the manufacturing cost of every craftable item, stored and updated as
          prices move; they feed the industry and valuation tools.

        ### Overview
        The landing dashboard. An activity summary across your authenticated characters and
        personal corporations — income/expense charts (market sales, bounties, contracts, taxes,
        fees) — an EVE Online news feed, and an Alerts panel (set in Settings > Alerts). Each
        alert is a link to its tool: skill queue empty, paused or ending soon (that character's
        Skills), items moved to Asset Safety, a standing project no longer active (Corp Activity
        > Standing Projects).

        ### Worklist
        A to-do list across your characters and personal corps, rebuilt on each refresh: buying,
        hauling, building and more, with blocked and snoozed tasks behind toggles. Tabs:
        Worklist, Station Needs and Item Needs (the work by place and by item), Bottlenecks, Final
        Products, and Config, which switches each source on or off: industry jobs, invention and
        copying, material purchases, logistics, refining, standing buy orders, inventory levels,
        standing projects, skill queues, asset safety and Order Tracker customer orders.

        ### Characters (Character Viewer)
        Deep per-character viewer; pick a character from the dropdown. Tabs: Skills (groups and
        levels, and the training queue with each skill's remaining time and the queue's total;
        skill names open the Item Browser), Attributes (with neural remap info), Clones (jump
        clones and implants), Medals, Titles and Standings. Useful when a character is not
        logged into the game client.

        ### Assets (Asset Browser)
        The asset list, with a Scope dropdown above the filters: "Characters and personal corps"
        (the default, remembered per machine) or "Everything", which adds corporations not marked
        personal. Every filter, set_asset_filter included, searches within that scope; search and
        filter by item name, location and owner.

        Locations nest: an item can sit in another item — a ship's holds, an assembled ship in a
        hangar, a station/secure/audit container, a can — and containers in containers. Each
        asset is resolved up that chain to its root (a station, a player structure, or space in
        a system): Location Name, Solar System, Region and Security show where it really is,
        however deep, and the Container column the path within it. Answer "where is X?" from the
        root, never the immediate container. A player structure's name resolves only if an
        authenticated character can dock there; otherwise it shows "<Unknown Structure>".

        ### Item Browser
        Look up any published item by name, or browse the market-group tree on the left.
        Description, attributes/dogma, current market orders and price history for your price
        sources, and industry details. Requirements lists the skills needed to use or build it;
        for a skill, Required For shows by level (I-V) what needs it. Skill names there — and in
        a character's Skills tab — open that skill.

        ### Industry Jobs
        Every manufacturing, reaction, invention and research job. Filter by status (active,
        delivered), activity and character or corp owner, and search by blueprint or output
        item.

        ### Indy Parks
        Define "industry parks": which structures you run each category of item in, with per-item
        structure exceptions. They drive the industry costs — job cost, ME/TE bonuses, rig and
        structure effects — used by the Production Calculator and the build-cost engine.

        ### Production Calc (Production Calculator)
        Plan a manufacturing job for a blueprint or product. Build cost, materials (optionally
        down the full build chain) and job details, from your Indy Parks and current prices.
        Double-click a material or product row to open it in the Item Browser; right-click the
        results for "Copy to Clipboard", "Export as CSV" or "Tab-delimited".

        ### Price Overrides
        Hand-set prices the build-cost engine and the Production Calculator use instead of their
        computed or market ones. "+ Add Type", then fill any of Build Cost, Market Value or
        Contract Value (for a blueprint, the per-run BPC price); a blank cell stays computed.
        "Save & Recalculate" stores the list and recalculates the stored build costs other tools
        read. A pinned build cost still loses to buying when the market is cheaper.

        ### Industry Opportunities
        Ranks what is worth manufacturing: each buildable item's stored build cost against a
        market price, weighing profit against how long a build ties up a slot. Pick a market
        config (the Market Sources configs) and a mode: "Build & Sell Order" (against the lowest
        sell) or "Build & Sell to Buy Order" (against the highest buy). Columns: Profit/Unit,
        Margin, Build Time / Slot Days (one unit), Profit per Slot Day, and 30-day units and ISK
        sold. Sorted by ISK Sold 30d until another column is clicked; market, mode, filters and
        sort are remembered on this machine across restarts. Build cost and time both use the
        default Indy Park; time assumes a TE20 blueprint and maxed skills with that park's
        structure role and rig time bonuses per category, so Slot Days match the actual setup.
        Items with no sell orders still show, priced from the 30-day history average with a "*" on
        Sell Price. Optional "Min 30d ISK Vol" / "Min 30d Unit Vol" filters; exclude market groups
        with "+ Add Group" beside the Exclude Groups chips (✕ removes one; none by default).
        Double-click a row to open the item in the Item Browser. It makes no ESI calls; history is
        kept current by the background Price History Sweep.

        ### Price sources & the Method dropdown (Settings > Market)
        Market and valuation tools price against a named "price source" (Settings > Market): a
        stored price per item, optionally filtering lowball/highball anomaly orders, and for an
        item with no market a % over build cost (capitals, supers, titans). Its Method says how
        it gets orders:
        - Region (ESI Region): every public order in a whole region, from ESI — for NPC trade
          hubs (The Forge for Jita). An optional Station Filter narrows the price to one NPC
          station (Jita 4-4) after the first refresh. ⚠️ ESI's region feed does NOT include
          orders inside player structures — only NPC-station orders and public regional buy
          orders — so a market in a citadel, Fortizar or Keepstar is invisible to it, and the
          Station Filter cannot reach one.
        - Player Structure: every order inside one player structure; needs an authenticated
          character with docking access there. The ONLY way to price a null-sec or low-sec
          staging market, or any private structure market.
        Fuzzwork is a LEGACY method: sources made with it by older versions (pre-computed
        percentile prices from fuzzwork.co.uk) still refresh and price, but it can no longer be
        chosen: it stores no individual orders (the Item Browser's Market Orders tab is empty for
        it) and cannot reach a structure. Never recommend it; use a Region source instead.

        ### Market Overview
        A regional dashboard over the stored market data: pick a Region (or all) and a Period
        (last 30 days by default). Summary shows open sell and buy order counts and ISK, units
        and ISK sold, pie charts by type and market group, and daily sales ISK; other tabs break
        these down by market group and type and list open orders by type. Sales are region-wide
        market history, not your own; NPC orders are left out.

        ### Item Valuation
        An appraisal tool like the web ones: paste any list the client copies — a hangar or
        cargo, a contract's items, a fit, a multibuy list, spreadsheet rows, or lines like
        "Tritanium 22222" or "Warrior II x5". Read leniently: in columns (tabs, commas,
        semicolons, pipes or spaces) the first is the name and the first whole number after it
        the count — no number means one; other columns are ignored, a header row is skipped and
        repeats are added up. Pick a STATION (any with orders in the app's books, from whichever
        source — so two stations of one region can be compared), a basis (Sell = lowest sell,
        Buy = highest buy, Split = halfway), the items or their reprocessed output, and press
        Appraise. Values shows each item three ways at that station, unit and total: market (from
        contracts where the station has none, marked "contract"), build (the app's build cost)
        and reprocessed (materials at that station's prices, at the app's yields) — the highest
        green, the others red with how far below; the panel above totals each, with volume and
        counts. "Reprocessed output" turns the list into materials batch by batch, keeping what
        could not be reprocessed as "Left over". Market compare adds stations: each item's unit,
        total and % below the best, and a total per station. Price % values at a share of the
        price (a 90% buyback). Buy orders count at the station, its system or region-wide; NPC and
        jump-ranged ones do not. Unknown names stay in the table, flagged; item names open the
        Item Browser; Copy puts both tables on the clipboard as tab-separated text. Nothing is
        stored.

        ### Market Levels
        Watch a definable market (a region or a structure) for how much of a list of items is
        listed for sale — whether it is being kept stocked. Collapsible collections and groups
        with a target level per item; columns for target vs available, market price, build cost
        and industry-job counts. Same controls as Inventory Levels.

        ### Inventory Levels
        Monitor your own holdings of a definable item list — available assets plus in-build, buy
        orders and so on — against target levels, like jEveAssets stockpiles. Collections hold
        Groups (each with a quantity multiplier ×N) and Groups hold Items (each with a target,
        listed alphabetically); columns for target, available, difference, assets, industry jobs,
        market price and build cost.

        ### Inventory Levels & Market Levels (same controls)
        - Toolbar: "+ Add Group", "+ Collection", "Refresh" (re-pulls the data).
        - Collection row: expand/collapse arrows, "+"/"−" to expand or collapse all its groups,
          "Rename", "Delete".
        - Group row: toggle arrow, the editable multiplier (×N), "+ Item", "Edit" (group
          scope/locality) and "Delete".
        - Right-click a row: "Open in Item Browser" (on an item), and the bulk adds — "Add Items
          From Fit" (paste or pick an EVE fit), "Add Items From Market Group" (a group and all its
          sub-groups) and "Add Items From Blueprint" (its materials) — and "Delete Item".

        ### LP Market Values
        What loyalty points are worth, per corporation. Each LP store offer is valued at the default
        asset-value prices (contract price where there is none), less its ISK cost and any items
        it consumes, per LP. Current Values lists each corporation's mean ISK / LP over offers
        worth more than zero, with Median, Best Offer, Best Item, Your LP and Holding Worth —
        corporations you hold LP with first. Recalculate reprices; History charts one
        corporation's ISK / LP over time.

        ### Contracts
        Two tabs, each a grid with a detail panel: parties, prices, locations, dates and every
        item offered or requested (with BPC runs and ME/TE). Corporation & Personal holds your
        characters' and corporations' contracts, filtered by Scope, Status, Assignee and
        Acceptor. Public searches the public contracts swept from every non-wormhole region, by
        Show (active, historical or all), type, region, category and item name, 200 per page.
        Both read stored data, not live ESI.

        ### Trade Opportunities
        Profitable hauling between two markets: pick a From and a To station (type to filter).
        Modes: "Sell to Buy Order" (buy source sell orders, sell into destination buy orders) and
        "Undercut Sell Order" (buy at the source, relist below the destination's lowest sell).
        Limit by cargo (m³) and an optional ISK cap; optional "Min 30d ISK Vol" / "Min 30d Unit
        Vol" check the destination region's 30-day history (kept current by the Price History
        Sweep; no ESI calls here). Exclude market groups, and everything under them, with "+ Add
        Group" beside the Exclude Groups chips (✕ removes one); Blueprints & Reactions, Ship
        SKINs, Special Edition Assets, Apparel, Skills and Trade Goods are excluded by default.
        The result is a shopping list within those limits, highest Total Profit first until
        re-sorted; double-click a row to open the item in the Item Browser.

        ### Standing Buy Orders
        Buy orders you mean to keep standing at a station or structure, declared with Add and
        matched against your characters' and corporations' live buy orders. Status: Missing (no
        live order there), Outbid or Active; the Note flags one low on volume or near expiry.
        Station best bid is known only where that station is a price source (Settings > Market).
        Missing, outbid, low and expiring orders can raise Worklist tasks.

        ### Order Tracker
        Orders you have committed to deliver: added with Add Order, or booked by a Store from EVE
        mail or the web; one row per line, lines of one order sharing an Order #. Contracts you
        issue to the buyer (or the order's Contract To) after the order was placed are matched
        automatically — units counted over every line; fitted rigs and other extras, packaged or
        assembled, do not matter — or attached with Edit (ids comma separated). An order can span
        several contracts and a contract several orders: On contract is units awaiting the buyer's
        acceptance, Delivered is units accepted of those ordered, and only the rest is planned
        from stock and builds (On hand, in build, Short). It completes when every unit is
        accepted; a declined contract for the whole order cancels it. Also Purchase Price, Status
        (Pending, Completed, Canceled), Build Cost and Profit; Priority orders come first in the
        Worklist. Sales already made are in the Sales Tracker.

        ### Sales Tracker
        Sales already made: market sell transactions and finished item-exchange contracts, each
        with build cost and market value from the nearest daily price snapshot, and profit against
        the "Profit based on" basis (Build or Market). Filter by date, owner and sale type.
        Summary shows profit by buyer, market group and item, with charts of sales, costs, profit
        and margin; Detail lists each sale. Right-click a sale to mark it not for profit (left out
        of the figures) or to add labels.

        ### Sale Posting
        A shareable price list. On Definitions a posting (with a region and a build-price
        multiplier) holds sections, which may override the multiplier, and sections hold items.
        Each item shows In Stock, In Build and Reserved (overridable), three reference prices
        (Build Cost, Mkt Value, Contract), your Sale Price, profit against Build or Market, and a
        Ready estimate. The Posting tab renders it as Plain Text, Slack, Discord, Markdown, HTML,
        EVE Mail or BBCode to copy. A Store sells a posting.

        ### Stores (EVE Mail store and web shop)
        A store sells a sale posting — priced from build costs or the market — by EVE Mail, on the
        web, or both. EVE Mail: buyers mail the store's character PRICES, ORDER, STATUS or CANCEL;
        the app answers and books orders. Web: a site the owner hosts on Cloudflare (a Worker and
        a D1 database per store) where buyers sign in with EVE SSO, see the price list with stock,
        in build and reserved, and place and follow orders. The app pushes the price list and
        order book and pulls what buyers did every few minutes ("Check the site every N minutes"
        on Config, default 5 — a buyer waits up to that long for a confirmation); the site never
        touches the database. A web order books only if its item is on the posting, its quantity
        is in bounds, the buyer passes the Serve policy (Anyone, or the allow list) and its price
        is close to the posting's; otherwise it waits under "Web site events" on the Overview for
        the owner to book or decline, where buyer visits (signing in, or back after half an hour
        or more) are noted too. Every order is in Order Tracker with a channel of mail, web or
        manual; buyers see all theirs on the site. Config holds the site address, the shared
        secret calls are signed with, the site's own theme (independent of this desktop's), the
        "About this store" text (plain or HTML) and an optional banner picture, the EVE developer
        application's Client ID and Secret Key the site signs buyers in with (required whoever
        hosts it), and a Cloudflare section used only by "Deploy or update site", which puts the
        newest release on the owner's account with a saved API token, at the free workers.dev
        address or their own domain; a site set up by hand with wrangler works the same.

        ### Net Worth
        A historical chart of your net worth over time (assets, wallet, etc.).

        ### Income & Expense
        Where ISK came from and went, from the wallet journal of every authenticated character and
        personal corp together (no owner picker). Income and Expenses list categories by journal
        type, largest first, smaller ones rolled into Other; a chart shows daily income, expense
        and running cashflow. The period follows the Overview's period selector. ISK moved
        between your own wallets is left out. Asset value is in Net Worth; single entries in Wallet.

        ### Wallet
        Browse wallet transactions and the wallet journal for your characters.

        ### Corp Activity
        Corporation activity and finances (needs a corp character with director or accountant
        scopes). Tabs: Activity (24h) and Monthly Activity summaries (ratting, industry, mining,
        kills/losses, income/expense); Income and Expense by type; Ratting Taxes, Industry Taxes
        and Donations; Mining; Killmails; Top 10 Lists (with an exclude list); and Projects —
        ACTIVE and HISTORY for live corp projects, plus STANDING PROJECTS: goals you always want
        running (a "deliver item" project at a station, or a "destroy NPC" project across a
        system, constellation or region with an ADM threshold), matched against live ESI projects
        to show remaining quantity and payout and whether each is active; the Overview alerts when
        one lapses. "+ Add Project" defines one; each row has "Edit" and "Delete"; right-click for
        "Clone item" and, on a deliver-item project, "Open Item in Item Browser". Players Active
        counts members only: characters seen acting while in the corp (a login, a kill or loss,
        moon mining, a corp job or contract, the corp's tax on their bounties or missions), plus
        wallet and contract counterparties who are members now — never its customers, the users
        of its structures or other corporations.

        ### Killmails
        Browse corporation and personal killmails with a detailed kill report view.

        ### Player Entities
        Look up players on three tabs: Pilots, Corporations and Alliances. Search by name (two
        letters minimum; names not yet cached are fetched from ESI). The selected entity shows its
        portrait or logo, key facts, and sub-tabs where there is content: Description, Kills /
        Losses, Corp History (pilots), Alliance History (corporations), Member Corps (alliances)
        and Intel Reports (pilots). Player names elsewhere in the app open here, as does
        navigate_to_entity for a pilot, corporation or alliance.

        ### NPC Entities
        NPC entities from the game's static data on four tabs — Agents, Stations, Corporations
        and Factions — searched by name. An NPC corporation shows its faction, headquarters,
        ticker and tax rate, with sub-tabs for its agents, stations, market orders (in regions
        you have market data for) and LP store offers. A station lists its agents; a faction, its
        corporations and Faction Warfare systems. navigate_to_entity opens an agent, NPC
        corporation or faction here.

        ### Eve Mail
        Read and compose EVE mail from within EVE Console.

        ### Notifications
        The in-game notifications synced for your characters: structure alerts, wars, insurance,
        corporation and faction messages and more. One sent to several of your characters is one
        row listing them all, unread while any has not read it. Filter by character, type, sender
        type and date (last 30 days by default), or Unread only; the selected row's formatted
        body shows below. A notification card on the Overview opens here. Needs the
        esi-characters.read_notifications.v1 scope.

        ### Structure Browser
        Player-owned structures the app knows of, from your own data and ESI's public list; Add
        by ID adds another. Filter by location, type, corp and alliance; Show unknown includes
        those ESI will not describe for lack of docking access. Selecting one shows tabs: Details
        (notes; name, system and type hand-editable only where ESI is silent), Fitting (from your
        assets, or entered by hand, which carries rigs to a linked Indy Parks entry), Assets,
        Cargo, Fuel, Fighters and Industry Jobs.

        ### Universe Map
        A drill-down map: New Eden, then a region, then a system page. Overlays colour systems by
        security, kills, jumps, industry indices, sovereignty, stations, planetary output and
        intel sightings. The system page has tabs for its celestials, kills, industry indices,
        graphs and intel.

        ### Jump Planner
        Plans a capital jump route. Pick a jump-capable hull, Jump Drive Calibration and Jump Fuel
        Conservation levels (the range per jump is shown) and a Jump Through rule for where
        midpoints may stop (anywhere, stations and structures, Fortizar/Keepstar or Keepstar
        systems), then add waypoints by name and Plan Route. The map shows the route with total
        jumps, light-years and isotope fuel, above a per-leg table; drag a midpoint to move it or
        click it for alternatives. High-sec waypoints are refused.

        ### Alarms
        User-defined alarms — none exist by default. Each is a condition to watch for and one or
        more actions when it fires, set up by the capsuleer or by you with manage_alarms (its
        description lists the conditions and actions); for "tell me when…" the action is
        agent_notify.

        ### Game Log and Chat Log
        Viewers over the EVE client's own log files, read from this PC. See "Local logs" below
        for what is in them.

        ### Background Processes
        A tab per background process — the ESI activity log and call schedule, price history,
        contract items, LP store, killmails, intel, alarms, order fulfilment, structures, the name
        cache — each saying what it is doing right now, live, even when the work runs on another
        client sharing the database. The bottom status bar has a short label per process (ESI
        Calls, Price History, Contract Items, LP Store, Killmails), lit while busy; clicking one
        opens this tool at that tab.

        ### ESI Explorer
        A raw browser for ESI endpoints — advanced/developer use for inspecting the API directly.

        ### Error Log
        The app's own recorded errors, such as a failed ESI call or a calculation error, newest
        first: Time, Client (which machine or worker logged it), Source, Context, Message and
        Inner exception. From / Thru defaults to the last 24 hours and loads at most 5,000 rows,
        read when the tab opens; Refresh re-reads. Selecting a row shows the full text — what to
        copy into a bug report.

        ### AI Usage (AI Usage & Cost)
        What this assistant has used and its estimated cost — agent turns, speech out and speech
        in — over a date range (last 30 days by default), by day, week or month. Tabs: Summary
        (calls, tokens, cache use and cost per period and model), Detail (each call, with the
        tools its turn used) and Rates, the editable price list costs are worked out from. A
        service with no rate shows "no rate" and is left out of the total; local models are free.
        Every model a paid service lists gets its own rate row when the list loads in Settings or
        this tool opens: priced from LiteLLM's price list where it has the model (the note names
        the list, its date and the provider's pricing page to check), else a copy of the
        service's "(any model)" rate noted "Not set yet". A rate edited here is never overwritten.

        ## Local logs (game logs, chat logs, intel)
        Read from the EVE client's own log files on this PC once log import is switched on under
        Settings > Game Logs / Chat Logs: only characters played here, only since then, never
        backfillable. Game log lines are classified by Kind (combat.*, movement.jumped,
        movement.undocked; unrecognised ones 'unmatched'); chat is stored once per message however
        many of your characters saw it; intel channels are parsed into IntelReports and
        IntelReportCharacters — system, pilot count, reporter, and named pilots with their hulls.
        Undocking from a player structure writes no log line.

        ## Settings (gear icon)
        Tabs: ESI Tokens (add and manage authenticated characters), SDE (import or update the
        Static Data Export — needed before item and market lookups work), Market (price sources,
        and the default asset-value and manufacturing-cost pricing), Timers and Polling (each kind
        of ESI data's own refresh interval), Corp Top 10 (the exclude list), AI Agent (this
        assistant's models, each picked from its service's own list beside that service's key;
        Roles — which model talks, which answers data questions, which summarises;
        Personalisation; voices in order of preference; push-to-talk), Alerts (Overview alerts),
        Price History (regions whose market history the "Price History Sweep" refreshes, every
        24h by default, for the opportunity tools), Database (path, scheduled backups, move,
        rename, repoint) and Other (theme and UI scale, 50% to 200%, both this desktop's own; the
        scale is also at the right end of the status bar).
        """;

    /// <summary>
    /// Every tool the application opens as a tab: its id — what open_window takes and a tab is
    /// keyed by — the name it goes by, and the heading its entry in <see cref="Guide"/> starts
    /// with. One list for the agent's open_window, for naming the tab the capsuleer has on screen,
    /// and for finding what the guide says about it.
    ///
    /// <para>⚠️ Keep it in step with the sidebar (MainWindowViewModel) and the guide. The agent
    /// could open 17 of these 41 tools, and the guide left 17 undescribed: asked about one of
    /// those, a small model borrowed the nearest entry — the Universe Map's for the Jump Planner —
    /// and could not open the tool it offered to open.</para>
    /// </summary>
    public static readonly IReadOnlyList<(string Id, string Name, string Heading)> Tools =
    [
        ("overview",            "Overview",               "Overview"),
        ("worklist",            "Worklist",               "Worklist"),
        ("characters",          "Characters",             "Characters"),
        ("assets",              "Assets",                 "Assets"),
        ("items",               "Item Browser",           "Item Browser"),
        ("inv_levels",          "Inventory Levels",       "Inventory Levels"),
        ("structure_browser",   "Structure Browser",      "Structure Browser"),
        ("universe",            "Universe Map",           "Universe Map"),
        ("jump_planner",        "Jump Planner",           "Jump Planner"),
        ("industry",            "Industry Jobs",          "Industry Jobs"),
        ("indy_parks",          "Indy Parks",             "Indy Parks"),
        ("prod_calc",           "Production Calculator",  "Production Calc"),
        ("fitting",             "Fitting",                "Fitting"),
        ("price_overrides",     "Price Overrides",        "Price Overrides"),
        ("industry_opps",       "Industry Opportunities", "Industry Opportunities"),
        ("market_viewer",       "Market Overview",        "Market Overview"),
        ("item_valuation",      "Item Valuation",         "Item Valuation"),
        ("lp_market_values",    "LP Market Values",       "LP Market Values"),
        ("market_levels",       "Market Levels",          "Market Levels"),
        ("contracts",           "Contracts",              "Contracts"),
        ("trade",               "Trade Opportunities",    "Trade Opportunities"),
        ("standing_buy_orders", "Standing Buy Orders",    "Standing Buy Orders"),
        ("order_tracker",       "Order Tracker",          "Order Tracker"),
        ("sales_tracker",       "Sales Tracker",          "Sales Tracker"),
        ("sale_posting",        "Sale Posting",           "Sale Posting"),
        ("stores",              "Stores",                 "Stores"),
        ("net_worth",           "Net Worth",              "Net Worth"),
        ("income_expense",      "Income & Expense",       "Income & Expense"),
        ("wallet",              "Wallet",                 "Wallet"),
        ("corp_activity",       "Corp Activity",          "Corp Activity"),
        ("killmails",           "Killmails",              "Killmails"),
        ("player_entities",     "Player Entities",        "Player Entities"),
        ("npc_entities",        "NPC Entities",           "NPC Entities"),
        ("eve_mail",            "Eve Mail",               "Eve Mail"),
        ("notifications",       "Notifications",          "Notifications"),
        ("background",          "Background Processes",   "Background Processes"),
        ("data",                "ESI Explorer",           "ESI Explorer"),
        ("error_log",           "Error Log",              "Error Log"),
        ("ai_usage",            "AI Usage",               "AI Usage"),
        ("game_log",            "Game Log",               "Game Log and Chat Log"),
        ("chat_log",            "Chat Log",               "Game Log and Chat Log"),
        ("alarms",              "Alarms",                 "Alarms"),
    ];

    /// <summary>
    /// The tool a tab shows: by its id, or — for a caller with only a title, or a name the agent
    /// wrote — by name. Tab titles are sometimes short forms ("Universe", "Trade"), so a name
    /// that starts with the title counts. Null for a tab that is not a tool: a table or document
    /// the agent opened.
    /// </summary>
    public static (string Id, string Name, string Heading)? Tool(string? idOrTitle)
    {
        var key = (idOrTitle ?? "").Trim();
        if (key.Length == 0) return null;
        foreach (var t in Tools) if (t.Id.Equals(key, StringComparison.OrdinalIgnoreCase)) return t;
        foreach (var t in Tools) if (t.Name.Equals(key, StringComparison.OrdinalIgnoreCase)) return t;
        foreach (var t in Tools)
            if (t.Name.StartsWith(key, StringComparison.OrdinalIgnoreCase) || t.Heading.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                return t;
        return null;
    }

    /// <summary>
    /// What the guide says about one tool: every section whose heading starts with the tool's —
    /// its description and, where there is one, its entry under Interactions — cut to
    /// <paramref name="limit"/> characters. Empty for a tool the guide does not describe.
    /// </summary>
    public static string EntryFor(string? toolId, int limit = 1800)
    {
        if (Tool(toolId) is not { } tool) return "";
        var lines = Guide.Split('\n');
        var entry = new System.Text.StringBuilder();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (!line.StartsWith("### ") || !line[4..].StartsWith(tool.Heading, StringComparison.OrdinalIgnoreCase)) continue;
            var end = i + 1;
            while (end < lines.Length && !lines[end].TrimStart().StartsWith('#')) end++;
            entry.AppendLine(string.Join('\n', lines[i..end].Select(l => l.TrimEnd())).Trim()).AppendLine();
            i = end - 1;
        }
        var text = entry.ToString().Trim();
        return text.Length <= limit ? text : text[..limit].TrimEnd() + "…";
    }

    /// <summary>
    /// What a tab is for, in a sentence: the first of the guide's entry for its tool. From the
    /// guide itself, so the two cannot disagree — the keyword matching this replaced gave the
    /// Market Overview the Overview's description ("overview") and Item Valuation the Item
    /// Browser's ("item").
    /// </summary>
    public static string TabIntent(string? tabTitleOrId)
    {
        var entry = EntryFor(Tool(tabTitleOrId)?.Id);
        if (entry.Length == 0) return "";
        var body = string.Join(' ', entry.Split('\n').Skip(1).TakeWhile(l => l.Trim().Length > 0).Select(l => l.Trim()));
        var stop = body.IndexOf(". ", StringComparison.Ordinal);
        return stop < 0 ? body : body[..(stop + 1)];
    }
}
