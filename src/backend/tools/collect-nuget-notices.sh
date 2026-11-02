#!/usr/bin/env bash
set -euo pipefail
if [[ $# -ne 2 ]]; then
    printf '%s\n' 'Usage: collect-nuget-notices.sh API_PROJECT OUTPUT_DIRECTORY' >&2
    exit 2
fi
PROJECT="$1"
OUTPUT="$2"
PACKAGES="${NUGET_PACKAGES:-$HOME/.nuget/packages}"
mkdir -p "$OUTPUT"
dotnet list "$PROJECT" package --include-transitive --format json --no-restore > "$OUTPUT/dependencies.json"
if [[ ! -d "$PACKAGES" ]]; then
    printf '%s\n' 'Restored NuGet package metadata is missing; dependency notices were not collected.' >&2
    exit 1
fi
while IFS= read -r -d '' file; do
    relative="${file#"$PACKAGES/"}"
    destination="$OUTPUT/packages/$relative"
    mkdir -p "$(dirname -- "$destination")"
    cp -- "$file" "$destination"
done < <(find "$PACKAGES" -type f \( -iname '*.nuspec' -o -iname 'license*' -o -iname 'licence*' \
    -o -iname 'copying*' -o -iname 'copyright*' -o -iname 'notice*' \) -print0)
