# gridsim — a headless rig for the DataGrid upward-scroll problem

`dotnet run -c Release` from this directory. Add `--trace` for a step-by-step dump.

Nothing here ships. It exists because the scroll fault could only be diagnosed by changing one
thing and re-testing, and doing that through a person driving the real app was costing a round
trip per idea.

It builds a DataGrid whose rows carry variable-height drawers — fifty rows, bodies 32-55, four
open with drawers of 1260, 810, 1188 and 774, shaped from a live log — drives it the way a wheel
and a scrollbar drag drive it, and compares what the grid believes against geometry it knows
exactly, because every height here is set rather than measured.

## What it reports

| column | meaning |
| --- | --- |
| `steps` | wheel ticks to get from the bottom back to the top. 5055px of content at 50px a tick is ~101; anything far below that means the scroll range has collapsed |
| `arrived` | whether it reached the top at all |
| `extentErr` | worst gap between the scrollbar's idea of the content height and the real one |
| `drift` | worst gap between the offset and where the view actually is. This is the sticking |
| `stuck` | steps where the offset moved and the view did not |
| `clamped` | steps where the scrollbar value was forced away from the offset |
| `badEst` | steps where RowHeightEstimate was under 1px — the grid pricing rows at nothing |

## What it has established

- **Stock Avalonia is catastrophic here**: nine wheel ticks traverse a 5055px list, because
  `RowHeightEstimate` goes negative and the extent collapses to a few hundred pixels.
- **Pinning both estimates fixes the extent**: 103 ticks, which is the arithmetic answer.
- **A second fault survives it.** During an upward walk `GetExactSlotElementHeight` reads
  `DesiredSize.Height`, and a recycled row's desired size is a full layout cycle behind:
  `DataGridDetailsPresenter.MeasureOverride` returns `ContentHeight` verbatim, and `ContentHeight`
  is only written from `DataGridRow.ArrangeOverride`. So slot 3 measures 1231 — row 7's geometry —
  while it is really 842, the walk lands inside a row that cannot hold it, and the view oscillates
  between two positions while the offset keeps moving. That is the sticking and jumping.

Tried against that third point without moving the numbers: pushing the known drawer height into
`ContentHeight` at `LoadingRow` and at `DataContextChanged`; the same plus a forced synchronous
`row.Measure`; and binding an explicit `Height` on the drawer content, which the presenter ignores
because it returns `ContentHeight` rather than its child's desired size.
