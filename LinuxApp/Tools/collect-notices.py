"""Collect upstream package notices alongside the frozen helper."""
import shutil
import sys
import sysconfig
from importlib.metadata import distributions
from pathlib import Path

out = Path(sys.argv[1])
out.mkdir(parents=True, exist_ok=True)
shutil.copyfile(Path(sysconfig.get_path("stdlib"), "LICENSE.txt"), out / "CPython.txt")
for dist in distributions():
    name = dist.metadata["Name"]
    dest = out / name
    dest.mkdir(exist_ok=True)
    (dest / "METADATA.txt").write_text(dist.read_text("METADATA") or name, encoding="utf-8")
    for item in dist.files or ():
        if any(word in item.name.upper() for word in ("LICENSE", "COPYING", "NOTICE")):
            path = Path(dist.locate_file(item))
            if path.is_file():
                target = dest.joinpath(*(part for part in item.parts if part not in ("..", ".")))
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(path, target)
