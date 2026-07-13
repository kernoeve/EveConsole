# Assets

A searchable, filterable browser over all of your synced assets across every authorized character and corporation, with valuation and location roll-ups.

Open it from the left sidebar under **Assets**.

## What it shows

The window has a filter bar at the top, a set of tabs in the middle, and a status bar at the bottom.

### Detailed View

One row per asset stack. Columns include:

- **Owner Type**, **Owner Name** — the character or corporation that holds the item.
- **Type Name**, **Group**, **Category** — the item and its SDE classification.
- **Quantity**.
- **Value Per Unit**, **Value** — ISK valuation of the stack (see valuation notes below).
- **Build Cost** — computed build cost of the type, when available.
- **Volume**, **Total Volume**, **ISK/m³**.
- **Location Name**, **Container**, **Flag** — where the item sits, including the nested container path (up to three levels deep) and the inventory flag.
- **Solar System**, **Region Name**, **Security**, **Location Type**.
- **Is Singleton**, **Is Blueprint Copy**.
- **Type Id**, **Item Id**, **Location Id** (identifier columns).

Blueprints and products tied to active, paused, or ready industry jobs are folded into the list as extra rows, flagged with `Industry Job` in the Flag column and located at the job facility.

### By Location / By System / By Region

Three aggregation tabs that group the (filtered) assets and sum them up:

- **By Location** — totals per station/structure/system, with Item Count, Total Volume, Total Value, and ISK/m³.
- **By System** — the same totals grouped by solar system.
- **By Region** — the same totals grouped by region.

## Using it

- **Filtering** — the filter bar starts with one filter row. Pick a column, choose an operator, and type a value. Operators are: Contains, Does Not Contain, Equal, Not Equal, Greater Than, Greater Than or Equal, Less Than, Less Than or Equal. Press **+ Filter** to add more rows (up to 10); multiple rows are combined with AND. Click **Apply** (or press Enter in a value box) to run the filters, and **Clear** to reset. The same filters drive both the Detailed View and the three aggregation tabs.
- **Sorting** — click a column header. In the Detailed View sorting is applied across the whole result set in the database; in the aggregation tabs it sorts the loaded rows.
- **Paging** — results load in pages of 5,000 rows. The status bar shows how many of the total are loaded; scroll to the bottom or press **Load More** to fetch the next page.
- **Selecting and copying** — click and drag to select a rectangular block of cells (Shift-click extends the selection). Use the row-selector column at the far left to grab whole rows. Copy with Ctrl+C or the right-click **Copy** / **Copy w/Headers** menu.
- **Open in Item Browser** — double-click a row in the Detailed View, or right-click and choose **Open in Item Browser**, to jump to that type in the [Item Browser](item-browser.md).

## Notes

- Assets come from synced ESI data, so you must have authorized characters and/or corporations with the relevant asset scopes. Items only appear after an asset sync has run.
- **Valuation** depends on the asset-value price source configured in your market settings (see [Configuring Markets](../configuring-markets.md)). Blueprint copies are valued at 0; blueprint originals use the NPC base price; other items use the configured market price, falling back to build cost plus a markup when no market price is available. If no asset-value source is set, market-derived values will be missing.
- Location, system, and region names resolve from the SDE and from synced structure names. Unresolved locations show a placeholder such as `<Unresolved - Please Refresh>` until the next sync.
