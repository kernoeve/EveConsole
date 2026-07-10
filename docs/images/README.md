# Documentation images

This folder holds screenshots and images used by the project **README** and the
**GitHub Wiki**. Keeping them here (in the main repo) means they are version-controlled
and there is a single source of truth for both.

## Adding an image

1. Drop the file in this folder. Use lowercase, hyphenated names that say what the
   image shows, e.g. `market-price-config.png`, `production-calculator.png`.
2. Prefer PNG for UI screenshots. Keep them reasonably sized (≤ ~500 KB, ≤ ~1600 px wide).
3. Reference it:
   - **From the README** (relative path):
     ```markdown
     <img src="docs/images/market-price-config.png" alt="Market price configuration" width="800">
     ```
   - **From a Wiki page** (absolute raw URL — relative paths are unreliable in wikis):
     ```markdown
     ![Market price configuration](https://raw.githubusercontent.com/kernoeve/EveCortex/main/docs/images/market-price-config.png)
     ```

## A note on privacy

Screenshots may contain character names, ISK balances, corp data, or API-derived
info. Review each image before committing — anything pushed here is public.
