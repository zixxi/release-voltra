"""Prepare both platform manifests with the existing external desktop release key."""
import argparse
import base64
import hashlib
import json
import os
import re
import shutil
import subprocess
import tarfile
import tempfile
import xml.etree.ElementTree as ET
from pathlib import Path


def sha(path):
    h = hashlib.sha256()
    with path.open("rb") as f:
        for block in iter(lambda: f.read(1024 * 1024), b""):
            h.update(block)
    return h.hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("installer", type=Path)
    parser.add_argument("archive", type=Path)
    parser.add_argument("--key", required=True, type=Path)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    props = ET.parse(root / "Directory.Build.props")
    version = props.findtext(".//VoltraVersion")
    repo = props.findtext(".//VoltraUpdateRepository")
    if not re.fullmatch(r"(0|[1-9][0-9]*)(\.(0|[1-9][0-9]*)){2}", version):
        raise ValueError("Expected a stable desktop version")
    exe = f"Voltra-{version}-win-x64.exe"
    archive = f"Voltra-linux-x64-{version}.tar.gz"
    if args.installer.name != exe or args.archive.name != archive:
        raise ValueError("Provide the Windows and Linux assets for the shared version")
    with tarfile.open(args.archive, "r:gz") as tar:
        marker = json.load(tar.extractfile("Voltra-linux-x64/voltra-portable.json"))
        if marker["version"] != version or marker["format"] != 1:
            raise ValueError("Linux portable package version mismatch")
    if args.key.resolve().is_relative_to(root):
        raise ValueError("Keep the signing key outside the repository")
    out = root / "artifacts"
    out.mkdir(exist_ok=True)
    dest = out / f"v{version}"
    if dest.exists():
        raise FileExistsError(f"Release directory already exists: {dest}")
    with tempfile.TemporaryDirectory(dir=out) as work:
        stage = Path(work) / "assets"
        stage.mkdir()
        pub = Path(work) / "release-public.der"
        pub.write_bytes(base64.b64decode(props.findtext(".//VoltraUpdatePublicKey"), validate=True))
        hashes = []
        for source in (args.installer, args.archive):
            package = stage / source.name
            shutil.copyfile(source, package)
            size = package.stat().st_size
            if not 0 < size <= 1024**3:
                raise ValueError("Package exceeds updater limits")
            digest = sha(package)
            manifest = stage / (package.name + ".json")
            signature = stage / (manifest.name + ".sig")
            manifest.write_bytes(json.dumps(dict(product="Voltra", repository=repo, version=version,
                asset=package.name, size=size, sha256=digest), separators=(",", ":")).encode())
            cmd = [os.environ.get("OPENSSL", "openssl"), "dgst", "-sha256", "-sigopt",
                   "rsa_padding_mode:pss", "-sigopt", "rsa_pss_saltlen:digest"]
            subprocess.run(cmd + ["-sign", str(args.key), "-out", str(signature), str(manifest)], check=True)
            subprocess.run(cmd + ["-keyform", "DER", "-verify", str(pub), "-signature", str(signature), str(manifest)], check=True)
            hashes.append(digest + "  " + package.name)
        (stage / "SHA256SUMS.txt").write_bytes(("\n".join(hashes) + "\n").encode())
        stage.rename(dest)
    print(f"Prepared both signed desktop packages in {dest}")


if __name__ == "__main__":
    main()
