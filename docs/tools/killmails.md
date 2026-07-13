# Killmails

A zKillboard-style browser for your corporations' kills and losses, with a filterable list and a full detail view for each killmail.

Open it from the left sidebar under **Corp / Interactions**.

## What it shows

A two-pane layout:

- **Left pane** — a filter bar and a scrollable kill list. Each row shows the date/time and total ISK value, the victim ship render, the system with its security status (colour-coded) plus constellation and region, the victim (name / corp / alliance with logo), and the final-blow pilot (name / corp / alliance with logo). A status bar reports the number of killmails matching the current filters.
- **Right pane** — the detail for the selected killmail:
    - **Header** — victim ship, victim name/corp/alliance, time and system/region, and an ISK summary (Destroyed, Dropped, Total).
    - **Items** — fitted and cargo items grouped by slot, each with icon, quantity destroyed, quantity dropped, and estimated value.
    - **Attackers** — every attacker with portrait, ship and weapon icons, name/corp/alliance, and damage done. The final-blow pilot is tagged **★ FB** and the top-damage dealer **▲ TD** (a pilot can be both).

Losses appear alongside kills in the same list. Ship, portrait, corp, and alliance images are loaded from EVE's image server and cached.

## Using it

- **Corp** — choose **All Corps** (the default) or a specific corporation. Changing the selection reloads the list.
- **From / Thru** — a date range, defaulting to the last 90 days.
- **Character**, **Ship**, **System/Region** — free-text filters. Character matches either the victim or the final-blow pilot; System/Region matches either the system or the region name. All filters are applied together.
- **Clear** — resets the filters (date range back to the last 90 days).
- **Refresh** — reloads killmails for the selected corp.
- Click a row to load its full detail on the right; the detail is fetched on demand.

## Notes

- Killmails are corp-scoped and require valid corp tokens with killmail scopes; the corporation list is populated from your authorized corporations (see [Getting Started](../getting-started.md)).
- Item estimated values come from stored pricing, which depends on a configured market — see [Configuring Markets](../configuring-markets.md).
- A recent 24-hour view of corp kills and losses also appears in [Corp Activity](corp-activity.md), which can hand a killmail off to this browser.
