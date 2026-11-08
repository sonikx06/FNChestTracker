# Fortnite Chest Tracker

A Windows desktop app that counts the treasure chests you open in Fortnite: per match and all time.
It has global hotkeys, an in-game overlay, match history, charts, and an **experimental** screen-recognition mode.

The UI is in Russian by default and can be switched to English in Settings.

> **Fair-play design.** The app never reads or writes Fortnite's memory, never injects DLLs, never hooks the
> renderer and never interacts with Easy Anti-Cheat. It uses only public Windows APIs: `RegisterHotKey` for
> keyboard hotkeys, Raw Input (read-only copy, nothing is blocked) for Mouse4/Mouse5 hotkeys, a normal topmost desktop window for the overlay, and Windows.Graphics.Capture (or a GDI screen copy)
> for the optional screen analysis.

## Features

| Area | What works |
|---|---|
| Manual tracking | F8 adds a chest, F7 removes the last one, F9 starts a new match, F10 ends the match, F6 shows/hides the overlay. Any action can also be bound to the mouse side buttons **Mouse4 / Mouse5** (optionally with Ctrl/Alt/Shift). Dashboard buttons do the same. |
| Event log | Every change is an immutable event in SQLite. Undo appends a compensating event that targets one specific add. A unique index makes sure an add can be undone only once. Counters are always `SUM(delta)`. |
| Duplicate protection | `MOD_NOREPEAT` ignores key auto-repeat. A configurable debounce (300 ms by default) ignores double presses. Automatic detections have a cooldown. In hybrid mode a hotkey press and a detection of the same chest are merged. |
| Dashboard | Shows the current match count and timer, **all-time chests**, **total matches**, average per match, personal record, recent events and pending reviews. One button starts the match (green ▶ "Start Match") or ends it (red ■ "End Match"); a double click within 1 s is ignored, so it never creates two matches or ends a match it just started. You can also reset or manually edit the current match. All-time totals are computed from the event log, so they survive restarts and every correction changes the match and the all-time total together. |
| Overlay | Transparent, always-on-top and click-through (`WS_EX_TRANSPARENT \| WS_EX_NOACTIVATE`). Compact, standard and expanded layouts. Opacity, scale, text and accent colours, an optional background panel, and presets. Move mode lets you drag it; the position is saved. It can also be hidden from screen capture. |
| History | Every match is listed with start time, duration, chests, manual and automatic adds, corrections, tracking mode and status. You can search, filter by period, mode and minimum chests, edit a match's count, delete a match, or export to CSV. |
| Statistics | Charts for chests per match, per day, per session, and a rolling plus cumulative average. Personal bests: best match, best day, best session, best pace. |
| Settings | Hotkey rebinding (keyboard or Mouse4/Mouse5) with conflict detection (duplicate bindings, or keys already taken by another app). Global hotkey on/off switch. Overlay, tracking mode, detection sensitivity and cooldown, start with Windows, minimize/close to tray, sounds and volume, RU/EN language. |
| Data safety | WAL journal with `synchronous=FULL`. Schema migrations through `PRAGMA user_version`, with a backup before each migration. A daily backup (the newest 14 are kept). At startup a corrupt database is quarantined and restored from the newest good backup. An unfinished match is resumed after a crash if it was active within the resume window; otherwise it is closed at its last activity. JSON export/import is idempotent. Statistics reset asks you to type a confirmation word and makes a backup first. |
| Automatic mode (experimental) | See below. |

## Experimental automatic detection

**What it does.** In automatic or hybrid mode, the app captures the Fortnite window with Windows.Graphics.Capture.
It samples 1 to 10 frames per second (4 by default). Frames that arrive between samples are dropped without being copied.

Each sampled frame is processed with OpenCV:

1. Crop a region around the crosshair and scale it down to 256 px wide.
2. Measure the share of **gold** pixels (HSV range), the brightness, and the motion compared with the previous frame.
3. If you captured one, compare the region with your "Search" prompt template.

A temporal state machine (`ChestDetector`) compares these values with a slowly adapting baseline. It requires the
signal to last at least 2 frames, then turns it into a confidence score:

- **≥ confirm threshold**: the chest is counted (source = automatic).
- **Between the review and confirm thresholds**: the detection is queued on the dashboard for you to *count* or *reject*.
- **Below the review threshold, or not gold enough**: the detection is discarded. This is how ammo boxes and bright effects are rejected.

