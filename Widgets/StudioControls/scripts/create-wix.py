"""Create WiX source from a verified Windows source publish."""
from __future__ import annotations
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import uuid
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
VERSION = ET.parse(ROOT / "Windows/StudioControls.Windows.csproj").findtext(".//Version")
WIX = "http://wixtoolset.org/schemas/v4/wxs"
NAMESPACE = uuid.UUID("a29e1283-f748-4cb8-94e4-a512caf9edc8")


def identity(prefix, value):
    return prefix + hashlib.sha256(value.encode()).hexdigest()[:24]


def wix_source(payload, output):
    def reject_reparse(path):
        info = path.lstat()
        if stat.S_ISLNK(info.st_mode) or getattr(info, 'st_file_attributes', 0) & 0x400:
            raise ValueError('Publish output contains a symbolic link or reparse point')
    reject_reparse(payload)
    for root, directories, names in os.walk(payload, followlinks=False):
        for name in directories + names:
            reject_reparse(Path(root) / name)
    manifest = json.loads((payload / '.publish-manifest.json').read_text(encoding='utf-8-sig'))
    if manifest.get('format') != 1 or manifest.get('application') != 'StudioControls' or manifest.get('version') != VERSION or manifest.get('runtime') != 'win-x64':
        raise ValueError('Publish manifest does not match this release')
    expected = {}
    for entry in manifest['files']:
        relative = entry['path']
        path = Path(relative)
        if path.is_absolute() or '\\' in relative or ':' in relative or any(part in ('', '.', '..') for part in relative.split('/')) or relative.lower() in expected:
            raise ValueError('Invalid manifest path')
        if re.search(r'(^|/)(panel\.json|project\.json|local\.settings\.json|credentials[^/]*|\.env[^/]*|logs|backups)(/|$)', relative, re.I) or re.search(r'\.(log|pdb)$|\.runtimeconfig\.dev\.json$', relative, re.I):
            raise ValueError('User data or development output cannot be packaged: ' + relative)
        expected[relative.lower()] = entry
        file = payload / path
        if not file.is_file() or file.stat().st_size != entry['bytes'] or hashlib.sha256(file.read_bytes()).hexdigest().lower() != entry['sha256'].lower():
            raise ValueError('Publish file changed: ' + relative)
    actual = {p.relative_to(payload).as_posix().lower() for p in payload.rglob('*') if p.is_file()
              and p.suffix.lower() != '.pdb' and p.name not in ('.publish-manifest.json', 'windows-verification.txt', 'ui-smoke.txt')}
    if actual != set(expected):
        raise ValueError('Unexpected or missing publish files')
    ET.register_namespace("", WIX)
    def child(parent, tag, **attrs):
        return ET.SubElement(parent, "{"+WIX+"}"+tag, attrs)
    root = ET.Element("{"+WIX+"}Wix")
    package = child(root, "Package", Name="Studio Controls", Manufacturer="Umberto Giacobbi",
                    Version=VERSION, Language="1033", UpgradeCode=str(NAMESPACE), Scope="perUser",
                    InstallerVersion="500", Compressed="yes")
    child(package, "MajorUpgrade", DowngradeErrorMessage="A newer version of Studio Controls is installed.")
    child(package, "MediaTemplate", EmbedCab="yes", CompressionLevel="medium")
    # Windows Installer's legacy WindowsBuild value can report 9600 on modern Windows.
    # AppSearch reads the actual build from the native registry view before LaunchConditions.
    os_build = child(package, "Property", Id="STUDIO_OS_BUILD", Secure="yes")
    child(os_build, "RegistrySearch", Id="ReadActualWindowsBuild", Root="HKLM",
          Key="SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion", Name="CurrentBuildNumber",
          Type="raw", Bitness="always64")
    child(package, "Launch", Condition="Installed OR (VersionNT64 AND STUDIO_OS_BUILD >= 22000)",
          Message="Studio Controls requires 64-bit Windows 11 or newer.")
    child(package, "Property", Id="ARPCOMMENTS", Value="Native OBS Studio control desk.")
    child(package, "Property", Id="ARPNOMODIFY", Value="1")
    child(package, "Icon", Id="AppIcon", SourceFile=str(payload / "StudioControls.Windows.exe"))
    child(package, "Property", Id="ARPPRODUCTICON", Value="AppIcon")
    local = child(package, "StandardDirectory", Id="LocalAppDataFolder")
    programs = child(local, "Directory", Id="UserPrograms", Name="Programs")
    install = child(programs, "Directory", Id="INSTALLFOLDER", Name="Studio Controls")
    dirs = {".": install}
    files = sorted(p for p in payload.rglob("*") if p.is_file() and p.suffix.lower() != ".pdb"
                   and p.name not in (".publish-manifest.json", "windows-verification.txt", "ui-smoke.txt"))
    component_ids = []
    exe_file = None
    for file in files:
        relative = file.relative_to(payload).as_posix()
        parent = file.relative_to(payload).parent
        cumulative = Path()
        for part in parent.parts:
            ancestor = cumulative.as_posix()
            cumulative /= part
            key = cumulative.as_posix()
            if key not in dirs:
                dirs[key] = child(dirs[ancestor], "Directory", Id=identity("D", key), Name=part)
        comp_id = identity("C", relative)
        comp = child(dirs[parent.as_posix()], "Component", Id=comp_id,
                     Guid=str(uuid.uuid5(NAMESPACE, relative)), Bitness="always64")
        file_id = identity("F", relative)
        child(comp, "File", Id=file_id, Source=str(file), Name=file.name, KeyPath="no")
        child(comp, "RegistryValue", Root="HKCU", Key="Software\\UmbertoGiacobbi\\StudioControls\\Components",
              Name=comp_id, Type="integer", Value="1", KeyPath="yes")
        component_ids.append(comp_id)
        if relative == "StudioControls.Windows.exe":
            exe_file = file_id
    if not exe_file:
        raise ValueError("Current Windows executable is missing")
    for key, directory in dirs.items():
        comp_id = identity("R", key)
        comp = child(directory, "Component", Id=comp_id, Guid=str(uuid.uuid5(NAMESPACE, "directory:"+key)), Bitness="always64")
        child(comp, "RemoveFolder", Id=identity("Remove", key), On="uninstall")
        child(comp, "RegistryValue", Root="HKCU", Key="Software\\UmbertoGiacobbi\\StudioControls\\Directories",
              Name=comp_id, Type="integer", Value="1", KeyPath="yes")
        component_ids.append(comp_id)
    menu = child(package, "StandardDirectory", Id="ProgramMenuFolder")
    comp = child(menu, "Component", Id="MenuShortcut", Guid=str(uuid.uuid5(NAMESPACE, "menu-shortcut")), Bitness="always64")
    shortcut = child(comp, "Shortcut", Id="StartMenuShortcut", Name="Studio Controls", Target="[#"+exe_file+"]",
          WorkingDirectory="INSTALLFOLDER", Icon="AppIcon", Advertise="no")
    child(shortcut,"ShortcutProperty",Key="System.AppUserModel.ID",Value="biz.umbertogiacobbi.StudioControls")
    child(comp, "RegistryValue", Root="HKCU", Key="Software\\UmbertoGiacobbi\\StudioControls",
          Name="StartMenuShortcut", Type="integer", Value="1", KeyPath="yes")
    component_ids.append("MenuShortcut")
    feature = child(package, "Feature", Id="Application", Title="Studio Controls", Level="1")
    for name in component_ids:
        child(feature, "ComponentRef", Id=name)
    ET.indent(root)
    ET.ElementTree(root).write(output, encoding="utf-8", xml_declaration=True)
    return len(files)



if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("payload", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    if args.output.exists():
        raise SystemExit("Output already exists")
    print("Files in installer:", wix_source(args.payload.absolute(), args.output.absolute()))
