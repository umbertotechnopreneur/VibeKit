"""Build WiX source for a per-user widget installer from a fresh Windows publish."""

from pathlib import Path
import argparse
import hashlib
import json
import sys
import uuid
import xml.etree.ElementTree as ET

WIX = "http://wixtoolset.org/schemas/v4/wxs"

def identity(prefix, value):
    """Keep WiX identifiers stable across rebuilds of the same relative path."""
    return prefix + hashlib.sha256(value.encode()).hexdigest()[:24]


def child(parent, tag, **attrs):
    return ET.SubElement(parent, "{" + WIX + "}" + tag, attrs)


def new_package(name, version, namespace, minimum_build, requirement, description):
    """Create the per-user MSI header and shared installation directories."""
    ET.register_namespace("", WIX)
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
    local = child(package, "StandardDirectory", Id="LocalAppDataFolder")
    programs = child(local, "Directory", Id="UserPrograms", Name="Programs")
    menu = child(package, "StandardDirectory", Id="ProgramMenuFolder")
    return root, package, programs, menu


def add_product(package, programs, menu, payload, *, name, namespace, executable, registry,
                app_id, icon, remove_user_startup, prefix=""):
    """Add one independent payload and shortcut, prefixing symbols for a shared MSI."""
    icon_id = prefix + "AppIcon"
    folder = prefix + "INSTALLFOLDER"
    child(package, "Icon", Id=icon_id, SourceFile=str(icon if icon is not None else payload / executable))
    install = child(programs, "Directory", Id=folder, Name=name)
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
                dirs[key] = child(dirs[ancestor], "Directory", Id=prefix + identity("D", key), Name=part)
        key = identity("C", relative)
        comp_id = prefix + key
        comp = child(dirs[parent.as_posix()], "Component", Id=comp_id,
                     Guid=str(uuid.uuid5(namespace, relative)), Bitness="always64")
        file_id = prefix + identity("F", relative)
        child(comp, "File", Id=file_id, Source=str(file), Name=file.name, KeyPath="no")
        child(comp, "RegistryValue", Root="HKCU", Key=registry + r"\Components",
              Name=key, Type="integer", Value="1", KeyPath="yes")
        component_ids.append(comp_id)
        if relative == executable:
            exe_file = file_id
    if not exe_file:
        raise ValueError("Current Windows executable is missing")
    if remove_user_startup:
        action = prefix + "RemoveUserStartup"
        child(package, "CustomAction", Id=action, FileRef=exe_file,
              ExeCommand="--remove-windows-startup", Execute="deferred", Impersonate="yes", Return="ignore")
        sequence = package.find(f"{{{WIX}}}InstallExecuteSequence")
        if sequence is None:
            sequence = child(package, "InstallExecuteSequence")
        child(sequence, "Custom", Action=action, Before="RemoveFiles",
              Condition='REMOVE="ALL" AND NOT UPGRADINGPRODUCTCODE')
    for key, directory in dirs.items():
        registry_name = identity("R", key)
        comp_id = prefix + registry_name
        comp = child(directory, "Component", Id=comp_id, Guid=str(uuid.uuid5(namespace, "directory:"+key)), Bitness="always64")
        child(comp, "RemoveFolder", Id=prefix + identity("Remove", key), On="uninstall")
        child(comp, "RegistryValue", Root="HKCU", Key=registry + r"\Directories",
              Name=registry_name, Type="integer", Value="1", KeyPath="yes")
        component_ids.append(comp_id)
    shortcut_component = prefix + "MenuShortcut"
    comp = child(menu, "Component", Id=shortcut_component, Guid=str(uuid.uuid5(namespace, "menu-shortcut")), Bitness="always64")
    shortcut = child(comp, "Shortcut", Id=prefix + "StartMenuShortcut", Name=name, Target="[#"+exe_file+"]",
          WorkingDirectory=folder, Icon=icon_id, Advertise="no")
    child(shortcut,"ShortcutProperty",Key="System.AppUserModel.ID",Value=app_id)
    child(comp, "RegistryValue", Root="HKCU", Key=registry,
          Name="StartMenuShortcut", Type="integer", Value="1", KeyPath="yes")
    component_ids.append(shortcut_component)
    feature = child(package, "Feature", Id=prefix + "Application", Title=name, Level="1", AllowAbsent="no")
    for component_id in component_ids:
        child(feature, "ComponentRef", Id=component_id)
    return len(files)


def wix_source(payload, output, *, name, version, namespace, executable, registry, app_id,
               minimum_build, requirement, description, icon=None, remove_user_startup=True):
    """Author a single application package for the shared VibeKit MSI generator."""
    root, package, programs, menu = new_package(name, version, namespace, minimum_build, requirement, description)
    count = add_product(package, programs, menu, payload, name=name, namespace=namespace,
                        executable=executable, registry=registry, app_id=app_id, icon=icon,
                        remove_user_startup=remove_user_startup)
    child(package, "Property", Id="ARPPRODUCTICON", Value="AppIcon")
    ET.indent(root)
    ET.ElementTree(root).write(output, encoding="utf-8", xml_declaration=True)
    return count


