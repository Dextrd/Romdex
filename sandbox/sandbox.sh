#!/usr/bin/env bash
# A throwaway Romdex over a made-up library (tools/make-gate-library.py), so nothing touches a real one.
#
#   sandbox/sandbox.sh up       make the library, build what's missing, start everything, set Romdex up, scan
#   sandbox/sandbox.sh build    rebuild the Romdex images, and pull or rebuild the inbox plugin's
#   sandbox/sandbox.sh status   show the systems and how the plugins are doing
#   sandbox/sandbox.sh scan     update the catalogue, rescan the library and wait for both
#   sandbox/sandbox.sh arrive   drop a new dump of each system into the inbox, for Romdex to file
#   sandbox/sandbox.sh down     stop and remove the containers and their data
#   sandbox/sandbox.sh reset    down, and throw away the library and the inbox
#
# Works with docker or podman (ENGINE=podman to choose). Romdex's login is generated on first run into sandbox/.env.
# It runs beside an install of the compose file, on its own port (ROMDEX_PORT, default 8789) and container names.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SANDBOX="$ROOT/sandbox"
# The generator writes dats, roms, arrivals and its CSVs here.
ROMS="$SANDBOX/roms"
DATS="$SANDBOX/dats"
ARRIVALS="$SANDBOX/arrivals"
INBOX="$SANDBOX/inbox"
ENV_FILE="$SANDBOX/.env"
# Romdex's session, for the calls this script makes.
COOKIES="$SANDBOX/.cookies"

NETWORK="romdex-sandbox"
ROMDEX_PORT="${ROMDEX_PORT:-8789}"
CONTAINERS=(romdex-sandbox-core romdex-sandbox-igir romdex-sandbox-emulatorjs romdex-sandbox-inbox)
VOLUMES=(romdex-sandbox-config romdex-sandbox-staging)

# Image, then build context and Dockerfile. The names are docker-compose.build.yml's, so the two share images.
IMAGES=(
  "romdex-core:dev|$ROOT|$ROOT/docker/core.Dockerfile"
  "romdex-igir-worker:dev|$ROOT/igir-worker|$ROOT/igir-worker/Dockerfile"
  "romdex-emulatorjs:dev|$ROOT/emulatorjs|$ROOT/emulatorjs/Dockerfile"
)

# The retrievers have repositories of their own. Their published images are used, or one built from a checkout of the
# retriever's repository (romdex-retriever-<name>) where PLUGINS_DIR has one: by default, beside this repository.
PLUGINS=(inbox)
PLUGINS_DIR="${PLUGINS_DIR:-$(dirname "$ROOT")}"
PLUGIN_TAG="${PLUGIN_TAG:-latest}"

plugin_image() {
  if [[ -f "$PLUGINS_DIR/romdex-retriever-$1/Dockerfile" ]]; then
    echo "romdex-retriever-$1:dev"
  else
    echo "ghcr.io/dextrd/romdex-retriever-$1:$PLUGIN_TAG"
  fi
}

# Docker when its daemon answers, else podman.
if [[ -z "${ENGINE:-}" ]]; then
  if command -v docker >/dev/null && docker info >/dev/null 2>&1; then ENGINE=docker
  else ENGINE="$(command -v podman || true)"
  fi
fi
[[ -n "$ENGINE" ]] || { echo "needs docker or podman" >&2; exit 1; }

# The igir worker and the inbox plugin run as you, so the library and the inbox stay yours.
if [[ "$(basename "$ENGINE")" == podman ]]; then
  AS_YOU=(--userns=keep-id)
else
  AS_YOU=(--user "$(id -u):$(id -g)")
fi

# Files a container writes carry its SELinux categories. Dumps pass from the inbox plugin through staging to the igir
# worker, so on SELinux hosts every container shares one level (elsewhere the option is ignored).
SHARED=(--network "$NETWORK" --security-opt label=level:s0:c100,c200)

if [[ ! -f "$ENV_FILE" ]]; then
  printf 'ROMDEX_USER=admin\nROMDEX_PASSWORD=%s\n' "$(head -c 18 /dev/urandom | base64 | tr -d '/+=')" > "$ENV_FILE"
fi
# shellcheck source=/dev/null
source "$ENV_FILE"

