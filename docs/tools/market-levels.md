# Market Levels

Track how much of each item you care about is currently on sell orders at a chosen station, and compare its live station prices against a market price source. It answers "which of my items are running low, and how are they priced?" at a glance.

Open it from the left sidebar under **Market / Trade**.

## What it shows

Items are organised in a two-level tree inside a single grid:

- **Collections** — optional top-level folders that group related market groups. Rows without a collection fall under a synthetic **Default** folder. Collections can be expanded/collapsed, renamed, and deleted.
- **Groups** — a named watch list tied to one **station** and one **market price source**. Each group has a **multiplier** (× applied to every item's target) and an optional **max price over source %**.
- **Items** — the individual types you monitor inside a group.

For every item row the grid shows these columns:

| Column | Meaning |
|--------|---------|
| **Target** | Your per-item target quantity (editable). |
| **Tgt Total** | Target × the group multiplier. |
| **Avail** | Units currently available on sell orders at the group's station. |
| **Diff** / **%** | Available minus target total, and that as a percentage. Green when at/over target, red when short. |
| **Mkt Price** | The reference price from the group's configured market price source. |
| **St Min / St Avg / St Max** | The lowest, average, and highest sell-order prices for the item at the group's station. |
| **Diff / %** (after each of St Min/Avg/Max) | The station price minus the market price, as ISK and percent. Green if the station price is above the reference, red if below. |
| **Build Price** | Cached material cost per unit (from the build-cost cache), if available. |
| **Volume (m³)** | Packaged volume per unit. |

ISK values are abbreviated (K/M/B). A dash (—) means no data is cached yet for that item.

The toolbar shows a status line (group/item counts or messages) and a "Data from …" timestamp indicating how old the cached order data is. The grid auto-refreshes about once a minute.

## Using it

**Create a group**

1. Click **+ Add Group**.
2. Fill in the dialog: group name, optional collection, **station** (only stations with cached sell orders appear), **market price source**, an optional **max price over source %**, and a **multiplier**.
3. Click **OK**.

**Add items to a group** — use the group's **+ Item** button, or right-click for the bulk options:

- **Add Items From Fit** — pulls every module/charge/hull from a saved fitting (skips implants, boosters, and invalid slots).
- **Add Items From Market Group** — adds every published type under a chosen market group (you're warned before adding more than 100).
- **Add Items From Blueprint** — adds the materials for a blueprint, either the direct bill of materials or the whole production chain, at a given runs/ME.

Items already present in the group are skipped automatically.

**Adjust and organise**

- Edit an item's **Target** inline; it saves immediately. The group **multiplier** scales every item's target total at once.
- Click any column header to sort items within each group; click again to reverse.
- Use **+ Collection** to create a folder, then set a group's collection via its **Edit** dialog. Collection rows have **Expand all** / **Collapse all** buttons.
- Right-click an item for **Open in Item Browser** or **Delete Item** (Delete key also works on a selected item).

!!! tip
    The per-item price deltas make this useful for restock pricing: if St Min sits well below your market source, your station is the cheap seller; if it's above, there's room to undercut or a restock opportunity.

## Notes

- Each group needs a **station with cached sell orders** and a **market price source**. Configure your price sources first — see [Configuring Markets](../configuring-markets.md). If a group shows "No cached orders", run market pricing to populate order data for that station.
- The **market price source** dropdown includes an "— Asset Default —" option, which uses whatever source is set as your asset-value default.
- **Build Price** only appears for items whose build cost has been calculated and cached.
- Related tools: [Market Overview](market-overview.md) for a regional dashboard, and [Trade Opportunities](trade-opportunities.md) for station-to-station arbitrage.
