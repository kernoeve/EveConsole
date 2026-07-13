# Overview

The Overview is Eve Cortex's landing dashboard: a customizable grid of sections that summarizes activity, alerts, notifications, killmails, wallet income/expense and news across all of your authorized characters and personal corporations.

Open it from the left sidebar under **General**.

## What it shows

A toolbar sits at the top, with a customizable grid of sections below it.

**Toolbar**

- **Period** dropdown — the time window most sections report over: *Last 24 Hours*, *Last 7 Days*, *Last 30 Days* (default) or *Last 90 Days*. Your choice is remembered between sessions.
- A **status line** showing the current load step, the final result (owner count and period) or an error in red.
- **⚙ Customize Layout** — opens the layout editor (see [Using it](#using-it)).

**Sections** (any of these can be placed on the grid)

- **Activity Summary** — a two-column metric table.
    - *In Period* (bound to the Period dropdown): Market Sales (count + ISK), Market Purchases (count + ISK), Completed Jobs, Player Ship Kills, Ships Lost.
    - *Current State* (a live snapshot, not period-bound): Outstanding Sell Orders (count + value), Outstanding Buy Orders (count + value), Outstanding Contracts, Active Indy Jobs.
- **Alerts** — actionable warnings. Skill-queue alerts (empty, paused, or ending within your configured threshold) show the character's portrait and jump to that character's Skills tab when clicked. Asset-safety alerts are dismissible. Inactive standing-project alerts jump to the Standing Projects view. Which alerts appear is controlled in **Settings ▸ Alerts**.
- **Notifications** — recent in-game notifications within the period, one row per notification, shown in-game style: icon, a one-line summary and an age, with the full detail in a tooltip. Unread notifications are marked with a dot.
- **Eve Online News** — the official EVE Online news feed. Each item can be expanded in place (More/Less) and opened in your browser.
- **Personal Killmails** — kills and losses for your authorized characters over the period, with kill/loss counts and ISK totals plus a zKillboard-style list.
- **Sale Listing (Build)** and **Sale Listing (Market)** — the Sale Listing grids embedded as sections.
- **Income (Pie)** and **Expenses (Pie)** — wallet-journal totals for the period, categorized and drawn as pie charts with a total above each.
- **Income & Expense** — the Income & Expense tool embedded as a section (it keeps its own period selector).
- **Standing Projects** — a corp standing-projects grid.

!!! note
    The default layout is a 2×3 grid: Sale Listing (Build), Income & Expense and Personal Killmails across the top row; Alerts, Notifications and News across the bottom.

## Using it

- **Change the period** with the dropdown to re-scope the period-based metrics, killmails and pie charts.
- **Act on alerts** by clicking them — skill-queue rows open the relevant character's Skills tab, the standing-projects row opens Standing Projects, and asset-safety rows can be dismissed with their dismiss control.
- **Read a notification's full text** by hovering it for the tooltip.
- **Customize the layout** with the **⚙ Customize Layout** button:
    1. Set the grid size with the **Rows** and **Columns** steppers (1–25 each).
    2. Drag a section from the **Sections** palette on the left onto the grid to place it.
    3. Move or resize placed sections on the grid; a section can span multiple cells, and the editor prevents overlaps and out-of-bounds placement.
    4. Save to apply. The layout is stored per-installation and persists between sessions.

The Overview reads only your local database, so it refreshes quickly and re-loads automatically about every 60 seconds (and immediately when you change the period or layout).

## Notes

- Data covers only **authorized characters** (those you have signed in via EVE SSO) and **personal corporations**. If none are configured, the Overview reports that no characters were found.
- The figures are only as current as your last ESI sync — market, wallet, industry, contract, killmail and notification data are populated by the app's background polling.
- Player Ship Kills and Ships Lost require full killmail detail to have been fetched; without it these counts stay at zero.
- The Personal Killmails and Standing Projects sections only load their data when they are enabled on the grid.
