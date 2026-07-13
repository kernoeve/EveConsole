# Trade Opportunities

Finds profitable station-to-station hauls by comparing cached sell orders at a source station against buy or sell orders at a destination, then packs the best items into a cargo-and-budget-limited shopping list.

Open it from the left sidebar under **Market / Trade**.

## What it shows

A results grid, one row per item type worth hauling, sorted by total profit by default:

| Column | Meaning |
|--------|---------|
| **Item** | Type name. Double-click a row to open it in the Item Browser. |
| **Sell Price** | Cheapest sell-order price at the source station (your buy cost per unit). |
| **Dest Price** | The destination price you sell into — the best buy-order price, or the cheapest destination sell price, depending on mode. |
| **Profit / Unit** | Dest price minus source sell price. |
| **Profit / m³** | Profit per unit divided by packaged volume — the ranking metric for the underlying scan. |
| **Qty** | Units to buy, capped by available orders, cargo space, and your ISK budget. |
| **Volume (m³)** | Total packaged volume for that quantity. |
| **Total Cost** | Quantity × source sell price. |
| **Total Profit** | Quantity × profit per unit. |

A footer summary (shown after a run) gives loaded **Volume** (used / cargo), total **Cost**, and total **Profit**. A status line reports the item-type count and m³ loaded, or messages such as no opportunities found.

## Using it

1. **Mode** — choose how the destination price is evaluated:
    - **Buy Sell → Sell to Buy Order** — buy from source sell orders, sell into destination buy orders. Only destination buy orders priced above the source cost are counted (junk 1-ISK orders are ignored).
    - **Buy Sell → Undercut Sell Order** — buy from source sell orders and resell against the destination's own sell orders (only where the destination sell price beats your cost).
2. **From** / **To** — pick the source and destination stations (type to filter). Only stations with cached orders appear; they must be different.
3. **Cargo (m³)** — your hauler's capacity (default 60000). The list is packed to fit.
4. **ISK Cap** — optional budget ceiling; leave blank for no limit.
5. **Min 30d ISK Vol** / **Min 30d Unit Vol** — optional liquidity filters that drop items whose 30-day traded volume (ISK or units) in the destination region falls below the threshold.
6. **Exclude Groups** — click **+ Add Group** to exclude a market group and everything nested under it (e.g. skip Blueprints or Ships). Excluded groups appear as chips; click **✕** to remove one. Exclusions are saved between sessions.
7. Click **Calculate**. A progress overlay shows while the scan runs.

The algorithm walks candidate items from best profit-per-m³ downward, buying as many units of each as available orders, remaining cargo, and remaining ISK allow, until the hold or budget is full. Click any column header to re-sort the results.

## Notes

- This tool **compares two markets**, so you need cached orders for **both** the source and destination stations. Make sure you have at least two market price sources configured and refreshed — see [Configuring Markets](../configuring-markets.md).
- The **Min 30d** volume filters read cached market history for the **destination region**. If that region's data hasn't been loaded, the tool cannot resolve it and the calculation stops with a message — refresh market data for the destination, or clear the filters.
- Prices reflect the last cached order sweep; they are a snapshot, not a live quote, so verify in-game before committing to a large haul.
- Related tools: [Market Overview](market-overview.md) for regional demand context, and [Market Levels](market-levels.md) for watching restock levels at a single station.
