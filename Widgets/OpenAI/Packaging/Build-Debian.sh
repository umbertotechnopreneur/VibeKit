#!/usr/bin/env bash
set -euo pipefail

root=$(cd -- "$(dirname -- "$0")/.." && pwd)
output=$(realpath -m -- "${1:-$root/artifacts/installers}")
cd -- "$root"
version=$(python3 -c 'import xml.etree.ElementTree as ET; print(ET.parse("Directory.Build.props").findtext(".//Version"))')
mkdir -p -- "$output"
installer="$output/openai-usage_${version}-1_amd64.deb"
if [[ -e "$installer" ]]; then
    printf 'Choose a new output directory. Installer already exists: %s\n' "$installer" >&2
    exit 1
fi
stage=$(mktemp -d "$output/.openai-usage-XXXXXX")
payload="$stage/payload"
export NUGET_PACKAGES="$stage/nuget"

dotnet publish Backend/OpenAIUsage.Backend.csproj -c Release -r linux-x64 --self-contained false -o "$payload/backend"
test -f "$payload/backend/OpenAIUsage.Backend"
test -f "$payload/backend/OpenAIUsage.Backend.runtimeconfig.json"
mkdir -p -- "$payload/Linux" "$payload/licenses"
cp -- Linux/*.py Linux/run.sh "$payload/Linux/"
cp -R -- Branding "$payload/Branding"
while IFS= read -r -d '' notice; do
    relative=${notice#"$NUGET_PACKAGES/"}
    cp -- "$notice" "$payload/licenses/${relative//\//-}"
done < <(find "$NUGET_PACKAGES" -mindepth 3 -maxdepth 3 -type f \( -iname 'license*' -o -iname 'notice*' -o -iname 'third*party*' \) -print0)
python3 Packaging/package.py deb "$payload" "$installer"
