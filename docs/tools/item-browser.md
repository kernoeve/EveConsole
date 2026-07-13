# Item Browser

A full reference browser over the EVE item database, combining SDE data (descriptions, attributes, blueprints, skills) with your synced market and price data for any type.

Open it from the left sidebar under **Assets**.

## What it shows

The window is split into a left navigation pane and a right detail pane.

### Left pane — tree and search

- A **market-group tree** of every published type, expandable by category.
- A **search box**: type at least two characters to switch from the tree to a live results list. Results show the item name and its group path, and are ranked so that names starting with your text come first.
- **Back / Forward** buttons that walk your navigation history (up to 100 items). Clicking any linked item elsewhere in the window pushes onto this history.

### Right pane — item header and detail tabs

The header shows the item icon, name, group path, and a stat strip with **Volume**, **Market Value** (labelled with the configured price type), **Build Cost**, and **Reproc Value** where available.

Below the header are detail tabs:

- **Description** — the type's in-game description (HTML stripped).
- **Attributes** — fixed type stats (Volume, Mass, Capacity, Portion Size, Base Price where applicable) followed by the item's published dogma attributes, grouped by attribute category and shown with units.
- **Requirements** — the skills required to use or build this item, with the required level (in Roman numerals). Skill names are clickable and navigate to that skill.
- **Required For** — only shown when the loaded item is itself a skill. A I–V level selector lets you pick a skill level; the tab lists the ships, modules, and other items that require this skill at that level, grouped by category. Levels that actually have items are highlighted on the selector.
- **Industry** — for a regular item: a **Produced By** section (blueprints that manufacture it, with their input materials) and a **Used In Manufacturing** section (blueprints that consume it). For a blueprint or reaction formula: an activity selector (Manufacturing, Reaction, Invention, Copying, ME Research, TE Research) showing the outcome/products, required skills, and input materials for the selected activity. All names are clickable to navigate.
- **Market Orders** — live buy and sell orders for the item from a selected market source. Sell orders show Qty, Price, Location, and Expires; buy orders additionally show Range and Min Qty. Sell orders are sorted cheapest-first, buy orders highest-first.
- **Price History** — only shown when at least one price-history region is configured. Pick a region and a period (All Time, 30, 90, or 365 days). A **Chart** sub-tab plots average / high / low price and trade volume; a **Grid** sub-tab lists Date, Volume, Avg Price, High, Low, and Orders per day.
- **Derived History** — a chart of the item's recorded daily **Market**, **Build**, and **Contract** value snapshots, filterable by period. These snapshots are captured automatically as prices refresh.

## Using it

- **Find an item** — browse the tree, or type in the search box and click a result.
- **Follow links** — clickable (underlined) item, skill, blueprint, and material names navigate to that type. Use the Back / Forward buttons to retrace your path.
- **Read stats and requirements** — use the Description, Attributes, and Requirements tabs. For a skill, the Required For tab reverses the lookup to show what needs it.
- **Trace production** — the Industry tab shows both directions of the blueprint graph and, for blueprints, the per-activity materials and skills.
- **Check the market** — on the Market Orders tab, choose a source from the **Source** dropdown to load orders. On Price History, choose region and period.

## Notes

- Tree, search, descriptions, attributes, blueprints, and skill data come from the bundled SDE and need no ESI authorization.
- **Market Value** and **Build Cost** in the header, and the values on the Derived History tab, depend on your market configuration (see [Configuring Markets](../configuring-markets.md)). If no asset-value price source is set, the Market Value figure is blank.
- The **Market Orders** tab requires at least one enabled ESI Region or player-structure market source; without one the tab shows a prompt to add a source in Settings → Market. Orders are read from synced data.
- The **Price History** tab only appears when a price-history region is configured (Settings → Price History). History for a region/type is refreshed on demand when you open it.
- Item icons load from the EVE image server, so they require an internet connection; they are optional and the rest of the page works without them.
- The [Assets](assets.md) and [Inventory Levels](inventory-levels.md) tools can open a selected item directly in this browser.
