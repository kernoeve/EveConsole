# gridsim — a headless rig for the DataGrid upward-scroll problem

```bash
dotnet run -c Release --project tools/gridsim
```

`--trace` dumps every step. **Run it after touching `Views/ScrollEstimates.cs`** — that file exists
because of what this found, and nothing here ships.

It builds a DataGrid whose rows carry variable-height drawers — fifty rows, bodies 32-55, four open
with drawers of 1260, 810, 1188 and 774, shaped from a live log — drives it the way a wheel and a
scrollbar drag drive it, and compares what the grid believes against geometry it knows exactly,
because every height in the model is set rather than measured.

Four variants run against three input modes. Only `pinned + reconciled` is graded; the other three
are controls and are supposed to fail.

| column | meaning |
| --- | --- |
| `steps` | wheel ticks from the bottom back to the top. 5055px at 50px a tick is ~101; far below that means the scroll range has collapsed |
| `extentErr` | worst gap between the scrollbar's idea of the content height and the real one |
| `drift` | worst gap between the offset and where the view actually is — this is what breaks dragging |
| `stuck` | steps where the offset moved and the view did not |
| `rev` | steps where the view moved the WRONG WAY. This is the flicker |
| `jump` | largest single step. Should equal the ask |
| `clamp` | steps where the scrollbar value was forced away from the offset |
| `badEst` | steps where RowHeightEstimate was under 1px — the grid pricing rows at nothing |

## What it established

**Stock Avalonia is catastrophic here.** Nine wheel ticks traverse a 5055px list, because
`RowHeightEstimate` goes negative and the extent collapses to a few hundred pixels.

**Two independent faults, both in turning pixels into a position.**

1. *The extent.* `EdgedRowsHeightCalculated` derives the row estimate FROM the details estimate, by
   subtracting a grid-wide mean from a local sample. Whatever single value the details estimate
   holds, some window of rows has taller drawers and some shorter, so the row estimate swings with
   wherever the viewport sits: -9.0, -4.8, 1.5, 5.6, 39.6, 132.0, 337.5 across one drag, and the
   scrollbar maximum with it, 3230 to 18856 against a true 6040. No constant fixes that.
2. *The landing.* The scroll walk measures rows on the way past, and a row measured while OFF
   screen reports the height of whichever row was recycled into it — `SetDetailsVisibilityInternal`
   returns immediately when the visibility flag is unchanged, so open-to-open reuse keeps the old
   drawer height. Slot 3 read 1231 (row 7's) against its real 842: the walk landed 1181px into an
   842px row, the guard bounced it forward, the next tick put it back, and the view sat in a
   two-cycle while the offset drained. That is the sticking; the jump is it breaking out.

Arrow keys were always fine, which is what separated the two paths: they walk slots and never
convert pixels.

## Tried and rejected

Recorded so they are not tried twice. None moved the numbers:

- pushing the known drawer height into `ContentHeight` at `LoadingRow`, and again at `DataContextChanged`
- the same plus a forced synchronous `row.Measure`
- binding an explicit `Height` on the drawer content — the presenter returns `ContentHeight`, not its child's desired size
- clearing `_appliedDetailsVisibility` and `_detailsDesiredHeight` on recycle, so Avalonia's own re-measure path runs

And two that broke it outright: driving `UpdateDisplayedRows` from a scroll handler rather than from
`LayoutUpdated`, and cancelling the grid's pending scroll so it would stop placing the view. Both
corrupt `DisplayData` — it cannot be relocated a long way from outside a layout pass.

## What passes

`pinned + reconciled`, on all three modes: 103 steps of exactly 50px, `extentErr` 1, and zero for
drift, stuck, reverse and clamping. That is `Views/ScrollEstimates.cs`.

## ⚠️ What CLEAN here does not mean

The first version of this rig said CLEAN for a candidate that then corrupted the real grid: rows
vanished, the grid locked, and the window stopped laying out entirely — a blank Overview tab, not
just a blank list.

It has since been rebuilt to drive **real input** (`MouseWheel`, and a real press-drag-release on
the scrollbar thumb) through **real layout passes** (`ForceRenderTimerTick`, not a fixed count of
`RunJobs`), with burst mode for holding the wheel down while the grid is behind, and invariants that
a live grid must keep: every claimed slot names a row, that row's index matches, `NegVerticalOffset`
sits inside its row, and the displayed rows cover the viewport. Exceptions thrown inside a layout
pass are counted rather than allowed to end the run.

**It still scores the takeover at zero faults.** So the rebuild did not fix the rig's blind spot; it
established that the blind spot cannot be closed from here. The difference between this rig and the
app — six grids in tabs, a `DataGridCollectionView`, refreshes that renumber every row, resizes — is
where the failure lives, and reproducing all of it is a bigger job than the fault is worth.

⚠️ **The standing rule that follows: nothing that writes to `DisplayData`, or that relocates the
displayed set from a layout callback, ships — whatever this rig says.** `+ takeover [REJ]` stays in
the list to keep scoring zero, as a reminder that a green line here is not a safety argument.

## Candidates measured

| candidate | verdict |
| --- | --- |
| `stock Avalonia` | catastrophic — nine wheel ticks cross a 5055px list, `RowHeightEstimate` negative on every step |
| `estimates pinned` | **shipped.** Fixes the extent: `badEst` 40 → 0, `clamp` 20 → 0. Leaves the landing fault |
| `+ takeover [REJ]` | scores clean here, destroyed the app. Permanently rejected |
| `whenSelected` | `VisibleWhenSelected` skips Avalonia's large-jump shortcut. Measured: no better than pinned, in any mode |
| `uniform drawers` | a fixed-height drawer scrolling inside. Measured: **worse** — 13 reverse steps against 3. The look that was rejected would not have fixed this either |
