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
