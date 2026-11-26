#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
import argparse
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument("--output-dir", type=Path, required=True)
args = parser.parse_args()
architecture = subprocess.check_output(["dpkg", "--print-architecture"], text=True).strip()
args.output_dir.mkdir(parents=True, exist_ok=True)
with tempfile.TemporaryDirectory(prefix="hdd-sound-package-") as temporary:
    stage = Path(temporary)
    app = stage / "usr/lib/vibeware-hard-drive-sound"
    app.mkdir(parents=True)
    for source in ("main.py", "disk_activity.py"):
        shutil.copyfile(ROOT / "Linux" / source, app / source)
    subprocess.run(["g++", "-std=c++20", "-O2", "-fPIC", "-shared", "-Wall", "-Wextra", "-Werror",
                    str(ROOT / "Shared/Mixer.cpp"), "-o", str(app / "libhdd-mixer.so")], check=True)
    subprocess.run([sys.executable, str(ROOT / "scripts/build-samples.py"), str(app / "default")], check=True)
    executable = stage / "usr/bin/vibeware-hard-drive-sound"
    executable.parent.mkdir()
    executable.write_text("#!/bin/sh\nexec /usr/bin/python3 /usr/lib/vibeware-hard-drive-sound/main.py \"$@\"\n")
    executable.chmod(0o755)
    entry = stage / "usr/share/applications"
    entry.mkdir(parents=True)
    shutil.copyfile(ROOT / "Linux/local.vibeware.HardDriveSound.desktop", entry / "local.vibeware.HardDriveSound.desktop")
    control = stage / "DEBIAN"
    control.mkdir()
    (control / "control").write_text(f"""Package: vibeware-hard-drive-sound
Version: 0.1.0
Architecture: {architecture}
Section: utils
Priority: optional
Maintainer: Umberto Giacobbi
Depends: python3, python3-gi, gir1.2-gtk-3.0, gir1.2-ayatanaappindicator3-0.1, gir1.2-gstreamer-1.0, gstreamer1.0-plugins-base, gstreamer1.0-pulseaudio, libstdc++6
Description: Mechanical hard drive sound driven by disk activity
 Native settings and tray controls for motor and head sounds.
""")
    license_dir = stage / "usr/share/doc/vibeware-hard-drive-sound"
    license_dir.mkdir(parents=True)
    shutil.copyfile(ROOT / "LICENSE", license_dir / "copyright")
    output = args.output_dir / f"vibeware-hard-drive-sound_0.1.0_{architecture}.deb"
    subprocess.run(["dpkg-deb", "--root-owner-group", "--build", str(stage), str(output)], check=True)
