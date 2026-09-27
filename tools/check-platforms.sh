#!/usr/bin/env bash
# Starts Fermata as a Wayland client and as an X11 client (through XWayland), and checks that each
# window appears with the identity that desktops match to the launcher entry, that the Wayland
# requests Fermata makes itself report no failure, and that Fermata then quits cleanly on SIGTERM.
# Runs inside tools/isolated-session.sh, which provides sway:
#
#   tools/isolated-session.sh tools/check-platforms.sh [EXECUTABLE]
#
# EXECUTABLE defaults to bin/fermata/fermata. Needs jq.
set -euo pipefail
cd "$(dirname "$0")/.."
executable="${1:-bin/fermata/fermata}"
failures=0

# Prints the window's shell, Wayland app ID and X11 class, or nothing before it appears.
window() {
    swaymsg -t get_tree | jq -c 'first(.. | objects | select(.type? == "con" and (.pid? // 0) > 0))
        | [.shell, .app_id, .window_properties.class]'
}

check() {
    local platform=$1 expected=$2 log="/tmp/fermata-$1.log" found="" status=0
    # A made-up activation token: sway ignores it, but Fermata still goes through the activation request.
    FERMATA_PLATFORM=$platform XDG_ACTIVATION_TOKEN=check "$executable" > "$log" 2>&1 &
    local pid=$!
    for _ in $(seq 150); do
        found="$(window)"
        [[ -n "$found" ]] && break
        kill -0 "$pid" 2>/dev/null || break
        sleep 0.1
    done
    kill -TERM "$pid" 2>/dev/null || true
    wait "$pid" || status=$?
    if [[ "$found" == "$expected" && $status == 0 ]] && ! grep -q '^fermata: could not' "$log"; then
        echo "$platform: window $found, quit with status 0"
    else
        echo "$platform: expected window $expected, no failure and status 0; got ${found:-no window} and status $status" >&2
        cat "$log" >&2
        failures=$((failures + 1))
    fi
}

check wayland '["xdg_shell","io.github.camelcase12.Fermata",null]'
check x11 '["xwayland",null,"fermata"]'
exit "$failures"
