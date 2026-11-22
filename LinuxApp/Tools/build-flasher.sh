#!/usr/bin/env bash
set -euo pipefail
tools=$(cd -- "$(dirname -- "$0")" && pwd)
app=$(dirname "$tools")
out="$app/obj/firmware-helper"
python=${PYTHON:-python3.12}
"$python" -c 'import platform,sys; assert sys.platform=="linux" and platform.machine()=="x86_64" and sys.version_info[:2]==(3,12), "use Linux x64 with CPython 3.12"'
export PYTHONHASHSEED=0 SOURCE_DATE_EPOCH=1735689600
mkdir -p "$out"
rm -f "$out/helper.sha256"
work=$(mktemp -d "$out/build.XXXXXXXX")
trap 'rm -rf -- "$work"' EXIT
"$python" -m venv "$work/venv"
grep '^setuptools==' "$tools/flasher-requirements.lock" > "$work/build-backend.lock"
"$work/venv/bin/python" -m pip install --require-hashes -r "$work/build-backend.lock"
"$work/venv/bin/python" -m pip install --require-hashes --no-build-isolation -r "$tools/flasher-requirements.lock"
"$work/venv/bin/python" -m PyInstaller --clean --noconfirm --onefile --noupx \
    --name VoltraFlasher --distpath "$work/dist" --workpath "$work/work" --specpath "$work" \
    --paths "$app/../VoltraFirmware/Tools" --collect-data esptool --copy-metadata esptool \
    "$tools/linux_flash.py"
"$work/dist/VoltraFlasher" --self-test
"$work/venv/bin/python" "$tools/collect-notices.py" "$out/notices"
mv "$work/dist/VoltraFlasher" "$out/VoltraFlasher"
(cd "$out" && sha256sum VoltraFlasher | cut -d' ' -f1 > "$work/helper.sha256")
mv "$work/helper.sha256" "$out/helper.sha256"
