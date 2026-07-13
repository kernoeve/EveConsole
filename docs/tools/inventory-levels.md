# Inventory Levels

A stockpile monitor, in the style of jEveAssets' stockpiles: define target quantities for the items you want to keep on hand, and see at a glance how much you actually have versus your targets.

Open it from the left sidebar under **Assets**.

## What it shows

A single tree-style grid organised as **Collections → Groups → Items**.

- **Collections** are optional folders that hold groups. Groups with no collection appear under a synthetic **Default** collection.
- **Groups** define *where* and *from which sources* availability is counted, plus a **multiplier** applied to every item's target in the group.
- **Items** are the tracked types, one row each, with their target and current availability.

Item rows show these columns:

- **GROUP / ITEM** — the item name (groups and collections render their name and controls here).
- **TARGET** — the per-item target quantity you set.
- **TGT TOTAL** — the effective target, i.e. TARGET × the group's multiplier.
- **AVAIL** — total available across all counted sources.
- **DIFF** — AVAIL minus TGT TOTAL, coloured green when at/over target and red when short.
- **%** — the difference as a percentage of the target.
- **ASSETS**, **IND JOBS**, **BUY ORDERS** — the availability broken down by source (assets on hand, products of active industry jobs, and quantities on market buy orders).
- **MKT PRICE**, **BUILD PRICE** — per-unit market and build prices for the type.
- **VOLUME (m³)** — per-unit volume.

The toolbar carries **+ Add Group**, **+ Collection**, and **Refresh** buttons, plus a status line.

## Using it

### Set up groups and collections

1. Click **+ Add Group** and configure it in the dialog:
   - **Scope** — where availability is counted: Station, System, Region, or Everywhere. For the first three you also pick a location; Everywhere counts across all of your holdings.
   - **Sources to include** — Assets, Industry Jobs (products), Market Buy Orders, and Contracts Buying (marked coming soon). Assets is on by default.
   - **Multiplier** — multiplies every item's target in the group (useful for "I want N sets of this").
   - Optionally assign the group to a collection.
2. Use **+ Collection** to create a named folder, then assign groups to it. Collections can be renamed, deleted, and expanded/collapsed as a whole.

### Add items to a group

Each group row has a **+ Item** button to add a single type. For bulk adds, right-click in the grid and use:

- **Add Items From Fit** — add a ship fit's hull and modules.
- **Add Items From Market Group** — add every published item under a chosen market group (you are asked to confirm for very large groups).
- **Add Items From Blueprint** — add a blueprint's materials, either the direct inputs or the whole production chain, honouring runs and material efficiency.

Items already present in the group are skipped.

### Monitor and adjust

- Edit a row's **TARGET** inline; changes are saved automatically. Edit a group's multiplier inline the same way.
- Click **Refresh** to recompute availability from current data. Availability also refreshes automatically about once a minute.
- Click a column header to sort items within their groups.
- Right-click an item and choose **Open in Item Browser** to inspect it, or **Delete Item** to remove it. Groups and collections have their own Edit / Rename / Delete controls on their rows.

## Notes

- Availability is only as current as your synced data. Counting assets, industry-job products, and market buy orders requires the relevant characters/corporations to be authorized and synced via ESI.
- **MKT PRICE** and **BUILD PRICE** come from your market configuration (see [Configuring Markets](../configuring-markets.md)); they are blank when no price is available.
- The **Contracts Buying** source is not yet functional (shown as "coming soon").
- Related tools: the [Item Browser](item-browser.md) (opened from the item context menu) and [Assets](assets.md).
