"""Attach the common VibeWare UI and .NET prerequisite to an authored WiX MSI."""

import json
from pathlib import Path
import re
import struct
import xml.etree.ElementTree as ET


WIX = "{http://wixtoolset.org/schemas/v4/wxs}"
NETFX = "http://wixtoolset.org/schemas/v4/wxs/netfx"
CONTACT = "https://umbertogiacobbi.biz/contacts/"
RUNTIME_FILES = ("coreclr.dll", "hostfxr.dll", "hostpolicy.dll", "System.Private.CoreLib.dll")
ARCHITECTURES = {"x64": "X64", "arm64": "A64"}


def write_setup_icon(logo, output):
    """Embed the original 64-pixel ICO frame without resizing or re-encoding it."""
    source = logo.read_bytes()
    header = struct.Struct("<HHH")
    directory = struct.Struct("<BBBBHHII")
    reserved, icon_type, count = header.unpack_from(source)
    directory_end = header.size + count * directory.size
    if (reserved, icon_type) != (0, 1) or directory_end > len(source):
        raise ValueError(f"Invalid ICO directory: {logo}")
    for entry in directory.iter_unpack(source[header.size:directory_end]):
        length, offset = entry[-2:]
        if entry[:2] == (64, 64) and length and directory_end <= offset <= len(source) - length:
            with output.open("xb") as icon:
                icon.write(header.pack(0, 1, 1))
                icon.write(directory.pack(*entry[:-1], header.size + directory.size))
                icon.write(source[offset:offset + length])
            return
    raise ValueError(f"No valid 64-pixel frame in {logo}")


def read_runtime(payload, executable, architecture):
    """Read the minimum compatible shared .NET 10 runtime from the publish output."""
    app = payload / executable
    options = json.loads(app.with_suffix(".runtimeconfig.json").read_text(encoding="utf-8"))["runtimeOptions"]
    framework = options.get("framework", {})
    version = framework.get("version", "")
    if (options.get("tfm") != "net10.0" or framework.get("name") != "Microsoft.NETCore.App"
            or not re.fullmatch(r"10\.0\.[0-9]+", version) or options.get("includedFrameworks")
            or options.get("rollForward", "Minor") not in ("Minor", "LatestPatch")
            or options.get("applyPatches", True) is not True):
        raise ValueError(f"{app.name} MSI requires a framework-dependent Microsoft .NET Runtime 10.0 publish.")

    for file in RUNTIME_FILES:
        if (app.parent / file).exists():
            raise ValueError(f"{app.name} MSI cannot include the shared runtime file {file}.")
    dependencies = json.loads(app.with_suffix(".deps.json").read_text(encoding="utf-8"))
    if not dependencies["runtimeTarget"]["name"].endswith(f"/win-{architecture}"):
        raise ValueError(f"{app.name} MSI requires a Windows {architecture} publish.")
    return version


def add_installer_ui(root, *, payload, executable, output, home_url, logo, architecture="x64"):
    """Add common native dialogs, metadata and the WiX 5 NetFx check to one package."""
    package = root.find(WIX + "Package")
    version = read_runtime(Path(payload), executable, architecture)
    scope = "Per-machine installation" if package.get("Scope") == "perMachine" else "Per-user installation"
    display_version = "10.0" if version == "10.0.0" else version
    properties = {
        "VibeWareInstallScope": scope,
        "VibeWareRuntimeDescription": f"Microsoft .NET Runtime {display_version} ({architecture})",
        "ARPURLINFOABOUT": home_url,
        "ARPHELPLINK": CONTACT,
    }
    for name, value in properties.items():
        prop = package.find(f"{WIX}Property[@Id='{name}']")
        if prop is None:
            prop = ET.SubElement(package, WIX + "Property", Id=name)
        prop.set("Value", value)

    ET.register_namespace("", WIX[1:-1])
    ET.register_namespace("netfx", NETFX)
    ET.SubElement(package, f"{{{NETFX}}}DotNetCompatibilityCheck",
                  Property="VibeWareDotNetResult", Platform=architecture, RuntimeType="core",
                  RollForward="latestPatch", Version=version)
    action = f"override Wix4NetFxDotNetCompatibilityCheck_{ARCHITECTURES[architecture]}"
    for name, condition in (("InstallUISequence", "UILevel = 5 AND NOT Installed"),
                            ("InstallExecuteSequence", "NOT Installed")):
        sequence = package.find(WIX + name)
        if sequence is None:
            sequence = ET.SubElement(package, WIX + name)
        ET.SubElement(sequence, WIX + "Custom", Action=action, Before="LaunchConditions", Condition=condition)

    icon = Path(output).resolve().with_suffix(".setup.ico")
    write_setup_icon(Path(logo), icon)
    ET.SubElement(package, WIX + "Binary", Id="VibeWareSetupIcon", SourceFile=str(icon))
    ET.SubElement(package, WIX + "UIRef", Id="VibeWareInstallerUI")
    root.extend(ET.parse(Path(__file__).with_name("VibeWareInstallerUI.wxs")).getroot())