romdex_url="http://localhost:$ROMDEX_PORT/api/v1"

romdex() {
  local method="$1" path="$2"; shift 2
  curl -sf -X "$method" "$romdex_url$path" -H 'Content-Type: application/json' -b "$COOKIES" -c "$COOKIES" "$@"
}

# Signs in to Romdex, making the admin login first if it's a new stack.
sign_in() {
  local login="{\"username\":\"$ROMDEX_USER\",\"password\":\"$ROMDEX_PASSWORD\"}"
  romdex POST /auth/setup -o /dev/null -d "$login" 2>/dev/null || romdex POST /auth/login -o /dev/null -d "$login"
}

wait_for() {
  local what="$1" url="$2"
  for _ in $(seq 1 60); do
    curl -sf -o /dev/null "$url" && return 0
    sleep 1
  done
  echo "$what didn't come up at $url" >&2
  exit 1
}

# Starts a background task and waits for it to finish.
run_task() {
  local path="$1" task id state
  task="$(romdex POST "$path")"
  id="$(python3 -I -c 'import json, sys; print(json.load(sys.stdin)["id"])' <<<"$task")"
  for _ in $(seq 1 600); do
    state="$(romdex GET "/tasks/$id" | python3 -I -c 'import json, sys; print(json.load(sys.stdin)["state"])')"
    case "$state" in
      succeeded) return 0 ;;
      queued|running|paused) sleep 1 ;;
      *) echo "$path: $state" >&2; exit 1 ;;
    esac
  done
  echo "$path didn't finish" >&2
  exit 1
}

build() {
  local only_missing="${1:-}" entry image context file
  for entry in "${IMAGES[@]}"; do
    IFS='|' read -r image context file <<<"$entry"
    if [[ -n "$only_missing" ]] && "$ENGINE" image inspect "$image" >/dev/null 2>&1; then continue; fi
    echo "Building $image..."
    "$ENGINE" build -q -f "$file" -t "$image" "$context" >/dev/null
  done
  local name
  for name in "${PLUGINS[@]}"; do
    image="$(plugin_image "$name")"
    if [[ -n "$only_missing" ]] && "$ENGINE" image inspect "$image" >/dev/null 2>&1; then continue; fi
    if [[ "$image" == romdex-retriever-* ]]; then
      echo "Building $image from $PLUGINS_DIR/romdex-retriever-$name..."
      "$ENGINE" build -q -t "$image" "$PLUGINS_DIR/romdex-retriever-$name" >/dev/null
    else
      echo "Pulling $image..."
      "$ENGINE" pull -q "$image" >/dev/null
    fi
  done
}

# Adds the plugins, once: the DAT folder as the catalogue, then the inbox. Setup is then done.
configure() {
  local plugins
  plugins="$(romdex GET /plugins)"
  if ! grep -q '"romdex.index.dat-folder"' <<<"$plugins"; then
    romdex POST /plugins -o /dev/null -d '{"pluginId":"romdex.index.dat-folder","settings":{"folder":"/dats"}}'
  fi
  if ! grep -q '"http://retriever-inbox:8080"' <<<"$plugins"; then
    # The plugin may still be starting.
    local added=""
    for _ in $(seq 1 30); do
      # It files what it's given straight away, rather than waiting for a copy to settle.
      romdex POST /plugins -o /dev/null -d '{"endpoint":"http://retriever-inbox:8080","settings":{"settleSeconds":2}}' 2>/dev/null &&
        { added=1; break; }
      sleep 1
    done
    [[ -n "$added" ]] || { echo "couldn't add the inbox plugin" >&2; exit 1; }
  fi
  romdex POST /setup/done -o /dev/null
  # Test each, so Settings shows how they're doing.
  for id in $(romdex GET /plugins | grep -o '"id":"[^"]*"' | cut -d'"' -f4); do
    romdex POST "/plugins/$id/test" -o /dev/null || true
  done
}

scan() {
  echo "Updating the catalogue and scanning the library..."
  run_task /catalogue/refresh
  run_task /library/scan
}