def detect_product(package, namespace, property_name):
    """Detect another package that owns the same installation paths."""
    upgrade = child(package, "Upgrade", Id=str(namespace))
    child(upgrade, "UpgradeVersion", Minimum="0.0.0", IncludeMinimum="yes",
          OnlyDetect="yes", Property=property_name)


def product_options(family, product, entry, executable):
    name = entry["name"]
    icon = family / product / entry["icon"]
    if not icon.is_file():
        raise FileNotFoundError(f"Missing icon for {name}: {icon}")
    return dict(name=name, namespace=uuid.UUID(entry["upgrade_code"]), executable=executable,
                registry=fr"Software\VibeWare\{product}\Installer",
                app_id=f"biz.umbertogiacobbi.{product}", icon=icon,
                remove_user_startup=entry["remove_user_startup"])


def main():
    repository = Path(__file__).resolve().parents[2]
    sys.path.insert(0, str(repository / "scripts" / "packaging"))
    from vibeware_msi import add_installer_ui, read_runtime

    family = repository / "Widgets/Nostalgia"
    catalog = json.loads((family / "build/windows-products.json").read_text(encoding="utf-8"))
    suite = json.loads((family / "build/suite.json").read_text(encoding="utf-8"))
    suite_code = uuid.UUID(suite["windows_upgrade_code"])
    parser = argparse.ArgumentParser()
    parser.add_argument("widget", choices=[*catalog, "Nostalgia"])
    parser.add_argument("version")
    parser.add_argument("executable")
    parser.add_argument("payload", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    if args.output.exists():
        parser.error("WiX output already exists")

    product = args.widget
    payload = args.payload.resolve()
    home = "https://github.com/umbertotechnopreneur/VibeKit/tree/main/Widgets/Nostalgia"
    requirement = "64-bit Windows 11 or newer is required."
    if product != "Nostalgia":
        entry = catalog[product]
        options = product_options(family, product, entry, args.executable)
        root, package, programs, menu = new_package(
            entry["name"], args.version, options["namespace"], 22000, requirement, entry["description"])
        count = add_product(package, programs, menu, payload, **options)
        child(package, "Property", Id="ARPPRODUCTICON", Value="AppIcon")
        detect_product(package, suite_code, "NOSTALGIA_SUITE_INSTALLED")
        child(package, "Launch", Condition="Installed OR NOT NOSTALGIA_SUITE_INSTALLED",
              Message="Uninstall Nostalgia before installing individual effects.")
        runtime, executable, icon = entry["runtime"], args.executable, options["icon"]
        home += "/" + product
    else:
        if args.version != suite["windows_version"]:
            parser.error("The suite version must match suite.json")
        executables = json.loads(Path(args.executable).read_text(encoding="utf-8"))
        root, package, programs, menu = new_package(
            "Nostalgia", args.version, suite_code, 22000, requirement, "Five VibeWare desktop effects.")
        count = 0
        detected = []
        requirements = []
        for widget, entry in catalog.items():
            options = product_options(family, widget, entry, executables[widget])
            count += add_product(package, programs, menu, payload / widget, prefix=widget + "_", **options)
            property_name = "NOSTALGIA_SINGLE_" + widget.upper()
            detect_product(package, options["namespace"], property_name)
            detected.append("NOT " + property_name)
            runtime = entry["runtime"]
            version = read_runtime(payload / widget, options["executable"], "x64", runtime)
            requirements.append((runtime, tuple(map(int, version.split("."))), widget, options))
        child(package, "Launch", Condition="Installed OR (" + " AND ".join(detected) + ")",
              Message="Uninstall the individual Nostalgia effects before installing the suite.")
        # The Windows Desktop Runtime distribution also installs its matching core runtime.
        runtime, version, runtime_widget, options = max(requirements, key=lambda item: (item[0] == "desktop", item[1]))
        if any(other_version > version for _, other_version, _, _ in requirements):
            raise ValueError("The suite runtime does not satisfy every included application.")
        payload = payload / runtime_widget
        executable = options["executable"]
        icon = family / "WaterRipple" / catalog["WaterRipple"]["icon"]
        child(package, "Property", Id="ARPPRODUCTICON", Value="WaterRipple_AppIcon")

    add_installer_ui(root, payload=payload, executable=executable, output=args.output,
                     home_url=home, logo=icon, runtime_type=runtime)
    ET.indent(root)
    ET.ElementTree(root).write(args.output, encoding="utf-8", xml_declaration=True)
    print(f"Files in installer: {count}")


if __name__ == "__main__":
    main()
