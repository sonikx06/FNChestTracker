# Contributing and developer guide

Thanks for your interest in Fortnite Chest Tracker. This page is for people who want to build, test or change the
code. Players only need the [README](README.md) and the [latest release](https://github.com/sonikx06/FNChestTracker/releases).

## Requirements

- Windows 10/11 x64
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Optional, for the server and its tests: PostgreSQL 13+ (a portable PostgreSQL in `tools/pgsql` works; it is not in git)
- Optional, for training the chest model: Python 3.10+ with Ultralytics (see `tools/yolo/README.md`)

## Build, run and test

```powershell
dotnet restore
dotnet build -c Release
dotnet test                                            # unit tests; server tests need PostgreSQL (see below)
dotnet run --project src/ChestTracker.App -c Release   # run the desktop app from source
```

The built-in chest model (`src/ChestTracker.App/models/best.onnx`) is part of the repository and is copied next to the
executable on build and publish.

**Server tests** run the real server against a throw-away PostgreSQL database. Point them at a server with the
`CHESTTRACKER_TEST_PG` environment variable (a connection string with rights to create databases) or a
`tools/pg-dev-connection.txt` file (ignored by git). Without either, they are skipped.

```powershell
tools\pgsql\bin\pg_ctl.exe -D tools\pgdata -l tools\pg.log start     # portable PostgreSQL on 127.0.0.1:5433
tools\pgsql\bin\pg_ctl.exe -D tools\pgdata stop
```

## Release build

```powershell
powershell -ExecutionPolicy Bypass -File .\build-release.ps1        # add -SkipTests to skip the test run
```

It runs all tests, publishes the self-contained Windows client and the Windows/Linux servers, checks that no key,
certificate, database or state file is included, and writes:

- `ChestTracker-Release/` with `Client/`, `Server/Windows/`, `Server/Linux/`, `Server/reverse-proxy/`, `Documentation/`
- `dist/FortniteChestTracker-win-x64.zip` (the player download), `dist/FortniteChestTracker-Server-win-x64.zip`,
  `dist/FortniteChestTracker-Server-linux-x64.zip` and `dist/SHA256SUMS.txt`

## Running a server for development

```powershell
$env:ConnectionStrings__Postgres = "Host=...;Port=5432;Username=...;Password=...;Database=chesttracker"
$env:ChestTracker__SigningKey    = "<random string of at least 32 bytes>"   # required outside Development
dotnet run --project src/ChestTracker.Server -c Release --urls http://0.0.0.0:5280
```

Migrations run automatically. Production deployment (Windows, Linux, Docker PostgreSQL, HTTPS reverse proxy) is
described in [deploy/docs/SERVER-SETUP.md](deploy/docs/SERVER-SETUP.md).

**Simulated teammate** — a full second client with its own database, for trying parties alone:

```powershell
dotnet run --project tools/TeammateSim -- http://localhost:5280 ABCD-EFGH Boris 5 3
```

**Detection evaluation** on your own recordings or labelled frames:

```powershell
dotnet run --project tools/DetectionEval -c Release -- video --model src/ChestTracker.App/models/best.onnx --video clip.mp4 --events openings.txt
dotnet run --project tools/DetectionEval -c Release -- frames --model src/ChestTracker.App/models/best.onnx --images images/val --labels labels/val
```

## Pull requests

- Keep changes focused and add or update tests (`tests/ChestTracker.Tests`, `tests/ChestTracker.Server.Tests`).
- Never commit secrets, databases, logs, `settings.json` files or gameplay recordings; `.gitignore` covers the usual
  paths and `build-release.ps1` refuses to package them.
- The app must stay fair-play: no game-memory access, no injection, no input automation, no anti-cheat interaction.

## Technical reference

### Feature details

| Area | What works |
|---|---|
| Manual tracking | F9 starts a match and, pressed again, ends it (counting runs only during a match); F8 adds a chest, F7 removes the last one, F10 ends the match, F6 shows/hides the overlay. Any action can also be bound to the mouse side buttons **Mouse4 / Mouse5** (optionally with Ctrl/Alt/Shift). Dashboard buttons do the same. |
| Event log | Every change is an immutable event in SQLite. Undo appends a compensating event that targets one specific add. A unique index makes sure an add can be undone only once. Counters are always `SUM(delta)`. |
| Duplicate protection | `MOD_NOREPEAT` ignores key auto-repeat. A configurable debounce (300 ms by default) ignores double presses. Automatic detections have a cooldown. In hybrid mode a hotkey press and a detection of the same chest are merged. |
| Dashboard | Shows the current match count and timer, **all-time chests**, **total matches**, average per match, personal record, recent events and pending reviews. One button starts the match (green ▶ "Start Match") or ends it (red ■ "End Match"); a double click within 1 s is ignored, so it never creates two matches or ends a match it just started. You can also reset or manually edit the current match. All-time totals are computed from the event log, so they survive restarts and every correction changes the match and the all-time total together. |
| Overlay | Transparent, always-on-top and click-through (`WS_EX_TRANSPARENT \| WS_EX_NOACTIVATE`). Compact, standard and expanded layouts. Opacity, scale, text and accent colours, an optional background panel, and presets. Move mode lets you drag it; the position is saved. It can also be hidden from screen capture. |
| History | Every match is listed with start time, duration, chests, manual and automatic adds, corrections, tracking mode and status. You can search, filter by period, mode and minimum chests, edit a match's count, delete a match, or export to CSV. |
| Statistics | Charts for chests per match, per day, per session, and a rolling plus cumulative average. Personal bests: best match, best day, best session, best pace. |
| Settings | Hotkey rebinding (keyboard or Mouse4/Mouse5) with conflict detection (duplicate bindings, or keys already taken by another app). Global hotkey on/off switch. Overlay, tracking mode, detection sensitivity and cooldown, start with Windows, minimize/close to tray, sounds and volume, RU/EN language. |
| Data safety | WAL journal with `synchronous=FULL`. Schema migrations through `PRAGMA user_version`, with a backup before each migration. A daily backup (the newest 14 are kept). At startup a corrupt database is quarantined and restored from the newest good backup. An unfinished match is resumed after a crash if it was active within the resume window; otherwise it is closed at its last activity. JSON export/import is idempotent. Statistics reset asks you to type a confirmation word and makes a backup first. |
| Automatic mode | YOLOv8 chest model included; see below. |

### Automatic detection (built-in chest model)

**What it does.** In automatic or hybrid mode, while a match is running, the app captures the Fortnite window with
Windows.Graphics.Capture at 8 frames per second and runs the built-in YOLOv8 chest model on each frame. Game memory is
never read, no DLLs are injected and Easy Anti-Cheat is not touched. Exclusive fullscreen may block capture; if
capture fails, the status shows it and manual tracking keeps working. Windows 11 may show a yellow capture border.

**Counting only inside a match.** The match hotkey (F9 by default, rebindable, Mouse4/Mouse5 supported) toggles
counting: first press starts a match, second press ends it (for example right after you die), third press starts the
next one, and so on. The dashboard's Start/End Match button does the same on the same state. While no match is running:
- the model is loaded but no frames are captured or analysed (Detection status: "Paused — start a match to count");
- chest hotkeys and the +1 button do not count and never start a match by themselves;
- corrections, undo and the review queue of finished matches still work.

Every start or end of a match begins a fresh tracking period: all chest tracks and half-confirmed openings are dropped,
frames that were being analysed when you stopped are discarded, and the engine refuses any detection that belongs to a
match that is no longer running (checked under its lock), so nothing is ever added to an ended match.

#### The built-in model

**Model.** `best.onnx` ships with the app (`models\best.onnx` next to the executable; source:
`src/ChestTracker.App/models/best.onnx`) and is the only detector: there is no engine choice, no model path and no
import. Old settings that chose the heuristic engine or another model file are ignored and reset on load. If the file
is missing or damaged, the Detection page and the log say so ("The built-in chest model is missing… / could not be
loaded; the file is probably damaged… Reinstall the app.") and manual tracking keeps working; there is no fallback to
another detector. (Replacing the file on disk is still possible; this is not copy protection.)

It is an Ultralytics YOLOv8n export (8.4.208): input `images` float32 [1, 3, 640, 640], output `output0` float32
[1, 6, 8400] (box + 2 class scores, no NMS in the model), classes `0: closed_chest` and `1: open_chest` read from the
model's metadata.

**Inference.** ONNX Runtime with DirectML: any DirectX 12 GPU (for example an RTX 2060) without installing CUDA. If the
GPU cannot be initialised or fails its first inference, the CPU is used automatically (the model line on the Detection
page shows which, and the log says why). One session is created per model and reused. Frames are captured at 8 FPS and
handed to a separate inference thread; if inference is busy, the newest frame replaces the waiting one, so neither
capture nor the UI ever waits for the model. Preprocessing: letterbox to 640 × 640 (gray 114 padding), BGRA → RGB,
0..1, CHW; decoding: confidence threshold, class-aware NMS, boxes mapped back to screen pixels.

**When a chest counts.** Each chest is tracked across frames:
`UNKNOWN → CLOSED → OPENED → COUNTED`.
- CLOSED: the chest was seen closed in at least 2 frames (closed detections ≥ 0.35).
- OPENED → COUNTED: the same tracked chest is then seen open in consecutive frames within 2 s (open detections
  ≥ 0.40), scored on its 2 best open frames. If the first 2 are not clear enough to count (the lid animation and loot
  burst blur them), up to 4 open frames are used before deciding. That produces exactly one chest event; the chest is
  then marked counted and never counts again.
- A chest seen open before it was confirmed closed (already looted, or first seen mid-animation) never counts, even if
  the model later flickers to "closed" on it. Looking at a closed chest, or a chest disappearing, never counts.
- Tracks follow chests through camera movement (IoU against the last and the motion-predicted box, then centre
  distance for same-class detections when the match is unambiguous), survive up to 2 s / 8 frames of missed
  detections, and are never handed from a closed chest to a different open chest (a closed → open change must be
  seen in place). Two nearby chests stay separate; an ambiguous detection starts its own track.
- A chest reported as closed and open in the same frame is one chest: the more confident state is used.
- A re-created track at the place of a chest counted in the last 10 s is the same chest and is not counted again.
- There is no global cooldown: different chests opened in quick succession each count.
- Confidence of an opening (mean open-chest probability) ≥ 0.55 counts automatically, 0.40–0.55 goes to the review
  queue (sensitivity 50; the sensitivity slider moves both).

**Counting.** A confirmed opening is a normal automatic chest event (`TrackingEngine.AddChestAutomatic`): one event
id, current match, lifetime, history, undo, outbox and party statistics like any other chest. In hybrid mode a hotkey
press within the merge window (2.5 s) of a detected opening, in either order, counts once.

**Diagnostics.** The detection log shows the evidence per opening and the debug view draws every box with its track
id, state and confidence. For a detailed log set `"DiagnosticLog": true` in the `Detection` section of
`settings.json`: state transitions, and rejected detections with the reason (at most every 2 s).

**Measured on gameplay (November 2026, 8 Discord clips of ~30 s, 1080p, about 4 minutes).** Openings were annotated
by hand from the footage; 4 complete chest openings (2 of them while spectating).
`tools/DetectionEval video` with the app's settings: 4 found, 0 missed, 0 false positives, 0 duplicates, latency
from the annotated opening to the count 0.3 s on average (max 0.8 s). A fifth opening at the very end of a clip
(one open frame before the clip ended) could not be confirmed. RTX 2060 with DirectML: 12–25 ms per frame
(p95 under 40 ms), about 10–16 % of all CPU cores including video decoding. This is a small sample from one
player's footage: it is not a reliable accuracy figure.

**Limitations.**
- The model was trained on a small dataset. It also detects some backpacks, menu icons and loot as chests; the state
  machine keeps these from counting in the footage above, but other skins, seasons, chest types and graphics
  settings have not been tested. More labelled frames from real matches (different distances, angles, lighting,
  chest types, HUD, scenes without chests) are the most effective improvement.
- Openings by a player you are spectating are counted too.
- An opening that the model sees only after the chest is already open (for example after looking away) is not counted.
- Exclusive fullscreen may block capture; use borderless/windowed.
- `tools/DetectionEval` measures counting on your own recordings (`video`, with annotated opening times) and per-class
  detection metrics on labelled frames (`frames`); `tools/yolo` contains the training scripts.

### Multiplayer parties

Private parties share chest statistics in real time. The server uses ASP.NET Core with SignalR, and data is stored
in PostgreSQL.

**Using it (Party page / «Пати»):**
1. Enter the server address and a display name, then press "Connect". This creates an anonymous device account. No
   Epic or Fortnite credentials are used. The account secret is stored DPAPI-encrypted in `settings.json`.
2. **Create a party.** Pick 1–4 players and share the `XXXX-XXXX` invitation code. Codes are random, expire after
   24 hours by default, and can be regenerated. Join attempts are rate-limited.
3. **Shared matches.** When the owner starts a match (F9 or the button), a shared match starts for the party.
   Pressing the match hotkey again (e.g. after dying) ends only **your** counting: the shared match goes on for the
   others, your chests so far stay in it, and you keep receiving the party's totals. The shared match ends when its
   last player stops, or when the owner ends it for everyone on the Party page. A stop made while offline is sent
   after reconnecting. If the owner starts their next match while the others are still in the old one, the old one
   is left alone and the owner's new match is counted locally (and in the party lifetime).
   Members join automatically if they enabled that; otherwise they accept or decline. Their local match starts and
   ends in sync.
4. **Party page contents:** each member's count, team totals, online/offline status, leaderboards (today, this week,
   all time) and the shared-match history.
5. **Overlay.** Switch it to the team layout (team total, your count, teammates' counts, online dots, recent activity)
   on the Party page, or with the optional "Overlay: personal / team" hotkey.
   Teammates' chests update the counters and can show a notification, but play no sound: the chest sound plays only
   on the computer of the player who recorded the chest (by hand or by detection).
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

#### Dedicated server and release builds

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

#### Running the server (development)

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

### Discord Rich Presence

The status shows in your Discord profile as **Chest Tracker**:

| Field | Content |
|---|---|
| Details | `Solo`, or `Team • 2/4 players` |
| State | `User • 12 chests this match`. The statistic shown depends on the "Main statistic" setting. Outside a match it shows the all-time total. |
| Elapsed time | Time since the current match started (Discord activity timestamps) |
| Large image | Chest Tracker logo. Its tooltip can show the lifetime chest count. |
| Small image | Solo or team badge. Its tooltip shows the tracking mode. |
| Button | "GitHub" link to this repository (always shown; Discord hides your own buttons from you). |

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

#### Discord application (fixed)

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

### Data location

`%LOCALAPPDATA%\FortniteChestTracker\` (set the `CHESTTRACKER_DATA_DIR` environment variable to use another folder,
for example for testing)

- `chests.db` (+ `-wal`): SQLite database
- `backups\`: automatic and manual backups
- `settings.json`: settings
- `detection-template.png`: optional prompt template
- `logs\app.log`: diagnostics log

### Project structure

```
ChestTracker.sln
src/
  ChestTracker.Core/          UI-free logic (net8.0) — fully unit tested
    Models/                   event, match, session, review records
    Data/                     SqliteDatabase, Migrations, ChestRepository, BackupService,
                              DatabaseBootstrapper (integrity check + recovery), DataExporter (JSON/CSV)
    Tracking/                 TrackingEngine (single entry point for all changes), DeduplicationPolicy
    Statistics/               StatisticsCalculator (per day / session, averages, bests, filters)
    Detection/                DetectionCounting (opening → chest event), legacy heuristic detector (unused by the app)
    Settings/                 AppSettings, SettingsStore, hotkey conflict detection
  ChestTracker.App/           WPF app (net8.0-windows10.0.19041.0), MVVM (CommunityToolkit.Mvvm)
    Views/                    MainWindow, Dashboard, History, Statistics, Detection, Settings, Overlay, dialogs
    ViewModels/
    Services/                 HotkeyService (RegisterHotKey), TrayService, SoundService, StartupService, dialogs
    Capture/                  WgcCaptureSource, GdiCaptureSource, DetectionService (inference thread), debug preview
    Controls/                 SimpleChart (OnRender charts), HotkeyBox, Pulse animation
    Themes/                   dark purple theme
    Localization/             Strings.ru.xaml (default), Strings.en.xaml, Loc
  ChestTracker.Vision/        YOLOv8 chest model: ONNX Runtime (DirectML/CPU) inference, letterbox/decoding/NMS,
                              chest tracking, Closed → Opened → Counted state machine, match-scoped detection, metrics
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

### Tests

`dotnet test` covers:

- **Counter logic**: no counting without a running match, match, session and lifetime totals, undo order, never going below zero,
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
