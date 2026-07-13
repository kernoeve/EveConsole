# Net Worth

Tracks the total ISK value of a character or corporation over time, broken down into the components that make it up.

Open it from the left sidebar under **Finance**.

## What it shows

A multi-line chart of daily net-worth snapshots, plus a KPI strip showing the most recent value of each component.

Each snapshot is split into eight series (also shown as KPI tiles):

- **Net Worth (Total)** — the sum of everything below.
- **Assets** — every item you own, priced from your configured market. Blueprint copies count as 0; blueprint originals use the SDE base price; items with no market price fall back to an estimated build cost plus a markup.
- **Wallet** — the sum of all wallet division balances (characters have one; corporations up to seven).
- **Industry Jobs** — the value tied up in active, paused or ready industry jobs (source blueprint value plus the value of the product being produced).
- **Sell Orders** — remaining quantity of your active sell orders times their listed price.
- **Buy Escrow** — ISK currently locked in active buy orders.
- **Contract Collateral** — collateral on your outstanding/in-progress courier contracts.
- **Contract Value** — the price of your outstanding item-exchange and auction contracts.

The KPI strip is labelled with the snapshot date (e.g. "As of 2026-07-12"). If there is no data for the selected range, the chart shows an empty-state message.

## Using it

- **Owner** — pick **Personal** (your characters plus your personal corporations, combined) or a specific non-personal corporation from the dropdown.
- **Timeframe** — Last 90 Days, Last 365 Days, Year to Date, Prior Year, or Custom Range. Choosing **Custom Range** reveals **From**/**To** boxes that accept `yyyy-MM-dd` dates.
- **Auto Range** — lets the value axis start above zero to zoom in on changes (ignored while Log Scale is on).
- **Log Scale** — switches the value axis to a base-10 log scale, useful when component values differ by orders of magnitude.

Hover any line to read the exact ISK value at that date. Snapshots are recorded automatically whenever ESI polling refreshes assets, wallet, orders, or contracts — there is no manual "record" action.

## Notes

- Asset and industry-job valuation depend on a configured market. Set one up under [Configuring Markets](../configuring-markets.md), otherwise items priced from the market fall back to estimated build costs.
- The **Industry Jobs** series deliberately excludes phantom output for ME/TE research jobs: material- and time-efficiency research (activities 3 and 4) produces no new item, so counting a product value would double-count the blueprint that is already valued once. Those jobs therefore add no output value.
- The **Personal** owner aggregates your characters and any corporations marked as personal; a corporation only appears as its own owner option when it is *not* marked personal. See [Getting Started](../getting-started.md) for the personal-corp designation.
- Corporation snapshots require valid corp tokens with the relevant asset/wallet/industry/market/contract scopes.
