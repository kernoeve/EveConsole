# Corp Activity

A corporation dashboard covering 24-hour and monthly activity, wallet income and expense, ratting/industry/mining taxes, donations, killmails, corp projects, and Top 10 leaderboards.

Open it from the left sidebar under **Corp / Interactions**.

## What it shows

A toolbar with a **Corporation** picker, a live status line, and a **Refresh** button sit above a set of tabs:

- **Activity (24H)** — a summary bar (players active, income, expenses over the last 24 hours), three side-by-side leaderboards (Ratting, Industry, Mining by value), and a "Recent Kills & Losses" killmail list.
- **Monthly Activity** — a per-month table (Total Income, Total Expense, Ratting Tax, Industry Tax, Project Payouts, Units Mined, Kills, Losses, Players Active) plus monthly ISK and count charts.
- **Income** and **Expense** — each has a **Summary** sub-tab (income/expense grouped by wallet reference Type, with Count and Amount) and a **Detail** sub-tab (the individual journal rows).
- **Ratting Taxes**, **Industry Taxes**, **Donations** — each has a **Summary** sub-tab (a ranked list of payers/donors by amount) and a **Detail** sub-tab (journal rows). Ratting, industry and donations also drive daily charts.
- **Mining** — a mining ledger with **Summary** (date, character, ore type, quantity, reprocessed value) and **Detail** sub-tabs.
- **Killmails** — corp kills and losses: a **Summary** sub-tab ranking characters by Kills/Losses, a daily kills/losses chart, and a **Detail** sub-tab listing individual killmails.
- **Projects** — corp projects across **Active**, **History**, and **Standing Projects** sub-tabs, with a detail panel showing project info, configuration, and a ranked contributor list (contributed / percent / payout).
- **Top 10 Lists** — leaderboards for a chosen month/year: Ratting Tax, Mining (units mined), Kills, Project Contributors, and Industry Tax. Each entry shows rank, character, amount, and share of the category total.

Most ISK figures are abbreviated (K / M / B); killmail rows use a zKillboard-style layout with ship render, system/security, victim, and final-blow columns.

## Using it

- **Corporation** — pick the corp to inspect; the first available corp is selected automatically. Changing corp reloads every tab.
- **Refresh** — forces a full reload. The dashboard also auto-refreshes: a light refresh every 60 seconds and a full refresh (including per-tab detail rows) every 5 minutes.
- **Per-tab period selectors** — the tax, donation, mining, kills, income, and expense tabs each have their own period dropdown (7 days to 1 year; most default to 30 days). The daily chart on the main view defaults to 90 days.
- **Top 10 period** — choose Month and Year; the lists reload for that window. **Export to Clipboard** and **Export (No ISK)** copy the leaderboards out.
- **Standing Projects** — add, clone, edit, delete, and refresh standing project definitions; rows flag near-complete or inactive projects, and a deliver-item row can be opened in the item browser.
- Clicking a killmail row can open it in the [Killmails](killmails.md) browser.

## Notes

- This is a corporation-scoped tool: it needs valid corp tokens with the relevant wallet, industry, mining, killmail, and projects scopes. Empty tabs usually mean the required scope or data has not been fetched yet.
- Value figures (e.g. mining reprocessed value) depend on a configured market — see [Configuring Markets](../configuring-markets.md).
- Top 10 leaderboards honour an exclude list, so specific characters can be kept out of the rankings.
- The corporation list is populated from your authorized corp tokens; see [Getting Started](../getting-started.md).
