#!/usr/bin/env bash
# Installs the built Fermata (run ./build.sh first) for the current user, without root:
#
#   ~/.local/lib/fermata/     the executable and the two native libraries it loads
#   ~/.local/bin/fermata      a link to the executable
#   ~/.local/share/applications/fermata.desktop and the icons, for launchers
#
#   ./install.sh               install or update
#   ./install.sh --uninstall   remove those files (settings, playlists and likes are kept)
#
# PREFIX changes ~/.local.
set -euo pipefail
cd "$(dirname "$0")"
prefix="${PREFIX:-$HOME/.local}"
lib="$prefix/lib/fermata"
applications="$prefix/share/applications"
icons="$prefix/share/icons/hicolor"
sizes=(32 48 64 128 256)

refresh_launchers() {
    command -v update-desktop-database >/dev/null && update-desktop-database -q "$applications" || true
    # Only keep an existing icon cache current: creating one where there was none could hide the
    # icons of applications installed later without updating it.
    if [[ -f "$icons/icon-theme.cache" ]] && command -v gtk-update-icon-cache >/dev/null; then
        gtk-update-icon-cache -q -t "$icons" 2>/dev/null || true
    fi
}

if [[ "${1:-}" == --uninstall ]]; then
    rm -f -- "$prefix/bin/fermata" "$applications/fermata.desktop" "$icons/scalable/apps/fermata.svg"
    for size in "${sizes[@]}"; do
        rm -f -- "$icons/${size}x${size}/apps/fermata.png"
    done
    rm -rf -- "$lib"
    refresh_launchers
    echo "Removed Fermata from $prefix"
    exit 0
fi

if [[ ! -x bin/fermata/fermata ]]; then
    echo "bin/fermata/fermata is missing; run ./build.sh first" >&2
    exit 1
fi
for file in fermata libSkiaSharp.so libHarfBuzzSharp.so; do
    install -Dm755 "bin/fermata/$file" "$lib/$file"
done
mkdir -p "$prefix/bin"
ln -sfn "$lib/fermata" "$prefix/bin/fermata"
install -Dm644 packaging/fermata.desktop "$applications/fermata.desktop"
install -Dm644 src/Fermata/Assets/fermata.svg "$icons/scalable/apps/fermata.svg"
for size in "${sizes[@]}"; do
    install -Dm644 "src/Fermata/Assets/fermata-$size.png" "$icons/${size}x${size}/apps/fermata.png"
done
refresh_launchers
echo "Installed Fermata in $prefix (run: fermata)"
case ":$PATH:" in
    *":$prefix/bin:"*) ;;
    *) echo "Note: $prefix/bin is not on PATH" ;;
esac
