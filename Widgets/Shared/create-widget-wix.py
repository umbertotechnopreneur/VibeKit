"""Build WiX source for a per-user widget installer from a fresh Windows publish."""

from pathlib import Path
import argparse
import hashlib
import sys
import uuid
import xml.etree.ElementTree as ET

WIX = "http://wixtoolset.org/schemas/v4/wxs"

def identity(prefix, value):
    return prefix + hashlib.sha256(value.encode()).hexdigest()[:24]


def wix_source(payload, output, *, name, version, namespace, executable, registry, app_id, minimum_build, requirement, description):
    ET.register_namespace("", WIX)
    def child(parent, tag, **attrs):
        return ET.SubElement(parent, "{"+WIX+"}"+tag, attrs)
    root = ET.Element("{"+WIX+"}Wix")
    package = child(root, "Package", Name=name, Manufacturer="Umberto Giacobbi",
                    Version=version, Language="1033", UpgradeCode=str(namespace), Scope="perUser",
                    InstallerVersion="500", Compressed="yes")
    child(package, "MajorUpgrade", DowngradeErrorMessage=f"A newer version of {name} is installed.")
    child(package, "MediaTemplate", EmbedCab="yes", CompressionLevel="medium")
    # Windows Installer's legacy WindowsBuild value can report 9600 on modern Windows.
    # AppSearch reads the actual build from the native registry view before LaunchConditions.
    os_build = child(package, "Property", Id="WIDGET_OS_BUILD", Secure="yes")
    child(os_build, "RegistrySearch", Id="ReadActualWindowsBuild", Root="HKLM",
          Key="SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion", Name="CurrentBuildNumber",
          Type="raw", Bitness="always64")
    child(package, "Launch", Condition=f"Installed OR (VersionNT64 AND WIDGET_OS_BUILD >= {minimum_build})",
          Message=requirement)
    child(package, "Property", Id="ARPCOMMENTS", Value=description)
    child(package, "Property", Id="ARPNOMODIFY", Value="1")
    child(package, "Icon", Id="AppIcon", SourceFile=str(payload / executable))
    child(package, "Property", Id="ARPPRODUCTICON", Value="AppIcon")
    local = child(package, "StandardDirectory", Id="LocalAppDataFolder")
    programs = child(local, "Directory", Id="UserPrograms", Name="Programs")
    install = child(programs, "Directory", Id="INSTALLFOLDER", Name=name)
    dirs = {".": install}
    files = sorted(p for p in payload.rglob("*") if p.is_file() and p.suffix.lower() != ".pdb")
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
                     Guid=str(uuid.uuid5(namespace, relative)), Bitness="always64")
        file_id = identity("F", relative)
        child(comp, "File", Id=file_id, Source=str(file), Name=file.name, KeyPath="no")
        child(comp, "RegistryValue", Root="HKCU", Key=registry + r"\Components",
              Name=comp_id, Type="integer", Value="1", KeyPath="yes")
        component_ids.append(comp_id)
        if relative == executable:
            exe_file = file_id
    if not exe_file:
        raise ValueError("Current Windows executable is missing")
    child(package, "CustomAction", Id="RemoveUserStartup", FileRef=exe_file,
          ExeCommand="--remove-windows-startup", Execute="deferred", Impersonate="yes", Return="ignore")
    sequence = child(package, "InstallExecuteSequence")
    child(sequence, "Custom", Action="RemoveUserStartup", Before="RemoveFiles",
          Condition='REMOVE="ALL" AND NOT UPGRADINGPRODUCTCODE')
    for key, directory in dirs.items():
        comp_id = identity("R", key)
        comp = child(directory, "Component", Id=comp_id, Guid=str(uuid.uuid5(namespace, "directory:"+key)), Bitness="always64")
        child(comp, "RemoveFolder", Id=identity("Remove", key), On="uninstall")
        child(comp, "RegistryValue", Root="HKCU", Key=registry + r"\Directories",
              Name=comp_id, Type="integer", Value="1", KeyPath="yes")
        component_ids.append(comp_id)
    menu = child(package, "StandardDirectory", Id="ProgramMenuFolder")
    comp = child(menu, "Component", Id="MenuShortcut", Guid=str(uuid.uuid5(namespace, "menu-shortcut")), Bitness="always64")
    shortcut = child(comp, "Shortcut", Id="StartMenuShortcut", Name=name, Target="[#"+exe_file+"]",
          WorkingDirectory="INSTALLFOLDER", Icon="AppIcon", Advertise="no")
    child(shortcut,"ShortcutProperty",Key="System.AppUserModel.ID",Value=app_id)
    child(comp, "RegistryValue", Root="HKCU", Key=registry,
          Name="StartMenuShortcut", Type="integer", Value="1", KeyPath="yes")
    component_ids.append("MenuShortcut")
    feature = child(package, "Feature", Id="Application", Title=name, Level="1")
    for name in component_ids:
        child(feature, "ComponentRef", Id=name)
    ET.indent(root)
    ET.ElementTree(root).write(output, encoding="utf-8", xml_declaration=True)
    return len(files)


WIDGETS = {
    "water-ripple": ("Water Ripple", "WaterRipple.exe", "WaterRipple", "4c38f791-02e4-4c90-910c-947475cbde47",
                     "Desktop water ripple effect.", "WaterRipple", "Assets/WaterRipple.ico"),
    "retro-terminal": ("Retro Terminal", "RetroStudio.exe", "RetroTerminal", "83fb29fe-a2f5-46b7-ba1b-a74ed0f45606",
                       "Desktop CRT effect and control panel.", "RetroTerminal", "Windows/Studio/RetroTerminal.ico"),
    "vignette": ("Vignette", "Vignette.exe", "Vignette", "88c39ecd-1581-4af1-9d14-6ee462e55f71",
                 "Elliptical desktop edge lighting and shading.", "Vignette", "Windows/Vignette.ico"),
}


if __name__ == "__main__":
    repository = Path(__file__).resolve().parents[2]
    sys.path.insert(0, str(repository / "scripts" / "packaging"))
    from vibeware_msi import add_installer_ui

    parser = argparse.ArgumentParser()
    parser.add_argument("widget", choices=WIDGETS)
    parser.add_argument("version")
    parser.add_argument("payload", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    name, exe, identifier, namespace, description, folder, logo = WIDGETS[args.widget]
    if args.output.exists():
        parser.error("WiX output already exists")
    count = wix_source(args.payload.resolve(), args.output.resolve(), name=name, version=args.version,
                       namespace=uuid.UUID(namespace), executable=exe,
                       registry=fr"Software\VibeWare\{identifier}\Installer",
                       app_id=f"biz.umbertogiacobbi.{identifier}", minimum_build=22000,
                       requirement=f"{name} requires 64-bit Windows 11 or newer.", description=description)
    root = ET.parse(args.output).getroot()
    # Vignette ships its own .NET Desktop runtime and needs no download dialog.
    if args.widget != "vignette":
        add_installer_ui(root, payload=args.payload, executable=exe, output=args.output,
                         home_url=f"https://github.com/umbertotechnopreneur/VibeKit/tree/main/Widgets/{folder}",
                         logo=repository / "Widgets" / folder / logo)
    ET.indent(root)
    ET.ElementTree(root).write(args.output, encoding="utf-8", xml_declaration=True)
    print(f"Files in installer: {count}")
