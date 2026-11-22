#!/usr/bin/env bash
set -euo pipefail
dir=$(cd -- "$(dirname -- "$0")" && pwd -P)
data=${XDG_DATA_HOME:-"$HOME/.local/share"}
[[ -x "$dir/LinuxApp" && ! "$dir" =~ [[:cntrl:]] ]] || { echo 'Extract Voltra into a permanent directory first.' >&2; exit 1; }
mkdir -p "$data/applications"
for size in 16 24 32 48 64 128 256 512; do
    install -Dm644 "$dir/Assets/Icons/voltra-$size.png" "$data/icons/hicolor/${size}x${size}/apps/voltra.png"
done
# Escape Exec argument syntax, then escape the desktop entry string containing it.
exe="$dir/LinuxApp"
exe=${exe//\\/\\\\}
exe=${exe//\"/\\\"}
exe=${exe//\$/\\\$}
exe=${exe//\`/\\\`}
exe=${exe//%/%%}
file=$(mktemp "$data/applications/.voltra.XXXXXXXX")
trap 'rm -f -- "$file"' EXIT
while IFS= read -r line; do
    if [[ "$line" == Exec=* ]]; then printf 'Exec="%s"\n' "${exe//\\/\\\\}"; else printf '%s\n' "$line"; fi
done < "$dir/voltra.desktop" > "$file"
chmod 644 "$file"
mv -f -- "$file" "$data/applications/voltra.desktop"
touch "$data/icons/hicolor"
echo "Installed Voltra launcher for $dir"
