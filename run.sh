#!/usr/bin/env bash
# Development: build the backend (src/PS4Mem.Api) and the macOS app (mac/), then run the app.
# The app starts the backend itself (dotnet run) unless one already answers on the port,
# and stops it again when you quit the app or press Ctrl+C here.
#
#   ./run.sh                     build both, run the app
#   PS4MEM_API_URL=http://127.0.0.1:5081 ./run.sh    use another port
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
cd "$ROOT"

export PS4MEM_REPO_ROOT="$ROOT"
export PS4MEM_API_URL="${PS4MEM_API_URL:-http://127.0.0.1:5080}"

echo "==> Building the backend (src/PS4Mem.Api)"
dotnet build src/PS4Mem.Api --nologo -v quiet

echo "==> Building the app (mac/)"
swift build --package-path mac

APP_PID=""
cleanup() {
    # The app stops its backend on SIGINT/SIGTERM. Wait for it, so nothing is left running.
    if [[ -n "$APP_PID" ]] && kill -0 "$APP_PID" 2>/dev/null; then
        kill -TERM "$APP_PID" 2>/dev/null || true
        wait "$APP_PID" 2>/dev/null || true
    fi
}
trap cleanup EXIT INT TERM

echo "==> Running PS4 Mem (backend at $PS4MEM_API_URL). Quit the app or press Ctrl+C to stop both."
"$(swift build --package-path mac --show-bin-path)/PS4Mem" &
APP_PID=$!
wait "$APP_PID"
