# Wallet

Wallet balances, income/expense breakdown, and the full journal and market-transaction history for a character, a corporation, or everything combined.

Open it from the left sidebar under **Finance**.

## What it shows

A toolbar with an owner picker, a period picker, and a Refresh button; a balance bar showing the selected owner's total wallet balance; and a tabbed area:

- **Overview** — two donut charts summarising the period. **Income** slices: Market Sales, NPC Bounties, Contract Sales, Other Income. **Expenses** slices: Market Purchases, Contract Purchases, Broker Fees, Transaction Tax, Industry Tax, Other Expenses. Each pie shows its own total; empty categories are omitted.
- **Journal** — a paged, sortable, filterable grid of wallet-journal entries: Date, Type, Amount (green for positive, red for negative), Balance, Owner, Div (division), and Description.
- **Market Transactions** — a paged grid of buy/sell transactions: Date, Item, Qty, Unit Price, Total (coloured by buy/sell), B/S direction, Location, Owner, and Div. When viewing all owners, a corporation's copy of a shared transaction wins over the character copy so each transaction shows once.
- **Divisions** — corporation only. Lists each of the seven wallet divisions with its name and balance.

Amounts are abbreviated (K / M / B). Balances aggregate across all wallet divisions for corporations.

## Using it

- **Owner** — choose **All Characters & Personal Corps** (the default), a single character, or a corporation (shown as `Name [TICKER]`). The Divisions tab appears only for corporation owners.
- **Period** — Last 24 Hours, 7 Days, 30 Days, or 90 Days (default 30). This bounds the Overview charts and both grids.
- **Refresh** — reloads balances, charts, divisions, and both grids for the current owner and period.
- **Journal filters** — filter by Type, Owner, Div, and a From/Thru date range; sort by date, amount, or balance. Text filters are debounced as you type; use **Clear** to reset.
- **Market Transaction filters** — filter by Item, direction (All/Buy/Sell), Location, Owner, and Div; sort by date, total, unit price, or quantity. **Clear** resets them.

Both grids are server-side paged with First/Prev/Next/Last controls, so filters and sorting apply to the entire owner+period set, not just the visible page.

## Notes

- Data comes from stored ESI wallet balances, journal, and transactions. Corporation data requires valid corp tokens with wallet scopes; the master-wallet / division names come from the corporation's division configuration.
- The **All Characters & Personal Corps** owner and the division fallbacks depend on which corporations are marked personal — see [Getting Started](../getting-started.md).
- For a categorized cross-owner summary see [Income & Expense](income-expense.md); for total worth including assets and orders see [Net Worth](net-worth.md).
