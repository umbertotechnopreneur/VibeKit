"""Generate per-user WiX source for the HelpMe and VoiceDucker MSI packages."""
import argparse
import importlib.util
from pathlib import Path
import struct
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
parser.add_argument('app', choices=[*APPS, 'portable'])
parser.add_argument('version')
parser.add_argument('payload', type=Path)
parser.add_argument('output', type=Path)
parser.add_argument('--architecture', choices=('x64', 'arm64'), default='x64')
parser.add_argument('--minimum-build', type=int, default=22000)
parser.add_argument('--name')
parser.add_argument('--executable')
parser.add_argument('--upgrade-code', type=uuid.UUID)
args = parser.parse_args()
if args.app == 'portable':
    if not (args.name and args.executable and args.upgrade_code):
        parser.error('A portable installer needs a display name, executable and upgrade code')
    name, executable, namespace = args.name, args.executable, args.upgrade_code
else:
    name, executable, guid, home_url, logo = APPS[args.app]
    namespace = uuid.UUID(guid)
if args.output.exists():
    parser.error('Choose a new output file')
payload = args.payload.resolve()
entry = (payload / executable).resolve()
if not entry.is_relative_to(payload) or not entry.is_file():
    parser.error('The executable must be inside the publish directory')
with entry.open('rb') as binary:
    if binary.read(2) != b'MZ':
        parser.error('Missing Windows executable header')
    binary.seek(0x3c)
    binary.seek(struct.unpack('<I', binary.read(4))[0])
    if binary.read(4) != b'PE\0\0' or struct.unpack('<H', binary.read(2))[0] != {'x64': 0x8664, 'arm64': 0xAA64}[args.architecture]:
        parser.error(f'Expected a native {args.architecture} executable')
generator.wix_source(args.payload.resolve(), args.output.resolve(), name=name, version=args.version,
    namespace=namespace, executable=executable, registry=f'Software\\VibeWare\\{name}\\Installer',
    app_id=f'biz.umbertogiacobbi.{name}', minimum_build=args.minimum_build,
    requirement=f'{name} requires 64-bit Windows build {args.minimum_build} or newer.', description=name)
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
if args.app != 'portable':
    add_installer_ui(root, payload=payload, executable=executable, output=args.output,
                     home_url=home_url, logo=ROOT / logo, architecture=args.architecture)
ET.indent(root)
tree.write(args.output, encoding='utf-8', xml_declaration=True)
