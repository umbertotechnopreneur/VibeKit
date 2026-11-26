#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
import argparse
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parent
UUID = 'crt-degauss@vibeware.local'


def build(output):
    output.mkdir(parents=True, exist_ok=True)
    source = ROOT / 'extension' / UUID
    version = json.loads((source / 'metadata.json').read_text())['version-name']
    package = output / f'vibeware-crt-degauss_{version}_all.deb'
    if package.exists():
        raise FileExistsError(package)
    with tempfile.TemporaryDirectory(prefix='crt-degauss-package-') as work:
        stage = Path(work) / 'package'
        extension = stage / 'usr/share/gnome-shell/extensions' / UUID
        shutil.copytree(source, extension, ignore=shutil.ignore_patterns('gschemas.compiled'))
        shutil.copy2(ROOT.parent / 'Shared/degauss.wav', extension / 'degauss.wav')
        subprocess.run([sys.executable, str(ROOT.parent / 'scripts/build-sounds.py'),
                        str(extension / 'sounds')], check=True)
        shared = extension / 'shared'
        shared.mkdir()
        for module in ('shell-effects', 'monitor-selection', 'shell-pressure', 'effect-stop'):
            shutil.copy2(ROOT.parent.parent / 'Shared/Linux' / f'{module}.js', shared)
        subprocess.run(['glib-compile-schemas', '--strict', str(extension / 'schemas')], check=True)
        schemas = stage / 'usr/share/glib-2.0/schemas'
        schemas.mkdir(parents=True)
        for schema in (source / 'schemas').glob('*.xml'):
            shutil.copy2(schema, schemas)
        applications = stage / 'usr/share/applications'
        applications.mkdir(parents=True)
        shutil.copy2(ROOT / 'packaging/local.vibeware.CrtDegauss.desktop', applications)
        copyright_dir = stage / 'usr/share/doc/vibeware-crt-degauss'
        copyright_dir.mkdir(parents=True)
        shutil.copy2(ROOT.parent / 'LICENSE', copyright_dir / 'copyright')
        control = stage / 'DEBIAN'
        control.mkdir()
        (control / 'control').write_text(f'''Package: vibeware-crt-degauss
Version: {version}
Architecture: all
Section: gnome
Priority: optional
Maintainer: Umberto Giacobbi
Depends: gnome-shell (>= 50), gnome-shell (<< 51), gjs, libadwaita-1-0 (>= 1.5), libgtk-4-1, libglib2.0-bin, desktop-file-utils
Description: Gradual CRT magnetization and manual degaussing for GNOME
 Native preferences, status menu, and configurable degauss reminder.
''')
        for name in ('postinst', 'postrm'):
            script = control / name
            script.write_text('#!/bin/sh\nset -e\nglib-compile-schemas /usr/share/glib-2.0/schemas\n')
        for path in stage.rglob('*'):
            path.chmod(0o755 if path.is_dir() or path.name in ('postinst', 'postrm') else 0o644)
        subprocess.run(['dpkg-deb', '--root-owner-group', '--build', str(stage), str(package)], check=True)
    print(package)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description='Build the CRT Degauss Debian package.')
    parser.add_argument('--output-dir', type=Path, required=True)
    build(parser.parse_args().output_dir.resolve())
