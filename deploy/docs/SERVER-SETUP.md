# Chest Tracker dedicated server — setup guide

The server handles parties, invitation codes, real-time chest synchronization, shared matches, leaderboards and
accounts for Chest Tracker clients on other computers.

- It does **not** need Fortnite.
- The builds are self-contained: no .NET runtime, Visual Studio or SDK is needed.
- The only dependency is **PostgreSQL 13 or newer**.

```
Server/
  Windows/   ChestTracker.Server.exe, appsettings.json, start-server.cmd, start-server.ps1
  Linux/     ChestTracker.Server, appsettings.json, start-server.sh, chesttracker-server.service
  reverse-proxy/  nginx-chesttracker.conf, Caddyfile
  docker-compose.postgres.yml
```

## 1. Install PostgreSQL

Pick one:

- **Linux (Debian/Ubuntu):** `sudo apt install postgresql`
- **Windows:** the installer from <https://www.postgresql.org/download/windows/>.
- **Docker (any OS):** set `POSTGRES_PASSWORD=...` in a `.env` file, then run
  `docker compose -f docker-compose.postgres.yml up -d`.

Create a user and a database. Skip this step for Docker; the compose file creates them.

```sql
CREATE USER chesttracker WITH PASSWORD 'a-long-random-password';
CREATE DATABASE chesttracker OWNER chesttracker;
```

The server creates and upgrades its tables automatically at start-up. Each schema migration runs in a
transaction.

## 2. Configure

Edit `appsettings.json` next to the executable:

| Setting | What to set |
|---|---|
| `ConnectionStrings:Postgres` | Replace `CHANGE_ME` with the database password. |
| `Kestrel:Endpoints:Http:Url` | Listening address. The default `http://0.0.0.0:5280` listens on all interfaces. Use `http://127.0.0.1:5280` when a reverse proxy runs on the same machine. |
| `ChestTracker:AdminApiKey` | Optional. Set a long random string to enable the admin API. Leave it empty to disable admin endpoints completely. |
| `ChestTracker:TrustedProxies` | IP addresses of your reverse proxies, for example `["127.0.0.1"]`. |
| `ChestTracker:AllowGuests` / `AllowAccounts` | Guest players (no password) and username/password accounts. |
| `ChestTracker:MaxPartySize` | Largest party allowed (default 4). |

Every setting can also come from an environment variable, which overrides the file. Use `__` for nesting, for example
`ConnectionStrings__Postgres` or `ChestTracker__AdminApiKey`. This keeps secrets out of files.

**Signing key.** The token signing key is created on first start in `data/signing.key`, with owner-only file
permissions on Linux. Release archives never contain a key.
- Back the key up together with the database.
- If you lose it, every client simply signs in again automatically.

## 3. Start

| OS | Command |
|---|---|
| Windows | Double-click `start-server.cmd`, or run `powershell -ExecutionPolicy Bypass -File .\start-server.ps1` |
| Linux | `bash start-server.sh` |

To run it as a Linux service, see the header of `chesttracker-server.service`:
`systemctl enable --now chesttracker-server`.

To check the server, open `http://SERVER:5280/api/status`. It answers with the version, the protocol and the number
of online players.

**Firewall.**
- Open TCP 5280. With a reverse proxy, open 80 and 443 instead.
- Windows: `New-NetFirewallRule -DisplayName "Chest Tracker" -Direction Inbound -Protocol TCP -LocalPort 5280 -Action Allow`
  (run as administrator).
- Ubuntu: `sudo ufw allow 5280/tcp`.

## 4. Internet access and HTTPS (recommended)

Players connect with a URL such as `https://chests.example.com`. The real-time channel then runs over **WSS**.
Terminate TLS in a reverse proxy:

- **Caddy** (simplest; it gets certificates automatically): use `reverse-proxy/Caddyfile`.
- **nginx:** use `reverse-proxy/nginx-chesttracker.conf` (it includes the WebSocket upgrade headers) with certbot.

Then do three things:
1. Set `"TrustedProxies": ["127.0.0.1"]`, so rate limits and logs see the real client IP.
2. Bind Kestrel to `http://127.0.0.1:5280`.
3. Open only ports 80 and 443.

Without a domain or proxy, clients can still use `http://PUBLIC-IP:5280`. The app warns that the connection is
unencrypted. On a home network, forward TCP 5280 on your router to the server machine.

## 5. Backups and recovery

- **Automatic backups:** every `BackupIntervalHours` (default 6 h). The newest `BackupKeep` (28) are kept in
  `backups/chesttracker-*.ctbak`. Each is a ZIP of consistent CSV snapshots of all tables.
- **Manual backup:** run `ChestTracker.Server --backup` (Linux: `./ChestTracker.Server --backup`), or call the admin API
  (`POST /api/admin/backups`).
- **Restore:**
  1. Stop the server.
  2. Create an empty database, or use `--replace` to overwrite the current one.
  3. Run `ChestTracker.Server --restore backups/chesttracker-YYYYMMDD-HHMMSS.ctbak [--replace]`.
  4. Start the server again.
- Backups contain password hashes and device-key hashes. Store them as privately as the database itself.
- Standard `pg_dump` / `pg_restore` work as well.

**Robustness:**
- If the database starts after the server, the server retries the connection for one minute.
- Clients keep working offline and resend queued events when the server is back. Event IDs make retries harmless.

## 6. Administration

With `AdminApiKey` set, send the header `X-Admin-Key: <key>` to call these endpoints:

| Request | Result |
|---|---|
| `GET /api/admin/stats` | Version, uptime, online players, connections, active rooms, active shared matches, totals, last backup |
| `GET /api/admin/parties` | Active rooms (parties with online members) |
| `GET /api/admin/backups`, `POST /api/admin/backups` | List backups / create one now |
| `DELETE /api/admin/players/{id}` | Remove a player and all their data |

Example:

```bash
curl -H "X-Admin-Key: $KEY" https://chests.example.com/api/admin/stats
```

**Logs** go to the console and to `logs/server-YYYYMMDD.log`, kept for `LogRetentionDays`. Access tokens are never logged.

## 7. Security summary

**Accounts and authentication:**
- Players are either guests (a random device key) or accounts (username plus a PBKDF2-SHA256 password hash).
- Clients store only an encrypted device key, never the password.
- All private data needs a signed, short-lived access token.

**Server-side checks:**
- Party membership and owner rights are checked on every action.
- Events are deduplicated by unique ID.
- Totals are always computed by the server from validated events; client-reported totals are never trusted.

**Rate limits:** registration and login per IP, invitation-code joins per player, API requests per player, and
real-time calls per player.

**Version check:** clients below `MinClientProtocol` get HTTP 426 with a clear "update the app" message.

**Admin API:** it does not exist unless a key is configured; the key is compared in constant time.

## 8. Updating

1. Stop the server.
2. Back up the database.
3. Replace the executable. Keep `appsettings.json`, `data/`, `backups/` and `logs/`.
4. Start the server; migrations apply automatically.

Check the version with `ChestTracker.Server --version`.
