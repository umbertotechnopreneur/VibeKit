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
from linux_package import copy_extension_components, package_workspace

ROOT = Path(__file__).resolve().parent
UUID = 'crt-degauss@vibeware.local'


def build(output):
    source = ROOT / 'extension' / UUID
    version = json.loads((source / 'metadata.json').read_text())['version-name']
    package_name = f"vibeware-crt-degauss_{version}_all.deb"
    with package_workspace("CrtDegauss", "all", output, (package_name,)) as (stage, output):
        extension = stage / 'usr/share/gnome-shell/extensions' / UUID
        shutil.copytree(source, extension, ignore=shutil.ignore_patterns('gschemas.compiled'))
        shutil.copy2(ROOT.parent / 'Shared/degauss.wav', extension / 'degauss.wav')
        subprocess.run([sys.executable, str(ROOT.parent / 'scripts/build-sounds.py'),
                        str(extension / 'sounds')], check=True)
        copy_extension_components(extension, panel=True)
        subprocess.run(['glib-compile-schemas', '--strict', str(extension / 'schemas')], check=True)
        schemas = stage / 'usr/share/glib-2.0/schemas'
        schemas.mkdir(parents=True)
        for schema in (source / 'schemas').glob('*.xml'):
            shutil.copy2(schema, schemas)
        applications = stage / 'usr/share/applications'
        applications.mkdir(parents=True)
        shutil.copy2(ROOT / 'packaging/local.vibeware.CrtDegauss.desktop', applications)
        shutil.copytree(ROOT.parent / 'Shared/icons/hicolor', stage / 'usr/share/icons/hicolor', dirs_exist_ok=True)
        shutil.copytree(ROOT.parent / 'Shared/icons/hicolor', extension / 'icons/hicolor', dirs_exist_ok=True)
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
        package = output / package_name
        subprocess.run(['dpkg-deb', '--root-owner-group', '--build', str(stage), str(package)], check=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description='Build the CRT Degauss Debian package.')
    parser.add_argument('--output-dir', type=Path)
    build(parser.parse_args().output_dir)
