# Notifications

Browse the in-game EVE notifications (structure alerts, war updates, insurance, corp and faction messages, and more) that EveCortex has synced for your characters.

Open it from the left sidebar under **Communication**.

## What it shows

A filter bar sits above a paged grid, with a details pane below.

- **Grid columns** — **Date** (local time), **Type** (a human-readable label for the notification type), **Character** (every character the notification arrived under), **Sender**, **Sender Type** (Character / Corporation), and **Read** (Read / Unread).
- **Details pane** (below the grid) — for the selected notification: an icon (sender portrait, corp/alliance logo, or structure-type icon, with a glyph fallback), the type label, and **Date**, **Read**, **Char**, and **Sender** fields, followed by the formatted notification body. If the body can't be formatted it falls back to the raw text.
- **Unread count** — the filter bar shows an "*N* unread" tally for the current filters (this ignores the *Unread only* toggle).

The same notification is often delivered to several of your characters; the grid collapses those into one row per notification and lists all recipient characters in the **Character** column. A row counts as unread if any recipient still has it unread.

## Using it

Filtering, sorting, and paging all run against the whole notifications table, not just the current page.

- **Character** — limit to one character, or *All characters*.
- **Type** — filter to a single notification type, or *All types*.
- **Sender** — *All senders*, *Corporation*, or *Character*.
- **From** / **Thru** — date range (calendar pickers). **From** defaults to 30 days ago; **Thru** is open-ended unless set. Dates are treated as UTC.
- **Sort** — *Date: newest first* (default), *Date: oldest first*, or *Type (A → Z)*.
- **Unread only** — show only notifications with an unread recipient.
- **Clear** — reset every filter to its default (character = all, type = all, sender = all, From = 30 days ago, Thru = none, unread-only off).
- **Pager** (bottom) — **First / Prev / Next / Last** buttons with a page indicator.

Select any row to load its formatted details in the pane below.

## Notes

- Requires characters authorized with the `esi-characters.read_notifications.v1` ESI scope. Add characters and grant scopes from the [getting-started guide](../getting-started.md).
- The grid shows what EveCortex has already synced, so freshness depends on the app's last notification sync rather than a live fetch.
- For player-written mail rather than system notifications, see [Eve Mail](eve-mail.md).
