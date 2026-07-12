# Configuring Markets

Markets are the foundation for almost everything price-related in Eve Cortex —
Trade Opportunities, Net Worth, build costs, the Production Calculator, and the
Item Browser all read from the prices produced here.

A **price source** answers two questions:

1. **Where do orders come from?** — an entire ESI region, or a single
   player-owned structure.
2. **How is the raw order book cleaned up?** — an optional high/low percentile
   filter that trims outlier orders before a price is computed.

You then choose, once, **which source values your assets** and **which source
prices your manufacturing inputs**, and how gaps are filled when the market has no
orders for an item.

All of this lives in **Settings** — click the **⚙** gear button in the top-right
of the title bar, then open the **Market** tab.

## Key concepts

- **Price source (method)** — each source pulls order data one of two ways:
    - **Region** — all public orders for an entire region, from ESI. Use this for
      NPC trade hubs (e.g. **The Forge** for Jita). Orders placed *inside*
      player-owned structures aren't included; only regional buy orders posted from
      structures appear.
    - **Player Structure** — every order inside one player-owned structure (citadel,
      engineering complex, etc.). Requires an auth character with docking access.
      Use this for null-sec staging keeps and other private markets.
- **High/low percentile filter** — thinly-traded items (capitals, supers, titans)
  often carry a few garbage orders far from real value. Enabled per source, it
  discards the extreme *N* % of the book: sell price becomes the *N*-th-percentile
  cheapest order, buy price the *(100−N)*-th-percentile highest. Default 5 %.
- **Default pricing** — separately from the sources, you pick which source (and
  whether **Buy**, **Sell**, or **Midpoint**) drives **asset valuation** and which
  drives **manufacturing cost**.
- **Missing-price markup** — a build-cost floor for items the market can't price:
  `price = build cost × (1 + markup%)`.

> NPC-seeded sell orders (which never expire) are excluded from pricing so that
> anonymous NPC orders don't drag prices toward seed value.

## Adding a price source

1. Open **Settings ▸ Market**. Existing sources are listed on the left under
   **Price Sources**; click **Add** to create a new source, then fill in its
   details on the right.
2. In the edit panel on the right, pick a **Method**. A short note explains each
   one, and the fields below adapt to your choice.
3. Set a **Location Name** — a friendly label such as *Jita 4-4* that appears in
   the source list and the Default Pricing dropdowns.
4. Point the source at a location, depending on the method:
    - **Region** — choose from the **Region** dropdown (e.g. *The Forge*).
      Optionally set a **Station Filter** to restrict prices to one station; the
      list is populated *after* the source's first refresh.
    - **Player Structure** — enter the structure's **Location ID**; the resolved
      name appears automatically a moment after you stop typing. Don't know the ID?
      Expand **Find Location by Name…**, search, pick a result, and click **Use
      Selected**. This method also needs an **Auth Character** with docking access —
      select one from the dropdown (structure lookups fail without it).
5. Leave **Filter High/Low Orders** on (default) unless you have a reason not to,
   and adjust **Filter %** if needed (0.1–25, default 5).
6. Make sure **Enabled** is ticked, then click **Save**.
7. Click **Refresh This** to pull prices now (or **Refresh All** at the bottom to
   refresh every source). The **Last refresh** and **Status** lines report the
   result.

<!--
  SCREENSHOT SLOTS (add files to docs/images/, then uncomment):

  ![Market configuration](images/market-config.png)
  ![Price type and outlier filtering](images/market-price-type.png)
-->

## Setting default pricing

The **Default Pricing** panel (below the source list) decides how the rest of the
app turns sources into the single price it needs:

- **Asset Value** — the source and price type (**Midpoint** / **Buy** / **Sell**)
  used for Net Worth and asset valuation.
- **Manufacturing Cost** — the source and price type used when pricing build
  inputs (defaults to **Sell**, i.e. what you'd pay to buy materials).
- **Missing Price Markup** (default 15 %) — applied when the market has no sell
  orders for an item: `price = build cost × (1 + markup%)`. If there are no buy
  orders, buy falls back to sell.
- **Filter lowball buy orders below N % of build cost** (default on, 25 %) —
  drops absurd buy orders from the *buy* price calculation. Those orders still show
  in the market UI; they're just ignored when computing a price.

Click **Save Defaults** after changing anything here. Use **Recalculate** next to
**Build Costs** to rebuild stored build costs after you've changed sources or
industry setup.

## Related

- [Industry Parks](industry-parks.md) — build costs feed the missing-price markup and
  lowball filter above.
- [Tools Reference](tools-reference.md)
