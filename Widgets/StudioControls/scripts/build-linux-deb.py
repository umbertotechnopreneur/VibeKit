#!/usr/bin/env python3
"""Build a reproducible, uninstalled Debian package using only project-owned files."""

import argparse
import hashlib
import math
import os
import re
import shutil
import subprocess
import tempfile
import tomllib
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parents[1]
PACKAGE = "studio-controls"
PREFIX = Path("usr/lib/studio-controls")
ASSET_NAMES = (
    "Sfondo-Hardware-Demo.png",
    "Cornice-Software.png",
    "Cornice-Tavolo.png",
    "Firma.png",
    "Attesa-Tavolo.png",
    "Vignette.png",
    "OBS-logo.png",
    "OBS-COPYING.txt",
    "VibeWare-logo.png",
    "CRT-test.mp4",
    "Lab-intro.wav",
    "CREDITS.txt",
    "Intro-card.png",
    "Thank-you-card.png",
    "Chapter-card.png",
    "Privacy-card.png",
    "Compare-frame.png",
    "CRT-overlay.png",
)
RUNTIME_FILES = (
    "camera_controls.py",
    "localization.py",
    "gtk_localization.py",
    "configure_lab.py",
    "controlli-brio.py",
    "diagnostics.py",
    "memory_watchdog.py",
    "lab_catalog.py",
    "lab_titles.py",
    "obs_remote.py",
    "panel_config.py",
    "panel_theme.py",
    "screen_framing.py",
    "studio_paths.py",
    "workflow_panel.py",
    "workflows.py",
    "window_placement.py",
)
DEPENDENCIES = (
    "python3 (>= 3.11)",
    "python3-gi",
    "python3-pil (>= 10)",
    "gir1.2-gtk-4.0",
    "fonts-dejavu-core",
    "libx11-6",
)
DESKTOP_ENTRY = """[Desktop Entry]
Type=Application
Name=Studio Controls
Comment=Native controls for local OBS Studio scenes and recording
Exec=studio-controls
Icon=biz.vibeware.StudioControls
Terminal=false
Categories=AudioVideo;Video;
StartupNotify=true
"""


def _copy(source, destination):
    source, destination = Path(source), Path(destination)
    if source.is_symlink() or not source.is_file():
        raise ValueError("Package input must be a regular project file: " + str(source))
    destination.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(source, destination)
    destination.chmod(0o644)


def _write(path, content, executable=False):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(content, encoding="utf-8")
    path.chmod(0o755 if executable else 0o644)


def _launcher(script, x11=False):
    environment = "env GDK_BACKEND=x11 " if x11 else ""
    return (
        f'#!/bin/sh\nexec {environment}/usr/bin/python3 -B /usr/lib/studio-controls/{script} "$@"\n'
    )


def _validate_metadata(version, epoch, maintainer):
    if len(version) > 64 or not re.fullmatch(r"[0-9][0-9A-Za-z]*(?:[.+~-][0-9A-Za-z]+)*", version):
        raise ValueError("Use a simple Debian version such as 0.3.1 or 0.3.1-1")
    if not isinstance(epoch, int) or epoch < 0 or epoch > 4_294_967_295:
        raise ValueError("Build epoch must be a nonnegative Unix timestamp below 2^32")
    if not maintainer.strip() or any(character in maintainer for character in "\r\n\x00"):
        raise ValueError("Maintainer must be one nonempty line")