**Calibration page.** Shows a live preview with gold pixels highlighted, live measurements, a confidence graph and a
detection log. It has a test mode (detections are shown but not counted) and sliders for sensitivity, cooldown, sampling
rate, region of interest and the gold HSV range. You can also capture a prompt template there.

**Limitations (honest).**
- This is a heuristic. It will miss some chests and count some false positives, for example golden items, legendary
  loot glow or golden skins near the crosshair.
- Accuracy depends on resolution, graphics settings, the season's visuals and the colour-grading options.
- Use **hybrid** mode if you want to correct mistakes with hotkeys.
- Exclusive fullscreen may block capture. If capture is unsupported or fails, the status shows it and manual tracking
  keeps working unchanged.
- Windows 11 may show a yellow capture border when the OS doesn't allow it to be hidden.

### Experimental: YOLOv8 detection engine

**Detection → Detection engine → YOLOv8 model** replaces the gold-burst heuristic with a YOLOv8 object detector run
locally with ONNX Runtime (CPU). **No trained model is included and no accuracy has been measured yet**: you need to
label your own footage and train a model with `tools/yolo` (see `tools/yolo/README.md`). Without a model the engine
shows "Model file not found" and manual tracking keeps working.

- Classes: `chest_closed`, `chest_opening`, `chest_opened`, plus `ammo_box` / `other_container` so other containers
  are recognised and never counted.
- Each chest is tracked across frames and goes through **Closed → Opening → Opened → Counted**. It counts only after
  it was seen closed, then opening in at least 2 frames, then open in at least 2 frames. Closed → opened without a
  captured opening frame only goes to the review queue. A chest that disappears is never counted, and a chest is
  counted at most once (also when its track is lost and found again).
- The detection log shows the evidence per opening (frames per state, mean confidences, state path), and the debug
  view draws every box with its track id, state and confidence.
- Wrong counts are corrected as before: −1 (undo) or reject in the review queue.
- `tools/DetectionEval` measures precision, recall and false positives on your own recordings (annotated opening
  times) and per-class detection metrics on labeled frames.

## Multiplayer parties

Private parties share chest statistics in real time. The server uses ASP.NET Core with SignalR, and data is stored
in PostgreSQL.

**Using it (Party page / «Пати»):**
1. Enter the server address and a display name, then press "Connect". This creates an anonymous device account. No
   Epic or Fortnite credentials are used. The account secret is stored DPAPI-encrypted in `settings.json`.
2. **Create a party.** Pick 1–4 players and share the `XXXX-XXXX` invitation code. Codes are random, expire after
   24 hours by default, and can be regenerated. Join attempts are rate-limited.
3. **Shared matches.** When the owner presses F9/F10, or the buttons, a shared match starts or ends for the whole party.
   Members join automatically if they enabled that; otherwise they accept or decline. Their local match starts and
   ends in sync.
4. **Party page contents:** each member's count, team totals, online/offline status, leaderboards (today, this week,
   all time) and the shared-match history.
5. **Overlay.** Switch it to the team layout (team total, your count, teammates' counts, online dots, recent activity)
   on the Party page, or with the optional "Overlay: personal / team" hotkey.
6. **Privacy controls:** hide your counts and/or activity, leave the party, sign out on this PC, or delete all of your
   server data. Local statistics are never touched by these actions.

**Reliability:**
- Every chest goes to the local SQLite log first.
- A durable outbox (`sync_outbox`) delivers events when the server is reachable. It covers server downtime, no
  network and app restarts.
- The client reconnects automatically with backoff and refreshes its access token after a server restart.
- The server processes events idempotently by event id, so retries never double-count.
- An event recorded offline is attributed to a shared match by its own timestamp. Late delivery therefore still counts
  for the right match.

**Reported vs verified.** Every count is shown as `verified (reported)` when the two differ.
- *Reported* is the sum of everything a player's client sent, including manual corrections and implausible presses.
- *Verified* counts unique add/undo events that passed server validation: active membership, plausible timestamps
  (at most 5 min in the future and 14 days old), at least 1 s between the same player's chests, and a valid undo target.
- Manual corrections are never verified.

**Party lifetime totals.** The party total is everything the party ever counted, including chests of members who
have since left or were kicked.
- Every leave/kick and rejoin is stored as a separate membership period (`party_membership_periods`). An event
  counts for a party only if its timestamp falls inside one of the sender's periods for that party (±10 s clock-skew
  allowance, `MembershipGraceSeconds`). Chests from before joining or after leaving never count, and rejoining never
  brings old chests in twice.
