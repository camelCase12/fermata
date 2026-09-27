#!/usr/bin/env bash
# Runs a command in a private headless sway session with XWayland, its own D-Bus session bus and
# XDG directories, and silent audio (FERMATA_AUDIO_OUTPUT=null). Inside, DISPLAY points at XWayland
# and WAYLAND_DISPLAY at sway.
#
#   tools/isolated-session.sh COMMAND [ARGS…]
#
# Needs bubblewrap, dbus-daemon and sway (built with XWayland). Set COMPOSITOR_ROOT to a
# directory containing usr/bin/sway and its libraries when sway is not installed system-wide,
# and FERMATA_GPU to a render node (e.g. /dev/dri/renderD128) to choose the GPU on a multi-GPU machine.
set -euo pipefail
if [[ "${FERMATA_INSIDE:-}" != 1 ]]; then
    here="$(cd "$(dirname "$0")/.." && pwd)"
    exec bwrap --die-with-parent --unshare-all --share-net --ro-bind / / --dev /dev --proc /proc \
        --tmpfs /tmp --tmpfs /run --dev-bind-try /dev/dri /dev/dri \
        $(for node in /dev/nvidia*; do [[ -e "$node" ]] && echo "--dev-bind $node $node"; done) \
        --bind "$here" "$here" ${FERMATA_BIND:+--bind "$FERMATA_BIND" "$FERMATA_BIND"} \
        --unsetenv WAYLAND_DISPLAY --unsetenv DISPLAY --unsetenv HYPRLAND_INSTANCE_SIGNATURE \
        --unsetenv DBUS_SESSION_BUS_ADDRESS --unsetenv SWAYSOCK \
        --setenv FERMATA_INSIDE 1 "$0" "$@"
fi
export XDG_RUNTIME_DIR=/run/fermata-session XDG_CONFIG_HOME=/tmp/config XDG_DATA_HOME=/tmp/data
export XDG_CACHE_HOME=/tmp/cache XDG_STATE_HOME=/tmp/state FERMATA_AUDIO_OUTPUT="${FERMATA_AUDIO_OUTPUT:-null}"
mkdir -p "$XDG_RUNTIME_DIR" /tmp/.X11-unix && chmod 700 "$XDG_RUNTIME_DIR" && chmod 1777 /tmp/.X11-unix
root="${COMPOSITOR_ROOT:-}"
cat > /tmp/sway.conf <<CONFIG
xwayland enable
output HEADLESS-1 resolution ${FERMATA_SCREEN:-1600x1000} scale ${FERMATA_SCALE:-1}
default_border none
default_floating_border none
focus_follows_mouse no
CONFIG
(
    [[ -n "$root" ]] && export LD_LIBRARY_PATH="$root/usr/lib"
    export WLR_BACKENDS=headless WLR_LIBINPUT_NO_DEVICES=1
    [[ -n "${FERMATA_GPU:-}" ]] && export WLR_RENDER_DRM_DEVICE="$FERMATA_GPU"
    exec "${root:+$root/usr/bin/}sway" --config /tmp/sway.conf >/tmp/sway.log 2>&1
) &
for _ in $(seq 200); do compgen -G "$XDG_RUNTIME_DIR/wayland-*" >/dev/null && compgen -G "/tmp/.X11-unix/X*" >/dev/null && break; sleep 0.05; done
socket="$(compgen -G "$XDG_RUNTIME_DIR/wayland-*" | grep -v lock | head -1)"
export WAYLAND_DISPLAY="$(basename "$socket")"
display="$(basename "$(compgen -G "/tmp/.X11-unix/X*" | head -1)")"
export DISPLAY=":${display#X}"
for _ in $(seq 100); do compgen -G "$XDG_RUNTIME_DIR/sway-ipc.*.sock" >/dev/null && break; sleep 0.05; done
export SWAYSOCK="$(compgen -G "$XDG_RUNTIME_DIR/sway-ipc.*.sock" | head -1)"
exec dbus-run-session -- "$@"
