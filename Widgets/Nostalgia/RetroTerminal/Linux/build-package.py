#!/usr/bin/env python3
"""Package Retro Terminal for GNOME 50 without installing or changing the session."""
import argparse
import json
from pathlib import Path
import shutil
import subprocess
import sys
import zipfile

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "build"))
from linux_package import copy_extension_components, package_workspace

ROOT = Path(__file__).resolve().parent
UUID = 'retro-terminal@vibeware.local'

def build(output):
    source = ROOT / 'extension' / UUID
    version = json.loads((source / 'metadata.json').read_text())['version-name']
    package_name = f"vibeware-retro-terminal_{version}_all.deb"
    archive_name = f"{UUID}.shell-extension.zip"
    with package_workspace("RetroTerminal", "all", output, (package_name, archive_name)) as (stage, output):
        extension = stage / 'usr/share/gnome-shell/extensions' / UUID
        shutil.copytree(source, extension, ignore=shutil.ignore_patterns('gschemas.compiled', '__pycache__'))
        copy_extension_components(extension, panel=True, capture=True, monitors=True)
        subprocess.run(['glib-compile-schemas', '--strict', str(extension / 'schemas')], check=True)
        schemas = stage / 'usr/share/glib-2.0/schemas'
        schemas.mkdir(parents=True)
        for path in (source / 'schemas').glob('*.xml'):
            shutil.copy2(path, schemas)
        applications = stage / 'usr/share/applications'
        applications.mkdir(parents=True)
        shutil.copy2(ROOT / 'packaging/local.vibeware.RetroTerminal.desktop', applications)
        shutil.copytree(source / 'icons/hicolor', stage / 'usr/share/icons/hicolor')
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
        archive = output / archive_name
        with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED) as zipped:
            for path in sorted(extension.rglob('*')):
                if path.is_file(): zipped.write(path, path.relative_to(extension))
        package = output / package_name
        subprocess.run(['dpkg-deb', '--root-owner-group', '--build', str(stage), str(package)], check=True)

if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output-dir', type=Path)
    build(parser.parse_args().output_dir)
