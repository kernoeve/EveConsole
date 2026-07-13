# Characters

An in-app character sheet for your authorized characters: skills and training queue, attributes, clones, medals, titles and standings.

Open it from the left sidebar under **Character**.

## What it shows

At the top is a **character selector** dropdown and a **Refresh** button. Below it, an info bar shows the selected character's portrait, name, corporation (`[TICKER] Corp Name`), Total SP, Unallocated SP, security status and the time the data was last updated. A status line and progress indicator sit along the bottom.

The rest of the sheet is a set of tabs:

- **Skills** — three panels:
    - *Skill Groups* (left): an "All Skills" entry plus one entry per skill group, each with its total SP.
    - *Skills* (center): the skills in the selected group, grouped under headers. Each skill shows its name, five level dots (filled to the trained level) and its skill points.
    - *Training Queue* (right): the active queue with an overall ETA at the top. Each entry shows its position, skill name, target level (`→ L5`), the time to train that level, a progress bar for the skill currently training, and a per-item ETA. A summary line at the bottom states how many skills are queued and when the queue finishes (or that it is paused/empty).
- **Attributes** — Intelligence, Memory, Charisma, Perception and Willpower, plus a Neural Remapping panel showing bonus remaps available, remap availability/cooldown and the last remap date.
- **Clones** — Active Clone Implants (implants fitted in the current clone) and Jump Clones (each with its name, location and fitted implants).
- **Medals** — awarded medals with title, date, reason and status, and a total count.
- **Titles** — the titles held by the character.
- **Standings** — the character's standings, each with the entity name, a type label (Faction, Corp or Agent) and the standing value, sorted from highest to lowest.

## Using it

- **Pick a character** from the dropdown to load their sheet; switching characters cancels any in-progress load and starts the new one.
- **Refresh** re-reads the selected character's data.
- On the **Skills** tab, select a group in the left panel to filter the center list (or "All Skills" to see everything). Click a skill name — in either the list or the queue — to open it in the **Item Browser**.
- Switch tabs to view attributes, clones, medals, titles and standings.

!!! note
    Opening the Characters tool from an Overview skill-queue alert selects that character and jumps straight to the Skills tab.

## Notes

- Only **authorized characters** (signed in via EVE SSO) appear in the selector.
- Almost all data is read from the local database and is only as current as the app's last ESI sync — skills, queue, attributes, clones, medals, titles and standings are populated by background polling. Some enrichments depend on additional data having been polled: corp medal titles require corp data, and standing names may fall back to placeholder IDs if the relevant SDE or ESI names aren't available.
- The portrait, and standings names not found in the SDE, are fetched from EVE's image and ESI name services and need a network connection; the sheet still loads without them.