- Events recorded during a membership are still delivered after leaving or being kicked (the client keeps them
  queued with their party); everything recorded after leaving is refused.
- An undo always applies to the party of the chest it removes, but only if it was recorded during a membership.
- Players who hide their counts are left out of the total; deleting an account deletes that player's events.
- Each member's own counter and the current-match total are unchanged: the match total counts every player who
  accepted the match.

**Server-side validation:**
- Party ids and roles are always taken from the database, never from the client.
- SignalR group membership is assigned by the server.
- Only owners can regenerate codes, kick members, transfer ownership and start/end shared matches.
- An event id that belongs to another player is rejected.

**Limitations:**
- Presence is tracked in memory, so the server runs as a single instance. Scaling out needs a SignalR backplane.
- "Verified" means plausible and unique per player. The server cannot prove that a chest was really opened in the game.

### Dedicated server and release builds

`ChestTracker.Server` is a standalone ASP.NET Core server. It does not need Fortnite, .NET, Visual Studio or other
development tools on the host; the only external dependency is PostgreSQL.

**Features:**
- SignalR real-time synchronization with automatic client reconnection.
- Guest sessions and username/password accounts that work from several computers (PBKDF2 hashes, per-device keys).
- Private parties with invitation codes.
- Server-side event validation and duplicate-event prevention by event ID.
- Match statistics and leaderboards.
- Tracking of online players and active rooms.
- An admin API protected by an `X-Admin-Key` header.
- File logging.
- Automatic and manual backups, with restore.
- Client/server protocol compatibility checks (outdated clients get HTTP 426).
- Rate limits.
- Forwarded headers for TLS-terminating reverse proxies (HTTPS/WSS).

**Building the release:**

```powershell
powershell -ExecutionPolicy Bypass -File .\build-release.ps1        # add -SkipTests to skip the test run
```

It produces:

```
ChestTracker-Release/
  Client/                 self-contained win-x64 desktop client
  Server/Windows/         self-contained single-file win-x64 server + start-server.cmd/.ps1
  Server/Linux/           self-contained single-file linux-x64 server + start-server.sh + systemd unit
  Server/reverse-proxy/   nginx and Caddy examples (TLS / WSS)
  Documentation/          README, SERVER-SETUP.md, CLIENT-GUIDE.md, Discord art assets
ChestTracker-<version>.zip
```

The script fails if any key, certificate, database or state file would end up in the release.
Deployment steps are in [deploy/docs/SERVER-SETUP.md](deploy/docs/SERVER-SETUP.md); the player guide is
[deploy/docs/CLIENT-GUIDE.md](deploy/docs/CLIENT-GUIDE.md).

**Client.** The client has no built-in server address. Players enter the URL their server owner gives them on the
**Server** page. That page offers:
- saved servers;
- status and ping;
- connect/disconnect and auto-connect;
- guest, login, sign-up, and turning a guest into an account;
- clear error messages.

### Running the server (development)

Required configuration (environment variables shown):

```powershell
$env:ConnectionStrings__Postgres = "Host=...;Port=5432;Username=...;Password=...;Database=chesttracker"
$env:ChestTracker__SigningKey    = "<random string of at least 32 bytes>"   # required outside Development
dotnet run --project src/ChestTracker.Server -c Release --urls http://0.0.0.0:5280
```

- Migrations run automatically at startup.
- The other options in `appsettings.json` (`ChestTracker` section) include `MaxPartySize`, `InviteTtlHours`,
  `MinSecondsBetweenAdds`, `MaxEventAgeDays` and the rate limits.
- For anything beyond a LAN, put the server behind HTTPS (a reverse proxy or Kestrel certificate). The client accepts
  `http://` only so you can test locally.

**Local development database.** `tools/pgsql` holds the portable PostgreSQL 16 binaries (ignored by git):

```powershell
tools\pgsql\bin\pg_ctl.exe -D tools\pgdata -l tools\pg.log start     # listens on 127.0.0.1:5433
tools\pgsql\bin\pg_ctl.exe -D tools\pgdata stop
```

