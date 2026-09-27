#!/usr/bin/env bash
# Opens Fermata with the window system it chooses itself, then as an X11 client, then as a Wayland
# client if it chose Wayland. It checks each window's app ID or class, that no Wayland request
# failed, and that Fermata quits cleanly on SIGTERM. It runs inside tools/isolated-session.sh:
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

wayland='["xdg_shell","io.github.camelcase12.Fermata",null]'
x11='["xwayland",null,"fermata"]'

# Runs Fermata with FERMATA_PLATFORM set to the platform, or unset for "default", and checks its window.
check() {
    local platform=$1 log="/tmp/fermata-$1.log" found="" status=0 expected
    # A made-up activation token: sway ignores it, but Fermata still goes through the activation request.
    FERMATA_PLATFORM=${platform#default} FERMATA_TRACE_STARTUP=1 XDG_ACTIVATION_TOKEN=check "$executable" > "$log" 2>&1 &
    local pid=$!
    for _ in $(seq 150); do
        found="$(window)"
        [[ -n "$found" ]] && break
        kill -0 "$pid" 2>/dev/null || break
        sleep 0.1
    done
    kill -TERM "$pid" 2>/dev/null || true
    wait "$pid" || status=$?
    if grep -q 'window system wayland' "$log"; then
        chosen=wayland expected=$wayland
    else
        chosen=x11 expected=$x11
    fi
    if [[ "$found" == "$expected" && $status == 0 ]] && ! grep -q '^fermata: could not' "$log"; then
        echo "$platform: window $found, quit with status 0"
    else
        echo "$platform: expected window $expected, no failure and status 0; got ${found:-no window} and status $status" >&2
        cat "$log" >&2
        failures=$((failures + 1))
    fi
}

check default
default_choice=$chosen
check x11
if [[ $default_choice == wayland ]]; then
    check wayland
fi
exit "$failures"
