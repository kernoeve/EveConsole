# Market Overview

A regional dashboard over your cached market data: open buy/sell order totals, sales history, and breakdowns by market group and type. Use it to see where value sits and what is actually moving in a region.

Open it from the left sidebar under **Market / Trade**.

## What it shows

Two selectors, floated at the top-right, apply to every tab:

- **Period** — All Time, Last 365 / 90 / 30 / 7 Days (default: Last 30 Days). Drives the sales-history figures.
- **Region** — All regions, or any single region you have data for. Regions are derived from your cached orders and sales history.

A status line next to the selectors shows the current region · period, or a loading/error message. The active tab auto-refreshes about every 5 minutes.

The window has five tabs:

**Summary** — a set of KPI boxes plus charts, all scoped to the selected region (or all regions):

- KPI boxes: Sell Order Count / ISK / Types, Buy Order Count / ISK / Types (from current open orders), and Sales Units / ISK / Types (driven by the selected period).
- Three pie charts: **Sell Orders by Type**, **Buy Orders by Type** (both by ISK value of the open order book), and **Sales by Market Group**. Each pie shows the top 10 slices; everything else collapses into a grey **Other** slice.
- A **Daily Sales ISK** line chart across the period.

**By Market Group** — one row per top-level market group, with Sell Order Units/ISK, Buy Order Units/ISK, and Sales Units/ISK. Sorted by sales ISK by default.

**By Type** — the same six measures but one row per individual item type.

**Sell Orders by Type** — the open sell-order book for the region, one row per type: Units and ISK.

**Buy Orders by Type** — the same for buy orders.

Numbers are abbreviated (K/M/B/T). Grid columns can be resized and sorted; the sortable value is the raw number, not the abbreviated text.

## Using it

- Pick a **Region** and **Period**, then switch tabs — each tab loads on demand for the current selection.
- On the data-grid tabs, click a column header to sort (e.g. by Sales ISK to find your best movers, or Sell Order ISK to see where inventory value is parked).
- Order KPIs and the order pies reflect **current open orders** and ignore the period; only the **Sales** figures and the daily line respond to the period selector.

!!! note
    "Sales" come from market **type history** (aggregate regional trade volume), not from your own transactions — this is region-wide activity, useful for gauging demand for a type or group.

## Notes

- The dashboard reads only **cached** market data. Configure and refresh your market price sources so orders and history are populated — see [Configuring Markets](../configuring-markets.md). A region only appears in the selector once you have order or history data for it.
- Open orders exclude NPC-seeded orders (those are filtered out by order duration), so counts and ISK reflect player activity.
- For player structures in null-sec (where an order carries no system id), the region is resolved via the structure's known location.
- Related tools: [Market Levels](market-levels.md) for watching specific items at a station, and [Trade Opportunities](trade-opportunities.md) for hauling routes.