The dev connection string is in `tools/pg-dev-connection.txt`. Server tests create and drop a throw-away database
through it, or through the `CHESTTRACKER_TEST_PG` variable. Without either, those tests are skipped.

**Simulated teammate.** A full second client with its own SQLite database, for trying parties alone:

```powershell
dotnet run --project tools/TeammateSim -- http://localhost:5280 ABCD-EFGH Boris 5 3
```

## Discord Rich Presence

The status shows in your Discord profile as **Chest Tracker**:

| Field | Content |
|---|---|
| Details | `Solo`, or `Team • 2/4 players` |
| State | `User • 12 chests this match`. The statistic shown depends on the "Main statistic" setting. Outside a match it shows the all-time total. |
| Elapsed time | Time since the current match started (Discord activity timestamps) |
| Large image | Chest Tracker logo. Its tooltip can show the lifetime chest count. |
| Small image | Solo or team badge. Its tooltip shows the tracking mode. |
| Button | Optional "GitHub" link. Set the project URL (https) once the repository exists. |

The status updates automatically when you open a chest, start or end a match, a teammate joins or leaves, the tracking
mode changes, or your nickname changes.

**Limits and behavior:**
- Changes are combined and sent at most once every 4 seconds; Discord allows about 5 updates per 20 seconds.
  Unchanged statuses are never re-sent.
- Texts are cut to Discord's limits: 2–128 characters, at most 2 buttons, labels of up to 32 characters, URLs of up
  to 512 characters.
- If Discord is not running, the app retries every 15 s and reconnects after Discord restarts. Tracking never depends
  on Discord.

**Settings → Discord Rich Presence:**
- enable/disable
- nickname (shown or hidden). Priority: 1) the name the server confirmed for your signed-in account,
  2) the nickname typed here, 3) "Player". A name cached on disk is not used as your identity until the server confirms
  it again after connecting. The settings show which name is used and where it came from.
- team info, match timer and lifetime count
- main statistic
- hide when Fortnite is not running
- the GitHub button always links <https://github.com/sonikx06/FNChestTracker/> (fixed, not a setting)
- a live preview of the status card

**How it works.** The app talks to the Discord desktop client over its local RPC pipe (`\\.\pipe\discord-ipc-N`), using
only `HANDSHAKE` and `SET_ACTIVITY`. It never asks for or stores your Discord password or token, and it never uses
OAuth. The official Discord Social SDK is a native C++ SDK that requires a Developer Portal download. This app uses
the documented local RPC transport for Rich Presence instead.

### Discord application (fixed)

The app always uses the Discord application **`1569001762663440454`** ("ChestTracker"). The ID, the GitHub button
URL and the art asset keys are constants in `src/ChestTracker.Core/Presence/DiscordApp.cs`. They are not settings,
and values saved by older versions in `settings.json` are ignored. New installs need no setup.

The images are art assets of that application (**Developer Portal → Rich Presence → Art Assets**), referenced by key:

| File | Key | Used as |
|---|---|---|
| `assets/discord/logo.png` (1024×1024) | `logo` | large image |
| `assets/discord/solo.png` | `solo` | small badge when playing solo |
| `assets/discord/team.png` | `team` | small badge in a party |

When Discord is enabled, the app checks these keys against Discord's public asset list for the application (only the
public application ID is sent). **Settings → Discord Rich Presence** then shows either "found" or which keys are
missing; a missing key is what makes Discord show a "?" placeholder. Newly uploaded assets can take a few minutes to
appear, and the Discord client may cache the old image until it restarts. To regenerate the images, run
`FortniteChestTracker.exe --export-discord-assets <dir>`.

## Overlay notes

Run Fortnite in **Windowed Fullscreen / borderless** mode. Windows does not compose other windows above a game in
exclusive fullscreen. While move mode is off, the overlay never receives mouse or keyboard input.

## Requirements

- Windows 10 version 1903 or later (window capture needs 2004 or later for the best results), or Windows 11, x64
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) to run the app
- .NET 8 SDK to build it

## Build

```powershell
dotnet restore
dotnet build -c Release
dotnet test
```

Run from source:

```powershell
dotnet run --project src/ChestTracker.App -c Release
```

