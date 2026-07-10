# Configuring Markets

Markets are the foundation for almost everything price-related in Eve Cortex —
Trade Opportunities, Net Worth, build costs, the Production Calculator, and the
Item Browser all read from the prices produced here.

A **market configuration** answers two questions:

1. **Where do orders come from?** — the region / station / structure whose order
   book is used.
2. **How is a single price per item derived from that order book?** — buy, sell,
   or midpoint; how to discard outlier "lowball/highball" orders; and whether to
   floor a price at a percentage over its build cost.

## Key concepts

- **Price type** — each config resolves an item to one number using **Buy**,
  **Sell**, or **Midpoint** of the order book.
- **Lowball / highball filtering** — thinly-traded items (capitals, supers,
  titans) often have a few garbage orders far from the real value. The config can
  trim these before computing the price.
- **Build-cost floor** — for items with no reliable market, the price can fall
  back to a percentage over the calculated build cost.
- **Default / asset-valuation config** — one config is designated as the default
  used for net-worth and asset valuation.

> NPC-seeded sell orders (which never expire) are excluded from pricing so that
> anonymous NPC orders don't drag prices to seed value.

## Steps

_This section will be completed with step-by-step screenshots._

<!--
  SCREENSHOT SLOTS (add files to docs/images/ in the main repo, then uncomment):

  ![Market configuration](https://raw.githubusercontent.com/kernoeve/EveCortex/main/docs/images/market-config.png)
  ![Price type and outlier filtering](https://raw.githubusercontent.com/kernoeve/EveCortex/main/docs/images/market-price-type.png)
-->

## Related

- [Industry Parks](Industry-Parks) — build costs feed the build-cost floor above.
- [Tools Reference](Tools-Reference)
