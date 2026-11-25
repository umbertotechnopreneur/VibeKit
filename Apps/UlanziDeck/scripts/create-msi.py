"""Author UlanziDeck's per-user Windows Installer package."""

import argparse
import importlib.util
from pathlib import Path
import sys
import uuid
import xml.etree.ElementTree as ET


REPOSITORY = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(REPOSITORY / 'scripts/packaging'))
from vibeware_msi import WIX, add_installer_ui


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('version')
    parser.add_argument('payload', type=Path)
    parser.add_argument('output', type=Path)
    args = parser.parse_args()

    spec = importlib.util.spec_from_file_location(
        'wix_generator', REPOSITORY / 'Widgets/Shared/create-widget-wix.py')
    generator = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(generator)

    launcher = 'ulanzi-dock-launcher.exe'
    registry = r'Software\VibeWare\UlanziDeck\Installer'
    generator.wix_source(
        args.payload.resolve(), args.output.resolve(), name='UlanziDeck',
        version=args.version, namespace=uuid.UUID('09430490-a652-40d0-b9b9-1a400a78c488'),
        executable=launcher, registry=registry, app_id='biz.umbertogiacobbi.UlanziDeck',
        minimum_build=17763, requirement='UlanziDeck requires 64-bit Windows 10 version 1809 or newer.',
        description='Controller for the Ulanzi D200H')

    root = ET.parse(args.output).getroot()
    package = root.find(WIX + 'Package')
    # The app creates writable settings beside the executables. They never enter the MSI.
    upgrade = package.find(WIX + 'MajorUpgrade')
    upgrade.set('AllowSameVersionUpgrades', 'yes')
    upgrade.set('Schedule', 'afterInstallInitialize')
    # The shared generator's startup cleanup is specific to widgets.
    package.remove(package.find(WIX + 'CustomAction'))
    package.remove(package.find(WIX + 'InstallExecuteSequence'))

    add_installer_ui(
        root, payload=args.payload, executable=launcher, output=args.output,
        home_url='https://github.com/umbertotechnopreneur/VibeKit/tree/main/Apps/UlanziDeck',
        logo=Path(__file__).resolve().parents[1] / 'assets/icons/ulanzi-dock.ico', runtime_type=None)
    ET.indent(root)
    ET.ElementTree(root).write(args.output, encoding='utf-8', xml_declaration=True)


if __name__ == '__main__':
    main()