status() {
  echo "Romdex: http://localhost:$ROMDEX_PORT (user $ROMDEX_USER, password in sandbox/.env) -> $(curl -sf "$romdex_url/health" || echo 'not running')"
  sign_in 2>/dev/null || true
  romdex GET /plugins 2>/dev/null | python3 -I -c '
import json, sys
for p in json.load(sys.stdin):
    message = p.get("healthMessage")
    print("  " + p["name"] + ": " + (p.get("health") or "not tested") + (" (" + message + ")" if message else ""))' || true
  romdex GET /systems 2>/dev/null | python3 -I -c '
import json, sys
for s in json.load(sys.stdin):
    print("  " + s["name"] + ": " + str(s["owned"]) + " of " + str(s["titles"]) + " owned")' || true
  echo "API reference: http://localhost:$ROMDEX_PORT/api/docs"
  echo "Library: $ROMS"
  echo "Inbox:   $INBOX (sandbox/sandbox.sh arrive drops a new dump of each system here)"
}

arrive() {
  [[ -d "$ARRIVALS" ]] || { echo "no arrivals; run up first" >&2; exit 1; }
  cp -r "$ARRIVALS"/. "$INBOX"/
  echo "Copied $(find "$ARRIVALS" -type f | wc -l) files into $INBOX; Romdex files them in a few seconds. Downloads shows how each went."
}

remove() {
  "$ENGINE" rm -f "${CONTAINERS[@]}" >/dev/null 2>&1 || true
}

up() {
  if [[ ! -d "$ROMS" ]]; then
    echo "Making the library..."
    python3 -I "$ROOT/tools/make-gate-library.py" "$SANDBOX" >/dev/null
  fi
  mkdir -p "$INBOX"
  build only-missing

  "$ENGINE" network inspect "$NETWORK" >/dev/null 2>&1 || "$ENGINE" network create "$NETWORK" >/dev/null
  remove

  # The core first, so the staging volume takes its ownership from the core's image. It restarts when it stops, as in
  # the compose file, so a factory reset brings it back.
  "$ENGINE" run -d --name romdex-sandbox-core "${SHARED[@]}" --network-alias romdex --restart unless-stopped \
    -p "$ROMDEX_PORT:8080" \
    -e Romdex__WorkerUrl=http://igir-worker:8080 -e Romdex__EmulatorUrl=http://emulatorjs:8080 \
    -v "$ROMS:/roms:ro,z" -v "$DATS:/dats:ro,z" -v romdex-sandbox-config:/config -v romdex-sandbox-staging:/staging \
    romdex-core:dev >/dev/null
  "$ENGINE" run -d --name romdex-sandbox-igir "${SHARED[@]}" --network-alias igir-worker "${AS_YOU[@]}" \
    -v "$ROMS:/roms:z" -v romdex-sandbox-staging:/staging \
    romdex-igir-worker:dev >/dev/null
  "$ENGINE" run -d --name romdex-sandbox-emulatorjs "${SHARED[@]}" --network-alias emulatorjs \
    romdex-emulatorjs:dev >/dev/null
  "$ENGINE" run -d --name romdex-sandbox-inbox "${SHARED[@]}" --network-alias retriever-inbox "${AS_YOU[@]}" \
    -v "$INBOX:/inbox:z" -v romdex-sandbox-staging:/staging \
    "$(plugin_image inbox)" >/dev/null

  wait_for Romdex "$romdex_url/health"
  rm -f "$COOKIES"
  sign_in
  configure
  scan
  status
}

case "${1:-}" in
  up) up ;;
  build) build ;;
  status) status ;;
  scan) sign_in; scan; status ;;
  arrive) arrive ;;
  down|reset)
    remove
    "$ENGINE" volume rm -f "${VOLUMES[@]}" >/dev/null 2>&1 || true
    "$ENGINE" network rm "$NETWORK" >/dev/null 2>&1 || true
    rm -f "$COOKIES"
    if [[ "$1" == reset ]]; then
      # Files the containers wrote may belong to a mapped user; let the engine remove them if needed.
      generated=("$ROMS" "$DATS" "$ARRIVALS" "$INBOX" "$SANDBOX/igir-out" "$SANDBOX/expected.csv" "$SANDBOX/arrivals.csv")
      rm -rf "${generated[@]}" 2>/dev/null || "$ENGINE" unshare rm -rf "${generated[@]}"
    fi
    echo "Sandbox removed." ;;
  *) sed -n '2,13p' "$0" | sed 's/^# \{0,1\}//'; exit 1 ;;
esac
