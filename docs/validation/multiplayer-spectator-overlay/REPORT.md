# Multiplayer history, spectator detection and overlay validation

Validation date: 2026-11-15. Changes were implemented in the existing .NET 8 WPF client, Vision state controller,
SyncClient, protocol contracts, ASP.NET Core/SignalR server and PostgreSQL queries. The SQLite personal event log,
built-in ONNX model, Discord integration, updater and hotkey assignments were not changed.

## Existing functionality and changes

The server already had `shared_matches`, `shared_match_participants`, event attribution via
`chest_events.shared_match_id`, reported/verified counts, idempotent delivery, membership periods, account-deletion
cascades and a current-party history endpoint. The client already fetched ten recent matches on the Party page.
No second history database was introduced.

The Party page now provides today/week/all-time history, 20-row paging, match identity, party name, status,
start/end/duration, team and local contributions, participant count and expandable participant details. Expanded
rows remain open across live refreshes. Personal history stays on the existing Match History page.
Stale responses from a previous party, account or filter cannot repopulate the displayed history.

A server summary query calculates completed-match count, total, average and best for the entire selected period,
independently of the current history page. It includes completed zero-chest matches, returns zeros for an empty
period, and excludes active matches. Period boundaries use match start time; the UI computes local day/Monday
boundaries and sends an absolute timestamp. The API also accepts an exclusive `to` boundary.

Both bases use the same completed-match set and historical accepted participants. Verified = validated add/undo
deltas; reported = all eligible deltas, including corrections. Hidden contributions follow the existing sharing
policy. All paired displays use verified first, with reported in parentheses when different. Verification is
server validation, not proof of a physical chest opening in Fortnite. Party lifetime counters are not used to
calculate any match-history summary.

Undo attribution now comes from the stored target event, so a mismatched submitted match ID cannot subtract from
the current match. Eligible manual corrections to an accepted historical match can update that match after its
opening window ended; they remain reported-only. Corrections naming another party's match are rejected instead
of becoming current-party lifetime deltas. Repeated acceptance/late decline cannot erase an already accepted
participant and their contribution. All existing membership, event-age, ownership and duplicate-undo checks remain.

## Persistence, migrations and historical limits

No schema migration or statistics reset was needed. Matches and contributions remain in the existing PostgreSQL
tables. Tests cover consecutive matches, leave/kick/rejoin, delayed delivery, retries, corrections, undo and restart.
The acceptance scenario passed: 13 and 6, two completed matches, total 19, average 9.5, best 13; both counting bases
agree when all events are eligible.

History is fetched from the connected server for the current party; it is not a new offline history cache.
Former participants remain in details but gain no access to the party API after leaving. Existing account deletion
removes that account's events and participation; deleting an empty party still removes its history. Hidden counts
are excluded from team totals and summaries as before. Participant identities come from stored participation, never
from the current member list; display names still use the existing player profile, not a newly invented historical
nickname snapshot. Missing old matches, previously unattributed events and old nickname snapshots are not fabricated
or backfilled. Install the updated server with the client to expose the new summary endpoint.

## Spectator detection and state handling

The supplied `Screenshot 2026-11-15 121549.png` shows **РЕЖИМ ЗРИТЕЛЯ**, not the previous guessed translation.
The production probe now runs English and Russian Windows OCR independently of the app language. It reads only the
top-centre title band, normalizes small images for OCR, filters white title text to exclude yellow player names,
and matches complete title lines with limited OCR tolerance. It does not scan chat or menus elsewhere on screen.

Both Windows OCR language features are required for a reliable bilingual negative check. A missing resource or OCR
failure keeps counting paused and produces a localized explanation. This machine has `en-US` and `ru` OCR resources;
they were exercised by the tests. Resources come from Windows language features, not the chest model.

One positive sighting immediately pauses automatic counting. The controller clears chest tracks and pending reviews,
invalidates generations for in-flight OCR/inference and checks generation and mode again at event commitment.
Unreadable frames cannot complete an opening, even during the status debounce. Manual mode and local Stop remain
authoritative. Spectator detection does not stop the shared match, disconnect synchronization or alter remote sounds.

**Control recovery is deliberately explicit:** disappearance of the banner cannot prove local control. After
spectating or prolonged unavailable checks, use **I am back in control** on the detection page after a reboot.
This rechecks the screen for a stable clean interval within the same running match. It cannot start a stopped
counter or create a match. There is no automatically verified screen cue for local-control recovery yet.

## Samples actually tested

- One real, user-supplied Russian gameplay screenshot, 493 × 673, copied to the test fixture with its source documented.
- That same screenshot rescaled to 1.5× and 2×; these are derived samples, not new gameplay recordings.
- The captured banner moved below the HUD region, which must not trigger detection.
- Synthetic English white-title images at 1280×720, 1920×1080, 2560×1440 and 3840×2160.
- Each OCR case in both Russian and English app languages.
- Existing recorded English OCR *text outputs*; their source footage is not in this checkout.
- Scripted state/inference tests for teammate openings, pending confirmations, unreadable frames, transient HUD
  changes, mode changes, manually stopped counters and stale inference.