def stage_package(
    destination, *, project=PROJECT_ROOT, version="0.3.1", epoch=0, maintainer="Umberto Giacobbi"
):
    """Create a fresh package tree; never inspect or modify installed application data."""
    _validate_metadata(version, epoch, maintainer)
    project, destination = Path(project).resolve(), Path(destination)
    destination.mkdir(parents=True, exist_ok=False)
    application = destination / PREFIX
    for name in RUNTIME_FILES:
        _copy(project / "Linux" / name, application / "Linux" / name)
    vendor = project / "Linux/_vendor"
    for required in ("websocket/__init__.py", "websocket_client-1.9.2.dist-info/licenses/LICENSE"):
        if not (vendor / required).is_file():
            raise FileNotFoundError("The licensed websocket-client runtime is missing: " + required)
    for source in sorted(vendor.rglob("*")):
        relative = source.relative_to(vendor)
        if any(part in {"tests", "__pycache__", ".pytest_cache"} for part in relative.parts):
            continue
        if source.is_file() and source.suffix != ".pyc":
            _copy(source, application / "Linux/_vendor" / relative)
    for name in ASSET_NAMES:
        _copy(project / "Assets" / name, application / "Assets" / name)
    for platform in ("linux", "windows"):
        _copy(
            project / "Scenes" / f"studio-{platform}.json",
            application / "Scenes" / f"studio-{platform}.json",
        )
    for name in ("scene-catalog.json", "ui-translations.json"):
        _copy(project / "Shared" / name, application / "Shared" / name)
    _copy(project / "scripts/prepare-scenes.py", application / "scripts/prepare-scenes.py")
    for name in ("LICENSE", "THIRD-PARTY-NOTICES.md"):
        _copy(project / name, application / name)
    documentation = destination / "usr/share/doc" / PACKAGE
    for name in ("README.md", "QUICK-START.txt", "VALIDATION.txt", "DEBIAN.md"):
        _copy(project / "Linux" / name, documentation / name)
    _copy(project / "THIRD-PARTY-NOTICES.md", documentation / "THIRD-PARTY-NOTICES.md")
    _copy(project / "Assets/CREDITS.txt", documentation / "CREDITS.txt")
    copyright_text = (
        (project / "LICENSE").read_text()
        + "\nThird-party notices\n\n"
        + (project / "THIRD-PARTY-NOTICES.md").read_text()
    )
    _write(documentation / "copyright", copyright_text)
    _write(
        destination / "usr/bin/studio-controls",
        _launcher("Linux/controlli-brio.py", x11=True),
        executable=True,
    )
    _write(
        destination / "usr/bin/studio-controls-prepare-scenes",
        _launcher("scripts/prepare-scenes.py"),
        executable=True,
    )
    _write(
        destination / "usr/share/applications/biz.vibeware.StudioControls.desktop", DESKTOP_ENTRY
    )
    _copy(
        project / "Assets/VibeWare-logo.png",
        destination / "usr/share/pixmaps/biz.vibeware.StudioControls.png",
    )
    installed_size = math.ceil(
        sum(path.stat().st_size for path in destination.rglob("*") if path.is_file()) / 1024
    )
    control = f"""Package: {PACKAGE}
Version: {version}
Section: video
Priority: optional
Architecture: all
Maintainer: {maintainer}
Installed-Size: {installed_size}
Depends: {", ".join(DEPENDENCIES)}
Recommends: x11-xserver-utils, xwayland, fonts-noto-cjk
Suggests: obs-studio
Homepage: https://github.com/umbertotechnopreneur/VibeKit
Description: native control desk for local OBS Studio demonstrations
 GTK controls for scenes, recording, camera framing, screen details, independent
 vignettes and locally rendered project cards. Connects only to local OBS.
 Installation does not start applications, access devices or modify OBS settings.
"""
    _write(destination / "DEBIAN/control", control)
    sums = []
    for path in sorted(destination.rglob("*")):
        if path.is_file() and "DEBIAN" not in path.relative_to(destination).parts:
            digest = hashlib.md5(path.read_bytes(), usedforsecurity=False).hexdigest()
            sums.append(f"{digest}  {path.relative_to(destination).as_posix()}")
    _write(destination / "DEBIAN/md5sums", "\n".join(sums) + "\n")
    # Fixed modes and mtimes avoid host umask, checkout timestamps and owner metadata.
    for path in sorted(destination.rglob("*"), reverse=True):
        if path.is_dir():
            path.chmod(0o755)
        os.utime(path, (epoch, epoch), follow_symlinks=False)
    destination.chmod(0o755)
    os.utime(destination, (epoch, epoch))
    return destination


def build(
    *, project=PROJECT_ROOT, output_dir, version="0.3.1", epoch=0, maintainer="Umberto Giacobbi"
):
    _validate_metadata(version, epoch, maintainer)
    executable = shutil.which("dpkg-deb")
    if not executable:
        raise RuntimeError("Install the distribution's dpkg tools to build a .deb package")
    output_dir = Path(output_dir).resolve()
    output_dir.mkdir(parents=True, exist_ok=True)
    output = output_dir / f"{PACKAGE}_{version}_all.deb"
    if output.exists():
        raise FileExistsError("Package output already exists; choose a fresh output directory")
    # Portable output volumes may ignore Unix modes. dpkg needs a POSIX staging tree.
    with tempfile.TemporaryDirectory(prefix=".studio-deb-") as temporary:
        temporary = Path(temporary)
        stage_package(
            temporary / "package",
            project=project,
            version=version,
            epoch=epoch,
            maintainer=maintainer,
        )
        staged_deb = temporary / "package.deb"
        environment = dict(os.environ, SOURCE_DATE_EPOCH=str(epoch), LC_ALL="C", TZ="UTC")
        subprocess.run(
            [
                executable,
                "--build",
                "--root-owner-group",
                "--uniform-compression",
                "-Zxz",
                "-z6",
                "--threads-max=1",
                str(temporary / "package"),
                str(staged_deb),
            ],
            env=environment,
            check=True,
            timeout=120,
        )
        # Exclusive publication preserves earlier release files, including on unsupported hard-link filesystems.
        target = output.open("xb")
        try:
            with target, staged_deb.open("rb") as source:
                shutil.copyfileobj(source, target)
                target.flush()
                os.fsync(target.fileno())
        except BaseException:
            output.unlink(missing_ok=True)
            raise
    return output


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    with (PROJECT_ROOT / "Linux/pyproject.toml").open("rb") as stream:
        default_version = tomllib.load(stream)["project"]["version"]
    parser.add_argument("--version", default=default_version)
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--epoch", type=int, default=int(os.environ.get("SOURCE_DATE_EPOCH", "0")))
    parser.add_argument("--maintainer", default="Umberto Giacobbi")
    args = parser.parse_args()
    package = build(
        output_dir=args.output_dir,
        version=args.version,
        epoch=args.epoch,
        maintainer=args.maintainer,
    )
    print("Built (not installed):", package)


if __name__ == "__main__":
    main()
