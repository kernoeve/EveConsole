# Order Tracker

A manual ledger of outgoing orders — items you've promised to buyers — with each order's build cost and profit against the agreed purchase price.

Open it from the left sidebar under **Market / Trade**.

## What it shows

One row per order you've entered, newest first:

- **Created** — the date you added the order.
- **Type** — the item.
- **Units** — quantity.
- **Buyer** — who the order is for.
- **Est. Date** — your estimated delivery/completion date (optional).
- **Purchase Price** — the total agreed price for the order.
- **Status** — *Pending*, *Completed*, or *Canceled*.
- **Build Cost** — the item's build cost × units, from your build-cost data. Shows `—` when unavailable.
- **Profit** / **Profit %** — the purchase price minus build cost, when a build cost exists.

The status line shows the count of orders currently listed.

## Using it

This tool is entirely user-driven — nothing is pulled from ESI. You maintain the list yourself:

- **Add Order** — opens a dialog: search and pick an item type, then set units, total purchase price, buyer, an optional estimated date, and status (Pending / Completed / Canceled).
- **Edit** — change the selected order (enabled once a row is selected).
- **Delete** — remove the selected order.

Filters in the header narrow the list:

- **Status** — *Active* (pending, the default), *Completed*, *Canceled*, or *All*.
- **Created** — a from/thru date range (`yyyy-MM-dd`).
- **Type** — substring match on the item name.
- **Buyer** — substring match on the buyer.

Columns are sortable.

## Notes

- The **Build Cost**, and therefore **Profit**, only appear for items that have build-cost data available; other rows show `—`. Build costs derive from your configured market price sources — see [Configuring Markets](../configuring-markets.md).
- Orders are stored locally and are independent of your actual EVE market orders. For real completed sales pulled from your wallet and contracts, use the Sales Tracker.
