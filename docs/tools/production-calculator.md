# Production Calculator

Plan a production run end to end: queue the items you want to build, and get the full material shopping list, a multi-level job breakdown, and the build-cost-versus-market profit for each product. The left-nav label is **Production Calc**.

Open it from the left sidebar under **Industry**.

## What it shows

The screen is split into an input panel on the left and a tabbed results area on the right.

### Input panel

- **Indy Park** — the industry park whose blueprint ME/TE, skills and structure bonuses are used for the calculation. Your default park is selected automatically.
- **Add Item** — a search box that only offers items you can actually build (types that are the output of a manufacturing or reaction blueprint); raw materials and blueprints are excluded.
- **Quantity** and **ME Level** — for the item being added (ME defaults to 10).
- **Production Queue** — the list of items to build, each showing its icon, name, quantity and ME badge, with a remove button.
- **Include final BPC cost** — see [Notes](#notes).
- **Calculate** — runs the plan.

### Results tabs

- **Summary** — a cost summary (Raw Materials, Job Costs, Leftover Value, and the resulting Net Cost) plus a card per final product showing requested/produced quantities, material and job cost, unit cost, market unit price, profit and margin.
- **Raw Materials** — every base material to buy, with quantity, unit price and total cost. Includes a **Shopping List** copy button and an **Export** menu (clipboard, CSV, or tab-delimited).
- **Final Products** — a grid of the queued products with Req, Produced, ME, Mat Cost, Job Cost, Unit Cost, Total Cost, Market/Unit, Profit and Margin.
- **Intermediates** — the sub-components built along the way, with quantities needed/produced, leftover, build value and leftover value.
- **Leftovers** — items produced in excess (batch/run rounding), with their source, quantity and build-cost value.
- **Jobs** — a tree of every job in the plan. Expand a job's **Materials** to see each input's base and effective quantity per run, total, cost, source and the SDE-base-to-result formula. Shared sub-jobs needed by more than one parent (fuel blocks, reactions, etc.) appear once at the tree root rather than being duplicated.
- **Flow** — a visualization placeholder ("coming soon").

## Using it

1. Pick your **Indy Park**.
2. Type in **Add Item**, choose a result, set **Quantity** and **ME Level**, and click **Add to Queue**. Repeat for as many items as you want to plan together.
3. Optionally tick **Include final BPC cost**.
4. Click **Calculate**.
5. Review the tabs — start with **Summary** for the bottom line, use **Raw Materials** to generate a shopping list, and drill into **Jobs** to see how each number is derived.

Click an item name (or double-click a grid row) anywhere in the results to open that item in the Item Browser. The status line under Calculate reports the job and raw-material counts when done.

## Notes

- **Include final BPC cost** adds the final product's blueprint copy (its contract price) as an input cost. It is always applied for faction / BPC-only items whose blueprint can't be bought as a BPO; the toggle only matters for standard BPO items.
- Accurate costs require a configured market for prices ([Configuring Markets](../configuring-markets.md)) and an industry park for the build parameters ([Indy Parks](../industry-parks.md)).
- Item icons are fetched from the EVE image server, so they may take a moment to appear.
- To scan across many items for the best things to build, use [Industry Opportunities](industry-opportunities.md); to track jobs you've started, use [Industry Jobs](industry-jobs.md).
