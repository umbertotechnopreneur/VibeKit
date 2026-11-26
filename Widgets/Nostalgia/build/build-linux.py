#!/usr/bin/env python3
"""Build the five independent Debian packages and a single package with all five payloads."""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys

from linux_package import FAMILY, package_workspace

PRODUCTS = ("WaterRipple", "Vignette", "RetroTerminal", "CrtDegauss", "RetroSound")


def deb_field(package, name):
    """Read package metadata through Debian's own parser."""
    return subprocess.check_output(["dpkg-deb", "--field", str(package), name], text=True).strip()


def add_payload(package, target, scratch):
    """Merge one unpacked payload, failing on any conflicting file or symlink."""
    extracted = scratch / package.stem
    subprocess.run(["dpkg-deb", "--extract", str(package), str(extracted)], check=True)
    for source in sorted(extracted.rglob("*")):
        destination = target / source.relative_to(extracted)
        if source.is_dir() and not source.is_symlink():
            if destination.is_symlink():
                raise RuntimeError(f"Duplicate Nostalgia payload path: {destination}")
            destination.mkdir(exist_ok=True)
        elif os.path.lexists(destination):
            raise RuntimeError(f"Duplicate Nostalgia payload path: {destination}")
        elif source.is_symlink():
            destination.symlink_to(source.readlink())
        else:
            shutil.copy2(source, destination)


def build_suite(packages, architecture, output):
    """Package all five current builds without requiring separate .deb files at install time."""
    version = json.loads((FAMILY / "build/suite.json").read_text(encoding="utf-8"))["version"]
    filename = f"vibeware-nostalgia_{version}_{architecture}.deb"
    names = [deb_field(package, "Package") for package in packages]
    dependencies = sorted({term.strip() for package in packages
                           for term in deb_field(package, "Depends").split(",") if term.strip()})
    with package_workspace("Nostalgia", architecture, output, (filename,)) as (stage, results):
        for package in packages:
            add_payload(package, stage, stage.parent)
        docs = stage / "usr/share/doc"
        copyright_file = docs / "vibeware-nostalgia/copyright"
        copyright_file.parent.mkdir()
        copyrights = [f"{name}\n\n{(docs / name / 'copyright').read_text(encoding='utf-8').strip()}"
                      for name in names]
        copyright_file.write_text("\n\n".join(copyrights) + "\n", encoding="utf-8")
        control = stage / "DEBIAN"
        control.mkdir()
        (control / "control").write_text(f"""Package: vibeware-nostalgia
Version: {version}
Architecture: {architecture}
Section: gnome
Priority: optional
Maintainer: Umberto Giacobbi
Depends: {', '.join(dependencies)}
Conflicts: {', '.join(names)}, vibeware-hard-drive-sound
Description: Nostalgia desktop effects
 Water Ripple, Vignette, Retro Terminal, CRT Degauss and Retro Sound.
""", encoding="utf-8")
        # Refresh global schemas after both installation and removal of the suite.
        for name in ("postinst", "postrm"):
            script = control / name
            schema_command = "glib-compile-schemas /usr/share/glib-2.0/schemas"
            if name == "postrm":
                schema_command = ("if command -v glib-compile-schemas >/dev/null 2>&1 "
                                  "&& [ -d /usr/share/glib-2.0/schemas ]; then\n"
                                  f"    {schema_command}\nfi")
            script.write_text(f"""#!/bin/sh
set -e
{schema_command}
if command -v gtk-update-icon-cache >/dev/null 2>&1; then
    gtk-update-icon-cache -q -t -f /usr/share/icons/hicolor || true
fi
""", encoding="utf-8")
            script.chmod(0o755)
        subprocess.run(["dpkg-deb", "--root-owner-group", "--build", str(stage), str(results / filename)], check=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output-dir", type=Path, help="Override the suite's final output folder")
    args = parser.parse_args()
    architecture = subprocess.check_output(["dpkg", "--print-architecture"], text=True).strip()
    channel = os.environ.get("NOSTALGIA_CHANNEL", "local")
    root = (FAMILY / Path(os.environ.get("NOSTALGIA_ARTIFACTS") or "artifacts").expanduser()).resolve()
    packages = []
    for product in PRODUCTS:
        arch = architecture if product == "RetroSound" else "all"
        destination = root / "packages" / channel / "linux" / arch / product
        completed = subprocess.run([sys.executable, str(FAMILY / product / "Linux/build-package.py"),
                                    "--output-dir", str(destination)], check=True, stdout=subprocess.PIPE, text=True)
        print(completed.stdout, end="")
        built = [Path(line) for line in completed.stdout.splitlines() if line.endswith(".deb")]
        if len(built) != 1 or not built[0].is_file() or deb_field(built[0], "Architecture") != arch:
            raise RuntimeError(f"Expected one freshly built {arch} Debian package for {product}")
        packages.extend(built)
    build_suite(packages, architecture, args.output_dir)


if __name__ == "__main__":
    main()
