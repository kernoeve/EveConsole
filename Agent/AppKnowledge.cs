namespace EveCortex.Agent;

/// <summary>
/// In-depth reference describing every Eve Cortex tool — its purpose, how to use it,
/// and the key concepts behind it. Injected into the agent's system prompt so the
/// agent can explain and guide the capsuleer from real understanding rather than by
/// describing a screenshot. Keep this current when tools are added or changed.
/// </summary>
public static class AppKnowledge
{
    public const string Guide = """
        # Eve Cortex — Tool Reference

        Eve Cortex is a locally-run capsuleer companion for EVE Online. All data lives
        in a local SQLite database and is kept current by background ESI polling. The
        left sidebar opens tools as tabs, grouped into: Character, Assets, Industry,
        Market / Trade, Finance, Communication, and Tools. The gear icon (top-right)
        opens Settings.

        When the capsuleer asks what a tool is for or how to use it, answer from the
        knowledge below — do NOT just screenshot and describe what is on screen. Use
        capture_tab only when you need to read specific current on-screen values that
        you cannot get from the database tools.

        ## How data flows (under the hood)
        - Background ESI polling refreshes assets, wallet, industry, market orders,
          skills, etc. on their own timers. Data is current; never suggest refreshing
          unless explicitly asked.
        - Market price definitions: the user defines named "price sources" (a region or
          a player structure market). As orders refresh, Eve Cortex computes and stores
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

        ### Characters (Character Viewer)
        Deep per-character viewer; pick a character from the dropdown. Tabs: Skills
        (skill groups and levels, plus the training queue showing each skill's remaining
        train time and the total time to finish the queue — skill names are clickable
        links into the Item Browser), Attributes (with neural remap info), Clones (jump
        clones and implants), Medals, Titles, and Standings. Use this when a character
        is not logged into the game client.

        ## Assets tools

        ### Assets (Asset Browser)
        The full asset list across all characters and personal corporations. Search and
        filter by item name, location, and owner. Use set_asset_filter to apply filters
        programmatically.

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
        immediate container. Eve Cortex precomputes this root for every asset (walking the
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

        ### Industry Opportunities
        Compares each buildable item's cached build cost against a market price to rank
        what is worth manufacturing — weighing profit against how long a build ties up a
        job slot. Pick a market config to price against (the same Market Sources configs)
        and one of two modes: "Build & Sell Order" (build cost vs the market's lowest sell
        price) and "Build & Sell to Buy Order" (build cost vs the highest buy order). For
        each item it lists Profit/Unit, Margin, the time to build one unit (Build Time /
        Slot Days), and — the headline metric — Profit per Slot Day (unit profit divided by
        the days a single unit occupies the slot), defaulting to that column descending.
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
        and the choice has real consequences the capsuleer often asks about:
        - Fuzzwork — pre-computed percentile prices pulled from fuzzwork.co.uk. Fast, needs
          no auth, and stores almost nothing locally (one price row per item). It does NOT
          store individual orders, so the Item Browser's Market Orders tab will be empty for
          a Fuzzwork source. Good as a quick global (Jita-style) reference price.
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

        So when the capsuleer asks something like "why can't I use the Region method for my
        null-sec staging market?": it is because that market is inside a player structure,
        and ESI's region endpoint does not return orders located inside structures — it only
        sees NPC-station orders (and public regional buy orders). The fix is to define that
        source with the Player Structure method using a character that has docking access to
        the keep. The Station Filter under the Region method is only for isolating one NPC
        station within a region; it cannot reach a player structure. (Fuzzwork likewise
        cannot, since it is regional/hub data with no per-structure orders.)

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

        ## Finance tools

        ### Net Worth
        A historical chart of your net worth over time (assets, wallet, etc.).

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

        ## Communication tools

        ### Eve Mail
        Read and compose EVE mail from within Eve Cortex.

        ## Tools

        ### ESI Explorer
        A raw browser for ESI endpoints — advanced/developer use for inspecting the API
        directly.

        ## Settings (gear icon)
        Tabs: ESI Tokens (add/manage ESI-authenticated characters via OAuth), SDE
        (import/update the EVE Static Data Export — required before item and market
        lookups work), Market (define price sources and the default asset-value and
        manufacturing-cost pricing), Timers and Polling (ESI poll intervals), Corp Top 10
        (exclude list for corp top-10 lists), AI Agent (configure this assistant —
        provider, model, API key, voice/TTS, push-to-talk), Alerts (toggle Overview
        alerts), Price History (regions whose market history is swept in the background —
        every type that trades in those regions is refreshed on the "Price History Sweep"
        interval in Timers, default 24h, so the opportunity tools read it from the DB),
        and Database (path, backups,
        move/rename/repoint).

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
    /// One-line "what this tab is for" summary, matched against a tab title or tool id.
    /// Used to give the agent focused context about the view the capsuleer is on so it
    /// does not have to guess what a screenshot is showing.
    /// </summary>
    public static string TabIntent(string tabTitleOrId)
    {
        var t = (tabTitleOrId ?? "").ToLowerInvariant();
        if (t.Contains("overview"))    return "Cross-character dashboard: income/expense charts, EVE news, and clickable alerts.";
        if (t.Contains("character"))   return "Per-character viewer: skills and training queue, attributes, clones, medals, titles, standings.";
        if (t.Contains("asset"))       return "Searchable list of all assets across characters and corps, filterable by name/location/owner.";
        if (t.Contains("item"))        return "Look up any item: description, attributes, requirements, required-for (skills), industry, market orders, price history.";
        if (t.Contains("industr") && t.Contains("job")) return "All manufacturing/reaction/invention/research jobs, filterable by status/activity/owner.";
        if (t.Contains("indy") || t.Contains("park"))   return "Define industry parks (structures per item category) that drive build-cost calculations.";
        if (t.Contains("prod"))        return "Production calculator: build cost, materials, and job breakdown for a chosen blueprint/product.";
        if (t.Contains("industry_opp") || (t.Contains("industry") && t.Contains("opp"))) return "Rank buildable items by profit vs slot time: build cost vs market sell/buy price, with Profit per Slot Day.";
        if (t.Contains("market level"))return "Monitor sell-order stock levels for a defined item list on a chosen market.";
        if (t.Contains("inv") && t.Contains("level")) return "Monitor your own holdings (assets/in-build/orders) against target levels, jEveAssets-style.";
        if (t.Contains("trade"))       return "Find profitable hauling between two markets with cargo/ISK/volume constraints and group exclusions.";
        if (t.Contains("net worth"))   return "Historical chart of net worth over time.";
        if (t.Contains("wallet"))      return "Wallet transactions and journal for your characters.";
        if (t.Contains("corp"))        return "Corporation activity and finances: 24h/monthly summaries, taxes, mining, killmails, projects, standing projects, top-10 lists.";
        if (t.Contains("killmail"))    return "Corp and personal killmails with detailed kill reports.";
        if (t.Contains("mail"))        return "Read and compose EVE mail.";
        if (t.Contains("esi") || t.Contains("explorer") || t == "data") return "Raw ESI endpoint browser (advanced/developer use).";
        return "";
    }
}
