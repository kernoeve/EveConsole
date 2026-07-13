# Error Log

A viewer for Eve Cortex's own internal error log — the errors the app records
when something goes wrong in the background (a failed ESI pull, a calculation
error, etc.). It's mainly useful for troubleshooting and bug reports.

Open it from the left sidebar under **Tools**.

## What it shows

A grid of logged application errors, newest first, with columns:

- **Time** — when the error occurred (local time).
- **Source** — the component that logged it (e.g. a view-model or service).
- **Context** — what it was doing at the time.
- **Message** — the error message.
- **Inner** — the inner-exception message, if any.

Selecting a row opens a **detail pane** below the grid showing the time, source and
context, and the full message (plus any inner exception) in a monospace,
selectable block you can copy from.

## Using it

- **From / Thru** — set an occurred-at date range (`yyyy-MM-dd HH:mm`; a plain date
  works too). It defaults to the **last 24 hours**; leave **Thru** blank for "up to
  now". Changing either field reloads automatically.
- **Refresh** — reloads the current range.
- Click a column header to **sort**; click a row to see its **details** below.
- The status text on the right shows the number of errors in range.

## Notes

- This reads the app's local error log only — it isn't EVE/ESI data and needs no
  tokens. The list is capped at 5,000 most-recent entries per load.
- If you hit a bug, the detail pane's text is the most useful thing to copy into a
  report.
