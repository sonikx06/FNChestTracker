# Party overlay count alignment — 2026-11-15

This follow-up changes only the player-row layout, its rendering tests and this evidence. Existing statistics,
formatting, synchronization, host indicators, local-player selection and click-through behavior are unchanged.

## Root cause

The current template already used a Grid, but its nickname column was fixed at 150 DIPs and its shared count column
used Auto with a 36-DIP minimum. The count column therefore changed width according to the largest current value.
It was not anchored to the row's right edge. In the actual pre-change WPF rendering, the count right edge moved
from X=222 with short counts to X=283 with wide counts.

## Layout

The ItemsControl now has a bounded 300-DIP width, so its star column can resolve correctly inside the overlay's
SizeToContent window. Every stretched row has columns 14 / * / 120 DIPs: status, nickname, count. The 120-DIP count
column includes a 12-DIP gap, leaving 108 DIPs for text. Counts are explicitly right-aligned and never wrap.
Names remain left-aligned, ellipsized, single-line and accessible through the existing tooltip/main application.
The team panel's minimum width increases from 280 to 300 DIPs. The rendered fixture is 338 DIPs wide including its
existing outer padding. Rows preserve their existing height, dot styling and current-player bold formatting.

Header statistics retain their existing layout; this change stabilizes player rows rather than redesigning the
aggregate-statistics header. No badges were added or removed from player rows.

## Files changed in this follow-up

- `src/ChestTracker.App/Views/OverlayWindow.xaml`
- `tests/ChestTracker.App.Tests/WindowsRenderingTests.cs`
- This report and three screenshots in `docs/validation/count-alignment`.

Other working-tree changes belong to the earlier task/user edits and were preserved.

## Verification

- Real WPF OverlayWindow rendered using its production view model and ApplyParty bindings, with test party data.
- Names: A, Boris, ОченьДлинныйНик, ДлинноеИмяИгрокаКотороеНеВлезает, qqwayne and Sonik109.
- Counts: 0, 1, 8, 123, 9999, 123 (145), 9999 (12345). Formatting remains verified (reported).
- Assertions check identical count right edges, no name/count overlap, count text fitting inside its allotted width,
  no wrapping and no change of window width or count edge during live value/name/local-player updates.
- Join/leave tests cover 1–4 members and check the same edge across membership sizes.
- Overlay scale samples: 0.5, 0.75, 1, 1.5, 2 and 3; 96/144/192-DPI render targets; WPF Ideal and Display text formatting.
- Updated WPF/OCR integration test passed; 228 core/vision/sync regression tests passed. None failed or skipped.
- Release app/test compilation and `git diff --check` passed. Existing platform/DPI warnings remain unrelated.
- Server integration tests were not rerun for this UI-only follow-up; no server/statistics source changed.

These screenshots are actual WPF renders with deterministic fixture data, not drawings or live Fortnite captures.
Live gameplay and changing the operating system's display-scale setting were not tested; DPI render targets test
rasterization, not monitor-switch behavior. The existing native click-through code was not modified.

## Screenshots

[Before: wide counts](before.png) · [Before: short counts shift left](before-short-counts.png) · [After](after.png)

Reproduce with English/Russian Windows OCR resources installed (the existing test also exercises OCR):

```powershell
$env:CHESTTRACKER_VALIDATION_DIR = Join-Path (Get-Location) 'artifacts/count-alignment'
dotnet test tests/ChestTracker.App.Tests -c Release
dotnet test tests/ChestTracker.Tests -c Release
```

The optional `overlay-before.xaml` in the artifact directory was copied from the working tree before this follow-up.
