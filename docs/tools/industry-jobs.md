# Industry Jobs

Monitor your active and finished industry jobs — manufacturing, research, invention, copying and reactions — across every authorized character and corporation in one grid.

Open it from the left sidebar under **Industry**.

## What it shows

The window has three parts: a detail panel at the top, a filter bar, and the jobs grid.

### Jobs grid

Each row is one industry job. Columns:

| Column | Meaning |
| --- | --- |
| Status | Job state (Active, Paused, Ready, Delivered, Cancelled, Reverted). |
| Time Remaining | Live countdown for active/paused jobs; "Ready" once the run completes. |
| Activity | Manufacturing, TE Research, ME Research, Copying, Invention, Reverse Eng., or Reactions. |
| Product | The item the job produces. |
| Runs | Runs installed. |
| Successful Runs | Runs completed so far. |
| Items Produced | Units produced (quantity per run × runs). |
| Build Cost | Estimated cost to build the produced items, from your cached build costs. |
| Market Value | Value of the output at your configured market prices (blueprint copies from invention/copying are valued from contract prices). |
| Facility | Station or structure the job runs in. |
| Installer | Character who installed the job. |
| Owner | Owning character or corporation. |
| Created | Job start date/time (UTC). |
| Completed | Actual completion if delivered, otherwise the projected end date. |

Columns are sortable and resizable. Jobs are ordered active/paused first, then ready, then the rest by end date.

### Detail panel

Selecting a row fills the top panel with that job's Owner Type, Owner, Installer, Start Date, End Date, Completed By, Activity, Status, Runs, Items and Success Chance, plus the facility (with location), and the blueprint (with its ME/TE) and product icons, the time left, and the job cost.

## Using it

The filter bar drives what the grid shows:

- **Owner** — limit to one character or corporation (options are populated from your synced data).
- **Activity** — All Activities, Manufacturing, TE Research, ME Research, Copying, Invention, Reverse Eng., or Reactions.
- **Status** — All Statuses, active, paused, ready, delivered, cancelled, or reverted.
- **Started** — a From / Thru date range on the job's start date.
- **Search** — matches the blueprint or product name.

Set your filters and click **Apply**; click **Clear** to reset them. The window opens showing **active** jobs by default. The status bar at the bottom reports the job count and how many are currently active.

## Notes

- Jobs come from the industry-job data synced for your authorized characters and corporations — sign in those tokens from [Getting Started](../getting-started.md) first, or the grid will be empty.
- **Build Cost** and **Market Value** depend on a configured market ([Configuring Markets](../configuring-markets.md)) and an industry park ([Indy Parks](../industry-parks.md)); without them those columns may be blank.
- Facility, installer and product names that aren't in local data are resolved from ESI on first load, so a name may appear as a numeric ID momentarily.
- Use the [Production Calculator](production-calculator.md) to plan new builds, and the [Industry Opportunities](industry-opportunities.md) scanner to find what to build next.
