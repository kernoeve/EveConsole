# Contracts

Browse your corporation and personal contracts, and search the wider pool of public contracts, with full item breakdowns and pricing for each.

Open it from the left sidebar under **Market / Trade**.

The tool has two tabs: **Corporation & Personal** and **Public**. Selecting any contract shows its full details in a panel on the right.

## What it shows

Each tab lists contracts in a grid with a detail panel alongside.

**Grid columns** (Corporation & Personal): contract **#**, **Type** (Item Exchange, Auction, Courier, Loan), **Status**, **Contents**, **Issuer**, **Assignee**, **Acceptor**, **Price**, **Reward**, and **Issued** date. The **Public** grid is similar but adds a **Region** column and a **Vol m³** column and omits Assignee/Acceptor.

- **Status** is colour-coded (outstanding, in progress, finished, closed, cancelled, etc.). A still-"outstanding" contract whose expiry has passed is shown as **Expired**.
- **Contents** shows the contract title if it has one, otherwise a compact item summary (e.g. `Item ×5` or `First item +3 more`).

**Detail panel** — for the selected contract: title, type, status and availability (Public/Private); the issuer, assignee and acceptor; price, reward, collateral, buyout and volume (reward/collateral/buyout only appear when set); start and end locations (the "To" location shows for couriers); the issued, expires, accepted and completed dates; and an **Items** list marking each line as **Offered** or **Requested**, with quantity and notes such as `BPC`/`BPO`, run count, ME/TE, and whether the item is assembled.

## Using it

### Corporation & Personal tab

Filter the stored contracts with the header combos:

- **Owner** — all owners, or a specific tracked character or corporation.
- **Assignee** / **Acceptor** — narrow to a specific party.
- **Refresh** — reload from your local data.

### Public tab

Public contracts are filtered, sorted and paged against the whole stored set on the server side, so large result sets stay responsive:

- **Show** — *Active* (outstanding and not expired), *Historical*, or *All*.
- **Contract** — *All types*, *Item Exchange*, *Auction*, or *Courier*.
- **Region** — limit to one region.
- **Category** — limit to contracts containing an item in a top-level market category.
- **Item** — type an item name to match contracts containing it (matching is debounced as you type).
- **Clear** — reset the item and category filters.
- **Sort** — price (low→high / high→low), newest/oldest, reward, volume, or contents A→Z. Sorting runs across the whole filtered set, not just the visible page.
- Paging controls (**First / Prev / Next / Last**) sit at the bottom with a page/total indicator; results are shown 200 per page.

## Notes

- Corporation and personal contracts appear only after the relevant characters/corporations are authorized via EVE SSO and their contract data has been pulled; the tab shows a note when none are stored yet.
- Party names (issuer/assignee/acceptor) and station/structure locations are resolved from local caches and ESI, and cached for future sessions. A name that can't be resolved falls back to an ID.
- Only public contracts already stored locally are searchable here; the Public tab reads from that stored set rather than querying ESI live.
