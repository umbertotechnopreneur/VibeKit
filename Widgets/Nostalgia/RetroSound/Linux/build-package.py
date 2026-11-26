#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
import argparse
import json
from pathlib import Path
import shutil
import subprocess
import sys

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "build"))
from linux_package import FAMILY, package_workspace

ROOT = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument("--output-dir", type=Path)
args = parser.parse_args()
version = json.loads((ROOT / "Linux/metadata.json").read_text(encoding="utf-8"))["version"]
architecture = subprocess.check_output(["dpkg", "--print-architecture"], text=True).strip()
package_name = f"vibeware-retro-sound_{version}_{architecture}.deb"
with package_workspace("RetroSound", architecture, args.output_dir, (package_name,)) as (stage, output):
    app = stage / "usr/lib/vibeware-retro-sound"
    app.mkdir(parents=True)
    for source in ("main.py", "disk_activity.py", "metadata.json"):
        shutil.copyfile(ROOT / "Linux" / source, app / source)
    shutil.copy2(FAMILY / "Shared/Linux/ui/about-content.json", app)
    for name in ("VibeWare.png", "VibeWare-LICENSE.txt"):
        shutil.copy2(FAMILY / "Shared/Assets" / name, app)
    subprocess.run(["g++", "-std=c++20", "-O2", "-fPIC", "-shared", "-Wall", "-Wextra", "-Werror",
                    str(ROOT / "Shared/Mixer.cpp"), "-o", str(app / "libhdd-mixer.so")], check=True)
    subprocess.run([sys.executable, str(ROOT / "scripts/build-samples.py"), str(app / "default")], check=True)
    subprocess.run([sys.executable, str(ROOT / "scripts/build-samples.py"), str(app / "warm"), "--variant", "warm"], check=True)
    executable = stage / "usr/bin/vibeware-retro-sound"
    executable.parent.mkdir()
    executable.write_text("#!/bin/sh\nexec /usr/bin/python3 /usr/lib/vibeware-retro-sound/main.py \"$@\"\n")
    executable.chmod(0o755)
    entry = stage / "usr/share/applications"
    entry.mkdir(parents=True)
    shutil.copyfile(ROOT / "Linux/local.vibeware.RetroSound.desktop", entry / "local.vibeware.RetroSound.desktop")
    shutil.copytree(ROOT / "Shared/icons/hicolor", stage / "usr/share/icons/hicolor", dirs_exist_ok=True)
    control = stage / "DEBIAN"
    control.mkdir()
    (control / "control").write_text(f"""Package: vibeware-retro-sound
Version: {version}
Architecture: {architecture}
Section: utils
Priority: optional
Maintainer: Umberto Giacobbi
Conflicts: vibeware-hard-drive-sound
Replaces: vibeware-hard-drive-sound
Depends: python3, python3-gi, gir1.2-gtk-3.0, gir1.2-ayatanaappindicator3-0.1, gir1.2-gstreamer-1.0, gstreamer1.0-plugins-base, gstreamer1.0-pulseaudio, libstdc++6
Description: Mechanical hard drive sound driven by disk activity
 Native settings and tray controls for motor and head sounds.
""")
    license_dir = stage / "usr/share/doc/vibeware-retro-sound"
    license_dir.mkdir(parents=True)
    shutil.copyfile(ROOT / "LICENSE", license_dir / "copyright")
    package = output / package_name
    subprocess.run(["dpkg-deb", "--root-owner-group", "--build", str(stage), str(package)], check=True)
