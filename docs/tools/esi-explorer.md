# ESI Explorer

A power-user tool for browsing the raw ESI data EveCortex has synced into its local database — every wallet, asset, skill, contract, and corp table, straight from the source rows.

Open it from the left sidebar under **Tools**.

## What it shows

- **ESI Tables** (left sidebar) — a flat list of the synced ESI tables, including Wallet Balances, Wallet Journal, Wallet Transactions, Skills, Skill Queue, Attributes, Fatigue, Clone State, Jump Clones, Implants, Assets, Blueprints, Industry Jobs, Market Orders, Contracts, Contacts, Kill Mails, Standings, Mining, Notifications, Planetary Colonies, Agent Research, Loyalty Points, Medals, Titles, Roles, Fittings, Corp Divisions/Members/Roles/Titles/Medals/Structures/Starbases/Facilities, and API Call Records.
- **Data grid** (right) — the selected table's rows, with every column exactly as stored. Columns are generated from the table, so you see the raw ESI field names and values.
- **Status bar** (bottom) — row counts, e.g. "*N* rows total" or "Showing *N* of *M* rows", plus a **Load More** button when more rows remain.

Each table is shown in full (shared tables show all their contents, not just one character's).

## Using it

1. Click a table in the left list to load it. Some tables open pre-sorted (for example Wallet Journal by date descending).
2. **Sort** by clicking a column header; click the same header again to flip between ascending and descending.
3. **Filter** using the filter bar above the grid:
    - Each filter row has a **column** picker, an **operator**, and a **value** box.
    - Operators: *Contains*, *Does Not Contain*, *Equal*, *Not Equal*, *Greater Than*, *Greater Than or Equal*, *Less Than*, *Less Than or Equal*.
    - Click **+ Filter** to add more rows (up to 10); rows are combined with AND. Remove an added row with its **×**.
    - Click **Apply** (or press **Enter** in a value box) to run the filters; **Clear** removes them and reloads.
4. **Load more rows** — rows load in pages of 5,000. Scroll near the bottom of the grid, or click **Load More**, to fetch the next page.
5. **Copy** — select one or more rows and press **Ctrl+C** to copy them (with a header row) as tab-separated text, ready to paste into a spreadsheet.

## Notes

- This browses the data EveCortex has already synced from ESI into its local SQLite database; it does not make live ESI calls. What you see is as fresh as the app's last sync for that data. Syncing itself uses your characters' authorized ESI tokens, so a table only has rows for characters (and scopes) you've authorized — see the [getting-started guide](../getting-started.md).
- Values are shown as stored, which may be raw IDs or ISO timestamps rather than resolved names — this is a diagnostic view, not a formatted report. For friendlier presentations use the dedicated tools (Wallet, Contracts, [Notifications](notifications.md), etc.).
- The grid is read-only; nothing here edits the database or your characters.
