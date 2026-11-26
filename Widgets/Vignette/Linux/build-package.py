#!/usr/bin/env python3
"""Build the independent Vignette extension as a Debian package."""
import argparse
import json
import shutil
import subprocess
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent
UUID = 'vignette@vibeware.local'


def build(output):
    output.mkdir(parents=True, exist_ok=True)
    source = ROOT / 'extension' / UUID
    widgets = ROOT.parents[1]
    crt = widgets / 'RetroTerminal/Linux/extension/retro-terminal@vibeware.local'
    version = json.loads((source / 'metadata.json').read_text())['version-name']
    with tempfile.TemporaryDirectory(prefix='vignette-deb-') as tmp:
        stage = Path(tmp)
        extension = stage / 'usr/share/gnome-shell/extensions' / UUID
        shutil.copytree(source, extension, ignore=shutil.ignore_patterns('gschemas.compiled'))
        shutil.copytree(widgets / 'Shared/Linux', extension / 'shared', ignore=shutil.ignore_patterns('tests'))
        for name in ('preferences-layout.js', 'VibeWare.png', 'VibeWare-LICENSE.txt'):
            shutil.copy2(crt / name, extension)
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
        package = output / f'vibeware-vignette_{version}_all.deb'
        subprocess.run(['dpkg-deb', '--root-owner-group', '--build', stage, package], check=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output-dir', type=Path, required=True)
    build(parser.parse_args().output_dir.resolve())