No live Fortnite session, English spectator video, reboot video or full-resolution Russian gameplay sequence was
available or tested. One cropped screenshot plus synthetic fixtures does not establish accuracy for every Fortnite
version, HUD scale, resolution, colour filter or display mode. Positive automatic local-control detection remains
unvalidated and is not claimed.

## Overlay cause and rendered verification

The old DockPanel gave the final nickname element the remaining width, then constrained it with `MaxWidth=140`.
WPF centered that constrained element within its remaining allocation, which changed with each count's width.
Measured nickname X positions for the reproduced four-player fixture were **91, 64, 51, 89**.

The replacement uses stretched item presenters and Grids: a fixed 14-DIP status column, a fixed 150-DIP left-aligned
nickname column, and a shared auto-sized, right-aligned count column. Rows share height and vertical alignment.
Long names ellipsize without wrapping; the full name is available in the main Party view and tooltip. Bold local
player styling never changes the name origin. No host badge existed in the overlay template; the existing Party
page host badge remains intact. Window click-through/native styles and authoritative ApplyParty bindings were not
changed.

The tests render the real OverlayWindow and view model with deterministic fixture PartyStateDto data, including
short/long Latin and Cyrillic names, bold current player, online/offline status, different digit lengths and
verified/reported differences. They exercise the same ApplyParty path for 1–4 members and renamed players.
Alignment/no-overlap assertions passed at overlay scales 0.5, 0.75, 1, 1.5, 2 and 3, with 96/144/192-DPI render targets.
These are WPF layout/rasterization tests; OS display-scale changes, monitor switching and live click-through gameplay
were not separately exercised.

Screenshots of actual WPF controls using fixture data:

- [Before overlay](overlay-before.png)
- [After overlay](overlay-after.png)
- [Russian history](history-ru.png)
- [English history](history-en.png)

## Test/build results

- Initial core regression baseline: 214 passed. The first server attempt skipped 55 tests because PostgreSQL was
  stopped; after starting the existing local PostgreSQL instance, all 55 passed with no skips.
- Final solution test run: **228 core/vision/sync tests + 61 server integration tests + 1 WPF/OCR integration test =
  290 passed, 0 failed, 0 skipped**. Server tests use real PostgreSQL, HTTP/SignalR test clients and the existing
  real SQLite/SyncClient multi-device infrastructure.
- The WPF/OCR test was rerun successfully after preserving expanded history rows.
- Release solution build and DetectionEval, TeammateSim and ReleaseSigner builds passed.
- Self-contained, single-file Windows x64 client publish passed. The published ONNX model hash matches the source.
- C# compilation supplies the project's type checks; there is no separate TypeScript/type-check command.
- `git diff --check` passed. Existing nullable warnings in CounterTests, Windows capture API-platform warnings and
  the existing WinForms DPI-manifest warning are unrelated to these changes.
- No failing test remains. The requested published-executable launch smoke test was **not run**: automatic approval
  review rejected the isolated launch command with only **“blocked by policy”**. WPF tests are not represented as a
  successful launch of that published executable. Linux/server release packaging was not rerun; server code builds
  and its integration tests passed.

Reproduce on Windows with English/Russian OCR installed and the existing PostgreSQL test connection configured:

```powershell
$env:CHESTTRACKER_VALIDATION_DIR = Join-Path (Get-Location) 'artifacts/validation'
dotnet test ChestTracker.sln -c Release --logger 'trx;LogFilePrefix=validation'
dotnet build ChestTracker.sln -c Release
```

The optional `artifacts/validation/overlay-before.xaml` is obtained from the pre-change source to render the baseline.
Final TRX files are under each test project's ignored `TestResults` directory. The published client is under
`artifacts/validation/published-client`.

## Files changed by this work

- `ChestTracker.sln`, `README.md`
- `src/ChestTracker.Protocol/Contracts.cs`
- `src/ChestTracker.Server/Endpoints.cs`, `Services/PartyService.cs`
- `src/ChestTracker.Sync/SyncClient.cs`
- `src/ChestTracker.Vision/GameplayState.cs`, `MatchDetectionController.cs`
- `src/ChestTracker.App/Capture/OcrSpectatorProbe.cs`, `DetectionService.cs`
- `src/ChestTracker.App/ViewModels/PartyViewModel.cs`, `DetectionViewModel.cs`
- `src/ChestTracker.App/Views/PartyView.xaml`, `DetectionView.xaml`, `OverlayWindow.xaml`
- `src/ChestTracker.App/Localization/Strings.en.xaml`, `Strings.ru.xaml`
- `tests/ChestTracker.Tests/SpectatorTests.cs`
- `tests/ChestTracker.Server.Tests/SharedMatchHistoryTests.cs`
- `tests/ChestTracker.App.Tests/ChestTracker.App.Tests.csproj`, `WindowsRenderingTests.cs`,
  `Fixtures/ru-spectator.png`, `Fixtures/README.md`
- This report and its four screenshots.

Pre-existing user edits to `CONTRIBUTING.md` and `ChestOpeningScenarioTests.cs` were left intact. The original supplied
screenshot remains in the workspace.
