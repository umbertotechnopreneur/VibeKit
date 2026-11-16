#!/usr/bin/env python3
"""Test the real extension in a separate headless GNOME session and private D-Bus.

Never uses --replace or the user's runtime/settings directories. It terminates
only the short-lived test session. Screenshots contain a synthetic fixture.
"""
# SPDX-License-Identifier: MIT
import argparse
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
from PIL import Image


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output-dir', type=Path, required=True)
    parser.add_argument('--mode', choices=['user', 'ubuntu'], default='user')
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
        extension = stage / 'data/gnome-shell/extensions' / uuid
        shutil.copytree(root / 'Linux/extension' / uuid, extension)
        (extension / 'extension.js').rename(extension / 'production-extension.js')
        shutil.copy2(root / 'tests/gnome-smoke.js', extension / 'extension.js')
        subprocess.run(['glib-compile-schemas', '--strict', str(extension / 'schemas')], check=True)
        runner = stage / 'run.sh'
        runner.write_text('''#!/bin/sh
set -eu
gsettings set org.gnome.shell enabled-extensions "['retro-terminal@vibeware.local']"
gsettings set org.gnome.shell disable-user-extensions false
gsettings set org.gnome.shell welcome-dialog-last-shown-version '50'
exec gnome-shell --headless --wayland --no-x11 --virtual-monitor=1280x800 --wayland-display=retro-test-only --mode="$RETRO_TEST_MODE"
''')
        result = subprocess.run(['timeout', '--signal=TERM', '40s', 'dbus-run-session', '--', 'sh', str(runner)],
                                env=env, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=50)
        (output / 'gnome-smoke.log').write_text(result.stdout)
        if 'RETRO_SMOKE_PASS' not in result.stdout or 'RETRO_SMOKE_FAILED' in result.stdout:
            raise SystemExit(f'GNOME smoke failed: inspect {output / "gnome-smoke.log"}')
        if result.returncode not in (0, 124):
            raise SystemExit(f'Test session exited unexpectedly: {result.returncode}')
        # Verify temporal behavior, not merely allocation of a history texture.
        # The driver moves a white marker from x240..360 to x800 at y650..686.
        with Image.open(output / 'gnome-persistence.png') as image:
            history = image.convert('RGB')
        with Image.open(output / 'gnome-persistence-off.png') as image:
            cleared = image.convert('RGB')
        def difference(y_start, y_end):
            values = [sum(history.getpixel((x, y))) - sum(cleared.getpixel((x, y)))
                      for y in range(y_start, y_end) for x in range(260, 340)]
            return sum(values) / len(values)
        residual = difference(655, 680) - difference(625, 640)
        if residual <= 10:
            raise SystemExit('Temporal check failed: old marker position has no retained afterglow')
        print(f'PASS retained old-position afterglow: background-adjusted RGB-sum difference {residual:.1f}')
        print(f'PASS real GNOME {args.mode} session: custom controls, glitches, history, stop and cleanup; screenshots in {output}')


if __name__ == '__main__':
    main()
