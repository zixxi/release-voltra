#!/usr/bin/env bash
set -euo pipefail
root=$(cd -- "$(dirname -- "$0")/../.." && pwd)
cd "$root"
project=LinuxApp/LinuxApp.csproj
tag=${1:?Usage: build-release.sh vX.Y.Z [--reuse-helper]}
[[ "$tag" =~ ^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]] || { echo 'Invalid Linux release tag.' >&2; exit 1; }
[[ "$(uname -sm)" == 'Linux x86_64' ]] || { echo 'Build on Linux x64.' >&2; exit 1; }
version=${tag#v}
base=$(dotnet msbuild "$project" -nologo -getProperty:VoltraVersion)
[[ "$version" == "$base" ]] || { echo "Tag disagrees with VoltraVersion $base" >&2; exit 1; }
case "${2:-}" in
    '') bash LinuxApp/Tools/build-flasher.sh ;;
    --reuse-helper) echo 'Reusing the previously built local helper. Publish will verify its pinned digest.' ;;
    *) echo 'Unknown option.' >&2; exit 1 ;;
esac
mkdir -p artifacts
temp=$(mktemp -d artifacts/.linux-release.XXXXXXXX)
trap 'rm -rf -- "$temp"' EXIT
stage="$temp/Voltra-linux-x64"
dotnet publish "$project" -c Release -r linux-x64 --self-contained true -o "$stage" \
    -p:Version="$version" -p:InformationalVersion="$version" -p:IncludeSourceRevisionInInformationalVersion=false -p:DebugType=None
chmod +x "$stage/LinuxApp" "$stage/tools/VoltraFlasher" "$stage/install-desktop.sh"
for file in LICENSE Selawik-OFL.txt voltra.desktop libcoreclr.so libSkiaSharp.so tools/notices/CPython.txt; do
    test -s "$stage/$file"
done
"$stage/tools/VoltraFlasher" --self-test
python3 LinuxApp/Tools/package-notices.py "$stage" LinuxApp/obj/project.assets.json
src="$stage/tools/source"
mkdir -p "$src/LinuxApp/Tools" "$src/VoltraFirmware/Tools"
cp LinuxApp/Tools/{linux_flash.py,build-flasher.sh,collect-notices.py,flasher-requirements.lock} "$src/LinuxApp/Tools/"
cp VoltraFirmware/Tools/voltra_flash.py "$src/VoltraFirmware/Tools/"
# Ship the matching upstream esptool source alongside the frozen helper.
grep '^esptool==' LinuxApp/Tools/flasher-requirements.lock > "$temp/esptool.lock"
"${PYTHON:-python3.12}" -m pip download --no-deps --require-hashes --no-binary esptool -r "$temp/esptool.lock" -d "$src"
cp LinuxApp/README.md "$stage/README.md"
python3 LinuxApp/Packaging/update-install.py inventory "$stage" "$version"
epoch=$(git log -1 --format=%ct)
tar -C "$temp" --sort=name --mtime="@$epoch" --owner=0 --group=0 --numeric-owner -cf - Voltra-linux-x64 | gzip -n > "$temp/package.gz"
mv -f -- "$temp/package.gz" "artifacts/Voltra-linux-x64-$version.tar.gz"
sha256sum "artifacts/Voltra-linux-x64-$version.tar.gz"
