# Sales Tracker

Lists everything you've sold — market (wallet) sell transactions and finished item-exchange contracts — alongside each sale's build cost, market value, and profit.

Open it from the left sidebar under **Market / Trade**.

## What it shows

The main grid has one row per sale, newest first:

- **Date / Time** — when the sale happened (UTC).
- **Type** — `Market` for a wallet sell transaction, `Contract` for a finished item-exchange contract sold for ISK.
- **Owner** — the character or corporation that made the sale.
- **Location** — the station or structure the items sold at (for contracts, the items' start location).
- **Buyer** — the client (market) or acceptor (contract) that bought from you.
- **Item(s)** — the item sold; contracts with several items show the first item plus a `+N more items` note.
- **Units** — quantity sold (`Multiple` for a multi-item contract).
- **Total Sale** — what the sale brought in.
- **Build Price** / **Market Price** — the item's build cost and market value at the time of the sale, taken from the nearest daily price snapshot. Shows `—` when no snapshot was available.
- **Profit** / **Profit %** — the sale total minus the selected cost basis (see below). Green when positive, red when negative, grey/`—` when the cost basis is unknown.

Above the grid are three rollup panels summarising the currently filtered sales:

- **Top Buyers** — buyers ranked by total ISK bought from you.
- **Profit by Market Group** — sales grouped by the item's market group (two levels up, e.g. a Revelation rolls up to *Standard Dreadnoughts*), showing summed profit and the average profit %.
- **Profit by Item** — the same profit summary grouped per item.

Both profit rollups rank by profit amount and follow the profit-basis toggle.

## Using it

Filters sit in the header bar and apply live to both the grid and the rollups:

- **From** / **Thru** — date range (`yyyy-MM-dd`). Defaults to the last 90 days; leave **Thru** blank for "up to now."
- **Owner** — *All*, *All Characters and Personal Corps* (the default), or a specific tracked character or corporation.
- **Sale Type** — *All types*, *Market*, or *Contract*.
- **Profit based on** — switches the cost basis the Profit column and both profit rollups measure against:
    - **Build** — profit versus the item's build cost (the default).
    - **Market** — profit versus the item's market value.

The grid columns are sortable. The status line shows how many sales match, and the tracker refreshes itself automatically every few minutes.

## Notes

- Sales data comes from your synced wallet transactions and contracts, so it only appears once the relevant characters/corporations are authorized via EVE SSO and their data has been pulled.
- Build cost and market value depend on daily price snapshots, which in turn rely on your configured market price sources — see [Configuring Markets](../configuring-markets.md). Sales of items with no snapshot show `—` for cost and profit.
- A corp trade a character executes is recorded once, under the corporation, to avoid double-counting.
- For sales grouped and priced for listing rather than profit review, see the Order Tracker for outgoing orders you enter yourself.
