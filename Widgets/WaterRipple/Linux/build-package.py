#!/usr/bin/python3
"""Build the GNOME 50 extension archive and Debian package."""

import argparse
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import zipfile

EXTENSION_ID = 'ripple@vibeware.local'

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--output-dir', required=True, type=Path, help='Directory for the package and archive')
output = parser.parse_args().output_dir.resolve()
output.mkdir(parents=True, exist_ok=True)
root = Path(__file__).resolve().parent
source = root / 'extension' / EXTENSION_ID
version = json.loads((source / 'metadata.json').read_text(encoding='utf-8'))['version-name']

with tempfile.TemporaryDirectory(prefix='water-ripple-') as staging_directory:
    stage = Path(staging_directory) / 'package'
    target = stage / 'usr/share/gnome-shell/extensions' / EXTENSION_ID
    shutil.copytree(source, target, ignore=shutil.ignore_patterns('gschemas.compiled', '*.pyc', '__pycache__'))
    shutil.copytree(root.parent / 'Shared', target / 'shared')
    subprocess.run(['glib-compile-schemas', '--strict', str(target / 'schemas')], check=True)

    common = stage / 'usr/share/glib-2.0/schemas'
    common.mkdir(parents=True)
    for schema in (source / 'schemas').glob('*.gschema.xml'):
        shutil.copy2(schema, common / schema.name)

    apps = stage / 'usr/share/applications'
    apps.mkdir(parents=True)
    shutil.copy2(root / 'packaging/local.vibeware.Ripple.desktop', apps)
    autostart = stage / 'etc/xdg/autostart'
    autostart.mkdir(parents=True)
    shutil.copy2(root / 'packaging/ripple-autostart.desktop', autostart / 'local.vibeware.Ripple.desktop')
    icons = stage / 'usr/share/icons/hicolor/scalable/apps'
    icons.mkdir(parents=True)
    shutil.copy2(root / 'packaging/ripple.svg', icons / 'local.vibeware.Ripple.svg')

    docs = stage / 'usr/share/doc/vibeware-ripple'
    docs.mkdir(parents=True)
    shutil.copy2(root / 'README.md', docs)
    shutil.copy2(root / 'LICENSE', docs / 'copyright')

    metadata = stage / 'DEBIAN'
    metadata.mkdir()
    (metadata / 'control').write_text(f'''Package: vibeware-ripple
Version: {version}
Section: gnome
Priority: optional
Architecture: all
Maintainer: Umberto Giacobbi
Depends: gnome-shell (>= 50), gnome-shell (<< 51), gjs, libadwaita-1-0 (>= 1.5), libgtk-4-1, libglib2.0-bin, gir1.2-dbusmenu-glib-0.4
Description: Bounded click refraction for GNOME Shell 50
 Real image refraction on the background or clicked window, with a compact
 controller, a heartbeat timeout and bounded rendering surfaces.
''')
    for name in ('postinst', 'postrm'):
        script = metadata / name
        script.write_text('''#!/bin/sh
set -e
if command -v glib-compile-schemas >/dev/null 2>&1; then
    glib-compile-schemas /usr/share/glib-2.0/schemas
fi
if command -v gtk-update-icon-cache >/dev/null 2>&1; then
    gtk-update-icon-cache -q -t -f /usr/share/icons/hicolor || true
fi
''')
        script.chmod(0o755)

    # Source checkout permissions can be synthetic on shared NTFS mounts.
    for path in stage.rglob('*'):
        executable = path in (metadata / 'postinst', metadata / 'postrm')
        path.chmod(0o755 if path.is_dir() or executable else 0o644)
    stage.chmod(0o755)

    package = output / f'vibeware-ripple_{version}_all.deb'
    subprocess.run(['dpkg-deb', '--root-owner-group', '--build', str(stage), str(package)], check=True)
    archive = output / f'{EXTENSION_ID}.shell-extension.zip'
    with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED) as zipped:
        for path in sorted(target.rglob('*')):
            if path.is_file():
                zipped.write(path, path.relative_to(target))

print(package)
print(archive)
