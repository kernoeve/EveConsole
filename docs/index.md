<p align="center">
  <img src="images/banner.png" alt="Eve Cortex">
</p>

# Eve Cortex

**Eve Cortex** is a local-first, free, open-source desktop companion for [EVE Online](https://www.eveonline.com/). All of your data stays on your machine in a local SQLite database, refreshed from CCP's ESI API while the app runs.

!!! note

    These docs are a work in progress alongside the app (currently Beta, `0.9.x`). Pages will fill out over time.

## New here?

Start with **[Getting Started](getting-started.md)** — download, install, first launch, and authorizing your characters.

## Setup guides

Before most tools are useful, configure a few things:

- **[Configuring Markets](configuring-markets.md)** — define which market(s) prices come from, and how per-item prices are calculated (including lowball/highball handling and build-cost-based pricing).
- **[Industry Parks](industry-parks.md)** — define the structures used for industry/build-cost calculations, including per-category structures and per-item exceptions.
- **[AI Agent (Eden)](ai-agent-eden.md)** — optional conversational assistant with access to your local data, plus optional text-to-speech and voice input.

## Functionality

Eve Cortex is organized into tools you open from the left sidebar, grouped by theme. Each tool below links to its own page with details on what it does and how to use it.

### General

- **[Overview](tools/overview.md)** — the landing dashboard: at-a-glance alerts, recent notifications and killmails, and a customizable grid of summary panels.
- **[Characters](tools/characters.md)** — an in-app character sheet (skills, attributes, and info) for your authorized characters, handy when you'd rather not log the character into the game.

### Assets

- **[Assets](tools/assets.md)** — search and browse all of your personal and corp assets across stations, structures, and containers.
- **[Item Browser](tools/item-browser.md)** — look up any item with its description, attributes, and blueprint/industry info, plus live market orders and price history for your configured markets.
- **[Inventory Levels](tools/inventory-levels.md)** — track a defined list of items (on hand, in build, on order) against target levels, similar to jEveAssets stockpiles.

### Industry

- **[Industry Jobs](tools/industry-jobs.md)** — monitor your active and finished industry jobs (manufacturing, research, reactions) across characters and corp.
- **[Indy Parks](industry-parks.md)** — define the structures you build in (type, rigs, system, tax) so build-cost and production calculations use your real bonuses.
- **[Production Calculator](tools/production-calculator.md)** — plan production runs: full build cost, materials needed, and a multi-level breakdown, with ME levels and an optional final blueprint-copy cost.
- **[Industry Opportunities](tools/industry-opportunities.md)** — scan items for build-and-sell profit, ranked by margin and profit per slot-day using your build costs and market prices.

### Market / Trade

- **[Market Levels](tools/market-levels.md)** — monitor sell-order inventory for a chosen list of items in a specific market, so you can spot stock and restock gaps.
- **[Market Overview](tools/market-overview.md)** — a regional market dashboard: order and sales summaries, breakdowns by group and type, and a daily-sales chart.
- **[Sales Tracker](tools/sales-tracker.md)** — review your completed sales and the profit on each, with a toggle for how profit is calculated.
- **[Order Tracker](tools/order-tracker.md)** — track your active and historical market orders and how they're filling.
- **[Trade Opportunities](tools/trade-opportunities.md)** — compare two markets to surface profitable hauls between them, with cargo/ISK limits, group exclusions, and profit per unit and per m³.
- **[Contracts](tools/contracts.md)** — browse your personal and corp contracts and their items, with valuations.

### Finance

- **[Net Worth](tools/net-worth.md)** — a running chart of your total value over time across wallets, assets, and jobs.
- **[Income & Expense](tools/income-expense.md)** — a categorized breakdown of where your ISK comes from and goes over a chosen period.
- **[Wallet](tools/wallet.md)** — wallet balances plus journal and transaction history for your characters and corp.

### Corp / Interactions

- **[Corp Activity](tools/corp-activity.md)** — corp-wide activity: ratting/industry/mining tax, donations, kills, projects, and Top 10 leaderboards, over 24h or monthly.
- **[Killmails](tools/killmails.md)** — your and your corp's recent kills and losses, with values and details.

### Communication

- **[Eve Mail](tools/eve-mail.md)** — read and compose EVE mail from inside the app.
- **[Notifications](tools/notifications.md)** — view your in-game EVE notifications.

### Tools

- **[ESI Explorer](tools/esi-explorer.md)** — a power-user browser for the raw ESI data the app has synced into its local database: filter, sort, and page through the underlying tables.
- **[Error Log](tools/error-log.md)** — a viewer for the app's own internal error log, filterable by date range — handy for troubleshooting and bug reports.

## Help

- **[FAQ & Troubleshooting](faq-and-troubleshooting.md)**

---

*Eve Cortex is a third-party tool and is not affiliated with or endorsed by CCP Games. EVE Online and the EVE logo are trademarks of CCP hf.*
