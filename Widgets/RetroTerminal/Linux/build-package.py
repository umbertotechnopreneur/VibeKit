#!/usr/bin/env python3
"""Package Retro Terminal for GNOME 50 without installing or changing the session."""
import argparse
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import zipfile

ROOT = Path(__file__).resolve().parent
UUID = 'retro-terminal@vibeware.local'

def build(output):
    output.mkdir(parents=True, exist_ok=True)
    source = ROOT / 'extension' / UUID
    version = json.loads((source / 'metadata.json').read_text())['version-name']
    with tempfile.TemporaryDirectory(prefix='retro-terminal-') as tmp:
        stage = Path(tmp) / 'package'
        extension = stage / 'usr/share/gnome-shell/extensions' / UUID
        shutil.copytree(source, extension, ignore=shutil.ignore_patterns('gschemas.compiled', '__pycache__'))
        subprocess.run(['glib-compile-schemas', '--strict', str(extension / 'schemas')], check=True)
        schemas = stage / 'usr/share/glib-2.0/schemas'
        schemas.mkdir(parents=True)
        for path in (source / 'schemas').glob('*.xml'):
            shutil.copy2(path, schemas)
        applications = stage / 'usr/share/applications'
        applications.mkdir(parents=True)
        shutil.copy2(ROOT / 'packaging/local.vibeware.RetroTerminal.desktop', applications)
        docs = stage / 'usr/share/doc/vibeware-retro-terminal'
        docs.mkdir(parents=True)
        shutil.copy2(ROOT.parent / 'README.md', docs)
        shutil.copy2(ROOT.parent / 'LICENSE', docs / 'copyright')
        control = stage / 'DEBIAN'
        control.mkdir()
        (control / 'control').write_text(f'''Package: vibeware-retro-terminal
Version: {version}
Architecture: all
Section: gnome
Priority: optional
Maintainer: Umberto Giacobbi
Depends: gnome-shell (>= 50), gnome-shell (<< 51), gjs, libadwaita-1-0 (>= 1.5), libgtk-4-1, libglib2.0-bin, desktop-file-utils
Description: Retro phosphor CRT effects for the GNOME desktop
 Green, amber and color CRT styles with native controls and emergency stop.
''')
        for name in ('postinst', 'postrm'):
            (control / name).write_text('#!/bin/sh\nset -e\nglib-compile-schemas /usr/share/glib-2.0/schemas\n')
        for path in stage.rglob('*'):
            path.chmod(0o755 if path.is_dir() or path.name in ('postinst', 'postrm') else 0o644)
        stage.chmod(0o755)
        archive = output / f'{UUID}.shell-extension.zip'
        with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED) as zipped:
            for path in sorted(extension.rglob('*')):
                if path.is_file(): zipped.write(path, path.relative_to(extension))
        package = output / f'vibeware-retro-terminal_{version}_all.deb'
        subprocess.run(['dpkg-deb', '--root-owner-group', '--build', str(stage), str(package)], check=True)
        print(archive)
        print(package)

if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output-dir', type=Path, required=True)
    build(parser.parse_args().output_dir.resolve())
