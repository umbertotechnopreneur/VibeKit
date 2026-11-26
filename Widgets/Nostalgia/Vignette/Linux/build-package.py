#!/usr/bin/env python3
"""Build the independent Vignette extension as a Debian package."""
import argparse
import json
import shutil
import subprocess
import sys
from pathlib import Path

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "build"))
from linux_package import copy_extension_components, package_workspace

ROOT = Path(__file__).resolve().parent
UUID = 'vignette@vibeware.local'


def build(output):
    source = ROOT / 'extension' / UUID
    version = json.loads((source / 'metadata.json').read_text())['version-name']
    package_name = f"vibeware-vignette_{version}_all.deb"
    with package_workspace("Vignette", "all", output, (package_name,)) as (stage, output):
        extension = stage / 'usr/share/gnome-shell/extensions' / UUID
        shutil.copytree(source, extension, ignore=shutil.ignore_patterns('gschemas.compiled'))
        copy_extension_components(extension, panel=True, capture=True, monitors=True)
        shutil.copy2(ROOT.parent / 'Shared/pipelines.json', extension)
        subprocess.run(['glib-compile-schemas', '--strict', extension / 'schemas'], check=True)
        schemas = stage / 'usr/share/glib-2.0/schemas'
        schemas.mkdir(parents=True)
        shutil.copy2(next((source / 'schemas').glob('*.xml')), schemas)
        desktop = stage / 'usr/share/applications'
        desktop.mkdir(parents=True)
        shutil.copy2(ROOT / 'packaging/local.vibeware.Vignette.desktop', desktop)
        shutil.copytree(source / 'icons/hicolor', stage / 'usr/share/icons/hicolor')
        docs = stage / 'usr/share/doc/vibeware-vignette'
        docs.mkdir(parents=True)
        shutil.copy2(ROOT.parent / 'LICENSE', docs / 'copyright')
        control = stage / 'DEBIAN'
        control.mkdir()
        (control / 'control').write_text(f'''Package: vibeware-vignette
Version: {version}
Architecture: all
Section: gnome
Priority: optional
Maintainer: Umberto Giacobbi
Depends: gnome-shell (>= 50), gnome-shell (<< 51), gjs, libadwaita-1-0 (>= 1.5), libgtk-4-1, libglib2.0-bin
Breaks: vibeware-retro-terminal (<< 0.4.7)
Description: Elliptical edge lighting and shading for GNOME
 Vignette provides per-display selection, 15 built-in looks and personal
 pipelines. It applies a static compositor overlay without screen capture.
''')
        for name in ('postinst', 'postrm'):
            (control / name).write_text('#!/bin/sh\nset -e\nglib-compile-schemas /usr/share/glib-2.0/schemas\n')
        for path in stage.rglob('*'):
            path.chmod(0o755 if path.is_dir() or path.name in ('postinst', 'postrm') else 0o644)
        stage.chmod(0o755)
        package = output / package_name
        subprocess.run(['dpkg-deb', '--root-owner-group', '--build', stage, package], check=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output-dir', type=Path)
    build(parser.parse_args().output_dir)
