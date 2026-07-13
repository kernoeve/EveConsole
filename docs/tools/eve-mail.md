# Eve Mail

Read your characters' EVE in-game mail and compose new messages without leaving EveCortex.

Open it from the left sidebar under **Communication**.

## What it shows

The window is split into three columns, with a toolbar across the top.

- **Toolbar** — a **Character** selector (defaults to *All Characters*), a status line ("Loading…", message count, or an error), and a **+ Compose** button.
- **Folders** (left column) — five fixed folders always present: **All Mail**, **Inbox**, **Sent**, **Corp**, and **Alliance**. When a single character is selected, that character's custom mailing-list / label folders are appended below the fixed ones.
- **Mail list** (middle column) — one row per message showing the sender portrait, sender name (or `#id` if the name is unknown), the timestamp (`yyyy-MM-dd HH:mm`, UTC), and the subject. Unread messages are shown brighter; read messages are dimmed.
- **Reading pane** (right column) — for the selected message: the 48×48 sender portrait, subject, **From**, **To**, and **Date**, and the full message body below. If nothing is selected it reads "Select a message to read it."

## Using it

1. Pick a character from the **Character** selector, or leave it on *All Characters* to pool mail from every authorized character.
2. Choose a folder on the left to filter (All Mail, Inbox, Sent, Corp, Alliance, or a custom label).
3. Click a message in the middle list to open it in the reading pane. Opening an unread message marks it read (both in EveCortex and in-game).
4. Click **+ Compose** to write a new mail.

The list auto-refreshes about once a minute, and again right after you send a message.

### Composing a mail

The **+ Compose** button opens a dialog:

- **From** — the character sending the mail. Defaults to the character currently selected in the toolbar (or your first character when on *All Characters*).
- **To** — type a character name and press **Enter** (or click **Add**) to resolve and add it as a recipient chip. Add several recipients; remove one with the **×** on its chip.
- **Subject** and **Body** — free text; the body accepts multiple lines.
- **Send** submits the mail; **Cancel** discards it. The status line reports "Mail sent." or the reason a send failed.

!!! tip
    Recipient names are resolved as you add them, so a misspelled name won't be accepted as a chip — check the spelling if a name fails to add.

## Notes

- Requires characters authorized with the EVE mail ESI scopes: `esi-mail.read_mail.v1` (read), `esi-mail.send_mail.v1` (compose/send), and `esi-mail.organize_mail.v1` (mark-as-read and labels). Add characters and grant scopes from the [getting-started guide](../getting-started.md).
- *All Characters* mode shows only the fixed folders; custom label folders appear only when a single character is selected.
- Timestamps in the list and header are UTC.
- For in-game notifications (structure attacks, war updates, etc.) rather than player mail, see [Notifications](notifications.md).
