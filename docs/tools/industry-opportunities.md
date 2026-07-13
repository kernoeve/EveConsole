# Industry Opportunities

Scan across buildable items to find profitable build-and-sell opportunities, ranked by how much profit each earns per day of manufacturing-slot time. The left-nav label is **Industry Opportunities**; the tab reads **Industry Opps**.

Open it from the left sidebar under **Industry**.

## What it shows

Each row is one item where building it and selling it turns a profit at your chosen market. Only profitable items are listed.

| Column | Meaning |
| --- | --- |
| Item | The item to build. |
| Build Cost | Cached total build cost for one unit. |
| Sell Price | The market number you'd sell into. A `*` means the item has no live sell orders and was priced from its 30-day trade history instead. |
| Profit / Unit | Sell Price − Build Cost. |
| Margin | Profit as a percentage of build cost. |
| Build Time | Time to build one unit. |
| Slot Days | Build Time expressed in days (the time one manufacturing slot is tied up). |
| Profit / Slot Day | Profit per unit ÷ Slot Days — the default sort, best first. |
| Units Sold 30d | Units traded in the pricing region over the last 30 days. |
| ISK Sold 30d | ISK traded in the pricing region over the last 30 days. |

## Using it

Set the controls along the top, then click **Calculate**:

- **Mode** — *Build & Sell Order* prices against the lowest sell order; *Build & Sell to Buy Order* prices against the highest buy order (items with no buy orders are skipped in that mode).
- **Price at** — the market pricing config to value output against (these are your configured market sources).
- **Min 30d ISK Vol** / **Min 30d Unit Vol** — optional thresholds to hide thin, illiquid items. Leave blank for no filter.
- **Skip faction** — excludes faction items (ME0 BPCs that are often not worth building). On by default.
- **Skip Non BPO traceable Items** — keeps only items whose blueprint is a buyable BPO, or is invented from one (e.g. T2 from a T1 BPO). Excludes faction/limited-run BPC items with no obtainable BPO, and "Limited Time" event items whose blueprint can no longer be bought. On by default.
- **Exclude Groups** — add market groups (and everything nested under them) to leave out of the scan; remove a chip with its ✕. Exclusions are saved between sessions.

Results default to highest **Profit / Slot Day** first; click any column header to re-sort. Double-click a row to open that item in the Item Browser. The footer summarises the result count and the pricing market, and notes how many rows were priced from 30-day history.

## Notes

- Build cost and build time are read from your cached build costs, computed for the default industry park — set one up under [Indy Parks](../industry-parks.md) and configure a market under [Configuring Markets](../configuring-markets.md) for accurate numbers.
- The 30-day volume columns and filters, and the fallback pricing for items with no sell orders, rely on market history that the app caches in the background; if it hasn't run yet, those figures may be missing.
- The ISK/unit volume filters need the pricing config's region to resolve — if it can't, the scan reports that instead of applying the filters.
- Once you've picked a target, model it in detail with the [Production Calculator](production-calculator.md), and track the resulting builds in [Industry Jobs](industry-jobs.md).
