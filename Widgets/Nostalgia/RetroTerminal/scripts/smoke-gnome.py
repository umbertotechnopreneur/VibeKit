#!/usr/bin/env python3
"""Test the real extension in a separate headless GNOME session and private D-Bus.

Never uses --replace or the user's runtime/settings directories. It terminates
only the short-lived test session. Screenshots contain a synthetic fixture.
"""
# SPDX-License-Identifier: MIT
import argparse
import json
import os
from pathlib import Path
import shutil
import re
import subprocess
import tempfile
from PIL import Image, ImageChops, ImageStat


def check_presets(output, log):
    match = re.search(r'RETRO_PRESET_FRAMES (\[[^\n]+\])', log)
    frames = json.loads(match.group(1)) if match else []
    if len(set(frames)) != 40 or len(frames) != 40:
        raise SystemExit(f'Expected 40 preset test frames, found {len(frames)}')
    for name in frames:
        with (Image.open(output / f'{name}.png') as frame,
              Image.open(output / f'{name}-no-grain.png') as without_grain,
              Image.open(output / f'{name}-no-raster.png') as without_raster):
            full = frame.convert('L')
            raster = without_grain.convert('L')
            flat = without_raster.convert('L')
        grain_rms = ImageStat.Stat(ImageChops.difference(full, raster)).rms[0]
        # Average along each row to exclude the horizontal phosphor pattern.
        width, height = flat.size
        scanline_drop = 0
        for y in range(height):
            row = (0, y, width, y + 1)
            base = ImageStat.Stat(flat.crop(row)).mean[0]
            if base <= 0:
                raise SystemExit(f'{name}: expected a lit test patch')
            scanline_drop = max(scanline_drop, 1 - ImageStat.Stat(raster.crop(row)).mean[0] / base)
        print(f'{name}: scanline drop {scanline_drop:.1%}, grain RMS {grain_rms:.2f}/255')
        if not 0.01 <= scanline_drop <= 0.065 or not 0.9 <= grain_rms <= 3.6:
            raise SystemExit(f'{name}: scanlines or grain are outside the expected visible, light range')
    print(f'PASS: rendered all {len(frames)} built-ins in GNOME; screenshots in {output}')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output-dir', type=Path, required=True)
    parser.add_argument('--mode', choices=['user', 'ubuntu'], default='user')
    scenario = parser.add_mutually_exclusive_group()
    scenario.add_argument('--oversized-capture', action='store_true', help='Exercise a real 6720×2160 capture at 150% scale with bounded afterglow')
    scenario.add_argument('--presets', action='store_true', help='Measure scanline contrast and static grain in all built-in looks')
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    output = args.output_dir.resolve()
    output.mkdir(parents=True, exist_ok=True)
    uuid = 'retro-terminal@vibeware.local'
    with tempfile.TemporaryDirectory(prefix='retro-gnome-smoke-') as tmp:
        stage = Path(tmp)
        env = os.environ.copy()
        # Set isolation BEFORE starting dbus-run-session: activated services must
        # inherit the same private settings/runtime directories as the shell.
        for key, folder in [('XDG_CONFIG_HOME', 'config'), ('XDG_DATA_HOME', 'data'),
                            ('XDG_CACHE_HOME', 'cache'), ('XDG_STATE_HOME', 'state'),
                            ('XDG_RUNTIME_DIR', 'runtime')]:
            path = stage / folder
            path.mkdir(mode=0o700)
            env[key] = str(path)
        for key in ['DISPLAY', 'WAYLAND_DISPLAY', 'GNOME_KEYRING_CONTROL', 'XDG_SESSION_ID']:
            env.pop(key, None)
        env['GSETTINGS_BACKEND'] = 'dconf'
        env['RETRO_TEST_OUTPUT'] = str(output)
        env['RETRO_TEST_MODE'] = args.mode
        env['RETRO_TEST_OVERSIZED'] = '1' if args.oversized_capture else '0'
        env['RETRO_TEST_PRESETS'] = '1' if args.presets else '0'
        env['RETRO_TEST_MONITOR'] = '6720x2160' if args.oversized_capture else '1280x800'
        extension = stage / 'data/gnome-shell/extensions' / uuid
        shutil.copytree(root / 'Linux/extension' / uuid, extension)
        shared = extension / 'shared'
        shared.mkdir(exist_ok=True)
        shutil.copytree(root.parent / 'Shared/Linux', shared, dirs_exist_ok=True, ignore=shutil.ignore_patterns('tests'))
        for name in ('VibeWare.png', 'VibeWare-LICENSE.txt'):
            shutil.copy2(root.parent / 'Shared/Assets' / name, shared.parent / name)
        (extension / 'extension.js').rename(extension / 'production-extension.js')
        shutil.copy2(root / 'tests/gnome-smoke.js', extension / 'extension.js')
        subprocess.run(['glib-compile-schemas', '--strict', str(extension / 'schemas')], check=True)
        runner = stage / 'run.sh'
        runner.write_text('''#!/bin/sh
set -eu
if gsettings list-keys org.freedesktop.Tracker3.Miner.Files >/dev/null 2>&1; then
    gsettings set org.freedesktop.Tracker3.Miner.Files index-recursive-directories '[]'
    gsettings set org.freedesktop.Tracker3.Miner.Files index-single-directories '[]'
    gsettings set org.freedesktop.Tracker3.Miner.Files enable-monitors false
fi
gsettings set org.gnome.shell enabled-extensions "['retro-terminal@vibeware.local']"
gsettings set org.gnome.shell disable-user-extensions false
gsettings set org.gnome.shell welcome-dialog-last-shown-version '50'
gsettings set org.gnome.mutter experimental-features "['scale-monitor-framebuffer']"
exec gnome-shell --headless --wayland --no-x11 --virtual-monitor="$RETRO_TEST_MONITOR" --wayland-display=retro-test-only --mode="$RETRO_TEST_MODE"
''')
        result = subprocess.run(['timeout', '--signal=TERM', '40s', 'dbus-run-session', '--', 'sh', str(runner)],
                                env=env, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=50)
        (output / 'gnome-smoke.log').write_text(result.stdout)
        if 'RETRO_SMOKE_PASS' not in result.stdout or 'RETRO_SMOKE_FAILED' in result.stdout:
            raise SystemExit(f'GNOME smoke failed: inspect {output / "gnome-smoke.log"}')
        if result.returncode not in (0, 124):
            raise SystemExit(f'Test session exited unexpectedly: {result.returncode}')
        if args.presets:
            check_presets(output, result.stdout)
            return
        # Verify temporal behavior, not merely allocation of a history texture.
        # The driver moves a white marker from x240..360 to x800 at y650..686.
        with Image.open(output / 'gnome-persistence.png') as image:
            history = image.convert('RGB')
        with Image.open(output / 'gnome-persistence-off.png') as image:
            cleared = image.convert('RGB')
        info_match = re.search(r'RETRO_CAPTURE_INFO (\{[^\n]+\})', result.stdout)
        info = json.loads(info_match.group(1)) if info_match else None
        if args.oversized_capture and not info:
            raise SystemExit('Oversized capture test did not report actual capture dimensions')
        scale = history.width / info['logicalWidth'] if info else 1
        def difference(x_start, x_end, y_start, y_end):
            values = [sum(history.getpixel((x, y))) - sum(cleared.getpixel((x, y)))
                      for y in range(round(y_start * scale), round(y_end * scale))
                      for x in range(round(x_start * scale), round(x_end * scale))]
            return sum(values) / len(values)
        markers = [(240, 650)]
        if info:
            markers.append((info['logicalWidth'] - 240, info['logicalHeight'] - 180))
        for x, y in markers:
            residual = difference(x + 20, x + 100, y + 5, y + 30) - difference(x + 20, x + 100, y - 25, y - 10)
            if residual <= 10:
                raise SystemExit(f'Temporal check failed: old marker at {x},{y} has no retained afterglow')
            print(f'PASS retained afterglow at {x},{y}: background-adjusted RGB-sum difference {residual:.1f}')
        if not args.oversized_capture:
            def marker_color(name):
                with Image.open(output / f'gnome-{name}.png') as image:
                    marker = image.convert('RGB').crop((270, 660, 330, 680))
                return tuple(round(channel, 1) for channel in ImageStat.Stat(marker).mean)
            green, amber, cyan, tray_amber = (marker_color(name) for name in ('green', 'amber', 'custom-cyan', 'tray-amber'))
            if not (green[1] > green[0] + 30 and amber[0] > amber[1] + 30 and
                    cyan[2] > cyan[0] + 30 and tray_amber[0] > tray_amber[1] + 30):
                raise SystemExit(f'Palette pixels did not match their settings: {green}, {amber}, {cyan}, {tray_amber}')
            print(f'PASS live palettes on the synthetic white marker: green={green}, amber={amber}, cyan={cyan}, tray amber={tray_amber}')
        if info:
            print(f"PASS native capture {info['nativeWidth']}×{info['nativeHeight']} reduced to {info['width']}×{info['height']}")
        checks = 'oversized capture, scaled history, stop and cleanup' if args.oversized_capture else 'custom controls, glitches, history, stop and cleanup'
        print(f'PASS real GNOME {args.mode} session: {checks}; screenshots in {output}')


if __name__ == '__main__':
    main()
