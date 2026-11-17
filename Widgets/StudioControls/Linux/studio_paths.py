"""Resolve bundled defaults and per-user assets without writing to a checkout."""

import os
import shutil
from pathlib import Path


def user_data_home():
    """XDG paths must be absolute; an empty/relative value must not target a checkout."""
    configured = os.environ.get("XDG_DATA_HOME", "")
    if configured and Path(configured).is_absolute():
        return Path(configured)
    return Path.home() / ".local/share"


def bundled_assets():
    module = Path(__file__).resolve().parent
    packaged = module.parent / "Assets"
    return packaged if packaged.is_dir() else module


def working_assets():
    """Copy missing defaults only; existing project artwork remains user-owned."""
    source = bundled_assets()
    # Retain compatibility with the original, already-installed flat panel.
    if source == Path(__file__).resolve().parent and (source / "Intro-card.png").is_file():
        return source
    target = user_data_home() / "VibeWare/StudioControls/working-assets"
    target.mkdir(parents=True, exist_ok=True)
    for asset in source.iterdir():
        if asset.is_file() and asset.suffix.lower() in {".png", ".mp4", ".wav", ".txt"}:
            destination = target / asset.name
            try:
                output = destination.open("xb")
            except FileExistsError:
                continue
            try:
                with output, asset.open("rb") as original:
                    shutil.copyfileobj(original, output)
            except BaseException:
                destination.unlink(missing_ok=True)
                raise
    return target
