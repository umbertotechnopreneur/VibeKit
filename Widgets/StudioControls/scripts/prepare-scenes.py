#!/usr/bin/env python3
"""Prepare a fresh OBS import file using only bundled templates and local assets."""

import argparse
import json
import os
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parents[1]
TOKEN = "${ASSET_ROOT}/"


def prepare(platform, assets, output):
    if platform not in {"linux", "windows"}:
        raise ValueError("Choose the linux or windows scene template")
    assets = Path(assets).resolve()
    output = Path(output)
    collection = json.loads((PROJECT_ROOT / "Scenes" / f"studio-{platform}.json").read_text())

    def resolve(value):
        if isinstance(value, dict):
            return {key: resolve(item) for key, item in value.items()}
        if isinstance(value, list):
            return [resolve(item) for item in value]
        if isinstance(value, str) and "${ASSET_ROOT}" in value:
            if not value.startswith(TOKEN):
                raise ValueError("Invalid scene asset placeholder")
            name = value[len(TOKEN) :]
            if not name or Path(name).name != name or "\\" in name:
                raise ValueError("Scene asset references must be plain filenames")
            asset = assets / name
            if asset.resolve().parent != assets:
                raise ValueError("Scene assets must stay inside the selected asset directory")
            if not asset.is_file():
                raise FileNotFoundError("Missing bundled asset: " + name)
            return str(asset)
        return value

    result = resolve(collection)
    content = json.dumps(result, ensure_ascii=False, indent=2) + "\n"
    output.parent.mkdir(parents=True, exist_ok=True)
    stream = output.open("x", encoding="utf-8")
    try:
        with stream:
            stream.write(content)
            stream.flush()
            os.fsync(stream.fileno())
    except BaseException:
        output.unlink(missing_ok=True)
        raise
    return output


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--platform", choices=["linux", "windows"], required=True)
    parser.add_argument("--assets", type=Path, default=PROJECT_ROOT / "Assets")
    parser.add_argument(
        "--output",
        type=Path,
        required=True,
        help="New JSON file; existing files are never replaced",
    )
    args = parser.parse_args()
    print(prepare(args.platform, args.assets, args.output))


if __name__ == "__main__":
    main()
