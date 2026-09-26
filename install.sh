#!/usr/bin/env bash
# Installs Fermata for the current user, without root. Run it from a source checkout after ./build.sh,
# or from an unpacked release.
#
#   ~/.local/lib/fermata/     the executable, the two native libraries it loads, and license notices
#   ~/.local/bin/fermata      a link to the executable
#   ~/.local/share/           the launcher entry, icons and AppStream metadata
#
#   ./install.sh               install or update
#   ./install.sh --uninstall   remove those files (settings, playlists and likes are kept)
#
# PREFIX changes ~/.local.
set -euo pipefail
cd "$(dirname "$0")"
id=io.github.camelcase12.Fermata
prefix="${PREFIX:-$HOME/.local}"
lib="$prefix/lib/fermata"
applications="$prefix/share/applications"
metainfo="$prefix/share/metainfo"
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

# Removes the launcher entry and icons under both the app ID and the name "fermata".
remove_launcher_files() {
    for name in fermata "$id"; do
        rm -f -- "$applications/$name.desktop" "$icons/scalable/apps/$name.svg"
        for size in "${sizes[@]}"; do
            rm -f -- "$icons/${size}x${size}/apps/$name.png"
        done
    done
    rm -f -- "$metainfo/$id.metainfo.xml"
}

if [[ "${1:-}" == --uninstall ]]; then
    rm -f -- "$prefix/bin/fermata"
    remove_launcher_files
    rm -rf -- "$lib"
    refresh_launchers
    echo "Removed Fermata from $prefix"
    exit 0
fi

# A source checkout keeps the build in bin/fermata and the icons with the sources; a release keeps
# both beside this script.
if [[ -x bin/fermata/fermata ]]; then
    binaries=bin/fermata
    assets=src/Fermata/Assets
elif [[ -x fermata ]]; then
    binaries=.
    assets=icons
else
    echo "The fermata executable is missing; run ./build.sh first" >&2
    exit 1
fi
for file in fermata libSkiaSharp.so libHarfBuzzSharp.so; do
    install -Dm755 "$binaries/$file" "$lib/$file"
done
install -Dm644 LICENSE "$lib/LICENSE"
install -Dm644 THIRD-PARTY-NOTICES.md "$lib/THIRD-PARTY-NOTICES.md"
for file in licenses/*; do
    install -Dm644 "$file" "$lib/$file"
done
mkdir -p "$prefix/bin"
ln -sfn "$lib/fermata" "$prefix/bin/fermata"
remove_launcher_files
install -Dm644 "packaging/$id.desktop" "$applications/$id.desktop"
install -Dm644 "packaging/$id.metainfo.xml" "$metainfo/$id.metainfo.xml"
install -Dm644 "$assets/fermata.svg" "$icons/scalable/apps/$id.svg"
for size in "${sizes[@]}"; do
    install -Dm644 "$assets/fermata-$size.png" "$icons/${size}x${size}/apps/$id.png"
done
refresh_launchers
echo "Installed Fermata in $prefix (run: fermata)"
case ":$PATH:" in
    *":$prefix/bin:"*) ;;
    *) echo "Note: $prefix/bin is not on PATH" ;;
esac
