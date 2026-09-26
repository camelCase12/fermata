#!/usr/bin/env bash
# Packages the build in bin/fermata as dist/fermata-VERSION-linux-x64.tar.gz, with install.sh, the
# launcher entry, icons, AppStream metadata and license notices. Run ./build.sh first.
set -euo pipefail
cd "$(dirname "$0")/.."
version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props)
name="fermata-$version-linux-x64"
stage="dist/$name"
rm -rf -- "$stage" "dist/$name.tar.gz"
mkdir -p "$stage/icons" "$stage/packaging" "$stage/licenses"
for file in fermata libSkiaSharp.so libHarfBuzzSharp.so; do
    install -m755 "bin/fermata/$file" "$stage/$file"
done
install -m755 install.sh "$stage/install.sh"
install -m644 README.md LICENSE THIRD-PARTY-NOTICES.md "$stage/"
install -m644 licenses/* "$stage/licenses/"
install -m644 packaging/*.desktop packaging/*.metainfo.xml "$stage/packaging/"
install -m644 src/Fermata/Assets/fermata.svg src/Fermata/Assets/fermata-*.png "$stage/icons/"
tar -C dist --owner=0 --group=0 -czf "dist/$name.tar.gz" "$name"
rm -rf -- "$stage"
(cd dist && sha256sum "$name.tar.gz" > "$name.tar.gz.sha256")
echo "dist/$name.tar.gz"
