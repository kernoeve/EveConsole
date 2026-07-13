# Income & Expense

A categorized breakdown of where your ISK comes from and where it goes, with a daily income/expense/cashflow chart across all your characters and personal corporations.

Open it from the left sidebar under **Finance**.

## What it shows

Three panels driven by your wallet journal over the selected period:

- **Income** — income categories, largest first, with the total at the top (green).
- **Expenses** — expense categories, largest first, with the total at the top (red).
- **Daily · Income · Expense · Cashflow** — a line chart with three series: daily **Income**, daily **Expense**, and a running **Cashflow** (net) line. The panel header shows the net total (income minus expense) for the period.

Categories are derived from wallet-journal reference types. The category lists are height-aware: the panels show as many rows as physically fit, and any remaining smaller categories are rolled up into a single **Other** line so the totals still add up.

## Using it

- The tool aggregates every authenticated character plus every corporation marked as personal — there is no owner picker.
- The period is driven by the shared Overview period selector rather than a local control; the status text confirms the active window (e.g. "Last 90 days").
- The panels and chart refresh automatically (roughly every five minutes) and on period changes.
- Read the category rows to see which reference types dominate; watch the Cashflow line to see whether you are net positive or negative across the period.

## Notes

- This tool reads the wallet journal only, so it reflects realized ISK flows (bounties, market transactions, fees, taxes, contract payments, etc.), not asset or order valuation. For total worth including assets and orders see [Net Worth](net-worth.md); for the full journal and per-owner transactions see [Wallet](wallet.md).
- Only characters with a stored refresh token and corporations marked as personal are included. See [Getting Started](../getting-started.md) for the personal-corp designation.
