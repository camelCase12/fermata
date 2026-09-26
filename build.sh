#!/usr/bin/env bash
# Builds Fermata as a native executable (NativeAOT) in bin/fermata.
#
#   ./build.sh         build bin/fermata/fermata
#   ./build.sh test    build, then run the checks in tests/Fermata.Tests
#
# The .NET SDK's home and package cache default to /tmp. FERMATA_DOTNET_HOME and
# FERMATA_NUGET_PACKAGES move them.
set -euo pipefail
cd "$(dirname "$0")"
export DOTNET_CLI_HOME="${FERMATA_DOTNET_HOME:-/tmp/fermata-dotnet}"
export NUGET_PACKAGES="${FERMATA_NUGET_PACKAGES:-/tmp/fermata-nuget}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_GENERATE_ASPNET_CERTIFICATE=false

# Publish beside the old build, then swap files in, so a running Fermata keeps working.
mkdir -p bin/fermata
publish=$(mktemp -d bin/.publish.XXXXXX)
trap 'rm -rf -- "$publish"' EXIT
dotnet publish src/Fermata/Fermata.csproj -c Release -r linux-x64 -p:PublishAot=true -o "$publish"
for file in "$publish"/*; do
    mv -f -- "$file" bin/fermata/
done
echo "Built bin/fermata/fermata"

if [[ "${1:-}" == test ]]; then
    dotnet run --project tests/Fermata.Tests -c Release
fi
