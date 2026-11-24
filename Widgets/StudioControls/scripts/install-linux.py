#!/usr/bin/env python3
"""Install Studio Controls for the current user; never launch OBS or the panel."""

import argparse
import os
import shutil
import tempfile
from pathlib import Path

PROJECT = Path(__file__).resolve().parents[1]
VERSION = "0.5.0"


def data_home_default():
    configured = os.environ.get("XDG_DATA_HOME", "")
    return (
        Path(configured)
        if configured and Path(configured).is_absolute()
        else Path.home() / ".local/share"
    )


def desktop_argument(value):
    """Escape a literal argument through the desktop-entry and Exec quoting layers."""
    value = str(value)
    if any(character in value for character in "\n\r\x00"):
        raise ValueError("The installation path must not contain line breaks or NUL")
    escaped = "".join(
        "\\" + character if character in '\\"`$' else character for character in value
    )
    return '"' + escaped.replace("\\", "\\\\").replace("%", "%%") + '"'


def desktop_value(value):
    value = str(value)
    if any(character in value for character in "\n\r\x00"):
        raise ValueError("The installation path must not contain line breaks or NUL")
    return value.replace("\\", "\\\\")


def install(data_home):
    data_home = Path(data_home).expanduser().resolve()
    destination = (data_home / "VibeWare/StudioControls/versions" / VERSION).resolve()
    if destination == PROJECT or PROJECT in destination.parents:
        raise ValueError("Choose a user data directory outside this source checkout")
    if destination.exists():
        raise FileExistsError("This version is already installed; existing files were preserved")
    entry = destination / "Linux/controlli-brio.py"
    icon = destination / "Assets/VibeWare-logo.png"
    content = (
        "[Desktop Entry]\nType=Application\nName=Studio Controls\n"
        "Comment=Native controls for local OBS Studio scenes and recording\n"
        f"Exec=env GDK_BACKEND=x11 /usr/bin/python3 {desktop_argument(entry)}\n"
        f"Icon={desktop_value(icon)}\nTerminal=false\nCategories=AudioVideo;Video;\n"
    )
    destination.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix=".install-", dir=destination.parent) as staging:
        staged = Path(staging) / "application"
        shutil.copytree(
            PROJECT,
            staged,
            ignore=shutil.ignore_patterns(
                ".git",
                "__pycache__",
                "*.pyc",
                ".ruff_cache",
                ".pytest_cache",
                "bin",
                "obj",
                "artifacts",
                "Windows",
                "Shared",
            ),
        )
        # Keep the small cross-platform catalog used by package checks, not the .NET build tree.
        for name in ("scene-catalog.json", "ui-translations.json"):
            catalog = PROJECT / "Shared" / name
            if catalog.is_file():
                (staged / "Shared").mkdir(exist_ok=True)
                shutil.copyfile(catalog, staged / "Shared" / name)
        # Reserve exclusively after copying, so an interrupted copy is never an installed version.
        destination.mkdir()
        try:
            os.replace(staged, destination)
        except BaseException:
            destination.rmdir()
            raise
    applications = data_home / "applications"
    applications.mkdir(parents=True, exist_ok=True)
    launcher = applications / "biz.vibeware.StudioControls.desktop"
    descriptor, temporary = tempfile.mkstemp(prefix=".studio-launcher-", dir=applications)
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8") as stream:
            stream.write(content)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, launcher)
    finally:
        Path(temporary).unlink(missing_ok=True)
    return destination, launcher


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--data-home",
        type=Path,
        default=data_home_default(),
    )
    args = parser.parse_args()
    destination, launcher = install(args.data_home)
    print("Installed:", destination)
    print("Launcher:", launcher)
    print("Open Studio Controls from the applications menu when ready.")


if __name__ == "__main__":
    main()
