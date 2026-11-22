"""Generate per-user WiX source for the HelpMe and VoiceDucker MSI packages."""
import argparse
import importlib.util
from pathlib import Path
import sys
import uuid
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts" / "packaging"))
from vibeware_msi import add_installer_ui

spec = importlib.util.spec_from_file_location('widget_wix', ROOT / 'Widgets/Shared/create-widget-wix.py')
generator = importlib.util.module_from_spec(spec)
spec.loader.exec_module(generator)
APPS = {
    'helpme': (
        'HelpMe', 'hm.exe', '3d310554-f152-4ec0-a4c3-673cd42c5a21',
        'https://umbertogiacobbi.biz/promptmeup/', 'Apps/HelpMe/assets/HelpMe.ico',
    ),
    'voiceducker': (
        'VoiceDucker', 'VoiceDucker.exe', '8eb22190-cc36-4767-bd03-3fb1ed508ab3',
        'https://umbertogiacobbi.biz/voiceducker/', 'Apps/VoiceDucker/Assets/AppIcon.ico',
    ),
}


parser = argparse.ArgumentParser()
parser.add_argument('app', choices=APPS)
parser.add_argument('version')
parser.add_argument('payload', type=Path)
parser.add_argument('output', type=Path)
args = parser.parse_args()
name, executable, guid, home_url, logo = APPS[args.app]
if args.output.exists():
    parser.error('Choose a new output file')
namespace = uuid.UUID(guid)
generator.wix_source(args.payload.resolve(), args.output.resolve(), name=name, version=args.version,
    namespace=namespace, executable=executable, registry=f'Software\\VibeWare\\{name}\\Installer',
    app_id=f'biz.umbertogiacobbi.{name}', minimum_build=22000,
    requirement=f'{name} requires 64-bit Windows 11 or newer.', description=name)
tree = ET.parse(args.output)
root = tree.getroot()
ns = '{' + generator.WIX + '}'
package = root.find(ns + 'Package')
for tag in ('CustomAction', 'InstallExecuteSequence'):
    for element in package.findall(ns + tag):
        package.remove(element)
if args.app == 'helpme':
    feature = package.find(ns + 'Feature')
    install = package.find('.//' + ns + 'Directory[@Id="INSTALLFOLDER"]')
    component = ET.SubElement(install, ns + 'Component', Id='CommandPath',
        Guid=str(uuid.uuid5(namespace, 'path')), Bitness='always64')
    ET.SubElement(component, ns + 'Environment', Id='HelpMePath', Name='PATH', Action='set',
        Part='last', Value='[INSTALLFOLDER]', System='no')
    ET.SubElement(component, ns + 'RegistryValue', Root='HKCU', Key='Software\\VibeWare\\HelpMe\\Installer',
        Name='Path', Type='integer', Value='1', KeyPath='yes')
    ET.SubElement(feature, ns + 'ComponentRef', Id='CommandPath')
add_installer_ui(root, payload=args.payload, executable=executable, output=args.output,
                 home_url=home_url, logo=ROOT / logo)
ET.indent(root)
tree.write(args.output, encoding='utf-8', xml_declaration=True)
