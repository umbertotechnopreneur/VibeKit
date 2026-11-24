#!/usr/bin/env python3
"""Measure real Ripple animation callbacks in an isolated headless GNOME session.

Uses private settings, D-Bus and runtime directories. Never replaces, restarts,
or terminates the user's graphical session. Callback cadence is not GPU time.
"""
# SPDX-License-Identifier: GPL-3.0-or-later
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output-dir', type=Path, required=True)
    parser.add_argument('--mode', choices=['user', 'ubuntu'], default='ubuntu')
    parser.add_argument('--screenshots', action='store_true',
                        help='Save synthetic fixture images outside the cadence measurements')
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    output = args.output_dir.resolve()
    output.mkdir(parents=True, exist_ok=True)
    uuid = 'ripple@vibeware.local'
    with tempfile.TemporaryDirectory(prefix='ripple-gnome-smoke-') as tmp:
        stage = Path(tmp)
        env = os.environ.copy()
        # D-Bus-activated services must inherit isolation, so configure it before
        # dbus-run-session, not merely inside the shell's startup script.
        for key, folder in [('XDG_CONFIG_HOME', 'config'), ('XDG_DATA_HOME', 'data'),
                            ('XDG_CACHE_HOME', 'cache'), ('XDG_STATE_HOME', 'state'),
                            ('XDG_RUNTIME_DIR', 'runtime')]:
            path = stage / folder
            path.mkdir(mode=0o700)
            env[key] = str(path)
        for key in ['DISPLAY', 'WAYLAND_DISPLAY', 'GNOME_KEYRING_CONTROL', 'XDG_SESSION_ID']:
            env.pop(key, None)
        env['GSETTINGS_BACKEND'] = 'dconf'
        env['RIPPLES_TEST_OUTPUT'] = str(output)
        env['RIPPLES_TEST_MODE'] = args.mode
        env['RIPPLES_TEST_SCREENSHOTS'] = '1' if args.screenshots else '0'
        extension = stage / 'data/gnome-shell/extensions' / uuid
        shutil.copytree(root / 'Linux/extension' / uuid, extension)
        shared = extension / 'shared'
        shared.mkdir(exist_ok=True)
        shutil.copytree(root.parent / 'Shared/Linux', shared, dirs_exist_ok=True, ignore=shutil.ignore_patterns('tests'))
        (extension / 'extension.js').rename(extension / 'production-extension.js')
        shutil.copy2(root / 'tests/gnome-animation-smoke.js', extension / 'extension.js')
        subprocess.run(['glib-compile-schemas', '--strict', str(extension / 'schemas')], check=True)
        runner = stage / 'run.sh'
        runner.write_text('''#!/bin/sh
set -eu
if gsettings list-keys org.freedesktop.Tracker3.Miner.Files >/dev/null 2>&1; then
    gsettings set org.freedesktop.Tracker3.Miner.Files index-recursive-directories '[]'
    gsettings set org.freedesktop.Tracker3.Miner.Files index-single-directories '[]'
    gsettings set org.freedesktop.Tracker3.Miner.Files enable-monitors false
fi
gsettings set org.gnome.shell enabled-extensions "['ripple@vibeware.local']"
gsettings set org.gnome.shell disable-user-extensions false
gsettings set org.gnome.shell welcome-dialog-last-shown-version '50'
exec gnome-shell --headless --wayland --no-x11 --virtual-monitor=1280x800 --wayland-display=ripple-test-only --mode="$RIPPLES_TEST_MODE"
''')
        result = subprocess.run(
            ['timeout', '--signal=TERM', '--kill-after=3s', '55s', 'dbus-run-session', '--', 'sh', str(runner)],
            env=env, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=62)
        log = output / 'gnome-animation-smoke.log'
        log.write_text(result.stdout)
        metrics = []
        for line in result.stdout.splitlines():
            if 'RIPPLES_CADENCE ' in line:
                metrics.append(json.loads(line.split('RIPPLES_CADENCE ', 1)[1]))
        (output / 'cadence.json').write_text(json.dumps(metrics, indent=2) + '\n')
        if 'RIPPLES_SMOKE_PASS' not in result.stdout or 'RIPPLES_SMOKE_FAILED' in result.stdout:
            raise SystemExit(f'GNOME animation smoke failed: inspect {log}')
        if result.returncode not in (0, 124):
            raise SystemExit(f'Test session exited unexpectedly: {result.returncode}; inspect {log}')
        for metric in metrics:
            print(f"{metric['label']}: {metric['updatesPerSecond']:.2f} updates/s; "
                  f"gap p50 {metric['p50GapMs']:.2f} ms, p95 {metric['p95GapMs']:.2f} ms")
        print(f'PASS GNOME {args.mode}: real shader, FPS change, age continuity, clear, manual stop and source cleanup')
        print(f'Callback cadence only, not GPU render time. Results: {output}')


if __name__ == '__main__':
    main()