## Install (portable)

Publish a framework-dependent build:

```powershell
dotnet publish src/ChestTracker.App -c Release -o publish
```

Copy the `publish` folder anywhere and start `FortniteChestTracker.exe`. No installer or admin rights are needed.
To make a self-contained build that doesn't need the .NET runtime, add `--self-contained true`. That build is larger.

To uninstall, delete the folder. Turn off "Start with Windows" first, or remove the `FortniteChestTracker` value from
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. Delete `%LOCALAPPDATA%\FortniteChestTracker` to remove your data.

## Data location

`%LOCALAPPDATA%\FortniteChestTracker\` (set the `CHESTTRACKER_DATA_DIR` environment variable to use another folder,
for example for testing)

- `chests.db` (+ `-wal`): SQLite database
- `backups\`: automatic and manual backups
- `settings.json`: settings
- `detection-template.png`: optional prompt template
- `logs\app.log`: diagnostics log

## Project structure

```
ChestTracker.sln
src/
  ChestTracker.Core/          UI-free logic (net8.0) — fully unit tested
    Models/                   event, match, session, review records
    Data/                     SqliteDatabase, Migrations, ChestRepository, BackupService,
                              DatabaseBootstrapper (integrity check + recovery), DataExporter (JSON/CSV)
    Tracking/                 TrackingEngine (single entry point for all changes), DeduplicationPolicy
    Statistics/               StatisticsCalculator (per day / session, averages, bests, filters)
    Detection/                ChestDetector state machine + tuning (experimental)
    Settings/                 AppSettings, SettingsStore, hotkey conflict detection
  ChestTracker.App/           WPF app (net8.0-windows10.0.19041.0), MVVM (CommunityToolkit.Mvvm)
    Views/                    MainWindow, Dashboard, History, Statistics, Detection, Settings, Overlay, dialogs
    ViewModels/
    Services/                 HotkeyService (RegisterHotKey), TrayService, SoundService, StartupService, dialogs
    Capture/                  WgcCaptureSource, GdiCaptureSource, FrameAnalyzer (OpenCV), DetectionService
    Controls/                 SimpleChart (OnRender charts), HotkeyBox, Pulse animation
    Themes/                   dark purple theme
    Localization/             Strings.ru.xaml (default), Strings.en.xaml, Loc
  ChestTracker.Vision/        EXPERIMENTAL YOLOv8: ONNX Runtime inference, letterbox/decoding/NMS, chest tracking,
                              Closed → Opening → Opened → Counted state machine, evaluation metrics
  ChestTracker.Protocol/      wire contracts shared by client and server, invitation codes
  ChestTracker.Sync/          SyncClient: SignalR connection, durable outbox delivery, shared-match mirroring
  ChestTracker.Server/        ASP.NET Core + SignalR + PostgreSQL (Npgsql): auth, parties, validation, leaderboards
tests/
  ChestTracker.Tests/         xUnit: counters, undo, dedup, persistence, migrations,
                              crash recovery, corruption recovery, import/export, detector, statistics, settings, outbox
  ChestTracker.Server.Tests/  real server (TestServer) + real PostgreSQL: auth, codes, capacity, idempotency,
                              validation, shared matches, ownership, privacy, deletion, presence, end-to-end clients
tools/
  TeammateSim/                simulated teammate client for manual testing
```

## Tests

`dotnet test` covers:

- **Counter logic**: auto-start, match, session and lifetime totals, undo order, never going below zero,
  corrections, editing past matches, lifetime adjustments, deletion, mode changes, review accept/reject,
  concurrent adds.
- **Deduplication**: hotkey debounce, UI clicks not debounced, remove debounce, automatic cooldown, hybrid merge in
  both directions (merged only once), clock going backwards, duplicate event ids, match boundaries.
- **Persistence**: fresh migration, restart, v1→v2 migration with backup, crash recovery (closing old matches, resuming
  recent ones), corrupt database restored from backup or recreated, export/import round-trip and idempotency, CSV,
  reset, summary counters, backup pruning.
- **Detector and statistics**: confirmed burst, cooldown, non-gold flash rejected, uncertain → review, single-frame
  spikes discarded, baseline adaptation, per day / session, averages, bests, filters.
- **Settings**: defaults, round-trip, corrupt file fallback, clamping, conflict detection.
