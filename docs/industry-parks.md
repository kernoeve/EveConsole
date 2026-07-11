# Industry Parks

An **industry park** describes the structures you actually build in, so that
build-cost and production calculations reflect your real bonuses (material
efficiency, rig bonuses, structure type, security modifiers, and facility tax)
instead of generic values.

Open **Indy Parks** from the left sidebar under the **Industry** group.

## Key concepts

- **Park** — a named collection of structures plus the rules mapping production
  work to them. One park can be marked the **default** (shown with a ★); that's
  the park used by build-cost calculations and the Production Calculator.
- **Structure** — a single facility in the park. Each has a name, a **type**
  (Raitaru, Azbel, Sotiyo, Athanor, Tatara, or NPC Station), a **solar system**,
  a **security class** (High/Low/Null/Wormhole), a **facility tax %**, and up to
  **three rig slots**. The available rigs adapt to the structure type — engineering
  complexes (Raitaru/Azbel/Sotiyo) offer manufacturing rigs sized to the hull;
  refineries (Athanor/Tatara) offer reaction and reprocessing rigs; NPC Stations
  take no rigs.
- **Production assignments** — map each production **category** (Large Ships,
  Capital Components, Ammo and Charges, the various reaction and reprocessing
  categories, etc.) to the structure that handles it.
- **Item exceptions** — override the category assignment for one specific item
  that doesn't belong with its category's default structure.

Parks feed the system-wide **build cost** Eve Cortex calculates and stores for
every craftable item, which in turn feeds the [Production Calculator](tools-reference.md)
and the build-cost floor in [market pricing](configuring-markets.md).

> Changes save automatically as you type or pick from a dropdown — there's no
> separate Save button. After significant changes, use **Recalculate** under
> **Build Costs** in **Settings ▸ Market** to refresh stored build costs.

## Building a park

1. In **Indy Parks**, click **+ New Park**. It appears in the list on the left;
   select it to edit. Rename it in the **Park Name** box at the top.
2. Under **Structures**, click **+ Add Structure** for each facility you build in,
   and fill in:
    - **Name** — a label such as *Main Raitaru*.
    - **Type** — the structure hull; this determines which rigs are available.
    - **Solar system** and **Security** — these drive the security/system-cost
      modifiers.
    - **Tax Percentage** — the facility's job tax.
    - **Rigs** — pick up to three from the per-type dropdowns (leave a slot on
      *— empty —* if unused).
    - Remove a structure with the **✕** button on its card.
3. Under **Production Assignments**, set each category's dropdown to the structure
   that produces it. Leave a category on *— not assigned —* if you don't build it.
4. (Optional) Under **Item Exceptions**, type an item name into the search box,
   pick it from the results to add it, then choose the structure that should build
   that specific item. Remove an exception with its **✕** button.
5. To make this the park used everywhere, click **★ Set Default**.

<!--
  SCREENSHOT SLOTS (add files to docs/images/, then uncomment):

  ![Industry park setup](images/industry-park.png)
-->

## Sharing parks

Use **Export Park** to write the selected park (structures, rigs, assignments, and
exceptions) to a JSON file, and **Import** to load one back — handy for backing up
a setup or sharing a corp-standard park with alliance mates. Imported parks arrive
as new entries and never overwrite an existing one.

## Related

- [Configuring Markets](configuring-markets.md)
- [Tools Reference](tools-reference.md)
