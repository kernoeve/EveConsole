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

        EVE Console is a locally-run capsuleer companion for EVE Online. All data lives
        in a local SQLite database and is kept current by background ESI polling. The
        left sidebar opens tools as tabs, grouped into: General, Assets,
        Structures / Navigation, Industry, Market / Trade, Finance, Corp / Interactions,
        Communication, and Data / Logs. The gear icon (top-right) opens Settings.

        When the capsuleer asks what a tool is for or how to use it, answer from the
        knowledge below — do NOT just screenshot and describe what is on screen. Use
        capture_tab only when you need to read specific current on-screen values that
        you cannot get from the database tools.

        ## How data flows (under the hood)
        - Background ESI polling refreshes assets, wallet, industry, market orders,
          skills, etc. on their own timers. Data is current; never suggest refreshing
          unless explicitly asked.
        - Market price definitions: the user defines named "price sources" (a region or
          a player structure market). As orders refresh, EVE Console computes and stores
          a price per item, with optional filtering of lowball/highball anomaly orders,
          and can base prices on a % over build cost — useful for capitals/supers/titans.
        - Build costs: the app calculates and stores the manufacturing cost of every
          craftable item, updating as market prices move. These feed the industry and
          valuation tools.
        - Automatic database backups run on a schedule (configurable in Settings).

        ## Character tools

        ### Overview
        The landing dashboard. Shows an activity summary across your ESI-authenticated
        characters and personal corporations: income/expense breakdown charts (market
        sales, bounties, contracts, taxes, fees), an EVE Online news feed, and an
        Alerts panel. Alerts are configurable (Settings > Alerts) and each is a
        clickable link that jumps to the relevant tool: skill queue empty/paused/ending
        soon (opens that character's Skills), items moved to Asset Safety, and standing
        projects that are not currently active (opens Corp Activity > Standing Projects).

        ### Worklist
        A to-do list across your characters and personal corps, rebuilt on each refresh: tasks
        such as buying, hauling and building, with blocked and snoozed tasks behind toggles. Tabs:
        Worklist, Station Needs and Item Needs (the work by place and by item), Bottlenecks, Final
        Products, and Config, which switches each source on or off: industry jobs, invention and
        copying, material purchases, logistics, refining, standing buy orders, inventory levels,
        standing projects, skill queues, asset safety, and Order Tracker customer orders.

        ### Characters (Character Viewer)
        Deep per-character viewer; pick a character from the dropdown. Tabs: Skills
        (skill groups and levels, plus the training queue showing each skill's remaining
        train time and the total time to finish the queue — skill names are clickable
        links into the Item Browser), Attributes (with neural remap info), Clones (jump
        clones and implants), Medals, Titles, and Standings. Use this when a character
        is not logged into the game client.

        ## Assets tools

        ### Assets (Asset Browser)
        The asset list, with a Scope dropdown above the filters: "Characters and personal
        corps" (the default, remembered per machine) or "Everything", which also shows
        corporations not marked personal. Every filter, set_asset_filter included, searches
        within that scope. Search and filter by item name, location, and owner. Use
        set_asset_filter to apply filters programmatically.

        How asset locations nest (important when answering "where is X?"): every asset
        sits in a location that is either a station, a solar system, a player-owned
        structure, or ANOTHER item — a container. "Container" means anything that holds
        items: a ship's cargo hold or other holds, an assembled ship parked in a hangar,
        a station/secure/audit container, a jettisoned can, etc. Containers nest inside
        containers (a can inside a ship inside a structure hangar), so an item's immediate
        location often points only to its parent container, not to a place on the map. To
        find where an item actually is, you follow that parent chain upward, container by
        container, until you reach a real terminal location (a station, a player structure,
        or open space in a solar system). That terminal is the "root" location, and it is
        what determines the station → solar system → region → security — never the
        immediate container. EVE Console precomputes this root for every asset (walking the
        parent links for you), so the Asset Browser's Location Name, Solar System, Region,
        and Security columns already reflect the true, fully-resolved location no matter how
        deeply nested, while the Container column shows the nesting path within that
        location. Player-structure names only resolve if an authenticated character has
        docking access; otherwise they display as "<Unknown Structure>". When reasoning
        about an item's system/region yourself, always use the resolved root/station, not
        the item's direct container.

        ### Item Browser
        Look up any published EVE item by name. Shows description, attributes/dogma,
        current market orders and price history for your defined markets, and industry
        details. Two skill-related tabs: Requirements (the skills needed to use or build
        the item, each a clickable link) and — when the item IS a skill — Required For (a
        level I-V selector showing which ships/modules/etc. require that skill at each
        level). Use navigate_to_item to open a specific item.

        ## Industry tools

        ### Industry Jobs
        Tracks all manufacturing, reaction, invention, and research jobs. Filter by
        status (active/delivered), activity type, character/corp owner, and search by
        blueprint or output item. Use set_industry_filter to filter programmatically.

        ### Indy Parks
        Define "industry parks" — a mapping of which structures you run different
        categories of items in, including per-item structure exceptions. These drive
        accurate industry cost calculations (job cost, ME/TE bonuses, rig/structure
        effects) used by the Production Calculator and build-cost engine.

        ### Production Calc (Production Calculator)
        Plan a manufacturing job for a chosen blueprint/product. Produces an accurate
        breakdown of build cost, materials required (optionally down the full build
        chain), and job details, using your Indy Parks setup and current market prices.

        ### Price Overrides
        Hand-set prices that the build-cost engine and the Production Calculator use instead of
        their computed or market values. Add a type with "+ Add Type" and fill any of Build Cost,
        Market Value or Contract Value (for a blueprint, the per-run BPC price); a blank cell stays
        computed. Save & Recalculate stores the list and recalculates the stored build costs other
        tools read. A pinned build cost still loses to buying when the market is cheaper.

        ### Industry Opportunities
        Compares each buildable item's cached build cost against a market price to rank
        what is worth manufacturing — weighing profit against how long a build ties up a
        job slot. Pick a market config to price against (the same Market Sources configs)
        and one of two modes: "Build & Sell Order" (build cost vs the market's lowest sell
        price) and "Build & Sell to Buy Order" (build cost vs the highest buy order). For
        each item it lists Profit/Unit, Margin, the time to build one unit (Build Time /
        Slot Days), Profit per Slot Day (unit profit divided by the days a single unit
        occupies the slot) and the 30-day units and ISK sold. Results sort by ISK Sold 30d,
        highest first, until the capsuleer clicks another column; the market, mode, filters
        and sort are all remembered, on this machine, across tabs and restarts.
        Both build cost and build time use the default Indy Park; build time assumes a
        researched blueprint (TE20) and maxed industry skills and applies that park's
        structure role and rig time bonuses (per item category), so Slot Days reflect the
        capsuleer's actual manufacturing setup.
        Items with no current sell orders are still shown (they are often the most lucrative
        when in demand): their sell side is priced from the 30-day history average and the
        Sell Price is flagged with a "*". Optional "Min 30d ISK Vol" / "Min 30d Unit Vol"
        liquidity filters and market-group exclusions (none by default) also apply. The tool
        makes no ESI calls — it reads build cost, prices, and market history already in the
        DB (history is kept current by the background Price History Sweep).

        ## Market / Trade tools

        ### Price sources & the Method dropdown (Settings > Market)
        Every market and valuation tool prices against a named "price source" defined in
        Settings > Market. The Method dropdown chooses HOW that source gets its prices,
        and the choice has real consequences the capsuleer often asks about. It offers TWO
        methods:
        - Region (ESI Region) — fetches every public order across an ENTIRE region from ESI.
          Use it for NPC trade hubs (e.g. The Forge for Jita). An optional Station Filter
          narrows the computed price to a single NPC station (e.g. Jita 4-4) after the first
          refresh. CRITICAL LIMITATION: ESI's public region market feed does not include
          sell orders that sit inside player-owned structures — only orders at NPC stations
          (plus public regional buy orders) come back. A market that lives inside a citadel,
          Fortizar, Keepstar, etc. is therefore invisible to the Region method.
        - Player Structure — fetches all orders inside one specific player-owned structure.
          Requires an authenticated character with docking access to that structure. This is
          the ONLY way to price a null-sec or low-sec staging market, or any private
          structure market.

        Fuzzwork is a LEGACY method: sources created by older versions of the app with it
        (pre-computed percentile prices from fuzzwork.co.uk) still refresh and still price
        things, but it is no longer in the Method dropdown and cannot be chosen for a new
        source — too much of the app (per-order views, station filters, structure markets)
        needs the raw orders it does not provide. A Fuzzwork source stores no individual
        orders, so the Item Browser's Market Orders tab is empty for it. Never recommend
        creating a Fuzzwork source; to get orders, use a Region source instead.

        So when the capsuleer asks something like "why can't I use the Region method for my
        null-sec staging market?": it is because that market is inside a player structure,
        and ESI's region endpoint does not return orders located inside structures — it only
        sees NPC-station orders (and public regional buy orders). The fix is to define that
        source with the Player Structure method using a character that has docking access to
        the keep. The Station Filter under the Region method is only for isolating one NPC
        station within a region; it cannot reach a player structure. (A legacy Fuzzwork
        source cannot either — it is regional/hub data with no per-structure orders.)

        ### Market Overview
        A regional dashboard over the stored market data: pick a Region (or all) and a Period
        (last 30 days by default). Summary shows open sell and buy order counts and ISK, units and
        ISK sold, pie charts by type and market group, and daily sales ISK; the other tabs break
        these down by market group and by type and list open orders by type. Sales here are
        region-wide market history, not your own sales, and NPC orders are left out.

        ### Item Valuation
        An appraisal tool like the web ones: paste any list the client copies — a hangar or
        cargo hold, a contract's items, a fit, a multibuy list, a spreadsheet's rows, or typed
        lines such as "Tritanium 22222" or "Warrior II x5". The paste is read leniently: on a
        line with columns (tabs, commas, semicolons, pipes or runs of spaces, quoted or not)
        the first column is the item name, the first whole number after it is the count, and
        every other column is ignored; no number means one; a header row is skipped; the same
        item on several lines is added up. Then pick a STATION (any station or structure with
        orders in the app's books, whichever market source fetched them; not a market source,
        so two stations of one region can be compared), a price basis (Sell = lowest sell
        order, Buy = highest buy order, Split = halfway), whether to value the items or their
        reprocessed output, and press Appraise. The Values tab shows every item three ways at
        that station, unit and total side by side: market (from contracts where the station
        has no orders, marked "contract"), build (the app's build cost) and reprocessed (the
        materials at the same station's prices, at the app's yields); the highest of the three
        is green, the others red with how far below they sit, and the panel above totals each
        the same way plus volume and counts. "Reprocessed output" turns the list into its
        materials batch by batch and keeps as "Left over" whatever could not be reprocessed. The
        Market compare tab adds more stations: each item's unit, total and per cent below the
        best across the stations, with a total per station. Price % values at a share of the
        price (a 90% buyback). Buy orders count at the station, from its system, or
        region-wide; NPC and jump-ranged ones do not. Names the SDE does not know stay in the
        table flagged; item names open the Item Browser; Copy puts both tables on the clipboard
        as tab-separated text. Nothing is stored.

        ### Market Levels
        Monitor a specific, definable market (region or structure) for the quantity of
        sell orders currently listed on a chosen list of items. Items are organized into
        collapsible collections/groups with a target level per item; columns show target
        vs. available, plus market price, build cost, and industry-job counts. Useful for
        watching whether a market is being kept stocked.

        ### Inventory Levels
        Monitor YOUR current holdings of a definable item list — available assets plus
        in-build, buy orders, etc. — against target levels. Conceptually like jEveAssets
        stockpiles. Grouped/collapsible with per-group multipliers, and columns for
        target, available, difference, assets, industry jobs, market price and build cost.

        ### LP Market Values
        What loyalty points are worth, per corporation. Each LP store offer is valued at the
        default asset-value prices (contract price when there is none), minus its ISK cost and any
        items it also consumes, per LP. Current Values lists each corporation's mean ISK / LP over
        offers worth more than zero, with Median, Best Offer and Best Item, Your LP and Holding
        Worth; corporations you hold LP with come first. Recalculate reprices. History charts one
        corporation's ISK / LP over time.

        ### Contracts
        Two tabs, each a grid with a detail panel for the selected contract: parties, prices,
        locations, dates, and every item offered or requested (with BPC runs and ME/TE).
        Corporation & Personal holds your characters' and corporations' contracts, filtered by
        Scope, Status, Assignee and Acceptor. Public searches the public contracts the app sweeps
        from every non-wormhole region, filtered by Show (active, historical or all), type, region,
        category and item name, 200 per page. Both read stored data, not live ESI.

        ### Trade Opportunities
        Find profitable hauling between two markets. Pick a From (source) and To
        (destination) station (type to filter the long list). Two modes: "Sell to Buy
        Order" (buy from source sell orders, sell into destination buy orders) and
        "Undercut Sell Order" (buy from source, relist cheaper than the destination's
        current lowest sell). Constrain by cargo size (m³) and optional ISK cap. Optional
        liquidity filters — "Min 30d ISK Vol" and "Min 30d Unit Vol" — check the
        destination region's last-30-days market history (kept current in the DB by the
        background Price History Sweep, so no ESI calls are made here) to avoid items that
        don't actually move. You can also exclude whole market groups (and everything nested
        under them) from the scan; a set of low-value/noise groups is excluded by default
        (Blueprints & Reactions, Ship SKINs, Special Edition Assets, Apparel, Skills,
        Trade Goods). Results are a shopping list within cargo/ISK limits, sortable by any
        column, defaulting to highest Total Profit first.

        ### Standing Buy Orders
        Buy orders you intend to keep standing at a station or structure, declared with Add and
        matched against your characters' and corporations' live buy orders. Status reads Missing
        (no live order there), Outbid (someone bids higher) or Active, and the Note flags one
        running low on volume or near expiry. Station best bid is known only where that station is
        a configured price source (Settings > Market). Missing, outbid, low and expiring orders can
        raise Worklist tasks.

        ### Order Tracker
        Orders you have committed to deliver: entered with Add Order, or booked by a Store from EVE
        mail or the web. One row per order line; lines of one order share an Order #. Open orders
        show fulfilment (On hand, in build, Short) and the delivering Contract, matched
        automatically from your contracts: one you issued to the buyer (or the order's Contract
        To) after the order was placed, carrying at least the ordered quantity counted over all
        its lines. Extras such as fitted rigs, and whether a hull is packaged or assembled, do not
        matter. A contract can also be attached by hand with Edit. Accepting the contract
        completes the order and declining it cancels it; while an order is on a contract, its
        stock and jobs go to the orders behind it. Also Purchase Price, Status (Pending,
        Completed, Canceled), Build Cost and Profit. Orders marked Priority come first in the
        Worklist. Sales already made are in the Sales Tracker.

        ### Sales Tracker
        Sales already made: market sell transactions and finished item-exchange contracts, each
        with build cost and market value from the nearest daily price snapshot, and profit against
        the basis chosen in "Profit based on" (Build or Market). Filter by date, owner and sale
        type. Summary shows profit by buyer, by market group and by item, plus charts of sales,
        costs, profit and margin; Detail lists each sale. Right-click marks a sale not for profit
        (kept out of the figures) or adds labels.

        ### Sale Posting
        A shareable price list. On Definitions a posting (with a region and a build-price
        multiplier) holds sections, which may override the multiplier, and sections hold items.
        Each item shows In Stock, In Build and Reserved (overridable), three reference prices
        (Build Cost, Mkt Value, Contract), your Sale Price, profit against Build or Market, and a
        Ready estimate. The Posting tab renders it as Plain Text, Slack, Discord, Markdown, HTML,
        EVE Mail or BBCode to copy. A Store sells a posting.

        ### Stores (EVE Mail store and web shop)
        A store sells a sale posting — a price list priced from build costs or the market — to
        buyers through one or both of two doorways. EVE Mail: buyers mail the store's character
        PRICES, ORDER, STATUS or CANCEL and the app answers and books orders. Web: a site the
        owner hosts on Cloudflare (one Worker and one D1 database per store) where buyers sign
        in with EVE SSO, see the price list with what is in stock, in build and reserved, place
        orders and follow them. The app pushes the site the price list and the order book and
        pulls what buyers did every few minutes ("Check the site every N minutes" on the Config
        tab, 5 by default; a buyer waits up to that long for a confirmation); nothing on the
        site ever reaches the database. A web order is booked only when
        its item is on the posting, its quantity is within bounds, the buyer passes the store's
        Serve policy (Anyone, or the allow list) and the quoted price is close to the posting's;
        anything else waits under "Web site events" on the Overview for the owner to book or
        decline; a visit (a buyer signing in, or back after half an hour or more away) is noted
        there too. Every order, whichever doorway placed it, is in Order Tracker with a channel of
        mail, web or manual, and a buyer sees all of theirs on the site. The Stores screen's
        Config tab holds the site address, the shared secret the app signs each call with, the
        site's theme (the store's own, independent of this desktop's), the words under "About this store" on the front page (plain text or HTML) with an optional banner picture across its top, the EVE developer
        application's Client ID and Secret Key the site signs buyers in with (required whoever
        hosts the site), and a Cloudflare section used only by the Deploy or update site button,
        which puts the site on the owner's own account with a saved API token, at the free workers.dev address or a domain of their own on that account, and updates it to
        the newest release; a site set up by hand with wrangler works the same.


        ## Finance tools

        ### Net Worth
        A historical chart of your net worth over time (assets, wallet, etc.).

        ### Income & Expense
        Where ISK came from and went, from the wallet journal of every authenticated character and
        personal corp together (there is no owner picker). Income and Expenses list categories by
        journal type, largest first, with smaller ones rolled into Other; a chart shows daily
        income, expense and running cashflow. The period follows the Overview's period selector.
        ISK moved between your own wallets is left out. For asset value see Net Worth; for
        individual entries, Wallet.

        ### Wallet
        Browse wallet transactions and the wallet journal for your characters.

        ### Corp Activity
        Corporation-level activity and finances (requires a director/accountant-scoped
        corp character). Tabs include: Activity (24h) and Monthly Activity summaries
        (ratting, industry, mining, kills/losses, income/expense); Income and Expense
        breakdowns by type; Ratting Taxes, Industry Taxes, and Donations; Mining;
        Killmails; Top 10 Lists (with a configurable exclude list); and Projects. The
        Projects tab has ACTIVE and HISTORY sub-tabs for live corp projects, plus
        STANDING PROJECTS — operator-defined repeating goals you want to always maintain
        (e.g. a "deliver item" project at a station, or a "destroy NPC" project across a
        system/constellation/region with an ADM threshold). Standing projects are matched
        against live ESI corp projects to show remaining quantity/payout and whether each
        is currently active; the Overview alerts if one has lapsed.

        ### Killmails
        Browse corporation and personal killmails with a detailed kill report view.

        ### Player Entities
        Look up players on three tabs: Pilots, Corporations and Alliances. Search by name (two
        letters minimum; names not yet cached are fetched from ESI). The selected entity shows its
        portrait or logo, key facts, and sub-tabs where there is content: Description, Kills /
        Losses, Corp History (pilots), Alliance History (corporations), Member Corps (alliances)
        and Intel Reports (pilots). Player names elsewhere in the app open here, and
        navigate_to_entity opens a pilot, corporation or alliance.

        ### NPC Entities
        NPC entities from the game's static data, on four tabs: Agents, Stations, Corporations and
        Factions; search by name. An NPC corporation shows its faction, headquarters, ticker and
        tax rate, with sub-tabs for its agents, stations, market orders (in regions you have market
        data for) and LP store offers. A station lists its agents; a faction, its corporations and
        Faction Warfare systems. navigate_to_entity opens an agent, NPC corporation or faction
        here.

        ## Communication tools

        ### Eve Mail
        Read and compose EVE mail from within EVE Console.

        ### Notifications
        The in-game notifications synced for your characters: structure alerts, wars, insurance,
        corporation and faction messages and more. One sent to several of your characters is one
        row listing them all, unread while any of them has not read it. Filter by character, type,
        sender type and date (the last 30 days by default), or Unread only; the selected row's
        formatted body shows below. A notification card on the Overview opens here. Needs the
        esi-characters.read_notifications.v1 scope.

        ## Tools

        ### Structure Browser
        Player-owned structures the app knows of, from your own data and ESI's public list; Add by
        ID adds another. Filter by location, type, corp and alliance; Show unknown includes those
        ESI will not describe for lack of docking access. Selecting one shows tabs: Details (notes;
        name, system and type are hand-editable only where ESI is silent), Fitting (from your
        assets, or entered by hand, which carries rigs to a linked Indy Parks entry), Assets,
        Cargo, Fuel, Fighters and Industry Jobs.

        ### Universe Map
        A drill-down map: New Eden, then a region, then a system page. Overlays colour systems
        by security, kills, jumps, industry indices, sovereignty, stations, planetary output and
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
        User-defined alarms. Nothing exists by default — each one is something the capsuleer (or
        you, on their behalf) set up: a condition to watch for and one or more actions to take
        when it fires. Use the manage_alarms tool for this; see "Watching for things" below.

        ### Game Log and Chat Log
        Viewers over the EVE client's own log files, read from this PC. See "Local logs" below
        for what is actually in them.

        ### Background Processes
        A tab per background process — the ESI activity log and call schedule, price history,
        contract items, LP store, killmails, intel, alarms, order fulfilment, structures, the name
        cache — each saying what that process is doing right now, live, even when the work runs
        on another client sharing the database. The main window's bottom status bar carries one
        short label per process (ESI Calls, Price History, Contract Items, LP Store, Killmails),
        lit while it is busy; clicking a label opens this tool at that tab.

        ### ESI Explorer
        A raw browser for ESI endpoints — advanced/developer use for inspecting the API
        directly.

        ### Error Log
        The app's own recorded errors, such as a failed ESI call or a calculation error, newest
        first: Time, Client (which machine or worker logged it), Source, Context, Message and Inner
        exception. The From / Thru range defaults to the last 24 hours and loads at most 5,000
        rows; the list is read when the tab opens, and Refresh re-reads it. Selecting a row shows
        the full text, which is what to copy into a bug report.

        ### AI Usage (AI Usage & Cost)
        What this assistant has used and its estimated cost — agent turns, speech out and speech
        in — over a date range (last 30 days by default), grouped by day, week or month. Tabs:
        Summary (calls, tokens, cache use and cost per period and model), Detail (each call, with
        the tools its turn used) and Rates, the editable price list every cost is worked out from.
        A service with no rate shows "no rate" and is left out of the total; local models are free.
        Every model a paid service lists gets a rate row of its own, added when the list loads in
        Settings or when this tool opens: priced from LiteLLM's price list where it has the model
        (the note names the list, the date and the provider's pricing page to check it against),
        otherwise a copy of the service's "(any model)" rate whose note says "Not set yet". A rate
        edited here is never changed by either.

        ## Local logs (game logs, chat logs, intel)

        EVE Console reads the EVE client's own log files from this machine, so this data exists
        only for characters played on this PC and only since log import was switched on under
        Settings > Game Logs / Chat Logs. It is not ESI data and cannot be backfilled from the
        server.

        - Game logs land in GameLogEvents, one row per line, classified into a Kind
          (combat.*, movement.jumped, movement.undocked) with the original line kept in
          RawText. Lines the parser does not recognise are still stored, with Kind='unmatched'.
        - Chat logs land in ChatMessages, one row per message, deduplicated across the several
          characters who may have been sitting in the same channel.
        - Channels marked as intel channels are additionally parsed into IntelReports and
          IntelReportCharacters — the system, the number of pilots, who reported it, and any
          named pilots with the hull they were seen in.

        Two things worth knowing before answering questions from game logs:
        - The client only writes "Undocking from <station> to <system> solar system." when
          undocking from an NPC station. Undocking from a player structure produces no such
          line, so movement.undocked is silent for anyone living in a Keepstar or Fortizar.
        - For where a character is and what they are flying right now, CharacterStatuses is
          better than the logs: it is polled from ESI, covers every authenticated character
          rather than only this PC, and carries the current ship.

        ## Watching for things (alarms)

        When the capsuleer asks to be told when something happens — "let me know when…", "alert
        me if…", "tell me when…" — that is a request for an alarm, not something to answer once
        and forget. Create it with manage_alarms. An alarm outlives the conversation; a promise
        to keep an eye out does not.

        Pick the condition that fits:
        - "timer" for a time or a reminder.
        - "intel" for a pilot being reported in named systems, or within N jumps of one.
        - "ship_undock" for one of the capsuleer's own characters undocking — anywhere or from
          named places, in any ship or named hulls and classes, and optionally only when the
          ship left unfit, short of jump fuel, or short of ammunition.
        - "undocked_too_long" for a wake-up call: a named hull or class still sitting undocked
          in the system it undocked in after N seconds, in up to three escalating stages. When
          it fires you are told what to say and that ANY reply from the capsuleer resets it —
          ask, then wait.
        - "market_contract" for an item listed at or below a price.
        - "store_order" for the EVE Mail store: a new order (with the store, buyer, item, price
          and whether it is in stock or must be built), an order contracted, accepted, canceled,
          or newly fillable from stock or in build — each kind switchable.
        - "sql" for anything else — it runs a SELECT on an interval.

        For the action, "agent_notify" is what the capsuleer means by "tell me": when the alarm
        fires you receive a message with the details and simply report it. That message IS the
        prompt — do not go looking anything up to confirm it, and do not ask a follow-up.

        Two rules for a "sql" alarm, both about not being noisy:
        - Give the query a stable identifying column and name it in key_column. The alarm
          announces only keys it has not seen; nominate something that changes every run and it
          will fire on every check.
        - Do not try to filter to "since I last looked". Write the query for current state over
          a sensible recent window; the alarm works out what is new.

        ## Settings (gear icon)
        Tabs: ESI Tokens (add/manage ESI-authenticated characters via OAuth), SDE
        (import/update the EVE Static Data Export — required before item and market
        lookups work), Market (define price sources and the default asset-value and
        manufacturing-cost pricing), Timers and Polling (ESI poll intervals), Corp Top 10
        (exclude list for corp top-10 lists), AI Agent (configure this assistant: the models
        it thinks with, each chosen from its service's own list with that service's key beside
        it, and which one talks, which answers data questions and which summarises, under Roles;
        Personalisation; the voices it speaks with, in order of preference; and push-to-talk
        speech input), Alerts (toggle Overview
        alerts), Price History (regions whose market history is swept in the background —
        every type that trades in those regions is refreshed on the "Price History Sweep"
        interval in Timers, default 24h, so the opportunity tools read it from the DB),
        Database (path, backups, move/rename/repoint), and Other (the theme and the UI scale,
        50% to 200%, both this desktop's own; the scale also sits at the right end of the status
        bar, next to the background-processing link, where clicking it offers the same choices).

        ## Interactions & hidden functions (right-click menus, buttons, shortcuts)
        Many actions live in right-click context menus or row buttons that are not
        obvious from a screenshot. When the capsuleer asks "how do I…", cite the exact
        control below.

        ### Inventory Levels & Market Levels (same interaction model)
        Structure: Collections contain Groups; Groups contain Items. Items have a target
        level; each Group has a quantity multiplier, and Groups can be organized under
        Collections.
        - Toolbar: "+ Add Group" and "+ Collection" create the containers; "Refresh"
          re-pulls the underlying data.
        - Collection row buttons: expand/collapse arrows, "+"/"−" to expand/collapse all
          groups, "Rename", "Delete".
        - Group row: a toggle arrow, an editable quantity multiplier (×N), "+ Item",
          "Edit" (group scope/locality settings), and "Delete".
        - RIGHT-CLICK a row for the context menu: "Open in Item Browser" (on an item
          row), and — the main bulk-add options — "Add Items From Fit" (paste/select an
          EVE fitting to add all its modules/items), "Add Items From Market Group" (pick a
          market group from a tree to add every item in that group and its sub-groups),
          and "Add Items From Blueprint" (add a blueprint's materials). Also "Delete Item".
        - Items within a group are listed alphabetically.

        ### Item Browser
        - Left tree: browse the market-group hierarchy, or use the search box to find an
          item by name.
        - Clickable skill links (in Requirements, Required For, and in the character
          Skills tab) navigate the Item Browser to that skill.

        ### Trade Opportunities
        - DOUBLE-CLICK any result row to open that item in the Item Browser.
        - "+ Add Group" (next to the Exclude Groups chips) opens the market-group tree to
          add an exclusion; click the ✕ on a chip to remove one.

        ### Industry Opportunities
        - DOUBLE-CLICK any result row to open that item in the Item Browser.
        - "+ Add Group" (next to the Exclude Groups chips) opens the market-group tree to
          add an exclusion; click the ✕ on a chip to remove one.

        ### Production Calculator
        - DOUBLE-CLICK a material/product row to open that item in the Item Browser.
        - Right-click the results for export options: "Copy to Clipboard", "Export as
          CSV", "Tab-delimited".

        ### Corp Activity — Standing Projects
        - "+ Add Project" opens a dialog to define a standing project (deliver-item or
          destroy-NPC, with scope/ADM settings). Each row has "Edit" and "Delete".
        - RIGHT-CLICK a standing-project row: "Clone item" (duplicate the project as a
          starting point) and, for deliver-item projects, "Open Item in Item Browser".

        ### Overview
        - Alert messages are clickable — clicking one navigates to the relevant tool
          (e.g. a skill-queue alert opens that character's Skills; a standing-project
          alert opens Corp Activity > Standing Projects).

        ### Characters
        - In the Skills tab, skill names are clickable links that open the Item Browser
          for that skill.
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
