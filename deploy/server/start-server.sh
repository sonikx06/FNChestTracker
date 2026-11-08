#!/usr/bin/env bash
# Starts the Chest Tracker dedicated server (Linux x64). Run from this folder:
#   bash start-server.sh            (extra arguments are passed to the server, e.g. --backup)
# Configuration: appsettings.json next to this script, or environment variables such as
#   ConnectionStrings__Postgres, ChestTracker__AdminApiKey (they override the file).
set -euo pipefail
cd "$(dirname "$(readlink -f "$0")")"

if [ ! -f ./ChestTracker.Server ]; then
  echo "ChestTracker.Server not found next to this script." >&2
  exit 1
fi
# ZIP archives created on Windows do not keep the executable bit.
chmod +x ./ChestTracker.Server

if [ -z "${ConnectionStrings__Postgres:-}" ] && grep -q 'Password=CHANGE_ME' appsettings.json; then
  echo "appsettings.json still contains the placeholder database password (CHANGE_ME)." >&2
  echo "Edit ConnectionStrings:Postgres (see Documentation/SERVER-SETUP.md) and run again." >&2
  exit 1
fi

export ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Production}"
echo "Starting Chest Tracker server (${ASPNETCORE_ENVIRONMENT}). Press Ctrl+C to stop."
exec ./ChestTracker.Server "$@"
