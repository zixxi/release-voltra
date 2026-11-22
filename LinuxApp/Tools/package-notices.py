"""Copy notices from the exact NuGet packages resolved for this application."""
import json
import shutil
import sys
from pathlib import Path

stage, assets = map(Path, sys.argv[1:])
restore = json.loads(assets.read_text())
folders = list(map(Path, restore["packageFolders"]))
deps = json.loads((stage / "LinuxApp.deps.json").read_text())
for identity, metadata in deps["libraries"].items():
    if metadata["type"] == "project":
        continue
    name, version = identity.removeprefix("runtimepack.").split("/")
    relative = Path(name.lower(), version)
    package = next((base / relative for base in folders if (base / relative).is_dir()), None)
    if package is None:
        raise SystemExit(f"Missing restored package {identity}")
    dest = stage / "notices" / relative
    dest.mkdir(parents=True, exist_ok=True)
    for source in package.iterdir():
        if source.is_file() and (source.suffix == ".nuspec" or
                source.name.upper().startswith(("LICENSE", "LICENCE", "COPYING", "NOTICE", "THIRD-PARTY"))):
            shutil.copyfile(source, dest / source.name)
# Packages declaring an SPDX MIT expression can ship their copyright in the
# nuspec without bundling the text. The runtime pack supplies that license.
runtime = next((stage / "notices/microsoft.netcore.app.runtime.linux-x64").glob("*/LICENSE.TXT"))
shutil.copyfile(runtime, stage / "notices/MIT.txt")
